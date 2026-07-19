using AvatarBuilder.Modules.Webcam.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AvatarBuilder.Modules.Infrastructure;
using AvatarBuilder.Modules.Webcam.MediaFoundation;

namespace AvatarBuilder.Modules.Webcam.Ffmpeg;

public sealed class FfmpegCameraModeService
{
    private static readonly double[] CommonFrameRates = [24d, 25d, 30d, 50d, 60d];

    private static readonly Regex DirectShowRangeModeRegex = new(
        @"(?:(?<kind>vcodec|pixel_format)=(?<format>[^\s]+).*?)?min\s+s=(?<minWidth>\d+)x(?<minHeight>\d+)\s+fps=(?<minFps>\d+(?:\.\d+)?).*?max\s+s=(?<maxWidth>\d+)x(?<maxHeight>\d+)\s+fps=(?<maxFps>\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DirectShowExactModeRegex = new(
        @"(?:(?<kind>vcodec|pixel_format)=(?<format>[^\s]+).*?)?\bs=(?<width>\d+)x(?<height>\d+).*?fps=(?<fps>\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly string? _ffmpegPath = FfmpegLocator.FindFfmpeg();
    private readonly MediaFoundationCameraModeService _mediaFoundationModeService = new();

    public bool IsAvailable => _ffmpegPath is not null || _mediaFoundationModeService.IsAvailable;

    public async Task<IReadOnlyList<CameraVideoMode>> GetModesAsync(CameraDevice camera, CancellationToken cancellationToken)
    {
        var modes = new List<CameraVideoMode> { CameraVideoMode.Auto };
        if (_mediaFoundationModeService.IsAvailable)
        {
            try
            {
                var mediaFoundationModes = await _mediaFoundationModeService.GetModesAsync(camera, cancellationToken);
                modes.AddRange(mediaFoundationModes.Where(mode => !mode.IsAuto));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // FFmpeg DirectShow probing below is still useful when Media Foundation is unavailable for a device.
            }
        }

        var directShowCamera = camera.DirectShowDeviceOrSelf();
        var cameraName = directShowCamera.Name;
        if (_ffmpegPath is null)
        {
            return SortAndFallback(camera.Name, modes);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-list_options");
        startInfo.ArgumentList.Add("true");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("dshow");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add($"video={cameraName}");

        Process? process = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            process = Process.Start(startInfo);
            if (process is null)
            {
                return SortAndFallback(camera.Name, modes);
            }

            var stderr = await process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            modes.AddRange(ParseModes(stderr));
        }
        catch
        {
            return SortAndFallback(camera.Name, modes);
        }
        finally
        {
            KillProcessIfRunning(process);
            process?.Dispose();
        }

        return SortAndFallback(camera.Name, modes);
    }

    private static IReadOnlyList<CameraVideoMode> SortAndFallback(string cameraName, IReadOnlyList<CameraVideoMode> modes)
    {
        var sortedModes = modes
            .GroupBy(mode => $"{mode.Width}|{mode.Height}|{mode.FramesPerSecond:0.###}")
            .Select(group => group
                .OrderBy(mode => FormatPriority(mode.InputFormat))
                .ThenBy(mode => mode.InputFormat)
                .First())
            .OrderBy(mode => mode.IsAuto ? -1 : 0)
            .ThenByDescending(mode => mode.Width.GetValueOrDefault() * mode.Height.GetValueOrDefault())
            .ThenByDescending(mode => mode.Width.GetValueOrDefault())
            .ThenByDescending(mode => mode.Height.GetValueOrDefault())
            .ThenByDescending(mode => mode.FramesPerSecond.GetValueOrDefault())
            .ToList();

        return sortedModes.Count > 1 ? sortedModes : AddFallbackModes(cameraName, sortedModes);
    }

    private static string CreateModeKey(CameraVideoMode mode)
    {
        return $"{mode.Width}|{mode.Height}|{mode.FramesPerSecond:0.###}";
    }

    private static IEnumerable<CameraVideoMode> ParseModes(string output)
    {
        foreach (var line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("(", StringComparison.Ordinal))
            {
                continue;
            }

            var rangeMatch = DirectShowRangeModeRegex.Match(line);
            if (rangeMatch.Success)
            {
                foreach (var mode in ParseRangeMode(rangeMatch))
                {
                    yield return mode;
                }

                continue;
            }

            var exactMatch = DirectShowExactModeRegex.Match(line);
            if (!exactMatch.Success)
            {
                continue;
            }

            var width = int.Parse(exactMatch.Groups["width"].Value, CultureInfo.InvariantCulture);
            var height = int.Parse(exactMatch.Groups["height"].Value, CultureInfo.InvariantCulture);
            var fps = NormalizeFrameRate(double.Parse(exactMatch.Groups["fps"].Value, CultureInfo.InvariantCulture));
            var format = exactMatch.Groups["format"].Success ? exactMatch.Groups["format"].Value : null;
            yield return CreateMode(width, height, fps, format);
        }
    }

