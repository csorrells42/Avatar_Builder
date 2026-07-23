namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class ReconstructionInputFaceBox
{
	public double Left { get; init; }

	public double Top { get; init; }

	public double Right { get; init; }

	public double Bottom { get; init; }

	public bool Normalized { get; init; } = true;

	public double Confidence { get; init; } = 1.0;
}
