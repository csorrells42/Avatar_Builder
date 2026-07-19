using AvatarBuilder.Modules.Vision.Common;
namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceLandmarkCueAnalysis
{
    public static FaceLandmarkCueAnalysis Waiting { get; } = new();

    public bool HasUsableMeasurements { get; init; }

    public bool BaselineReady { get; init; }

    public int BaselineSamples { get; init; }

    public double QualityPercent { get; init; }

    public double EyeQualityPercent { get; init; }

    public double MouthQualityPercent { get; init; }

    public bool EyeCueEligible { get; init; }

    public bool MouthCueEligible { get; init; }

    public bool EyeBaselineReady { get; init; }

    public bool MouthBaselineReady { get; init; }

    public bool MediaPipeBlinkBaselineReady { get; init; }

    public bool MediaPipeMouthBaselineReady { get; init; }

    public double? EyeOpeningRatio { get; init; }

    public double? EyeBaselineRatio { get; init; }

    public double? EyeClosurePercent { get; init; }

    public double? MouthOpeningRatio { get; init; }

    public double? MouthBaselineRatio { get; init; }

    public double? MouthOpeningChangePercent { get; init; }

    public double? MouthOpeningVelocityPerSecond { get; init; }

    public double? JawDroopRatio { get; init; }

    public double? JawDroopBaselineRatio { get; init; }

    public double? JawDroopChangePercent { get; init; }

    public double? JawDroopVelocityPerSecond { get; init; }

    public double? MediaPipeBlinkBaselinePercent { get; init; }

    public double? MediaPipeJawOpenBaselinePercent { get; init; }

    public double? MediaPipeMouthCloseBaselinePercent { get; init; }

    public double? MediaPipeBlinkChangePercent { get; init; }

    public double? MediaPipeJawOpenChangePercent { get; init; }

    public double? MediaPipeMouthCloseDropPercent { get; init; }

    public double? MediaPipeMouthOpeningEvidencePercent { get; init; }

    public double CompositeCuePercent { get; init; }

    public string Status
    {
        get
        {
            if (!HasUsableMeasurements)
            {
                return QualityPercent > 0d
                    ? $"landmark cues waiting, q {QualityPercent:0}%"
                    : "landmark cues waiting";
            }

            if (!BaselineReady)
            {
                var mediaPipeBaseline = MediaPipeBlinkBaselineReady || MediaPipeMouthBaselineReady
                    ? ", mp baseline ready"
                    : "";
                return $"landmark baseline eye {BaselineSamples}/20, q {QualityPercent:0}%{mediaPipeBaseline}";
            }

            var eye = EyeClosurePercent is double eyeClosure
                ? $"eye closure {eyeClosure:0}%"
                : EyeCueEligible && MediaPipeBlinkChangePercent is double blinkEvidence
                    ? $"eye closure mp +{blinkEvidence:0}%"
                    : "eye closure --";
            var mouth = MouthOpeningChangePercent is double mouthChange
                ? $"mouth opening +{mouthChange:0}%"
                : MouthCueEligible && MediaPipeMouthOpeningEvidencePercent is double lowerFaceEvidence
                    ? $"mouth opening mp +{lowerFaceEvidence:0}%"
                    : "mouth opening --";
            var jaw = JawDroopChangePercent is double jawChange ? $", jaw drop +{jawChange:0}%" : "";
            var mediaPipeEye = MediaPipeBlinkChangePercent is double blinkChange
                ? $", mp blink +{blinkChange:0}%"
                : "";
            var mediaPipeMouth = MediaPipeMouthOpeningEvidencePercent is double mouthEvidence
                ? $", mp mouth +{mouthEvidence:0}%"
                : "";
            return $"{eye}, {mouth}{jaw}, score {CompositeCuePercent:0}%{mediaPipeEye}{mediaPipeMouth}";
        }
    }
}
