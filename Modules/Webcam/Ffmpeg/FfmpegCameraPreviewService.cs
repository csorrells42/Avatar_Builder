using AvatarBuilder.Modules.Webcam.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Webcam.Ffmpeg;

public sealed class FfmpegCameraPreviewService : ICameraPreviewService
{
    private readonly string? _ffmpegPath = FfmpegLocator.FindFfmpeg();
    private readonly object _errorLock = new();
    private readonly List<string> _recentErrors = [];
    private Process? _process;
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource<bool>? _firstFrameSignal;

    public event EventHandler<BitmapSource>? FrameAvailable;
    public event EventHandler<CameraFrame>? CameraFrameAvailable;
    public event EventHandler<string>? StatusChanged;

    public bool IsAvailable => _ffmpegPath is not null;

    public string? FfmpegPath => _ffmpegPath;

    public bool DenoiseEnabled { get; set; }

    public double DenoiseStrength { get; set; } = 2d;

    public int MaxOutputWidth { get; set; } = 960;

    public double MaxOutputFramesPerSecond { get; set; } = 15d;

    public async Task<bool> StartAsync(CameraDevice camera, CameraVideoMode? mode, CancellationToken cancellationToken = default)
    {
        Stop();
        ClearRecentErrors();

        if (_ffmpegPath is null)
        {
            StatusChanged?.Invoke(this, "FFmpeg was not found on this computer");
            return false;
        }

        var cameraName = camera.Name;
        _cancellation = new CancellationTokenSource();
        _firstFrameSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("warning");
        startInfo.ArgumentList.Add("-fflags");
        startInfo.ArgumentList.Add("nobuffer");
        startInfo.ArgumentList.Add("-flags");
        startInfo.ArgumentList.Add("low_delay");
        startInfo.ArgumentList.Add("-rtbufsize");
        startInfo.ArgumentList.Add($"{GetRealtimeBufferMegabytes(mode)}M");
        if (mode is { IsAuto: false })
        {
            if (!string.IsNullOrWhiteSpace(mode.InputFormat))
            {
                AddFormatArguments(startInfo, mode.InputFormat);
            }

            if (mode.Width is int width && mode.Height is int height)
            {
                startInfo.ArgumentList.Add("-video_size");
                startInfo.ArgumentList.Add($"{width}x{height}");
            }

            if (mode.FramesPerSecond is double fps)
            {
                startInfo.ArgumentList.Add("-framerate");
                startInfo.ArgumentList.Add($"{fps:0.###}");
            }
        }

        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("dshow");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add($"video={cameraName}");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add(CreatePreviewFilter(mode));
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("image2pipe");
        startInfo.ArgumentList.Add("-vcodec");
        startInfo.ArgumentList.Add("mjpeg");
        startInfo.ArgumentList.Add("-q:v");
        startInfo.ArgumentList.Add("8");
        startInfo.ArgumentList.Add("pipe:1");

        try
        {
            LogCameraLine($"Starting FFmpeg preview for {cameraName} / {mode?.Label ?? "Auto"}");
            LogCameraLine($"Preview fidelity: max width {MaxOutputWidth}px, max {MaxOutputFramesPerSecond:0.###} fps");
            LogCameraLine($"Arguments: {string.Join(" ", startInfo.ArgumentList)}");
            _process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            LogCameraLine($"Could not start camera preview: {ex}");
            StatusChanged?.Invoke(this, $"Could not start camera preview: {ex.Message}");
            return false;
        }

        if (_process is null)
        {
            StatusChanged?.Invoke(this, "Could not start camera preview");
            return false;
        }

        _ = Task.Run(() => ReadFramesAsync(_process, _cancellation.Token));
        _ = Task.Run(() => ReadErrorsAsync(_process, _cancellation.Token));
        var exitTask = WatchExitAsync(_process, _cancellation.Token);
        _ = Task.Run(() => exitTask);
        StatusChanged?.Invoke(this, $"Starting preview: {cameraName}");

        try
        {
            var firstFrameTask = _firstFrameSignal.Task;
            var readinessTask = await Task.WhenAny(firstFrameTask, exitTask, Task.Delay(1500, cancellationToken));
            if (readinessTask == firstFrameTask)
            {
                return true;
            }

            if (readinessTask == exitTask && _process.HasExited)
            {
                var error = GetRecentErrorSummary();
                var message = string.IsNullOrWhiteSpace(error)
                    ? $"Camera preview stopped with FFmpeg exit code {_process.ExitCode}"
                    : $"Camera preview stopped with FFmpeg exit code {_process.ExitCode}: {error}";
                LogCameraLine(message);
                StatusChanged?.Invoke(this, message);
                Stop();
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            Stop();
            return false;
        }

        return true;
    }

    private string CreatePreviewFilter(CameraVideoMode? mode)
    {
        var width = mode?.Width ?? 1280;
        var height = mode?.Height ?? 720;
        var sourceFps = mode?.FramesPerSecond ?? 30d;
        var targetFps = Math.Clamp(Math.Min(sourceFps, MaxOutputFramesPerSecond), 1d, 60d);
        var maximumWidth = Math.Clamp(MaxOutputWidth, 320, 3840);
        var targetWidth = Math.Min(width, maximumWidth);
        var filters = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"fps={targetFps:0.###}")
        };

