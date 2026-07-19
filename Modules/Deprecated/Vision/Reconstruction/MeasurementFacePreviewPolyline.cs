namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class MeasurementFacePreviewPolyline
{
    public string Id { get; set; } = "";

    public string Label { get; set; } = "";

    public string Role { get; set; } = "";

    public string Provenance { get; set; } = "";

    public double ConfidencePercent { get; set; }

    public List<string> PointIds { get; set; } = [];
}
