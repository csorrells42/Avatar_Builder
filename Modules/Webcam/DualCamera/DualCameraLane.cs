using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.MediaPipe;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectX12;
using AvatarBuilder.Modules.Webcam.Pipeline;

namespace AvatarBuilder.Modules.Webcam.DualCamera;

internal sealed class DualCameraLane : IAsyncDisposable
{
	private const int AnalysisMaximumWidth = 1920;

	private const double AnalysisTargetFramesPerSecond = 15.0;

	private static readonly TimeSpan AnalysisInterval = TimeSpan.FromSeconds(1.0 / 15.0);

	private static readonly TimeSpan HealthPollInterval = TimeSpan.FromSeconds(1L);

	private static readonly TimeSpan SourceStallTimeout = TimeSpan.FromSeconds(4L);

	private static readonly TimeSpan PreviewRefreshTimeout = TimeSpan.FromSeconds(3L);

	private static readonly TimeSpan PreviewRestartTimeout = TimeSpan.FromSeconds(7L);

	private static readonly TimeSpan PreviewRefreshCooldown = TimeSpan.FromSeconds(5L);

	private static readonly TimeSpan WorkerShutdownGrace = TimeSpan.FromSeconds(3L);

	private readonly object _acceptedFrameLock = new object();

	private readonly Panel _previewPanel;

	private readonly UIElement _placeholder;

	private readonly TextBlock _previewStateText;

	private readonly string _name;

	private readonly Action<string> _statusChanged;

	private readonly Action<string> _recoveryRequested;

	private Dx12Camera? _camera;

	private Dx12UploadCamera? _uploadCamera;

	private MediaPipeFaceLandmarkerSidecarTracker? _tracker;

	private TextureNativeFrameLease? _acceptedTextureFrame;

	private CameraFrame? _acceptedDecodedFrame;

	private CancellationTokenSource? _workerCancellation;

	private AutoResetEvent? _workerSignal;

	private Task? _workerTask;

	private Task? _healthTask;

	private DateTime _lastAnalysisAcceptedAtUtc = DateTime.MinValue;

	private long _lastSourceFrameTimestamp;

	private long _lastRenderedFrameTimestamp;

	private long _lastPreviewRefreshTimestamp;

	private long _sourceFrames;

	private long _analysisFrames;

	private long _lastRenderedFrames;

	private long _lastCameraStoppedTimestamp;

	private double _renderFramesPerSecond;

	private double _measuredSourceFramesPerSecond;

	private double _measuredAnalysisFramesPerSecond;

	private double _trackingConfidence;

	private int _sourceWidth;

	private int _sourceHeight;

	private double _advertisedSourceFramesPerSecond;

	private int _hasFace;

	private int _analysisBusy;

	private int _recoveryScheduled;

	private int _acceptFrames;

	private int _disposed;

	private int _translationViewEnabled;

	private int _calibrationCaptureEnabled;

	private long _latestObservationTicks;

	private PreviewTrackingOverlay _latestBaseOverlay = PreviewTrackingOverlay.Empty;

	private DualCameraRegistrationFrame? _latestRegistration;

	private DualCameraCalibrationOverlay? _latestCalibrationOverlay;

	public bool IsRunning
	{
		get
		{
			if (Volatile.Read(in _acceptFrames) != 0)
			{
				if (_camera == null)
				{
					return _uploadCamera?.IsRunning ?? false;
				}
				return true;
			}
			return false;
		}
	}

	public event Action<DualCameraObservation>? ObservationAvailable;

	public event Action<DualCameraCalibrationFrame>? CalibrationFrameAvailable;

	public DualCameraLane(string name, Panel previewPanel, UIElement placeholder, TextBlock previewStateText, Action<string> statusChanged, Action<string> recoveryRequested)
	{
		_name = name;
		_previewPanel = previewPanel;
		_placeholder = placeholder;
		_previewStateText = previewStateText;
		_statusChanged = statusChanged;
		_recoveryRequested = recoveryRequested;
	}

	public void SetTranslationViewEnabled(bool enabled)
	{
		Volatile.Write(ref _translationViewEnabled, enabled ? 1 : 0);
		if (!enabled)
		{
			Volatile.Write(ref _latestRegistration, null);
		}
		PublishTrackingOverlay();
	}

