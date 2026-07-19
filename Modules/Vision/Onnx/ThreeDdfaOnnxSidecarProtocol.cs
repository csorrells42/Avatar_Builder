using System.Text.Json.Serialization;

namespace AvatarBuilder.Modules.Vision.Onnx;

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

    [JsonPropertyName("returnDenseVertices")]
    public bool ReturnDenseVertices { get; init; }

    [JsonPropertyName("denseSampleStride")]
    public int DenseSampleStride { get; init; } = 24;
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
    public IReadOnlyList<ThreeDdfaOnnxSidecarVertex> DenseVertices { get; init; } = [];

    [JsonPropertyName("denseEdges")]
    public IReadOnlyList<ThreeDdfaOnnxSidecarEdge> DenseEdges { get; init; } = [];

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
