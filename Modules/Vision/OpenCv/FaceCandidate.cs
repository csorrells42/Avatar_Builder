using OpenCvSharp;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed record FaceCandidate(Rect Face, string Source, YuNetFaceDetection? YuNetFace = null, double DetectorScore = 0.5);
