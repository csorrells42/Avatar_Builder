using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Deca.StandardModel;

public sealed record AvatarStandardPoseSample
{
	public string DirectionKey { get; init; } = "";

	public string DisplayName { get; init; } = "";

	public string ObservationId { get; init; } = "";

	public DateTime CapturedAtUtc { get; init; }

	public double ARotationAroundXDegrees { get; init; }

	public double BRotationAroundYDegrees { get; init; }

	public double CRotationAroundZDegrees { get; init; }

	public double MeasuredFitPercent { get; init; }

	public double CoefficientDeltaRms { get; init; }

	public int SourceFrameWidthPixels { get; init; }

	public int SourceFrameHeightPixels { get; init; }

	public IReadOnlyList<FaceMeshLandmarkPoint> MediaPipeLandmarks { get; init; } = Array.Empty<FaceMeshLandmarkPoint>();

	public IReadOnlyList<double> IdentityShapeCoefficients { get; init; } = Array.Empty<double>();

	public IReadOnlyList<FaceMeshLandmarkPoint> CanonicalIdentityVertices { get; init; } = Array.Empty<FaceMeshLandmarkPoint>();
}
