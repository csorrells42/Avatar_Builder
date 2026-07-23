using System.Collections.Generic;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed record PreviewOverlayPolyline(IReadOnlyList<PreviewOverlayPoint> Points, bool Closed, bool Inferred = false);
