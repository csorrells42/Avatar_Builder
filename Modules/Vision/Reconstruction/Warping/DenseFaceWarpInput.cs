using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.Reconstruction.Warping;

public sealed class DenseFaceWarpInput
{
	public string SubjectId { get; init; } = "";

	public string SubjectDisplayName { get; init; } = "";

	public DateTime CreatedAtUtc { get; init; }

	public IReadOnlyList<DenseFaceWarpVertex> SourceVertices { get; init; } = Array.Empty<DenseFaceWarpVertex>();

	public IReadOnlyList<MeshTopologyEdge> TopologyEdges { get; init; } = Array.Empty<MeshTopologyEdge>();

	public IReadOnlyList<DenseFaceWarpControlPoint> ControlPoints { get; init; } = Array.Empty<DenseFaceWarpControlPoint>();
}
