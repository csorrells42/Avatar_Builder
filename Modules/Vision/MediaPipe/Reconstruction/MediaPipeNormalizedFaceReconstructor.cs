using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction;

public sealed class MediaPipeNormalizedFaceReconstructor
{
	private enum ImageSide
	{
		Left,
		Right
	}

	private readonly record struct NormalizedMeasurement(double U, double V, double UnrolledU, double UnrolledV);

	private readonly record struct SilhouetteProfileKey(int YawBinDegrees, int PitchBinDegrees, string CameraId);

	private readonly record struct HullPoint(double X, double Z);

	private readonly record struct HalfPlaneConstraint(double Nx, double Nz, double Maximum)
	{
		public bool Contains(HullPoint point)
		{
			return Nx * point.X + Nz * point.Z <= Maximum + 1E-07;
		}

		public HullPoint Intersection(HullPoint first, HullPoint second)
		{
			double num = second.X - first.X;
			double num2 = second.Z - first.Z;
			double num3 = Nx * num + Nz * num2;
			if (Math.Abs(num3) < 1E-09)
			{
				return first;
			}
			double num4 = (Maximum - Nx * first.X - Nz * first.Z) / num3;
			return new HullPoint(first.X + num * num4, first.Z + num2 * num4);
		}
	}

	private readonly record struct Vector3(double X, double Y, double Z)
	{
		public double Length => Math.Sqrt(Dot(this));

		public static Vector3 Cross(Vector3 first, Vector3 second)
		{
			return new Vector3(first.Y * second.Z - first.Z * second.Y, first.Z * second.X - first.X * second.Z, first.X * second.Y - first.Y * second.X);
		}

		public double Dot(Vector3 other)
		{
			return X * other.X + Y * other.Y + Z * other.Z;
		}

		public Vector3 Normalize()
		{
			double length = Length;
			if (!(length <= 1E-09))
			{
				return new Vector3(X / length, Y / length, Z / length);
			}
			return default(Vector3);
		}

		public static Vector3 operator -(Vector3 first, Vector3 second)
		{
			return new Vector3(first.X - second.X, first.Y - second.Y, first.Z - second.Z);
		}

		public static Vector3 operator *(Vector3 value, double scale)
		{
			return new Vector3(value.X * scale, value.Y * scale, value.Z * scale);
		}
	}

	private readonly record struct RotationMatrix(Vector3 Row0, Vector3 Row1, Vector3 Row2)
	{
		public static bool TryCreate(MediaPipeGeometryFrame frame, out RotationMatrix rotation)
		{
			if (TryCreateFromFacialMatrix(frame.FacialTransformationMatrix, out rotation))
			{
				return true;
			}
			rotation = CreateFromEuler(frame.ARotationAroundXDegrees, frame.BRotationAroundYDegrees, frame.CRotationAroundZDegrees);
			return true;
		}

		private static bool TryCreateFromFacialMatrix(IReadOnlyList<double> values, out RotationMatrix rotation)
		{
			rotation = default(RotationMatrix);
			if (values.Count < 16)
			{
				return false;
			}
			Vector3 vector = new Vector3(values[0], values[1], values[2]).Normalize();
			Vector3 vector2 = new Vector3(values[4], values[5], values[6]);
			Vector3 vector3 = (vector2 - vector * vector2.Dot(vector)).Normalize();
			Vector3 row = Vector3.Cross(vector, vector3).Normalize();
			if (vector.Length < 0.9 || vector3.Length < 0.9 || row.Length < 0.9)
			{
				return false;
			}
			rotation = new RotationMatrix(vector, vector3, row);
			return true;
		}

		private static RotationMatrix CreateFromEuler(double aDegrees, double bDegrees, double cDegrees)
		{
			double num = aDegrees * Math.PI / 180.0;
			double num2 = bDegrees * Math.PI / 180.0;
			double num3 = cDegrees * Math.PI / 180.0;
			double num4 = Math.Cos(num);
			double num5 = Math.Sin(num);
			double num6 = Math.Cos(num2);
			double num7 = Math.Sin(num2);
			double num8 = Math.Cos(num3);
			double num9 = Math.Sin(num3);
			return new RotationMatrix(new Vector3(num8 * num6 - num9 * num5 * num7, (0.0 - num9) * num4, num8 * num7 + num9 * num5 * num6), new Vector3(num9 * num6 + num8 * num5 * num7, num8 * num4, num9 * num7 - num8 * num5 * num6), new Vector3((0.0 - num4) * num7, num5, num4 * num6));
		}
	}

	private sealed class VertexAccumulator
	{
		private int _index;

		private double _m00;

		private double _m01;

		private double _m02;

		private double _m11;

		private double _m12;

		private double _m22;

		private double _b0;

		private double _b1;

		private double _b2;

		private double _sumMeasurementSquares;

		private double _totalWeight;

		private long _directObservationCount;

		private long _rejectedHiddenObservationCount;

		private double _minimumYaw = double.PositiveInfinity;

		private double _maximumYaw = double.NegativeInfinity;

		private double _minimumPitch = double.PositiveInfinity;

		private double _maximumPitch = double.NegativeInfinity;

		private bool _pinnedToOrigin;

		public VertexAccumulator(int index)
		{
			Reset(index);
		}

		public void Reset(int index)
		{
			_index = index;
			_m00 = (_m01 = (_m02 = (_m11 = (_m12 = (_m22 = 0.0)))));
			_b0 = (_b1 = (_b2 = 0.0));
			_sumMeasurementSquares = 0.0;
			_totalWeight = 0.0;
			_directObservationCount = 0L;
			_rejectedHiddenObservationCount = 0L;
			_minimumYaw = double.PositiveInfinity;
			_maximumYaw = double.NegativeInfinity;
			_minimumPitch = double.PositiveInfinity;
			_maximumPitch = double.NegativeInfinity;
			_pinnedToOrigin = false;
		}

