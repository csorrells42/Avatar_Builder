namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarPoseCoverage
{
	public int TotalSampleCount { get; init; }

	public int FrontSampleCount { get; init; }

	public int LeftBTurnSampleCount { get; init; }

	public int RightBTurnSampleCount { get; init; }

	public int NegativeATiltSampleCount { get; init; }

	public int PositiveATiltSampleCount { get; init; }

	public int NegativeCTiltSampleCount { get; init; }

	public int PositiveCTiltSampleCount { get; init; }

	public int CloseZSampleCount { get; init; }

	public int FarZSampleCount { get; init; }

	public double ARangeDegrees { get; init; }

	public double BRangeDegrees { get; init; }

	public double CRangeDegrees { get; init; }

	public double ZScaleRangePercent { get; init; }

	public double CoveragePercent { get; init; }

	public string Summary { get; init; } = "waiting";
}
