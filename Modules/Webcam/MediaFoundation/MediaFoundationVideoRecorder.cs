using System.Runtime.InteropServices;
using System.IO;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

internal sealed class MediaFoundationVideoRecorder : IDisposable
{
    private readonly object _lock = new();
    private readonly int _frameBytes;
    private readonly IMFDXGIDeviceManager? _d3dManager;
    private IMFSinkWriter? _writer;
    private int _streamIndex;
    private long _nextSampleTime;
    private int _samplesWritten;
    private int _framesOffered;
    private int _framesSkipped;
    private bool _isPaused;
    private bool _isFinalized;

    public MediaFoundationVideoRecorder(
        string path,
        int width,
        int height,
        double framesPerSecond,
        IMFDXGIDeviceManager? d3dManager)
    {
        Path = path;
        Width = width;
        Height = height;
        FramesPerSecond = Math.Clamp(framesPerSecond, 1d, 120d);
        Stride = Width * 4;
        _frameBytes = Stride * Height;
        _d3dManager = d3dManager;
        _nextSampleTime = 0;

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
        InitializeWriter();
    }

    public string Path { get; }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public double FramesPerSecond { get; }

    public int SamplesWritten => _samplesWritten;

    public int FramesOffered => _framesOffered;

    public int FramesSkipped => _framesSkipped;

    public string? LastSkipReason { get; private set; }

    private long SampleDuration => Math.Max(1, (long)Math.Round(MediaFoundationInterop.TicksPerSecond / FramesPerSecond));

    public void Pause()
    {
        lock (_lock)
        {
            _isPaused = true;
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            _isPaused = false;
        }
    }

