using System;

namespace AvatarBuilder.Modules.Webcam.DualCamera;

internal sealed record DualCameraCalibrationFrame(DateTime CapturedAtUtc, int Width, int Height, int Stride, byte[] BgraPixels);