		public void Restore(MediaPipeVertexAccumulatorState state)
		{
			_index = state.Index;
			_m00 = state.M00;
			_m01 = state.M01;
			_m02 = state.M02;
			_m11 = state.M11;
			_m12 = state.M12;
			_m22 = state.M22;
			_b0 = state.B0;
			_b1 = state.B1;
			_b2 = state.B2;
			_sumMeasurementSquares = state.SumMeasurementSquares;
			_totalWeight = state.TotalWeight;
			_directObservationCount = Math.Max(0L, state.DirectObservationCount);
			_rejectedHiddenObservationCount = Math.Max(0L, state.RejectedHiddenObservationCount);
			_minimumYaw = state.MinimumYawDegrees;
			_maximumYaw = state.MaximumYawDegrees;
			_minimumPitch = state.MinimumPitchDegrees;
			_maximumPitch = state.MaximumPitchDegrees;
			_pinnedToOrigin = state.Index == 168 && state.DirectObservationCount > 0;
		}

		public void AddObservation(Vector3 row0, Vector3 row1, double u, double v, double weight, double yawDegrees, double pitchDegrees)
		{
			AddRow(row0, u, weight);
			AddRow(row1, v, weight);
			_sumMeasurementSquares += weight * (u * u + v * v);
			_totalWeight += weight * 2.0;
			_directObservationCount++;
			_minimumYaw = Math.Min(_minimumYaw, yawDegrees);
			_maximumYaw = Math.Max(_maximumYaw, yawDegrees);
			_minimumPitch = Math.Min(_minimumPitch, pitchDegrees);
			_maximumPitch = Math.Max(_maximumPitch, pitchDegrees);
		}

		public void PinToOrigin(double yawDegrees, double pitchDegrees)
		{
			_pinnedToOrigin = true;
			_directObservationCount++;
			_minimumYaw = Math.Min(_minimumYaw, yawDegrees);
			_maximumYaw = Math.Max(_maximumYaw, yawDegrees);
			_minimumPitch = Math.Min(_minimumPitch, pitchDegrees);
			_maximumPitch = Math.Max(_maximumPitch, pitchDegrees);
		}

		public void RejectHidden()
		{
			_rejectedHiddenObservationCount++;
		}

		public MediaPipeNormalizedFaceVertex CreateVertex(bool expressionOnly)
		{
			if (_pinnedToOrigin)
			{
				return new MediaPipeNormalizedFaceVertex
				{
					Index = _index,
					DirectObservationCount = _directObservationCount,
					RejectedHiddenObservationCount = _rejectedHiddenObservationCount,
					AngularCoverageDegrees = Math.Max(Range(_minimumYaw, _maximumYaw), Range(_minimumPitch, _maximumPitch)),
					ConfidencePercent = 100.0,
					EvidenceClass = "directly-measured"
				};
			}
			if (_directObservationCount == 0L || !TrySolve(out var solution))
			{
				return new MediaPipeNormalizedFaceVertex
				{
					Index = _index,
					DirectObservationCount = _directObservationCount,
					RejectedHiddenObservationCount = _rejectedHiddenObservationCount,
					EvidenceClass = (expressionOnly ? "expression-only" : "underconstrained")
				};
			}
			double num = Math.Max(Range(_minimumYaw, _maximumYaw), Range(_minimumPitch, _maximumPitch));
			double num2 = CalculateResidual(solution);
			double d = Math.Min(1.0, (double)_directObservationCount / 36.0);
			double val = Math.Min(1.0, num / 55.0);
			double val2 = CalculateConditioningScore();
			double num3 = Math.Exp((0.0 - Math.Min(2.0, num2)) * 10.0);
			double num4 = Math.Clamp(100.0 * Math.Sqrt(d) * Math.Sqrt(Math.Max(0.02, val)) * Math.Pow(Math.Max(0.02, val2), 0.28) * num3, 0.0, 100.0);
			string evidenceClass = (expressionOnly ? "expression-only" : ((_directObservationCount >= 12 && num >= 15.0 && num4 >= 40.0) ? "directly-measured" : "partially-measured"));
			return new MediaPipeNormalizedFaceVertex
			{
				Index = _index,
				X = solution.X,
				Y = solution.Y,
				Z = solution.Z,
				DirectObservationCount = _directObservationCount,
				RejectedHiddenObservationCount = _rejectedHiddenObservationCount,
				AngularCoverageDegrees = num,
				ResidualPercent = num2 * 100.0,
				ConfidencePercent = num4,
				EvidenceClass = evidenceClass
			};
		}

		public MediaPipeVertexAccumulatorState CreateState()
		{
			return new MediaPipeVertexAccumulatorState
			{
				Index = _index,
				M00 = _m00,
				M01 = _m01,
				M02 = _m02,
				M11 = _m11,
				M12 = _m12,
				M22 = _m22,
				B0 = _b0,
				B1 = _b1,
				B2 = _b2,
				SumMeasurementSquares = _sumMeasurementSquares,
				TotalWeight = _totalWeight,
				DirectObservationCount = _directObservationCount,
				RejectedHiddenObservationCount = _rejectedHiddenObservationCount,
				MinimumYawDegrees = FiniteOrZero(_minimumYaw),
				MaximumYawDegrees = FiniteOrZero(_maximumYaw),
				MinimumPitchDegrees = FiniteOrZero(_minimumPitch),
				MaximumPitchDegrees = FiniteOrZero(_maximumPitch)
			};
		}

		private void AddRow(Vector3 row, double measurement, double weight)
		{
			_m00 += weight * row.X * row.X;
			_m01 += weight * row.X * row.Y;
			_m02 += weight * row.X * row.Z;
			_m11 += weight * row.Y * row.Y;
			_m12 += weight * row.Y * row.Z;
			_m22 += weight * row.Z * row.Z;
			_b0 += weight * row.X * measurement;
			_b1 += weight * row.Y * measurement;
			_b2 += weight * row.Z * measurement;
		}

