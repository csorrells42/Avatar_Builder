using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarIdentityModel
{
	public string CoordinateSpace { get; init; } = "Canonical backend identity space: expression-free vertices are centered, scaled, and weighted directly in their shared model coordinates.";

	public int SampleCount { get; init; }

	public double ConfidencePercent { get; init; }

	public int DenseVertexCount { get; init; }

	public int DenseTopologyEdgeCount { get; init; }

	public int ShapeCoefficientCount { get; init; }

	public double ShapeCoefficientStabilityPercent { get; init; }

	public double TotalIdentityWeight { get; init; }

	public List<double> MeanShapeCoefficients { get; init; } = new List<double>();

	public List<double> ShapeCoefficientWeights { get; init; } = new List<double>();

	public List<double> MeanShapeCoefficientSquares { get; init; } = new List<double>();

	public List<FaceMeshLandmarkPoint> MeanDenseVertices { get; init; } = new List<FaceMeshLandmarkPoint>();

	public string MappingStatus { get; init; } = "Multi-frame identity mapping is waiting for independent landmark evidence.";

	public DateTime? MappingUpdatedAtUtc { get; init; }

	public int MappingFrameCount { get; init; }

	public int MappingIterationCount { get; init; }

	public double MappingInitialLandmarkRmsePercent { get; init; }

	public double MappingFinalLandmarkRmsePercent { get; init; }

	public double MappingImprovementPercent { get; init; }

	public double GenericIdentityDisplacementPercent { get; init; }

	public List<double> MappedShapeCoefficients { get; init; } = new List<double>();

	public List<FaceMeshLandmarkPoint> MappedDenseVertices { get; init; } = new List<FaceMeshLandmarkPoint>();

	public List<MeshTopologyEdge> TopologyEdges { get; init; } = new List<MeshTopologyEdge>();

	public List<AvatarRegionConfidence> RegionConfidence { get; init; } = new List<AvatarRegionConfidence>();
}
