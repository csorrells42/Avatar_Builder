using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AvatarBuilder.Modules.Vision.Deca;

public sealed class DecaIdentityFitFrame
{
	[JsonPropertyName("frameWidthPixels")]
	public int FrameWidthPixels { get; init; }

	[JsonPropertyName("frameHeightPixels")]
	public int FrameHeightPixels { get; init; }

	[JsonPropertyName("faceBox")]
	public DecaSidecarFaceBox? FaceBox { get; init; }

	[JsonPropertyName("observedLandmarkCoordinates")]
	public IReadOnlyList<double> ObservedLandmarkCoordinates { get; init; } = Array.Empty<double>();
}
