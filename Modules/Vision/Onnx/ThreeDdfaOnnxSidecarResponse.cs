using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using AvatarBuilder.Modules.Vision.Diagnostics;

namespace AvatarBuilder.Modules.Vision.Onnx;

public sealed class ThreeDdfaOnnxSidecarResponse
{
	public static ThreeDdfaOnnxSidecarResponse Waiting { get; } = new ThreeDdfaOnnxSidecarResponse
	{
		Ok = false,
		Status = "3DDFA/ONNX waiting",
		TrustDecision = "3DDFA/ONNX has not produced a reconstruction yet."
	};

	[JsonPropertyName("requestId")]
	public string RequestId { get; init; } = "";

	[JsonPropertyName("ok")]
	public bool Ok { get; init; }

	[JsonPropertyName("hasFace")]
	public bool HasFace { get; init; }

	[JsonPropertyName("status")]
	public string Status { get; init; } = "";

	[JsonPropertyName("backend")]
	public string Backend { get; init; } = "3DDFA_V2 ONNX";

	[JsonPropertyName("capturedAtUtc")]
	public string CapturedAtUtc { get; init; } = "";

	[JsonPropertyName("trustDecision")]
	public string TrustDecision { get; init; } = "";

	[JsonPropertyName("reconstructionConfidencePercent")]
	public double ReconstructionConfidencePercent { get; init; }

	[JsonPropertyName("pose")]
	public ThreeDdfaOnnxSidecarPose Pose { get; init; } = new ThreeDdfaOnnxSidecarPose();

	[JsonPropertyName("faceBox")]
	public ThreeDdfaOnnxSidecarFaceBox? FaceBox { get; init; }

	[JsonPropertyName("roiBox")]
	public IReadOnlyList<double> RoiBox { get; init; } = Array.Empty<double>();

	[JsonPropertyName("denseVertexCount")]
	public int DenseVertexCount { get; init; }

	[JsonPropertyName("denseSampleStride")]
	public int DenseSampleStride { get; init; }

	[JsonPropertyName("denseVertices")]
	public IReadOnlyList<ThreeDdfaOnnxSidecarVertex> DenseVertices { get; set; } = Array.Empty<ThreeDdfaOnnxSidecarVertex>();

	[JsonPropertyName("canonicalIdentityVertices")]
	public IReadOnlyList<ThreeDdfaOnnxSidecarVertex> CanonicalIdentityVertices { get; set; } = Array.Empty<ThreeDdfaOnnxSidecarVertex>();

	[JsonPropertyName("denseEdges")]
	public IReadOnlyList<ThreeDdfaOnnxSidecarEdge> DenseEdges { get; set; } = Array.Empty<ThreeDdfaOnnxSidecarEdge>();

	[JsonPropertyName("denseVertexCoordinates")]
	public IReadOnlyList<double> DenseVertexCoordinates { get; set; } = Array.Empty<double>();

	[JsonPropertyName("canonicalIdentityCoordinates")]
	public IReadOnlyList<double> CanonicalIdentityCoordinates { get; set; } = Array.Empty<double>();

	[JsonPropertyName("denseEdgeIndices")]
	public IReadOnlyList<int> DenseEdgeIndices { get; set; } = Array.Empty<int>();

	[JsonPropertyName("sparseLandmarks")]
	public IReadOnlyList<ThreeDdfaOnnxSidecarVertex> SparseLandmarks { get; init; } = Array.Empty<ThreeDdfaOnnxSidecarVertex>();

	[JsonPropertyName("canonicalSparseLandmarks")]
	public IReadOnlyList<ThreeDdfaOnnxSidecarVertex> CanonicalSparseLandmarks { get; init; } = Array.Empty<ThreeDdfaOnnxSidecarVertex>();

	[JsonPropertyName("cameraMatrixCoefficients")]
	public IReadOnlyList<double> CameraMatrixCoefficients { get; init; } = Array.Empty<double>();

	[JsonPropertyName("shapeCoefficients")]
	public IReadOnlyList<double> ShapeCoefficients { get; init; } = Array.Empty<double>();

	[JsonPropertyName("expressionCoefficients")]
	public IReadOnlyList<double> ExpressionCoefficients { get; init; } = Array.Empty<double>();

	[JsonPropertyName("warnings")]
	public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

	[JsonPropertyName("mode")]
	public string Mode { get; init; } = "";

	[JsonPropertyName("timingsMilliseconds")]
	public IReadOnlyDictionary<string, double> TimingsMilliseconds { get; init; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

	[JsonIgnore]
	public VisionPipelineDiagnostics Diagnostics { get; set; } = VisionPipelineDiagnostics.None;

	public void ExpandCompactMeshData()
	{
		if (DenseVertices.Count == 0 && DenseVertexCoordinates.Count >= 3)
		{
			DenseVertices = ExpandVertices(DenseVertexCoordinates);
		}
		if (CanonicalIdentityVertices.Count == 0 && CanonicalIdentityCoordinates.Count >= 3)
		{
			CanonicalIdentityVertices = ExpandVertices(CanonicalIdentityCoordinates);
		}
		if (DenseEdges.Count == 0 && DenseEdgeIndices.Count >= 2)
		{
			List<ThreeDdfaOnnxSidecarEdge> list = new List<ThreeDdfaOnnxSidecarEdge>(DenseEdgeIndices.Count / 2);
			for (int i = 0; i + 1 < DenseEdgeIndices.Count; i += 2)
			{
				list.Add(new ThreeDdfaOnnxSidecarEdge
				{
					FromIndex = DenseEdgeIndices[i],
					ToIndex = DenseEdgeIndices[i + 1]
				});
			}
			DenseEdges = list;
		}
		DenseVertexCoordinates = Array.Empty<double>();
		CanonicalIdentityCoordinates = Array.Empty<double>();
		DenseEdgeIndices = Array.Empty<int>();
	}

	private static List<ThreeDdfaOnnxSidecarVertex> ExpandVertices(IReadOnlyList<double> coordinates)
	{
		List<ThreeDdfaOnnxSidecarVertex> list = new List<ThreeDdfaOnnxSidecarVertex>(coordinates.Count / 3);
		for (int i = 0; i + 2 < coordinates.Count; i += 3)
		{
			list.Add(new ThreeDdfaOnnxSidecarVertex
			{
				Index = i / 3,
				X = coordinates[i],
				Y = coordinates[i + 1],
				Z = coordinates[i + 2]
			});
		}
		return list;
	}
}
