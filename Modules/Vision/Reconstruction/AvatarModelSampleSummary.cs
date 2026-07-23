using System;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarModelSampleSummary
{
	public string RequestId { get; init; } = "";

	public string SampleId { get; init; } = "";

	public DateTime CapturedAtUtc { get; init; }

	public double WeightPercent { get; init; }

	public double ReconstructionConfidencePercent { get; init; }

	public double SampleQualityPercent { get; init; }

	public double ARotationAroundXDegrees { get; init; }

	public double BRotationAroundYDegrees { get; init; }

	public double CRotationAroundZDegrees { get; init; }

	public int VertexCount { get; init; }

	public string IdentityUse { get; init; } = "";

	public string SourceImageUri { get; init; } = "";
}
