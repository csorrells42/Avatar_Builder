using System.Buffers;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectX11;
using AvatarBuilder.Modules.Webcam.MediaFoundation;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed record TextureNativeRecordingResult(
    bool Success,
    string Path,
    int SamplesWritten,
    long BytesWritten,
    string DeviceMode,
    string MediaSubtype,
    int Width,
    int Height,
    double FramesPerSecond,
    string Status,
    string RecordingPipeline = "Media Foundation texture-native raw camera samples",
    bool RecordingDenoiseApplied = false,
    bool RecordingMatchesPreviewDenoise = false,
    bool RecordingColorPolishApplied = false,
    bool RecordingMatchesPreviewColor = false);

public sealed record TextureNativeRecordingOptions(
    bool ProcessedOutputEnabled,
    bool DenoiseEnabled,
    double DenoiseStrength,
    VideoFrameColorSettings ColorSettings = default);

public sealed record TextureNativeFrameInfo(
    int Width,
    int Height,
    double FramesPerSecond,
    string DeviceMode,
    string MediaSubtype,
    long FrameNumber);

public sealed class TextureNativeFrameLease : IDisposable
{
    private IntPtr _resource;
    private PooledFrameBuffer? _nv12PreviewBuffer;

    internal TextureNativeFrameLease(
        IntPtr resource,
        int subresource,
        int width,
        int height,
        double framesPerSecond,
        string deviceMode,
        string mediaSubtype,
        long frameNumber,
        PooledFrameBuffer? nv12PreviewBuffer = null,
        int nv12PreviewStride = 0)
    {
        _resource = resource;
        Subresource = subresource;
        Width = width;
        Height = height;
        FramesPerSecond = framesPerSecond;
        DeviceMode = deviceMode;
        MediaSubtype = mediaSubtype;
        FrameNumber = frameNumber;
        _nv12PreviewBuffer = nv12PreviewBuffer;
        Nv12PreviewStride = nv12PreviewStride;
    }

    public IntPtr Resource => _resource;

    public int Subresource { get; }

    public int Width { get; }

    public int Height { get; }

    public double FramesPerSecond { get; }

    public string DeviceMode { get; }

    public string MediaSubtype { get; }

    public long FrameNumber { get; }

    public byte[]? Nv12PreviewBytes => _nv12PreviewBuffer?.Bytes;

    public int Nv12PreviewStride { get; }

    public bool IsValid => _resource != IntPtr.Zero;

    public TextureNativeFrameLease? Duplicate()
    {
        var resource = _resource;
        if (resource == IntPtr.Zero)
        {
            return null;
        }

        System.Runtime.InteropServices.Marshal.AddRef(resource);
        var nv12PreviewBuffer = _nv12PreviewBuffer?.AddReference();

        return new TextureNativeFrameLease(
            resource,
            Subresource,
            Width,
            Height,
            FramesPerSecond,
            DeviceMode,
            MediaSubtype,
            FrameNumber,
            nv12PreviewBuffer,
            Nv12PreviewStride);
    }

    public void Dispose()
    {
        var resource = Interlocked.Exchange(ref _resource, IntPtr.Zero);
        if (resource != IntPtr.Zero)
        {
            System.Runtime.InteropServices.Marshal.Release(resource);
        }

        Interlocked.Exchange(ref _nv12PreviewBuffer, null)?.Dispose();
    }
}

internal sealed class PooledFrameBuffer : IDisposable
{
    private byte[]? _bytes;
    private int _referenceCount = 1;

    private PooledFrameBuffer(int minimumLength)
    {
        _bytes = ArrayPool<byte>.Shared.Rent(minimumLength);
    }

    public byte[] Bytes => _bytes ?? throw new ObjectDisposedException(nameof(PooledFrameBuffer));

    public static PooledFrameBuffer Rent(int minimumLength)
    {
        return new PooledFrameBuffer(minimumLength);
    }

    public PooledFrameBuffer AddReference()
    {
        if (Interlocked.Increment(ref _referenceCount) <= 1)
        {
            Interlocked.Decrement(ref _referenceCount);
            throw new ObjectDisposedException(nameof(PooledFrameBuffer));
        }

        return this;
    }

    public void Dispose()
    {
        if (Interlocked.Decrement(ref _referenceCount) != 0)
        {
            return;
        }

        var bytes = Interlocked.Exchange(ref _bytes, null);
        if (bytes is not null)
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }
}

internal static class Nv12FrameConverter
{
    public static byte[]? ConvertToBgra(IntPtr source, int sourceLength, int width, int height, out int bgraStride)
    {
        bgraStride = width * 4;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var pitch = Math.Max(width, sourceLength * 2 / Math.Max(1, height * 3));
        var requiredLength = pitch * height + pitch * ((height + 1) / 2);
        if (sourceLength < requiredLength)
        {
            return null;
        }

        var nv12 = new byte[requiredLength];
        System.Runtime.InteropServices.Marshal.Copy(source, nv12, 0, requiredLength);
        return ConvertToBgra(nv12, pitch, width, height, out bgraStride);
    }

    public static byte[]? ConvertToBgra(byte[] nv12, int nv12Stride, int width, int height, out int bgraStride)
    {
        return ConvertToBgra(
            nv12,
            nv12Stride,
            width,
            height,
            width,
            out _,
            out _,
            out bgraStride);
    }

