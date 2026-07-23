namespace AvatarBuilder.Modules.Vision.Diagnostics;

public sealed class PoseAxisAlignment
{
	public string Name { get; init; } = "";

	public int SampleCount { get; init; }

	public double Scale { get; init; }

	public double OffsetDegrees { get; init; }

	public double Correlation { get; init; }

	public double MediaPipeRangeDegrees { get; init; }

	public double ThreeDdfaRangeDegrees { get; init; }

	public double MeanAbsoluteErrorDegrees { get; init; }

	public double P95AbsoluteErrorDegrees { get; init; }

	public bool Ready { get; init; }

	public string Status { get; init; } = "waiting";
}
