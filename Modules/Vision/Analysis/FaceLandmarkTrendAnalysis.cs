using AvatarBuilder.Modules.Vision.Common;
namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceLandmarkTrendAnalysis
{
    public static FaceLandmarkTrendAnalysis Waiting { get; } = new();

    public bool HasUsableTrend { get; init; }

    public int SampleCount { get; init; }

    public double WindowSeconds { get; init; }

    public double QualityPercent { get; init; }

    public bool EyeTrendEligible { get; init; }

    public bool MouthTrendEligible { get; init; }

    public double? EyeOpeningStartRatio { get; init; }

    public double? EyeOpeningEndRatio { get; init; }

    public double? EyeOpeningSlopePerSecond { get; init; }

    public double? EyeClosingTrendPercent { get; init; }

    public double? MouthOpeningStartRatio { get; init; }

    public double? MouthOpeningEndRatio { get; init; }

    public double? MouthOpeningSlopePerSecond { get; init; }

    public double? MouthOpeningTrendPercent { get; init; }

    public double TrendCuePercent { get; init; }

    public string Status
    {
        get
        {
            if (!HasUsableTrend)
            {
                return SampleCount > 0
                    ? $"landmark trend warming {SampleCount}, q {QualityPercent:0}%"
                    : "landmark trend waiting";
            }

            var eye = EyeClosingTrendPercent is double eyeTrend
                ? $"eye trend {eyeTrend:0}%"
                : "eye trend --";
            var mouth = MouthOpeningTrendPercent is double mouthTrend
                ? $"mouth trend +{mouthTrend:0}%"
                : "mouth trend --";
            return $"{eye}, {mouth}, trend score {TrendCuePercent:0}%";
        }
    }
}
