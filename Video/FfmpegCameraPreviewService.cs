using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;

namespace EpisodeMonitor.Video;

public sealed class FfmpegCameraPreviewService : IDisposable
{
    private readonly string? _ffmpegPath = FfmpegLocator.FindFfmpeg();
    private Process? _process;
    private CancellationTokenSource? _cancellation;

    public event EventHandler<BitmapSource>? FrameAvailable;
    public event EventHandler<string>? StatusChanged;

    public bool IsAvailable => _ffmpegPath is not null;

    public string? FfmpegPath => _ffmpegPath;

    public bool DenoiseEnabled { get; set; }

    public double DenoiseStrength { get; set; } = 2d;

    public bool Start(string cameraName, CameraVideoMode? mode)
    {
        Stop();

        if (_ffmpegPath is null)
        {
            StatusChanged?.Invoke(this, "FFmpeg was not found on this computer");
            return false;
        }

        _cancellation = new CancellationTokenSource();

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
        startInfo.ArgumentList.Add("64M");
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
            _process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
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
        _ = Task.Run(() => WatchExitAsync(_process, _cancellation.Token));
        StatusChanged?.Invoke(this, $"Starting preview: {cameraName}");
        return true;
    }

    private string CreatePreviewFilter(CameraVideoMode? mode)
    {
        var width = mode?.Width ?? 1280;
        var height = mode?.Height ?? 720;
        var sourceFps = mode?.FramesPerSecond ?? 30d;
        var filters = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"fps={sourceFps:0.###}"),
            $"scale={width}:{height}"
        };

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

    private static void AddFormatArguments(ProcessStartInfo startInfo, string format)
    {
        if (IsPixelFormat(format))
        {
            startInfo.ArgumentList.Add("-pixel_format");
            startInfo.ArgumentList.Add(format);
            return;
        }

        startInfo.ArgumentList.Add("-vcodec");
        startInfo.ArgumentList.Add(format);
    }

    private static bool IsPixelFormat(string format)
    {
        return format.Equals("yuyv422", StringComparison.OrdinalIgnoreCase)
            || format.Equals("uyvy422", StringComparison.OrdinalIgnoreCase)
            || format.Equals("nv12", StringComparison.OrdinalIgnoreCase)
            || format.Equals("rgb24", StringComparison.OrdinalIgnoreCase)
            || format.Equals("bgr24", StringComparison.OrdinalIgnoreCase);
    }

    public void Stop()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;

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
                    var status = SimplifyStatusLine(line);
                    if (status is not null)
                    {
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
                StatusChanged?.Invoke(this, $"Camera preview stopped with FFmpeg exit code {process.ExitCode}");
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
            || line.Contains("Unregistered window class", StringComparison.OrdinalIgnoreCase))
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
}

