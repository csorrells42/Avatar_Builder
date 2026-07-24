using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Vision.MediaPipe;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectX12;
using OpenCvSharp;

namespace VideoPipelineSmoke;

internal static class Program
{
	[STAThread]
	private static int Main(string[] args)
	{
		if (args.Length != 1 || !File.Exists(args[0]))
		{
			Console.Error.WriteLine("Usage: VideoPipelineSmoke <video-path>");
			return 2;
		}

		string videoPath = Path.GetFullPath(args[0]);
		VideoPipelineResult? result = null;
		Exception? failure = null;
		Application application = new Application
		{
			ShutdownMode = ShutdownMode.OnExplicitShutdown
		};
		Direct3D12PreviewHost preview = new Direct3D12PreviewHost
		{
			HorizontalAlignment = HorizontalAlignment.Stretch,
			VerticalAlignment = VerticalAlignment.Stretch
		};
		System.Windows.Window window = new System.Windows.Window
		{
			Title = "Avatar Builder Video Pipeline Acceptance Test",
			Width = 1280,
			Height = 760,
			Background = Brushes.Black,
			Content = preview,
			WindowStartupLocation = WindowStartupLocation.CenterScreen
		};
		window.Loaded += async (_, _) =>
		{
			try
			{
				result = await Task.Run(() => VideoPipelineRunner.Run(videoPath, preview));
			}
			catch (Exception ex)
			{
				failure = ex;
			}
			finally
			{
				window.Close();
				application.Shutdown();
			}
		};
		application.Run(window);
		preview.Dispose();

		if (failure != null)
		{
			Console.Error.WriteLine(failure);
			return 1;
		}
		if (result == null)
		{
			Console.Error.WriteLine("Video pipeline test produced no result.");
			return 1;
		}

		Console.WriteLine(result.Format());
		return result.Succeeded ? 0 : 1;
	}
}

internal sealed record VideoPipelineResult(
	bool Succeeded,
	string VideoPath,
	int Width,
	int Height,
	double SourceFramesPerSecond,
	long ExpectedFrames,
	long DecodedFrames,
	long SubmittedFrames,
	long RenderedFrames,
	long DroppedDisplayFrames,
	TimeSpan PlaybackDuration,
	TimeSpan SourceDuration,
	TimeSpan TrackingWarmupDuration,
	long TrackingAcceptedFrames,
	long TrackingCompletedFrames,
	long TrackingSkippedBeforeWork,
	long FaceFrames,
	double TrackingFramesPerSecond,
	double HorizontalMotion,
	double VerticalMotion)
{
	public string Format()
	{
		string status = Succeeded ? "PASS" : "FAIL";
		return $"{status} | Full-speed video tracking pipeline{Environment.NewLine}"
			+ $"Video: {VideoPath}{Environment.NewLine}"
			+ $"Source: {Width}x{Height} @ {SourceFramesPerSecond:0.###} fps; expected {ExpectedFrames:n0} frames; duration {SourceDuration.TotalSeconds:0.000} s{Environment.NewLine}"
			+ $"Playback: decoded {DecodedFrames:n0}; submitted {SubmittedFrames:n0}; rendered {RenderedFrames:n0}; display drops {DroppedDisplayFrames:n0}; wall {PlaybackDuration.TotalSeconds:0.000} s ({DecodedFrames / Math.Max(0.001, PlaybackDuration.TotalSeconds):0.0} fps){Environment.NewLine}"
			+ $"Tracking warm-up before playback: {TrackingWarmupDuration.TotalSeconds:0.000} s{Environment.NewLine}"
			+ $"Tracking: accepted {TrackingAcceptedFrames:n0}; completed {TrackingCompletedFrames:n0}; skipped before work {TrackingSkippedBeforeWork:n0}; face frames {FaceFrames:n0}; {TrackingFramesPerSecond:0.0} analyzed fps{Environment.NewLine}"
			+ $"Tracked motion span: horizontal {HorizontalMotion:P1}; vertical {VerticalMotion:P1}.";
	}
}

