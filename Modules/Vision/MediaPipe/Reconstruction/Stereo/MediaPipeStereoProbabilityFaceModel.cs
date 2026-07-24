using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public sealed class MediaPipeStereoProbabilityFaceModel
{
	public string SubjectId { get; init; } = "";

	public string SubjectDisplayName { get; init; } = "";

	public string CalibrationId { get; init; } = "";

	public DateTime BuiltAtUtc { get; init; }

	public DateTime EvidenceUpdatedAtUtc { get; init; }

	public long SourceObservationCount { get; init; }

	public int SourceBinCount { get; init; }

	public int RepeatedSourceBinCount { get; init; }

	public double SurfaceCellSizeInches { get; init; }

	public double FaceWidthInches { get; init; }

	public double FaceHeightInches { get; init; }

	public double FaceDepthInches { get; init; }

	public double MeanConfidencePercent { get; init; }

	public string Status { get; init; } = "Repeated stereo evidence is still gathering.";

	public IReadOnlyList<MediaPipeStereoProbabilityFaceVertex> Vertices { get; init; } = Array.Empty<MediaPipeStereoProbabilityFaceVertex>();

	public IReadOnlyList<MediaPipeStereoProbabilityFaceTriangle> Triangles { get; init; } = Array.Empty<MediaPipeStereoProbabilityFaceTriangle>();

	public IReadOnlyList<MediaPipeStereoProbabilityFaceVertex> SmoothedVertices { get; init; } = Array.Empty<MediaPipeStereoProbabilityFaceVertex>();

	public IReadOnlyList<MediaPipeStereoProbabilityFaceTriangle> SmoothedTriangles { get; init; } = Array.Empty<MediaPipeStereoProbabilityFaceTriangle>();

	public bool HasSurface
	{
		get
		{
			if (Vertices.Count >= 100)
			{
				return Triangles.Count >= 100;
			}
			return false;
		}
	}

	public bool HasSmoothedSurface
	{
		get
		{
			if (SmoothedVertices.Count >= 100)
			{
				return SmoothedTriangles.Count >= 100;
			}
			return false;
		}
	}
}
