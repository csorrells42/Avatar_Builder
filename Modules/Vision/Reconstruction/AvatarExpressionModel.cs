using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarExpressionModel
{
	public int SampleCount { get; init; }

	public double ConfidencePercent { get; init; }

	public int ExpressionCoefficientCount { get; init; }

	public double ExpressionEnergyPercent { get; init; }

	public List<double> MeanExpressionCoefficients { get; init; } = new List<double>();

	public List<AvatarCoefficientRange> ExpressionRanges { get; init; } = new List<AvatarCoefficientRange>();

	public List<AvatarExpressionBucket> Buckets { get; init; } = new List<AvatarExpressionBucket>();
}