internal static class VideoPipelineRunner
{
	public static VideoPipelineResult Run(string videoPath, Direct3D12PreviewHost preview)
	{
		if (!SpinWait.SpinUntil(() => preview.IsReady, TimeSpan.FromSeconds(5)))
		{
			throw new TimeoutException("DX12 preview surface did not become ready.");
		}

		using VideoCapture capture = new VideoCapture(videoPath);
		if (!capture.IsOpened())
		{
			throw new InvalidOperationException("OpenCV could not open the acceptance video.");
		}

		int width = (int)Math.Round(capture.Get(VideoCaptureProperties.FrameWidth));
		int height = (int)Math.Round(capture.Get(VideoCaptureProperties.FrameHeight));
		double sourceFramesPerSecond = capture.Get(VideoCaptureProperties.Fps);
		long expectedFrames = (long)Math.Round(capture.Get(VideoCaptureProperties.FrameCount));
		if (width <= 0 || height <= 0 || sourceFramesPerSecond <= 0.0)
		{
			throw new InvalidOperationException($"Invalid video metadata: {width}x{height} @ {sourceFramesPerSecond:0.###} fps.");
		}

		using TrackingLane tracking = new TrackingLane(preview);
		using Mat bgr = new Mat();
		using Mat bgra = new Mat();
		long decodedFrames = 0;
		long started = 0L;
		TimeSpan trackingWarmupDuration = TimeSpan.Zero;
		long displaySubmittedBaseline = 0L;
		long displayRenderedBaseline = 0L;
		long displayDroppedBaseline = 0L;
		long sourceIntervalTicks = Math.Max(1L, (long)Math.Round(Stopwatch.Frequency / sourceFramesPerSecond));
		while (capture.Read(bgr) && !bgr.Empty())
		{
			Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
			int stride = checked(width * 4);
			using CameraFrame frame = CameraFrame.RentBgra(width, height, stride, "video-bgra32");
			Marshal.Copy(bgra.Data, frame.BgraBytes, 0, checked(stride * height));
			decodedFrames++;
			if (decodedFrames == 1)
			{
				long warmupStarted = Stopwatch.GetTimestamp();
				preview.RenderBgraFrame(frame, 0L);
				tracking.TryAccept(frame);
				if (!SpinWait.SpinUntil(
					() => preview.RenderedFrames + preview.DroppedFrames >= preview.SubmittedFrames,
					TimeSpan.FromSeconds(5)))
				{
					throw new TimeoutException("The first 4K frame did not finish DX12 initialization.");
				}
				if (!tracking.WaitUntilIdle(TimeSpan.FromSeconds(10)))
				{
					throw new TimeoutException("The GPU tracking worker did not finish its warm-up frame.");
				}
				while (Stopwatch.GetElapsedTime(warmupStarted) < TimeSpan.FromSeconds(2))
				{
					Thread.Sleep(10);
				}
				trackingWarmupDuration = Stopwatch.GetElapsedTime(warmupStarted);
				tracking.ResetMeasurements();
				displaySubmittedBaseline = preview.SubmittedFrames;
				displayRenderedBaseline = preview.RenderedFrames;
				displayDroppedBaseline = preview.DroppedFrames;
				started = Stopwatch.GetTimestamp() - sourceIntervalTicks;
			}
			preview.RenderBgraFrame(frame, decodedFrames);
			tracking.TryAccept(frame);

			long due = started + decodedFrames * sourceIntervalTicks;
			while (true)
			{
				long remainingTicks = due - Stopwatch.GetTimestamp();
				if (remainingTicks <= 0)
				{
					break;
				}
				double remainingMilliseconds = remainingTicks * 1000.0 / Stopwatch.Frequency;
				if (remainingMilliseconds >= 2.0)
				{
					Thread.Sleep(1);
				}
				else
				{
					Thread.SpinWait(64);
				}
			}
		}
		TimeSpan playbackDuration = Stopwatch.GetElapsedTime(started);
		tracking.Drain(TimeSpan.FromSeconds(10));
		SpinWait.SpinUntil(
			() => preview.RenderedFrames + preview.DroppedFrames >= preview.SubmittedFrames,
			TimeSpan.FromSeconds(3));

		TimeSpan sourceDuration = TimeSpan.FromSeconds(decodedFrames / sourceFramesPerSecond);
		long submittedFrames = preview.SubmittedFrames - displaySubmittedBaseline;
		long renderedFrames = preview.RenderedFrames - displayRenderedBaseline;
		long droppedFrames = preview.DroppedFrames - displayDroppedBaseline;
		double allowedPlaybackSeconds = sourceDuration.TotalSeconds + Math.Max(0.15, sourceDuration.TotalSeconds * 0.03);
		bool allFramesDecoded = expectedFrames <= 0 || Math.Abs(decodedFrames - expectedFrames) <= 1;
		bool allFramesDisplayed = submittedFrames == decodedFrames
			&& renderedFrames == decodedFrames
			&& droppedFrames == 0;
		bool fullSpeed = playbackDuration.TotalSeconds <= allowedPlaybackSeconds;
		bool trackedMotion = tracking.FaceFrames > 0
			&& (tracking.HorizontalMotion >= 0.01 || tracking.VerticalMotion >= 0.01);
		bool succeeded = allFramesDecoded && allFramesDisplayed && fullSpeed && trackedMotion;
		return new VideoPipelineResult(
			succeeded,
			videoPath,
			width,
			height,
			sourceFramesPerSecond,
			expectedFrames,
			decodedFrames,
			submittedFrames,
			renderedFrames,
			droppedFrames,
			playbackDuration,
			sourceDuration,
			trackingWarmupDuration,
			tracking.AcceptedFrames,
			tracking.CompletedFrames,
			Math.Max(0, decodedFrames - tracking.AcceptedFrames),
			tracking.FaceFrames,
			tracking.CompletedFrames / Math.Max(0.001, playbackDuration.TotalSeconds),
			tracking.HorizontalMotion,
			tracking.VerticalMotion);
	}
}

