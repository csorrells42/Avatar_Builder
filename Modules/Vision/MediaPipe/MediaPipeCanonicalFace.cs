using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

public sealed record MediaPipeCanonicalFace(IReadOnlyList<MediaPipeCanonicalPoint> Points, double EyeSpan)
{
	public static MediaPipeCanonicalFace Empty { get; } = new MediaPipeCanonicalFace(Array.Empty<MediaPipeCanonicalPoint>(), 0.0);
}
