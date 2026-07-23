using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AvatarBuilder.Modules.Webcam.DirectX12;
using OpenCvSharp;

namespace AvatarBuilder.Modules.Webcam.DualCamera;

internal sealed class DualCameraCalibrationCoordinator : IAsyncDisposable
{
	private sealed record CalibrationSample(Point2f[] CameraACorners, Point2f[] CameraBCorners);

	private readonly record struct SampleSignature(double CenterX, double CenterY, double Area, double Angle)
	{
		public static SampleSignature Create(IReadOnlyList<Point2f> corners, int width, int height)
		{
			Point2f point2f = corners[0];
			Point2f point2f2 = corners[8];
			Point2f point2f3 = corners[corners.Count - 9];
			double centerX = (double)corners.Average((Point2f point) => point.X) / Math.Max(1.0, width);
			double centerY = (double)corners.Average((Point2f point) => point.Y) / Math.Max(1.0, height);
			float num = point2f2.X - point2f.X;
			float num2 = point2f2.Y - point2f.Y;
			float num3 = point2f3.X - point2f.X;
			float num4 = point2f3.Y - point2f.Y;
			double area = (double)Math.Abs(num * num4 - num2 * num3) / Math.Max(1.0, (double)width * (double)height);
			double angle = Math.Atan2(num2, num);
			return new SampleSignature(centerX, centerY, area, angle);
		}

		public bool IsDifferentEnough(SampleSignature other)
		{
			double num = Math.Sqrt(Math.Pow(CenterX - other.CenterX, 2.0) + Math.Pow(CenterY - other.CenterY, 2.0));
			double num2 = Math.Abs(Area - other.Area) / Math.Max(1E-06, other.Area);
			double num3 = Math.Abs(Angle - other.Angle);
			if (!(num >= 0.045) && !(num2 >= 0.12))
			{
				return num3 >= 0.1;
			}
			return true;
		}
	}

	private const int RequiredPairCount = 15;

	private static readonly Size BoardSize = new Size(9, 6);

	private static readonly TimeSpan MaximumPairSkew = TimeSpan.FromMilliseconds(50L);

	private static readonly TimeSpan MinimumSampleInterval = TimeSpan.FromMilliseconds(650L);

	private static readonly TimeSpan RequiredBoardStillness = TimeSpan.FromMilliseconds(300L);

	private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(3L);

	private const double MaximumSettledCornerRmsPixels = 2.5;

	private static readonly Point3f[] BoardObjectPoints = CreateBoardObjectPoints();

	private static readonly PreviewOverlayEdge[] BoardEdges = CreateBoardEdges();

	private readonly object _frameLock = new object();

	private readonly AutoResetEvent _signal = new AutoResetEvent(initialState: false);

	private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

	private readonly Task _worker;

	private readonly string _outputRoot;

	private readonly List<CalibrationSample> _samples = new List<CalibrationSample>();

	private DualCameraCalibrationFrame? _cameraAFrame;

	private DualCameraCalibrationFrame? _cameraBFrame;

	private string _cameraAName = "Camera A";

	private string _cameraBName = "Camera B";

	private DateTime _lastAcceptedAtUtc = DateTime.MinValue;

	private SampleSignature? _lastAcceptedSignature;

	private Point2f[]? _settlingCornersA;

	private Point2f[]? _settlingCornersB;

	private DateTime _settlingSinceUtc = DateTime.MinValue;

	private long _lastCameraATicks;

	private long _lastCameraBTicks;

	private int _enabled;

	private int _disposed;

	public bool IsEnabled => Volatile.Read(in _enabled) != 0;

	public event Action<DualCameraCalibrationProgress>? ProgressChanged;

	public event Action<DualCameraCalibrationOverlay?, DualCameraCalibrationOverlay?>? OverlaysChanged;

