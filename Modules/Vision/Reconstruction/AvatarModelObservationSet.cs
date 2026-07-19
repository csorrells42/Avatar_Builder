using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarModelObservationSet
{
    public const string CurrentSchemaVersion = "avatar-model-observations-v2";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;

    public string SubjectId { get; init; } = "";

    public string SubjectDisplayName { get; init; } = "";

    public int MaxObservationCount { get; init; } = AvatarModelObservationStore.MaxObservationCount;

    public string StoragePolicy { get; init; } =
        "Bounded measurement-only 3DDFA observation set. The newest accepted observations are retained; raw camera video and photos are not stored here.";

    public List<MeshTopologyEdge> DenseTopologyEdges { get; init; } = [];

    public List<AvatarModelObservation> Observations { get; init; } = [];
}

public sealed class AvatarModelObservation
{
    public string RequestId { get; init; } = "";

    public string SampleId { get; init; } = "";

    public DateTime CapturedAtUtc { get; init; }

    public string Source { get; init; } = "3DDFA_V2 ONNX";

    public double ReconstructionConfidencePercent { get; init; }

    public double SampleQualityPercent { get; init; }

    public double EyeQualityPercent { get; init; }

    public double MouthQualityPercent { get; init; }

    public double BrowQualityPercent { get; init; }

    public double ARotationAroundXDegrees { get; init; }

    public double BRotationAroundYDegrees { get; init; }

    public double CRotationAroundZDegrees { get; init; }

    public double XHorizontalPercent { get; init; }

    public double YVerticalPercent { get; init; }

    public double? RelativeDistanceScale { get; init; }

    public double? ApparentDistanceUnits { get; init; }

    public double IdentityWeightPercent { get; init; }

    public double ExpressionWeightPercent { get; init; }

    public string IdentityUse { get; init; } = "";

    public string TrustDecision { get; init; } = "";

    public List<FaceMeshLandmarkPoint> Vertices { get; init; } = [];

    public List<FaceMeshLandmarkPoint> CanonicalIdentityVertices { get; init; } = [];

    public string IdentityGeometrySource { get; init; } = "legacy image-space 3DDFA vertices";

    public List<double> CameraMatrixCoefficients { get; init; } = [];

    public List<double> ShapeCoefficients { get; init; } = [];

    public List<double> ExpressionCoefficients { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
