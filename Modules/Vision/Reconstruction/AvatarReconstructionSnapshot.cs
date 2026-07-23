using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarReconstructionSnapshot
{
	public string BackendId { get; init; } = "";

	public string RequestId { get; init; } = "";

	public DateTime CapturedAtUtc { get; init; }

	public string Source { get; init; } = "avatar reconstruction";

	public string CoordinateSpace { get; init; } = "backend-defined reconstructed face coordinates";

	public int DenseVertexCount { get; init; }

	public int DenseSampleStride { get; init; } = 1;

	public double ReconstructionConfidencePercent { get; init; }

	public double ARotationAroundXDegrees { get; init; }

	public double BRotationAroundYDegrees { get; init; }

	public double CRotationAroundZDegrees { get; init; }

	public string PoseSource { get; init; } = "avatar reconstruction";

	public string TrustDecision { get; init; } = "";

	public string SourceImageUri { get; init; } = "";

	public int VertexCount => Vertices.Count;

	public int EdgeCount => TopologyEdges.Count;

	public List<FaceMeshLandmarkPoint> Vertices { get; init; } = new List<FaceMeshLandmarkPoint>();

	public List<FaceMeshLandmarkPoint> CanonicalIdentityVertices { get; init; } = new List<FaceMeshLandmarkPoint>();

	public List<FaceMeshLandmarkPoint> AlignedIdentityVertices { get; init; } = new List<FaceMeshLandmarkPoint>();

	public IReadOnlyList<double> CurrentModelShapeCoefficients { get; init; } = Array.Empty<double>();

	public long CurrentModelSequenceNumber { get; init; }

	public double CurrentModelCoefficientDeltaRms { get; init; }

	public bool PinnedStillConverged { get; init; }

	public int PinnedStillPassCount { get; init; }

	public int PinnedStillStablePassCount { get; init; }

	public List<MeshTopologyEdge> TopologyEdges { get; init; } = new List<MeshTopologyEdge>();

	public List<FaceMeshLandmarkPoint> SparseLandmarks { get; init; } = new List<FaceMeshLandmarkPoint>();

	public IReadOnlyList<double> CameraMatrixCoefficients { get; init; } = Array.Empty<double>();

	public IReadOnlyList<double> ShapeCoefficients { get; init; } = Array.Empty<double>();

	public IReadOnlyList<double> ExpressionCoefficients { get; init; } = Array.Empty<double>();

	public IReadOnlyList<double> PoseCoefficients { get; init; } = Array.Empty<double>();

	public int SourceFrameWidthPixels { get; init; }

	public int SourceFrameHeightPixels { get; init; }

	public ReconstructionInputFaceBox? InputFaceBox { get; init; }

	public List<FaceMeshLandmarkPoint> ObservedLandmarks { get; init; } = new List<FaceMeshLandmarkPoint>();

	public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
