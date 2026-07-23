namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class MeshTopologyEdge
{
	public int FromIndex { get; init; }

	public int ToIndex { get; init; }

	public string Role { get; init; } = "";

	public string Source { get; init; } = "";

	public double LengthPercent { get; init; }

	public double ConfidencePercent { get; init; }
}
