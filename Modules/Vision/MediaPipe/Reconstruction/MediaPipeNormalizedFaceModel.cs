using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction;

public sealed class MediaPipeNormalizedFaceModel
{
	public static MediaPipeNormalizedFaceModel Empty { get; } = new MediaPipeNormalizedFaceModel();

	public string SubjectId { get; init; } = "";

	public string SubjectDisplayName { get; init; } = "";

	public DateTime UpdatedAtUtc { get; init; }

	public long AcceptedFrameCount { get; init; }

	public long RejectedFrameCount { get; init; }

	public long DirectLandmarkObservationCount { get; init; }

	public long HiddenLandmarkRejectionCount { get; init; }

	public long SilhouetteObservationCount { get; init; }

	public double MinimumARotationDegrees { get; init; }

	public double MaximumARotationDegrees { get; init; }

	public double MinimumBRotationDegrees { get; init; }

	public double MaximumBRotationDegrees { get; init; }

	public double MinimumCRotationDegrees { get; init; }

	public double MaximumCRotationDegrees { get; init; }

	public double AngularBinSizeDegrees { get; init; } = 5.0;

	public double ConfidentVertexPercent { get; init; }

	public double MedianResidualPercent { get; init; }

	public string Status { get; init; } = "MediaPipe visible-evidence geometry is waiting for tracked frames.";

	public IReadOnlyList<MediaPipeNormalizedFaceVertex> Vertices { get; init; } = Array.Empty<MediaPipeNormalizedFaceVertex>();

	public IReadOnlyList<MeshTopologyEdge> TopologyEdges { get; init; } = Array.Empty<MeshTopologyEdge>();

	public IReadOnlyList<MediaPipeSilhouetteAngleProfile> SilhouetteProfiles { get; init; } = Array.Empty<MediaPipeSilhouetteAngleProfile>();

	public IReadOnlyList<MediaPipeVisualHullSlice> VisualHullSlices { get; init; } = Array.Empty<MediaPipeVisualHullSlice>();

	public bool HasGeometry
	{
		get
		{
			if (AcceptedFrameCount > 0)
			{
				return Vertices.Count >= 468;
			}
			return false;
		}
	}
}
