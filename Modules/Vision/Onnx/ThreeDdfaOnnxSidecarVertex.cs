using System.Text.Json.Serialization;

namespace AvatarBuilder.Modules.Vision.Onnx;

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
