using System.Collections.Generic;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed record PreviewOverlayMesh(IReadOnlyList<PreviewOverlayPoint> Points, IReadOnlyList<PreviewOverlayEdge> Edges, IReadOnlyList<PreviewOverlayIndexedPath> FeaturePaths, IReadOnlyList<bool> FeaturePointMask, bool DrawPoints = true);
