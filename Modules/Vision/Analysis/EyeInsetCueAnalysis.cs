namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class EyeInsetCueAnalysis
{
    public static EyeInsetCueAnalysis Waiting { get; } = new();

    public bool HasMeasurement { get; init; }

    public bool BaselineReady { get; init; }

    public int BaselineSamples { get; init; }

    public bool CueEligible { get; init; }

    public double QualityPercent { get; init; }

    public double? OpeningRatio { get; init; }

    public double? BaselineOpeningRatio { get; init; }

    public double? EyeClosurePercent { get; init; }

    public double CompositeCuePercent { get; init; }

    public string Status
    {
        get
        {
            if (!HasMeasurement)
            {
                return "eye inset cue waiting";
            }

            if (!BaselineReady)
            {
                return $"eye inset baseline {BaselineSamples}/12, q {QualityPercent:0}%";
            }

            var closure = EyeClosurePercent is double value ? $"{value:0}%" : "--";
            return $"eye inset closure {closure}, score {CompositeCuePercent:0}%";
        }
    }
}
