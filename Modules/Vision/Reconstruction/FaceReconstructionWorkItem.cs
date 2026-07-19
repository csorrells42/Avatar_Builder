namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class FaceReconstructionWorkItem
{
    public string SchemaVersion { get; set; } = "face-reconstruction-workitem-v1";

    public string WorkItemId { get; set; } = Guid.NewGuid().ToString("N");

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public string BackendId { get; set; } = FaceReconstructionBackendIds.MeasurementOnlyPreview;

    public FaceReconstructionSubjectGate SubjectGate { get; set; } = new();

    public string OutputFolder { get; set; } = "";

    public List<FaceReconstructionSourceFrame> SourceFrames { get; set; } = [];

    public List<string> RequestedOutputs { get; set; } =
    [
        "face_mesh_obj",
        "shape_coefficients",
        "expression_coefficients",
        "texture_or_albedo",
        "preview_render",
        "quality_report"
    ];

    public bool RequiresSubjectGate { get; set; } = true;

    public bool RequiresExplicitTrainingMediaForPhotorealReconstruction { get; set; } = true;

    public bool StoresRawContinuousVideo { get; set; }

    public string SafetyBoundary { get; set; } =
        "Output is a digital representation of a real person, not the living person.";

    public string Notes { get; set; } = "";
}
