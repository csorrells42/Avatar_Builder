namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class FaceReconstructionResult
{
    public string SchemaVersion { get; set; } = "face-reconstruction-result-v1";

    public string WorkItemId { get; set; } = "";

    public string BackendId { get; set; } = "";

    public string SubjectId { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAtUtc { get; set; }

    public string Status { get; set; } = "pending";

    public string MeshObjPath { get; set; } = "";

    public string ShapeCoefficientPath { get; set; } = "";

    public string ExpressionCoefficientPath { get; set; } = "";

    public string TexturePath { get; set; } = "";

    public string PreviewRenderPath { get; set; } = "";

    public string QualityReportPath { get; set; } = "";

    public double? ReconstructionConfidencePercent { get; set; }

    public List<string> Warnings { get; set; } = [];
}
