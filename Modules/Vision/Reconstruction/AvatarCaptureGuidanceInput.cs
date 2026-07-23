using AvatarBuilder.Modules.Vision.Personalization;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarCaptureGuidanceInput
{
	public bool UserLoggedIn { get; set; }

	public bool AvatarLearningRequested { get; set; }

	public bool CameraActive { get; set; }

	public bool FaceLocked { get; set; }

	public AvatarCaptureQualityAssessment CaptureQuality { get; set; } = AvatarCaptureQualityAssessment.Waiting;
}
