namespace AvatarBuilder.Modules.Webcam.DualCamera;

internal sealed record DualCameraDenseStereoSource(DualCameraObservation CameraA, DualCameraObservation CameraB, DualCameraCalibrationModel Calibration);
