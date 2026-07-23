using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AvatarBuilder.Modules.Vision.MediaPipe;
using AvatarBuilder.Modules.Webcam.DirectX12;
using OpenCvSharp;

namespace AvatarBuilder.Modules.Webcam.DualCamera;

internal sealed class DualCameraRegistrationCoordinator : IAsyncDisposable
{
	private readonly record struct MeshTriangle(int A, int B, int C);

	private readonly record struct CameraParameters(double Fx, double Fy, double Cx, double Cy, int Width, int Height, IReadOnlyList<double> Distortion)
	{
		public static CameraParameters Create(IReadOnlyList<double> matrix, IReadOnlyList<double> distortion, int calibrationWidth, int calibrationHeight, int frameWidth, int frameHeight)
		{
			double num = (double)frameWidth / Math.Max(1.0, calibrationWidth);
			double num2 = (double)frameHeight / Math.Max(1.0, calibrationHeight);
			return new CameraParameters(matrix[0] * num, matrix[4] * num2, matrix[2] * num, matrix[5] * num2, frameWidth, frameHeight, distortion);
		}

		public Point2d Undistort(double pixelX, double pixelY)
		{
			double num = (pixelX - Cx) / Math.Max(1E-09, Fx);
			double num2 = (pixelY - Cy) / Math.Max(1E-09, Fy);
			double num3 = num;
			double num4 = num2;
			double num5 = Coefficient(0);
			double num6 = Coefficient(1);
			double num7 = Coefficient(2);
			double num8 = Coefficient(3);
			double num9 = Coefficient(4);
			double num10 = Coefficient(5);
			double num11 = Coefficient(6);
			double num12 = Coefficient(7);
			for (int i = 0; i < 8; i++)
			{
				double num13 = num3 * num3 + num4 * num4;
				double num14 = num13 * num13;
				double num15 = num14 * num13;
				double val = 1.0 + num10 * num13 + num11 * num14 + num12 * num15;
				double val2 = (1.0 + num5 * num13 + num6 * num14 + num9 * num15) / Math.Max(1E-09, val);
				double num16 = 2.0 * num7 * num3 * num4 + num8 * (num13 + 2.0 * num3 * num3);
				double num17 = num7 * (num13 + 2.0 * num4 * num4) + 2.0 * num8 * num3 * num4;
				num3 = (num - num16) / Math.Max(1E-09, val2);
				num4 = (num2 - num17) / Math.Max(1E-09, val2);
			}
			return new Point2d(num3, num4);
		}

		public PreviewOverlayPoint Project(double x, double y, double z)
		{
			if (!double.IsFinite(z) || z <= 1E-09)
			{
				return InvalidPoint;
			}
			double num = x / z;
			double num2 = y / z;
			double num3 = num * num + num2 * num2;
			double num4 = num3 * num3;
			double num5 = num4 * num3;
			double val = 1.0 + Coefficient(5) * num3 + Coefficient(6) * num4 + Coefficient(7) * num5;
			double num6 = (1.0 + Coefficient(0) * num3 + Coefficient(1) * num4 + Coefficient(4) * num5) / Math.Max(1E-09, val);
			double num7 = 2.0 * Coefficient(2) * num * num2 + Coefficient(3) * (num3 + 2.0 * num * num);
			double num8 = Coefficient(2) * (num3 + 2.0 * num2 * num2) + 2.0 * Coefficient(3) * num * num2;
			double num9 = num * num6 + num7;
			double num10 = num2 * num6 + num8;
			double num11 = Fx * num9 + Cx;
			return new PreviewOverlayPoint(Y: (Fy * num10 + Cy) / Math.Max(1.0, Height), X: num11 / Math.Max(1.0, Width));
		}

		private double Coefficient(int index)
		{
			if ((uint)index >= (uint)Distortion.Count)
			{
				return 0.0;
			}
			return Distortion[index];
		}
	}

