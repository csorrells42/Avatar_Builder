using System;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public sealed class MediaPipeStereoGeometryFrame
{
	public required string CalibrationId { get; init; }

	public required DateTime CapturedAtUtc { get; init; }

	public required TimeSpan PairSkew { get; init; }

	public required double BaselineInches { get; init; }

	public required double FrameReprojectionResidualPercent { get; init; }

	public required double CameraATrackingConfidence { get; init; }

	public required double CameraBTrackingConfidence { get; init; }

	public required MediaPipeStereoRigLandmark[] Landmarks { get; init; }

	public MediaPipeStereoImagePair? ImagePair { get; init; }
}
