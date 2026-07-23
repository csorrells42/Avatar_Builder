namespace AvatarBuilder.Modules.Vision.Diagnostics;

public sealed class MediaPipeLandmarkVariance
{
	public int Index { get; init; }

	public long SampleCount { get; init; }

	public double RmsDeviationPercent { get; init; }
}
