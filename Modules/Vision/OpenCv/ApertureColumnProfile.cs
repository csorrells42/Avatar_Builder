namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed record ApertureColumnProfile(double AverageHeight, double MedianHeight, int SampleCount, double CoverageRatio, double CenterY)
{
	public static ApertureColumnProfile None { get; } = new ApertureColumnProfile(0.0, 0.0, 0, 0.0, 0.0);
}
