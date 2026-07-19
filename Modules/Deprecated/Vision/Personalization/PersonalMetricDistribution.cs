namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalMetricDistribution
{
    public int SampleCount { get; set; }

    public double TotalWeight { get; set; }

    public double? Average { get; set; }

    public double? Minimum { get; set; }

    public double? Maximum { get; set; }

    public double? StandardDeviation { get; set; }

    public double? ExponentialMovingAverage { get; set; }

    public double? NormalLow { get; set; }

    public double? NormalHigh { get; set; }
}
