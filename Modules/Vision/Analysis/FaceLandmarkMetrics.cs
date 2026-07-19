using AvatarBuilder.Modules.Vision.Common;
namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceLandmarkMetrics
{
    public static FaceLandmarkMetrics None { get; } = new();

    public bool HasFace { get; init; }

    public string Source { get; init; } = "";

    public string ConfidenceLabel { get; init; } = "none";

    public DateTime CapturedAtUtc { get; init; }

    public double TrackingConfidence { get; init; }

    public double EyeConfidence { get; init; }

    public double MouthConfidence { get; init; }

    public double EyeMeasurementQualityPercent { get; init; }

    public double MouthMeasurementQualityPercent { get; init; }

    public double BrowMeasurementQualityPercent { get; init; }

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

    public double? RawEyeAsymmetryPercent { get; init; }

    public double? EyeAsymmetryPercent { get; init; }

    public double EyeAgreementPercent => EyeAsymmetryPercent is double asymmetry
        ? Math.Clamp(100d - asymmetry, 0d, 100d)
        : 0d;

    public bool PossibleOneEyeArtifact { get; init; }

    public bool LeftEyeReconstructed { get; init; }

    public bool RightEyeReconstructed { get; init; }

    public bool MouthReconstructed { get; init; }

    public bool EyeArtifactSuppressed { get; init; }

    public bool AnyEyeReconstructed => LeftEyeReconstructed || RightEyeReconstructed;

    public double OverallMeasurementQualityPercent
    {
        get
        {
            if (!HasFace)
            {
                return 0d;
            }

            var hasEye = AverageEyeOpeningRatio.HasValue;
            var hasMouth = MouthOpeningRatio.HasValue;
            return (hasEye, hasMouth) switch
            {
                (true, true) => EyeMeasurementQualityPercent * 0.72d + MouthMeasurementQualityPercent * 0.28d,
                (true, false) => EyeMeasurementQualityPercent,
                (false, true) => MouthMeasurementQualityPercent * 0.75d,
                _ => 0d
            };
        }
    }

    public bool IsEyeMeasurementUsable => AverageEyeOpeningRatio.HasValue && EyeMeasurementQualityPercent >= 42d;

    public bool IsMouthMeasurementUsable => MouthOpeningRatio.HasValue && MouthMeasurementQualityPercent >= 40d;

    public bool IsJawDroopMeasurementUsable =>
        JawDroopRatio.HasValue
        && MouthMeasurementQualityPercent >= 38d
        && TrackingConfidence >= 0.35d;

    public bool IsBrowMeasurementUsable => AverageBrowHeightRatio.HasValue && BrowMeasurementQualityPercent >= 42d;

    public string MeasurementQualityLabel
    {
        get
        {
            var quality = OverallMeasurementQualityPercent;
            if (quality >= 75d)
            {
                return "strong";
            }

            if (quality >= 55d)
            {
                return "usable";
            }

            if (quality >= 35d)
            {
                return "limited";
            }

            return "low";
        }
    }

    public double? RawLeftEyeOpeningRatio { get; init; }

    public double? RawRightEyeOpeningRatio { get; init; }

    public double? RawAverageEyeOpeningRatio { get; init; }

    public double? RawMouthOpeningRatio { get; init; }

    public double? RawJawDroopRatio { get; init; }

    public double? RawLeftBrowHeightRatio { get; init; }

    public double? RawRightBrowHeightRatio { get; init; }

    public double? RawAverageBrowHeightRatio { get; init; }

    public double? LeftEyeOpeningRatio { get; init; }

    public double? RightEyeOpeningRatio { get; init; }

    public double? AverageEyeOpeningRatio { get; init; }

    public double? MouthOpeningRatio { get; init; }

    public double? MouthOpeningVelocityPerSecond { get; init; }

    public double? JawDroopRatio { get; init; }

    public double? JawDroopVelocityPerSecond { get; init; }

    public double? LeftBrowHeightRatio { get; init; }

    public double? RightBrowHeightRatio { get; init; }

    public double? AverageBrowHeightRatio { get; init; }

    public double? BrowHeightVelocityPerSecond { get; init; }

    public double? BrowAsymmetryPercent { get; init; }

    public double? MediaPipeLeftEyeBlinkPercent { get; init; }

    public double? MediaPipeRightEyeBlinkPercent { get; init; }

    public double? MediaPipeAverageEyeBlinkPercent { get; init; }

    public double? MediaPipeJawOpenPercent { get; init; }

    public double? MediaPipeMouthClosePercent { get; init; }

    public double? MediaPipeEyeOpeningCorrectionRatio { get; init; }

    public double? MediaPipeMouthOpeningCorrectionRatio { get; init; }

    public bool MediaPipeEyeOpeningCorrected => MediaPipeEyeOpeningCorrectionRatio.HasValue;

    public bool MediaPipeMouthOpeningCorrected => MediaPipeMouthOpeningCorrectionRatio.HasValue;

    public bool HasMediaPipeBlendshapeEvidence =>
        MediaPipeAverageEyeBlinkPercent.HasValue
        || MediaPipeJawOpenPercent.HasValue
        || MediaPipeMouthClosePercent.HasValue;

    public double HeadYawDegrees { get; init; }

    public double HeadPitchDegrees { get; init; }

    public double HeadRollDegrees { get; init; }

    public string Status
    {
        get
        {
            if (!HasFace)
            {
                return "landmarks waiting";
            }

            var eyes = AverageEyeOpeningRatio is double eyeRatio
                ? $"eyes {eyeRatio * 100d:0}%"
                : "eyes --";
            var mouth = MouthOpeningRatio is double mouthRatio
                ? $"mouth {mouthRatio * 100d:0}%"
                : "mouth --";
            var jawDroop = JawDroopRatio is double jawRatio
                ? $", jaw drop {jawRatio * 100d:0}%"
                : "";
            var brow = AverageBrowHeightRatio is double browRatio
                ? $", brow {browRatio * 100d:0}%"
                : "";
            var agreement = EyeAsymmetryPercent is double
                ? $", eye agreement {EyeAgreementPercent:0}%"
                : "";
            var artifact = PossibleOneEyeArtifact ? ", possible one-eye artifact" : "";
            var reconstructed = AnyEyeReconstructed || MouthReconstructed || EyeArtifactSuppressed
                ? ", reconstruction used"
                : "";
            var blendshape = HasMediaPipeBlendshapeEvidence
                ? $", mp blink {MediaPipeAverageEyeBlinkPercent?.ToString("0") ?? "--"}%, jaw {MediaPipeJawOpenPercent?.ToString("0") ?? "--"}%"
                : "";
            var correction = MediaPipeEyeOpeningCorrected || MediaPipeMouthOpeningCorrected
                ? $", mp correction eye {FormatSigned(MediaPipeEyeOpeningCorrectionRatio)}, mouth {FormatSigned(MediaPipeMouthOpeningCorrectionRatio)}"
                : "";
            return $"landmarks {MeasurementQualityLabel}: {eyes}, {mouth}{jawDroop}{brow}, q {OverallMeasurementQualityPercent:0}%{agreement}{artifact}{reconstructed}{blendshape}{correction}";
        }
    }

    private static string FormatSigned(double? value)
    {
        return value is double number ? number.ToString("+0.###;-0.###;0") : "--";
    }
}