	private readonly record struct SimilarityTransform3D(Quaternion Rotation, double Scale, Vector3 Translation)
	{
		public static bool TryCreate(DualCameraObservation source, DualCameraObservation target, out SimilarityTransform3D transform)
		{
			transform = default(SimilarityTransform3D);
			Span<int> span = stackalloc int[StableAnchorIndices.Length];
			int num = 0;
			int[] stableAnchorIndices = StableAnchorIndices;
			foreach (int num2 in stableAnchorIndices)
			{
				if ((uint)num2 < (uint)source.Landmarks.Count && (uint)num2 < (uint)target.Landmarks.Count && source.Landmarks[num2].IsValid && target.Landmarks[num2].IsValid)
				{
					span[num++] = num2;
				}
			}
			if (num >= 5)
			{
				return TrySolve(source, target, span.Slice(0, num), out transform);
			}
			return false;
		}

		public Vector3 Transform(DualCameraLandmark point)
		{
			return Transform(new Vector3((float)point.X, (float)point.Y, (float)point.Z));
		}

		public Vector3 Transform(Vector3 point)
		{
			return Vector3.Transform(point, Rotation) * (float)Scale + Translation;
		}

		public SimilarityTransform3D Inverse()
		{
			Quaternion rotation = Quaternion.Conjugate(Rotation);
			double num = 1.0 / Math.Max(1E-08, Scale);
			Vector3 translation = Vector3.Transform(-Translation, rotation) * (float)num;
			return new SimilarityTransform3D(rotation, num, translation);
		}

		private static bool TrySolve(DualCameraObservation source, DualCameraObservation target, ReadOnlySpan<int> anchors, out SimilarityTransform3D transform)
		{
			transform = default(SimilarityTransform3D);
			Vector3 zero = Vector3.Zero;
			Vector3 zero2 = Vector3.Zero;
			ReadOnlySpan<int> readOnlySpan = anchors;
			for (int i = 0; i < readOnlySpan.Length; i++)
			{
				int index = readOnlySpan[i];
				zero += ToVector(source.Landmarks[index]);
				zero2 += ToVector(target.Landmarks[index]);
			}
			zero /= (float)anchors.Length;
			zero2 /= (float)anchors.Length;
			double num = 0.0;
			double num2 = 0.0;
			double num3 = 0.0;
			double num4 = 0.0;
			double num5 = 0.0;
			double num6 = 0.0;
			double num7 = 0.0;
			double num8 = 0.0;
			double num9 = 0.0;
			double num10 = 0.0;
			ReadOnlySpan<int> readOnlySpan2 = anchors;
			for (int i = 0; i < readOnlySpan2.Length; i++)
			{
				int index2 = readOnlySpan2[i];
				Vector3 vector = ToVector(source.Landmarks[index2]) - zero;
				Vector3 vector2 = ToVector(target.Landmarks[index2]) - zero2;
				num += (double)(vector.X * vector2.X);
				num2 += (double)(vector.X * vector2.Y);
				num3 += (double)(vector.X * vector2.Z);
				num4 += (double)(vector.Y * vector2.X);
				num5 += (double)(vector.Y * vector2.Y);
				num6 += (double)(vector.Y * vector2.Z);
				num7 += (double)(vector.Z * vector2.X);
				num8 += (double)(vector.Z * vector2.Y);
				num9 += (double)(vector.Z * vector2.Z);
				num10 += (double)vector.LengthSquared();
			}
			if (num10 <= 1E-10)
			{
				return false;
			}
			Quaternion rotation = DominantQuaternion(num, num2, num3, num4, num5, num6, num7, num8, num9);
			double num11 = 0.0;
			ReadOnlySpan<int> readOnlySpan3 = anchors;
			for (int i = 0; i < readOnlySpan3.Length; i++)
			{
				int index3 = readOnlySpan3[i];
				Vector3 value = ToVector(source.Landmarks[index3]) - zero;
				Vector3 vector3 = ToVector(target.Landmarks[index3]) - zero2;
				num11 += (double)Vector3.Dot(vector3, Vector3.Transform(value, rotation));
			}
			double num12 = num11 / num10;
			if (!double.IsFinite(num12) || num12 <= 1E-05 || num12 > 100.0)
			{
				return false;
			}
			Vector3 translation = zero2 - Vector3.Transform(zero, rotation) * (float)num12;
			transform = new SimilarityTransform3D(rotation, num12, translation);
			return true;
		}

		private static Quaternion DominantQuaternion(double sxx, double sxy, double sxz, double syx, double syy, double syz, double szx, double szy, double szz)
		{
			double[,] array = new double[4, 4];
			array[0, 0] = sxx + syy + szz;
			array[0, 1] = syz - szy;
			array[0, 2] = szx - sxz;
			array[0, 3] = sxy - syx;
			array[1, 0] = array[0, 1];
			array[1, 1] = sxx - syy - szz;
			array[1, 2] = sxy + syx;
			array[1, 3] = szx + sxz;
			array[2, 0] = array[0, 2];
			array[2, 1] = array[1, 2];
			array[2, 2] = 0.0 - sxx + syy - szz;
			array[2, 3] = syz + szy;
			array[3, 0] = array[0, 3];
			array[3, 1] = array[1, 3];
			array[3, 2] = array[2, 3];
			array[3, 3] = 0.0 - sxx - syy + szz;
			double num = 0.0;
			for (int i = 0; i < 4; i++)
			{
				double num2 = 0.0;
				for (int j = 0; j < 4; j++)
				{
					num2 += Math.Abs(array[i, j]);
				}
				num = Math.Max(num, num2 + 1E-09);
			}
			for (int k = 0; k < 4; k++)
			{
				array[k, k] += num;
			}
			Span<double> span = stackalloc double[4] { 4.6053809789490693E+18, 4.600877379321699E+18, 4.5990759394707507E+18, 4.5963737796943283E+18 };
			Span<double> span2 = stackalloc double[4];
			for (int l = 0; l < 40; l++)
			{
				for (int m = 0; m < 4; m++)
				{
					span2[m] = 0.0;
					for (int n = 0; n < 4; n++)
					{
						span2[m] += array[m, n] * span[n];
					}
				}
				double num3 = Math.Sqrt(span2[0] * span2[0] + span2[1] * span2[1] + span2[2] * span2[2] + span2[3] * span2[3]);
				if (num3 <= 1E-12)
				{
					return Quaternion.Identity;
				}
				for (int num4 = 0; num4 < 4; num4++)
				{
					span[num4] = span2[num4] / num3;
				}
			}
			return Quaternion.Normalize(new Quaternion((float)span[1], (float)span[2], (float)span[3], (float)span[0]));
		}

		private static Vector3 ToVector(DualCameraLandmark point)
		{
			return new Vector3((float)point.X, (float)point.Y, (float)point.Z);
		}
	}

