namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class MeasurementFacePreviewSurfacePatch
{
    public string Id { get; set; } = "";

    public string Label { get; set; } = "";

    public string Role { get; set; } = "";

    public string Provenance { get; set; } = "";

    public double ConfidencePercent { get; set; }

    public double FillOpacity { get; set; }

    public string CenterPointId { get; set; } = "";

    public int TriangleCount { get; set; }

    public double SurfaceArea { get; set; }

    public double AverageTriangleArea { get; set; }

    public double DepthRelief { get; set; }

    public double AverageNormalX { get; set; }

    public double AverageNormalY { get; set; }

    public double AverageNormalZ { get; set; }

    public double NormalConsistencyPercent { get; set; }

    public double GeometryHealthPercent { get; set; }

    public string GeometryStatus { get; set; } = "";

    public string GeometryFinding { get; set; } = "";

    public List<string> PointIds { get; set; } = [];

    public List<MeasurementFacePreviewSurfaceTriangle> Triangles { get; set; } = [];
}
