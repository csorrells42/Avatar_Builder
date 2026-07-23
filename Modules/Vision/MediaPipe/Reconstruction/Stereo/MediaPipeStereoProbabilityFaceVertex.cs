namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public sealed class MediaPipeStereoProbabilityFaceVertex
{
	public int Index { get; init; }

	public int GridX { get; init; }

	public int GridY { get; init; }

	public double XInches { get; init; }

	public double YInches { get; init; }

	public double ZInches { get; init; }

	public long ObservationCount { get; init; }

	public double AcceptedRatio { get; init; }

	public double DepthDominanceRatio { get; init; }

	public double ConfidencePercent { get; init; }

	public bool IsInterpolated { get; init; }
}
