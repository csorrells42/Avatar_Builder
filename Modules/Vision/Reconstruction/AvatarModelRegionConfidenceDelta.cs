namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarModelRegionConfidenceDelta
{
	public string Region { get; init; } = "";

	public double ConfidencePercent { get; init; }

	public double DeltaPoints { get; init; }
}
