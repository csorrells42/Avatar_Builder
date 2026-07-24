using System;
using System.Collections.Generic;
using System.Linq;

namespace AvatarBuilder.Modules.Vision.Reconstruction.Warping;

internal sealed class DenseFaceWarpDocument
{
	public string SubjectId { get; init; } = "";

	public string SubjectDisplayName { get; init; } = "";

	public DateTime CreatedAtUtc { get; init; }

	public string Status { get; init; } = "";

	public int SourceVertexCount { get; init; }

	public int AppliedControlPointCount { get; init; }

	public int MeasuredVertexCount { get; init; }

	public int MeasuredEdgeCount { get; init; }

	public int DenseEdgeCount { get; init; }

	public int HighConfidenceMeasuredVertexCount { get; init; }

	public int MediumConfidenceMeasuredVertexCount { get; init; }

	public int LowConfidenceMeasuredVertexCount { get; init; }

	public double MeanMeasuredConfidence { get; init; }

	public double AnchorRmsImprovementPercent { get; init; }

	public double SourceAnchorRms { get; init; }

	public double WarpedAnchorRms { get; init; }

	public double MaximumAppliedDisplacement { get; init; }

	public double MedianAppliedDisplacement { get; init; }

	public double Percentile95AppliedDisplacement { get; init; }

	public double SafetyClampVertexPercent { get; init; }

	public IReadOnlyList<double> SourceCoordinates { get; init; } = Array.Empty<double>();

	public IReadOnlyList<double> MeasuredCoordinates { get; init; } = Array.Empty<double>();

	public IReadOnlyList<double> MeasuredConfidences { get; init; } = Array.Empty<double>();

	public IReadOnlyList<double> WarpedCoordinates { get; init; } = Array.Empty<double>();

	public IReadOnlyList<int> EdgeIndices { get; init; } = Array.Empty<int>();

	public IReadOnlyList<int> MeasuredEdgeIndices { get; init; } = Array.Empty<int>();

	public IReadOnlyList<DenseFaceWarpControlDocument> Controls { get; init; } = Array.Empty<DenseFaceWarpControlDocument>();

	public static DenseFaceWarpDocument Create(DenseFaceWarpResult result)
	{
		double[] measuredConfidences = NormalizeMeasuredConfidences(result.MeasuredConfidences, result.MeasuredVertices.Count);
		int highConfidenceCount = measuredConfidences.Count(confidence => confidence >= 0.72);
		int mediumConfidenceCount = measuredConfidences.Count(confidence => confidence >= 0.35 && confidence < 0.72);
		int lowConfidenceCount = measuredConfidences.Length - highConfidenceCount - mediumConfidenceCount;
		return new DenseFaceWarpDocument
		{
			SubjectId = result.SubjectId,
			SubjectDisplayName = result.SubjectDisplayName,
			CreatedAtUtc = result.CreatedAtUtc,
			Status = result.Status,
			SourceVertexCount = result.SourceVertexCount,
			AppliedControlPointCount = result.AppliedControlPointCount,
			MeasuredVertexCount = result.MeasuredVertices.Count,
			MeasuredEdgeCount = result.MeasuredTopologyEdges.Count,
			DenseEdgeCount = result.TopologyEdges.Count,
			HighConfidenceMeasuredVertexCount = highConfidenceCount,
			MediumConfidenceMeasuredVertexCount = mediumConfidenceCount,
			LowConfidenceMeasuredVertexCount = lowConfidenceCount,
			MeanMeasuredConfidence = measuredConfidences.Length == 0 ? 0.0 : measuredConfidences.Average(),
			AnchorRmsImprovementPercent = CalculateAnchorRmsImprovement(result.SourceAnchorRms, result.WarpedAnchorRms),
			SourceAnchorRms = result.SourceAnchorRms,
			WarpedAnchorRms = result.WarpedAnchorRms,
			MaximumAppliedDisplacement = result.MaximumAppliedDisplacement,
			MedianAppliedDisplacement = result.MedianAppliedDisplacement,
			Percentile95AppliedDisplacement = result.Percentile95AppliedDisplacement,
			SafetyClampVertexPercent = result.SafetyClampVertexPercent,
			SourceCoordinates = FlattenVertices(result.SourceVertices),
			MeasuredCoordinates = FlattenVertices(result.MeasuredVertices),
			MeasuredConfidences = measuredConfidences,
			WarpedCoordinates = FlattenVertices(result.WarpedVertices),
			EdgeIndices = FlattenEdges(result.TopologyEdges),
			MeasuredEdgeIndices = FlattenEdges(result.MeasuredTopologyEdges),
			Controls = result.ControlPoints.Select(DenseFaceWarpControlDocument.Create).ToArray()
		};
	}

	private static double[] NormalizeMeasuredConfidences(IReadOnlyList<double> confidences, int vertexCount)
	{
		double[] normalized = new double[Math.Max(0, vertexCount)];
		for (int i = 0; i < normalized.Length; i++)
		{
			double confidence = i < confidences.Count ? confidences[i] : 0.0;
			normalized[i] = double.IsFinite(confidence) ? Math.Clamp(confidence, 0.0, 1.0) : 0.0;
		}
		return normalized;
	}

	private static double CalculateAnchorRmsImprovement(double sourceRms, double warpedRms)
	{
		if (!double.IsFinite(sourceRms) || !double.IsFinite(warpedRms) || sourceRms <= 1e-9)
		{
			return 0.0;
		}
		return Math.Clamp((sourceRms - warpedRms) / sourceRms * 100.0, -999.0, 100.0);
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
