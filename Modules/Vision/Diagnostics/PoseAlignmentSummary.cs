using System;

namespace AvatarBuilder.Modules.Vision.Diagnostics;

public sealed class PoseAlignmentSummary
{
	public static PoseAlignmentSummary Waiting { get; } = new PoseAlignmentSummary();

	public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;

	public int PoseConventionVersion { get; init; } = 2;

	public int SampleCount { get; init; }

	public bool ReadyForComparison { get; init; }

	public string Status { get; init; } = "waiting for exact-frame A/B/C pairs";

	public string Guidance { get; init; } = "Start avatar capture with MediaPipe selected, then slowly move through A, B, and C.";

	public PoseAxisAlignment A { get; init; } = new PoseAxisAlignment
	{
		Name = "A around X"
	};

	public PoseAxisAlignment B { get; init; } = new PoseAxisAlignment
	{
		Name = "B around Y"
	};

	public PoseAxisAlignment C { get; init; } = new PoseAxisAlignment
	{
		Name = "C around Z"
	};
}
