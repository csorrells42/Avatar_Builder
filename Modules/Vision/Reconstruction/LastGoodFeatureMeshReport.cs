namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class LastGoodFeatureMeshReport
{
    public string SchemaVersion { get; init; } = "last-good-feature-mesh-v1";

    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    public string SubjectId { get; init; } = "";

    public string SubjectDisplayName { get; init; } = "";

    public string StoragePolicy { get; init; } =
        "Inspection-only rolling MediaPipe feature-lock cache. Stores only the last five good fast-tracking landmark sets; dense 3DDFA reconstructions are stored in their own review file.";

    public string AvatarModelProgressHtmlPath { get; init; } = "";

    public LastGoodFeatureMeshStabilityReport HeadLockedStability { get; init; } = new();

    public FaceReconstructionLaneStatus ReconstructionLane { get; init; } = FaceReconstructionLaneStatus.Waiting;

    public IReadOnlyList<LastGoodFeatureMeshSample> Samples { get; init; } = [];
}