internal sealed class TrackingLane : IDisposable
{
	private readonly object _frameLock = new object();

	private readonly AutoResetEvent _frameReady = new AutoResetEvent(initialState: false);

	private readonly Thread _worker;

	private readonly Direct3D12PreviewHost _preview;

	private CameraFrame? _acceptedFrame;

	private int _busy;

	private int _accepting = 1;

	private int _stopping;

	private long _acceptedFrames;

	private long _completedFrames;

	private long _faceFrames;

	private double _minimumCenterX = double.PositiveInfinity;

	private double _maximumCenterX = double.NegativeInfinity;

	private double _minimumCenterY = double.PositiveInfinity;

	private double _maximumCenterY = double.NegativeInfinity;

	public long AcceptedFrames => Interlocked.Read(ref _acceptedFrames);

	public long CompletedFrames => Interlocked.Read(ref _completedFrames);

	public long FaceFrames => Interlocked.Read(ref _faceFrames);

	public double HorizontalMotion => double.IsFinite(_minimumCenterX) ? Math.Max(0.0, _maximumCenterX - _minimumCenterX) : 0.0;

	public double VerticalMotion => double.IsFinite(_minimumCenterY) ? Math.Max(0.0, _maximumCenterY - _minimumCenterY) : 0.0;

	public TrackingLane(Direct3D12PreviewHost preview)
	{
		_preview = preview;
		_worker = new Thread(WorkerLoop)
		{
			IsBackground = true,
			Name = "Avatar Builder video acceptance tracking",
			Priority = ThreadPriority.BelowNormal
		};
		_worker.Start();
	}

