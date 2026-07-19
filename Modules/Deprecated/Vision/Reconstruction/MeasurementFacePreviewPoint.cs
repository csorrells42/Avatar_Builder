namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class MeasurementFacePreviewPoint
{
    public string Id { get; set; } = "";

    public string Label { get; set; } = "";

    public string Role { get; set; } = "";

    public string Provenance { get; set; } = "";

    public double ConfidencePercent { get; set; }

    // A preview point owns X/Y/Z. A/B/C orientation is supplied by the preview
    // model metrics, pose bucket, or future local surface patch.
    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }
}
