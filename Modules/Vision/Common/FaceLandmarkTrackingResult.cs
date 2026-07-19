namespace AvatarBuilder.Modules.Vision.Common;

public sealed class FaceLandmarkTrackingResult
{
    public static FaceLandmarkTrackingResult None { get; } = new();

    public bool HasFace => LandmarkFrame.HasFace || FeatureDetection.HasFace;

    public string BackendName { get; init; } = "";

    public string BackendStatus { get; init; } = "waiting";

    public FaceFeatureDetection FeatureDetection { get; init; } = FaceFeatureDetection.None;

    public FaceLandmarkFrame LandmarkFrame { get; init; } = FaceLandmarkFrame.None;
}
