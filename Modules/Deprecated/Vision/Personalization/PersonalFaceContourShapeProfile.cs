namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceContourShapeProfile
{
    public string FeatureId { get; set; } = "";

    public string Label { get; set; } = "";

    public string CoordinateSpace { get; set; } =
        "Face-local normalized profile. Each resampled point stores weighted X/Y/Z distributions; A/B/C orientation comes from the frame or pose bucket used to collect the sample. Depth evidence fields summarize whether the Z profile is populated, stable, and varied enough to treat as measured surface shape.";

    public bool Closed { get; set; }

    public int PointCount { get; set; }

    public int SampleCount { get; set; }

    public double TotalWeight { get; set; }

    public int DepthPointCount { get; set; }

    public double PointCoveragePercent { get; set; }

    public double DepthPointCoveragePercent { get; set; }

    public double DepthEvidencePercent { get; set; }

    public double DepthStabilityPercent { get; set; }

    public double? DepthRange { get; set; }

    public double? AverageDepthStandardDeviation { get; set; }

    public List<PersonalFaceContourShapePointProfile> Points { get; set; } = [];

    public bool HasProfile => SampleCount > 0 && Points.Count >= Math.Max(2, PointCount);
}

public sealed class PersonalFaceContourShapePointProfile
{
    public int Index { get; set; }

    public PersonalMetricDistribution X { get; set; } = new();

    public PersonalMetricDistribution Y { get; set; } = new();

    public PersonalMetricDistribution Z { get; set; } = new();
}
