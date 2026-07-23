namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public sealed class MediaPipeStereoRawPointBinState
{
	public int BinX { get; init; }

	public int BinY { get; init; }

	public int BinZ { get; init; }

	public double MeanXInches { get; init; }

	public double MeanYInches { get; init; }

	public double MeanZInches { get; init; }

	public long ObservationCount { get; init; }

	public long AcceptedObservationCount { get; init; }
}
