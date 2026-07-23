using System.Collections.Generic;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed record PreviewOverlayIndexedPath(IReadOnlyList<int> PointIndices, bool Closed, PreviewOverlayMeshFeatureRole Role);