    public static byte[]? ConvertToBgra(
        byte[] nv12,
        int nv12Stride,
        int width,
        int height,
        int maximumWidth,
        out int outputWidth,
        out int outputHeight,
        out int bgraStride)
    {
        bgraStride = width * 4;
        outputWidth = width;
        outputHeight = height;
        if (width <= 0 || height <= 0 || nv12Stride < width)
        {
            return null;
        }

        var pitch = nv12Stride;
        var requiredLength = pitch * height + pitch * ((height + 1) / 2);
        if (nv12.Length < requiredLength)
        {
            return null;
        }

        if (maximumWidth > 0 && width > maximumWidth)
        {
            var scale = maximumWidth / (double)width;
            outputWidth = maximumWidth;
            outputHeight = Math.Max(1, (int)Math.Round(height * scale));
        }

        bgraStride = outputWidth * 4;
        var bgra = new byte[bgraStride * outputHeight];
        var uvOffset = pitch * height;
        for (var y = 0; y < outputHeight; y++)
        {
            var sourceY = Math.Min(height - 1, (int)((y + 0.5d) * height / outputHeight));
            var yRow = sourceY * pitch;
            var uvRow = uvOffset + (sourceY / 2) * pitch;
            var outputRow = y * bgraStride;
            for (var x = 0; x < outputWidth; x++)
            {
                var sourceX = Math.Min(width - 1, (int)((x + 0.5d) * width / outputWidth));
                var yy = nv12[yRow + sourceX];
                var uvIndex = uvRow + (sourceX & ~1);
                var u = nv12[uvIndex];
                var v = nv12[uvIndex + 1];
                var c = yy - 16;
                var d = u - 128;
                var e = v - 128;
                var r = ClampToByte((298 * c + 409 * e + 128) >> 8);
                var g = ClampToByte((298 * c - 100 * d - 208 * e + 128) >> 8);
                var b = ClampToByte((298 * c + 516 * d + 128) >> 8);
                var output = outputRow + x * 4;
                bgra[output] = b;
                bgra[output + 1] = g;
                bgra[output + 2] = r;
                bgra[output + 3] = 255;
            }
        }

        return bgra;
    }

    private static byte ClampToByte(int value)
    {
        return (byte)Math.Clamp(value, 0, 255);
    }
}

public static class TextureNativeCameraRecorder
{
    public static TextureNativeCameraRecordingSession StartSession(
        string cameraName,
        CameraVideoMode? mode,
        string path,
        TextureNativeRecordingOptions? options = null)
    {
        return new TextureNativeCameraRecordingSession(cameraName, mode, path, options);
    }

