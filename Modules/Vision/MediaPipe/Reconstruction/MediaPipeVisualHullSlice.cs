using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction;

public sealed class MediaPipeVisualHullSlice
{
	public int BandIndex { get; init; }

	public double CanonicalY { get; init; }

	public double ConfidencePercent { get; init; }

	public double AngularCoverageDegrees { get; init; }

	public int SupportingAngleCount { get; init; }

	public IReadOnlyList<MediaPipeVisualHullPoint> Boundary { get; init; } = Array.Empty<MediaPipeVisualHullPoint>();
}
