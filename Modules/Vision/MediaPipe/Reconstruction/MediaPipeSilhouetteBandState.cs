namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction;

public readonly record struct MediaPipeSilhouetteBandState
{
	public int BandIndex { get; init; }

	public long ObservationCount { get; init; }

	public double MinimumSupportMean { get; init; }

	public double MaximumSupportMean { get; init; }

	public double MinimumSupportM2 { get; init; }

	public double MaximumSupportM2 { get; init; }
}
