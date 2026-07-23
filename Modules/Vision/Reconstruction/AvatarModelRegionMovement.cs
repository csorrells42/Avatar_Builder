namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarModelRegionMovement
{
	public string Region { get; init; } = "";

	public int MatchedVertexCount { get; init; }

	public double RmsFaceSpanPercent { get; init; }
}
