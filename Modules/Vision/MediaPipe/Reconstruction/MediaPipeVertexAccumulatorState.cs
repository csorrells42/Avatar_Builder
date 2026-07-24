namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction;

public readonly record struct MediaPipeVertexAccumulatorState
{
	public int Index { get; init; }

	public double M00 { get; init; }

	public double M01 { get; init; }

	public double M02 { get; init; }

	public double M11 { get; init; }

	public double M12 { get; init; }

	public double M22 { get; init; }

	public double B0 { get; init; }

	public double B1 { get; init; }

	public double B2 { get; init; }

	public double SumMeasurementSquares { get; init; }

	public double TotalWeight { get; init; }

	public long DirectObservationCount { get; init; }

	public long RejectedHiddenObservationCount { get; init; }

	public double MinimumYawDegrees { get; init; }

	public double MaximumYawDegrees { get; init; }

	public double MinimumPitchDegrees { get; init; }

	public double MaximumPitchDegrees { get; init; }
}
