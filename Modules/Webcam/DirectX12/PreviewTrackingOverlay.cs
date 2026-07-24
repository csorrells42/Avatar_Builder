using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed record PreviewTrackingOverlay
{
	public static PreviewTrackingOverlay Empty { get; } = new PreviewTrackingOverlay();

	public PreviewOverlayRect? FaceBox { get; init; }

	public PreviewOverlayPolyline? FaceContour { get; init; }

	public PreviewOverlayPolyline? JawContour { get; init; }

	public PreviewOverlayPolyline? LeftEyeContour { get; init; }

	public PreviewOverlayPolyline? RightEyeContour { get; init; }

	public PreviewOverlayPolyline? LeftBrowContour { get; init; }

	public PreviewOverlayPolyline? RightBrowContour { get; init; }

	public PreviewOverlayPolyline? OuterLipContour { get; init; }

	public PreviewOverlayPolyline? InnerLipContour { get; init; }

	public PreviewOverlayMesh? FaceMesh { get; init; }

	public IReadOnlyList<PreviewOverlayDiagnosticMesh> DiagnosticMeshes { get; init; } = Array.Empty<PreviewOverlayDiagnosticMesh>();

	public long SourceTimestamp { get; init; }

	public TimeSpan MaximumAge { get; init; }

	public bool IsFresh
	{
		get
		{
			return MaximumAge <= TimeSpan.Zero
				|| (SourceTimestamp != 0L && Stopwatch.GetElapsedTime(SourceTimestamp) <= MaximumAge);
		}
	}

	public bool HasContent
	{
		get
		{
			if (!FaceBox.HasValue && (object)FaceContour == null && (object)JawContour == null && (object)LeftEyeContour == null && (object)RightEyeContour == null && (object)LeftBrowContour == null && (object)RightBrowContour == null && (object)OuterLipContour == null && (object)InnerLipContour == null && (object)FaceMesh == null)
			{
				return DiagnosticMeshes.Count > 0;
			}
			return true;
		}
	}
}
