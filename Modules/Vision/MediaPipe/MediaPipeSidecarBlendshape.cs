using System.Text.Json.Serialization;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

internal sealed class MediaPipeSidecarBlendshape
{
	[JsonPropertyName("categoryName")]
	public string CategoryName { get; init; } = "";

	[JsonPropertyName("score")]
	public double Score { get; init; }
}