    public static Task<TextureNativeRecordingResult> RecordAsync(
        string cameraName,
        CameraVideoMode? mode,
        string path,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => Record(cameraName, mode, path, duration, cancellationToken),
            cancellationToken);
    }

    private static TextureNativeRecordingResult Record(
        string cameraName,
        CameraVideoMode? mode,
        string path,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        using var _ = MediaFoundationCameraDeviceFactory.Startup();
        ITextureNativeDeviceManager? deviceManager = null;
        object? mediaSource = null;
        IMFSourceReader? reader = null;
        MediaFoundationTextureVideoRecorder? recorder = null;
        try
        {
            (deviceManager, reader, mediaSource) = OpenTextureSourceReader(cameraName, mode);

            var (width, height, fps, subtype) = ReadCurrentFormat(reader, mode);
            recorder = new MediaFoundationTextureVideoRecorder(
                path,
                width,
                height,
                fps,
                subtype,
                deviceManager.Manager);

            var started = DateTimeOffset.UtcNow;
            var samplesWritten = 0;
            var nextSampleTime = 0L;
            var sampleDuration = Math.Max(1, (long)Math.Round(MediaFoundationInterop.TicksPerSecond / Math.Clamp(fps, 1d, 120d)));

            while (DateTimeOffset.UtcNow - started < duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = reader.ReadSample(
                    MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                    0,
                    out var actualStreamIndex,
                    out var streamFlags,
                    out var timestamp,
                    out var sampleObject);

                if (MediaFoundationInterop.Failed(result))
                {
                    return CreateResult(
                        false,
                        path,
                        samplesWritten,
                        deviceManager.ModeName,
                        subtype,
                        width,
                        height,
                        fps,
                        $"Texture-native ReadSample failed: 0x{result:X8}");
                }

                if ((streamFlags & MediaFoundationInterop.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    break;
                }

                if (sampleObject is not IMFSample sample)
                {
                    MediaFoundationInterop.ReleaseComObject(sampleObject);
                    continue;
                }

                try
                {
                    MediaFoundationInterop.ThrowIfFailed(sample.SetSampleTime(nextSampleTime));
                    MediaFoundationInterop.ThrowIfFailed(sample.SetSampleDuration(sampleDuration));
                    recorder.WriteSample(sample);
                    samplesWritten++;
                    nextSampleTime += sampleDuration;
                }
                finally
                {
                    MediaFoundationInterop.ReleaseComObject(sampleObject);
                }
            }

            recorder.Stop();
            return CreateResult(
                samplesWritten > 0 && System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 4096,
                path,
                samplesWritten,
                deviceManager.ModeName,
                subtype,
                width,
                height,
                fps,
                samplesWritten > 0
                    ? "Texture-native GPU sample recording completed."
                    : "No texture-native samples were written.");
        }
        catch (Exception ex)
        {
            return CreateResult(
                false,
                path,
                recorder?.SamplesWritten ?? 0,
                deviceManager?.ModeName ?? "none",
                Guid.Empty,
                mode?.Width ?? 0,
                mode?.Height ?? 0,
                mode?.FramesPerSecond ?? 0d,
                ex.Message);
        }
        finally
        {
            recorder?.Dispose();
            MediaFoundationInterop.ReleaseComObject(reader);
            MediaFoundationInterop.ReleaseComObject(mediaSource);
            deviceManager?.Dispose();
        }
    }

    internal static (ITextureNativeDeviceManager DeviceManager, IMFSourceReader Reader, object MediaSource) OpenTextureSourceReader(
        string cameraName,
        CameraVideoMode? mode)
    {
        return OpenTextureSourceReader(new CameraDevice(-1, cameraName, string.Empty), mode);
    }

    internal static (ITextureNativeDeviceManager DeviceManager, IMFSourceReader Reader, object MediaSource) OpenTextureSourceReader(
        CameraDevice camera,
        CameraVideoMode? mode)
    {
        Exception? direct3D12Error = null;
        ITextureNativeDeviceManager? deviceManager = null;
        object? mediaSource = null;
        try
        {
            deviceManager = Direct3D12DeviceManager.Create();
            ValidateTextureReader(camera, mode, deviceManager, "D3D12");
            var (reader, liveMediaSource) = CreateTextureSourceReader(camera, mode, deviceManager);
            return (deviceManager, reader, liveMediaSource);
        }
        catch (Exception ex)
        {
            direct3D12Error = ex;
            MediaFoundationInterop.ReleaseComObject(mediaSource);
            deviceManager?.Dispose();
        }

        mediaSource = null;
        deviceManager = null;
        try
        {
            deviceManager = Direct3D11DeviceManager.Create();
            var (reader, liveMediaSource) = CreateTextureSourceReader(camera, mode, deviceManager);
            return (deviceManager, reader, liveMediaSource);
        }
        catch (Exception ex)
        {
            MediaFoundationInterop.ReleaseComObject(mediaSource);
            deviceManager?.Dispose();
            throw new InvalidOperationException(
                $"Texture-native camera reader unavailable. D3D12 path: {direct3D12Error?.Message ?? "not attempted"} D3D11 bridge: {ex.Message}",
                ex);
        }
    }

    private static (IMFSourceReader Reader, object MediaSource) CreateTextureSourceReader(
        CameraDevice camera,
        CameraVideoMode? mode,
        ITextureNativeDeviceManager deviceManager)
    {
        var reader = MediaFoundationCameraDeviceFactory.CreateTextureSourceReader(
            camera,
            mode,
            deviceManager.Manager,
            out var mediaSource,
            enableAdvancedVideoProcessing: true,
            preferredSubtype: MediaFoundationGuids.MFVideoFormat_NV12,
            configureMediaType: true);
        return (reader, mediaSource);
    }

    private static void ValidateTextureReader(
        CameraDevice camera,
        CameraVideoMode? mode,
        ITextureNativeDeviceManager deviceManager,
        string pathName)
    {
        object? validationMediaSource = null;
        IMFSourceReader? validationReader = null;
        try
        {
            (validationReader, validationMediaSource) = CreateTextureSourceReader(camera, mode, deviceManager);
            EnsureTextureReaderProducesSample(validationReader, pathName);
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(validationReader);
            MediaFoundationInterop.ReleaseComObject(validationMediaSource);
        }
    }

    private static void EnsureTextureReaderProducesSample(IMFSourceReader reader, string pathName)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = reader.ReadSample(
                MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                0,
                out _,
                out var streamFlags,
                out _,
                out var sampleObject);

            try
            {
                if (MediaFoundationInterop.Failed(result))
                {
                    lastError = new InvalidOperationException($"{pathName} texture reader warmup failed: 0x{result:X8}");
                    Thread.Sleep(25);
                    continue;
                }

                if ((streamFlags & MediaFoundationInterop.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    throw new InvalidOperationException($"{pathName} texture reader ended during warmup.");
                }

                if (sampleObject is IMFSample)
                {
                    return;
                }
            }
            finally
            {
                MediaFoundationInterop.ReleaseComObject(sampleObject);
            }

            Thread.Sleep(25);
        }

        throw lastError ?? new InvalidOperationException($"{pathName} texture reader produced no samples during warmup.");
    }

    private static TextureNativeRecordingResult CreateResult(
        bool success,
        string path,
        int samplesWritten,
        string deviceMode,
        Guid subtype,
        int width,
        int height,
        double fps,
        string status)
    {
        var bytes = System.IO.File.Exists(path) ? new System.IO.FileInfo(path).Length : 0;
        return new TextureNativeRecordingResult(
            success,
            path,
            samplesWritten,
            bytes,
            deviceMode,
            MediaFoundationInterop.FormatSubtype(subtype),
            width,
            height,
            fps,
            status);
    }

    private static (int Width, int Height, double Fps, Guid Subtype) ReadCurrentFormat(
        IMFSourceReader reader,
        CameraVideoMode? requestedMode)
    {
        var width = requestedMode?.Width ?? 1280;
        var height = requestedMode?.Height ?? 720;
        var fps = requestedMode?.FramesPerSecond ?? 30d;
        var subtype = MediaFoundationGuids.MFVideoFormat_NV12;

        var result = reader.GetCurrentMediaType(
            MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
            out var currentType);
        if (MediaFoundationInterop.Failed(result))
        {
            return (width, height, fps, subtype);
        }

        try
        {
            if (MediaFoundationInterop.TryGetFrameSize(currentType, out var activeWidth, out var activeHeight))
            {
                width = activeWidth;
                height = activeHeight;
            }

            if (MediaFoundationInterop.TryGetFrameRate(currentType, out var activeFps))
            {
                fps = activeFps;
            }

            currentType.GetGUID(MediaFoundationGuids.MF_MT_SUBTYPE, out subtype);
            return (width, height, fps, subtype);
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(currentType);
        }
    }
}

public sealed class TextureNativeCameraStream : IDisposable
{
    private static readonly TimeSpan StreamStartTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan StreamStopTimeout = TimeSpan.FromSeconds(3);

    private readonly object _stateLock = new();
    private readonly object _processedDenoiseLock = new();
    private readonly CameraDevice _camera;
    private readonly CameraVideoMode? _mode;
    private readonly ManualResetEventSlim _streamReady = new(false);
    private readonly CancellationTokenSource _cancellation = new();
    private MediaFoundationCameraDeviceFactory.MediaFoundationScope? _mediaFoundationScope;
    private ITextureNativeDeviceManager? _deviceManager;
    private object? _mediaSource;
    private IMFSourceReader? _reader;
    private Task? _captureTask;
    private int _width;
    private int _height;
    private double _fps;
    private Guid _subtype;
    private long _sampleDuration;
    private MediaFoundationTextureVideoRecorder? _recorder;
    private MediaFoundationVideoRecorder? _processedRecorder;
    private byte[]? _previousProcessedDenoiseFrame;
    private string? _recordingPath;
    private string _recordingPipeline = "Media Foundation texture-native raw camera samples";
    private bool _recordingMatchesPreviewDenoise;
    private bool _recordingColorPolishApplied;
    private bool _recordingMatchesPreviewColor;
    private VideoFrameColorSettings _processedRecordingColorSettings = VideoFrameColorSettings.Off;
    private bool _processedRecordingDenoiseEnabled;
    private double _processedRecordingDenoiseStrength = 2d;
    private bool _isPaused;
    private bool _isStopping;
    private long _nextSampleTime;
    private long _framesRead;
    private string _status = "Texture-native camera stream started.";
    private bool _captureStarted;
    private Exception? _startException;

