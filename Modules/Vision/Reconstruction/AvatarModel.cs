using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarModel
{
    public const string CurrentSchemaVersion = "avatar-model-v2-canonical-3ddfa";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;

    public string SubjectId { get; init; } = "";

    public string SubjectDisplayName { get; init; } = "";

    public string Status { get; init; } = "waiting for 3DDFA observations";

    public string StoragePolicy { get; init; } =
        "Stores measurement-only 3DDFA avatar observations and a derived model. It does not store webcam video or raw photos.";

    public AvatarIdentityModel Identity { get; init; } = new();

    public AvatarExpressionModel Expression { get; init; } = new();

    public AvatarPoseCoverage PoseCoverage { get; init; } = new();

    public List<AvatarModelSampleSummary> RecentSamples { get; init; } = [];

    public List<string> Findings { get; init; } = [];
}

public sealed class AvatarIdentityModel
{
    public string CoordinateSpace { get; init; } =
        "Canonical 3DDFA identity space: expression-free BFM vertices are centered, scaled, rigidly aligned without deformation, and weighted across accepted observations. Legacy image-space scans use rigid Procrustes alignment only.";

    public int SampleCount { get; init; }

    public double ConfidencePercent { get; init; }

    public int DenseVertexCount { get; init; }

    public int DenseTopologyEdgeCount { get; init; }

    public int ShapeCoefficientCount { get; init; }

    public double ShapeCoefficientStabilityPercent { get; init; }

    public List<double> MeanShapeCoefficients { get; init; } = [];

    public List<FaceMeshLandmarkPoint> MeanDenseVertices { get; init; } = [];

    public List<MeshTopologyEdge> TopologyEdges { get; init; } = [];

    public List<AvatarRegionConfidence> RegionConfidence { get; init; } = [];
}

public sealed class AvatarExpressionModel
{
    public int SampleCount { get; init; }

    public double ConfidencePercent { get; init; }

    public int ExpressionCoefficientCount { get; init; }

    public double ExpressionEnergyPercent { get; init; }

    public List<double> MeanExpressionCoefficients { get; init; } = [];

    public List<AvatarCoefficientRange> ExpressionRanges { get; init; } = [];

    public List<AvatarExpressionBucket> Buckets { get; init; } = [];
}

public sealed class AvatarPoseCoverage
{
    public int TotalSampleCount { get; init; }

    public int FrontSampleCount { get; init; }

    public int LeftBTurnSampleCount { get; init; }

    public int RightBTurnSampleCount { get; init; }

    public int NegativeATiltSampleCount { get; init; }

    public int PositiveATiltSampleCount { get; init; }

    public int NegativeCTiltSampleCount { get; init; }

    public int PositiveCTiltSampleCount { get; init; }

    public int CloseZSampleCount { get; init; }

    public int FarZSampleCount { get; init; }

    public double ARangeDegrees { get; init; }

    public double BRangeDegrees { get; init; }

    public double CRangeDegrees { get; init; }

    public double ZScaleRangePercent { get; init; }

    public double CoveragePercent { get; init; }

    public string Summary { get; init; } = "waiting";
}

public sealed class AvatarModelSampleSummary
{
    public string RequestId { get; init; } = "";

    public string SampleId { get; init; } = "";

    public DateTime CapturedAtUtc { get; init; }

    public double WeightPercent { get; init; }

    public double ReconstructionConfidencePercent { get; init; }

    public double SampleQualityPercent { get; init; }

    public double ARotationAroundXDegrees { get; init; }

    public double BRotationAroundYDegrees { get; init; }

    public double CRotationAroundZDegrees { get; init; }

    public int VertexCount { get; init; }

    public string IdentityUse { get; init; } = "";
}

public sealed class AvatarRegionConfidence
{
    public string Region { get; init; } = "";

    public double ConfidencePercent { get; init; }

    public string Basis { get; init; } = "";
}

public sealed class AvatarCoefficientRange
{
    public int Index { get; init; }

    public double Minimum { get; init; }

    public double Maximum { get; init; }

    public double Range { get; init; }
}

public sealed class AvatarExpressionBucket
{
    public string Name { get; init; } = "";

    public int SampleCount { get; init; }

    public double AverageEnergyPercent { get; init; }

    public string Meaning { get; init; } = "";
}