	public void ApplyRegistration(DualCameraRegistrationFrame registration)
	{
		if (Volatile.Read(in _translationViewEnabled) != 0 && registration.TargetCapturedAtUtc.Ticks == Interlocked.Read(in _latestObservationTicks))
		{
			Volatile.Write(ref _latestRegistration, registration);
			PublishTrackingOverlay();
		}
	}

	public void SetCalibrationCaptureEnabled(bool enabled)
	{
		Volatile.Write(ref _calibrationCaptureEnabled, enabled ? 1 : 0);
		if (!enabled)
		{
			Volatile.Write(ref _latestCalibrationOverlay, null);
		}
		PublishTrackingOverlay();
	}

	public void ApplyCalibrationOverlay(DualCameraCalibrationOverlay? overlay)
	{
		if (Volatile.Read(in _calibrationCaptureEnabled) != 0)
		{
			Volatile.Write(ref _latestCalibrationOverlay, overlay);
			PublishTrackingOverlay();
		}
	}

	public TimeSpan GetRemainingReleaseCooldown(TimeSpan cooldown)
	{
		long num = Volatile.Read(in _lastCameraStoppedTimestamp);
		if (num == 0L)
		{
			return TimeSpan.Zero;
		}
		TimeSpan elapsedTime = Stopwatch.GetElapsedTime(num);
		if (!(elapsedTime >= cooldown))
		{
			return cooldown - elapsedTime;
		}
		return TimeSpan.Zero;
	}