    public TextureNativeCameraStream(CameraDevice camera, CameraVideoMode? mode, bool startImmediately = true)
    {
        _camera = camera;
        _mode = mode;
        if (startImmediately)
        {
            Start();
        }
    }

    public event EventHandler<TextureNativeFrameInfo>? FrameAvailable;
    public event EventHandler<TextureNativeFrameLease>? TextureFrameAvailable;
    public event EventHandler<string>? StatusChanged;

    public int Width => _width;

    public int Height => _height;

    public double FramesPerSecond => _fps;

    public string DeviceMode => _deviceManager?.ModeName ?? "starting";

    public string MediaSubtype => MediaFoundationInterop.FormatSubtype(_subtype);

    public long FramesRead => Interlocked.Read(ref _framesRead);

    public int SamplesWritten => _recorder?.SamplesWritten ?? _processedRecorder?.SamplesWritten ?? 0;

    public IntPtr DuplicateNativeD3D12Device() => _deviceManager?.DuplicateNativeD3D12Device() ?? IntPtr.Zero;

    public void Start()
    {
        lock (_stateLock)
        {
            if (_captureStarted || _isStopping)
            {
                return;
            }

            _captureStarted = true;
            _captureTask = Task.Run(() => CaptureLoop(_cancellation.Token));
        }

        if (!_streamReady.Wait(StreamStartTimeout))
        {
            throw new TimeoutException($"Texture-native stream did not initialize within {StreamStartTimeout.TotalSeconds:0.#} seconds.");
        }

        if (_startException is not null)
        {
            throw new InvalidOperationException($"Texture-native stream failed to initialize: {_startException.Message}", _startException);
        }
    }

    public bool IsRecording
    {
        get
        {
            lock (_stateLock)
            {
                return _recorder is not null || _processedRecorder is not null;
            }
        }
    }

    public bool StartRecording(string path, TextureNativeRecordingOptions? options = null)
    {
        lock (_stateLock)
        {
            var deviceManager = _deviceManager
                ?? throw new InvalidOperationException("Texture-native recording requires an initialized device manager.");

            if (_recorder is not null || _processedRecorder is not null)
            {
                return true;
            }

            if (options?.ProcessedOutputEnabled == true)
            {
                _processedRecorder = new MediaFoundationVideoRecorder(
                    path,
                    _width,
                    _height,
                    _fps,
                    d3dManager: null);
                _processedRecordingDenoiseEnabled = options.DenoiseEnabled;
                _processedRecordingDenoiseStrength = options.DenoiseStrength;
                _processedRecordingColorSettings = options.ColorSettings;
                ResetProcessedDenoiseHistory();
                _recordingPipeline = "Texture-native processed BGRA bridge";
                _recordingMatchesPreviewDenoise = true;
                _recordingColorPolishApplied = options.ColorSettings.HasVisibleAdjustments;
                _recordingMatchesPreviewColor = true;
            }
            else
            {
                _recorder = new MediaFoundationTextureVideoRecorder(
                    path,
                    _width,
                    _height,
                    _fps,
                    _subtype,
                    deviceManager.Manager);
                _processedRecordingDenoiseEnabled = false;
                _processedRecordingColorSettings = VideoFrameColorSettings.Off;
                ResetProcessedDenoiseHistory();
                _recordingPipeline = "Media Foundation texture-native raw camera samples";
                _recordingMatchesPreviewDenoise = options?.DenoiseEnabled != true;
                _recordingColorPolishApplied = false;
                _recordingMatchesPreviewColor = options?.ColorSettings.HasVisibleAdjustments != true;
            }

            _recordingPath = path;
            _nextSampleTime = 0;
            _isPaused = false;
            _status = $"Texture-native GPU recording started: {System.IO.Path.GetFileName(path)}";
            StatusChanged?.Invoke(this, _status);
            return true;
        }
    }

    public void PauseRecording()
    {
        lock (_stateLock)
        {
            _isPaused = true;
            _processedRecorder?.Pause();
            _status = "Texture-native GPU recording paused.";
            StatusChanged?.Invoke(this, _status);
        }
    }

    public void ResumeRecording()
    {
        lock (_stateLock)
        {
            _isPaused = false;
            _processedRecorder?.Resume();
            _status = "Texture-native GPU recording resumed.";
            StatusChanged?.Invoke(this, _status);
        }
    }

    public TextureNativeRecordingResult? StopRecording()
    {
        MediaFoundationTextureVideoRecorder? recorder;
        MediaFoundationVideoRecorder? processedRecorder;
        string? path;
        string status;
        string recordingPipeline;
        bool processedDenoiseEnabled;
        bool recordingMatchesPreviewDenoise;
        bool recordingColorPolishApplied;
        bool recordingMatchesPreviewColor;
        lock (_stateLock)
        {
            recorder = _recorder;
            processedRecorder = _processedRecorder;
            path = _recordingPath;
            status = _status;
            recordingPipeline = _recordingPipeline;
            processedDenoiseEnabled = _processedRecordingDenoiseEnabled;
            recordingMatchesPreviewDenoise = _recordingMatchesPreviewDenoise;
            recordingColorPolishApplied = _recordingColorPolishApplied;
            recordingMatchesPreviewColor = _recordingMatchesPreviewColor;
            _recorder = null;
            _processedRecorder = null;
            _recordingPath = null;
            _isPaused = false;
            ResetProcessedDenoiseHistory();
            _processedRecordingDenoiseEnabled = false;
            _processedRecordingColorSettings = VideoFrameColorSettings.Off;
            _recordingMatchesPreviewDenoise = false;
            _recordingColorPolishApplied = false;
            _recordingMatchesPreviewColor = false;
            _recordingPipeline = "Media Foundation texture-native raw camera samples";
        }

        if ((recorder is null && processedRecorder is null) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            recorder?.Stop();
            processedRecorder?.Stop();
            var bytes = System.IO.File.Exists(path) ? new System.IO.FileInfo(path).Length : 0;
            var samplesWritten = recorder?.SamplesWritten ?? processedRecorder?.SamplesWritten ?? 0;
            var success = samplesWritten > 0 && bytes > 4096;
            status = success
                ? processedRecorder is not null
                    ? "Texture-native processed bridge recording completed."
                    : "Texture-native shared stream recording completed."
                : status;
            return new TextureNativeRecordingResult(
                success,
                path,
                samplesWritten,
                bytes,
                _deviceManager?.ModeName ?? "none",
                processedRecorder is not null ? "rgb32" : MediaFoundationInterop.FormatSubtype(_subtype),
                _width,
                _height,
                _fps,
                status,
                recordingPipeline,
                processedDenoiseEnabled,
                recordingMatchesPreviewDenoise,
                recordingColorPolishApplied,
                recordingMatchesPreviewColor);
        }
        finally
        {
            recorder?.Dispose();
            processedRecorder?.Dispose();
        }
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (_isStopping)
            {
                return;
            }

            _isStopping = true;
        }

