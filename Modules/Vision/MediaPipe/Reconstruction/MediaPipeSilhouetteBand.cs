namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction;

public sealed class MediaPipeSilhouetteBand
{
	public int BandIndex { get; init; }

	public double CanonicalY { get; init; }

	public long ObservationCount { get; init; }

	public double MinimumSupport { get; init; }

	public double MaximumSupport { get; init; }

	public double StandardDeviation { get; init; }
}
