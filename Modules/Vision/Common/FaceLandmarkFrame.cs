using System.Windows;

namespace AvatarBuilder.Modules.Vision.Common;

public sealed class FaceLandmarkFrame
{
    public static FaceLandmarkFrame None { get; } = new();

    public bool HasFace { get; init; }

    public string Source { get; init; } = "";

    public DateTime CapturedAtUtc { get; init; }

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

    public bool LeftEyeReconstructed { get; init; }

    public bool RightEyeReconstructed { get; init; }

    public bool MouthReconstructed { get; init; }

    public bool EyeArtifactSuppressed { get; init; }

    public double HeadYawDegrees { get; init; }

    public double HeadPitchDegrees { get; init; }

    public double HeadRollDegrees { get; init; }

    public IReadOnlyDictionary<string, double> BlendshapeScores { get; init; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    public string DenseMeshTopology { get; init; } = "";

    public IReadOnlyList<FaceMeshLandmarkPoint> DenseMeshPoints { get; init; } = [];

    public IReadOnlyList<double> FacialTransformationMatrix { get; init; } = [];

    public IReadOnlyList<Point> FaceContour { get; init; } = [];

    public IReadOnlyList<Point> LeftEyeContour { get; init; } = [];

    public IReadOnlyList<Point> RightEyeContour { get; init; } = [];

    public IReadOnlyList<Point> LeftBrowContour { get; init; } = [];

    public IReadOnlyList<Point> RightBrowContour { get; init; } = [];

    public IReadOnlyList<Point> OuterLipContour { get; init; } = [];

    public IReadOnlyList<Point> InnerLipContour { get; init; } = [];

    public IReadOnlyList<Point> JawContour { get; init; } = [];

    public bool HasEyeContours => LeftEyeContour.Count >= 4 && RightEyeContour.Count >= 4;

    public bool HasBrowContours => LeftBrowContour.Count >= 3 && RightBrowContour.Count >= 3;

    public bool HasMouthContours => InnerLipContour.Count >= 4 || OuterLipContour.Count >= 4;

    public bool HasDenseMesh => DenseMeshPoints.Count >= 100;

    public string ConfidenceLabel
    {
        get
        {
            var confidence = Math.Min(TrackingConfidence, Math.Min(EyeConfidence, MouthConfidence));
            if (confidence >= 0.75d)
            {
                return "strong";
            }

            if (confidence >= 0.45d)
            {
                return "usable";
            }

            return "limited";
        }
    }
}