        Task? captureTask;
        _cancellation.Cancel();
        TryFlushSourceReader();
        lock (_stateLock)
        {
            captureTask = _captureTask;
        }

        try
        {
            captureTask?.Wait(StreamStopTimeout);
        }
        catch
        {
        }

        StopRecording();
    }

    public void Dispose()
    {
        Stop();
        _cancellation.Dispose();
        _streamReady.Dispose();
        MediaFoundationInterop.ReleaseComObject(_reader);
        MediaFoundationInterop.ReleaseComObject(_mediaSource);
        _deviceManager?.Dispose();
        _mediaFoundationScope?.Dispose();
    }

    private void CaptureLoop(CancellationToken cancellationToken)
    {
        try
        {
            _mediaFoundationScope = MediaFoundationCameraDeviceFactory.Startup();
            (_deviceManager, _reader, _mediaSource) = TextureNativeCameraRecorder.OpenTextureSourceReader(_camera, _mode);
            (_width, _height, _fps, _subtype) = ReadCurrentFormat(_reader, _mode);
            _sampleDuration = Math.Max(1, (long)Math.Round(MediaFoundationInterop.TicksPerSecond / Math.Clamp(_fps, 1d, 120d)));
            _streamReady.Set();

            while (!cancellationToken.IsCancellationRequested)
            {
                var reader = _reader;
                if (reader is null)
                {
                    ReportStatus("Texture-native shared stream reader is not initialized.");
                    break;
                }

                var result = reader.ReadSample(
                    MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                    0,
                    out _,
                    out var streamFlags,
                    out _,
                    out var sampleObject);

                if (MediaFoundationInterop.Failed(result))
                {
                    ReportStatus($"Texture-native shared stream read failed: 0x{result:X8}");
                    break;
                }

                if ((streamFlags & MediaFoundationInterop.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    ReportStatus("Texture-native shared stream ended.");
                    break;
                }

                if (sampleObject is not IMFSample sample)
                {
                    MediaFoundationInterop.ReleaseComObject(sampleObject);
                    continue;
                }

                try
                {
                    var frameNumber = Interlocked.Increment(ref _framesRead);
                    using var frameLease = TryCreateFrameLease(sample, frameNumber);
                    FrameAvailable?.Invoke(
                        this,
                        new TextureNativeFrameInfo(
                            _width,
                            _height,
                            _fps,
                            _deviceManager.ModeName,
                            MediaFoundationInterop.FormatSubtype(_subtype),
                            frameNumber));
                    if (frameLease is not null)
                    {
                        TextureFrameAvailable?.Invoke(this, frameLease);
                    }

                    WriteRecordingSample(sample);
                }
                finally
                {
                    MediaFoundationInterop.ReleaseComObject(sampleObject);
                }
            }
        }
        catch (Exception ex)
        {
            _startException ??= ex;
            _streamReady.Set();
            ReportStatus(ex.Message);
        }
    }

    private void TryFlushSourceReader()
    {
        try
        {
            _reader?.Flush(MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
        }
        catch
        {
        }
    }

    private void WriteRecordingSample(IMFSample sample)
    {
        MediaFoundationTextureVideoRecorder? recorder;
        MediaFoundationVideoRecorder? processedRecorder;
        bool isPaused;
        bool processedDenoiseEnabled;
        double processedDenoiseStrength;
        VideoFrameColorSettings processedColorSettings;
        lock (_stateLock)
        {
            recorder = _recorder;
            processedRecorder = _processedRecorder;
            isPaused = _isPaused;
            processedDenoiseEnabled = _processedRecordingDenoiseEnabled;
            processedDenoiseStrength = _processedRecordingDenoiseStrength;
            processedColorSettings = _processedRecordingColorSettings;
        }

        if (isPaused || (recorder is null && processedRecorder is null))
        {
            return;
        }

        if (processedRecorder is not null)
        {
            if (!TryCreateBgraFrame(sample, out var bgraBytes))
            {
                return;
            }

            if (processedDenoiseEnabled)
            {
                lock (_processedDenoiseLock)
                {
                    VideoFrameDenoiser.ApplyTemporalDenoise(bgraBytes, processedDenoiseStrength, ref _previousProcessedDenoiseFrame);
                }
            }
            else
            {
                ResetProcessedDenoiseHistory();
            }

            if (processedColorSettings.HasVisibleAdjustments)
            {
                VideoFrameColorProcessor.Apply(bgraBytes, processedColorSettings);
            }

            processedRecorder.WriteFrame(bgraBytes);
        }
        else if (recorder is not null)
        {
            MediaFoundationInterop.ThrowIfFailed(sample.SetSampleTime(_nextSampleTime));
            MediaFoundationInterop.ThrowIfFailed(sample.SetSampleDuration(_sampleDuration));
            recorder.WriteSample(sample);
        }

        _nextSampleTime += _sampleDuration;
    }

    private void ReportStatus(string status)
    {
        lock (_stateLock)
        {
            _status = status;
        }

        StatusChanged?.Invoke(this, status);
    }

    private void ResetProcessedDenoiseHistory()
    {
        lock (_processedDenoiseLock)
        {
            _previousProcessedDenoiseFrame = null;
        }
    }

    private bool TryCreateBgraFrame(IMFSample sample, out byte[] bgraBytes)
    {
        bgraBytes = [];
        IMFMediaBuffer? buffer = null;
        try
        {
            var bufferResult = sample.GetBufferByIndex(0, out buffer);
            if (MediaFoundationInterop.Failed(bufferResult) || buffer is null)
            {
                return false;
            }

            if (_subtype != MediaFoundationGuids.MFVideoFormat_NV12)
            {
                return false;
            }

            var result = buffer.Lock(out var source, out _, out var currentLength);
            if (MediaFoundationInterop.Failed(result) || source == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var converted = Nv12FrameConverter.ConvertToBgra(source, currentLength, _width, _height, out _);
                if (converted is null)
                {
                    return false;
                }

                bgraBytes = converted;
                return true;
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(buffer);
        }
    }

    private TextureNativeFrameLease? TryCreateFrameLease(IMFSample sample, long frameNumber)
    {
        IMFMediaBuffer? buffer = null;
        try
        {
            var bufferResult = sample.GetBufferByIndex(0, out buffer);
            if (MediaFoundationInterop.Failed(bufferResult) || buffer is null)
            {
                return null;
            }

            var dxgiBuffer = QueryDxgiBuffer(buffer);
            if (dxgiBuffer is null)
            {
                return null;
            }

            try
            {
                var deviceManager = _deviceManager;
                if (deviceManager is null)
                {
                    return null;
                }

                var subresource = 0;
                dxgiBuffer.GetSubresourceIndex(out subresource);
                var resourceResult = dxgiBuffer.GetResource(
                    deviceManager.TextureResourceId,
                    out var resource);
                if (MediaFoundationInterop.Failed(resourceResult) || resource == IntPtr.Zero)
                {
                    return null;
                }

                var nv12PreviewBytes = TryCreateNv12Preview(buffer, out var nv12PreviewStride);

                return new TextureNativeFrameLease(
                    resource,
                    subresource,
                    _width,
                    _height,
                    _fps,
                    deviceManager.ModeName,
                    MediaFoundationInterop.FormatSubtype(_subtype),
                    frameNumber,
                    nv12PreviewBytes,
                    nv12PreviewStride);
            }
            finally
            {
                MediaFoundationInterop.ReleaseComObject(dxgiBuffer);
            }
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(buffer);
        }
    }

    private PooledFrameBuffer? TryCreateNv12Preview(IMFMediaBuffer buffer, out int nv12Stride)
    {
        nv12Stride = 0;
        if (_subtype != MediaFoundationGuids.MFVideoFormat_NV12)
        {
            return null;
        }

        var result = buffer.Lock(out var source, out _, out var currentLength);
        if (MediaFoundationInterop.Failed(result) || source == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var pitch = Math.Max(_width, currentLength * 2 / Math.Max(1, _height * 3));
            var requiredLength = pitch * _height + pitch * ((_height + 1) / 2);
            if (currentLength < requiredLength)
            {
                return null;
            }

            var nv12 = PooledFrameBuffer.Rent(requiredLength);
            try
            {
                System.Runtime.InteropServices.Marshal.Copy(source, nv12.Bytes, 0, requiredLength);
                nv12Stride = pitch;
                return nv12;
            }
            catch
            {
                nv12.Dispose();
                throw;
            }
        }
        finally
        {
            buffer.Unlock();
        }
    }

    private static IMFDXGIBuffer? QueryDxgiBuffer(IMFMediaBuffer buffer)
    {
        var unknown = IntPtr.Zero;
        var dxgiBufferPointer = IntPtr.Zero;
        try
        {
            unknown = System.Runtime.InteropServices.Marshal.GetIUnknownForObject(buffer);
            var dxgiBufferId = typeof(IMFDXGIBuffer).GUID;
            if (System.Runtime.InteropServices.Marshal.QueryInterface(unknown, in dxgiBufferId, out dxgiBufferPointer) < 0
                || dxgiBufferPointer == IntPtr.Zero)
            {
                return null;
            }

            return (IMFDXGIBuffer)System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(dxgiBufferPointer);
        }
        finally
        {
            if (dxgiBufferPointer != IntPtr.Zero)
            {
                System.Runtime.InteropServices.Marshal.Release(dxgiBufferPointer);
            }

            if (unknown != IntPtr.Zero)
            {
                System.Runtime.InteropServices.Marshal.Release(unknown);
            }
        }
    }

    private static (int Width, int Height, double Fps, Guid Subtype) ReadCurrentFormat(
        IMFSourceReader reader,
        CameraVideoMode? requestedMode)
    {
        var width = requestedMode?.Width ?? 1280;
        var height = requestedMode?.Height ?? 720;
        var fps = requestedMode?.FramesPerSecond ?? 30d;
        var subtype = MediaFoundationGuids.MFVideoFormat_NV12;

        var result = reader.GetCurrentMediaType(
            MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
            out var currentType);
        if (MediaFoundationInterop.Failed(result))
        {
            return (width, height, fps, subtype);
        }

        try
        {
            if (MediaFoundationInterop.TryGetFrameSize(currentType, out var activeWidth, out var activeHeight))
            {
                width = activeWidth;
                height = activeHeight;
            }

            if (MediaFoundationInterop.TryGetFrameRate(currentType, out var activeFps))
            {
                fps = activeFps;
            }

            currentType.GetGUID(MediaFoundationGuids.MF_MT_SUBTYPE, out subtype);
            return (width, height, fps, subtype);
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(currentType);
        }
    }
}

public sealed class TextureNativeCameraRecordingSession : IDisposable
{
    private static readonly TimeSpan RecordingStopTimeout = TimeSpan.FromSeconds(3);
    private readonly object _stateLock = new();
    private readonly object _processedDenoiseLock = new();
    private readonly MediaFoundationCameraDeviceFactory.MediaFoundationScope _mediaFoundationScope;
    private readonly ITextureNativeDeviceManager _deviceManager;
    private readonly object _mediaSource;
    private readonly IMFSourceReader _reader;
    private readonly MediaFoundationTextureVideoRecorder? _recorder;
    private readonly MediaFoundationVideoRecorder? _processedRecorder;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _recordingTask;
    private readonly int _width;
    private readonly int _height;
    private readonly double _fps;
    private readonly Guid _subtype;
    private readonly long _sampleDuration;
    private readonly string _recordingPipeline;
    private readonly bool _recordingDenoiseApplied;
    private readonly bool _recordingMatchesPreviewDenoise;
    private readonly double _recordingDenoiseStrength;
    private readonly bool _recordingColorPolishApplied;
    private readonly bool _recordingMatchesPreviewColor;
    private readonly VideoFrameColorSettings _recordingColorSettings;
    private byte[]? _previousProcessedDenoiseFrame;
    private bool _isPaused;
    private bool _isStopping;
    private long _nextSampleTime;
    private string _status = "Texture-native GPU recording started.";

    internal TextureNativeCameraRecordingSession(
        string cameraName,
        CameraVideoMode? mode,
        string path,
        TextureNativeRecordingOptions? options = null)
    {
        Path = path;
        _mediaFoundationScope = MediaFoundationCameraDeviceFactory.Startup();
        (_deviceManager, _reader, _mediaSource) = TextureNativeCameraRecorder.OpenTextureSourceReader(cameraName, mode);
        (_width, _height, _fps, _subtype) = ReadCurrentFormat(_reader, mode);
        _sampleDuration = Math.Max(1, (long)Math.Round(MediaFoundationInterop.TicksPerSecond / Math.Clamp(_fps, 1d, 120d)));
        if (options?.ProcessedOutputEnabled == true)
        {
            _processedRecorder = new MediaFoundationVideoRecorder(
                path,
                _width,
                _height,
                _fps,
                d3dManager: null);
            _recordingPipeline = "Texture-native processed BGRA bridge";
            _recordingDenoiseApplied = options.DenoiseEnabled;
            _recordingMatchesPreviewDenoise = true;
            _recordingDenoiseStrength = options.DenoiseStrength;
            _recordingColorSettings = options.ColorSettings;
            _recordingColorPolishApplied = options.ColorSettings.HasVisibleAdjustments;
            _recordingMatchesPreviewColor = true;
        }
        else
        {
            _recorder = new MediaFoundationTextureVideoRecorder(
                path,
                _width,
                _height,
                _fps,
                _subtype,
                _deviceManager.Manager);
            _recordingPipeline = "Media Foundation texture-native raw camera samples";
            _recordingDenoiseApplied = false;
            _recordingMatchesPreviewDenoise = options?.DenoiseEnabled != true;
            _recordingDenoiseStrength = options?.DenoiseStrength ?? 2d;
            _recordingColorSettings = VideoFrameColorSettings.Off;
            _recordingColorPolishApplied = false;
            _recordingMatchesPreviewColor = options?.ColorSettings.HasVisibleAdjustments != true;
        }

        _recordingTask = Task.Run(() => CaptureLoop(_cancellation.Token));
    }

    public string Path { get; }

    public bool IsPaused
    {
        get
        {
            lock (_stateLock)
            {
                return _isPaused;
            }
        }
    }

    public int SamplesWritten => _recorder?.SamplesWritten ?? _processedRecorder?.SamplesWritten ?? 0;

    public void Pause()
    {
        lock (_stateLock)
        {
            _isPaused = true;
            _processedRecorder?.Pause();
            _status = "Texture-native GPU recording paused.";
        }
    }

    public void Resume()
    {
        lock (_stateLock)
        {
            _isPaused = false;
            _processedRecorder?.Resume();
            _status = "Texture-native GPU recording resumed.";
        }
    }

    public TextureNativeRecordingResult Stop()
    {
        lock (_stateLock)
        {
            if (_isStopping)
            {
                return CreateResult(SamplesWritten > 0, _status);
            }

            _isStopping = true;
        }

        _cancellation.Cancel();
        try
        {
            _reader.Flush(MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
        }
        catch
        {
        }

        try
        {
            _recordingTask.Wait(RecordingStopTimeout);
        }
        catch
        {
        }

        _recorder?.Stop();
        _processedRecorder?.Stop();
        return CreateResult(
            SamplesWritten > 0 && System.IO.File.Exists(Path) && new System.IO.FileInfo(Path).Length > 4096,
            SamplesWritten > 0
                ? _processedRecorder is not null
                    ? "Texture-native processed bridge recording completed."
                    : "Texture-native GPU sample recording completed."
                : _status);
    }

    public void Dispose()
    {
        Stop();
        _cancellation.Dispose();
        _recorder?.Dispose();
        _processedRecorder?.Dispose();
        MediaFoundationInterop.ReleaseComObject(_reader);
        MediaFoundationInterop.ReleaseComObject(_mediaSource);
        _deviceManager.Dispose();
        _mediaFoundationScope.Dispose();
    }

    private void CaptureLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (IsPaused)
                {
                    Thread.Sleep(20);
                    continue;
                }

                var result = _reader.ReadSample(
                    MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                    0,
                    out _,
                    out var streamFlags,
                    out _,
                    out var sampleObject);

                if (MediaFoundationInterop.Failed(result))
                {
                    lock (_stateLock)
                    {
                        _status = $"Texture-native ReadSample failed: 0x{result:X8}";
                    }
                    break;
                }

                if ((streamFlags & MediaFoundationInterop.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    lock (_stateLock)
                    {
                        _status = "Texture-native camera stream ended.";
                    }
                    break;
                }

                if (sampleObject is not IMFSample sample)
                {
                    MediaFoundationInterop.ReleaseComObject(sampleObject);
                    continue;
                }

                try
                {
                    WriteRecordingSample(sample);
                }
                finally
                {
                    MediaFoundationInterop.ReleaseComObject(sampleObject);
                }
            }
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                _status = ex.Message;
            }
        }
    }

    private void WriteRecordingSample(IMFSample sample)
    {
        if (_processedRecorder is not null)
        {
            if (!TryCreateBgraFrame(sample, out var bgraBytes))
            {
                return;
            }

            if (_recordingDenoiseApplied)
            {
                lock (_processedDenoiseLock)
                {
                    VideoFrameDenoiser.ApplyTemporalDenoise(bgraBytes, _recordingDenoiseStrength, ref _previousProcessedDenoiseFrame);
                }
            }

            if (_recordingColorSettings.HasVisibleAdjustments)
            {
                VideoFrameColorProcessor.Apply(bgraBytes, _recordingColorSettings);
            }

            _processedRecorder.WriteFrame(bgraBytes);
        }
        else if (_recorder is not null)
        {
            MediaFoundationInterop.ThrowIfFailed(sample.SetSampleTime(_nextSampleTime));
            MediaFoundationInterop.ThrowIfFailed(sample.SetSampleDuration(_sampleDuration));
            _recorder.WriteSample(sample);
        }

        _nextSampleTime += _sampleDuration;
    }

    private bool TryCreateBgraFrame(IMFSample sample, out byte[] bgraBytes)
    {
        bgraBytes = [];
        IMFMediaBuffer? buffer = null;
        try
        {
            var bufferResult = sample.GetBufferByIndex(0, out buffer);
            if (MediaFoundationInterop.Failed(bufferResult) || buffer is null)
            {
                return false;
            }

            if (_subtype != MediaFoundationGuids.MFVideoFormat_NV12)
            {
                return false;
            }

            var result = buffer.Lock(out var source, out _, out var currentLength);
            if (MediaFoundationInterop.Failed(result) || source == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var converted = Nv12FrameConverter.ConvertToBgra(source, currentLength, _width, _height, out _);
                if (converted is null)
                {
                    return false;
                }

                bgraBytes = converted;
                return true;
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(buffer);
        }
    }

    private TextureNativeRecordingResult CreateResult(bool success, string status)
    {
        var bytes = System.IO.File.Exists(Path) ? new System.IO.FileInfo(Path).Length : 0;
        return new TextureNativeRecordingResult(
            success,
            Path,
            SamplesWritten,
            bytes,
            _deviceManager.ModeName,
            _processedRecorder is not null ? "rgb32" : MediaFoundationInterop.FormatSubtype(_subtype),
            _width,
            _height,
            _fps,
            status,
            _recordingPipeline,
            _recordingDenoiseApplied,
            _recordingMatchesPreviewDenoise,
            _recordingColorPolishApplied,
            _recordingMatchesPreviewColor);
    }

    private static (int Width, int Height, double Fps, Guid Subtype) ReadCurrentFormat(
        IMFSourceReader reader,
        CameraVideoMode? requestedMode)
    {
        var width = requestedMode?.Width ?? 1280;
        var height = requestedMode?.Height ?? 720;
        var fps = requestedMode?.FramesPerSecond ?? 30d;
        var subtype = MediaFoundationGuids.MFVideoFormat_NV12;

        var result = reader.GetCurrentMediaType(
            MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
            out var currentType);
        if (MediaFoundationInterop.Failed(result))
        {
            return (width, height, fps, subtype);
        }

        try
        {
            if (MediaFoundationInterop.TryGetFrameSize(currentType, out var activeWidth, out var activeHeight))
            {
                width = activeWidth;
                height = activeHeight;
            }

            if (MediaFoundationInterop.TryGetFrameRate(currentType, out var activeFps))
            {
                fps = activeFps;
            }

            currentType.GetGUID(MediaFoundationGuids.MF_MT_SUBTYPE, out subtype);
            return (width, height, fps, subtype);
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(currentType);
        }
    }
}

internal sealed class MediaFoundationTextureVideoRecorder : IDisposable
{
    private readonly object _lock = new();
    private readonly IMFDXGIDeviceManager _deviceManager;
    private IMFSinkWriter? _writer;
    private int _streamIndex;
    private bool _isFinalized;

    public MediaFoundationTextureVideoRecorder(
        string path,
        int width,
        int height,
        double framesPerSecond,
        Guid inputSubtype,
        IMFDXGIDeviceManager deviceManager)
    {
        Path = path;
        Width = width;
        Height = height;
        FramesPerSecond = Math.Clamp(framesPerSecond, 1d, 120d);
        InputSubtype = inputSubtype;
        _deviceManager = deviceManager;

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
        InitializeWriter();
    }

    public string Path { get; }

    public int Width { get; }

    public int Height { get; }

    public double FramesPerSecond { get; }

    public Guid InputSubtype { get; }

    public int SamplesWritten { get; private set; }

    public void WriteSample(IMFSample sample)
    {
        lock (_lock)
        {
            if (_writer is null || _isFinalized)
            {
                return;
            }

            MediaFoundationInterop.ThrowIfFailed(_writer.WriteSample(_streamIndex, sample));
            SamplesWritten++;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_writer is null || _isFinalized)
            {
                return;
            }

            try
            {
                _isFinalized = true;
                var result = _writer.Finalize_();
                if (MediaFoundationInterop.Failed(result) && SamplesWritten > 0)
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
    }

    public void Dispose()
    {
        Stop();
    }

    private void InitializeWriter()
    {
        MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateAttributes(out var writerAttributes, 2));
        try
        {
            MediaFoundationInterop.ThrowIfFailed(writerAttributes.SetUINT32(
                MediaFoundationGuids.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS,
                1));
            MediaFoundationInterop.ThrowIfFailed(writerAttributes.SetUnknown(
                MediaFoundationGuids.MF_SOURCE_READER_D3D_MANAGER,
                _deviceManager));
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
            ConfigureVideoType(inputType, InputSubtype, bitrate: null, includePixelAspect: true);
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
    }

    private int EstimateBitrate()
    {
        var megapixels = Width * Height / 1_000_000d;
        var frameRateFactor = FramesPerSecond / 30d;
        return (int)Math.Round(Math.Clamp(megapixels * frameRateFactor * 5_500_000d, 8_000_000d, 64_000_000d));
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
