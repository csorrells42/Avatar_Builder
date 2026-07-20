using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public sealed class AvatarObservationDataset
{
    public const string CurrentSchemaVersion = "avatar-observation-catalog-v1";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string StorageRoot { get; init; } = "";

    public string SubjectId { get; init; } = "";

    public string SubjectDisplayName { get; init; } = "";

    public long Revision { get; init; }

    public long AcceptedObservationCount { get; init; }

    public long RejectedObservationCount { get; init; }

    public DateTime UpdatedAtUtc { get; init; }

    public IReadOnlyList<MeshTopologyEdge> DenseTopologyEdges { get; init; } = [];

    public IReadOnlyList<AvatarObservation> Observations { get; init; } = [];
}