	private static readonly TimeSpan MaximumPairSkew = TimeSpan.FromMilliseconds(250L);

	private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2L);

	private static readonly int[] StableAnchorIndices = new int[13]
	{
		10, 33, 133, 168, 263, 362, 1, 4, 127, 234,
		356, 454, 152
	};

	private static readonly MeshTriangle[] MeshTriangles = CreateMeshTriangles();

	private static readonly int[][] IncidentTriangles = CreateIncidentTriangleMap(MeshTriangles);

	private readonly object _observationLock = new object();

	private readonly AutoResetEvent _signal = new AutoResetEvent(initialState: false);

	private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

	private readonly Task _worker;

	private DualCameraObservation? _cameraAObservation;

	private DualCameraObservation? _cameraBObservation;

	private long _lastCameraATicks;

	private long _lastCameraBTicks;

	private long _lastLiveStatusTimestamp;

	private int _enabled;

	private int _disposed;

	private DualCameraCalibrationModel? _physicalCalibration;

	public bool IsEnabled => Volatile.Read(in _enabled) != 0;

	private static PreviewOverlayPoint InvalidPoint => new PreviewOverlayPoint(double.NaN, double.NaN);

	public event Action<DualCameraRegistrationFrame, DualCameraRegistrationFrame, DualCameraRegistrationDiagnostics>? RegistrationAvailable;

	public event Action<string>? StatusChanged;

	public DualCameraRegistrationCoordinator()
	{
		_worker = Task.Factory.StartNew(RunWorker, _cancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
	}

	public void SetPhysicalCalibration(DualCameraCalibrationModel? calibration)
	{
		Volatile.Write(ref _physicalCalibration, ((object)calibration != null && calibration.IsUsable) ? calibration : null);
	}

	public void SetEnabled(bool enabled)
	{
		Volatile.Write(ref _enabled, enabled ? 1 : 0);
		if (!enabled)
		{
			lock (_observationLock)
			{
				_cameraAObservation = null;
				_cameraBObservation = null;
			}
		}
		_signal.Set();
	}

	public void Submit(bool cameraA, DualCameraObservation observation)
	{
		if (!IsEnabled || Volatile.Read(in _disposed) != 0)
		{
			return;
		}
		lock (_observationLock)
		{
			if (cameraA)
			{
				_cameraAObservation = observation;
			}
			else
			{
				_cameraBObservation = observation;
			}
		}
		_signal.Set();
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
		{
			Volatile.Write(ref _enabled, 0);
			_cancellation.Cancel();
			_signal.Set();
			try
			{
				await _worker.WaitAsync(ShutdownGrace);
			}
			catch
			{
			}
			_signal.Dispose();
			_cancellation.Dispose();
		}
	}

	private void RunWorker()
	{
		while (!_cancellation.IsCancellationRequested)
		{
			try
			{
				_signal.WaitOne(100);
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			if (IsEnabled && TryTakeLatestPair(out DualCameraObservation cameraA, out DualCameraObservation cameraB))
			{
				try
				{
					ProcessPair(cameraA, cameraB);
				}
				catch (Exception ex2)
				{
					this.StatusChanged?.Invoke("Translation skipped one pair: " + ex2.Message);
				}
			}
		}
	}

	private bool TryTakeLatestPair(out DualCameraObservation cameraA, out DualCameraObservation cameraB)
	{
		lock (_observationLock)
		{
			cameraA = _cameraAObservation;
			cameraB = _cameraBObservation;
		}
		if ((object)cameraA == null || (object)cameraB == null)
		{
			return false;
		}
		long ticks = cameraA.CapturedAtUtc.Ticks;
		long ticks2 = cameraB.CapturedAtUtc.Ticks;
		if (ticks == Volatile.Read(in _lastCameraATicks) && ticks2 == Volatile.Read(in _lastCameraBTicks))
		{
			return false;
		}
		if ((cameraA.CapturedAtUtc - cameraB.CapturedAtUtc).Duration() > MaximumPairSkew)
		{
			return false;
		}
		Volatile.Write(ref _lastCameraATicks, ticks);
		Volatile.Write(ref _lastCameraBTicks, ticks2);
		return true;
	}

	private void ProcessPair(DualCameraObservation cameraA, DualCameraObservation cameraB)
	{
		DualCameraCalibrationModel dualCameraCalibrationModel = Volatile.Read(in _physicalCalibration);
		if ((object)dualCameraCalibrationModel != null && TryCreatePhysicalRegistration(cameraA, cameraB, dualCameraCalibrationModel, out DualCameraRegistrationFrame frameA, out DualCameraRegistrationFrame frameB, out DualCameraRegistrationDiagnostics diagnostics))
		{
			this.RegistrationAvailable?.Invoke(frameA, frameB, diagnostics);
			PublishLiveStatus(diagnostics.ToStatusText());
			return;
		}
		if (!SimilarityTransform3D.TryCreate(cameraB, cameraA, out var transform))
		{
			this.StatusChanged?.Invoke("Translation waiting for enough shared stable landmarks.");
			return;
		}
		double[] array = CalculateDirectness(cameraA);
		double[] array2 = CalculateDirectness(cameraB);
		DualCameraRegistrationFrame dualCameraRegistrationFrame = CreateTargetFrame(cameraA, cameraB, transform, array, array2);
		DualCameraRegistrationFrame arg = CreateTargetFrame(cameraB, cameraA, transform.Inverse(), array2, array);
		double rootMeanSquareResidualPercent = CalculateSymmetricResidualPercent(cameraA, cameraB, transform);
		DualCameraRegistrationDiagnostics dualCameraRegistrationDiagnostics = new DualCameraRegistrationDiagnostics(cameraA.CapturedAtUtc, cameraB.CapturedAtUtc, (cameraA.CapturedAtUtc - cameraB.CapturedAtUtc).Duration(), rootMeanSquareResidualPercent, dualCameraRegistrationFrame.TargetOwnedPointCount, dualCameraRegistrationFrame.PartnerOwnedPointCount, Volatile.Read(in _physicalCalibration)?.BaselineInches);
		this.RegistrationAvailable?.Invoke(dualCameraRegistrationFrame, arg, dualCameraRegistrationDiagnostics);
		PublishLiveStatus(dualCameraRegistrationDiagnostics.ToStatusText());
	}

	private void PublishLiveStatus(string status)
	{
		long timestamp = Stopwatch.GetTimestamp();
		long num = Volatile.Read(in _lastLiveStatusTimestamp);
		if (num == 0L || Stopwatch.GetElapsedTime(num, timestamp) >= TimeSpan.FromMilliseconds(500L))
		{
			Volatile.Write(ref _lastLiveStatusTimestamp, timestamp);
			this.StatusChanged?.Invoke(status);
		}
	}

	private static bool TryCreatePhysicalRegistration(DualCameraObservation cameraA, DualCameraObservation cameraB, DualCameraCalibrationModel calibration, out DualCameraRegistrationFrame frameA, out DualCameraRegistrationFrame frameB, out DualCameraRegistrationDiagnostics diagnostics)
	{
		frameA = null;
		frameB = null;
		diagnostics = null;
		if (cameraA.FrameWidth <= 0 || cameraA.FrameHeight <= 0 || cameraB.FrameWidth <= 0 || cameraB.FrameHeight <= 0)
		{
			return false;
		}
		int num = Math.Min(cameraA.Landmarks.Count, cameraB.Landmarks.Count);
		if (num < 5)
		{
			return false;
		}
		List<int> list = new List<int>(num);
		List<Point2d> list2 = new List<Point2d>(num);
		List<Point2d> list3 = new List<Point2d>(num);
		CameraParameters cameraParameters = CameraParameters.Create(calibration.CameraAMatrix, calibration.CameraADistortion, calibration.ImageWidth, calibration.ImageHeight, cameraA.FrameWidth, cameraA.FrameHeight);
		CameraParameters cameraParameters2 = CameraParameters.Create(calibration.CameraBMatrix, calibration.CameraBDistortion, calibration.ImageWidth, calibration.ImageHeight, cameraB.FrameWidth, cameraB.FrameHeight);
		for (int i = 0; i < num; i++)
		{
			DualCameraLandmark dualCameraLandmark = cameraA.Landmarks[i];
			DualCameraLandmark dualCameraLandmark2 = cameraB.Landmarks[i];
			if (dualCameraLandmark.IsValid && dualCameraLandmark2.IsValid)
			{
				list.Add(i);
				list2.Add(cameraParameters.Undistort(dualCameraLandmark.X * (double)cameraA.FrameWidth, dualCameraLandmark.Y * (double)cameraA.FrameHeight));
				list3.Add(cameraParameters2.Undistort(dualCameraLandmark2.X * (double)cameraB.FrameWidth, dualCameraLandmark2.Y * (double)cameraB.FrameHeight));
			}
		}
		if (list.Count < 5)
		{
			return false;
		}
		double[] cameraAToBRotation = calibration.CameraAToBRotation;
		double[] cameraAToBTranslationInches = calibration.CameraAToBTranslationInches;
		using Mat mat = new Mat(3, 4, MatType.CV_32FC1, Scalar.All(0.0));
		mat.Set(0, 0, 1f);
		mat.Set(1, 1, 1f);
		mat.Set(2, 2, 1f);
		using Mat mat2 = new Mat(3, 4, MatType.CV_32FC1);
		for (int j = 0; j < 3; j++)
		{
			for (int k = 0; k < 3; k++)
			{
				mat2.Set(j, k, (float)cameraAToBRotation[j * 3 + k]);
			}
			mat2.Set(j, 3, (float)cameraAToBTranslationInches[j]);
		}
		using Mat mat3 = new Mat(2, list.Count, MatType.CV_32FC1);
		using Mat mat4 = new Mat(2, list.Count, MatType.CV_32FC1);
		for (int l = 0; l < list.Count; l++)
		{
			mat3.Set(0, l, (float)list2[l].X);
			mat3.Set(1, l, (float)list2[l].Y);
			mat4.Set(0, l, (float)list3[l].X);
			mat4.Set(1, l, (float)list3[l].Y);
		}
		using Mat mat5 = new Mat();
		Cv2.TriangulatePoints(mat, mat2, mat3, mat4, mat5);
		if (mat5.Rows != 4 || mat5.Cols != list.Count)
		{
			return false;
		}
		PreviewOverlayPoint[] array = CreateInvalidPointArray(num);
		PreviewOverlayPoint[] array2 = CreateInvalidPointArray(num);
		PreviewOverlayPoint[] array3 = CreateInvalidPointArray(num);
		PreviewOverlayPoint[] array4 = CreateInvalidPointArray(num);
		DualCameraRigPoint[] array5 = new DualCameraRigPoint[num];
		double[] array6 = CalculateDirectness(cameraA);
		double[] array7 = CalculateDirectness(cameraB);
		double num2 = MedianPositive(array6);
		double num3 = MedianPositive(array7);
		double targetGlobalScore = GlobalDirectness(cameraA);
		double partnerGlobalScore = GlobalDirectness(cameraB);
		double num4 = Math.Max(1E-06, (FaceWidth(cameraA) + FaceWidth(cameraB)) * 0.5);
		int num5 = 0;
		int num6 = 0;
		int num7 = 0;
		double num8 = 0.0;
		int num9 = 0;
		for (int m = 0; m < list.Count; m++)
		{
			float num10 = mat5.At<float>(3, m);
			if (!double.IsFinite(num10) || (double)Math.Abs(num10) <= 1E-09)
			{
				continue;
			}
			float num11 = mat5.At<float>(0, m) / num10;
			float num12 = mat5.At<float>(1, m) / num10;
			float num13 = mat5.At<float>(2, m) / num10;
			if (!double.IsFinite(num11) || !double.IsFinite(num12) || !double.IsFinite(num13) || (double)num13 <= 0.0)
			{
				continue;
			}
			int num14 = list[m];
			PreviewOverlayPoint previewOverlayPoint = cameraParameters.Project(num11, num12, num13);
			Vector3 vector = TransformPoint(cameraAToBRotation, cameraAToBTranslationInches, num11, num12, num13);
			PreviewOverlayPoint previewOverlayPoint2 = cameraParameters2.Project(vector.X, vector.Y, vector.Z);
			if (IsDrawable(previewOverlayPoint.X, previewOverlayPoint.Y) && IsDrawable(previewOverlayPoint2.X, previewOverlayPoint2.Y))
			{
				array[num14] = previewOverlayPoint;
				array2[num14] = previewOverlayPoint2;
				DualCameraLandmark dualCameraLandmark3 = cameraA.Landmarks[num14];
				DualCameraLandmark dualCameraLandmark4 = cameraB.Landmarks[num14];
				double num15 = Math.Sqrt((Math.Pow(previewOverlayPoint.X - dualCameraLandmark3.X, 2.0) + Math.Pow(previewOverlayPoint.Y - dualCameraLandmark3.Y, 2.0) + Math.Pow(previewOverlayPoint2.X - dualCameraLandmark4.X, 2.0) + Math.Pow(previewOverlayPoint2.Y - dualCameraLandmark4.Y, 2.0)) * 0.5) / num4 * 100.0;
				double num16 = array6[num14] / num2;
				double num17 = array7[num14] / num3;
				bool isDirectlyMeasured = num15 <= 2.75 && Math.Min(num16, num17) >= 0.08 && Math.Max(num16, num17) >= 0.35;
				array5[num14] = new DualCameraRigPoint(num11, num12, num13, IsValid: true, num15, num16, num17, isDirectlyMeasured);
				num7++;
				if (IsPartnerMoreDirect(array6[num14], array7[num14], targetGlobalScore, partnerGlobalScore))
				{
					array3[num14] = previewOverlayPoint;
					array4[num14] = new PreviewOverlayPoint(dualCameraLandmark4.X, dualCameraLandmark4.Y);
					num6++;
				}
				else
				{
					array3[num14] = new PreviewOverlayPoint(dualCameraLandmark3.X, dualCameraLandmark3.Y);
					array4[num14] = previewOverlayPoint2;
					num5++;
				}
				num8 += Math.Pow(previewOverlayPoint.X - dualCameraLandmark3.X, 2.0) + Math.Pow(previewOverlayPoint.Y - dualCameraLandmark3.Y, 2.0);
				num8 += Math.Pow(previewOverlayPoint2.X - dualCameraLandmark4.X, 2.0) + Math.Pow(previewOverlayPoint2.Y - dualCameraLandmark4.Y, 2.0);
				num9 += 2;
			}
		}
		if (num7 < 5)
		{
			return false;
		}
		double rootMeanSquareResidualPercent = Math.Sqrt(num8 / (double)Math.Max(1, num9)) / num4 * 100.0;
		frameA = new DualCameraRegistrationFrame(cameraA.CapturedAtUtc, array, array3, array5, num5, num6);
		frameB = new DualCameraRegistrationFrame(cameraB.CapturedAtUtc, array2, array4, array5, num6, num5);
		diagnostics = new DualCameraRegistrationDiagnostics(cameraA.CapturedAtUtc, cameraB.CapturedAtUtc, (cameraA.CapturedAtUtc - cameraB.CapturedAtUtc).Duration(), rootMeanSquareResidualPercent, num5, num6, calibration.BaselineInches, num7, cameraA.TrackingConfidence, cameraB.TrackingConfidence, new DualCameraDenseStereoSource(cameraA, cameraB, calibration));
		return true;
	}

	private static double MedianPositive(IReadOnlyList<double> values)
	{
		Span<double> span = ((values.Count > 512) ? ((Span<double>)new double[values.Count]) : stackalloc double[values.Count]);
		Span<double> span2 = span;
		int num = 0;
		for (int i = 0; i < values.Count; i++)
		{
			double num2 = values[i];
			if (double.IsFinite(num2) && num2 > 0.0)
			{
				span2[num++] = num2;
			}
		}
		if (num > 1)
		{
			span2.Slice(0, num).Sort();
		}
		if (num != 0)
		{
			return Math.Max(1E-09, span2[num / 2]);
		}
		return 1.0;
	}

	private static PreviewOverlayPoint[] CreateInvalidPointArray(int count)
	{
		PreviewOverlayPoint[] array = new PreviewOverlayPoint[count];
		Array.Fill(array, InvalidPoint);
		return array;
	}

	private static Vector3 TransformPoint(IReadOnlyList<double> rotation, IReadOnlyList<double> translation, double x, double y, double z)
	{
		return new Vector3((float)(rotation[0] * x + rotation[1] * y + rotation[2] * z + translation[0]), (float)(rotation[3] * x + rotation[4] * y + rotation[5] * z + translation[1]), (float)(rotation[6] * x + rotation[7] * y + rotation[8] * z + translation[2]));
	}

	private static DualCameraRegistrationFrame CreateTargetFrame(DualCameraObservation target, DualCameraObservation partner, SimilarityTransform3D partnerToTarget, IReadOnlyList<double> targetDirectness, IReadOnlyList<double> partnerDirectness)
	{
		int num = Math.Min(target.Landmarks.Count, partner.Landmarks.Count);
		PreviewOverlayPoint[] array = new PreviewOverlayPoint[num];
		PreviewOverlayPoint[] array2 = new PreviewOverlayPoint[num];
		Array.Fill(array, InvalidPoint);
		Array.Fill(array2, InvalidPoint);
		double targetGlobalScore = GlobalDirectness(target);
		double partnerGlobalScore = GlobalDirectness(partner);
		int num2 = 0;
		int num3 = 0;
		for (int i = 0; i < num; i++)
		{
			DualCameraLandmark dualCameraLandmark = target.Landmarks[i];
			DualCameraLandmark point = partner.Landmarks[i];
			bool flag = false;
			PreviewOverlayPoint previewOverlayPoint = default(PreviewOverlayPoint);
			if (point.IsValid)
			{
				Vector3 vector = partnerToTarget.Transform(point);
				flag = IsDrawable(vector.X, vector.Y);
				if (flag)
				{
					previewOverlayPoint = (array[i] = new PreviewOverlayPoint(vector.X, vector.Y));
				}
			}
			if (dualCameraLandmark.IsValid || flag)
			{
				if (flag && (!dualCameraLandmark.IsValid || IsPartnerMoreDirect(targetDirectness[i], partnerDirectness[i], targetGlobalScore, partnerGlobalScore)))
				{
					array2[i] = previewOverlayPoint;
					num3++;
				}
				else
				{
					array2[i] = new PreviewOverlayPoint(dualCameraLandmark.X, dualCameraLandmark.Y);
					num2++;
				}
			}
		}
		return new DualCameraRegistrationFrame(target.CapturedAtUtc, array, array2, Array.Empty<DualCameraRigPoint>(), num2, num3);
	}

	private static bool IsPartnerMoreDirect(double targetScore, double partnerScore, double targetGlobalScore, double partnerGlobalScore)
	{
		if (partnerScore > targetScore * 1.08)
		{
			return true;
		}
		if (targetScore > partnerScore * 1.08)
		{
			return false;
		}
		return partnerGlobalScore > targetGlobalScore;
	}

	private static double[] CalculateDirectness(DualCameraObservation observation)
	{
		double[] array = new double[observation.Landmarks.Count];
		double num = FaceWidth(observation);
		double num2 = Math.Max(1E-08, num * num);
		for (int i = 0; i < array.Length && i < IncidentTriangles.Length; i++)
		{
			if (!observation.Landmarks[i].IsValid)
			{
				continue;
			}
			int[] obj = IncidentTriangles[i];
			double num3 = 0.0;
			int num4 = 0;
			int[] array2 = obj;
			foreach (int num5 in array2)
			{
				MeshTriangle meshTriangle = MeshTriangles[num5];
				DualCameraLandmark dualCameraLandmark = observation.Landmarks[meshTriangle.A];
				DualCameraLandmark dualCameraLandmark2 = observation.Landmarks[meshTriangle.B];
				DualCameraLandmark dualCameraLandmark3 = observation.Landmarks[meshTriangle.C];
				if (dualCameraLandmark.IsValid && dualCameraLandmark2.IsValid && dualCameraLandmark3.IsValid)
				{
					num3 += Math.Abs((dualCameraLandmark2.X - dualCameraLandmark.X) * (dualCameraLandmark3.Y - dualCameraLandmark.Y) - (dualCameraLandmark2.Y - dualCameraLandmark.Y) * (dualCameraLandmark3.X - dualCameraLandmark.X)) * 0.5;
					num4++;
				}
			}
			if (num4 > 0)
			{
				array[i] = num3 / (double)num4 / num2;
			}
		}
		return array;
	}

	private static double GlobalDirectness(DualCameraObservation observation)
	{
		double num = Math.Abs(observation.HeadYawDegrees) / 45.0;
		double num2 = Math.Abs(observation.HeadPitchDegrees) / 55.0;
		return observation.TrackingConfidence / (1.0 + num * num + num2 * num2);
	}

	private static double CalculateSymmetricResidualPercent(DualCameraObservation cameraA, DualCameraObservation cameraB, SimilarityTransform3D cameraBToA)
	{
		double num = CalculateResidual(cameraA, cameraB, cameraBToA);
		double num2 = CalculateResidual(cameraB, cameraA, cameraBToA.Inverse());
		return (num + num2) * 50.0;
	}

	private static double CalculateResidual(DualCameraObservation target, DualCameraObservation source, SimilarityTransform3D sourceToTarget)
	{
		int num = Math.Min(target.Landmarks.Count, source.Landmarks.Count);
		double num2 = 0.0;
		int num3 = 0;
		for (int i = 0; i < num; i++)
		{
			DualCameraLandmark dualCameraLandmark = target.Landmarks[i];
			DualCameraLandmark point = source.Landmarks[i];
			if (dualCameraLandmark.IsValid && point.IsValid)
			{
				Vector3 vector = sourceToTarget.Transform(point);
				double num4 = (double)vector.X - dualCameraLandmark.X;
				double num5 = (double)vector.Y - dualCameraLandmark.Y;
				num2 += num4 * num4 + num5 * num5;
				num3++;
			}
		}
		if (num3 == 0)
		{
			return double.PositiveInfinity;
		}
		return Math.Sqrt(num2 / (double)num3) / Math.Max(1E-06, FaceWidth(target));
	}

	private static double FaceWidth(DualCameraObservation observation)
	{
		if (TryDistance(observation.Landmarks, 234, 454, out var distance))
		{
			return distance;
		}
		if (TryDistance(observation.Landmarks, 33, 263, out var distance2))
		{
			return distance2 * 1.8;
		}
		return 0.25;
	}

	private static bool TryDistance(IReadOnlyList<DualCameraLandmark> points, int firstIndex, int secondIndex, out double distance)
	{
		distance = 0.0;
		if ((uint)firstIndex >= (uint)points.Count || (uint)secondIndex >= (uint)points.Count)
		{
			return false;
		}
		DualCameraLandmark dualCameraLandmark = points[firstIndex];
		DualCameraLandmark dualCameraLandmark2 = points[secondIndex];
		if (!dualCameraLandmark.IsValid || !dualCameraLandmark2.IsValid)
		{
			return false;
		}
		distance = Math.Sqrt(Math.Pow(dualCameraLandmark.X - dualCameraLandmark2.X, 2.0) + Math.Pow(dualCameraLandmark.Y - dualCameraLandmark2.Y, 2.0));
		return distance > 1E-06;
	}

	private static bool IsDrawable(double x, double y)
	{
		if (double.IsFinite(x) && double.IsFinite(y) && x >= -0.15 && x <= 1.15 && y >= -0.15)
		{
			return y <= 1.15;
		}
		return false;
	}

	private static MeshTriangle[] CreateMeshTriangles()
	{
		(int, int)[] tessellationEdges = MediaPipeFaceMeshTopology.TessellationEdges;
		HashSet<int>[] array = new HashSet<int>[468];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = new HashSet<int>();
		}
		(int, int)[] array2 = tessellationEdges;
		for (int j = 0; j < array2.Length; j++)
		{
			(int, int) tuple = array2[j];
			if ((uint)tuple.Item1 < (uint)array.Length && (uint)tuple.Item2 < (uint)array.Length)
			{
				array[tuple.Item1].Add(tuple.Item2);
				array[tuple.Item2].Add(tuple.Item1);
			}
		}
		List<MeshTriangle> list = new List<MeshTriangle>();
		int a;
		for (a = 0; a < array.Length; a++)
		{
			int[] array3 = array[a].Where((int index) => index > a).Order().ToArray();
			for (int num = 0; num < array3.Length; num++)
			{
				for (int num2 = num + 1; num2 < array3.Length; num2++)
				{
					int num3 = array3[num];
					int num4 = array3[num2];
					if (array[num3].Contains(num4))
					{
						list.Add(new MeshTriangle(a, num3, num4));
					}
				}
			}
		}
		return list.ToArray();
	}

	private static int[][] CreateIncidentTriangleMap(IReadOnlyList<MeshTriangle> triangles)
	{
		List<int>[] array = new List<int>[468];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = new List<int>();
		}
		for (int j = 0; j < triangles.Count; j++)
		{
			MeshTriangle meshTriangle = triangles[j];
			array[meshTriangle.A].Add(j);
			array[meshTriangle.B].Add(j);
			array[meshTriangle.C].Add(j);
		}
		return array.Select((List<int> indices) => indices.ToArray()).ToArray();
	}
}
