namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public readonly record struct MediaPipeStereoRigLandmark(int Index, double XInches, double YInches, double ZInches, bool IsValid, double ReprojectionResidualPercent, double CameraADirectnessRatio, double CameraBDirectnessRatio, bool IsDirectlyMeasured);
