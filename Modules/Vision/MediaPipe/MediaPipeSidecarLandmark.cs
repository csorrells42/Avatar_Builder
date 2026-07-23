using System.Text.Json.Serialization;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

internal sealed class MediaPipeSidecarLandmark
{
	[JsonPropertyName("x")]
	public double X { get; init; }

	[JsonPropertyName("y")]
	public double Y { get; init; }

	[JsonPropertyName("z")]
	public double Z { get; init; }
}
