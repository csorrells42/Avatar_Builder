using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.Reconstruction.Warping;

internal sealed class DenseFaceWarpControlDocument
{
	public int SparseLandmarkIndex { get; init; }

	public int MediaPipeLandmarkIndex { get; init; }

	public string Role { get; init; } = "";

	public double Confidence { get; init; }

	public IReadOnlyList<double> Source { get; init; } = Array.Empty<double>();

	public IReadOnlyList<double> Target { get; init; } = Array.Empty<double>();

	public static DenseFaceWarpControlDocument Create(DenseFaceWarpControlPoint control)
	{
		return new DenseFaceWarpControlDocument
		{
			SparseLandmarkIndex = control.SparseLandmarkIndex,
			MediaPipeLandmarkIndex = control.MediaPipeLandmarkIndex,
			Role = control.Role,
			Confidence = control.Confidence,
			Source =
			[
				control.Source.X,
				control.Source.Y,
				control.Source.Z
			],
			Target =
			[
				control.Target.X,
				control.Target.Y,
				control.Target.Z
			]
		};
	}
}
