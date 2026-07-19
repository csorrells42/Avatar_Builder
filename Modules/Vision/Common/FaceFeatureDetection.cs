using System.Windows;

namespace AvatarBuilder.Modules.Vision.Common;

public sealed class FaceFeatureDetection
{
    public static FaceFeatureDetection None { get; } = new();

    public bool HasFace { get; init; }

    public Rect FaceBox { get; init; }

    public Rect? LeftEyeBox { get; init; }

    public Rect? RightEyeBox { get; init; }

    public Rect? MouthBox { get; init; }

    public IReadOnlyList<Point> FaceContour { get; init; } = [];

    public IReadOnlyList<Point> LeftEyeContour { get; init; } = [];

    public IReadOnlyList<Point> RightEyeContour { get; init; } = [];

    public IReadOnlyList<Point> OuterLipContour { get; init; } = [];

    public IReadOnlyList<Point> InnerLipContour { get; init; } = [];

    public IReadOnlyList<Point> JawContour { get; init; } = [];

    public double TrackingConfidence { get; init; }

    public double EyeConfidence { get; init; }

    public double MouthConfidence { get; init; }

    public bool EyeImageQualityAvailable { get; init; }

    public bool MouthImageQualityAvailable { get; init; }

    public double EyeGlarePercent { get; init; }

    public double MouthGlarePercent { get; init; }

    public double EyeContrastPercent { get; init; }

    public double MouthContrastPercent { get; init; }

    public double EyeSharpnessPercent { get; init; }

    public double MouthSharpnessPercent { get; init; }

    public double EyeDarkCoveragePercent { get; init; }

    public double MouthDarkCoveragePercent { get; init; }

    public string Source { get; init; } = "";

    public FaceCueGuideLayout ToGuideLayout(FaceCueGuideLayout fallback)
    {
        if (!HasFace || FaceBox.Width <= 0d || FaceBox.Height <= 0d)
        {
            return fallback;
        }

        return new FaceCueGuideLayout(
            (FaceBox.Left + FaceBox.Width / 2d) * 100d,
            (FaceBox.Top + FaceBox.Height / 2d) * 100d,
            FaceBox.Height * 100d);
    }
}
