using System.Text.Json.Serialization;

namespace AvatarBuilder.Modules.Vision.Onnx;

public sealed class ThreeDdfaOnnxSidecarEdge
{
	[JsonPropertyName("fromIndex")]
	public int FromIndex { get; init; }

	[JsonPropertyName("toIndex")]
	public int ToIndex { get; init; }
}
