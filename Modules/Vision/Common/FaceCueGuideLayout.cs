using AvatarBuilder.Modules.Vision.Common;
using System.Windows;

namespace AvatarBuilder.Modules.Vision.Common;

public sealed class FaceCueGuideLayout
{
    public FaceCueGuideLayout(double centerXPercent, double centerYPercent, double heightPercent)
    {
        CenterXPercent = Math.Clamp(centerXPercent, 20d, 80d);
        CenterYPercent = Math.Clamp(centerYPercent, 20d, 80d);
        HeightPercent = Math.Clamp(heightPercent, 25d, 90d);
    }

    public double CenterXPercent { get; }

    public double CenterYPercent { get; }

    public double HeightPercent { get; }

    public FaceCueRelativeRegion Face => new(0d, 0d, 1d, 1d);

    public FaceCueRelativeRegion LeftEye => new(0.24d, 0.24d, 0.50d, 0.45d);

    public FaceCueRelativeRegion RightEye => new(0.50d, 0.24d, 0.76d, 0.45d);

    public FaceCueRelativeRegion Eyes => new(0.24d, 0.24d, 0.76d, 0.45d);

    public FaceCueRelativeRegion Jaw => new(0.25d, 0.55d, 0.75d, 0.84d);

    public FaceCueRelativeRegion LeftJaw => new(0.25d, 0.55d, 0.50d, 0.84d);

    public FaceCueRelativeRegion RightJaw => new(0.50d, 0.55d, 0.75d, 0.84d);

    public Rect GetFaceBox()
    {
        var height = HeightPercent / 100d;
        var width = height * 0.80d;
        var centerX = CenterXPercent / 100d;
        var centerY = CenterYPercent / 100d;
        var left = Math.Clamp(centerX - width / 2d, 0d, 1d - width);
        var top = Math.Clamp(centerY - height / 2d, 0d, 1d - height);
        return new Rect(left, top, width, height);
    }

    public Rect ToFrameRect(FaceCueRelativeRegion region)
    {
        var face = GetFaceBox();
        return new Rect(
            face.X + face.Width * region.Left,
            face.Y + face.Height * region.Top,
            face.Width * (region.Right - region.Left),
            face.Height * (region.Bottom - region.Top));
    }

    public Int32Rect ToPixelRegion(int width, int height, FaceCueRelativeRegion region)
    {
        var rect = ToFrameRect(region);
        var x = Math.Clamp((int)(width * rect.X), 0, width - 1);
        var y = Math.Clamp((int)(height * rect.Y), 0, height - 1);
        var regionWidth = Math.Clamp((int)(width * rect.Width), 1, width - x);
        var regionHeight = Math.Clamp((int)(height * rect.Height), 1, height - y);
        return new Int32Rect(x, y, regionWidth, regionHeight);
    }
}

public readonly record struct FaceCueRelativeRegion(double Left, double Top, double Right, double Bottom);
