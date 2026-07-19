using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceFrameGeometryEstimatorInput
{
    public FaceLandmarkFrame Frame { get; init; } = FaceLandmarkFrame.None;

    public int? FrameWidthPixels { get; init; }

    public int? FrameHeightPixels { get; init; }

    public FaceFrameGeometryCalibration Calibration { get; init; } = FaceFrameGeometryCalibration.None;
}
