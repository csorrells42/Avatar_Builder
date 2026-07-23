using OpenCvSharp;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed record YuNetCueBoxes(Rect LeftEye, Rect RightEye, Rect Mouth);