    private static IEnumerable<CameraVideoMode> ParseRangeMode(Match match)
    {
        var minWidth = int.Parse(match.Groups["minWidth"].Value, CultureInfo.InvariantCulture);
        var minHeight = int.Parse(match.Groups["minHeight"].Value, CultureInfo.InvariantCulture);
        var maxWidth = int.Parse(match.Groups["maxWidth"].Value, CultureInfo.InvariantCulture);
        var maxHeight = int.Parse(match.Groups["maxHeight"].Value, CultureInfo.InvariantCulture);
        if (minWidth != maxWidth || minHeight != maxHeight)
        {
            yield break;
        }

        var minFps = double.Parse(match.Groups["minFps"].Value, CultureInfo.InvariantCulture);
        var maxFps = double.Parse(match.Groups["maxFps"].Value, CultureInfo.InvariantCulture);
        var format = match.Groups["format"].Success ? match.Groups["format"].Value : null;

        foreach (var fps in CommonFrameRates.Where(fps => fps >= minFps - 0.01d && fps <= maxFps + 0.25d))
        {
            yield return CreateMode(minWidth, minHeight, fps, format);
        }
    }

    private static CameraVideoMode CreateMode(int width, int height, double fps, string? format)
    {
        var normalizedFormat = NormalizeFormat(format);
        var label = normalizedFormat is null
            ? $"{width}x{height} @ {fps:0.###} fps"
            : $"{width}x{height} @ {fps:0.###} fps ({normalizedFormat.ToUpperInvariant()})";

        return new CameraVideoMode(label, width, height, fps, normalizedFormat);
    }

    private static double NormalizeFrameRate(double frameRate)
    {
        foreach (var commonFrameRate in CommonFrameRates)
        {
            if (Math.Abs(frameRate - commonFrameRate) < 0.25d)
            {
                return commonFrameRate;
            }
        }

        return Math.Round(frameRate, 3);
    }

    private static string? NormalizeFormat(string? format)
    {
        return format?.ToLowerInvariant() switch
        {
            null or "" => null,
            "mjpg" => "mjpeg",
            _ => format.ToLowerInvariant()
        };
    }

    private static int FormatPriority(string? format)
    {
        return format?.ToLowerInvariant() switch
        {
            "mjpeg" => 0,
            "mjpg" => 0,
            null => 1,
            "h264" => 2,
            _ => 3
        };
    }

    private static void KillProcessIfRunning(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1500);
            }
        }
        catch
        {
        }
    }

    private static IReadOnlyList<CameraVideoMode> AddFallbackModes(string cameraName, IReadOnlyList<CameraVideoMode> existingModes)
    {
        if (!cameraName.Contains("insta360", StringComparison.OrdinalIgnoreCase)
            && !cameraName.Contains("link 2", StringComparison.OrdinalIgnoreCase))
        {
            return existingModes;
        }

        var modes = existingModes.ToList();
        var keys = modes.Select(CreateModeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mode in CreateInsta360Link2FallbackModes())
        {
            if (keys.Add(CreateModeKey(mode)))
            {
                modes.Add(mode);
            }
        }

        return modes
            .OrderBy(mode => mode.IsAuto ? -1 : 0)
            .ThenByDescending(mode => mode.Width.GetValueOrDefault() * mode.Height.GetValueOrDefault())
            .ThenByDescending(mode => mode.Width.GetValueOrDefault())
            .ThenByDescending(mode => mode.Height.GetValueOrDefault())
            .ThenByDescending(mode => mode.FramesPerSecond.GetValueOrDefault())
            .ToList();
    }

    private static IEnumerable<CameraVideoMode> CreateInsta360Link2FallbackModes()
    {
        var sizes = new[]
        {
            (Width: 3840, Height: 2160, MaxFps: 30d),
            (Width: 1920, Height: 1440, MaxFps: 60d),
            (Width: 1920, Height: 1080, MaxFps: 60d),
            (Width: 1280, Height: 960, MaxFps: 60d),
            (Width: 1280, Height: 720, MaxFps: 60d)
        };

        foreach (var size in sizes)
        {
            foreach (var fps in CommonFrameRates.Where(fps => fps <= size.MaxFps))
            {
                yield return CreateMode(size.Width, size.Height, fps, "mjpeg");
            }
        }
    }
}

