using System;

namespace AvatarBuilder.Modules.Vision.Deca;

public sealed record DecaPinnedStillConvergenceOptions
{
	public int MaximumPasses { get; init; } = 96;

	public int StablePassesRequired { get; init; } = 3;

	public double CoefficientDeltaThreshold { get; init; } = 0.001;

	public int OptimizationIterationsPerPass { get; init; } = 12;

	public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(8L);
}
