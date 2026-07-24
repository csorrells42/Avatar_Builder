using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction;

public sealed class MediaPipeNormalizedFaceState
{
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

	public IReadOnlyList<MediaPipeVertexAccumulatorState> VertexAccumulators { get; init; } = Array.Empty<MediaPipeVertexAccumulatorState>();

	public IReadOnlyList<MediaPipeSilhouetteProfileState> SilhouetteAccumulators { get; init; } = Array.Empty<MediaPipeSilhouetteProfileState>();
}
