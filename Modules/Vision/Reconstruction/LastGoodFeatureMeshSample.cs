using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class LastGoodFeatureMeshSample
{
    public string SampleId { get; init; } = "";

    public DateTime CapturedAtUtc { get; init; }

    public string Source { get; init; } = "";

    public string DenseMeshTopology { get; init; } = "";

    public string CoordinateSpace { get; init; } =
        "Dense points carry X/Y/Z positions for this frame. A/B/C rotation comes from the current pose estimator when available: A around X, B around Y, C around Z.";

    public int PointCount { get; init; }

    public double TrackingConfidencePercent { get; init; }

    public double EyeConfidencePercent { get; init; }

    public double MouthConfidencePercent { get; init; }

    public double OverallQualityPercent { get; init; }

    public double EyeQualityPercent { get; init; }

    public double MouthQualityPercent { get; init; }

    public double BrowQualityPercent { get; init; }

    public double FaceReliabilityPercent { get; init; }

    public double FaceContinuityPercent { get; init; }

    public double EyeReliabilityPercent { get; init; }

    public double MouthReliabilityPercent { get; init; }

    public double HeadYawDegrees { get; init; }

    public double HeadPitchDegrees { get; init; }

    public double HeadRollDegrees { get; init; }

    public double ARotationAroundXDegrees => HeadPitchDegrees;

    public double BRotationAroundYDegrees => HeadYawDegrees;

    public double CRotationAroundZDegrees => HeadRollDegrees;

    public double XHorizontalPercent { get; init; }

    public double YVerticalPercent { get; init; }

    public double? DistanceInches { get; init; }

    public double? ApparentDistanceUnits { get; init; }

    public double? RelativeDistanceScale { get; init; }

    public double? InterEyeFrameWidthPercent { get; init; }

    public double ZConfidencePercent { get; init; }

    public bool DistanceCalibrated { get; init; }

    public bool ZUsesCameraFov { get; init; }

    public bool ZUsesLearnedReference { get; init; }

    public string ZEstimateKind { get; init; } = "";

    public string ZQualityLabel { get; init; } = "";

    public string RotationSource { get; init; } = "";

    public string DistanceSource { get; init; } = "";

    public string ReferenceScaleSource { get; init; } = "";

    public double? LeftBrowHeightRatio { get; init; }

    public double? RightBrowHeightRatio { get; init; }

    public double? AverageBrowHeightRatio { get; init; }

    public double? BrowAsymmetryPercent { get; init; }

    public bool PossibleOneEyeArtifact { get; init; }

    public bool LeftEyeReconstructed { get; init; }

    public bool RightEyeReconstructed { get; init; }

    public bool MouthReconstructed { get; init; }

    public bool EyeArtifactSuppressed { get; init; }

    public string CaptureQualityLabel { get; init; } = "";

    public double CaptureQualityScorePercent { get; init; }

    public string GoodFeatureReason { get; init; } = "";

    public IReadOnlyList<double> FacialTransformationMatrix { get; init; } = [];

    public List<FaceMeshLandmarkPoint> Points { get; init; } = [];

    public List<LastGoodFeatureMeshWireframeEdge> WireframeEdges { get; init; } = [];

    public List<LastGoodFeatureMeshFeatureGroup> FeatureGroups { get; init; } = [];

    public LastGoodFeatureThreeDdfaSnapshot? ThreeDdfaFullResolution { get; set; }
}

public sealed class LastGoodFeatureThreeDdfaSnapshot
{
    public string RequestId { get; init; } = "";

    public DateTime CapturedAtUtc { get; init; }

    public string Source { get; init; } = "3DDFA_V2 ONNX";

    public string CoordinateSpace { get; init; } =
        "3DDFA dense vertices are reconstructed face coordinates for this frame. A/B/C rotation is supplied by the 3DDFA pose solver: A around X, B around Y, C around Z.";

    public int DenseVertexCount { get; init; }

    public int DenseSampleStride { get; init; } = 1;

    public double ReconstructionConfidencePercent { get; init; }

    public double ARotationAroundXDegrees { get; init; }

    public double BRotationAroundYDegrees { get; init; }

    public double CRotationAroundZDegrees { get; init; }

    public string PoseSource { get; init; } = "3DDFA_V2 ONNX";

    public string TrustDecision { get; init; } = "";

    public int VertexCount => Vertices.Count;

    public int EdgeCount => TopologyEdges.Count;

    public List<FaceMeshLandmarkPoint> Vertices { get; init; } = [];

    public List<LastGoodFeatureMeshWireframeEdge> TopologyEdges { get; init; } = [];

    public List<FaceMeshLandmarkPoint> SparseLandmarks { get; init; } = [];

    public IReadOnlyList<double> CameraMatrixCoefficients { get; init; } = [];

    public IReadOnlyList<double> ShapeCoefficients { get; init; } = [];

    public IReadOnlyList<double> ExpressionCoefficients { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
