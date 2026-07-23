namespace AvatarBuilder.Modules.Webcam.DualCamera;

internal readonly record struct DualCameraRigPoint(double XInches, double YInches, double ZInches, bool IsValid, double ReprojectionResidualPercent, double CameraADirectnessRatio, double CameraBDirectnessRatio, bool IsDirectlyMeasured);
