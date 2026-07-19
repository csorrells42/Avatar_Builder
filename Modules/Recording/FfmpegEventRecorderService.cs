using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Recording;

public sealed class FfmpegEventRecorderService : IDisposable
{
    private readonly string? _ffmpegPath = FfmpegLocator.FindFfmpeg();
    private readonly object _sync = new();

    private BlockingCollection<byte[]>? _frames;
    private Process? _process;
    private CancellationTokenSource? _cancellation;
    private Task? _writerTask;
    private DateTime _lastQueuedAt = DateTime.MinValue;
    private bool _isRecording;

    public event EventHandler<string>? StatusChanged;

    public bool IsAvailable => _ffmpegPath is not null;

    public string? CurrentPath { get; private set; }

    public bool Start(string outputPath, IEnumerable<byte[]>? preRollFrames = null)
    {
        Stop();

        if (_ffmpegPath is null)
        {
            StatusChanged?.Invoke(this, "FFmpeg was not found. Event video will not be saved.");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        _cancellation = new CancellationTokenSource();
        _frames = new BlockingCollection<byte[]>(1200);

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("warning");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("image2pipe");
        startInfo.ArgumentList.Add("-framerate");
        startInfo.ArgumentList.Add("10");
        startInfo.ArgumentList.Add("-vcodec");
        startInfo.ArgumentList.Add("mjpeg");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("pipe:0");
        startInfo.ArgumentList.Add("-an");
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("libx264");
        startInfo.ArgumentList.Add("-preset");
        startInfo.ArgumentList.Add("veryfast");
        startInfo.ArgumentList.Add("-crf");
        startInfo.ArgumentList.Add("23");
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("yuv420p");
        startInfo.ArgumentList.Add("-movflags");
        startInfo.ArgumentList.Add("+faststart");
        startInfo.ArgumentList.Add(outputPath);

        try
        {
            _process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Could not start event video recording: {ex.Message}");
            return false;
        }

        if (_process is null)
        {
            StatusChanged?.Invoke(this, "Could not start event video recording.");
            return false;
        }

        CurrentPath = outputPath;
        _lastQueuedAt = DateTime.MinValue;
        _isRecording = true;
        if (preRollFrames is not null)
        {
            foreach (var frame in preRollFrames)
            {
                if (!_frames.TryAdd(frame))
                {
                    break;
                }
            }
        }

        _writerTask = Task.Run(() => WriteFramesAsync(_process, _cancellation.Token));
        _ = Task.Run(() => ReadErrorsAsync(_process, _cancellation.Token));
        StatusChanged?.Invoke(this, $"Event video recording: {outputPath}");
        return true;
    }

    public void AddFrame(byte[] jpegFrame)
    {
        if (!_isRecording)
        {
            return;
        }

        var now = DateTime.Now;
        if ((now - _lastQueuedAt).TotalSeconds < 0.1d)
        {
            return;
        }

        _lastQueuedAt = now;
        try
        {
            if (_frames is null || !_frames.TryAdd(jpegFrame))
            {
                StatusChanged?.Invoke(this, "Event video encoder is behind; dropping a frame.");
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Stop()
    {
        Process? process;
        Task? writerTask;
        lock (_sync)
        {
            if (!_isRecording && _process is null)
            {
                return;
            }

            _isRecording = false;
            _frames?.CompleteAdding();
            _cancellation?.CancelAfter(TimeSpan.FromSeconds(10));
            process = _process;
            writerTask = _writerTask;
            _process = null;
            _writerTask = null;
        }

        try
        {
            writerTask?.Wait(TimeSpan.FromSeconds(12));
        }
        catch
        {
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.StandardInput.Close();
                    process.WaitForExit(5000);
                }

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        _cancellation?.Dispose();
        _cancellation = null;
        CurrentPath = null;
        _frames?.Dispose();
        _frames = null;
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task WriteFramesAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            if (_frames is null)
            {
                return;
            }

            foreach (var bytes in _frames.GetConsumingEnumerable(cancellationToken))
            {
                await process.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken);
                await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Event video recording stopped: {ex.Message}");
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch
            {
            }
        }
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
                    StatusChanged?.Invoke(this, line);
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

}
