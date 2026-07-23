namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public sealed class MediaPipeStereoDenseFaceVertex
{
	public int SampleIndex { get; init; }

	public int TriangleIndex { get; init; }

	public double XInches { get; init; }

	public double YInches { get; init; }

	public double ZInches { get; init; }

	public long DirectObservationCount { get; init; }

	public long RejectedObservationCount { get; init; }

	public double StandardDeviationInches { get; init; }

	public double MeanReprojectionResidualPercent { get; init; }

	public double ConfidencePercent { get; init; }

	public bool IsExpressionSurface { get; init; }

	public string EvidenceClass { get; init; } = "underconstrained";
}
