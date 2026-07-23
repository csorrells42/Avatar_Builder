using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Webcam.DirectX12;

namespace AvatarBuilder.Modules.Webcam.DualCamera;

internal sealed record DualCameraRegistrationFrame(DateTime TargetCapturedAtUtc, IReadOnlyList<PreviewOverlayPoint> TranslatedPartnerPoints, IReadOnlyList<PreviewOverlayPoint> FusedPoints, IReadOnlyList<DualCameraRigPoint> TriangulatedRigPoints, int TargetOwnedPointCount, int PartnerOwnedPointCount);
