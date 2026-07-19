namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class LastGoodThreeDdfaReport
{
    public string SchemaVersion { get; init; } = "last-good-3ddfa-v1";

    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    public string SubjectId { get; init; } = "";

    public string SubjectDisplayName { get; init; } = "";

    public string StoragePolicy { get; init; } =
        "Inspection-only rolling 3DDFA dense reconstruction cache. Stores only the last five full-resolution 3DDFA samples for review; it is not a raw webcam video or photo archive.";

    public string AvatarModelProgressHtmlPath { get; init; } = "";

    public FaceReconstructionLaneStatus ReconstructionLane { get; init; } = FaceReconstructionLaneStatus.Waiting;

    public IReadOnlyList<ThreeDdfaReconstructionSnapshot> Samples { get; init; } = [];
}
