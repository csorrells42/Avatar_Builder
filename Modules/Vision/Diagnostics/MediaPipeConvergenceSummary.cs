using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.Diagnostics;

public sealed class MediaPipeConvergenceSummary
{
	public string SessionId { get; init; } = "";

	public string SessionReason { get; init; } = "";

	public DateTime SessionStartedAtUtc { get; init; }

	public DateTime UpdatedAtUtc { get; init; }

	public double ElapsedSeconds { get; init; }

	public long TotalFrames { get; init; }

	public long FaceFrames { get; init; }

	public long MissingFaceFrames { get; init; }

	public long AuditFramesReplaced { get; init; }

	public double FaceLockPercent { get; init; }

	public int RetainedSampleCount { get; init; }

	public double FirstMinuteStableRmsPercent { get; init; } = double.NaN;

	public double RecentMinuteStableRmsPercent { get; init; } = double.NaN;

	public double RecentMinuteAllRmsPercent { get; init; } = double.NaN;

	public double RecentMinuteScreenMotionPercent { get; init; } = double.NaN;

	public double RecentMinutePoseMotionDegrees { get; init; } = double.NaN;

	public double RecentMinuteAppCorrectionPercent { get; init; } = double.NaN;

	public double ConvergenceImprovementPercent { get; init; } = double.NaN;

	public string State { get; init; } = "Waiting";

	public string Interpretation { get; init; } = "Turn on MediaPipe tracking to begin.";

	public IReadOnlyList<MediaPipeLandmarkVariance> MostVariableLandmarks { get; init; } = Array.Empty<MediaPipeLandmarkVariance>();
}
