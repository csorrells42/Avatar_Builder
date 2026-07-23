using System;
using System.Collections.Generic;
using System.Linq;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public static class MediaPipeStereoProbabilityFaceBuilder
{
	private readonly record struct GridKey(int X, int Y);

	private sealed class SurfaceCell(GridKey key)
	{
		private readonly Dictionary<int, DepthCluster> _depthClusters = new Dictionary<int, DepthCluster>();

		private long _totalAcceptedVotes;

		public GridKey Key { get; } = key;

		public void Add(MediaPipeStereoRawPointBinState bin)
		{
			int key = Quantize(bin.MeanZInches, 0.08);
			if (!_depthClusters.TryGetValue(key, out DepthCluster value))
			{
				value = new DepthCluster();
				_depthClusters.Add(key, value);
			}
			value.Add(bin);
			_totalAcceptedVotes += bin.AcceptedObservationCount;
		}

		public bool TryCreateCandidate(out CandidateVertex candidate)
		{
			candidate = null;
			if (_depthClusters.Count == 0 || _totalAcceptedVotes < 3)
			{
				return false;
			}
			int num = 0;
			double num2 = double.NegativeInfinity;
			foreach (int key in _depthClusters.Keys)
			{
				double num3 = ScoreDepthNeighborhood(key);
				if (num3 > num2)
				{
					num2 = num3;
					num = key;
				}
			}
			DepthCluster depthCluster = new DepthCluster();
			for (int i = -1; i <= 1; i++)
			{
				if (_depthClusters.TryGetValue(num + i, out DepthCluster value))
				{
					depthCluster.Add(value);
				}
			}
			if (depthCluster.AcceptedVotes < 3)
			{
				return false;
			}
			double num4 = (double)depthCluster.AcceptedVotes / (double)Math.Max(1L, depthCluster.ObservationCount);
			double num5 = (double)depthCluster.AcceptedVotes / (double)Math.Max(1L, _totalAcceptedVotes);
			double confidence = Math.Clamp((1.0 - Math.Exp((double)(-depthCluster.AcceptedVotes) / 12.0)) * (0.35 + 0.65 * num4) * (0.45 + 0.55 * Math.Sqrt(num5)), 0.0, 1.0);
			candidate = new CandidateVertex(Key, depthCluster.MeanX, depthCluster.MeanY, depthCluster.MeanZ, depthCluster.ObservationCount, num4, num5, confidence);
			return true;
		}

		private double ScoreDepthNeighborhood(int center)
		{
			long num = 0L;
			long num2 = 0L;
			for (int i = -1; i <= 1; i++)
			{
				if (_depthClusters.TryGetValue(center + i, out DepthCluster value))
				{
					num += value.ObservationCount;
					num2 += value.AcceptedVotes;
				}
			}
			double num3 = (double)num2 / (double)Math.Max(1L, num);
			return (double)num2 * (0.35 + 0.65 * num3);
		}
	}

	private sealed class DepthCluster
	{
		private double _weightedX;

		private double _weightedY;

		private double _weightedZ;

		public long ObservationCount { get; private set; }

		public long AcceptedVotes { get; private set; }

		public double MeanX
		{
			get
			{
				if (AcceptedVotes != 0L)
				{
					return _weightedX / (double)AcceptedVotes;
				}
				return 0.0;
			}
		}

		public double MeanY
		{
			get
			{
				if (AcceptedVotes != 0L)
				{
					return _weightedY / (double)AcceptedVotes;
				}
				return 0.0;
			}
		}

		public double MeanZ
		{
			get
			{
				if (AcceptedVotes != 0L)
				{
					return _weightedZ / (double)AcceptedVotes;
				}
				return 0.0;
			}
		}

		public void Add(MediaPipeStereoRawPointBinState bin)
		{
			long num = Math.Max(1L, bin.AcceptedObservationCount);
			ObservationCount += bin.ObservationCount;
			AcceptedVotes += bin.AcceptedObservationCount;
			_weightedX += bin.MeanXInches * (double)num;
			_weightedY += bin.MeanYInches * (double)num;
			_weightedZ += bin.MeanZInches * (double)num;
		}

		public void Add(DepthCluster cluster)
		{
			ObservationCount += cluster.ObservationCount;
			AcceptedVotes += cluster.AcceptedVotes;
			_weightedX += cluster._weightedX;
			_weightedY += cluster._weightedY;
			_weightedZ += cluster._weightedZ;
		}
	}

	private sealed class CandidateVertex(GridKey key, double x, double y, double z, long observationCount, double acceptedRatio, double depthDominanceRatio, double confidence, bool isInterpolated = false)
	{
		public GridKey Key { get; } = key;

		public double X { get; } = x;

		public double Y { get; } = y;

		public double Z { get; set; } = z;

		public long ObservationCount { get; } = observationCount;

		public double AcceptedRatio { get; } = acceptedRatio;

		public double DepthDominanceRatio { get; } = depthDominanceRatio;

		public double Confidence { get; } = confidence;

		public bool IsInterpolated { get; } = isInterpolated;

		public CandidateVertex Clone()
		{
			return new CandidateVertex(Key, X, Y, Z, ObservationCount, AcceptedRatio, DepthDominanceRatio, Confidence, IsInterpolated);
		}
	}

	private const double SurfaceCellSizeInches = 0.12;

	private const double DepthClusterSizeInches = 0.08;

	private const long MinimumAcceptedVotes = 3L;

	private const double MinimumAcceptedRatio = 0.55;

	private const double MaximumNeighborDepthStepInches = 0.55;

	private const int SurfaceSmoothingPasses = 7;

	public static MediaPipeStereoProbabilityFaceModel Build(MediaPipeStereoFaceState state, MediaPipeStereoFaceModel sourceModel)
	{
		ArgumentNullException.ThrowIfNull(state, "state");
		ArgumentNullException.ThrowIfNull(sourceModel, "sourceModel");
		MediaPipeStereoRawPointBinState[] array = state.RawPointBins.Where(IsUsableBin).ToArray();
		if (array.Length == 0)
		{
			return Empty(state, sourceModel, "No repeated accepted stereo evidence is available yet.");
		}
		MediaPipeStereoRawPointBinState[] array2 = array.Where((MediaPipeStereoRawPointBinState bin) => bin.AcceptedObservationCount >= 2).ToArray();
		if (array2.Length == 0)
		{
			array2 = array;
		}
		double num = WeightedQuantile(array2, (MediaPipeStereoRawPointBinState bin) => bin.MeanXInches, 0.01);
		double num2 = WeightedQuantile(array2, (MediaPipeStereoRawPointBinState bin) => bin.MeanXInches, 0.99);
		double num3 = WeightedQuantile(array2, (MediaPipeStereoRawPointBinState bin) => bin.MeanYInches, 0.01);
		double num4 = WeightedQuantile(array2, (MediaPipeStereoRawPointBinState bin) => bin.MeanYInches, 0.99);
		if (!double.IsFinite(num) || !double.IsFinite(num2) || !double.IsFinite(num3) || !double.IsFinite(num4) || num2 <= num || num4 <= num3)
		{
			return Empty(state, sourceModel, "Repeated evidence does not yet define a finite face volume.");
		}
		Dictionary<GridKey, SurfaceCell> dictionary = new Dictionary<GridKey, SurfaceCell>();
		MediaPipeStereoRawPointBinState[] array3 = array;
		foreach (MediaPipeStereoRawPointBinState mediaPipeStereoRawPointBinState in array3)
		{
			if (!(mediaPipeStereoRawPointBinState.MeanXInches < num) && !(mediaPipeStereoRawPointBinState.MeanXInches > num2) && !(mediaPipeStereoRawPointBinState.MeanYInches < num3) && !(mediaPipeStereoRawPointBinState.MeanYInches > num4))
			{
				GridKey key = new GridKey(Quantize(mediaPipeStereoRawPointBinState.MeanXInches, 0.12), Quantize(mediaPipeStereoRawPointBinState.MeanYInches, 0.12));
				if (!dictionary.TryGetValue(key, out var value))
				{
					value = new SurfaceCell(key);
					dictionary.Add(key, value);
				}
				value.Add(mediaPipeStereoRawPointBinState);
			}
		}
		Dictionary<GridKey, CandidateVertex> dictionary2 = new Dictionary<GridKey, CandidateVertex>();
		foreach (KeyValuePair<GridKey, SurfaceCell> item5 in dictionary)
		{
			if (item5.Value.TryCreateCandidate(out CandidateVertex candidate))
			{
				dictionary2.Add(item5.Key, candidate);
			}
		}
		RemoveIsolatedCandidates(dictionary2);
		SmoothLowConfidenceDepth(dictionary2);
		(MediaPipeStereoProbabilityFaceVertex[] Vertices, Dictionary<GridKey, int> Indices) tuple = CreateVertices(dictionary2);
		MediaPipeStereoProbabilityFaceVertex[] item = tuple.Vertices;
		Dictionary<GridKey, int> item2 = tuple.Indices;
		IReadOnlyList<MediaPipeStereoProbabilityFaceTriangle> readOnlyList = CreateTriangles(item, item2);
		(MediaPipeStereoProbabilityFaceVertex[] Vertices, Dictionary<GridKey, int> Indices) tuple2 = CreateVertices(CreateSmoothedSurface(dictionary2));
		MediaPipeStereoProbabilityFaceVertex[] item3 = tuple2.Vertices;
		Dictionary<GridKey, int> item4 = tuple2.Indices;
		IReadOnlyList<MediaPipeStereoProbabilityFaceTriangle> readOnlyList2 = CreateTriangles(item3, item4);
		double meanConfidencePercent = ((item.Length == 0) ? 0.0 : item.Average((MediaPipeStereoProbabilityFaceVertex vertex) => vertex.ConfidencePercent));
		string status = ((readOnlyList.Count == 0) ? "Repeated evidence produced points, but neighboring depth modes do not yet form a continuous surface." : $"Built the most likely face surface from {item.Length:n0} measured depth modes; the derived skin contains {item3.Length:n0} vertices and {readOnlyList2.Count:n0} triangles.");
		MediaPipeStereoProbabilityFaceVertex[] values = ((item3.Length == 0) ? item : item3);
		return new MediaPipeStereoProbabilityFaceModel
		{
			SubjectId = state.SubjectId,
			SubjectDisplayName = state.SubjectDisplayName,
			CalibrationId = state.CalibrationId,
			BuiltAtUtc = DateTime.UtcNow,
			EvidenceUpdatedAtUtc = state.UpdatedAtUtc,
			SourceObservationCount = state.RawPointBins.Sum((MediaPipeStereoRawPointBinState bin) => bin.ObservationCount),
			SourceBinCount = state.RawPointBins.Count,
			RepeatedSourceBinCount = array.Length,
			SurfaceCellSizeInches = 0.12,
			FaceWidthInches = Extent(values, (MediaPipeStereoProbabilityFaceVertex vertex) => vertex.XInches),
			FaceHeightInches = Extent(values, (MediaPipeStereoProbabilityFaceVertex vertex) => vertex.YInches),
			FaceDepthInches = Extent(values, (MediaPipeStereoProbabilityFaceVertex vertex) => vertex.ZInches),
			MeanConfidencePercent = meanConfidencePercent,
			Status = status,
			Vertices = item,
			Triangles = readOnlyList,
			SmoothedVertices = item3,
			SmoothedTriangles = readOnlyList2
		};
	}

	private static (MediaPipeStereoProbabilityFaceVertex[] Vertices, Dictionary<GridKey, int> Indices) CreateVertices(IReadOnlyDictionary<GridKey, CandidateVertex> candidates)
	{
		CandidateVertex[] array = (from candidate in candidates.Values
			orderby candidate.Key.Y, candidate.Key.X
			select candidate).ToArray();
		MediaPipeStereoProbabilityFaceVertex[] array2 = new MediaPipeStereoProbabilityFaceVertex[array.Length];
		Dictionary<GridKey, int> dictionary = new Dictionary<GridKey, int>(array.Length);
		for (int num = 0; num < array.Length; num++)
		{
			CandidateVertex candidateVertex = array[num];
			dictionary.Add(candidateVertex.Key, num);
			array2[num] = new MediaPipeStereoProbabilityFaceVertex
			{
				Index = num,
				GridX = candidateVertex.Key.X,
				GridY = candidateVertex.Key.Y,
				XInches = candidateVertex.X,
				YInches = candidateVertex.Y,
				ZInches = candidateVertex.Z,
				ObservationCount = candidateVertex.ObservationCount,
				AcceptedRatio = candidateVertex.AcceptedRatio,
				DepthDominanceRatio = candidateVertex.DepthDominanceRatio,
				ConfidencePercent = candidateVertex.Confidence * 100.0,
				IsInterpolated = candidateVertex.IsInterpolated
			};
		}
		return (Vertices: array2, Indices: dictionary);
	}

	private static Dictionary<GridKey, CandidateVertex> CreateSmoothedSurface(IReadOnlyDictionary<GridKey, CandidateVertex> measured)
	{
		Dictionary<GridKey, CandidateVertex> dictionary = measured.ToDictionary<KeyValuePair<GridKey, CandidateVertex>, GridKey, CandidateVertex>((KeyValuePair<GridKey, CandidateVertex> pair) => pair.Key, (KeyValuePair<GridKey, CandidateVertex> pair) => pair.Value.Clone());
		FillSmallInteriorHoles(dictionary);
		for (int num = 0; num < 7; num++)
		{
			BilateralSmoothDepth(dictionary);
		}
		return dictionary;
	}

	private static void FillSmallInteriorHoles(Dictionary<GridKey, CandidateVertex> surface)
	{
		if (surface.Count < 8)
		{
			return;
		}
		for (int i = 0; i < 2; i++)
		{
			List<CandidateVertex> list = new List<CandidateVertex>();
			int num = surface.Keys.Min((GridKey key) => key.X);
			int num2 = surface.Keys.Max((GridKey key) => key.X);
			int num3 = surface.Keys.Min((GridKey key) => key.Y);
			int num4 = surface.Keys.Max((GridKey key) => key.Y);
			for (int num5 = num3 + 1; num5 < num4; num5++)
			{
				for (int num6 = num + 1; num6 < num2; num6++)
				{
					GridKey gridKey = new GridKey(num6, num5);
					if (surface.ContainsKey(gridKey))
					{
						continue;
					}
					CandidateVertex[] array = GetNeighbors(surface, gridKey).ToArray();
					bool flag = surface.ContainsKey(new GridKey(num6 - 1, num5)) && surface.ContainsKey(new GridKey(num6 + 1, num5));
					bool flag2 = surface.ContainsKey(new GridKey(num6, num5 - 1)) && surface.ContainsKey(new GridKey(num6, num5 + 1));
					if (array.Length < 5 || (!flag && !flag2))
					{
						continue;
					}
					double num7 = array.Min((CandidateVertex neighbor) => neighbor.Z);
					if (!(array.Max((CandidateVertex neighbor) => neighbor.Z) - num7 > 0.55))
					{
						double num8 = array.Sum((CandidateVertex neighbor) => 0.2 + neighbor.Confidence);
						double z = array.Sum((CandidateVertex neighbor) => neighbor.Z * (0.2 + neighbor.Confidence)) / num8;
						double confidence = array.Average((CandidateVertex neighbor) => neighbor.Confidence) * 0.62;
						list.Add(new CandidateVertex(gridKey, (double)num6 * 0.12, (double)num5 * 0.12, z, 0L, array.Average((CandidateVertex neighbor) => neighbor.AcceptedRatio), array.Average((CandidateVertex neighbor) => neighbor.DepthDominanceRatio), confidence, isInterpolated: true));
					}
				}
			}
			foreach (CandidateVertex item in list)
			{
				surface.TryAdd(item.Key, item);
			}
			if (list.Count == 0)
			{
				break;
			}
		}
	}

	private static IEnumerable<CandidateVertex> GetNeighbors(IReadOnlyDictionary<GridKey, CandidateVertex> surface, GridKey center)
	{
		for (int y = -1; y <= 1; y++)
		{
			for (int x = -1; x <= 1; x++)
			{
				if ((x != 0 || y != 0) && surface.TryGetValue(new GridKey(center.X + x, center.Y + y), out CandidateVertex value))
				{
					yield return value;
				}
			}
		}
	}

	private static void BilateralSmoothDepth(Dictionary<GridKey, CandidateVertex> surface)
	{
		List<(GridKey, double)> list = new List<(GridKey, double)>(surface.Count);
		foreach (KeyValuePair<GridKey, CandidateVertex> item in surface)
		{
			double num = item.Value.Z * 3.5;
			double num2 = 3.5;
			foreach (CandidateVertex neighbor in GetNeighbors(surface, item.Key))
			{
				double num3 = neighbor.Z - item.Value.Z;
				if (!(Math.Abs(num3) > 0.55))
				{
					double num4 = Math.Exp((0.0 - num3 * num3) / 0.08000000000000002) * (0.2 + neighbor.Confidence);
					num += neighbor.Z * num4;
					num2 += num4;
				}
			}
			double num5 = num / num2;
			double num6 = (item.Value.IsInterpolated ? 0.48 : (0.08 + (1.0 - item.Value.Confidence) * 0.2));
			list.Add((item.Key, item.Value.Z * (1.0 - num6) + num5 * num6));
		}
		foreach (var item2 in list)
		{
			surface[item2.Item1].Z = item2.Item2;
		}
	}

	private static bool IsUsableBin(MediaPipeStereoRawPointBinState bin)
	{
		if (bin.ObservationCount <= 0 || bin.AcceptedObservationCount < 3 || !double.IsFinite(bin.MeanXInches) || !double.IsFinite(bin.MeanYInches) || !double.IsFinite(bin.MeanZInches))
		{
			return false;
		}
		return (double)bin.AcceptedObservationCount / (double)bin.ObservationCount >= 0.55;
	}

	private static MediaPipeStereoProbabilityFaceModel Empty(MediaPipeStereoFaceState state, MediaPipeStereoFaceModel sourceModel, string status)
	{
		return new MediaPipeStereoProbabilityFaceModel
		{
			SubjectId = state.SubjectId,
			SubjectDisplayName = state.SubjectDisplayName,
			CalibrationId = state.CalibrationId,
			BuiltAtUtc = DateTime.UtcNow,
			EvidenceUpdatedAtUtc = state.UpdatedAtUtc,
			SourceObservationCount = state.RawPointBins.Sum((MediaPipeStereoRawPointBinState bin) => bin.ObservationCount),
			SourceBinCount = state.RawPointBins.Count,
			RepeatedSourceBinCount = 0,
			FaceWidthInches = sourceModel.FaceWidthInches,
			FaceHeightInches = sourceModel.FaceHeightInches,
			FaceDepthInches = sourceModel.MeasuredDepthInches,
			Status = status
		};
	}

	private static int Quantize(double value, double size)
	{
		return checked((int)Math.Round(value / size, MidpointRounding.AwayFromZero));
	}

	private static double WeightedQuantile(IReadOnlyList<MediaPipeStereoRawPointBinState> bins, Func<MediaPipeStereoRawPointBinState, double> selector, double quantile)
	{
		MediaPipeStereoRawPointBinState[] array = bins.OrderBy(selector).ToArray();
		long num = array.Sum((MediaPipeStereoRawPointBinState bin) => Math.Max(1L, bin.AcceptedObservationCount));
		if (num <= 0)
		{
			return double.NaN;
		}
		double num2 = Math.Clamp(quantile, 0.0, 1.0) * (double)num;
		long num3 = 0L;
		MediaPipeStereoRawPointBinState[] array2 = array;
		foreach (MediaPipeStereoRawPointBinState mediaPipeStereoRawPointBinState in array2)
		{
			num3 += Math.Max(1L, mediaPipeStereoRawPointBinState.AcceptedObservationCount);
			if ((double)num3 >= num2)
			{
				return selector(mediaPipeStereoRawPointBinState);
			}
		}
		return selector(array[^1]);
	}

	private static void RemoveIsolatedCandidates(Dictionary<GridKey, CandidateVertex> candidates)
	{
		if (candidates.Count < 4)
		{
			candidates.Clear();
			return;
		}
		List<GridKey> list = new List<GridKey>();
		foreach (KeyValuePair<GridKey, CandidateVertex> candidate in candidates)
		{
			int num = 0;
			for (int i = -1; i <= 1; i++)
			{
				for (int j = -1; j <= 1; j++)
				{
					if ((j != 0 || i != 0) && candidates.ContainsKey(new GridKey(candidate.Key.X + j, candidate.Key.Y + i)))
					{
						num++;
					}
				}
			}
			if (num < 2)
			{
				list.Add(candidate.Key);
			}
		}
		foreach (GridKey item in list)
		{
			candidates.Remove(item);
		}
	}

	private static void SmoothLowConfidenceDepth(Dictionary<GridKey, CandidateVertex> candidates)
	{
		List<(GridKey, double)> list = new List<(GridKey, double)>();
		foreach (KeyValuePair<GridKey, CandidateVertex> candidate in candidates)
		{
			double num = 0.0;
			double num2 = 0.0;
			for (int i = -1; i <= 1; i++)
			{
				for (int j = -1; j <= 1; j++)
				{
					if (candidates.TryGetValue(new GridKey(candidate.Key.X + j, candidate.Key.Y + i), out CandidateVertex value) && !(Math.Abs(value.Z - candidate.Value.Z) > 0.55))
					{
						double num3 = ((j == 0 && i == 0) ? 2.0 : ((j == 0 || i == 0) ? 1.0 : 0.7)) * (0.25 + value.Confidence);
						num += value.Z * num3;
						num2 += num3;
					}
				}
			}
			if (!(num2 <= 0.0))
			{
				double num4 = num / num2;
				double num5 = 0.05 + (1.0 - candidate.Value.Confidence) * 0.17;
				list.Add((candidate.Key, candidate.Value.Z * (1.0 - num5) + num4 * num5));
			}
		}
		foreach (var item in list)
		{
			candidates[item.Item1].Z = item.Item2;
		}
	}

	private static IReadOnlyList<MediaPipeStereoProbabilityFaceTriangle> CreateTriangles(IReadOnlyList<MediaPipeStereoProbabilityFaceVertex> vertices, IReadOnlyDictionary<GridKey, int> indices)
	{
		List<MediaPipeStereoProbabilityFaceTriangle> list = new List<MediaPipeStereoProbabilityFaceTriangle>(vertices.Count * 2);
		foreach (KeyValuePair<GridKey, int> index in indices)
		{
			int value = index.Value;
			int value2;
			bool flag = indices.TryGetValue(new GridKey(index.Key.X + 1, index.Key.Y), out value2);
			int value3;
			bool flag2 = indices.TryGetValue(new GridKey(index.Key.X, index.Key.Y + 1), out value3);
			int value4;
			bool flag3 = indices.TryGetValue(new GridKey(index.Key.X + 1, index.Key.Y + 1), out value4);
			if (flag && flag2 && flag3)
			{
				double num = Math.Abs(vertices[value].ZInches - vertices[value4].ZInches);
				double num2 = Math.Abs(vertices[value2].ZInches - vertices[value3].ZInches);
				if (num <= num2)
				{
					AddTriangle(list, vertices, value, value2, value4);
					AddTriangle(list, vertices, value, value4, value3);
				}
				else
				{
					AddTriangle(list, vertices, value, value2, value3);
					AddTriangle(list, vertices, value2, value4, value3);
				}
			}
			else if (flag && flag2)
			{
				AddTriangle(list, vertices, value, value2, value3);
			}
			else if (flag && flag3)
			{
				AddTriangle(list, vertices, value, value2, value4);
			}
			else if (flag2 && flag3)
			{
				AddTriangle(list, vertices, value, value4, value3);
			}
		}
		return list;
	}

	private static void AddTriangle(ICollection<MediaPipeStereoProbabilityFaceTriangle> triangles, IReadOnlyList<MediaPipeStereoProbabilityFaceVertex> vertices, int a, int b, int c)
	{
		if (IsContinuous(vertices[a], vertices[b]) && IsContinuous(vertices[b], vertices[c]) && IsContinuous(vertices[c], vertices[a]) && !(Math.Abs((vertices[b].XInches - vertices[a].XInches) * (vertices[c].YInches - vertices[a].YInches) - (vertices[b].YInches - vertices[a].YInches) * (vertices[c].XInches - vertices[a].XInches)) < 1E-06))
		{
			triangles.Add(new MediaPipeStereoProbabilityFaceTriangle
			{
				A = a,
				B = b,
				C = c
			});
		}
	}

	private static bool IsContinuous(MediaPipeStereoProbabilityFaceVertex first, MediaPipeStereoProbabilityFaceVertex second)
	{
		double num = first.XInches - second.XInches;
		double num2 = first.YInches - second.YInches;
		if (Math.Sqrt(num * num + num2 * num2) <= 0.216)
		{
			return Math.Abs(first.ZInches - second.ZInches) <= 0.55;
		}
		return false;
	}

	private static double Extent<T>(IReadOnlyList<T> values, Func<T, double> selector)
	{
		if (values.Count == 0)
		{
			return 0.0;
		}
		double num = double.PositiveInfinity;
		double num2 = double.NegativeInfinity;
		foreach (T value in values)
		{
			double val = selector(value);
			num = Math.Min(num, val);
			num2 = Math.Max(num2, val);
		}
		if (!double.IsFinite(num) || !double.IsFinite(num2))
		{
			return 0.0;
		}
		return num2 - num;
	}
}
