using System;

namespace AvatarBuilder.Modules.Vision.Diagnostics;

public sealed class MediaPipeConvergenceMarker
{
	public DateTime CapturedAtUtc { get; init; }

	public string Event { get; init; } = "";

	public string Detail { get; init; } = "";
}
