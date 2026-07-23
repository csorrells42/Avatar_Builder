using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public sealed class MediaPipeStereoFaceModel
{
	public const string CurrentSchemaVersion = "calibrated-stereo-face-v1";

	public static MediaPipeStereoFaceModel Empty { get; } = new MediaPipeStereoFaceModel();

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

	public double BaselineInches { get; init; }

	public double ConfidentVertexPercent { get; init; }

	public double MedianVertexDeviationInches { get; init; }

	public double FaceWidthInches { get; init; }

	public double FaceHeightInches { get; init; }

	public double MeasuredDepthInches { get; init; }

	public double DenseStableVertexPercent { get; init; }

	public int DenseMeasuredVertexCount { get; init; }

	public int DenseMaximumVertexCount { get; init; }

	public long RawTriangulatedObservationCount { get; init; }

	public int RawPointBinCount { get; init; }

	public long RawMaximumBinObservationCount { get; init; }

	public long RawUnstoredObservationCount { get; init; }

	public string Status { get; init; } = "Calibrated stereo reconstruction is waiting for synchronized face measurements.";

	public IReadOnlyList<MediaPipeStereoFaceVertex> Vertices { get; init; } = Array.Empty<MediaPipeStereoFaceVertex>();

	public IReadOnlyList<MeshTopologyEdge> TopologyEdges { get; init; } = Array.Empty<MeshTopologyEdge>();

	public IReadOnlyList<MediaPipeStereoDenseFaceVertex> DenseVertices { get; init; } = Array.Empty<MediaPipeStereoDenseFaceVertex>();

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
