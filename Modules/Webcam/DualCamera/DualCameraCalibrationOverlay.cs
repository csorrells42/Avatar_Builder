using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Webcam.DirectX12;

namespace AvatarBuilder.Modules.Webcam.DualCamera;

internal sealed record DualCameraCalibrationOverlay(DateTime CapturedAtUtc, IReadOnlyList<PreviewOverlayPoint> Points, IReadOnlyList<PreviewOverlayEdge> Edges);
