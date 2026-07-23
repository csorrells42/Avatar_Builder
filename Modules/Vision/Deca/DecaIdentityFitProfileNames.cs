namespace AvatarBuilder.Modules.Vision.Deca;

internal static class DecaIdentityFitProfileNames
{
	public const string Flame68 = "flame-68";

	public const string MediaPipeSurfaceAssisted = "mediapipe-surface-assisted";

	public static string ToProtocolValue(DecaIdentityFitProfile profile)
	{
		if (profile == DecaIdentityFitProfile.MediaPipeSurfaceAssisted)
		{
			return "mediapipe-surface-assisted";
		}
		return "flame-68";
	}
}
