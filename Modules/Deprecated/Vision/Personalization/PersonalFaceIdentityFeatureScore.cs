namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceIdentityFeatureScore
{
    public string Name { get; set; } = "";

    public double Value { get; set; }

    public double? BaselineAverage { get; set; }

    public double? NormalLow { get; set; }

    public double? NormalHigh { get; set; }

    public double ConfidencePercent { get; set; }

    public bool IsOutlier { get; set; }
}
