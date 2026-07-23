using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction;

public sealed class MediaPipeSilhouetteAngleProfile
{
	public int YawBinDegrees { get; init; }

	public int PitchBinDegrees { get; init; }

	public string CameraId { get; init; } = "";

	public long FrameCount { get; init; }

	public IReadOnlyList<MediaPipeSilhouetteBand> Bands { get; init; } = Array.Empty<MediaPipeSilhouetteBand>();
}
