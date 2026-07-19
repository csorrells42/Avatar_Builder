using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class ThreeDdfaReconstructionSnapshot
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

    public List<MeshTopologyEdge> TopologyEdges { get; init; } = [];

    public List<FaceMeshLandmarkPoint> SparseLandmarks { get; init; } = [];

    public IReadOnlyList<double> CameraMatrixCoefficients { get; init; } = [];

    public IReadOnlyList<double> ShapeCoefficients { get; init; } = [];

    public IReadOnlyList<double> ExpressionCoefficients { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}
