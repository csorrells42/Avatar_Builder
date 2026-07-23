using System;
using System.Collections.Generic;
using System.Linq;

namespace AvatarBuilder.Modules.Vision.Reconstruction.Warping;

internal sealed class DenseFaceWarpDocument
{
	public string SchemaVersion { get; init; } = "three-ddfa-mediapipe-warp-v2";

	public string SubjectId { get; init; } = "";

	public string SubjectDisplayName { get; init; } = "";

	public DateTime CreatedAtUtc { get; init; }

	public string Status { get; init; } = "";

	public int SourceVertexCount { get; init; }

	public int AppliedControlPointCount { get; init; }

	public double SourceAnchorRms { get; init; }

	public double WarpedAnchorRms { get; init; }

	public double MaximumAppliedDisplacement { get; init; }

	public double MedianAppliedDisplacement { get; init; }

	public double Percentile95AppliedDisplacement { get; init; }

	public double SafetyClampVertexPercent { get; init; }

	public IReadOnlyList<double> SourceCoordinates { get; init; } = Array.Empty<double>();

	public IReadOnlyList<double> WarpedCoordinates { get; init; } = Array.Empty<double>();

	public IReadOnlyList<int> EdgeIndices { get; init; } = Array.Empty<int>();

	public IReadOnlyList<DenseFaceWarpControlDocument> Controls { get; init; } = Array.Empty<DenseFaceWarpControlDocument>();

	public static DenseFaceWarpDocument Create(DenseFaceWarpResult result)
	{
		return new DenseFaceWarpDocument
		{
			SubjectId = result.SubjectId,
			SubjectDisplayName = result.SubjectDisplayName,
			CreatedAtUtc = result.CreatedAtUtc,
			Status = result.Status,
			SourceVertexCount = result.SourceVertexCount,
			AppliedControlPointCount = result.AppliedControlPointCount,
			SourceAnchorRms = result.SourceAnchorRms,
			WarpedAnchorRms = result.WarpedAnchorRms,
			MaximumAppliedDisplacement = result.MaximumAppliedDisplacement,
			MedianAppliedDisplacement = result.MedianAppliedDisplacement,
			Percentile95AppliedDisplacement = result.Percentile95AppliedDisplacement,
			SafetyClampVertexPercent = result.SafetyClampVertexPercent,
			SourceCoordinates = FlattenVertices(result.SourceVertices),
			WarpedCoordinates = FlattenVertices(result.WarpedVertices),
			EdgeIndices = FlattenEdges(result.TopologyEdges),
			Controls = result.ControlPoints.Select(DenseFaceWarpControlDocument.Create).ToArray()
		};
	}

	private static double[] FlattenVertices(IReadOnlyList<DenseFaceWarpVertex> vertices)
	{
		double[] array = new double[vertices.Count * 3];
		for (int i = 0; i < vertices.Count; i++)
		{
			DenseFaceWarpVertex denseFaceWarpVertex = vertices[i];
			int num = i * 3;
			array[num] = denseFaceWarpVertex.X;
			array[num + 1] = denseFaceWarpVertex.Y;
			array[num + 2] = denseFaceWarpVertex.Z;
		}
		return array;
	}

	private static int[] FlattenEdges(IReadOnlyList<MeshTopologyEdge> edges)
	{
		int[] array = new int[edges.Count * 2];
		for (int i = 0; i < edges.Count; i++)
		{
			int num = i * 2;
			array[num] = edges[i].FromIndex;
			array[num + 1] = edges[i].ToIndex;
		}
		return array;
	}
}
