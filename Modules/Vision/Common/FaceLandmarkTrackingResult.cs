using AvatarBuilder.Modules.Vision.Diagnostics;

namespace AvatarBuilder.Modules.Vision.Common;

public sealed class FaceLandmarkTrackingResult
{
	public static FaceLandmarkTrackingResult None { get; } = new FaceLandmarkTrackingResult();

	public bool HasFace
	{
		get
		{
			if (!LandmarkFrame.HasFace)
			{
				return FeatureDetection.HasFace;
			}
			return true;
		}
	}

	public string BackendName { get; init; } = "";

	public string BackendStatus { get; init; } = "waiting";

	public FaceFeatureDetection FeatureDetection { get; init; } = FaceFeatureDetection.None;

	public FaceLandmarkFrame LandmarkFrame { get; init; } = FaceLandmarkFrame.None;

	public VisionPipelineDiagnostics Diagnostics { get; init; } = VisionPipelineDiagnostics.None;
}