		private bool TrySolve(out Vector3 solution)
		{
			double num = _m00 + 1E-06;
			double m = _m01;
			double m2 = _m02;
			double num2 = _m11 + 1E-06;
			double m3 = _m12;
			double num3 = _m22 + 1E-06;
			double num4 = num * (num2 * num3 - m3 * m3) - m * (m * num3 - m3 * m2) + m2 * (m * m3 - num2 * m2);
			if (!double.IsFinite(num4) || Math.Abs(num4) < 1E-12)
			{
				solution = default(Vector3);
				return false;
			}
			double num5 = (num2 * num3 - m3 * m3) / num4;
			double num6 = (m2 * m3 - m * num3) / num4;
			double num7 = (m * m3 - m2 * num2) / num4;
			double num8 = (num * num3 - m2 * m2) / num4;
			double num9 = (m * m2 - num * m3) / num4;
			double num10 = (num * num2 - m * m) / num4;
			solution = new Vector3(num5 * _b0 + num6 * _b1 + num7 * _b2, num6 * _b0 + num8 * _b1 + num9 * _b2, num7 * _b0 + num9 * _b1 + num10 * _b2);
			if (double.IsFinite(solution.X) && double.IsFinite(solution.Y))
			{
				return double.IsFinite(solution.Z);
			}
			return false;
		}

		private double CalculateResidual(Vector3 solution)
		{
			if (_totalWeight <= 0.0)
			{
				return 0.0;
			}
			double num = _m00 * solution.X * solution.X + 2.0 * _m01 * solution.X * solution.Y + 2.0 * _m02 * solution.X * solution.Z + _m11 * solution.Y * solution.Y + 2.0 * _m12 * solution.Y * solution.Z + _m22 * solution.Z * solution.Z;
			double num2 = 2.0 * (_b0 * solution.X + _b1 * solution.Y + _b2 * solution.Z);
			return Math.Sqrt(Math.Max(0.0, num - num2 + _sumMeasurementSquares) / _totalWeight);
		}

		private double CalculateConditioningScore()
		{
			double num = _m00 + _m11 + _m22;
			if (num <= 0.0)
			{
				return 0.0;
			}
			double val = _m00 * (_m11 * _m22 - _m12 * _m12) - _m01 * (_m01 * _m22 - _m12 * _m02) + _m02 * (_m01 * _m12 - _m11 * _m02);
			return Math.Clamp(27.0 * Math.Max(0.0, val) / (num * num * num), 0.0, 1.0);
		}
	}

	private sealed class SilhouetteProfileAccumulator
	{
		public SilhouetteProfileKey Key { get; }

		public long FrameCount { get; set; }

		public SilhouetteBandAccumulator[] Bands { get; }

		public SilhouetteProfileAccumulator(SilhouetteProfileKey key)
		{
			Key = key;
			Bands = new SilhouetteBandAccumulator[18];
			for (int i = 0; i < Bands.Length; i++)
			{
				Bands[i] = new SilhouetteBandAccumulator(i);
			}
		}

		public static SilhouetteProfileAccumulator Restore(MediaPipeSilhouetteProfileState state)
		{
			SilhouetteProfileAccumulator silhouetteProfileAccumulator = new SilhouetteProfileAccumulator(new SilhouetteProfileKey(state.YawBinDegrees, state.PitchBinDegrees, state.CameraId ?? "camera"))
			{
				FrameCount = Math.Max(0L, state.FrameCount)
			};
			foreach (MediaPipeSilhouetteBandState band in state.Bands)
			{
				if (band.BandIndex >= 0 && band.BandIndex < silhouetteProfileAccumulator.Bands.Length)
				{
					silhouetteProfileAccumulator.Bands[band.BandIndex].Restore(band);
				}
			}
			return silhouetteProfileAccumulator;
		}

		public MediaPipeSilhouetteAngleProfile CreateProfile()
		{
			List<MediaPipeSilhouetteBand> list = new List<MediaPipeSilhouetteBand>(Bands.Length);
			for (int i = 0; i < Bands.Length; i++)
			{
				if (Bands[i].ObservationCount > 0)
				{
					list.Add(Bands[i].CreateBand());
				}
			}
			return new MediaPipeSilhouetteAngleProfile
			{
				YawBinDegrees = Key.YawBinDegrees,
				PitchBinDegrees = Key.PitchBinDegrees,
				CameraId = Key.CameraId,
				FrameCount = FrameCount,
				Bands = list
			};
		}

		public MediaPipeSilhouetteProfileState CreateState()
		{
			List<MediaPipeSilhouetteBandState> list = new List<MediaPipeSilhouetteBandState>(Bands.Length);
			for (int i = 0; i < Bands.Length; i++)
			{
				if (Bands[i].ObservationCount > 0)
				{
					list.Add(Bands[i].CreateState());
				}
			}
			return new MediaPipeSilhouetteProfileState
			{
				YawBinDegrees = Key.YawBinDegrees,
				PitchBinDegrees = Key.PitchBinDegrees,
				CameraId = Key.CameraId,
				FrameCount = FrameCount,
				Bands = list
			};
		}
	}

	private sealed class SilhouetteBandAccumulator
	{
		private readonly int _bandIndex;

		private double _minimumMean;

		private double _maximumMean;

		private double _minimumM2;

		private double _maximumM2;

		public long ObservationCount { get; private set; }

		public SilhouetteBandAccumulator(int bandIndex)
		{
			_bandIndex = bandIndex;
		}

		public void Add(double minimum, double maximum)
		{
			ObservationCount++;
			UpdateRunningMoments(minimum, ObservationCount, ref _minimumMean, ref _minimumM2);
			UpdateRunningMoments(maximum, ObservationCount, ref _maximumMean, ref _maximumM2);
		}

