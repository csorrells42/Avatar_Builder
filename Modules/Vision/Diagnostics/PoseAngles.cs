namespace AvatarBuilder.Modules.Vision.Diagnostics;

public sealed record PoseAngles(double A, double B, double C)
{
	public bool IsFinite
	{
		get
		{
			if (double.IsFinite(A) && double.IsFinite(B))
			{
				return double.IsFinite(C);
			}
			return false;
		}
	}
}
