using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction;
using AvatarBuilder.Modules.Vision.Onnx;

namespace AvatarBuilder.Modules.Vision.Reconstruction.Warping;

public static class ThreeDdfaMediaPipeWarpInputFactory
{
	private sealed record LandmarkMapping(int SparseIndex, int MediaPipeIndex, string Role);

	private readonly record struct CanonicalBasis(Vector3 Origin, Vector3 XAxis, Vector3 DownAxis, Vector3 ForwardAxis, float EyeSpan)
	{
		public static CanonicalBasis Create(Vector3 eyeA, Vector3 eyeB, Vector3 chin, Vector3 nose)
		{
			Vector3 vector = (eyeA + eyeB) * 0.5f;
			Vector3 value = eyeB - eyeA;
			float num = value.Length();
			if (num <= 1E-06f)
			{
				throw new InvalidOperationException("Cannot normalize a face whose eye centers overlap.");
			}
			Vector3 vector2 = Vector3.Normalize(value);
			Vector3 vector3 = chin - vector;
			vector3 -= Vector3.Dot(vector3, vector2) * vector2;
			if (vector3.LengthSquared() <= 1E-10f)
			{
				throw new InvalidOperationException("Cannot normalize a face without a distinct eye-to-chin axis.");
			}
			Vector3 vector4 = Vector3.Normalize(vector3);
			Vector3 vector5 = Vector3.Normalize(Vector3.Cross(vector2, vector4));
			if (Vector3.Dot(nose - vector, vector5) < 0f)
			{
				vector5 = -vector5;
			}
			return new CanonicalBasis(vector, vector2, vector4, vector5, num);
		}

		public Vector3 Transform(Vector3 vertex)
		{
			Vector3 vector = vertex - Origin;
			return new Vector3(Vector3.Dot(vector, XAxis) / EyeSpan, (0f - Vector3.Dot(vector, DownAxis)) / EyeSpan, Vector3.Dot(vector, ForwardAxis) / EyeSpan);
		}
	}

	private static readonly LandmarkMapping[] LandmarkMappings = CreateLandmarkMappings();

	private static readonly int[] SourceEyeA = new int[6] { 36, 37, 38, 39, 40, 41 };

	private static readonly int[] SourceEyeB = new int[6] { 42, 43, 44, 45, 46, 47 };

	private static readonly int[] TargetEyeA = new int[6] { 33, 160, 158, 133, 153, 144 };

	private static readonly int[] TargetEyeB = new int[6] { 362, 385, 387, 263, 373, 380 };

