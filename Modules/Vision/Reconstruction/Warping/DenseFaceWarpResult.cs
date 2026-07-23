using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.Reconstruction.Warping;

public sealed class DenseFaceWarpResult
{
	public const string CurrentSchemaVersion = "three-ddfa-mediapipe-warp-v2";

	public string SchemaVersion { get; init; } = "three-ddfa-mediapipe-warp-v2";

	public string SubjectId { get; init; } = "";

	public string SubjectDisplayName { get; init; } = "";

	public DateTime CreatedAtUtc { get; init; }

	public string Status { get; init; } = "Dense warp is waiting for source and target geometry.";

	public int SourceVertexCount { get; init; }

	public int AppliedControlPointCount { get; init; }

	public double SourceAnchorRms { get; init; }

	public double WarpedAnchorRms { get; init; }

	public double MaximumAppliedDisplacement { get; init; }

	public double MedianAppliedDisplacement { get; init; }

	public double Percentile95AppliedDisplacement { get; init; }

	public double SafetyClampVertexPercent { get; init; }

	public IReadOnlyList<DenseFaceWarpVertex> SourceVertices { get; init; } = Array.Empty<DenseFaceWarpVertex>();

	public IReadOnlyList<DenseFaceWarpVertex> WarpedVertices { get; init; } = Array.Empty<DenseFaceWarpVertex>();

	public IReadOnlyList<MeshTopologyEdge> TopologyEdges { get; init; } = Array.Empty<MeshTopologyEdge>();

	public IReadOnlyList<DenseFaceWarpControlPoint> ControlPoints { get; init; } = Array.Empty<DenseFaceWarpControlPoint>();

	public bool HasGeometry
	{
		get
		{
			if (SourceVertices.Count > 0 && SourceVertices.Count == WarpedVertices.Count)
			{
				return ControlPoints.Count >= 12;
			}
			return false;
		}
	}
}