	public DualCameraCalibrationCoordinator(string outputRoot)
	{
		_outputRoot = outputRoot;
		_worker = Task.Factory.StartNew(RunWorker, _cancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
	}

	public void Start(string cameraAName, string cameraBName)
	{
		lock (_frameLock)
		{
			_cameraAName = cameraAName;
			_cameraBName = cameraBName;
			_cameraAFrame = null;
			_cameraBFrame = null;
			_samples.Clear();
			_lastAcceptedAtUtc = DateTime.MinValue;
			_lastAcceptedSignature = null;
			ResetSettlingState();
			_lastCameraATicks = 0L;
			_lastCameraBTicks = 0L;
		}
		Volatile.Write(ref _enabled, 1);
		this.ProgressChanged?.Invoke(new DualCameraCalibrationProgress("Hold the rigid checkerboard where both cameras can see all 9 x 6 inner corners. Move it after each accepted pair.", 0, 15, CameraABoardFound: false, CameraBBoardFound: false, Completed: false));
		_signal.Set();
	}

	public void Stop(string status = "Physical calibration stopped.")
	{
		Volatile.Write(ref _enabled, 0);
		lock (_frameLock)
		{
			_cameraAFrame = null;
			_cameraBFrame = null;
		}
		this.OverlaysChanged?.Invoke(null, null);
		this.ProgressChanged?.Invoke(new DualCameraCalibrationProgress(status, _samples.Count, 15, CameraABoardFound: false, CameraBBoardFound: false, Completed: false));
		_signal.Set();
	}

	public void Submit(bool cameraA, DualCameraCalibrationFrame frame)
	{
		if (!IsEnabled || Volatile.Read(in _disposed) != 0)
		{
			return;
		}
		lock (_frameLock)
		{
			if (cameraA)
			{
				_cameraAFrame = frame;
			}
			else
			{
				_cameraBFrame = frame;
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
			if (IsEnabled && TryTakeLatestPair(out DualCameraCalibrationFrame cameraA, out DualCameraCalibrationFrame cameraB))
			{
				try
				{
					ProcessPair(cameraA, cameraB);
				}
				catch (Exception ex2)
				{
					this.ProgressChanged?.Invoke(new DualCameraCalibrationProgress("Calibration skipped one pair: " + ex2.Message, _samples.Count, 15, CameraABoardFound: false, CameraBBoardFound: false, Completed: false));
				}
			}
		}
	}

	private bool TryTakeLatestPair(out DualCameraCalibrationFrame cameraA, out DualCameraCalibrationFrame cameraB)
	{
		lock (_frameLock)
		{
			cameraA = _cameraAFrame;
			cameraB = _cameraBFrame;
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

	private void ProcessPair(DualCameraCalibrationFrame cameraA, DualCameraCalibrationFrame cameraB)
	{
		Point2f[] corners;
		DualCameraCalibrationOverlay overlay;
		bool flag = TryFindBoard(cameraA, out corners, out overlay);
		DualCameraCalibrationOverlay overlay2;
		Point2f[] corners2;
		bool flag2 = TryFindBoard(cameraB, out corners2, out overlay2);
		this.OverlaysChanged?.Invoke(overlay, overlay2);
		if (!flag || !flag2)
		{
			ResetSettlingState();
			string text = ((!flag && !flag2) ? "both cameras" : ((!flag) ? "Camera A" : "Camera B"));
			this.ProgressChanged?.Invoke(new DualCameraCalibrationProgress("Board waiting in " + text + ". Keep the entire checkerboard visible and reduce glare.", _samples.Count, 15, flag, flag2, Completed: false));
			return;
		}
		corners2 = AlignCornerOrder(corners, corners2);
		if (cameraA.Width != cameraB.Width || cameraA.Height != cameraB.Height)
		{
			this.ProgressChanged?.Invoke(new DualCameraCalibrationProgress("Both cameras must use the same analysis resolution during physical calibration.", _samples.Count, 15, CameraABoardFound: true, CameraBBoardFound: true, Completed: false));
			return;
		}
		DateTime dateTime = ((cameraA.CapturedAtUtc >= cameraB.CapturedAtUtc) ? cameraA.CapturedAtUtc : cameraB.CapturedAtUtc);
		if (!IsBoardSettled(corners, corners2, dateTime, out var cornerMotionRms))
		{
			this.ProgressChanged?.Invoke(new DualCameraCalibrationProgress($"Board locked in both cameras. Hold it still briefly ({cornerMotionRms:0.0} px motion).", _samples.Count, 15, CameraABoardFound: true, CameraBBoardFound: true, Completed: false));
			return;
		}
		DateTime dateTime2 = dateTime;
		SampleSignature value = SampleSignature.Create(corners, cameraA.Width, cameraA.Height);
		if (!(dateTime2 - _lastAcceptedAtUtc < MinimumSampleInterval))
		{
			SampleSignature? lastAcceptedSignature = _lastAcceptedSignature;
			if (lastAcceptedSignature.HasValue)
			{
				SampleSignature valueOrDefault = lastAcceptedSignature.GetValueOrDefault();
				if (!value.IsDifferentEnough(valueOrDefault))
				{
					goto IL_01c7;
				}
			}
			_samples.Add(new CalibrationSample(corners, corners2));
			_lastAcceptedAtUtc = dateTime2;
			_lastAcceptedSignature = value;
			BeginSettling(corners, corners2, dateTime);
			if (_samples.Count < 15)
			{
				this.ProgressChanged?.Invoke(new DualCameraCalibrationProgress($"Accepted pair {_samples.Count}/{15}. Move the board to a new position and tilt.", _samples.Count, 15, CameraABoardFound: true, CameraBBoardFound: true, Completed: false));
				return;
			}
			this.ProgressChanged?.Invoke(new DualCameraCalibrationProgress("Solving camera intrinsics and the physical camera-to-camera transform...", _samples.Count, 15, CameraABoardFound: true, CameraBBoardFound: true, Completed: false));
			DualCameraCalibrationModel dualCameraCalibrationModel = Solve(cameraA.Width, cameraA.Height);
			Volatile.Write(ref _enabled, 0);
			this.OverlaysChanged?.Invoke(null, null);
			if (!dualCameraCalibrationModel.IsUsable)
			{
				this.ProgressChanged?.Invoke(new DualCameraCalibrationProgress($"Calibration rejected: stereo RMS {dualCameraCalibrationModel.StereoReprojectionErrorPixels:0.00} px exceeds the {5.0:0.00} px quality limit. Previous saved calibration was kept.", dualCameraCalibrationModel.AcceptedPairCount, 15, CameraABoardFound: true, CameraBBoardFound: true, Completed: true));
			}
			else
			{
				DualCameraCalibrationModel.Save(_outputRoot, dualCameraCalibrationModel);
				this.ProgressChanged?.Invoke(new DualCameraCalibrationProgress($"Calibration complete: {dualCameraCalibrationModel.AcceptedPairCount} pairs | baseline {dualCameraCalibrationModel.BaselineInches:0.00} in | stereo RMS {dualCameraCalibrationModel.StereoReprojectionErrorPixels:0.00} px.", dualCameraCalibrationModel.AcceptedPairCount, 15, CameraABoardFound: true, CameraBBoardFound: true, Completed: true, dualCameraCalibrationModel));
			}
			return;
		}
		goto IL_01c7;
		IL_01c7:
		this.ProgressChanged?.Invoke(new DualCameraCalibrationProgress($"Board locked in both cameras. Move or tilt it for the next view ({_samples.Count}/{15}).", _samples.Count, 15, CameraABoardFound: true, CameraBBoardFound: true, Completed: false));
	}

	private bool IsBoardSettled(Point2f[] cornersA, Point2f[] cornersB, DateTime observedAtUtc, out double cornerMotionRms)
	{
		if (_settlingCornersA == null || _settlingCornersB == null || observedAtUtc <= _settlingSinceUtc)
		{
			cornerMotionRms = double.PositiveInfinity;
			BeginSettling(cornersA, cornersB, observedAtUtc);
			return false;
		}
		cornerMotionRms = Math.Max(CalculateCornerRms(_settlingCornersA, cornersA), CalculateCornerRms(_settlingCornersB, cornersB));
		if (cornerMotionRms > 2.5)
		{
			BeginSettling(cornersA, cornersB, observedAtUtc);
			return false;
		}
		return observedAtUtc - _settlingSinceUtc >= RequiredBoardStillness;
	}

	private void BeginSettling(Point2f[] cornersA, Point2f[] cornersB, DateTime observedAtUtc)
	{
		_settlingCornersA = (Point2f[])cornersA.Clone();
		_settlingCornersB = (Point2f[])cornersB.Clone();
		_settlingSinceUtc = observedAtUtc;
	}

	private void ResetSettlingState()
	{
		_settlingCornersA = null;
		_settlingCornersB = null;
		_settlingSinceUtc = DateTime.MinValue;
	}

	private static double CalculateCornerRms(IReadOnlyList<Point2f> reference, IReadOnlyList<Point2f> current)
	{
		if (reference.Count == 0 || reference.Count != current.Count)
		{
			return double.PositiveInfinity;
		}
		double num = 0.0;
		for (int i = 0; i < reference.Count; i++)
		{
			float num2 = current[i].X - reference[i].X;
			float num3 = current[i].Y - reference[i].Y;
			num += (double)(num2 * num2 + num3 * num3);
		}
		return Math.Sqrt(num / (double)reference.Count);
	}

	private static Point2f[] AlignCornerOrder(IReadOnlyList<Point2f> cornersA, Point2f[] cornersB)
	{
		double num = CalculateOrientationAgreement(cornersA, cornersB);
		Point2f[] array = cornersB.Reverse().ToArray();
		if (!(CalculateOrientationAgreement(cornersA, array) > num))
		{
			return cornersB;
		}
		return array;
	}

	private static double CalculateOrientationAgreement(IReadOnlyList<Point2f> cornersA, IReadOnlyList<Point2f> cornersB)
	{
		int index = 8;
		int index2 = cornersA.Count - 9;
		return DirectionAgreement(cornersA[0], cornersA[index], cornersB[0], cornersB[index]) + DirectionAgreement(cornersA[0], cornersA[index2], cornersB[0], cornersB[index2]);
	}

	private static double DirectionAgreement(Point2f startA, Point2f endA, Point2f startB, Point2f endB)
	{
		float num = endA.X - startA.X;
		float num2 = endA.Y - startA.Y;
		float num3 = endB.X - startB.X;
		float num4 = endB.Y - startB.Y;
		double num5 = Math.Sqrt(num * num + num2 * num2);
		double num6 = Math.Sqrt(num3 * num3 + num4 * num4);
		if (!(num5 <= 1E-06) && !(num6 <= 1E-06))
		{
			return (double)(num * num3 + num2 * num4) / (num5 * num6);
		}
		return -1.0;
	}

	private DualCameraCalibrationModel Solve(int width, int height)
	{
		IEnumerable<Point3f>[] objectPoints = ((IEnumerable<CalibrationSample>)_samples).Select((Func<CalibrationSample, IEnumerable<Point3f>>)((CalibrationSample _) => BoardObjectPoints)).ToArray();
		IEnumerable<Point2f>[] array = ((IEnumerable<CalibrationSample>)_samples).Select((Func<CalibrationSample, IEnumerable<Point2f>>)((CalibrationSample sample) => sample.CameraACorners)).ToArray();
		IEnumerable<Point2f>[] array2 = ((IEnumerable<CalibrationSample>)_samples).Select((Func<CalibrationSample, IEnumerable<Point2f>>)((CalibrationSample sample) => sample.CameraBCorners)).ToArray();
		Size imageSize = new Size(width, height);
		double[,] array3 = Identity3x3();
		double[,] array4 = Identity3x3();
		double[] array5 = new double[8];
		double[] array6 = new double[8];
		TermCriteria value = new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps, 100, 1E-07);
		Vec3d[] rvecs;
		Vec3d[] tvecs;
		double cameraAReprojectionErrorPixels = Cv2.CalibrateCamera(objectPoints, array, imageSize, array3, array5, out rvecs, out tvecs, CalibrationFlags.None, value);
		double cameraBReprojectionErrorPixels = Cv2.CalibrateCamera(objectPoints, array2, imageSize, array4, array6, out tvecs, out rvecs, CalibrationFlags.None, value);
		using Mat mat = new Mat();
		using Mat mat2 = new Mat();
		using Mat mat3 = new Mat();
		using Mat mat4 = new Mat();
		double stereoReprojectionErrorPixels = Cv2.StereoCalibrate(objectPoints, array, array2, array3, array5, array4, array6, imageSize, mat, mat2, mat3, mat4, CalibrationFlags.FixIntrinsic, value);
		return new DualCameraCalibrationModel
		{
			CalibratedAtUtc = DateTime.UtcNow,
			CameraAName = _cameraAName,
			CameraBName = _cameraBName,
			ImageWidth = width,
			ImageHeight = height,
			AcceptedPairCount = _samples.Count,
			CameraAReprojectionErrorPixels = cameraAReprojectionErrorPixels,
			CameraBReprojectionErrorPixels = cameraBReprojectionErrorPixels,
			StereoReprojectionErrorPixels = stereoReprojectionErrorPixels,
			CameraAMatrix = Flatten(array3),
			CameraADistortion = array5,
			CameraBMatrix = Flatten(array4),
			CameraBDistortion = array6,
			CameraAToBRotation = Flatten(mat),
			CameraAToBTranslationInches = Flatten(mat2),
			EssentialMatrix = Flatten(mat3),
			FundamentalMatrix = Flatten(mat4)
		};
	}

	private static bool TryFindBoard(DualCameraCalibrationFrame frame, out Point2f[] corners, out DualCameraCalibrationOverlay? overlay)
	{
		using Mat mat = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.BgraPixels, frame.Stride);
		using Mat mat2 = new Mat();
		Cv2.CvtColor(mat, mat2, ColorConversionCodes.BGRA2GRAY);
		if (!Cv2.FindChessboardCornersSB(mat2, BoardSize, out corners, ChessboardFlags.NormalizeImage | ChessboardFlags.Exhaustive | ChessboardFlags.Accuracy) || corners.Length != 54)
		{
			corners = Array.Empty<Point2f>();
			overlay = null;
			return false;
		}
		PreviewOverlayPoint[] points = corners.Select((Point2f point) => new PreviewOverlayPoint((double)point.X / Math.Max(1.0, frame.Width), (double)point.Y / Math.Max(1.0, frame.Height))).ToArray();
		overlay = new DualCameraCalibrationOverlay(frame.CapturedAtUtc, points, BoardEdges);
		return true;
	}

	private static double[,] Identity3x3()
	{
		return new double[3, 3]
		{
			{ 1.0, 0.0, 0.0 },
			{ 0.0, 1.0, 0.0 },
			{ 0.0, 0.0, 1.0 }
		};
	}

	private static double[] Flatten(double[,] values)
	{
		double[] array = new double[values.Length];
		int num = 0;
		for (int i = 0; i < values.GetLength(0); i++)
		{
			for (int j = 0; j < values.GetLength(1); j++)
			{
				array[num++] = values[i, j];
			}
		}
		return array;
	}

	private static double[] Flatten(Mat values)
	{
		int rows = values.Rows;
		int cols = values.Cols;
		double[] array = new double[checked(rows * cols * values.Channels())];
		int num = 0;
		for (int i = 0; i < rows; i++)
		{
			for (int j = 0; j < cols; j++)
			{
				array[num++] = values.At<double>(i, j);
			}
		}
		return array;
	}

	private static Point3f[] CreateBoardObjectPoints()
	{
		Point3f[] array = new Point3f[54];
		int num = 0;
		for (int i = 0; i < 6; i++)
		{
			for (int j = 0; j < 9; j++)
			{
				array[num++] = new Point3f((float)((double)j * 0.75), (float)((double)i * 0.75), 0f);
			}
		}
		return array;
	}

	private static PreviewOverlayEdge[] CreateBoardEdges()
	{
		List<PreviewOverlayEdge> list = new List<PreviewOverlayEdge>();
		for (int i = 0; i < 6; i++)
		{
			for (int j = 0; j < 9; j++)
			{
				int num = i * 9 + j;
				if (j + 1 < 9)
				{
					list.Add(new PreviewOverlayEdge(num, num + 1));
				}
				if (i + 1 < 6)
				{
					list.Add(new PreviewOverlayEdge(num, num + 9));
				}
			}
		}
		return list.ToArray();
	}
}
