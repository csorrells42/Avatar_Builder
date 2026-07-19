namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class MeasurementFacePreviewSurfaceEvidence
{
    public string RegionId { get; set; } = "";

    public string Label { get; set; } = "";

    public string Role { get; set; } = "";

    public string Status { get; set; } = "";

    public double FrontEvidencePercent { get; set; }

    public double DepthEvidencePercent { get; set; }

    public double DepthProfileCoveragePercent { get; set; }

    public double DepthStabilityPercent { get; set; }

    public double? DepthRange { get; set; }

    public double? AverageDepthStandardDeviation { get; set; }

    public double OverallConfidencePercent { get; set; }

    public string EvidenceBasis { get; set; } = "";

    public string NextCaptureHint { get; set; } = "";

    public List<string> SupportingPoseBuckets { get; set; } = [];
}
