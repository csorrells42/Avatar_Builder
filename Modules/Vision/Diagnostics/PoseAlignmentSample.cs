using System;

namespace AvatarBuilder.Modules.Vision.Diagnostics;

public sealed class PoseAlignmentSample
{
	public DateTime CapturedAtUtc { get; init; }

	public int PoseConventionVersion { get; init; } = 2;

	public double MediaPipeA { get; init; }

	public double MediaPipeB { get; init; }

	public double MediaPipeC { get; init; }

	public double ThreeDdfaA { get; init; }

	public double ThreeDdfaB { get; init; }

	public double ThreeDdfaC { get; init; }
}
