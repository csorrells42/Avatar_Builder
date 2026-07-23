namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction;

public sealed class MediaPipeNormalizedFaceVertex
{
	public int Index { get; init; }

	public double X { get; init; }

	public double Y { get; init; }

	public double Z { get; init; }

	public long DirectObservationCount { get; init; }

	public long RejectedHiddenObservationCount { get; init; }

	public double AngularCoverageDegrees { get; init; }

	public double ResidualPercent { get; init; }

	public double ConfidencePercent { get; init; }

	public string EvidenceClass { get; init; } = "underconstrained";
}
