namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarRegionConfidence
{
	public string Region { get; init; } = "";

	public double ConfidencePercent { get; init; }

	public string Basis { get; init; } = "";
}