	public bool TryAccept(CameraFrame source)
	{
		if (Volatile.Read(ref _accepting) == 0
			|| Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
		{
			return false;
		}

		CameraFrame? retained = null;
		bool accepted = false;
		try
		{
			retained = source.Duplicate();
			lock (_frameLock)
			{
				if (Volatile.Read(ref _accepting) != 0 && _acceptedFrame == null)
				{
					_acceptedFrame = retained;
					retained = null;
					accepted = true;
				}
			}
			if (accepted)
			{
				Interlocked.Increment(ref _acceptedFrames);
				_frameReady.Set();
			}
			return accepted;
		}
		finally
		{
			retained?.Dispose();
			if (!accepted)
			{
				Interlocked.Exchange(ref _busy, 0);
			}
		}
	}

	public void Drain(TimeSpan timeout)
	{
		Volatile.Write(ref _accepting, 0);
		WaitUntilIdle(timeout);
	}

	public bool WaitUntilIdle(TimeSpan timeout)
	{
		return SpinWait.SpinUntil(() => Volatile.Read(ref _busy) == 0, timeout);
	}

	public void ResetMeasurements()
	{
		if (Volatile.Read(ref _busy) != 0)
		{
			throw new InvalidOperationException("Tracking measurements can only be reset while the lane is idle.");
		}
		Interlocked.Exchange(ref _acceptedFrames, 0L);
		Interlocked.Exchange(ref _completedFrames, 0L);
		Interlocked.Exchange(ref _faceFrames, 0L);
		_minimumCenterX = double.PositiveInfinity;
		_maximumCenterX = double.NegativeInfinity;
		_minimumCenterY = double.PositiveInfinity;
		_maximumCenterY = double.NegativeInfinity;
	}

	public void Dispose()
	{
		Drain(TimeSpan.FromSeconds(10));
		if (Interlocked.Exchange(ref _stopping, 1) == 0)
		{
			_frameReady.Set();
		}
		if (_worker != Thread.CurrentThread)
		{
			_worker.Join(TimeSpan.FromSeconds(10));
		}
		_frameReady.Dispose();
	}

	private void WorkerLoop()
	{
		using MediaPipeFaceLandmarkerSidecarTracker tracker = new MediaPipeFaceLandmarkerSidecarTracker(
			MediaPipeExecutionBackend.Gpu,
			collectDiagnostics: true);
		while (true)
		{
			_frameReady.WaitOne();
			CameraFrame? frame;
			lock (_frameLock)
			{
				frame = _acceptedFrame;
				_acceptedFrame = null;
			}
			if (frame == null)
			{
				Interlocked.Exchange(ref _busy, 0);
				if (Volatile.Read(ref _stopping) != 0)
				{
					break;
				}
				continue;
			}

			try
			{
				BitmapSource bitmap = BitmapSource.Create(
					frame.Width,
					frame.Height,
					96.0,
					96.0,
					PixelFormats.Bgra32,
					null,
					frame.BgraBytes,
					frame.Stride);
				bitmap.Freeze();
				long capturedAtTimestamp = Stopwatch.GetTimestamp();
				FaceLandmarkTrackingResult result = tracker.Detect(bitmap, DateTime.UtcNow);
				if (result.HasFace && result.LandmarkFrame.DenseMeshPoints.Count > 0)
				{
					double centerX = result.LandmarkFrame.DenseMeshPoints.Average(point => point.X);
					double centerY = result.LandmarkFrame.DenseMeshPoints.Average(point => point.Y);
					_minimumCenterX = Math.Min(_minimumCenterX, centerX);
					_maximumCenterX = Math.Max(_maximumCenterX, centerX);
					_minimumCenterY = Math.Min(_minimumCenterY, centerY);
					_maximumCenterY = Math.Max(_maximumCenterY, centerY);
					Interlocked.Increment(ref _faceFrames);
					_preview.UpdateTrackingOverlay(MediaPipePreviewOverlayFactory.Create(result.LandmarkFrame) with
					{
						SourceTimestamp = capturedAtTimestamp,
						MaximumAge = TimeSpan.FromMilliseconds(250)
					});
				}
				Interlocked.Increment(ref _completedFrames);
			}
			finally
			{
				frame.Dispose();
				Interlocked.Exchange(ref _busy, 0);
			}
		}
	}
}
