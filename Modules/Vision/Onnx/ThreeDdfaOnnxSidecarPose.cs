using System.Text.Json.Serialization;

namespace AvatarBuilder.Modules.Vision.Onnx;

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
