namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class LastGoodThreeDdfaReport
{
    public string SchemaVersion { get; init; } = "last-good-3ddfa-v2";

    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    public string SubjectId { get; init; } = "";

    public string SubjectDisplayName { get; init; } = "";

    public string StoragePolicy { get; init; } =
        "Inspection-only rolling 3DDFA dense reconstruction cache. Stores the last five observed full-resolution meshes with one shared topology; canonical identity geometry remains in Avatar Model Progress.";

    public string AvatarModelProgressHtmlPath { get; init; } = "";

    public FaceReconstructionLaneStatus ReconstructionLane { get; init; } = FaceReconstructionLaneStatus.Waiting;

    public List<MeshTopologyEdge> DenseTopologyEdges { get; init; } = [];

    public IReadOnlyList<ThreeDdfaReconstructionSnapshot> Samples { get; init; } = [];
}
