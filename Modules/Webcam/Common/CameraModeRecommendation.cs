namespace AvatarBuilder.Modules.Webcam.Common;

public static class CameraModeRecommendation
{
    public static CameraVideoMode? FindRecommendedMode(
        IReadOnlyList<CameraVideoMode> modes,
        int maximumWidth,
        double targetFramesPerSecond)
    {
        var concreteModes = modes
            .Where(mode => !mode.IsAuto && mode.Width is > 0 && mode.Height is > 0)
            .ToList();
        if (concreteModes.Count == 0)
        {
            return modes.FirstOrDefault(mode => mode.IsAuto) ?? modes.FirstOrDefault();
        }

        var matchingFidelity = concreteModes
            .Where(mode => mode.Width.GetValueOrDefault() <= maximumWidth)
            .ToList();
        if (matchingFidelity.Count == 0)
        {
            return concreteModes
                .OrderBy(mode => mode.Width.GetValueOrDefault() * mode.Height.GetValueOrDefault())
                .ThenBy(mode => FrameRateLoadPriority(mode.FramesPerSecond, targetFramesPerSecond))
                .ThenBy(mode => CaptureFormatPriority(mode.InputFormat))
                .FirstOrDefault();
        }

        return matchingFidelity
            .OrderByDescending(mode => mode.Width.GetValueOrDefault() * mode.Height.GetValueOrDefault())
            .ThenBy(mode => FrameRateLoadPriority(mode.FramesPerSecond, targetFramesPerSecond))
            .ThenBy(mode => CaptureFormatPriority(mode.InputFormat))
            .FirstOrDefault();
    }

    public static double FrameRateLoadPriority(double? framesPerSecond, double targetFramesPerSecond)
    {
        if (framesPerSecond is not double fps || fps <= 0)
        {
            return double.MaxValue;
        }

        var target = Math.Clamp(targetFramesPerSecond, 1d, 60d);
        return fps <= target + 0.25d
            ? target - fps
            : 1000d + fps - target;
    }

    public static int CaptureFormatPriority(string? format)
    {
        return format?.ToLowerInvariant() switch
        {
            "mjpeg" or "mjpg" => 0,
            "h264" => 1,
            "nv12" => 2,
            "rgb32" => 3,
            null => 4,
            _ => 5
        };
    }
}
