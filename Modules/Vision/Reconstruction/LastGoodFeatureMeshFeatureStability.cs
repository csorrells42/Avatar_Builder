namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class LastGoodFeatureMeshFeatureStability
{
    public string FeatureId { get; set; } = "";

    public string Label { get; set; } = "";

    public string Role { get; set; } = "";

    public int SampleCount { get; set; }

    public double AverageX { get; set; }

    public double AverageY { get; set; }

    public double AverageZ { get; set; }

    public double AverageDriftPercent { get; set; }

    public double MaximumDriftPercent { get; set; }

    public string Status { get; set; } = "waiting";
}
