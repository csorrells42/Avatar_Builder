using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public sealed class MediaPipeStereoFaceState
{
	public string SchemaVersion { get; init; } = "calibrated-stereo-face-v1";

	public string SubjectId { get; init; } = "";

	public string SubjectDisplayName { get; init; } = "";

	public string CalibrationId { get; init; } = "";

	public DateTime UpdatedAtUtc { get; init; }

	public long AcceptedFrameCount { get; init; }

	public long RejectedFrameCount { get; init; }

	public long DirectObservationCount { get; init; }

	public long RejectedPointCount { get; init; }

	public long DenseObservationCount { get; init; }

	public long RejectedDensePointCount { get; init; }

	public long RawTriangulatedObservationCount { get; init; }

	public long RawUnstoredObservationCount { get; init; }

	public double BaselineInches { get; init; }

	public IReadOnlyList<MediaPipeStereoVertexAccumulatorState> VertexAccumulators { get; init; } = Array.Empty<MediaPipeStereoVertexAccumulatorState>();

	public IReadOnlyList<MediaPipeStereoDenseVertexAccumulatorState> DenseVertexAccumulators { get; init; } = Array.Empty<MediaPipeStereoDenseVertexAccumulatorState>();

	public IReadOnlyList<MediaPipeStereoRawPointBinState> RawPointBins { get; init; } = Array.Empty<MediaPipeStereoRawPointBinState>();
}
