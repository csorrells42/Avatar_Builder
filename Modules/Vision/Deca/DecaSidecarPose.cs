using System.Text.Json.Serialization;

namespace AvatarBuilder.Modules.Vision.Deca;

public sealed class DecaSidecarPose
{
	[JsonPropertyName("aRotationAroundXDegrees")]
	public double ARotationAroundXDegrees { get; init; }

	[JsonPropertyName("bRotationAroundYDegrees")]
	public double BRotationAroundYDegrees { get; init; }

	[JsonPropertyName("cRotationAroundZDegrees")]
	public double CRotationAroundZDegrees { get; init; }
}
