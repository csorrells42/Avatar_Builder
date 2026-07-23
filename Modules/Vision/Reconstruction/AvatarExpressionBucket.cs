namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarExpressionBucket
{
	public string Name { get; init; } = "";

	public int SampleCount { get; init; }

	public double AverageEnergyPercent { get; init; }

	public string Meaning { get; init; } = "";
}
