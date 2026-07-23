using System;

namespace AvatarBuilder.Modules.Vision.Diagnostics;

public sealed class MediaPipeConvergenceSample
{
	public DateTime CapturedAtUtc { get; init; }

	public double ElapsedSeconds { get; init; }

	public long FaceFrameCount { get; init; }

	public double ScreenMotionPercent { get; init; }

	public double CanonicalAllRmsPercent { get; init; }

	public double CanonicalStableRmsPercent { get; init; }

	public double PoseMotionDegrees { get; init; }

	public double AppCorrectionRmsPercent { get; init; }

	public double TrackingConfidencePercent { get; init; }

	public double EyeSpanNormalized { get; init; }

	public double HeadA { get; init; }

	public double HeadB { get; init; }

	public double HeadC { get; init; }
}