	public static DenseFaceWarpInput Create(ThreeDdfaOnnxSidecarResponse source, MediaPipeNormalizedFaceModel target, string subjectId, string subjectDisplayName, DateTime createdAtUtc)
	{
		ArgumentNullException.ThrowIfNull(source, "source");
		ArgumentNullException.ThrowIfNull(target, "target");
		if (!source.Ok || !source.HasFace)
		{
			throw new InvalidOperationException("3DDFA did not produce a usable face: " + source.Status);
		}
		if (source.CanonicalIdentityVertices.Count < 30000)
		{
			throw new InvalidOperationException($"Dense warping requires the full 3DDFA identity mesh; {source.CanonicalIdentityVertices.Count:n0} vertices were returned.");
		}
		if (source.CanonicalSparseLandmarks.Count < 68)
		{
			throw new InvalidOperationException($"Dense warping requires 68 canonical 3DDFA landmarks; {source.CanonicalSparseLandmarks.Count:n0} were returned.");
		}
		if (!target.HasGeometry)
		{
			throw new InvalidOperationException("MediaPipe has not accumulated enough directly observed geometry yet.");
		}
		Vector3[] array = source.CanonicalSparseLandmarks.OrderBy((ThreeDdfaOnnxSidecarVertex vertex) => vertex.Index).Select(ToVector).ToArray();
		Dictionary<int, MediaPipeNormalizedFaceVertex> dictionary = target.Vertices.ToDictionary((MediaPipeNormalizedFaceVertex vertex) => vertex.Index);
		CanonicalBasis sourceBasis = CanonicalBasis.Create(Average(array, SourceEyeA), Average(array, SourceEyeB), array[8], array[30]);
		CanonicalBasis canonicalBasis = CanonicalBasis.Create(Average(dictionary, TargetEyeA), Average(dictionary, TargetEyeB), GetVector(dictionary, 152), GetVector(dictionary, 1));
		Vector3[] array2 = array.Select(((CanonicalBasis)sourceBasis).Transform).ToArray();
		DenseFaceWarpVertex[] normalizedSourceVertices = (from vertex in source.CanonicalIdentityVertices
			orderby vertex.Index
			select ToDenseVertex(vertex.Index, sourceBasis.Transform(ToVector(vertex)))).ToArray();
		MediaPipeNormalizedFaceVertex[] measuredSourceVertices = target.Vertices.OrderBy((MediaPipeNormalizedFaceVertex vertex) => vertex.Index).ToArray();
		DenseFaceWarpVertex[] measuredVertices = measuredSourceVertices.Select((MediaPipeNormalizedFaceVertex vertex, int position) => ToDenseVertex(position, canonicalBasis.Transform(ToVector(vertex)))).ToArray();
		double[] measuredConfidences = measuredSourceVertices.Select((MediaPipeNormalizedFaceVertex vertex) => Math.Clamp(vertex.ConfidencePercent / 100.0, 0.0, 1.0)).ToArray();
		Dictionary<int, int> measuredPositions = measuredSourceVertices.Select((MediaPipeNormalizedFaceVertex vertex, int position) => new { vertex.Index, position }).ToDictionary(item => item.Index, item => item.position);
		MeshTopologyEdge[] measuredTopologyEdges = (from edge in target.TopologyEdges
			where measuredPositions.ContainsKey(edge.FromIndex) && measuredPositions.ContainsKey(edge.ToIndex)
			select new MeshTopologyEdge
			{
				FromIndex = measuredPositions[edge.FromIndex],
				ToIndex = measuredPositions[edge.ToIndex],
				Role = edge.Role,
				Source = "MediaPipe measured topology",
				LengthPercent = edge.LengthPercent,
				ConfidencePercent = edge.ConfidencePercent
			}).ToArray();
		List<DenseFaceWarpControlPoint> list = new List<DenseFaceWarpControlPoint>(LandmarkMappings.Length);
		LandmarkMapping[] landmarkMappings = LandmarkMappings;
		foreach (LandmarkMapping landmarkMapping in landmarkMappings)
		{
			if (dictionary.TryGetValue(landmarkMapping.MediaPipeIndex, out var value) && value.DirectObservationCount > 0 && !string.Equals(value.EvidenceClass, "underconstrained", StringComparison.Ordinal) && !string.Equals(value.EvidenceClass, "expression-only", StringComparison.Ordinal))
			{
				double num2 = CalculateConfidence(value, landmarkMapping.Role);
				if (!(num2 < 0.05))
				{
					list.Add(new DenseFaceWarpControlPoint
					{
						SparseLandmarkIndex = landmarkMapping.SparseIndex,
						MediaPipeLandmarkIndex = landmarkMapping.MediaPipeIndex,
						Role = landmarkMapping.Role,
						Confidence = num2,
						InfluenceRadius = InfluenceRadius(landmarkMapping.Role),
						Source = ToDenseVertex(landmarkMapping.SparseIndex, array2[landmarkMapping.SparseIndex]),
						Target = ToDenseVertex(landmarkMapping.MediaPipeIndex, canonicalBasis.Transform(ToVector(value)))
					});
				}
			}
		}
		return new DenseFaceWarpInput
		{
			SubjectId = subjectId,
			SubjectDisplayName = subjectDisplayName,
			CreatedAtUtc = createdAtUtc,
			SourceVertices = normalizedSourceVertices,
			MeasuredVertices = measuredVertices,
			MeasuredConfidences = measuredConfidences,
			TopologyEdges = (from edge in source.DenseEdges
				where edge.FromIndex >= 0 && edge.ToIndex >= 0 && edge.FromIndex < normalizedSourceVertices.Length && edge.ToIndex < normalizedSourceVertices.Length
				select new MeshTopologyEdge
				{
					FromIndex = edge.FromIndex,
					ToIndex = edge.ToIndex,
					Role = "dense-surface",
					Source = "3DDFA-V2 identity topology"
				}).ToArray(),
			MeasuredTopologyEdges = measuredTopologyEdges,
			ControlPoints = list
		};
	}