    public bool WriteFrame(byte[] bgraBytes)
    {
        _framesOffered++;
        if (bgraBytes.Length < _frameBytes)
        {
            _framesSkipped++;
            LastSkipReason = $"frame buffer too small ({bgraBytes.Length} < {_frameBytes})";
            return false;
        }

        lock (_lock)
        {
            if (_writer is null)
            {
                _framesSkipped++;
                LastSkipReason = "writer is not initialized";
                return false;
            }

            if (_isPaused)
            {
                _framesSkipped++;
                LastSkipReason = "recording is paused";
                return false;
            }

            if (_isFinalized)
            {
                _framesSkipped++;
                LastSkipReason = "writer is already finalized";
                return false;
            }

            IMFMediaBuffer? buffer = null;
            IMFSample? sample = null;
            try
            {
                MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateMemoryBuffer(_frameBytes, out buffer));
                MediaFoundationInterop.ThrowIfFailed(buffer.Lock(out var destination, out _, out _));
                try
                {
                    Marshal.Copy(bgraBytes, 0, destination, _frameBytes);
                }
                finally
                {
                    buffer.Unlock();
                }

                MediaFoundationInterop.ThrowIfFailed(buffer.SetCurrentLength(_frameBytes));
                MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateSample(out sample));
                MediaFoundationInterop.ThrowIfFailed(sample.AddBuffer(buffer));
                MediaFoundationInterop.ThrowIfFailed(sample.SetSampleTime(_nextSampleTime));
                MediaFoundationInterop.ThrowIfFailed(sample.SetSampleDuration(SampleDuration));
                MediaFoundationInterop.ThrowIfFailed(sample.SetUINT32(MediaFoundationGuids.MFSampleExtension_CleanPoint, 1));
                MediaFoundationInterop.ThrowIfFailed(_writer.WriteSample(_streamIndex, sample));
                _samplesWritten++;
                LastSkipReason = null;
                _nextSampleTime += SampleDuration;
                return true;
            }
            finally
            {
                MediaFoundationInterop.ReleaseComObject(sample);
                MediaFoundationInterop.ReleaseComObject(buffer);
            }
        }
    }

    public string Stop()
    {
        lock (_lock)
        {
            if (_writer is null || _isFinalized)
            {
                return Path;
            }

            try
            {
                _isFinalized = true;
                var result = _writer.Finalize_();
                if (MediaFoundationInterop.Failed(result) && _samplesWritten > 0)
                {
                    MediaFoundationInterop.ThrowIfFailed(result);
                }
            }
            finally
            {
                MediaFoundationInterop.ReleaseComObject(_writer);
                _writer = null;
            }
        }

        return Path;
    }

    public void Dispose()
    {
        Stop();
    }

    private void InitializeWriter()
    {
        MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateAttributes(out var writerAttributes, 1));
        try
        {
            MediaFoundationInterop.ThrowIfFailed(writerAttributes.SetUINT32(
                MediaFoundationGuids.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS,
                1));
            if (_d3dManager is not null)
            {
                MediaFoundationInterop.ThrowIfFailed(writerAttributes.SetUnknown(
                    MediaFoundationGuids.MF_SOURCE_READER_D3D_MANAGER,
                    _d3dManager));
            }

            MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateSinkWriterFromURL(
                Path,
                IntPtr.Zero,
                writerAttributes,
                out _writer));
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(writerAttributes);
        }

        IMFMediaType? outputType = null;
        IMFMediaType? inputType = null;
        try
        {
            MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateMediaType(out outputType));
            ConfigureVideoType(outputType, MediaFoundationGuids.MFVideoFormat_H264, EstimateBitrate(), includePixelAspect: true);
            MediaFoundationInterop.ThrowIfFailed(_writer.AddStream(outputType, out _streamIndex));

            MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateMediaType(out inputType));
            ConfigureVideoType(inputType, MediaFoundationGuids.MFVideoFormat_RGB32, bitrate: null, includePixelAspect: true);
            MediaFoundationInterop.ThrowIfFailed(_writer.SetInputMediaType(_streamIndex, inputType, null));
            MediaFoundationInterop.ThrowIfFailed(_writer.BeginWriting());
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(outputType);
            MediaFoundationInterop.ReleaseComObject(inputType);
        }
    }

    private void ConfigureVideoType(IMFMediaType mediaType, Guid subtype, int? bitrate, bool includePixelAspect)
    {
        var (fpsNumerator, fpsDenominator) = CreateFrameRateRatio(FramesPerSecond);
        MediaFoundationInterop.ThrowIfFailed(mediaType.SetGUID(
            MediaFoundationGuids.MF_MT_MAJOR_TYPE,
            MediaFoundationGuids.MFMediaType_Video));
        MediaFoundationInterop.ThrowIfFailed(mediaType.SetGUID(
            MediaFoundationGuids.MF_MT_SUBTYPE,
            subtype));
        MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT64(
            MediaFoundationGuids.MF_MT_FRAME_SIZE,
            MediaFoundationInterop.PackRatio(Width, Height)));
        MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT64(
            MediaFoundationGuids.MF_MT_FRAME_RATE,
            MediaFoundationInterop.PackRatio(fpsNumerator, fpsDenominator)));
        MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT32(
            MediaFoundationGuids.MF_MT_INTERLACE_MODE,
            MediaFoundationInterop.MFVideoInterlace_Progressive));

        if (includePixelAspect)
        {
            MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT64(
                MediaFoundationGuids.MF_MT_PIXEL_ASPECT_RATIO,
                MediaFoundationInterop.PackRatio(1, 1)));
        }

        if (bitrate is int value)
        {
            MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT32(
                MediaFoundationGuids.MF_MT_AVG_BITRATE,
                value));
        }

        if (subtype == MediaFoundationGuids.MFVideoFormat_RGB32)
        {
            MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT32(
                MediaFoundationGuids.MF_MT_DEFAULT_STRIDE,
                Stride));
            MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT32(
                MediaFoundationGuids.MF_MT_FIXED_SIZE_SAMPLES,
                1));
            MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT32(
                MediaFoundationGuids.MF_MT_ALL_SAMPLES_INDEPENDENT,
                1));
        }
    }

    private int EstimateBitrate()
    {
        var megapixels = Width * Height / 1_000_000d;
        var frameRateFactor = FramesPerSecond / 30d;
        var bitrate = (int)Math.Round(Math.Clamp(megapixels * frameRateFactor * 5_500_000d, 8_000_000d, 64_000_000d));
        return bitrate;
    }

    private static (int Numerator, int Denominator) CreateFrameRateRatio(double fps)
    {
        if (Math.Abs(fps - 29.97d) < 0.02d)
        {
            return (30000, 1001);
        }

        if (Math.Abs(fps - 59.94d) < 0.02d)
        {
            return (60000, 1001);
        }

        return ((int)Math.Round(Math.Clamp(fps, 1d, 240d)), 1);
    }
}
