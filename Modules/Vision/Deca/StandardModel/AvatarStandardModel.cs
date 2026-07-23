using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Vision.Deca.StandardModel;

public sealed record AvatarStandardModel
{
	public string SchemaVersion { get; init; } = "avatar-standard-model-v1";

	public string SubjectId { get; init; } = "";

	public string SubjectDisplayName { get; init; } = "";

	public string SourceFolder { get; init; } = "";

	public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;

	public int SourceImageCount { get; init; }

	public int CompletedImageCount { get; init; }

	public int ConvergedImageCount { get; init; }

	public int FailedImageCount { get; init; }

	public string LastSourceImageName { get; init; } = "";

	public long ModelSequenceNumber { get; init; }

	public double CoefficientDeltaRms { get; init; }

	public bool LastStillConverged { get; init; }

	public int LastStillPassCount { get; init; }

	public double LastMeasuredFitPercent { get; init; }

	public int IdentityEvidencePoseCount { get; init; }

	public bool UsesLegacyIdentityAnchor { get; init; }

	public IReadOnlyList<double> ShapeCoefficients { get; init; } = Array.Empty<double>();

	public IReadOnlyList<FaceMeshLandmarkPoint> CanonicalIdentityVertices { get; init; } = Array.Empty<FaceMeshLandmarkPoint>();

	public IReadOnlyList<MeshTopologyEdge> TopologyEdges { get; init; } = Array.Empty<MeshTopologyEdge>();

	public Dictionary<string, AvatarStandardPoseSample> PoseAtlas { get; init; } = new Dictionary<string, AvatarStandardPoseSample>(StringComparer.Ordinal);

	public const string CurrentSchemaVersion = "avatar-standard-model-v1";
}