        if (targetWidth < width)
        {
            var scale = targetWidth / (double)Math.Max(1, width);
            var targetHeight = Math.Max(2, (int)Math.Round(height * scale / 2d) * 2);
            filters.Add($"scale={targetWidth}:{targetHeight}");
        }

        if (DenoiseEnabled)
        {
            var strength = Math.Clamp(DenoiseStrength, 0.5d, 8d);
            var chromaStrength = Math.Max(0.25d, strength * 0.7d);
            filters.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"hqdn3d={strength:0.##}:{chromaStrength:0.##}:{strength * 1.5d:0.##}:{chromaStrength * 1.5d:0.##}"));
        }

        return string.Join(",", filters);
    }

    private int GetRealtimeBufferMegabytes(CameraVideoMode? mode)
    {
        var requestedWidth = mode?.Width ?? MaxOutputWidth;
        var requestedHeight = mode?.Height ?? 720;
        var requestedPixels = requestedWidth * requestedHeight;
        var outputPixels = Math.Min(requestedWidth, Math.Clamp(MaxOutputWidth, 320, 3840))
            * Math.Min(requestedHeight, 2160);

        if (requestedPixels >= 3840 * 2160 || outputPixels >= 3840 * 2160)
        {
            return 256;
        }

        if (requestedPixels >= 1920 * 1080 || outputPixels >= 1920 * 1080)
        {
            return 96;
        }

        return 32;
    }

    private static void AddFormatArguments(ProcessStartInfo startInfo, string format)
    {
        format = NormalizeInputFormat(format);
        if (IsPixelFormat(format))
        {
            startInfo.ArgumentList.Add("-pixel_format");
            startInfo.ArgumentList.Add(format);
            return;
        }

        startInfo.ArgumentList.Add("-vcodec");
        startInfo.ArgumentList.Add(format);
    }

    private static string NormalizeInputFormat(string format)
    {
        return format.ToLowerInvariant() switch
        {
            "mjpg" => "mjpeg",
            "yuy2" => "yuyv422",
            "uyvy" => "uyvy422",
            _ => format
        };
    }

    private static bool IsPixelFormat(string format)
    {
        return format.Equals("yuyv422", StringComparison.OrdinalIgnoreCase)
            || format.Equals("uyvy422", StringComparison.OrdinalIgnoreCase)
            || format.Equals("nv12", StringComparison.OrdinalIgnoreCase)
            || format.Equals("rgb32", StringComparison.OrdinalIgnoreCase)
            || format.Equals("rgb24", StringComparison.OrdinalIgnoreCase)
            || format.Equals("bgr24", StringComparison.OrdinalIgnoreCase);
    }

    public void Stop()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        _firstFrameSignal = null;

        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(1500);
            }
        }
        catch
        {
            // The process may already be gone.
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task ReadErrorsAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    AddRecentError(line);
                    var status = SimplifyStatusLine(line);
                    if (status is not null)
                    {
                        LogCameraLine(status);
                        StatusChanged?.Invoke(this, status);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, ex.Message);
        }
    }

    private async Task WatchExitAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            if (!cancellationToken.IsCancellationRequested && process.ExitCode != 0)
            {
                var error = GetRecentErrorSummary();
                var message = string.IsNullOrWhiteSpace(error)
                    ? $"Camera preview stopped with FFmpeg exit code {process.ExitCode}"
                    : $"Camera preview stopped with FFmpeg exit code {process.ExitCode}: {error}";
                LogCameraLine(message);
                StatusChanged?.Invoke(this, message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, ex.Message);
        }
    }

    private static string? SimplifyStatusLine(string line)
    {
        if (line.Contains("[INFO]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Failed to open settings hive", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Failed to open NBX hive", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Creating WndMsg Listener Window", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Destroying WndMsg Listener Window", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Unregistered window class", StringComparison.OrdinalIgnoreCase)
            || line.Contains("deprecated pixel format", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return line;
    }

    private async Task ReadFramesAsync(Process process, CancellationToken cancellationToken)
    {
        var stream = process.StandardOutput.BaseStream;
        var buffer = new byte[8192];
        var pending = new List<byte>(512 * 1024);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                for (var i = 0; i < read; i++)
                {
                    pending.Add(buffer[i]);
                }

                ExtractFrames(pending);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, ex.Message);
        }
    }

    private void ExtractFrames(List<byte> pending)
    {
        while (true)
        {
            var start = FindMarker(pending, 0, 0xFF, 0xD8);
            if (start < 0)
            {
                pending.Clear();
                return;
            }

            if (start > 0)
            {
                pending.RemoveRange(0, start);
            }

            var end = FindMarker(pending, 2, 0xFF, 0xD9);
            if (end < 0)
            {
                return;
            }

            var frameLength = end + 2;
            var frameBytes = pending.Take(frameLength).ToArray();
            pending.RemoveRange(0, frameLength);

            var bitmap = CreateBitmap(frameBytes);
            if (bitmap is not null)
            {
                _firstFrameSignal?.TrySetResult(true);
                var cameraFrame = CreateCameraFrame(bitmap);
                if (cameraFrame is not null)
                {
                    CameraFrameAvailable?.Invoke(this, cameraFrame);
                }

                FrameAvailable?.Invoke(this, bitmap);
            }
        }
    }

    private static int FindMarker(List<byte> bytes, int startIndex, byte first, byte second)
    {
        for (var i = startIndex; i < bytes.Count - 1; i++)
        {
            if (bytes[i] == first && bytes[i + 1] == second)
            {
                return i;
            }
        }

        return -1;
    }

    private static BitmapSource? CreateBitmap(byte[] bytes)
    {
        try
        {
            using var memory = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static CameraFrame? CreateCameraFrame(BitmapSource bitmap)
    {
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            return null;
        }

        BitmapSource source = bitmap;
        if (source.Format != PixelFormats.Bgra32)
        {
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        }

        var stride = source.PixelWidth * 4;
        var bytes = new byte[stride * source.PixelHeight];
        source.CopyPixels(bytes, stride, 0);
        return new CameraFrame(bytes, source.PixelWidth, source.PixelHeight, stride);
    }

    private void ClearRecentErrors()
    {
        lock (_errorLock)
        {
            _recentErrors.Clear();
        }
    }

    private void AddRecentError(string line)
    {
        lock (_errorLock)
        {
            _recentErrors.Add(line);
            if (_recentErrors.Count > 12)
            {
                _recentErrors.RemoveAt(0);
            }
        }
    }

    private string GetRecentErrorSummary()
    {
        lock (_errorLock)
        {
            return string.Join(" | ", _recentErrors
                .Select(SimplifyStatusLine)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .TakeLast(4));
        }
    }

    private static void LogCameraLine(string line)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "AvatarBuilder-camera.log");
            File.AppendAllText(path, $"{DateTime.Now:O} {line}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}

