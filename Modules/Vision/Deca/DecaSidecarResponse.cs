using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AvatarBuilder.Modules.Vision.Deca;

public sealed class DecaSidecarResponse
{
	[JsonPropertyName("requestId")]
	public string RequestId { get; init; } = "";

	[JsonPropertyName("capturedAtUtc")]
	public string CapturedAtUtc { get; init; } = "";

	[JsonPropertyName("ok")]
	public bool Ok { get; init; }

	[JsonPropertyName("hasFace")]
	public bool HasFace { get; init; }

	[JsonPropertyName("status")]
	public string Status { get; init; } = "";

	[JsonPropertyName("backend")]
	public string Backend { get; init; } = "DECA FLAME";

	[JsonPropertyName("trustDecision")]
	public string TrustDecision { get; init; } = "";

	[JsonPropertyName("reconstructionConfidencePercent")]
	public double ReconstructionConfidencePercent { get; init; }

	[JsonPropertyName("pose")]
	public DecaSidecarPose Pose { get; init; } = new DecaSidecarPose();

	[JsonPropertyName("projectedVertexCoordinates")]
	public IReadOnlyList<double> ProjectedVertexCoordinates { get; init; } = Array.Empty<double>();

	[JsonPropertyName("canonicalIdentityCoordinates")]
	public IReadOnlyList<double> CanonicalIdentityCoordinates { get; init; } = Array.Empty<double>();

	[JsonPropertyName("alignedIdentityProjectedCoordinates")]
	public IReadOnlyList<double> AlignedIdentityProjectedCoordinates { get; init; } = Array.Empty<double>();

	[JsonPropertyName("currentModelShapeCoefficients")]
	public IReadOnlyList<double> CurrentModelShapeCoefficients { get; init; } = Array.Empty<double>();

	[JsonPropertyName("currentModelSequenceNumber")]
	public long CurrentModelSequenceNumber { get; init; }

	[JsonPropertyName("currentModelCoefficientDeltaRms")]
	public double CurrentModelCoefficientDeltaRms { get; init; }

	[JsonPropertyName("pinnedStillConverged")]
	public bool PinnedStillConverged { get; init; }

	[JsonPropertyName("pinnedStillPassCount")]
	public int PinnedStillPassCount { get; init; }

	[JsonPropertyName("pinnedStillStablePassCount")]
	public int PinnedStillStablePassCount { get; init; }

	[JsonPropertyName("denseEdgeIndices")]
	public IReadOnlyList<int> DenseEdgeIndices { get; set; } = Array.Empty<int>();

	[JsonPropertyName("sparseLandmarkCoordinates")]
	public IReadOnlyList<double> SparseLandmarkCoordinates { get; init; } = Array.Empty<double>();

	[JsonPropertyName("cameraMatrixCoefficients")]
	public IReadOnlyList<double> CameraMatrixCoefficients { get; init; } = Array.Empty<double>();

	[JsonPropertyName("shapeCoefficients")]
	public IReadOnlyList<double> ShapeCoefficients { get; init; } = Array.Empty<double>();

	[JsonPropertyName("expressionCoefficients")]
	public IReadOnlyList<double> ExpressionCoefficients { get; init; } = Array.Empty<double>();

	[JsonPropertyName("poseCoefficients")]
	public IReadOnlyList<double> PoseCoefficients { get; init; } = Array.Empty<double>();

	[JsonPropertyName("warnings")]
	public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

	[JsonPropertyName("timingsMilliseconds")]
	public IReadOnlyDictionary<string, double> TimingsMilliseconds { get; init; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
}