		public void Restore(MediaPipeSilhouetteBandState state)
		{
			ObservationCount = Math.Max(0L, state.ObservationCount);
			_minimumMean = state.MinimumSupportMean;
			_maximumMean = state.MaximumSupportMean;
			_minimumM2 = Math.Max(0.0, state.MinimumSupportM2);
			_maximumM2 = Math.Max(0.0, state.MaximumSupportM2);
		}

		public MediaPipeSilhouetteBand CreateBand()
		{
			double val = ((ObservationCount > 1) ? ((_minimumM2 + _maximumM2) / (2.0 * ((double)ObservationCount - 1.0))) : 0.0);
			return new MediaPipeSilhouetteBand
			{
				BandIndex = _bandIndex,
				CanonicalY = BandCenter(_bandIndex),
				ObservationCount = ObservationCount,
				MinimumSupport = _minimumMean,
				MaximumSupport = _maximumMean,
				StandardDeviation = Math.Sqrt(Math.Max(0.0, val))
			};
		}

		public MediaPipeSilhouetteBandState CreateState()
		{
			return new MediaPipeSilhouetteBandState
			{
				BandIndex = _bandIndex,
				ObservationCount = ObservationCount,
				MinimumSupportMean = _minimumMean,
				MaximumSupportMean = _maximumMean,
				MinimumSupportM2 = _minimumM2,
				MaximumSupportM2 = _maximumM2
			};
		}

		private static void UpdateRunningMoments(double value, long count, ref double mean, ref double m2)
		{
			double num = value - mean;
			mean += num / (double)count;
			double num2 = value - mean;
			m2 += num * num2;
		}
	}

	public const int RequiredLandmarkCount = 468;

	public const int NoseBridgeAnchorIndex = 168;

	public const double AngularBinSizeDegrees = 5.0;

	private const int MaximumLandmarkCount = 478;

	private const int SilhouetteBandCount = 18;

	private const double SilhouetteMinimumY = -0.72;

	private const double SilhouetteMaximumY = 0.58;

	private const double SolverRegularization = 1E-06;

	private const double MinimumScalePixels = 4.0;

	private static readonly bool[] DynamicIdentityMask = CreateIndexMask(MediaPipeFaceMeshTopology.DynamicIdentityIndices);

	private static readonly MeshTopologyEdge[] TopologyEdges = CreateTopologyEdges();

	private readonly VertexAccumulator[] _vertices = CreateVertexAccumulators();

	private readonly Dictionary<SilhouetteProfileKey, SilhouetteProfileAccumulator> _silhouettes = new Dictionary<SilhouetteProfileKey, SilhouetteProfileAccumulator>();

	private string _subjectId = "";

	private string _subjectDisplayName = "";

	private DateTime _updatedAtUtc;

	private long _acceptedFrameCount;

	private long _rejectedFrameCount;

	private long _directLandmarkObservationCount;

	private long _hiddenLandmarkRejectionCount;

	private long _silhouetteObservationCount;

	private double _minimumA = double.PositiveInfinity;

	private double _maximumA = double.NegativeInfinity;

	private double _minimumB = double.PositiveInfinity;

	private double _maximumB = double.NegativeInfinity;

	private double _minimumC = double.PositiveInfinity;

	private double _maximumC = double.NegativeInfinity;

	public void Reset(string subjectId, string subjectDisplayName)
	{
		_subjectId = subjectId ?? "";
		_subjectDisplayName = subjectDisplayName ?? "";
		_updatedAtUtc = DateTime.MinValue;
		_acceptedFrameCount = 0L;
		_rejectedFrameCount = 0L;
		_directLandmarkObservationCount = 0L;
		_hiddenLandmarkRejectionCount = 0L;
		_silhouetteObservationCount = 0L;
		_minimumA = double.PositiveInfinity;
		_maximumA = double.NegativeInfinity;
		_minimumB = double.PositiveInfinity;
		_maximumB = double.NegativeInfinity;
		_minimumC = double.PositiveInfinity;
		_maximumC = double.NegativeInfinity;
		_silhouettes.Clear();
		for (int i = 0; i < _vertices.Length; i++)
		{
			_vertices[i].Reset(i);
		}
	}

