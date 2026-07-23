using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarIdentityMappingUpdate
{
	public bool Accepted { get; init; }

	public string Status { get; init; } = "Multi-frame identity mapping did not run.";

	public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;

	public int FrameCount { get; init; }

	public int IterationCount { get; init; }

	public double InitialLandmarkRmsePercent { get; init; }

	public double FinalLandmarkRmsePercent { get; init; }

	public double ImprovementPercent { get; init; }

	public double GenericIdentityDisplacementPercent { get; init; }

	public IReadOnlyList<double> ShapeCoefficients { get; init; } = Array.Empty<double>();

	public IReadOnlyList<FaceMeshLandmarkPoint> CanonicalIdentityVertices { get; init; } = Array.Empty<FaceMeshLandmarkPoint>();
}
