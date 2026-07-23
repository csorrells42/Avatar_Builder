namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed record ApertureImageQuality(double GlareRatio, double ContrastScore, double SharpnessScore)
{
	public static ApertureImageQuality None { get; } = new ApertureImageQuality(0.0, 0.0, 0.0);
}
