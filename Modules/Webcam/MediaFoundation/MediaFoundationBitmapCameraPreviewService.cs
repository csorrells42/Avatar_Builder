using AvatarBuilder.Modules.Webcam.Common;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

public sealed class MediaFoundationBitmapCameraPreviewService : ICameraPreviewService
{
    private static readonly TimeSpan CaptureStopTimeout = TimeSpan.FromSeconds(3);
    private MediaFoundationCameraDeviceFactory.MediaFoundationScope? _mediaFoundationScope;
    private IMFSourceReader? _reader;
    private object? _mediaSource;
    private CancellationTokenSource? _cancellation;
    private Task? _captureTask;
    private readonly object _analysisFrameLock = new();
    private CameraFrame? _pendingAnalysisFrame;
    private DateTime _lastFrameEmittedAtUtc = DateTime.MinValue;
    private int _activeWidth = 1280;
    private int _activeHeight = 720;
    private double _activeFramesPerSecond = 30d;
    private Guid _activeSubtype = MediaFoundationGuids.MFVideoFormat_RGB32;
    private int _activeStride;
    private int _analysisFrameWorkerQueued;

    public event EventHandler<BitmapSource>? FrameAvailable;

    public event EventHandler<CameraFrame>? CameraFrameAvailable;

    public event EventHandler<string>? StatusChanged;

    public bool IsAvailable => OperatingSystem.IsWindows();

    public int MaxOutputWidth { get; set; } = 960;

    public double MaxOutputFramesPerSecond { get; set; } = 15d;

    public Task<bool> StartAsync(CameraDevice camera, CameraVideoMode? mode, CancellationToken cancellationToken = default)
    {
        Stop();
        if (!OperatingSystem.IsWindows())
        {
            NotifyStatusChanged("Media Foundation camera capture requires Windows.");
            return Task.FromResult(false);
        }

        return StartCoreAsync(camera, mode, cancellationToken);
    }

