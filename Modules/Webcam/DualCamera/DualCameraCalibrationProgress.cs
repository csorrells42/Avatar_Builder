namespace AvatarBuilder.Modules.Webcam.DualCamera;

internal sealed record DualCameraCalibrationProgress(string Status, int AcceptedPairCount, int RequiredPairCount, bool CameraABoardFound, bool CameraBBoardFound, bool Completed, DualCameraCalibrationModel? Calibration = null);
