namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarModelConvergence
{
	public double ScorePercent { get; init; }

	public double SampleAdequacyPercent { get; init; }

	public double QualityPercent { get; init; }

	public bool IsMatureCandidate { get; init; }

	public string Label { get; init; } = "waiting";

	public string Basis { get; init; } = "Waiting for ranked reconstruction observations.";
}