    public void Stop()
    {
        var captureTask = _captureTask;
        _cancellation?.Cancel();
        TryFlushSourceReader();

        try
        {
            captureTask?.Wait(CaptureStopTimeout);
        }
        catch
        {
        }

        _captureTask = null;
        ResetAnalysisFramePump();
        _cancellation?.Dispose();
        _cancellation = null;
        CleanupPreviewObjects();
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task<bool> StartCoreAsync(CameraDevice camera, CameraVideoMode? mode, CancellationToken cancellationToken)
    {
        try
        {
            _cancellation = new CancellationTokenSource();
            var startup = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstFrame = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _captureTask = Task.Run(
                () => CaptureLoop(camera, mode, _cancellation.Token, startup, firstFrame),
                CancellationToken.None);

            var startupReady = await Task.WhenAny(startup.Task, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
            if (startupReady != startup.Task)
            {
                Stop();
                NotifyStatusChanged($"Could not start Media Foundation preview: timed out opening {camera.Name}.");
                return false;
            }

            var startupError = await startup.Task;
            if (!string.IsNullOrWhiteSpace(startupError))
            {
                Stop();
                NotifyStatusChanged($"Could not start Media Foundation preview: {startupError}");
                return false;
            }

            var frameReady = await Task.WhenAny(firstFrame.Task, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
            if (frameReady == firstFrame.Task && await firstFrame.Task)
            {
                return true;
            }

            Stop();
            NotifyStatusChanged($"Could not start Media Foundation preview: no frames arrived from {camera.Name}.");
            return false;
        }
        catch (OperationCanceledException)
        {
            Stop();
            return false;
        }
        catch (Exception ex)
        {
            Stop();
            NotifyStatusChanged($"Could not start Media Foundation preview: {ex.Message}");
            return false;
        }
    }

    private void CaptureLoop(
        CameraDevice camera,
        CameraVideoMode? mode,
        CancellationToken cancellationToken,
        TaskCompletionSource<string?> startup,
        TaskCompletionSource<bool> firstFrame)
    {
        IMFSourceReader? reader = null;
        object? mediaSource = null;
        MediaFoundationCameraDeviceFactory.MediaFoundationScope? mediaFoundationScope = null;

        try
        {
            mediaFoundationScope = MediaFoundationCameraDeviceFactory.Startup();
            reader = MediaFoundationCameraDeviceFactory.CreatePreviewReader(camera, mode, out mediaSource);
            _mediaFoundationScope = mediaFoundationScope;
            _reader = reader;
            _mediaSource = mediaSource;
            UpdateActiveFormat(reader, mode);

            NotifyStatusChanged($"Media Foundation preview format: {_activeWidth}x{_activeHeight}@{_activeFramesPerSecond:0.###} {MediaFoundationInterop.FormatSubtype(_activeSubtype)}.");
            startup.TrySetResult(null);

            while (!cancellationToken.IsCancellationRequested)
            {
                var result = reader.ReadSample(
                    MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                    0,
                    out _,
                    out var streamFlags,
                    out _,
                    out var sampleObject);

                if (MediaFoundationInterop.Failed(result))
                {
                    NotifyStatusChanged($"Camera read failed: 0x{result:X8}");
                    Thread.Sleep(50);
                    continue;
                }

                if ((streamFlags & MediaFoundationInterop.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    NotifyStatusChanged("Camera preview ended.");
                    break;
                }

                if (sampleObject is not IMFSample sample)
                {
                    MediaFoundationInterop.ReleaseComObject(sampleObject);
                    continue;
                }

                try
                {
                    if (TryReadFrame(sample, _activeWidth, _activeHeight, _activeSubtype, _activeStride, out var frame))
                    {
                        firstFrame.TrySetResult(true);
                        NotifyCameraFrameAvailable(frame);

                        if (CanEmitFrame())
                        {
                            MarkFrameEmitted();
                            QueueAnalysisFrame(frame);
                        }
                    }
                }
                finally
                {
                    MediaFoundationInterop.ReleaseComObject(sampleObject);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            startup.TrySetResult(ex.Message);
            firstFrame.TrySetResult(false);
            NotifyStatusChanged(ex.Message);
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(reader);
            MediaFoundationInterop.ReleaseComObject(mediaSource);
            mediaFoundationScope?.Dispose();

            if (ReferenceEquals(_reader, reader))
            {
                _reader = null;
            }

            if (ReferenceEquals(_mediaSource, mediaSource))
            {
                _mediaSource = null;
            }

            if (ReferenceEquals(_mediaFoundationScope, mediaFoundationScope))
            {
                _mediaFoundationScope = null;
            }

            startup.TrySetResult("Capture loop ended before startup completed.");
            firstFrame.TrySetResult(false);
        }
    }

    private bool CanEmitFrame()
    {
        var now = DateTime.UtcNow;
        var maxFramesPerSecond = Math.Clamp(MaxOutputFramesPerSecond, 1d, 60d);
        return (now - _lastFrameEmittedAtUtc).TotalSeconds >= 1d / maxFramesPerSecond;
    }

    private void MarkFrameEmitted()
    {
        _lastFrameEmittedAtUtc = DateTime.UtcNow;
    }

    private void QueueAnalysisFrame(CameraFrame frame)
    {
        lock (_analysisFrameLock)
        {
            _pendingAnalysisFrame = frame;
        }

        if (Interlocked.Exchange(ref _analysisFrameWorkerQueued, 1) == 0)
        {
            _ = Task.Run(ProcessPendingAnalysisFrames);
        }
    }

    private void ProcessPendingAnalysisFrames()
    {
        while (_cancellation?.IsCancellationRequested != true)
        {
            CameraFrame? frame;
            lock (_analysisFrameLock)
            {
                frame = _pendingAnalysisFrame;
                _pendingAnalysisFrame = null;
            }

            if (frame is null)
            {
                break;
            }

            if (TryCreateBitmap(frame, out var bitmap))
            {
                NotifyFrameAvailable(bitmap);
            }
        }

        Interlocked.Exchange(ref _analysisFrameWorkerQueued, 0);
        lock (_analysisFrameLock)
        {
            if (_pendingAnalysisFrame is not null
                && Interlocked.Exchange(ref _analysisFrameWorkerQueued, 1) == 0)
            {
                _ = Task.Run(ProcessPendingAnalysisFrames);
            }
        }
    }

    private void ResetAnalysisFramePump()
    {
        lock (_analysisFrameLock)
        {
            _pendingAnalysisFrame = null;
        }

        Interlocked.Exchange(ref _analysisFrameWorkerQueued, 0);
    }

    private void NotifyCameraFrameAvailable(CameraFrame frame)
    {
        var handlers = CameraFrameAvailable;
        if (handlers is null)
        {
            return;
        }

        foreach (var callback in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<CameraFrame>)callback)(this, frame);
            }
            catch (Exception ex)
            {
                NotifyStatusChanged($"Camera frame observer failed: {ex.Message}");
            }
        }
    }

    private void NotifyFrameAvailable(BitmapSource bitmap)
    {
        var handlers = FrameAvailable;
        if (handlers is null)
        {
            return;
        }

        foreach (var callback in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<BitmapSource>)callback)(this, bitmap);
            }
            catch (Exception ex)
            {
                NotifyStatusChanged($"Preview frame observer failed: {ex.Message}");
            }
        }
    }

