namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public readonly record struct MediaPipeStereoVertexAccumulatorState
{
	public int Index { get; init; }

	public double TotalWeight { get; init; }

	public double MeanXInches { get; init; }

	public double MeanYInches { get; init; }

	public double MeanZInches { get; init; }

	public double M2X { get; init; }

	public double M2Y { get; init; }

	public double M2Z { get; init; }

	public double WeightedResidualSum { get; init; }

	public long DirectObservationCount { get; init; }

	public long RejectedObservationCount { get; init; }
}
