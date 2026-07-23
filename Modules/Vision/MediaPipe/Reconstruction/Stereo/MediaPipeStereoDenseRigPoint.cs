namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public readonly record struct MediaPipeStereoDenseRigPoint(int SampleIndex, int TriangleIndex, double XInches, double YInches, double ZInches, double ReprojectionResidualPercent, double MatchErrorPixels, bool IsExpressionSurface);
