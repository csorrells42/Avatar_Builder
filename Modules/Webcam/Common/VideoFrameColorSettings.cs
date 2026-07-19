namespace AvatarBuilder.Modules.Webcam.Common;

public readonly record struct VideoFrameColorSettings(
    bool Enabled,
    double Exposure,
    double Contrast,
    double Saturation,
    double Warmth)
{
    public static VideoFrameColorSettings Off { get; } = new(false, 0d, 0d, 0d, 0d);

    public bool HasVisibleAdjustments =>
        Enabled
        && (Math.Abs(Exposure) > 0.001d
            || Math.Abs(Contrast) > 0.001d
            || Math.Abs(Saturation) > 0.001d
            || Math.Abs(Warmth) > 0.001d);
}

public static class VideoFrameColorProcessor
{
    public static void Apply(byte[] bgraBytes, VideoFrameColorSettings settings)
    {
        if (!settings.HasVisibleAdjustments)
        {
            return;
        }

        var exposureOffset = Math.Clamp(settings.Exposure, -30d, 30d) * 2.2d;
        var contrast = 1d + Math.Clamp(settings.Contrast, -40d, 40d) / 100d;
        var saturation = 1d + Math.Clamp(settings.Saturation, -40d, 40d) / 100d;
        var warmth = Math.Clamp(settings.Warmth, -40d, 40d) * 0.9d;

        for (var i = 0; i + 3 < bgraBytes.Length; i += 4)
        {
            var blue = bgraBytes[i];
            var green = bgraBytes[i + 1];
            var red = bgraBytes[i + 2];

            var adjustedRed = ApplyContrast(red + exposureOffset + warmth, contrast);
            var adjustedGreen = ApplyContrast(green + exposureOffset, contrast);
            var adjustedBlue = ApplyContrast(blue + exposureOffset - warmth, contrast);

            var luma = adjustedRed * 0.2126d + adjustedGreen * 0.7152d + adjustedBlue * 0.0722d;
            adjustedRed = luma + (adjustedRed - luma) * saturation;
            adjustedGreen = luma + (adjustedGreen - luma) * saturation;
            adjustedBlue = luma + (adjustedBlue - luma) * saturation;

            bgraBytes[i] = ClampByte(adjustedBlue);
            bgraBytes[i + 1] = ClampByte(adjustedGreen);
            bgraBytes[i + 2] = ClampByte(adjustedRed);
            bgraBytes[i + 3] = 255;
        }
    }

    private static double ApplyContrast(double value, double contrast)
    {
        return ((value - 128d) * contrast) + 128d;
    }

    private static byte ClampByte(double value)
    {
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }
}
