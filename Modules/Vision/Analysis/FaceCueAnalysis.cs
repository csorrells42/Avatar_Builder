using AvatarBuilder.Modules.Vision.Common;
namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceCueAnalysis
{
    public string AnalyzerName { get; init; } = "Local calibrated region analyzer";

    public bool BaselineReady { get; init; }

    public int BaselineSamples { get; init; }

    public double QualityPercent { get; init; }

    public string QualityStatus
    {
        get
        {
            if (QualityPercent >= 75d)
            {
                return "good";
            }

            if (QualityPercent >= 50d)
            {
                return "usable";
            }

            return "limited";
        }
    }

    public double CompositeCuePercent { get; init; }

    public double EyeOpennessPercent { get; init; }

    public double EyeDropPercent { get; init; }

    public double EyeAsymmetryPercent { get; init; }

    public double JawChangePercent { get; init; }

    public double JawAsymmetryPercent { get; init; }

    public double LowerFaceDropPercent { get; init; }

    public double HeadDriftPercent { get; init; }

    public string Status
    {
        get
        {
            if (!BaselineReady)
            {
                return $"calibrating ({BaselineSamples}/30) | quality {QualityStatus}";
            }

            return $"eyes {EyeOpennessPercent:0}% open | eye drop {EyeDropPercent:0}% | jaw support {JawChangePercent:0}% | score {CompositeCuePercent:0}% | quality {QualityStatus}";
        }
    }
}
