using System.Text.Json.Serialization;
using AvatarBuilder.Modules.Vision.Diagnostics;

namespace AvatarBuilder.Modules.Vision.Onnx;

public enum ThreeDdfaOnnxRequestMode
{
    FaceBoxOnly,
    Tracking,
    Preview,
    Full
}

internal sealed class ThreeDdfaOnnxSidecarRequest
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = "";

    [JsonPropertyName("imageBase64")]
    public string ImageBase64 { get; init; } = "";

    [JsonPropertyName("capturedAtUtc")]
    public string CapturedAtUtc { get; init; } = "";

    [JsonPropertyName("faceBox")]
    public ThreeDdfaOnnxSidecarFaceBox? FaceBox { get; init; }

    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "tracking";

    [JsonPropertyName("denseSampleStride")]
    public int DenseSampleStride { get; init; } = 24;

    [JsonPropertyName("includeTopology")]
    public bool IncludeTopology { get; init; }
}

public sealed class ThreeDdfaOnnxSidecarResponse
{
    public static ThreeDdfaOnnxSidecarResponse Waiting { get; } = new()
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
    public ThreeDdfaOnnxSidecarPose Pose { get; init; } = new();

    [JsonPropertyName("faceBox")]
    public ThreeDdfaOnnxSidecarFaceBox? FaceBox { get; init; }

    [JsonPropertyName("roiBox")]
    public IReadOnlyList<double> RoiBox { get; init; } = [];

    [JsonPropertyName("denseVertexCount")]
    public int DenseVertexCount { get; init; }

    [JsonPropertyName("denseSampleStride")]
    public int DenseSampleStride { get; init; }

    [JsonPropertyName("denseVertices")]
    public IReadOnlyList<ThreeDdfaOnnxSidecarVertex> DenseVertices { get; set; } = [];

    [JsonPropertyName("canonicalIdentityVertices")]
    public IReadOnlyList<ThreeDdfaOnnxSidecarVertex> CanonicalIdentityVertices { get; set; } = [];

    [JsonPropertyName("denseEdges")]
    public IReadOnlyList<ThreeDdfaOnnxSidecarEdge> DenseEdges { get; set; } = [];

    [JsonPropertyName("denseVertexCoordinates")]
    public IReadOnlyList<double> DenseVertexCoordinates { get; set; } = [];

    [JsonPropertyName("canonicalIdentityCoordinates")]
    public IReadOnlyList<double> CanonicalIdentityCoordinates { get; set; } = [];

    [JsonPropertyName("denseEdgeIndices")]
    public IReadOnlyList<int> DenseEdgeIndices { get; set; } = [];

    [JsonPropertyName("sparseLandmarks")]
    public IReadOnlyList<ThreeDdfaOnnxSidecarVertex> SparseLandmarks { get; init; } = [];

    [JsonPropertyName("cameraMatrixCoefficients")]
    public IReadOnlyList<double> CameraMatrixCoefficients { get; init; } = [];

    [JsonPropertyName("shapeCoefficients")]
    public IReadOnlyList<double> ShapeCoefficients { get; init; } = [];

    [JsonPropertyName("expressionCoefficients")]
    public IReadOnlyList<double> ExpressionCoefficients { get; init; } = [];

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "";

    [JsonPropertyName("timingsMilliseconds")]
    public IReadOnlyDictionary<string, double> TimingsMilliseconds { get; init; }
        = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

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
            var edges = new List<ThreeDdfaOnnxSidecarEdge>(DenseEdgeIndices.Count / 2);
            for (var index = 0; index + 1 < DenseEdgeIndices.Count; index += 2)
            {
                edges.Add(new ThreeDdfaOnnxSidecarEdge
                {
                    FromIndex = DenseEdgeIndices[index],
                    ToIndex = DenseEdgeIndices[index + 1]
                });
            }

            DenseEdges = edges;
        }

        DenseVertexCoordinates = [];
        CanonicalIdentityCoordinates = [];
        DenseEdgeIndices = [];
    }

    private static List<ThreeDdfaOnnxSidecarVertex> ExpandVertices(IReadOnlyList<double> coordinates)
    {
        var vertices = new List<ThreeDdfaOnnxSidecarVertex>(coordinates.Count / 3);
        for (var coordinateIndex = 0; coordinateIndex + 2 < coordinates.Count; coordinateIndex += 3)
        {
            vertices.Add(new ThreeDdfaOnnxSidecarVertex
            {
                Index = coordinateIndex / 3,
                X = coordinates[coordinateIndex],
                Y = coordinates[coordinateIndex + 1],
                Z = coordinates[coordinateIndex + 2]
            });
        }

        return vertices;
    }
}

public sealed class ThreeDdfaOnnxSidecarFaceBox
{
    [JsonPropertyName("left")]
    public double Left { get; init; }

    [JsonPropertyName("top")]
    public double Top { get; init; }

    [JsonPropertyName("right")]
    public double Right { get; init; }

    [JsonPropertyName("bottom")]
    public double Bottom { get; init; }

    [JsonPropertyName("normalized")]
    public bool Normalized { get; init; } = true;

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; } = 1d;
}

public sealed class ThreeDdfaOnnxSidecarPose
{
    [JsonPropertyName("aRotationAroundXDegrees")]
    public double ARotationAroundXDegrees { get; init; }

    [JsonPropertyName("bRotationAroundYDegrees")]
    public double BRotationAroundYDegrees { get; init; }

    [JsonPropertyName("cRotationAroundZDegrees")]
    public double CRotationAroundZDegrees { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = "3DDFA_V2 ONNX";
}

public sealed class ThreeDdfaOnnxSidecarVertex
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("z")]
    public double Z { get; init; }
}

public sealed class ThreeDdfaOnnxSidecarEdge
{
    [JsonPropertyName("fromIndex")]
    public int FromIndex { get; init; }

    [JsonPropertyName("toIndex")]
    public int ToIndex { get; init; }
}
