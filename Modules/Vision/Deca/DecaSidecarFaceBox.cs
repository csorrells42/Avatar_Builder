using System.Text.Json.Serialization;

namespace AvatarBuilder.Modules.Vision.Deca;

public sealed class DecaSidecarFaceBox
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
	public double Confidence { get; init; } = 1.0;
}