	public async Task StartAsync(CameraDevice cameraDevice, CameraVideoMode mode)
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(in _disposed) != 0, this);
		await StopAsync();
		_tracker = new MediaPipeFaceLandmarkerSidecarTracker
		{
			MaxDetectionDimension = 1920
		};
		_workerCancellation = new CancellationTokenSource();
		_workerSignal = new AutoResetEvent(initialState: false);
		_lastAnalysisAcceptedAtUtc = DateTime.MinValue;
		long timestamp = Stopwatch.GetTimestamp();
		Interlocked.Exchange(ref _lastSourceFrameTimestamp, timestamp);
		Interlocked.Exchange(ref _lastRenderedFrameTimestamp, timestamp);
		Interlocked.Exchange(ref _lastPreviewRefreshTimestamp, 0L);
		Interlocked.Exchange(ref _sourceFrames, 0L);
		Interlocked.Exchange(ref _analysisFrames, 0L);
		Interlocked.Exchange(ref _analysisBusy, 0);
		Interlocked.Exchange(ref _lastRenderedFrames, 0L);
		Interlocked.Exchange(ref _hasFace, 0);
		Interlocked.Exchange(ref _recoveryScheduled, 0);
		Interlocked.Exchange(ref _latestObservationTicks, 0L);
		Volatile.Write(ref _latestBaseOverlay, PreviewTrackingOverlay.Empty);
		Volatile.Write(ref _latestRegistration, null);
		Volatile.Write(ref _latestCalibrationOverlay, null);
		_measuredSourceFramesPerSecond = 0.0;
		_measuredAnalysisFramesPerSecond = 0.0;
		_trackingConfidence = 0.0;
		_advertisedSourceFramesPerSecond = mode.FramesPerSecond ?? 30.0;
		Volatile.Write(ref _acceptFrames, 1);
		_workerTask = Task.Factory.StartNew(delegate
		{
			RunAnalysisWorker(_workerCancellation.Token);
		}, _workerCancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
		try
		{
			PublishStatus(_name + ": starting " + cameraDevice.Name + ".");
			Dx12Camera.PreviewTarget target = new Dx12Camera.PreviewTarget(_previewPanel, null, _placeholder, _previewStateText, 0, _name);
			if (string.Equals(cameraDevice.Source, "DirectShow", StringComparison.OrdinalIgnoreCase))
			{
				await StartUploadCameraAsync(target, cameraDevice, mode);
			}
			else
			{
				try
				{
					_camera = WebcamModule.StartDx12Camera(target, new Dx12CameraOptions
					{
						Camera = cameraDevice,
						Mode = mode,
						MaxPreviewRenderFramesPerSecond = 0.0,
						FrameAvailable = CameraFrameAvailable,
						TextureFrameAvailable = CameraTextureFrameAvailable,
						DiagnosticsChanged = CameraDiagnosticsChanged,
						StatusChanged = CameraStatusChanged
					});
				}
				catch (Exception ex)
				{
					PublishStatus(_name + ": texture-native capture unavailable (" + ex.Message + "); opening compatible capture path.");
					await StartUploadCameraAsync(target, cameraDevice.DirectShowDeviceOrSelf(), mode);
				}
			}
			_placeholder.Visibility = Visibility.Collapsed;
			PublishStatus(_name + ": camera active; MediaPipe starting.");
			_healthTask = Task.Run(() => RunHealthMonitorAsync(_workerCancellation.Token), _workerCancellation.Token);
		}
		catch
		{
			await StopAsync();
			throw;
		}
	}

	public async Task StopAsync()
	{
		Volatile.Write(ref _acceptFrames, 0);
		Dx12Camera dx12Camera = Interlocked.Exchange(ref _camera, null);
		if (dx12Camera != null)
		{
			dx12Camera.FrameAvailable -= CameraFrameAvailable;
			dx12Camera.TextureFrameAvailable -= CameraTextureFrameAvailable;
			dx12Camera.DiagnosticsChanged -= CameraDiagnosticsChanged;
			dx12Camera.StatusChanged -= CameraStatusChanged;
			dx12Camera.UpdateTrackingOverlay(PreviewTrackingOverlay.Empty);
			dx12Camera.Dispose();
			Interlocked.Exchange(ref _lastCameraStoppedTimestamp, Stopwatch.GetTimestamp());
		}
		Dx12UploadCamera dx12UploadCamera = Interlocked.Exchange(ref _uploadCamera, null);
		if (dx12UploadCamera != null)
		{
			dx12UploadCamera.FrameAvailable -= DecodedCameraFrameAvailable;
			dx12UploadCamera.DiagnosticsChanged -= CameraDiagnosticsChanged;
			dx12UploadCamera.StatusChanged -= CameraStatusChanged;
			dx12UploadCamera.UpdateTrackingOverlay(PreviewTrackingOverlay.Empty);
			await dx12UploadCamera.DisposeAsync();
			Interlocked.Exchange(ref _lastCameraStoppedTimestamp, Stopwatch.GetTimestamp());
		}
		TextureNativeFrameLease acceptedTextureFrame;
		CameraFrame acceptedDecodedFrame;
		lock (_acceptedFrameLock)
		{
			acceptedTextureFrame = _acceptedTextureFrame;
			_acceptedTextureFrame = null;
			acceptedDecodedFrame = _acceptedDecodedFrame;
			_acceptedDecodedFrame = null;
		}
		acceptedTextureFrame?.Dispose();
		acceptedDecodedFrame?.Dispose();
		if (acceptedTextureFrame != null || acceptedDecodedFrame != null)
		{
			Interlocked.Exchange(ref _analysisBusy, 0);
		}
		CancellationTokenSource cancellation = Interlocked.Exchange(ref _workerCancellation, null);
		AutoResetEvent signal = Interlocked.Exchange(ref _workerSignal, null);
		Task worker = Interlocked.Exchange(ref _workerTask, null);
		Task task = Interlocked.Exchange(ref _healthTask, null);
		cancellation?.Cancel();
		try
		{
			signal?.Set();
		}
		catch (ObjectDisposedException)
		{
		}
		if (task != null)
		{
			try
			{
				await task.WaitAsync(WorkerShutdownGrace);
			}
			catch (OperationCanceledException)
			{
			}
			catch (TimeoutException)
			{
			}
		}
		if (worker != null)
		{
			try
			{
				await worker.WaitAsync(WorkerShutdownGrace);
			}
			catch (TimeoutException)
			{
				_tracker?.Dispose();
				try
				{
					await worker.WaitAsync(WorkerShutdownGrace);
				}
				catch
				{
				}
			}
			catch (OperationCanceledException)
			{
			}
		}
		_tracker?.Dispose();
		_tracker = null;
		Interlocked.Exchange(ref _latestObservationTicks, 0L);
		Volatile.Write(ref _latestBaseOverlay, PreviewTrackingOverlay.Empty);
		Volatile.Write(ref _latestRegistration, null);
		Volatile.Write(ref _latestCalibrationOverlay, null);
		signal?.Dispose();
		cancellation?.Dispose();
		if (_previewPanel.Dispatcher.CheckAccess())
		{
			_placeholder.Visibility = Visibility.Visible;
			_previewStateText.Text = "Camera off";
			return;
		}
		await _previewPanel.Dispatcher.InvokeAsync(delegate
		{
			_placeholder.Visibility = Visibility.Visible;
			_previewStateText.Text = "Camera off";
		});
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 0)
		{
			await StopAsync();
		}
	}

	private void CameraFrameAvailable(object? sender, TextureNativeFrameInfo frame)
	{
		_sourceWidth = frame.Width;
		_sourceHeight = frame.Height;
		_advertisedSourceFramesPerSecond = frame.FramesPerSecond;
		Interlocked.Increment(ref _sourceFrames);
		Interlocked.Exchange(ref _lastSourceFrameTimestamp, Stopwatch.GetTimestamp());
	}

	private void CameraTextureFrameAvailable(object? sender, TextureNativeFrameLease frame)
	{
		if (Volatile.Read(in _acceptFrames) == 0)
		{
			return;
		}
		DateTime utcNow = DateTime.UtcNow;
		if ((_lastAnalysisAcceptedAtUtc != DateTime.MinValue && utcNow - _lastAnalysisAcceptedAtUtc < AnalysisInterval) || Interlocked.CompareExchange(ref _analysisBusy, 1, 0) != 0)
		{
			return;
		}
		_lastAnalysisAcceptedAtUtc = utcNow;
		TextureNativeFrameLease textureNativeFrameLease;
		try
		{
			textureNativeFrameLease = frame.DuplicatePreviewData();
		}
		catch
		{
			Interlocked.Exchange(ref _analysisBusy, 0);
			return;
		}
		if (textureNativeFrameLease == null)
		{
			Interlocked.Exchange(ref _analysisBusy, 0);
			return;
		}
		lock (_acceptedFrameLock)
		{
			_acceptedTextureFrame = textureNativeFrameLease;
		}
		AutoResetEvent autoResetEvent = Volatile.Read(in _workerSignal);
		if (autoResetEvent == null)
		{
			lock (_acceptedFrameLock)
			{
				if (_acceptedTextureFrame == textureNativeFrameLease)
				{
					_acceptedTextureFrame = null;
					textureNativeFrameLease.Dispose();
					Interlocked.Exchange(ref _analysisBusy, 0);
				}
				return;
			}
		}
		try
		{
			autoResetEvent.Set();
		}
		catch (ObjectDisposedException)
		{
			lock (_acceptedFrameLock)
			{
				if (_acceptedTextureFrame == textureNativeFrameLease)
				{
					_acceptedTextureFrame = null;
					textureNativeFrameLease.Dispose();
					Interlocked.Exchange(ref _analysisBusy, 0);
				}
			}
		}
	}

	private async Task StartUploadCameraAsync(Dx12Camera.PreviewTarget target, CameraDevice cameraDevice, CameraVideoMode mode)
	{
		Dx12UploadCamera dx12UploadCamera = new Dx12UploadCamera(target);
		dx12UploadCamera.FrameAvailable += DecodedCameraFrameAvailable;
		dx12UploadCamera.DiagnosticsChanged += CameraDiagnosticsChanged;
		dx12UploadCamera.StatusChanged += CameraStatusChanged;
		_uploadCamera = dx12UploadCamera;
		await dx12UploadCamera.StartAsync(cameraDevice, mode);
	}

	private void DecodedCameraFrameAvailable(object? sender, CameraFrame frame)
	{
		if (Volatile.Read(in _acceptFrames) == 0)
		{
			return;
		}
		_sourceWidth = frame.Width;
		_sourceHeight = frame.Height;
		Interlocked.Increment(ref _sourceFrames);
		Interlocked.Exchange(ref _lastSourceFrameTimestamp, Stopwatch.GetTimestamp());
		DateTime utcNow = DateTime.UtcNow;
		if ((_lastAnalysisAcceptedAtUtc != DateTime.MinValue && utcNow - _lastAnalysisAcceptedAtUtc < AnalysisInterval) || Interlocked.CompareExchange(ref _analysisBusy, 1, 0) != 0)
		{
			return;
		}
		_lastAnalysisAcceptedAtUtc = utcNow;
		CameraFrame cameraFrame;
		try
		{
			cameraFrame = frame.Duplicate();
		}
		catch
		{
			Interlocked.Exchange(ref _analysisBusy, 0);
			return;
		}
		lock (_acceptedFrameLock)
		{
			_acceptedDecodedFrame = cameraFrame;
		}
		AutoResetEvent autoResetEvent = Volatile.Read(in _workerSignal);
		if (autoResetEvent == null)
		{
			lock (_acceptedFrameLock)
			{
				if (_acceptedDecodedFrame == cameraFrame)
				{
					_acceptedDecodedFrame = null;
					cameraFrame.Dispose();
					Interlocked.Exchange(ref _analysisBusy, 0);
				}
				return;
			}
		}
		try
		{
			autoResetEvent.Set();
		}
		catch (ObjectDisposedException)
		{
			lock (_acceptedFrameLock)
			{
				if (_acceptedDecodedFrame == cameraFrame)
				{
					_acceptedDecodedFrame = null;
					cameraFrame.Dispose();
					Interlocked.Exchange(ref _analysisBusy, 0);
				}
			}
		}
	}

	private void CameraDiagnosticsChanged(object? sender, Direct3D12PreviewDiagnostics diagnostics)
	{
		_renderFramesPerSecond = diagnostics.RenderFramesPerSecond;
		long num = Interlocked.Exchange(ref _lastRenderedFrames, diagnostics.RenderedFrames);
		if (diagnostics.RenderedFrames > num)
		{
			Interlocked.Exchange(ref _lastRenderedFrameTimestamp, Stopwatch.GetTimestamp());
		}
	}

	private void CameraStatusChanged(object? sender, string status)
	{
		if (status.Contains("fail", StringComparison.OrdinalIgnoreCase) || status.Contains("error", StringComparison.OrdinalIgnoreCase) || status.Contains("timeout", StringComparison.OrdinalIgnoreCase) || status.Contains("stopped", StringComparison.OrdinalIgnoreCase))
		{
			PublishStatus(_name + ": " + status);
		}
	}

	private void RunAnalysisWorker(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				_workerSignal?.WaitOne(100);
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			TextureNativeFrameLease acceptedTextureFrame;
			CameraFrame cameraFrame;
			lock (_acceptedFrameLock)
			{
				acceptedTextureFrame = _acceptedTextureFrame;
				_acceptedTextureFrame = null;
				cameraFrame = ((acceptedTextureFrame == null) ? _acceptedDecodedFrame : null);
				_acceptedDecodedFrame = null;
			}
			if (acceptedTextureFrame == null && cameraFrame == null)
			{
				Interlocked.Exchange(ref _analysisBusy, 0);
				continue;
			}
			try
			{
				if (acceptedTextureFrame != null)
				{
					AnalyzeFrame(acceptedTextureFrame);
				}
				else if (cameraFrame != null)
				{
					AnalyzeFrame(cameraFrame);
				}
			}
			catch (Exception ex2)
			{
				PublishStatus(_name + ": MediaPipe skipped one frame: " + ex2.Message);
			}
			finally
			{
				acceptedTextureFrame?.Dispose();
				cameraFrame?.Dispose();
				Interlocked.Exchange(ref _analysisBusy, 0);
			}
		}
	}

	private void AnalyzeFrame(TextureNativeFrameLease frame)
	{
		byte[] nv12PreviewBytes = frame.Nv12PreviewBytes;
		if (nv12PreviewBytes != null && nv12PreviewBytes.Length > 0 && frame.Nv12PreviewStride > 0)
		{
			int outputWidth;
			int outputHeight;
			int bgraStride;
			byte[] array = Nv12FrameConverter.ConvertToBgra(nv12PreviewBytes, frame.Nv12PreviewStride, frame.Width, frame.Height, 1920, out outputWidth, out outputHeight, out bgraStride);
			if (array != null && array.Length != 0 && bgraStride > 0)
			{
				AnalyzeBgraFrame(array, outputWidth, outputHeight, bgraStride);
			}
		}
	}

	private void AnalyzeFrame(CameraFrame frame)
	{
		byte[] bgra;
		int outputWidth;
		int outputHeight;
		int bgraStride;
		if (frame.HasBgra)
		{
			bgra = frame.BgraBytes;
			outputWidth = frame.Width;
			outputHeight = frame.Height;
			bgraStride = frame.Stride;
		}
		else
		{
			if (!frame.HasNv12)
			{
				return;
			}
			byte[] nv12Bytes = frame.Nv12Bytes;
			if (nv12Bytes == null || nv12Bytes.Length <= 0)
			{
				return;
			}
			bgra = Nv12FrameConverter.ConvertToBgra(nv12Bytes, frame.Nv12Stride, frame.Width, frame.Height, 1920, out outputWidth, out outputHeight, out bgraStride) ?? Array.Empty<byte>();
		}
		AnalyzeBgraFrame(bgra, outputWidth, outputHeight, bgraStride);
	}

	private void AnalyzeBgraFrame(byte[] bgra, int outputWidth, int outputHeight, int stride)
	{
		if (bgra.Length == 0 || outputWidth <= 0 || outputHeight <= 0 || stride <= 0)
		{
			return;
		}
		BitmapSource bitmapSource = BitmapSource.Create(outputWidth, outputHeight, 96.0, 96.0, PixelFormats.Bgra32, null, bgra, stride);
		bitmapSource.Freeze();
		if (Volatile.Read(in _calibrationCaptureEnabled) != 0)
		{
			this.CalibrationFrameAvailable?.Invoke(new DualCameraCalibrationFrame(DateTime.UtcNow, outputWidth, outputHeight, stride, bgra));
		}
		MediaPipeFaceLandmarkerSidecarTracker tracker = _tracker;
		if (tracker != null && Volatile.Read(in _acceptFrames) != 0)
		{
			FaceLandmarkFrame landmarkFrame = tracker.Detect(bitmapSource, DateTime.UtcNow).LandmarkFrame;
			PreviewTrackingOverlay value = MediaPipePreviewOverlayFactory.Create(landmarkFrame);
			DualCameraObservation dualCameraObservation = DualCameraObservation.Create(landmarkFrame, outputWidth, outputHeight, stride, bgra);
			Volatile.Write(ref _latestBaseOverlay, value);
			Interlocked.Exchange(ref _latestObservationTicks, dualCameraObservation?.CapturedAtUtc.Ticks ?? 0);
			Volatile.Write(ref _latestRegistration, null);
			PublishTrackingOverlay();
			Interlocked.Increment(ref _analysisFrames);
			_trackingConfidence = landmarkFrame.TrackingConfidence;
			Interlocked.Exchange(ref _hasFace, landmarkFrame.HasFace ? 1 : 0);
			if ((object)dualCameraObservation != null)
			{
				this.ObservationAvailable?.Invoke(dualCameraObservation);
			}
		}
	}

	private void PublishTrackingOverlay()
	{
		PreviewTrackingOverlay previewTrackingOverlay = Volatile.Read(in _latestBaseOverlay);
		DualCameraRegistrationFrame dualCameraRegistrationFrame = Volatile.Read(in _latestRegistration);
		List<PreviewOverlayDiagnosticMesh> list = new List<PreviewOverlayDiagnosticMesh>(3);
		if (Volatile.Read(in _translationViewEnabled) != 0 && (object)dualCameraRegistrationFrame != null && dualCameraRegistrationFrame.TargetCapturedAtUtc.Ticks == Interlocked.Read(in _latestObservationTicks))
		{
			PreviewOverlayMesh faceMesh = previewTrackingOverlay.FaceMesh;
			if ((object)faceMesh != null)
			{
				list.Add(new PreviewOverlayDiagnosticMesh(dualCameraRegistrationFrame.TranslatedPartnerPoints, faceMesh.Edges, PreviewOverlayDiagnosticMeshRole.TranslatedPartner, DrawPoints: false));
				list.Add(new PreviewOverlayDiagnosticMesh(dualCameraRegistrationFrame.FusedPoints, Array.Empty<PreviewOverlayEdge>(), PreviewOverlayDiagnosticMeshRole.DirectViewFusion));
			}
		}
		DualCameraCalibrationOverlay dualCameraCalibrationOverlay = Volatile.Read(in _latestCalibrationOverlay);
		if (Volatile.Read(in _calibrationCaptureEnabled) != 0 && (object)dualCameraCalibrationOverlay != null)
		{
			list.Add(new PreviewOverlayDiagnosticMesh(dualCameraCalibrationOverlay.Points, dualCameraCalibrationOverlay.Edges, PreviewOverlayDiagnosticMeshRole.CalibrationBoard));
		}
		if (list.Count > 0)
		{
			previewTrackingOverlay = previewTrackingOverlay with
			{
				DiagnosticMeshes = list
			};
		}
		_camera?.UpdateTrackingOverlay(previewTrackingOverlay);
		_uploadCamera?.UpdateTrackingOverlay(previewTrackingOverlay);
	}

	private async Task RunHealthMonitorAsync(CancellationToken cancellationToken)
	{
		long previousTimestamp = Stopwatch.GetTimestamp();
		RefreshNativeSourceHeartbeat();
		long previousSourceFrames = Interlocked.Read(in _sourceFrames);
		long previousAnalysisFrames = Interlocked.Read(in _analysisFrames);
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(HealthPollInterval, cancellationToken);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			if (Volatile.Read(in _acceptFrames) == 0)
			{
				break;
			}
			long timestamp = Stopwatch.GetTimestamp();
			double num = Math.Max(0.001, Stopwatch.GetElapsedTime(previousTimestamp, timestamp).TotalSeconds);
			RefreshNativeSourceHeartbeat();
			long num2 = Interlocked.Read(in _sourceFrames);
			long num3 = Interlocked.Read(in _analysisFrames);
			_measuredSourceFramesPerSecond = (double)(num2 - previousSourceFrames) / num;
			_measuredAnalysisFramesPerSecond = (double)(num3 - previousAnalysisFrames) / num;
			previousTimestamp = timestamp;
			previousSourceFrames = num2;
			previousAnalysisFrames = num3;
			PublishStatistics();
			TimeSpan elapsedTime = Stopwatch.GetElapsedTime(Interlocked.Read(in _lastSourceFrameTimestamp), timestamp);
			if (elapsedTime >= SourceStallTimeout)
			{
				RequestRecovery($"camera source produced no frames for {elapsedTime.TotalSeconds:0.#} seconds");
				break;
			}
			TimeSpan elapsedTime2 = Stopwatch.GetElapsedTime(Interlocked.Read(in _lastRenderedFrameTimestamp), timestamp);
			if (elapsedTime2 < PreviewRefreshTimeout)
			{
				continue;
			}
			if (elapsedTime2 >= PreviewRestartTimeout)
			{
				RequestRecovery($"DX12 preview produced no frames for {elapsedTime2.TotalSeconds:0.#} seconds");
				break;
			}
			long num4 = Interlocked.Read(in _lastPreviewRefreshTimestamp);
			if (num4 == 0L || !(Stopwatch.GetElapsedTime(num4, timestamp) < PreviewRefreshCooldown))
			{
				Interlocked.Exchange(ref _lastPreviewRefreshTimestamp, timestamp);
				PublishStatus(_name + ": source is live; refreshing the stalled DX12 presenter.");
				await _previewPanel.Dispatcher.InvokeAsync(delegate
				{
					_camera?.ResumePreview();
					_uploadCamera?.ResumePreview();
				});
			}
		}
	}

	private void RefreshNativeSourceHeartbeat()
	{
		Dx12Camera? camera = _camera;
		if (camera == null)
		{
			return;
		}
		Interlocked.Exchange(ref _sourceFrames, camera.FramesRead);
		long sourceTimestamp = camera.LastSourceFrameTimestamp;
		if (sourceTimestamp != 0L)
		{
			Interlocked.Exchange(ref _lastSourceFrameTimestamp, sourceTimestamp);
		}
	}

	private void PublishStatistics()
	{
		string value = ((Volatile.Read(in _hasFace) != 0) ? $"lock {_trackingConfidence:P0}" : "face waiting");
		PublishStatus($"{_name}: {_sourceWidth}x{_sourceHeight} | source {_measuredSourceFramesPerSecond:0.#}/{_advertisedSourceFramesPerSecond:0.#} fps | preview {_renderFramesPerSecond:0.#} fps | MediaPipe {_measuredAnalysisFramesPerSecond:0.#} fps | {value}");
	}

	private void RequestRecovery(string reason)
	{
		if (Interlocked.CompareExchange(ref _recoveryScheduled, 1, 0) == 0 && Volatile.Read(in _acceptFrames) != 0)
		{
			PublishStatus(_name + ": " + reason + "; reopening only this camera lane.");
			_previewPanel.Dispatcher.InvokeAsync(delegate
			{
				_recoveryRequested(reason);
			});
		}
	}

	private void PublishStatus(string status)
	{
		if (_previewPanel.Dispatcher.CheckAccess())
		{
			_statusChanged(status);
			return;
		}
		_previewPanel.Dispatcher.InvokeAsync(delegate
		{
			_statusChanged(status);
		});
	}
}
