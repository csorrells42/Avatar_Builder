using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public sealed class MediaPipeStereoFaceReconstructor
{
	private readonly record struct CandidatePoint(int Index, Point3 Point, double Weight, double ResidualPercent);

	private readonly record struct RawPointBinKey(int X, int Y, int Z);

	private readonly record struct Point3(double X, double Y, double Z)
	{
		public bool IsFinite
		{
			get
			{
				if (double.IsFinite(X) && double.IsFinite(Y))
				{
					return double.IsFinite(Z);
				}
				return false;
			}
		}

		public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

		public Point3 Normalized
		{
			get
			{
				if (!(Length <= 1E-09))
				{
					return this * (1.0 / Length);
				}
				return default(Point3);
			}
		}

		public static Point3 operator +(Point3 left, Point3 right)
		{
			return new Point3(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
		}

		public static Point3 operator -(Point3 left, Point3 right)
		{
			return new Point3(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
		}

		public static Point3 operator *(Point3 point, double scalar)
		{
			return new Point3(point.X * scalar, point.Y * scalar, point.Z * scalar);
		}

		public static double Dot(Point3 left, Point3 right)
		{
			return left.X * right.X + left.Y * right.Y + left.Z * right.Z;
		}

		public static Point3 Cross(Point3 left, Point3 right)
		{
			return new Point3(left.Y * right.Z - left.Z * right.Y, left.Z * right.X - left.X * right.Z, left.X * right.Y - left.Y * right.X);
		}

		public static double Distance(Point3 left, Point3 right)
		{
			return (left - right).Length;
		}
	}

	private readonly record struct HeadBasis(Point3 Origin, Point3 XAxis, Point3 YAxis, Point3 ZAxis, double EyeSpanInches)
	{
		public Point3 ToLocal(double x, double y, double z)
		{
			Point3 left = new Point3(x, y, z) - Origin;
			return new Point3(Point3.Dot(left, XAxis), Point3.Dot(left, YAxis), Point3.Dot(left, ZAxis));
		}

		public static bool TryCreate(IReadOnlyList<MediaPipeStereoRigLandmark> points, out HeadBasis basis)
		{
			basis = default(HeadBasis);
			if (!TryMidpoint(points, 33, 133, out var midpoint) || !TryMidpoint(points, 263, 362, out var midpoint2) || !TryPoint(points, 10, out var point) || !TryPoint(points, 152, out var point2) || !TryPoint(points, 1, out var point3))
			{
				return false;
			}
			double num = Point3.Distance(midpoint, midpoint2);
			double num2 = Point3.Distance(point, point2);
			if (num < 1.2 || num > 4.5 || num2 < 3.5 || num2 > 10.0)
			{
				return false;
			}
			Point3 point4 = (midpoint + midpoint2) * 0.5;
			Point3 normalized = (midpoint2 - midpoint).Normalized;
			Point3 point5 = point - point2;
			Point3 normalized2 = (point5 - normalized * Point3.Dot(point5, normalized)).Normalized;
			Point3 normalized3 = Point3.Cross(normalized, normalized2).Normalized;
			if (normalized.Length < 0.99 || normalized2.Length < 0.99 || normalized3.Length < 0.99)
			{
				return false;
			}
			if (Point3.Dot(point3 - point4, normalized3) < 0.0)
			{
				normalized3 *= -1.0;
			}
			normalized2 = Point3.Cross(normalized3, normalized).Normalized;
			basis = new HeadBasis(point4, normalized, normalized2, normalized3, num);
			return true;
		}

		private static bool TryMidpoint(IReadOnlyList<MediaPipeStereoRigLandmark> points, int firstIndex, int secondIndex, out Point3 midpoint)
		{
			midpoint = default(Point3);
			if (!TryPoint(points, firstIndex, out var point) || !TryPoint(points, secondIndex, out var point2))
			{
				return false;
			}
			midpoint = (point + point2) * 0.5;
			return true;
		}

		private static bool TryPoint(IReadOnlyList<MediaPipeStereoRigLandmark> points, int index, out Point3 point)
		{
			point = default(Point3);
			if ((uint)index >= (uint)points.Count)
			{
				return false;
			}
			MediaPipeStereoRigLandmark mediaPipeStereoRigLandmark = points[index];
			if (!mediaPipeStereoRigLandmark.IsValid || !double.IsFinite(mediaPipeStereoRigLandmark.XInches) || !double.IsFinite(mediaPipeStereoRigLandmark.YInches) || !double.IsFinite(mediaPipeStereoRigLandmark.ZInches) || mediaPipeStereoRigLandmark.ReprojectionResidualPercent > 4.0)
			{
				return false;
			}
			point = new Point3(mediaPipeStereoRigLandmark.XInches, mediaPipeStereoRigLandmark.YInches, mediaPipeStereoRigLandmark.ZInches);
			return true;
		}
	}

	private sealed class VertexAccumulator
	{
		private int _index;

		private double _totalWeight;

		private Point3 _mean;

		private Point3 _m2;

		private double _weightedResidualSum;

		public long DirectObservationCount { get; private set; }

		public long RejectedObservationCount { get; private set; }

		private double StandardDeviation
		{
			get
			{
				if (!(_totalWeight <= 0.0))
				{
					return Math.Sqrt(Math.Max(0.0, (_m2.X + _m2.Y + _m2.Z) / (3.0 * _totalWeight)));
				}
				return 0.0;
			}
		}

		public VertexAccumulator(int index)
		{
			Reset(index);
		}

		public void Reset(int index)
		{
			_index = index;
			_totalWeight = 0.0;
			_mean = default(Point3);
			_m2 = default(Point3);
			_weightedResidualSum = 0.0;
			DirectObservationCount = 0L;
			RejectedObservationCount = 0L;
		}

		public void Restore(MediaPipeStereoVertexAccumulatorState state)
		{
			_index = state.Index;
			_totalWeight = Math.Max(0.0, state.TotalWeight);
			_mean = new Point3(state.MeanXInches, state.MeanYInches, state.MeanZInches);
			_m2 = new Point3(Math.Max(0.0, state.M2X), Math.Max(0.0, state.M2Y), Math.Max(0.0, state.M2Z));
			_weightedResidualSum = Math.Max(0.0, state.WeightedResidualSum);
			DirectObservationCount = Math.Max(0L, state.DirectObservationCount);
			RejectedObservationCount = Math.Max(0L, state.RejectedObservationCount);
		}

		public bool CanAccept(Point3 point, bool expressionOnly)
		{
			if (DirectObservationCount < 8 || _totalWeight <= 0.0)
			{
				return true;
			}
			double standardDeviation = StandardDeviation;
			double min = (expressionOnly ? 0.28 : 0.16);
			double max = (expressionOnly ? 0.65 : 0.45);
			double num = Math.Clamp(standardDeviation * 4.5, min, max);
			return Point3.Distance(point, _mean) <= num;
		}

		public void Add(Point3 point, double weight, double residualPercent)
		{
			double num = _totalWeight + weight;
			Point3 point2 = point - _mean;
			double num2 = weight / Math.Max(1E-09, num);
			Point3 point3 = _mean + point2 * num2;
			Point3 point4 = point - point3;
			_m2 += new Point3(weight * point2.X * point4.X, weight * point2.Y * point4.Y, weight * point2.Z * point4.Z);
			_mean = point3;
			_totalWeight = num;
			_weightedResidualSum += residualPercent * weight;
			DirectObservationCount++;
		}

		public void Reject()
		{
			RejectedObservationCount++;
		}

		public MediaPipeStereoFaceVertex CreateVertex(bool expressionOnly)
		{
			double standardDeviation = StandardDeviation;
			double num = Math.Min(1.0, (double)DirectObservationCount / 30.0);
			double num2 = Math.Exp((0.0 - standardDeviation) / (expressionOnly ? 0.3 : 0.12));
			double confidencePercent = Math.Clamp(num * num2 * 100.0, 0.0, 100.0);
			string evidenceClass = ((DirectObservationCount == 0L) ? "underconstrained" : (expressionOnly ? "expression-only" : ((DirectObservationCount >= 12 && standardDeviation <= 0.12) ? "directly-measured" : "partially-measured")));
			return new MediaPipeStereoFaceVertex
			{
				Index = _index,
				XInches = _mean.X,
				YInches = _mean.Y,
				ZInches = _mean.Z,
				DirectObservationCount = DirectObservationCount,
				RejectedObservationCount = RejectedObservationCount,
				StandardDeviationInches = standardDeviation,
				MeanReprojectionResidualPercent = ((_totalWeight <= 0.0) ? 0.0 : (_weightedResidualSum / _totalWeight)),
				ConfidencePercent = confidencePercent,
				EvidenceClass = evidenceClass
			};
		}

		public MediaPipeStereoVertexAccumulatorState CreateState()
		{
			return new MediaPipeStereoVertexAccumulatorState
			{
				Index = _index,
				TotalWeight = _totalWeight,
				MeanXInches = _mean.X,
				MeanYInches = _mean.Y,
				MeanZInches = _mean.Z,
				M2X = _m2.X,
				M2Y = _m2.Y,
				M2Z = _m2.Z,
				WeightedResidualSum = _weightedResidualSum,
				DirectObservationCount = DirectObservationCount,
				RejectedObservationCount = RejectedObservationCount
			};
		}
	}

	private sealed class DenseVertexAccumulator
	{
		private int _sampleIndex;

		private int _triangleIndex = -1;

		private bool _isExpressionSurface;

		private double _totalWeight;

		private Point3 _mean;

		private Point3 _m2;

		private double _weightedResidualSum;

		public long DirectObservationCount { get; private set; }

		public long RejectedObservationCount { get; private set; }

		public bool HasEvidence
		{
			get
			{
				if (DirectObservationCount > 0)
				{
					return _totalWeight > 0.0;
				}
				return false;
			}
		}

		private double StandardDeviation
		{
			get
			{
				if (!(_totalWeight <= 0.0))
				{
					return Math.Sqrt(Math.Max(0.0, (_m2.X + _m2.Y + _m2.Z) / (3.0 * _totalWeight)));
				}
				return 0.0;
			}
		}

		public DenseVertexAccumulator(int sampleIndex)
		{
			Reset(sampleIndex);
		}

		public void Reset(int sampleIndex)
		{
			_sampleIndex = sampleIndex;
			_triangleIndex = -1;
			_isExpressionSurface = false;
			_totalWeight = 0.0;
			_mean = default(Point3);
			_m2 = default(Point3);
			_weightedResidualSum = 0.0;
			DirectObservationCount = 0L;
			RejectedObservationCount = 0L;
		}

		public void Restore(MediaPipeStereoDenseVertexAccumulatorState state)
		{
			_sampleIndex = state.SampleIndex;
			_triangleIndex = state.TriangleIndex;
			_isExpressionSurface = state.IsExpressionSurface;
			_totalWeight = Math.Max(0.0, state.TotalWeight);
			_mean = new Point3(state.MeanXInches, state.MeanYInches, state.MeanZInches);
			_m2 = new Point3(Math.Max(0.0, state.M2X), Math.Max(0.0, state.M2Y), Math.Max(0.0, state.M2Z));
			_weightedResidualSum = Math.Max(0.0, state.WeightedResidualSum);
			DirectObservationCount = Math.Max(0L, state.DirectObservationCount);
			RejectedObservationCount = Math.Max(0L, state.RejectedObservationCount);
		}

		public bool CanAccept(Point3 point, bool expressionSurface)
		{
			if (DirectObservationCount < 8 || _totalWeight <= 0.0)
			{
				return true;
			}
			double standardDeviation = StandardDeviation;
			double min = (expressionSurface ? 0.3 : 0.14);
			double max = (expressionSurface ? 0.7 : 0.42);
			double num = Math.Clamp(standardDeviation * 4.5, min, max);
			return Point3.Distance(point, _mean) <= num;
		}

		public void Add(int triangleIndex, bool expressionSurface, Point3 point, double weight, double residualPercent)
		{
			_triangleIndex = triangleIndex;
			_isExpressionSurface = expressionSurface;
			double num = _totalWeight + weight;
			Point3 point2 = point - _mean;
			double num2 = weight / Math.Max(1E-09, num);
			Point3 point3 = _mean + point2 * num2;
			Point3 point4 = point - point3;
			_m2 += new Point3(weight * point2.X * point4.X, weight * point2.Y * point4.Y, weight * point2.Z * point4.Z);
			_mean = point3;
			_totalWeight = num;
			_weightedResidualSum += residualPercent * weight;
			DirectObservationCount++;
		}

		public void Reject()
		{
			RejectedObservationCount++;
		}

		public MediaPipeStereoDenseFaceVertex CreateVertex()
		{
			double standardDeviation = StandardDeviation;
			double num = Math.Min(1.0, (double)DirectObservationCount / 24.0);
			double num2 = Math.Exp((0.0 - standardDeviation) / (_isExpressionSurface ? 0.32 : 0.14));
			double confidencePercent = Math.Clamp(num * num2 * 100.0, 0.0, 100.0);
			string evidenceClass = (_isExpressionSurface ? "expression-only" : ((DirectObservationCount >= 10 && standardDeviation <= 0.14) ? "directly-measured" : "partially-measured"));
			return new MediaPipeStereoDenseFaceVertex
			{
				SampleIndex = _sampleIndex,
				TriangleIndex = _triangleIndex,
				XInches = _mean.X,
				YInches = _mean.Y,
				ZInches = _mean.Z,
				DirectObservationCount = DirectObservationCount,
				RejectedObservationCount = RejectedObservationCount,
				StandardDeviationInches = standardDeviation,
				MeanReprojectionResidualPercent = ((_totalWeight <= 0.0) ? 0.0 : (_weightedResidualSum / _totalWeight)),
				ConfidencePercent = confidencePercent,
				IsExpressionSurface = _isExpressionSurface,
				EvidenceClass = evidenceClass
			};
		}

		public MediaPipeStereoDenseVertexAccumulatorState CreateState()
		{
			return new MediaPipeStereoDenseVertexAccumulatorState
			{
				SampleIndex = _sampleIndex,
				TriangleIndex = _triangleIndex,
				IsExpressionSurface = _isExpressionSurface,
				TotalWeight = _totalWeight,
				MeanXInches = _mean.X,
				MeanYInches = _mean.Y,
				MeanZInches = _mean.Z,
				M2X = _m2.X,
				M2Y = _m2.Y,
				M2Z = _m2.Z,
				WeightedResidualSum = _weightedResidualSum,
				DirectObservationCount = DirectObservationCount,
				RejectedObservationCount = RejectedObservationCount
			};
		}
	}

	private sealed class RawPointBinAccumulator
	{
		private readonly RawPointBinKey _key;

		private Point3 _mean;

		public long ObservationCount { get; private set; }

		public long AcceptedObservationCount { get; private set; }

		public RawPointBinAccumulator(RawPointBinKey key)
		{
			_key = key;
		}

		public static RawPointBinAccumulator Restore(RawPointBinKey key, MediaPipeStereoRawPointBinState state)
		{
			return new RawPointBinAccumulator(key)
			{
				_mean = new Point3(state.MeanXInches, state.MeanYInches, state.MeanZInches),
				ObservationCount = Math.Max(0L, state.ObservationCount),
				AcceptedObservationCount = Math.Clamp(state.AcceptedObservationCount, 0L, Math.Max(0L, state.ObservationCount))
			};
		}

		public void Add(Point3 point, bool acceptedForModel)
		{
			ObservationCount++;
			double num = 1.0 / (double)ObservationCount;
			_mean += (point - _mean) * num;
			if (acceptedForModel)
			{
				AcceptedObservationCount++;
			}
		}

		public MediaPipeStereoRawPointBinState CreateState()
		{
			return new MediaPipeStereoRawPointBinState
			{
				BinX = _key.X,
				BinY = _key.Y,
				BinZ = _key.Z,
				MeanXInches = _mean.X,
				MeanYInches = _mean.Y,
				MeanZInches = _mean.Z,
				ObservationCount = ObservationCount,
				AcceptedObservationCount = AcceptedObservationCount
			};
		}
	}

	public const int RequiredLandmarkCount = 468;

	private const int MaximumLandmarkCount = 478;

	private const int MinimumDirectPointsPerFrame = 180;

	private const double MaximumPairSkewMilliseconds = 90.0;

	private const double MaximumFrameResidualPercent = 4.0;

	private const double MinimumTrackingConfidence = 0.75;

	private const double MinimumEyeSpanInches = 1.2;

	private const double MaximumEyeSpanInches = 4.5;

	private const double MinimumFaceHeightInches = 3.5;

	private const double MaximumFaceHeightInches = 10.0;

	private const double RawPointBinSizeInches = 0.04;

	private const double RawDiagnosticExtentEyeSpans = 6.0;

	private const int MaximumRawPointBins = 100000;

	private static readonly bool[] DynamicIdentityMask = CreateIndexMask(MediaPipeFaceMeshTopology.DynamicIdentityIndices);

	private static readonly MeshTopologyEdge[] TopologyEdges = CreateTopologyEdges();

	private readonly VertexAccumulator[] _vertices = CreateVertexAccumulators();

	private readonly DenseVertexAccumulator[] _denseVertices = CreateDenseVertexAccumulators();

	private readonly Dictionary<RawPointBinKey, RawPointBinAccumulator> _rawPointBins = new Dictionary<RawPointBinKey, RawPointBinAccumulator>();

	private string _subjectId = "";

	private string _subjectDisplayName = "";

	private string _calibrationId = "";

	private DateTime _updatedAtUtc;

	private long _acceptedFrameCount;

	private long _rejectedFrameCount;

	private long _directObservationCount;

	private long _rejectedPointCount;

	private long _denseObservationCount;

	private long _rejectedDensePointCount;

	private long _rawTriangulatedObservationCount;

	private long _rawUnstoredObservationCount;

	private double _baselineInches;

	private string _lastFrameStatus = "Calibrated stereo reconstruction is waiting for synchronized face points.";

	private string _lastDenseFrameStatus = "Dense stereo is waiting for paired image evidence.";

	public void Reset(string subjectId, string subjectDisplayName)
	{
		_subjectId = subjectId?.Trim() ?? "";
		_subjectDisplayName = subjectDisplayName?.Trim() ?? "";
		_calibrationId = "";
		_updatedAtUtc = DateTime.MinValue;
		_acceptedFrameCount = 0L;
		_rejectedFrameCount = 0L;
		_directObservationCount = 0L;
		_rejectedPointCount = 0L;
		_denseObservationCount = 0L;
		_rejectedDensePointCount = 0L;
		_rawTriangulatedObservationCount = 0L;
		_rawUnstoredObservationCount = 0L;
		_rawPointBins.Clear();
		_baselineInches = 0.0;
		_lastFrameStatus = "Calibrated stereo reconstruction is waiting for synchronized face points.";
		_lastDenseFrameStatus = "Dense stereo is waiting for paired image evidence.";
		for (int i = 0; i < _vertices.Length; i++)
		{
			_vertices[i].Reset(i);
		}
		for (int j = 0; j < _denseVertices.Length; j++)
		{
			_denseVertices[j].Reset(j);
		}
	}

	public void Restore(MediaPipeStereoFaceState? state, string subjectId, string subjectDisplayName)
	{
		Reset(subjectId, subjectDisplayName);
		if (state == null || !string.Equals(state.SubjectId, subjectId, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		_updatedAtUtc = state.UpdatedAtUtc;
		_calibrationId = state.CalibrationId?.Trim() ?? "";
		_acceptedFrameCount = Math.Max(0L, state.AcceptedFrameCount);
		_rejectedFrameCount = Math.Max(0L, state.RejectedFrameCount);
		_directObservationCount = Math.Max(0L, state.DirectObservationCount);
		_rejectedPointCount = Math.Max(0L, state.RejectedPointCount);
		_denseObservationCount = Math.Max(0L, state.DenseObservationCount);
		_rejectedDensePointCount = Math.Max(0L, state.RejectedDensePointCount);
		_rawTriangulatedObservationCount = Math.Max(0L, state.RawTriangulatedObservationCount);
		_rawUnstoredObservationCount = Math.Max(0L, state.RawUnstoredObservationCount);
		_baselineInches = Math.Max(0.0, state.BaselineInches);
		foreach (MediaPipeStereoVertexAccumulatorState vertexAccumulator in state.VertexAccumulators)
		{
			if ((uint)vertexAccumulator.Index < (uint)_vertices.Length)
			{
				_vertices[vertexAccumulator.Index].Restore(vertexAccumulator);
			}
		}
		foreach (MediaPipeStereoDenseVertexAccumulatorState denseVertexAccumulator in state.DenseVertexAccumulators)
		{
			if ((uint)denseVertexAccumulator.SampleIndex < (uint)_denseVertices.Length)
			{
				_denseVertices[denseVertexAccumulator.SampleIndex].Restore(denseVertexAccumulator);
			}
		}
		foreach (MediaPipeStereoRawPointBinState rawPointBin in state.RawPointBins)
		{
			if (rawPointBin.ObservationCount > 0 && _rawPointBins.Count < 100000)
			{
				RawPointBinKey key = new RawPointBinKey(rawPointBin.BinX, rawPointBin.BinY, rawPointBin.BinZ);
				_rawPointBins[key] = RawPointBinAccumulator.Restore(key, rawPointBin);
			}
		}
	}

	public bool TryAddFrame(MediaPipeStereoGeometryFrame frame)
	{
		ArgumentNullException.ThrowIfNull(frame, "frame");
		if (frame.Landmarks.Length < 468)
		{
			return RejectFrame($"Waiting for {468} shared landmarks; received {frame.Landmarks.Length}.");
		}
		if (frame.PairSkew.TotalMilliseconds > 90.0)
		{
			return RejectFrame($"Camera timing is {frame.PairSkew.TotalMilliseconds:0} ms apart; need {90.0:0} ms or less.");
		}
		if (frame.FrameReprojectionResidualPercent > 4.0)
		{
			return RejectFrame($"Stereo reprojection error is {frame.FrameReprojectionResidualPercent:0.00}% of face width; need {4.0:0.00}% or less.");
		}
		if (frame.CameraATrackingConfidence < 0.75 || frame.CameraBTrackingConfidence < 0.75)
		{
			return RejectFrame($"Face lock is A {frame.CameraATrackingConfidence:P0}, B {frame.CameraBTrackingConfidence:P0}; both need {0.75:P0} or better.");
		}
		if (!HeadBasis.TryCreate(frame.Landmarks, out var basis))
		{
			return RejectFrame("Physical face scale or pose anchors are not stable enough yet.");
		}
		string text = frame.CalibrationId?.Trim() ?? "";
		if (text.Length == 0)
		{
			return RejectFrame("Physical camera calibration identity is missing.");
		}
		if (!string.Equals(_calibrationId, text, StringComparison.Ordinal))
		{
			bool flag = _acceptedFrameCount > 0;
			Reset(_subjectId, _subjectDisplayName);
			_calibrationId = text;
			_baselineInches = frame.BaselineInches;
			_lastFrameStatus = (flag ? "Camera calibration changed; incompatible prior stereo geometry was cleared." : "Physical camera calibration is bound to this reconstruction.");
		}
		Span<CandidatePoint> span = stackalloc CandidatePoint[478];
		int num = 0;
		int num2 = Math.Min(478, frame.Landmarks.Length);
		for (int i = 0; i < num2; i++)
		{
			MediaPipeStereoRigLandmark mediaPipeStereoRigLandmark = frame.Landmarks[i];
			if (!mediaPipeStereoRigLandmark.IsValid || !mediaPipeStereoRigLandmark.IsDirectlyMeasured)
			{
				_vertices[i].Reject();
				_rejectedPointCount++;
				continue;
			}
			Point3 point = basis.ToLocal(mediaPipeStereoRigLandmark.XInches, mediaPipeStereoRigLandmark.YInches, mediaPipeStereoRigLandmark.ZInches);
			if (!point.IsFinite || Math.Abs(point.X) > basis.EyeSpanInches * 2.4 || Math.Abs(point.Y) > basis.EyeSpanInches * 3.4 || Math.Abs(point.Z) > basis.EyeSpanInches * 2.4)
			{
				RecordRawPoint(point, basis.EyeSpanInches, acceptedForModel: false);
				_vertices[i].Reject();
				_rejectedPointCount++;
				continue;
			}
			double num3 = 1.0 / (1.0 + Math.Pow(mediaPipeStereoRigLandmark.ReprojectionResidualPercent / 1.5, 2.0));
			double num4 = Math.Clamp(Math.Sqrt(Math.Max(0.0, mediaPipeStereoRigLandmark.CameraADirectnessRatio * mediaPipeStereoRigLandmark.CameraBDirectnessRatio)), 0.15, 2.0);
			double num5 = (DynamicIdentityMask[i] ? 0.35 : 1.0);
			double num6 = num3 * num4 * num5;
			bool flag2 = num6 >= 0.025 && _vertices[i].CanAccept(point, DynamicIdentityMask[i]);
			RecordRawPoint(point, basis.EyeSpanInches, flag2);
			if (!flag2)
			{
				_vertices[i].Reject();
				_rejectedPointCount++;
			}
			else
			{
				span[num++] = new CandidatePoint(i, point, num6, mediaPipeStereoRigLandmark.ReprojectionResidualPercent);
			}
		}
		if (num < 180)
		{
			return RejectFrame($"Only {num} directly measured landmarks passed; need {180}.");
		}
		for (int j = 0; j < num; j++)
		{
			CandidatePoint candidatePoint = span[j];
			_vertices[candidatePoint.Index].Add(candidatePoint.Point, candidatePoint.Weight, candidatePoint.ResidualPercent);
			_directObservationCount++;
		}
		AddDenseSurface(frame, basis);
		_acceptedFrameCount++;
		_updatedAtUtc = frame.CapturedAtUtc;
		_baselineInches = frame.BaselineInches;
		_lastFrameStatus = $"Accepted {_acceptedFrameCount:n0} synchronized frames; {num} direct landmarks in the latest frame.";
		return true;
	}

	public MediaPipeStereoFaceModel CreateModel()
	{
		MediaPipeStereoFaceVertex[] array = new MediaPipeStereoFaceVertex[478];
		Span<double> span = stackalloc double[478];
		int deviationCount = 0;
		int directlyMeasuredCount = 0;
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = _vertices[i].CreateVertex(DynamicIdentityMask[i]);
			if (array[i].DirectObservationCount > 1)
			{
				span[deviationCount++] = array[i].StandardDeviationInches;
			}
			if (array[i].EvidenceClass == MediaPipeStereoEvidenceClasses.DirectlyMeasured)
			{
				directlyMeasuredCount++;
			}
		}
		span[..deviationCount].Sort();
		double num2 = ((deviationCount == 0) ? 0.0 : span[deviationCount / 2]);
		double num3 = (double)directlyMeasuredCount * 100.0 / 478.0;
		double faceWidthInches = Distance(array, 234, 454);
		double faceHeightInches = Distance(array, 10, 152);
		double measuredDepthInches = ExtentZ(array);
		int denseVertexCount = 0;
		for (int i = 0; i < _denseVertices.Length; i++)
		{
			if (_denseVertices[i].HasEvidence)
			{
				denseVertexCount++;
			}
		}
		MediaPipeStereoDenseFaceVertex[] array2 = new MediaPipeStereoDenseFaceVertex[denseVertexCount];
		int denseVertexIndex = 0;
		int directlyMeasuredDenseCount = 0;
		for (int i = 0; i < _denseVertices.Length; i++)
		{
			DenseVertexAccumulator denseVertexAccumulator = _denseVertices[i];
			if (denseVertexAccumulator.HasEvidence)
			{
				MediaPipeStereoDenseFaceVertex mediaPipeStereoDenseFaceVertex = denseVertexAccumulator.CreateVertex();
				array2[denseVertexIndex++] = mediaPipeStereoDenseFaceVertex;
				if (mediaPipeStereoDenseFaceVertex.EvidenceClass == MediaPipeStereoEvidenceClasses.DirectlyMeasured)
				{
					directlyMeasuredDenseCount++;
				}
			}
		}
		double denseStableVertexPercent = ((array2.Length == 0) ? 0.0 : ((double)directlyMeasuredDenseCount * 100.0 / (double)array2.Length));
		long rawMaximumBinObservationCount = 0;
		foreach (RawPointBinAccumulator rawPointBin in _rawPointBins.Values)
		{
			if (rawPointBin.ObservationCount > rawMaximumBinObservationCount)
			{
				rawMaximumBinObservationCount = rawPointBin.ObservationCount;
			}
		}
		string status = ((_acceptedFrameCount == 0L) ? _lastFrameStatus : ($"Measured {_acceptedFrameCount:n0} synchronized stereo frames in physical scale; {num3:0.#}% of vertices are stable direct measurements and median spread is {num2:0.000} in. Dense stereo has measured {array2.Length:n0}/{MediaPipeDenseStereoMatcher.MaximumSampleCount:n0} surface samples. " + _lastFrameStatus + " " + _lastDenseFrameStatus));
		return new MediaPipeStereoFaceModel
		{
			SubjectId = _subjectId,
			SubjectDisplayName = _subjectDisplayName,
			CalibrationId = _calibrationId,
			UpdatedAtUtc = _updatedAtUtc,
			AcceptedFrameCount = _acceptedFrameCount,
			RejectedFrameCount = _rejectedFrameCount,
			DirectObservationCount = _directObservationCount,
			RejectedPointCount = _rejectedPointCount,
			DenseObservationCount = _denseObservationCount,
			RejectedDensePointCount = _rejectedDensePointCount,
			BaselineInches = _baselineInches,
			ConfidentVertexPercent = num3,
			MedianVertexDeviationInches = num2,
			FaceWidthInches = faceWidthInches,
			FaceHeightInches = faceHeightInches,
			MeasuredDepthInches = measuredDepthInches,
			DenseStableVertexPercent = denseStableVertexPercent,
			DenseMeasuredVertexCount = array2.Length,
			DenseMaximumVertexCount = MediaPipeDenseStereoMatcher.MaximumSampleCount,
			RawTriangulatedObservationCount = _rawTriangulatedObservationCount,
			RawPointBinCount = _rawPointBins.Count,
			RawMaximumBinObservationCount = rawMaximumBinObservationCount,
			RawUnstoredObservationCount = _rawUnstoredObservationCount,
			Status = status,
			Vertices = array,
			TopologyEdges = TopologyEdges,
			DenseVertices = array2
		};
	}

	public MediaPipeStereoFaceState CreateState()
	{
		MediaPipeStereoVertexAccumulatorState[] array = new MediaPipeStereoVertexAccumulatorState[_vertices.Length];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = _vertices[i].CreateState();
		}
		return new MediaPipeStereoFaceState
		{
			SubjectId = _subjectId,
			SubjectDisplayName = _subjectDisplayName,
			CalibrationId = _calibrationId,
			UpdatedAtUtc = _updatedAtUtc,
			AcceptedFrameCount = _acceptedFrameCount,
			RejectedFrameCount = _rejectedFrameCount,
			DirectObservationCount = _directObservationCount,
			RejectedPointCount = _rejectedPointCount,
			DenseObservationCount = _denseObservationCount,
			RejectedDensePointCount = _rejectedDensePointCount,
			RawTriangulatedObservationCount = _rawTriangulatedObservationCount,
			RawUnstoredObservationCount = _rawUnstoredObservationCount,
			BaselineInches = _baselineInches,
			VertexAccumulators = array,
			DenseVertexAccumulators = CreateDenseAccumulatorStates(),
			RawPointBins = CreateRawPointBinStates()
		};
	}

	private void AddDenseSurface(MediaPipeStereoGeometryFrame frame, HeadBasis basis)
	{
		if (frame.ImagePair == null)
		{
			_lastDenseFrameStatus = "Dense stereo is waiting for paired image pixels.";
			return;
		}
		MediaPipeStereoDenseRigPoint[] array;
		MediaPipeDenseStereoDiagnostics diagnostics;
		try
		{
			array = MediaPipeDenseStereoMatcher.Match(frame.ImagePair, out diagnostics);
		}
		catch (Exception ex)
		{
			_lastDenseFrameStatus = "Dense stereo skipped one pair: " + ex.Message;
			return;
		}
		int num = 0;
		MediaPipeStereoDenseRigPoint[] array2 = array;
		for (int i = 0; i < array2.Length; i++)
		{
			MediaPipeStereoDenseRigPoint mediaPipeStereoDenseRigPoint = array2[i];
			if ((uint)mediaPipeStereoDenseRigPoint.SampleIndex < (uint)_denseVertices.Length)
			{
				Point3 point = basis.ToLocal(mediaPipeStereoDenseRigPoint.XInches, mediaPipeStereoDenseRigPoint.YInches, mediaPipeStereoDenseRigPoint.ZInches);
				double reprojectionRatio = mediaPipeStereoDenseRigPoint.ReprojectionResidualPercent / 1.25;
				double matchErrorRatio = mediaPipeStereoDenseRigPoint.MatchErrorPixels / 1.5;
				double num2 = 1.0 / (1.0 + reprojectionRatio * reprojectionRatio);
				double num3 = 1.0 / (1.0 + matchErrorRatio * matchErrorRatio);
				double num4 = (mediaPipeStereoDenseRigPoint.IsExpressionSurface ? 0.3 : 1.0);
				double num5 = num2 * num3 * num4;
				bool flag = point.IsFinite && Math.Abs(point.X) <= basis.EyeSpanInches * 2.4 && Math.Abs(point.Y) <= basis.EyeSpanInches * 3.4 && Math.Abs(point.Z) <= basis.EyeSpanInches * 2.4 && num5 >= 0.02 && _denseVertices[mediaPipeStereoDenseRigPoint.SampleIndex].CanAccept(point, mediaPipeStereoDenseRigPoint.IsExpressionSurface);
				RecordRawPoint(point, basis.EyeSpanInches, flag);
				if (!flag)
				{
					_denseVertices[mediaPipeStereoDenseRigPoint.SampleIndex].Reject();
					_rejectedDensePointCount++;
				}
				else
				{
					_denseVertices[mediaPipeStereoDenseRigPoint.SampleIndex].Add(mediaPipeStereoDenseRigPoint.TriangleIndex, mediaPipeStereoDenseRigPoint.IsExpressionSurface, point, num5, mediaPipeStereoDenseRigPoint.ReprojectionResidualPercent);
					_denseObservationCount++;
					num++;
				}
			}
		}
		_lastDenseFrameStatus = $"Dense stereo accepted {num:n0} surface samples from {diagnostics.CandidateCount:n0} candidates ({diagnostics.FlowMatchedCount:n0} image matches, {diagnostics.TriangulatedCount:n0} triangulated; source A {diagnostics.CameraASourceMatchedCount:n0}/{diagnostics.CameraASourceCandidateCount:n0}, source B {diagnostics.CameraBSourceMatchedCount:n0}/{diagnostics.CameraBSourceCandidateCount:n0}).";
	}

	private void RecordRawPoint(Point3 point, double eyeSpanInches, bool acceptedForModel)
	{
		if (!point.IsFinite)
		{
			_rawUnstoredObservationCount++;
			return;
		}
		_rawTriangulatedObservationCount++;
		double num = Math.Max(1.0, eyeSpanInches * 6.0);
		if (Math.Abs(point.X) > num || Math.Abs(point.Y) > num || Math.Abs(point.Z) > num)
		{
			_rawUnstoredObservationCount++;
			return;
		}
		RawPointBinKey key = new RawPointBinKey(QuantizeRawCoordinate(point.X), QuantizeRawCoordinate(point.Y), QuantizeRawCoordinate(point.Z));
		if (!_rawPointBins.TryGetValue(key, out RawPointBinAccumulator value))
		{
			if (_rawPointBins.Count >= 100000)
			{
				_rawUnstoredObservationCount++;
				return;
			}
			value = new RawPointBinAccumulator(key);
			_rawPointBins.Add(key, value);
		}
		value.Add(point, acceptedForModel);
	}

	private static int QuantizeRawCoordinate(double coordinate)
	{
		return checked((int)Math.Round(coordinate / 0.04, MidpointRounding.AwayFromZero));
	}

	private IReadOnlyList<MediaPipeStereoDenseVertexAccumulatorState> CreateDenseAccumulatorStates()
	{
		int count = 0;
		for (int i = 0; i < _denseVertices.Length; i++)
		{
			if (_denseVertices[i].HasEvidence)
			{
				count++;
			}
		}
		MediaPipeStereoDenseVertexAccumulatorState[] states = new MediaPipeStereoDenseVertexAccumulatorState[count];
		int destinationIndex = 0;
		for (int i = 0; i < _denseVertices.Length; i++)
		{
			DenseVertexAccumulator denseVertexAccumulator = _denseVertices[i];
			if (denseVertexAccumulator.HasEvidence)
			{
				states[destinationIndex++] = denseVertexAccumulator.CreateState();
			}
		}
		return states;
	}

	private IReadOnlyList<MediaPipeStereoRawPointBinState> CreateRawPointBinStates()
	{
		MediaPipeStereoRawPointBinState[] states = new MediaPipeStereoRawPointBinState[_rawPointBins.Count];
		int index = 0;
		foreach (RawPointBinAccumulator rawPointBin in _rawPointBins.Values)
		{
			states[index++] = rawPointBin.CreateState();
		}
		return states;
	}

	private static double Distance(IReadOnlyList<MediaPipeStereoFaceVertex> vertices, int first, int second)
	{
		if ((uint)first >= (uint)vertices.Count || (uint)second >= (uint)vertices.Count || vertices[first].DirectObservationCount == 0L || vertices[second].DirectObservationCount == 0L)
		{
			return 0.0;
		}
		MediaPipeStereoFaceVertex mediaPipeStereoFaceVertex = vertices[first];
		MediaPipeStereoFaceVertex mediaPipeStereoFaceVertex2 = vertices[second];
		double deltaX = mediaPipeStereoFaceVertex.XInches - mediaPipeStereoFaceVertex2.XInches;
		double deltaY = mediaPipeStereoFaceVertex.YInches - mediaPipeStereoFaceVertex2.YInches;
		double deltaZ = mediaPipeStereoFaceVertex.ZInches - mediaPipeStereoFaceVertex2.ZInches;
		return Math.Sqrt(deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ);
	}

	private static double ExtentZ(IReadOnlyList<MediaPipeStereoFaceVertex> vertices)
	{
		double num = double.PositiveInfinity;
		double num2 = double.NegativeInfinity;
		foreach (MediaPipeStereoFaceVertex vertex in vertices)
		{
			if (vertex.DirectObservationCount != 0L && !DynamicIdentityMask[vertex.Index])
			{
				double val = vertex.ZInches;
				num = Math.Min(num, val);
				num2 = Math.Max(num2, val);
			}
		}
		if (!double.IsFinite(num) || !double.IsFinite(num2))
		{
			return 0.0;
		}
		return Math.Max(0.0, num2 - num);
	}

	private static VertexAccumulator[] CreateVertexAccumulators()
	{
		VertexAccumulator[] array = new VertexAccumulator[478];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = new VertexAccumulator(i);
		}
		return array;
	}

	private static DenseVertexAccumulator[] CreateDenseVertexAccumulators()
	{
		DenseVertexAccumulator[] array = new DenseVertexAccumulator[MediaPipeDenseStereoMatcher.MaximumSampleCount];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = new DenseVertexAccumulator(i);
		}
		return array;
	}

	private bool RejectFrame(string status)
	{
		_rejectedFrameCount++;
		_lastFrameStatus = status;
		return false;
	}

	private static bool[] CreateIndexMask(IEnumerable<int> indices)
	{
		bool[] array = new bool[478];
		foreach (int index in indices)
		{
			if ((uint)index < (uint)array.Length)
			{
				array[index] = true;
			}
		}
		return array;
	}

	private static MeshTopologyEdge[] CreateTopologyEdges()
	{
		(int, int)[] tessellationEdges = MediaPipeFaceMeshTopology.TessellationEdges;
		MeshTopologyEdge[] array = new MeshTopologyEdge[tessellationEdges.Length];
		for (int i = 0; i < tessellationEdges.Length; i++)
		{
			array[i] = new MeshTopologyEdge
			{
				FromIndex = tessellationEdges[i].Item1,
				ToIndex = tessellationEdges[i].Item2,
				Role = "surface",
				Source = "calibrated-stereo-mediapipe-face-tessellation",
				ConfidencePercent = 100.0
			};
		}
		return array;
	}
}