    private void NotifyStatusChanged(string status)
    {
        var handlers = StatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (var callback in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<string>)callback)(this, status);
            }
            catch
            {
                // Status observers cannot be allowed to terminate capture or analysis workers.
            }
        }
    }

    private void UpdateActiveFormat(IMFSourceReader reader, CameraVideoMode? requestedMode)
    {
        var result = reader.GetCurrentMediaType(
            MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
            out var currentType);
        if (MediaFoundationInterop.Failed(result))
        {
            _activeWidth = requestedMode?.Width ?? 1280;
            _activeHeight = requestedMode?.Height ?? 720;
            _activeFramesPerSecond = requestedMode?.FramesPerSecond ?? 30d;
            _activeSubtype = MediaFoundationGuids.MFVideoFormat_RGB32;
            _activeStride = _activeWidth * 4;
            return;
        }

        try
        {
            if (MediaFoundationInterop.TryGetFrameSize(currentType, out var width, out var height))
            {
                _activeWidth = width;
                _activeHeight = height;
            }

            if (MediaFoundationInterop.TryGetFrameRate(currentType, out var fps))
            {
                _activeFramesPerSecond = fps;
            }

            if (!MediaFoundationInterop.Failed(currentType.GetGUID(MediaFoundationGuids.MF_MT_SUBTYPE, out var subtype)))
            {
                _activeSubtype = subtype;
            }

            if (!MediaFoundationInterop.Failed(currentType.GetUINT32(MediaFoundationGuids.MF_MT_DEFAULT_STRIDE, out var stride)))
            {
                _activeStride = stride;
            }
            else
            {
                _activeStride = _activeSubtype == MediaFoundationGuids.MFVideoFormat_RGB32
                    ? _activeWidth * 4
                    : _activeWidth;
            }
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(currentType);
        }
    }

    private static bool TryReadFrame(
        IMFSample sample,
        int width,
        int height,
        Guid subtype,
        int stride,
        out CameraFrame frame)
    {
        frame = new CameraFrame([], 0, 0, 0);
        IMFMediaBuffer? buffer = null;
        try
        {
            var result = sample.GetBufferByIndex(0, out buffer);
            if (MediaFoundationInterop.Failed(result))
            {
                MediaFoundationInterop.ThrowIfFailed(sample.ConvertToContiguousBuffer(out buffer));
            }

            MediaFoundationInterop.ThrowIfFailed(buffer.Lock(out var source, out _, out var currentLength));
            try
            {
                if (subtype == MediaFoundationGuids.MFVideoFormat_NV12)
                {
                    var nv12Stride = stride != 0 ? Math.Abs(stride) : width;
                    var uvHeight = (height + 1) / 2;
                    var expectedNv12Bytes = nv12Stride * height + nv12Stride * uvHeight;
                    if (currentLength < expectedNv12Bytes)
                    {
                        return false;
                    }

                    var nv12Bytes = new byte[expectedNv12Bytes];
                    Marshal.Copy(source, nv12Bytes, 0, expectedNv12Bytes);
                    frame = new CameraFrame([], width, height, 0, nv12Bytes, nv12Stride, "nv12");
                    return true;
                }

                var bgraStride = stride != 0 ? Math.Abs(stride) : width * 4;
                var expectedBytes = bgraStride * height;
                if (currentLength < expectedBytes)
                {
                    return false;
                }

                var bytes = new byte[expectedBytes];
                Marshal.Copy(source, bytes, 0, expectedBytes);
                frame = new CameraFrame(bytes, width, height, bgraStride);
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

    private bool TryCreateBitmap(CameraFrame frame, out BitmapSource bitmap)
    {
        bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            return false;
        }

        var maximumWidth = Math.Clamp(MaxOutputWidth, 320, 3840);
        if (!frame.HasBgra)
        {
            if (!frame.HasNv12)
            {
                return false;
            }

            frame = CreateBgraFrameFromNv12(frame, maximumWidth);
        }

        var source = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            frame.BgraBytes,
            frame.Stride);
        source.Freeze();

        if (frame.Width <= maximumWidth)
        {
            bitmap = source;
            return true;
        }

        var scale = maximumWidth / (double)frame.Width;
        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        transformed.Freeze();
        bitmap = transformed;
        return true;
    }

    private static CameraFrame CreateBgraFrameFromNv12(CameraFrame frame, int maximumWidth)
    {
        var nv12Bytes = frame.Nv12Bytes;
        if (nv12Bytes is null)
        {
            return frame;
        }

        var width = frame.Width;
        var height = frame.Height;
        var nv12Stride = frame.Nv12Stride;
        var outputWidth = width;
        var outputHeight = height;
        if (maximumWidth > 0 && width > maximumWidth)
        {
            var scale = maximumWidth / (double)width;
            outputWidth = maximumWidth;
            outputHeight = Math.Max(1, (int)Math.Round(height * scale));
        }

        var bgraStride = outputWidth * 4;
        var bgraBytes = new byte[bgraStride * outputHeight];
        var uvOffset = nv12Stride * height;

        for (var y = 0; y < outputHeight; y++)
        {
            var sourceY = Math.Min(height - 1, (int)((y + 0.5d) * height / outputHeight));
            var yRow = sourceY * nv12Stride;
            var uvRow = uvOffset + sourceY / 2 * nv12Stride;
            var bgraRow = y * bgraStride;

            for (var x = 0; x < outputWidth; x++)
            {
                var sourceX = Math.Min(width - 1, (int)((x + 0.5d) * width / outputWidth));
                var luma = (nv12Bytes[yRow + sourceX] - 16) * (255d / 219d);
                var uvIndex = uvRow + (sourceX & ~1);
                var chromaBlue = nv12Bytes[uvIndex] - 128d;
                var chromaRed = nv12Bytes[uvIndex + 1] - 128d;
                var red = luma + 1.5748d * chromaRed;
                var green = luma - 0.1873d * chromaBlue - 0.4681d * chromaRed;
                var blue = luma + 1.8556d * chromaBlue;
                var destination = bgraRow + x * 4;
                bgraBytes[destination] = ClampByte(blue);
                bgraBytes[destination + 1] = ClampByte(green);
                bgraBytes[destination + 2] = ClampByte(red);
                bgraBytes[destination + 3] = 255;
            }
        }

        return new CameraFrame(bgraBytes, outputWidth, outputHeight, bgraStride, null, 0, "nv12-analysis");
    }

    private static byte ClampByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private void CleanupPreviewObjects()
    {
        ResetPreviewState();
        MediaFoundationInterop.ReleaseComObject(_reader);
        MediaFoundationInterop.ReleaseComObject(_mediaSource);
        _reader = null;
        _mediaSource = null;
        _mediaFoundationScope?.Dispose();
        _mediaFoundationScope = null;
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

    private void ResetPreviewState()
    {
        _activeWidth = 1280;
        _activeHeight = 720;
        _activeFramesPerSecond = 30d;
        _activeSubtype = MediaFoundationGuids.MFVideoFormat_RGB32;
        _activeStride = 0;
        _lastFrameEmittedAtUtc = DateTime.MinValue;
    }
}
