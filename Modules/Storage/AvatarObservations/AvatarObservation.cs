using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public sealed record AvatarObservation
{
	public string ObservationId { get; init; } = "";

	public string RequestId { get; init; } = "";

	public string SampleId { get; init; } = "";

	public DateTime CapturedAtUtc { get; init; }

	public string BackendId { get; init; } = "3ddfa-v2-onnx-reconstruction";

	public string Source { get; init; } = "avatar reconstruction";

	public double ReconstructionConfidencePercent { get; init; }

	public long ModelSequenceNumber { get; init; }

	public double ModelCoefficientDeltaRms { get; init; }

	public double SampleQualityPercent { get; init; }

	public double EyeQualityPercent { get; init; }

	public double MouthQualityPercent { get; init; }

	public double BrowQualityPercent { get; init; }

	public double StabilityQualityPercent { get; init; }

	public double ARotationAroundXDegrees { get; init; }

	public double BRotationAroundYDegrees { get; init; }

	public double CRotationAroundZDegrees { get; init; }

	public double XHorizontalPercent { get; init; }

	public double YVerticalPercent { get; init; }

	public double? RelativeDistanceScale { get; init; }

	public double? ApparentDistanceUnits { get; init; }

	public double? FaceWidthPercent { get; init; }

	public double? FaceHeightPercent { get; init; }

	public double IdentityWeightPercent { get; init; }

	public double ExpressionWeightPercent { get; init; }

	public double IdentityScorePercent { get; init; }

	public double AnimationScorePercent { get; init; }

	public double CoverageScorePercent { get; init; }

	public double RetentionScorePercent { get; init; }

	public double ExpressionEnergyPercent { get; init; }

	public string PoseBucket { get; init; } = "";

	public string IdentityUse { get; init; } = "";

	public string TrustDecision { get; init; } = "";

	public string ScanObjectPath { get; init; } = "";

	public string ImageObjectPath { get; init; } = "";

	public string TopologyObjectPath { get; init; } = "";

	public string ScanSha256 { get; init; } = "";

	public string ImageSha256 { get; init; } = "";

	public string TopologySha256 { get; init; } = "";

	public int DenseVertexCount { get; init; }

	public int CanonicalVertexCount { get; init; }

	public List<double> ShapeCoefficients { get; init; } = new List<double>();

	public List<double> ExpressionCoefficients { get; init; } = new List<double>();

	public List<double> PoseCoefficients { get; init; } = new List<double>();

	public int SourceFrameWidthPixels { get; init; }

	public int SourceFrameHeightPixels { get; init; }

	public ReconstructionInputFaceBox? InputFaceBox { get; init; }

	public List<FaceMeshLandmarkPoint> ObservedLandmarks { get; init; } = new List<FaceMeshLandmarkPoint>();

	public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

	public List<FaceMeshLandmarkPoint> Vertices { get; init; } = new List<FaceMeshLandmarkPoint>();

	public List<FaceMeshLandmarkPoint> CanonicalIdentityVertices { get; init; } = new List<FaceMeshLandmarkPoint>();

	public List<FaceMeshLandmarkPoint> SparseLandmarks { get; init; } = new List<FaceMeshLandmarkPoint>();

	public List<double> CameraMatrixCoefficients { get; init; } = new List<double>();
}