	public void Restore(MediaPipeNormalizedFaceState? state, string subjectId, string subjectDisplayName)
	{
		Reset(subjectId, subjectDisplayName);
		if (state == null || !string.Equals(state.SchemaVersion, "mediapipe-visible-geometry-v2", StringComparison.Ordinal) || !string.Equals(state.SubjectId, subjectId, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		_updatedAtUtc = state.UpdatedAtUtc;
		_acceptedFrameCount = Math.Max(0L, state.AcceptedFrameCount);
		_rejectedFrameCount = Math.Max(0L, state.RejectedFrameCount);
		_directLandmarkObservationCount = Math.Max(0L, state.DirectLandmarkObservationCount);
		_hiddenLandmarkRejectionCount = Math.Max(0L, state.HiddenLandmarkRejectionCount);
		_silhouetteObservationCount = Math.Max(0L, state.SilhouetteObservationCount);
		if (_acceptedFrameCount > 0)
		{
			_minimumA = state.MinimumARotationDegrees;
			_maximumA = state.MaximumARotationDegrees;
			_minimumB = state.MinimumBRotationDegrees;
			_maximumB = state.MaximumBRotationDegrees;
			_minimumC = state.MinimumCRotationDegrees;
			_maximumC = state.MaximumCRotationDegrees;
		}
		foreach (MediaPipeVertexAccumulatorState vertexAccumulator in state.VertexAccumulators)
		{
			if (vertexAccumulator.Index >= 0 && vertexAccumulator.Index < _vertices.Length)
			{
				_vertices[vertexAccumulator.Index].Restore(vertexAccumulator);
			}
		}
		foreach (MediaPipeSilhouetteProfileState silhouetteAccumulator in state.SilhouetteAccumulators)
		{
			SilhouetteProfileKey key = new SilhouetteProfileKey(silhouetteAccumulator.YawBinDegrees, silhouetteAccumulator.PitchBinDegrees, silhouetteAccumulator.CameraId ?? "camera");
			_silhouettes[key] = SilhouetteProfileAccumulator.Restore(silhouetteAccumulator);
		}
	}

	public bool TryAddFrame(MediaPipeGeometryFrame frame)
	{
		ArgumentNullException.ThrowIfNull(frame, "frame");
		if (frame.Landmarks.Length < 468 || !TryGetPoint(frame.Landmarks, 168, out FaceMeshLandmarkPoint point) || !TryCalculateScale(frame, out var scale) || !RotationMatrix.TryCreate(frame, out var rotation))
		{
			_rejectedFrameCount++;
			return false;
		}
		int num = Math.Min(478, frame.Landmarks.Length);
		Span<NormalizedMeasurement> span = stackalloc NormalizedMeasurement[478];
		double num2 = frame.CRotationAroundZDegrees * Math.PI / 180.0;
		double num3 = Math.Cos(num2);
		double num4 = Math.Sin(num2);
		for (int i = 0; i < num; i++)
		{
			FaceMeshLandmarkPoint obj = frame.Landmarks[i];
			double num5 = (obj.X - point.X) * (double)frame.FrameWidthPixels / scale;
			double num6 = (0.0 - (obj.Y - point.Y) * (double)frame.FrameHeightPixels) / scale;
			double unrolledU = num3 * num5 + num4 * num6;
			double unrolledV = (0.0 - num4) * num5 + num3 * num6;
			span[i] = new NormalizedMeasurement(num5, num6, unrolledU, unrolledV);
		}
		Span<bool> span2 = stackalloc bool[478];
		Span<double> span3 = stackalloc double[18];
		Span<double> span4 = stackalloc double[18];
		Span<int> span5 = stackalloc int[18];
		Span<int> span6 = stackalloc int[18];
		InitializeSilhouetteBands(span3, span4, span5, span6);
		FindSilhouetteEnvelope(span, num, span3, span4, span5, span6);
		for (int j = 0; j < 18; j++)
		{
			if (span5[j] >= 0)
			{
				span2[span5[j]] = true;
			}
			if (span6[j] >= 0)
			{
				span2[span6[j]] = true;
			}
		}
		GetUnrolledFaceExtents(span, num, out var minimum, out var maximum);
		double num7 = Math.Max(0.001, 0.0 - minimum);
		double num8 = Math.Max(0.001, maximum);
		ImageSide imageSide = ((!(num7 <= num8)) ? ImageSide.Right : ImageSide.Left);
		bool flag = Math.Abs(frame.BRotationAroundYDegrees) >= 12.0;
		for (int k = 0; k < num; k++)
		{
			NormalizedMeasurement normalizedMeasurement = span[k];
			bool flag2 = span2[k];
			bool flag3 = ((imageSide == ImageSide.Left) ? (normalizedMeasurement.UnrolledU < -0.045) : (normalizedMeasurement.UnrolledU > 0.045));
			if (flag && flag3 && !flag2)
			{
				_vertices[k].RejectHidden();
				_hiddenLandmarkRejectionCount++;
				continue;
			}
			double num9 = (DynamicIdentityMask[k] ? 0.24 : 1.0);
			if (flag2)
			{
				num9 = Math.Max(num9, 0.9);
			}
			else if (flag)
			{
				num9 *= 0.82;
			}
			_vertices[k].AddObservation(rotation.Row0, rotation.Row1, normalizedMeasurement.U, normalizedMeasurement.V, num9, frame.BRotationAroundYDegrees, frame.ARotationAroundXDegrees);
			_directLandmarkObservationCount++;
		}
		_vertices[168].PinToOrigin(frame.BRotationAroundYDegrees, frame.ARotationAroundXDegrees);
		if (Math.Abs(frame.ARotationAroundXDegrees) <= 20.0 && Math.Abs(frame.CRotationAroundZDegrees) <= 16.0)
		{
			AccumulateSilhouette(frame, span3, span4, span5, span6);
		}
		_acceptedFrameCount++;
		_updatedAtUtc = frame.CapturedAtUtc;
		UpdateAngleRanges(frame);
		return true;
	}

	public MediaPipeNormalizedFaceModel CreateModel()
	{
		MediaPipeNormalizedFaceVertex[] array = new MediaPipeNormalizedFaceVertex[478];
		List<double> list = new List<double>(478);
		int num = 0;
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = _vertices[i].CreateVertex(DynamicIdentityMask[i]);
			if (array[i].DirectObservationCount > 0)
			{
				list.Add(array[i].ResidualPercent);
			}
			if (array[i].EvidenceClass == "directly-measured")
			{
				num++;
			}
		}
		list.Sort();
		double medianResidualPercent = ((list.Count == 0) ? 0.0 : list[list.Count / 2]);
		IReadOnlyList<MediaPipeSilhouetteAngleProfile> readOnlyList = CreateSilhouetteProfiles();
		IReadOnlyList<MediaPipeVisualHullSlice> readOnlyList2 = CreateVisualHullSlices(readOnlyList);
		double num2 = ((array.Length == 0) ? 0.0 : ((double)num * 100.0 / (double)array.Length));
		double num3 = Range(_minimumB, _maximumB);
		string status = ((_acceptedFrameCount == 0L) ? "MediaPipe visible-evidence geometry is waiting for tracked frames." : ((num3 < 15.0) ? $"Collected {_acceptedFrameCount:n0} frames. Turn slowly left and right to make depth observable; current B coverage is {num3:0.#} degrees." : $"Measured {_acceptedFrameCount:n0} frames across {num3:0.#} degrees of B rotation; {num2:0.#}% of landmarks are directly constrained and {readOnlyList2.Count:n0} silhouette slices are available."));
		return new MediaPipeNormalizedFaceModel
		{
			SubjectId = _subjectId,
			SubjectDisplayName = _subjectDisplayName,
			UpdatedAtUtc = _updatedAtUtc,
			AcceptedFrameCount = _acceptedFrameCount,
			RejectedFrameCount = _rejectedFrameCount,
			DirectLandmarkObservationCount = _directLandmarkObservationCount,
			HiddenLandmarkRejectionCount = _hiddenLandmarkRejectionCount,
			SilhouetteObservationCount = _silhouetteObservationCount,
			MinimumARotationDegrees = FiniteOrZero(_minimumA),
			MaximumARotationDegrees = FiniteOrZero(_maximumA),
			MinimumBRotationDegrees = FiniteOrZero(_minimumB),
			MaximumBRotationDegrees = FiniteOrZero(_maximumB),
			MinimumCRotationDegrees = FiniteOrZero(_minimumC),
			MaximumCRotationDegrees = FiniteOrZero(_maximumC),
			AngularBinSizeDegrees = 5.0,
			ConfidentVertexPercent = num2,
			MedianResidualPercent = medianResidualPercent,
			Status = status,
			Vertices = array,
			TopologyEdges = TopologyEdges,
			SilhouetteProfiles = readOnlyList,
			VisualHullSlices = readOnlyList2
		};
	}

	public MediaPipeNormalizedFaceState CreateState()
	{
		MediaPipeVertexAccumulatorState[] array = new MediaPipeVertexAccumulatorState[_vertices.Length];
		for (int i = 0; i < _vertices.Length; i++)
		{
			array[i] = _vertices[i].CreateState();
		}
		List<MediaPipeSilhouetteProfileState> list = new List<MediaPipeSilhouetteProfileState>(_silhouettes.Count);
		foreach (SilhouetteProfileAccumulator value in _silhouettes.Values)
		{
			list.Add(value.CreateState());
		}
		list.Sort(delegate(MediaPipeSilhouetteProfileState left, MediaPipeSilhouetteProfileState right)
		{
			int num = left.YawBinDegrees.CompareTo(right.YawBinDegrees);
			if (num != 0)
			{
				return num;
			}
			int num2 = left.PitchBinDegrees.CompareTo(right.PitchBinDegrees);
			return (num2 == 0) ? string.Compare(left.CameraId, right.CameraId, StringComparison.OrdinalIgnoreCase) : num2;
		});
		return new MediaPipeNormalizedFaceState
		{
			SubjectId = _subjectId,
			SubjectDisplayName = _subjectDisplayName,
			UpdatedAtUtc = _updatedAtUtc,
			AcceptedFrameCount = _acceptedFrameCount,
			RejectedFrameCount = _rejectedFrameCount,
			DirectLandmarkObservationCount = _directLandmarkObservationCount,
			HiddenLandmarkRejectionCount = _hiddenLandmarkRejectionCount,
			SilhouetteObservationCount = _silhouetteObservationCount,
			MinimumARotationDegrees = FiniteOrZero(_minimumA),
			MaximumARotationDegrees = FiniteOrZero(_maximumA),
			MinimumBRotationDegrees = FiniteOrZero(_minimumB),
			MaximumBRotationDegrees = FiniteOrZero(_maximumB),
			MinimumCRotationDegrees = FiniteOrZero(_minimumC),
			MaximumCRotationDegrees = FiniteOrZero(_maximumC),
			VertexAccumulators = array,
			SilhouetteAccumulators = list
		};
	}

	private static bool TryCalculateScale(MediaPipeGeometryFrame frame, out double scale)
	{
		scale = 0.0;
		if (!TryGetPoint(frame.Landmarks, 33, out FaceMeshLandmarkPoint point) || !TryGetPoint(frame.Landmarks, 263, out FaceMeshLandmarkPoint point2) || !TryGetPoint(frame.Landmarks, 10, out FaceMeshLandmarkPoint point3) || !TryGetPoint(frame.Landmarks, 152, out FaceMeshLandmarkPoint point4))
		{
			return false;
		}
		double num = Distance(point, point2, frame.FrameWidthPixels, frame.FrameHeightPixels);
		double num2 = Distance(point3, point4, frame.FrameWidthPixels, frame.FrameHeightPixels);
		double num3 = Math.Max(0.5, Math.Abs(Math.Cos(frame.BRotationAroundYDegrees * Math.PI / 180.0)));
		double num4 = Math.Max(0.6, Math.Abs(Math.Cos(frame.ARotationAroundXDegrees * Math.PI / 180.0)));
		double num5 = num / num3;
		double num6 = num2 / num4;
		scale = Math.Sqrt(Math.Max(0.0, num5 * num6));
		if (double.IsFinite(scale))
		{
			return scale >= 4.0;
		}
		return false;
	}

	private static double Distance(FaceMeshLandmarkPoint first, FaceMeshLandmarkPoint second, int frameWidthPixels, int frameHeightPixels)
	{
		double num = (second.X - first.X) * (double)frameWidthPixels;
		double num2 = (second.Y - first.Y) * (double)frameHeightPixels;
		return Math.Sqrt(num * num + num2 * num2);
	}

	private static bool TryGetPoint(IReadOnlyList<FaceMeshLandmarkPoint> points, int index, out FaceMeshLandmarkPoint point)
	{
		if (index >= 0 && index < points.Count)
		{
			point = points[index];
			if (double.IsFinite(point.X) && double.IsFinite(point.Y))
			{
				return double.IsFinite(point.Z);
			}
			return false;
		}
		point = new FaceMeshLandmarkPoint();
		return false;
	}

	private static void InitializeSilhouetteBands(Span<double> minimum, Span<double> maximum, Span<int> minimumIndex, Span<int> maximumIndex)
	{
		for (int i = 0; i < 18; i++)
		{
			minimum[i] = double.PositiveInfinity;
			maximum[i] = double.NegativeInfinity;
			minimumIndex[i] = -1;
			maximumIndex[i] = -1;
		}
	}

	private static void FindSilhouetteEnvelope(ReadOnlySpan<NormalizedMeasurement> measurements, int pointCount, Span<double> minimum, Span<double> maximum, Span<int> minimumIndex, Span<int> maximumIndex)
	{
		int[] faceOvalIndices = MediaPipeFaceMeshTopology.FaceOvalIndices;
		foreach (int num in faceOvalIndices)
		{
			if (num < 0 || num >= pointCount)
			{
				continue;
			}
			NormalizedMeasurement normalizedMeasurement = measurements[num];
			int num2 = ToSilhouetteBand(normalizedMeasurement.UnrolledV);
			if (num2 >= 0)
			{
				if (normalizedMeasurement.UnrolledU < minimum[num2])
				{
					minimum[num2] = normalizedMeasurement.UnrolledU;
					minimumIndex[num2] = num;
				}
				if (normalizedMeasurement.UnrolledU > maximum[num2])
				{
					maximum[num2] = normalizedMeasurement.UnrolledU;
					maximumIndex[num2] = num;
				}
			}
		}
	}

	private static void GetUnrolledFaceExtents(ReadOnlySpan<NormalizedMeasurement> measurements, int pointCount, out double minimum, out double maximum)
	{
		minimum = double.PositiveInfinity;
		maximum = double.NegativeInfinity;
		int[] faceOvalIndices = MediaPipeFaceMeshTopology.FaceOvalIndices;
		foreach (int num in faceOvalIndices)
		{
			if (num >= 0 && num < pointCount)
			{
				minimum = Math.Min(minimum, measurements[num].UnrolledU);
				maximum = Math.Max(maximum, measurements[num].UnrolledU);
			}
		}
		if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
		{
			minimum = -0.5;
			maximum = 0.5;
		}
	}

	private void AccumulateSilhouette(MediaPipeGeometryFrame frame, ReadOnlySpan<double> minimum, ReadOnlySpan<double> maximum, ReadOnlySpan<int> minimumIndex, ReadOnlySpan<int> maximumIndex)
	{
		int yawBinDegrees = Quantize(frame.BRotationAroundYDegrees, 5.0, -75, 75);
		int pitchBinDegrees = Quantize(frame.ARotationAroundXDegrees, 10.0, -30, 30);
		SilhouetteProfileKey key = new SilhouetteProfileKey(yawBinDegrees, pitchBinDegrees, frame.CameraId);
		if (!_silhouettes.TryGetValue(key, out SilhouetteProfileAccumulator value))
		{
			value = new SilhouetteProfileAccumulator(key);
			_silhouettes.Add(key, value);
		}
		value.FrameCount++;
		for (int i = 0; i < 18; i++)
		{
			if (minimumIndex[i] >= 0 && maximumIndex[i] >= 0 && !(maximum[i] <= minimum[i]))
			{
				value.Bands[i].Add(minimum[i], maximum[i]);
				_silhouetteObservationCount += 2L;
			}
		}
	}

	private IReadOnlyList<MediaPipeSilhouetteAngleProfile> CreateSilhouetteProfiles()
	{
		List<MediaPipeSilhouetteAngleProfile> list = new List<MediaPipeSilhouetteAngleProfile>(_silhouettes.Count);
		foreach (SilhouetteProfileAccumulator value in _silhouettes.Values)
		{
			list.Add(value.CreateProfile());
		}
		list.Sort(delegate(MediaPipeSilhouetteAngleProfile left, MediaPipeSilhouetteAngleProfile right)
		{
			int num = left.YawBinDegrees.CompareTo(right.YawBinDegrees);
			if (num != 0)
			{
				return num;
			}
			int num2 = left.PitchBinDegrees.CompareTo(right.PitchBinDegrees);
			return (num2 == 0) ? string.Compare(left.CameraId, right.CameraId, StringComparison.OrdinalIgnoreCase) : num2;
		});
		return list;
	}

	private static IReadOnlyList<MediaPipeVisualHullSlice> CreateVisualHullSlices(IReadOnlyList<MediaPipeSilhouetteAngleProfile> profiles)
	{
		List<MediaPipeVisualHullSlice> list = new List<MediaPipeVisualHullSlice>(18);
		for (int i = 0; i < 18; i++)
		{
			List<HalfPlaneConstraint> list2 = new List<HalfPlaneConstraint>();
			double num = double.PositiveInfinity;
			double num2 = double.NegativeInfinity;
			long num3 = 0L;
			for (int j = 0; j < profiles.Count; j++)
			{
				MediaPipeSilhouetteAngleProfile mediaPipeSilhouetteAngleProfile = profiles[j];
				if (Math.Abs(mediaPipeSilhouetteAngleProfile.PitchBinDegrees) > 10)
				{
					continue;
				}
				MediaPipeSilhouetteBand mediaPipeSilhouetteBand = null;
				for (int k = 0; k < mediaPipeSilhouetteAngleProfile.Bands.Count; k++)
				{
					if (mediaPipeSilhouetteAngleProfile.Bands[k].BandIndex == i)
					{
						mediaPipeSilhouetteBand = mediaPipeSilhouetteAngleProfile.Bands[k];
						break;
					}
				}
				if (mediaPipeSilhouetteBand != null && mediaPipeSilhouetteBand.ObservationCount >= 2)
				{
					double num4 = (double)mediaPipeSilhouetteAngleProfile.YawBinDegrees * Math.PI / 180.0;
					double num5 = Math.Cos(num4);
					double num6 = Math.Sin(num4);
					list2.Add(new HalfPlaneConstraint(num5, num6, mediaPipeSilhouetteBand.MaximumSupport));
					list2.Add(new HalfPlaneConstraint(0.0 - num5, 0.0 - num6, 0.0 - mediaPipeSilhouetteBand.MinimumSupport));
					num = Math.Min(num, mediaPipeSilhouetteAngleProfile.YawBinDegrees);
					num2 = Math.Max(num2, mediaPipeSilhouetteAngleProfile.YawBinDegrees);
					num3 += mediaPipeSilhouetteBand.ObservationCount;
				}
			}
			int num7 = list2.Count / 2;
			double num8 = Range(num, num2);
			if (num7 < 3 || num8 < 10.0)
			{
				continue;
			}
			List<HullPoint> list3 = ClipVisualHull(list2);
			if (list3.Count < 3)
			{
				continue;
			}
			double confidencePercent = Math.Clamp(Math.Min(1.0, (double)num7 / 12.0) * 45.0 + Math.Min(1.0, num8 / 90.0) * 45.0 + Math.Min(1.0, (double)num3 / 80.0) * 10.0, 0.0, 100.0);
			MediaPipeVisualHullPoint[] array = new MediaPipeVisualHullPoint[list3.Count];
			for (int l = 0; l < list3.Count; l++)
			{
				HullPoint hullPoint = list3[l];
				bool directlyConstrained = false;
				for (int m = 0; m < list2.Count; m++)
				{
					HalfPlaneConstraint halfPlaneConstraint = list2[m];
					if (Math.Abs(halfPlaneConstraint.Nx * hullPoint.X + halfPlaneConstraint.Nz * hullPoint.Z - halfPlaneConstraint.Maximum) <= 0.012)
					{
						directlyConstrained = true;
						break;
					}
				}
				array[l] = new MediaPipeVisualHullPoint
				{
					X = hullPoint.X,
					Z = hullPoint.Z,
					DirectlyConstrained = directlyConstrained
				};
			}
			list.Add(new MediaPipeVisualHullSlice
			{
				BandIndex = i,
				CanonicalY = BandCenter(i),
				ConfidencePercent = confidencePercent,
				AngularCoverageDegrees = num8,
				SupportingAngleCount = num7,
				Boundary = array
			});
		}
		return list;
	}

	private static List<HullPoint> ClipVisualHull(IReadOnlyList<HalfPlaneConstraint> constraints)
	{
		double num = 0.95;
		List<HullPoint> list = new List<HullPoint>
		{
			new HullPoint(0.0 - num, 0.0 - num),
			new HullPoint(num, 0.0 - num),
			new HullPoint(num, num),
			new HullPoint(0.0 - num, num)
		};
		for (int i = 0; i < constraints.Count; i++)
		{
			if (list.Count <= 0)
			{
				break;
			}
			HalfPlaneConstraint halfPlaneConstraint = constraints[i];
			List<HullPoint> list2 = list;
			list = new List<HullPoint>(list2.Count + 2);
			HullPoint hullPoint = list2[list2.Count - 1];
			bool flag = halfPlaneConstraint.Contains(hullPoint);
			for (int j = 0; j < list2.Count; j++)
			{
				HullPoint hullPoint2 = list2[j];
				bool num2 = halfPlaneConstraint.Contains(hullPoint2);
				if (num2 != flag)
				{
					list.Add(halfPlaneConstraint.Intersection(hullPoint, hullPoint2));
				}
				if (num2)
				{
					list.Add(hullPoint2);
				}
				hullPoint = hullPoint2;
				flag = num2;
			}
		}
		return list;
	}

	private void UpdateAngleRanges(MediaPipeGeometryFrame frame)
	{
		_minimumA = Math.Min(_minimumA, frame.ARotationAroundXDegrees);
		_maximumA = Math.Max(_maximumA, frame.ARotationAroundXDegrees);
		_minimumB = Math.Min(_minimumB, frame.BRotationAroundYDegrees);
		_maximumB = Math.Max(_maximumB, frame.BRotationAroundYDegrees);
		_minimumC = Math.Min(_minimumC, frame.CRotationAroundZDegrees);
		_maximumC = Math.Max(_maximumC, frame.CRotationAroundZDegrees);
	}

	private static int ToSilhouetteBand(double canonicalY)
	{
		if (canonicalY < -0.72 || canonicalY > 0.58)
		{
			return -1;
		}
		return Math.Clamp((int)((canonicalY - -0.72) / 1.2999999999999998 * 18.0), 0, 17);
	}

	private static double BandCenter(int bandIndex)
	{
		double num = ((double)bandIndex + 0.5) / 18.0;
		return -0.72 + num * 1.2999999999999998;
	}

	private static int Quantize(double value, double increment, int minimum, int maximum)
	{
		return Math.Clamp((int)Math.Round(value / increment, MidpointRounding.AwayFromZero) * (int)increment, minimum, maximum);
	}

	private static double Range(double minimum, double maximum)
	{
		if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
		{
			return 0.0;
		}
		return Math.Max(0.0, maximum - minimum);
	}

	private static double FiniteOrZero(double value)
	{
		if (!double.IsFinite(value))
		{
			return 0.0;
		}
		return value;
	}

	private static bool[] CreateIndexMask(IReadOnlyList<int> indices)
	{
		bool[] array = new bool[478];
		foreach (int index in indices)
		{
			if (index >= 0 && index < array.Length)
			{
				array[index] = true;
			}
		}
		return array;
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

	private static MeshTopologyEdge[] CreateTopologyEdges()
	{
		MeshTopologyEdge[] array = new MeshTopologyEdge[MediaPipeFaceMeshTopology.TessellationEdges.Length];
		for (int i = 0; i < array.Length; i++)
		{
			(int, int) tuple = MediaPipeFaceMeshTopology.TessellationEdges[i];
			array[i] = new MeshTopologyEdge
			{
				FromIndex = tuple.Item1,
				ToIndex = tuple.Item2,
				Role = "surface",
				Source = "mediapipe-face-tessellation",
				ConfidencePercent = 100.0
			};
		}
		return array;
	}
}
