using System.Collections.Generic;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed record PreviewOverlayDiagnosticMesh(IReadOnlyList<PreviewOverlayPoint> Points, IReadOnlyList<PreviewOverlayEdge> Edges, PreviewOverlayDiagnosticMeshRole Role, bool DrawPoints = true);
