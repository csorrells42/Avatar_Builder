namespace AvatarBuilder.Modules.Vision.Reconstruction.Warping;

public sealed class DenseFaceWarpControlPoint
{
	public int SparseLandmarkIndex { get; init; }

	public int MediaPipeLandmarkIndex { get; init; }

	public string Role { get; init; } = "surface";

	public double Confidence { get; init; }

	public double InfluenceRadius { get; init; }

	public DenseFaceWarpVertex Source { get; init; } = new DenseFaceWarpVertex();

	public DenseFaceWarpVertex Target { get; init; } = new DenseFaceWarpVertex();
}
