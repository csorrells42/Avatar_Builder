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

	[JsonPropertyName("mode")]
	public string Mode { get; init; } = "tracking";

	[JsonPropertyName("denseSampleStride")]
	public int DenseSampleStride { get; init; } = 24;

	[JsonPropertyName("includeTopology")]
	public bool IncludeTopology { get; init; }
}