	private static double CalculateConfidence(MediaPipeNormalizedFaceVertex vertex, string role)
	{
		double num = Math.Clamp(vertex.ConfidencePercent / 100.0, 0.0, 1.0);
		double num2 = 1.0 - Math.Exp((double)(-vertex.DirectObservationCount) / 20.0);
		double num3 = Math.Clamp(vertex.AngularCoverageDegrees / 60.0, 0.25, 1.0);
		double num4 = (string.Equals(vertex.EvidenceClass, "directly-measured", StringComparison.Ordinal) ? 1.0 : 0.65);
		bool flag = ((role == "eye" || role == "mouth") ? true : false);
		double num5 = (flag ? 0.58 : 1.0);
		return num * num2 * num3 * num4 * num5;
	}

	private static double InfluenceRadius(string role)
	{
		return role switch
		{
			"jaw" => 0.88, 
			"brow" => 0.56, 
			"nose" => 0.52, 
			"eye" => 0.44, 
			"mouth" => 0.5, 
			_ => 0.55, 
		};
	}

	private static LandmarkMapping[] CreateLandmarkMappings()
	{
		List<LandmarkMapping> list = new List<LandmarkMapping>(68);
		Add(list, 0, "jaw", new int[17]
		{
			234, 93, 132, 58, 172, 136, 150, 176, 152, 400,
			379, 365, 397, 288, 361, 323, 454
		});
		Add(list, 17, "brow", new int[10] { 70, 63, 105, 66, 107, 336, 296, 334, 293, 300 });
		Add(list, 27, "nose", new int[9] { 168, 6, 195, 1, 98, 97, 2, 326, 327 });
		Add(list, 36, "eye", new int[12]
		{
			33, 160, 158, 133, 153, 144, 362, 385, 387, 263,
			373, 380
		});
		Add(list, 48, "mouth", new int[20]
		{
			61, 40, 37, 0, 267, 270, 291, 321, 314, 17,
			84, 91, 78, 81, 13, 311, 308, 402, 14, 178
		});
		return list.ToArray();
	}

	private static void Add(List<LandmarkMapping> mappings, int sparseStart, string role, int[] mediaPipeIndices)
	{
		for (int i = 0; i < mediaPipeIndices.Length; i++)
		{
			mappings.Add(new LandmarkMapping(sparseStart + i, mediaPipeIndices[i], role));
		}
	}

	private static Vector3 Average(Vector3[] vertices, int[] indices)
	{
		Vector3 zero = Vector3.Zero;
		foreach (int num in indices)
		{
			zero += vertices[num];
		}
		return zero / indices.Length;
	}

	private static Vector3 Average(IReadOnlyDictionary<int, MediaPipeNormalizedFaceVertex> vertices, int[] indices)
	{
		Vector3 zero = Vector3.Zero;
		foreach (int index in indices)
		{
			zero += GetVector(vertices, index);
		}
		return zero / indices.Length;
	}

	private static Vector3 GetVector(IReadOnlyDictionary<int, MediaPipeNormalizedFaceVertex> vertices, int index)
	{
			if (!vertices.TryGetValue(index, out MediaPipeNormalizedFaceVertex? value))
		{
			throw new InvalidOperationException($"MediaPipe geometry is missing required landmark {index}.");
		}
		return ToVector(value);
	}

	private static Vector3 ToVector(ThreeDdfaOnnxSidecarVertex vertex)
	{
		return new Vector3((float)vertex.X, (float)vertex.Y, (float)vertex.Z);
	}

	private static Vector3 ToVector(MediaPipeNormalizedFaceVertex vertex)
	{
		return new Vector3((float)vertex.X, (float)vertex.Y, (float)vertex.Z);
	}

	private static DenseFaceWarpVertex ToDenseVertex(int index, Vector3 vertex)
	{
		return new DenseFaceWarpVertex
		{
			Index = index,
			X = vertex.X,
			Y = vertex.Y,
			Z = vertex.Z
		};
	}
}
