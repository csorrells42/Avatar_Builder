using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AvatarBuilder.Modules.Infrastructure;
using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.Identity;
using AvatarBuilder.Modules.Vision.MediaPipe;
using AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction;
using AvatarBuilder.Modules.Vision.Onnx;
using AvatarBuilder.Modules.Vision.Personalization;
using AvatarBuilder.Modules.Vision.Pipeline;
using AvatarBuilder.Modules.Vision.Reconstruction;
using AvatarBuilder.Modules.Vision.Reconstruction.Warping;
using AvatarBuilder.Modules.Webcam;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectShow;
using AvatarBuilder.Modules.Webcam.DirectX12;
using AvatarBuilder.Modules.Webcam.DualCamera;
using AvatarBuilder.Modules.Webcam.Ffmpeg;
using AvatarBuilder.Modules.Webcam.MediaFoundation;
using AvatarBuilder.Modules.Webcam.Pipeline;
using Microsoft.Win32;

namespace AvatarBuilder;

public partial class MainWindow
{
	private static readonly TimeSpan PersonIdentityObservationInterval =
		TimeSpan.FromMilliseconds(250);

	private async void CameraToggleChanged(object sender, RoutedEventArgs e)
	{
		if (_isUpdatingCameraToggle)
		{
			return;
		}
		if (CameraToggle.IsChecked == true)
		{
			_cameraShouldRun = true;
			_consecutiveCameraRecoveryAttempts = 0;
			Volatile.Write(ref _nextCameraRecoveryTimestamp, 0L);
			await StartPreviewAsync();
		}
		else
		{
			_cameraShouldRun = false;
			_cameraRecoveryPending = false;
			await StopPreviewAsync();
		}
	}

	private async void DirectX12PreviewMenuItemClicked(object sender, RoutedEventArgs e)
	{
		_isDirectX12PreviewEnabled = DirectX12PreviewMenuItem.IsChecked;
		if (_isCameraEnabled)
		{
			await RestartPreviewAsync();
			return;
		}
		UpdateDirectX12PreviewMode();
		if (_latestFrame != null)
		{
			SetPreviewState("Camera active", _latestFrame);
		}
	}

	private void FaceMeshOverlayMenuItemClicked(object sender, RoutedEventArgs e)
	{
		_showFaceMeshOverlay = FaceMeshOverlayMenuItem.IsChecked;
		UpdateFaceCueGuideOverlay(_latestFrame);
	}

	private void LiveWireframeMenuItemClicked(object sender, RoutedEventArgs e)
	{
		_showLiveWireframePreview = LiveWireframeMenuItem.IsChecked;
		SetPreviewState(_showLiveWireframePreview ? "Live wireframe preview" : "Camera active", _latestFrame);
	}

	private async Task StartPreviewAsync()
	{
		int operationGeneration = Interlocked.Increment(ref _cameraLifecycleGeneration);
		CancellationTokenSource cancellation = new CancellationTokenSource();
		CancellationTokenSource? previousCancellation = Interlocked.Exchange(ref _cameraStartCancellation, cancellation);
		previousCancellation?.Cancel();
		bool enteredLifecycleGate = false;
		try
		{
			await _cameraLifecycleGate.WaitAsync(cancellation.Token);
			enteredLifecycleGate = true;
			if (IsCurrentCameraStart(operationGeneration, cancellation.Token))
			{
				await StartPreviewCoreAsync(operationGeneration, cancellation.Token);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			_isCameraEnabled = false;
			SetCameraToggle(_cameraShouldRun);
			if (!_isClosing)
			{
				SetStatus(
					"Camera start failed without taking down the app; " +
					"the continuity guard will retry: " + ex.Message);
			}
		}
		finally
		{
			if (enteredLifecycleGate)
			{
				_cameraLifecycleGate.Release();
			}
			if (Interlocked.CompareExchange(ref _cameraStartCancellation, null, cancellation) == cancellation)
			{
				cancellation.Dispose();
			}
			previousCancellation?.Dispose();
		}
	}

	private async Task StartPreviewCoreAsync(int operationGeneration, CancellationToken cancellationToken)
	{
		object selectedItem = CameraComboBox.SelectedItem;
		if (!(selectedItem is CameraDevice camera))
		{
			_isCameraEnabled = false;
			SetCameraToggle(_cameraShouldRun);
			SetStatus(_cameraShouldRun
				? "No camera is selected. The camera watchdog will keep retrying."
				: "Choose a camera first.");
			return;
		}
		CameraVideoMode mode = (CameraModeComboBox.SelectedItem as CameraVideoMode) ?? CameraVideoMode.Auto;
		Volatile.Write(ref _cameraStartedTimestamp, Stopwatch.GetTimestamp());
		Volatile.Write(ref _lastCameraSourceFrameTimestamp, 0L);
		Volatile.Write(ref _lastCameraPresentedFrameTimestamp, 0L);
		ApplySelectedCameraMode(mode);
		SetPreviewState($"Starting {camera.Name} ({mode.Label})", null);
		SetStatus($"Opening camera: {camera.Name} ({mode.Label})");
		Task pendingNativeDisposal =
			Volatile.Read(ref _pendingNativeCameraDisposal);
		if (!pendingNativeDisposal.IsCompleted)
		{
			try
			{
				await pendingNativeDisposal.WaitAsync(
					CameraRecoveryNativeReleaseWait,
					cancellationToken);
			}
			catch (TimeoutException)
			{
				// The old instance is isolated on its cleanup thread. Continue
				// opening; a busy device will be retried by the watchdog.
			}
		}
		if (IsDirectX12PreviewEnabled() && TryStartDirectX12NativeCamera(camera, mode))
		{
			if (!IsCurrentCameraStart(operationGeneration, cancellationToken))
			{
				StopPreviewCore();
				return;
			}
			_isCameraEnabled = true;
			SetCameraToggle(enabled: true);
			ScheduleDirectX12PreviewWakeAfterCameraStart();
			SetStatus($"Camera active through native DX12 texture path: {camera.Name} ({mode.Label})");
			return;
		}
		_directX12PreviewMaxRenderFramesPerSecond = (IsDirectX12PreviewEnabled() ? GetDirectX12PreviewRenderFramesPerSecond(mode, nativeTexturePath: false) : 0.0);
		UpdateDirectX12PreviewMode();
		_isCameraEnabled = await _previewService.StartAsync(camera, mode, cancellationToken);
		if (!IsCurrentCameraStart(operationGeneration, cancellationToken))
		{
			StopPreviewCore();
			return;
		}
		if (!_isCameraEnabled && !mode.IsAuto)
		{
			SetStatus("Selected camera mode failed. Retrying with Auto safe mode...");
			SetPreviewState("Retrying camera with Auto safe mode", null);
			CameraModeComboBox.SelectedItem = CameraVideoMode.Auto;
			_isCameraEnabled = await _previewService.StartAsync(camera, CameraVideoMode.Auto, cancellationToken);
			if (!IsCurrentCameraStart(operationGeneration, cancellationToken))
			{
				StopPreviewCore();
				return;
			}
		}
		SetCameraToggle(_cameraShouldRun);
		if (_isCameraEnabled)
		{
			SetStatus($"Camera active: {camera.Name} ({mode.Label})");
		}
		else
		{
			SetPreviewState("Camera failed to start", null);
			SetStatus(_cameraShouldRun
				? "Camera did not open. It remains armed and will retry automatically."
				: "Camera failed to open.");
		}
	}

	private bool IsCurrentCameraStart(int operationGeneration, CancellationToken cancellationToken)
	{
		if (!_isClosing && !cancellationToken.IsCancellationRequested)
		{
			return operationGeneration == Volatile.Read(in _cameraLifecycleGeneration);
		}
		return false;
	}

	private async Task RestartPreviewAsync()
	{
		if (_isCameraEnabled)
		{
			await StopPreviewAsync(keepToggleChecked: true);
			await StartPreviewAsync();
		}
	}

	private async Task StopPreviewAsync(
		bool keepToggleChecked = false,
		TimeSpan? gateTimeout = null,
		bool releaseNativeCameraInBackground = false)
	{
		Interlocked.Increment(ref _cameraLifecycleGeneration);
		CancellationTokenSource? startCancellation = Interlocked.Exchange(ref _cameraStartCancellation, null);
		startCancellation?.Cancel();
		bool flag;
		if (gateTimeout.HasValue)
		{
			TimeSpan valueOrDefault = gateTimeout.GetValueOrDefault();
			flag = await _cameraLifecycleGate.WaitAsync(valueOrDefault);
		}
		else
		{
			flag = await WaitForCameraLifecycleGateAsync();
		}
		bool flag2 = flag;
		try
		{
			if (!flag2)
			{
				WriteShutdownTrace("Camera lifecycle gate timed out; forcing final camera release.");
			}
			StopPreviewCore(
				keepToggleChecked,
				releaseNativeCameraInBackground);
		}
		finally
		{
			if (flag2)
			{
				_cameraLifecycleGate.Release();
			}
			startCancellation?.Dispose();
		}
	}

	private async Task<bool> WaitForCameraLifecycleGateAsync()
	{
		await _cameraLifecycleGate.WaitAsync();
		return true;
	}

	private void StopPreviewCore(
		bool keepToggleChecked = false,
		bool releaseNativeCameraInBackground = false)
	{
		DisposeDirectX12NativeCamera(releaseNativeCameraInBackground);
		_previewService.Stop();
		DisposeDirectX12PreviewHost();
		_isCameraEnabled = false;
		Volatile.Write(ref _cameraStartedTimestamp, 0L);
		Volatile.Write(ref _lastCameraSourceFrameTimestamp, 0L);
		Volatile.Write(ref _lastCameraPresentedFrameTimestamp, 0L);
		_currentFaceFeatureDetection = FaceFeatureDetection.None;
		ResetLandmarkTracking();
		if (!keepToggleChecked)
		{
			SetCameraToggle(enabled: false);
			SetPreviewState("Camera disabled", null);
		}
	}

	private void SetCameraToggle(bool enabled)
	{
		_isUpdatingCameraToggle = true;
		CameraToggle.IsChecked = enabled;
		CameraToggle.Content = (enabled ? "Camera On" : "Camera Off");
		_isUpdatingCameraToggle = false;
	}

	private async void CameraHealthTimerTick(object? sender, EventArgs e)
	{
		if (_isClosing || !_cameraShouldRun || _cameraRecoveryPending)
		{
			return;
		}
		long now = Stopwatch.GetTimestamp();
		long nextRecoveryTimestamp =
			Volatile.Read(ref _nextCameraRecoveryTimestamp);
		if (nextRecoveryTimestamp != 0L && now < nextRecoveryTimestamp)
		{
			return;
		}
		if (!_isCameraEnabled)
		{
			await RecoverCameraAsync(
				"camera is armed but no capture path is active",
				rememberNativeFailure: false);
			return;
		}
		long nativeSourceTimestamp = _directX12NativeCamera?.LastSourceFrameTimestamp ?? 0L;
		long lastSourceTimestamp = nativeSourceTimestamp != 0L
			? nativeSourceTimestamp
			: Volatile.Read(ref _lastCameraSourceFrameTimestamp);
		long referenceTimestamp = lastSourceTimestamp == 0L
			? Volatile.Read(ref _cameraStartedTimestamp)
			: lastSourceTimestamp;
		if (referenceTimestamp != 0L
			&& Stopwatch.GetElapsedTime(referenceTimestamp, now) >=
				CameraStallTimeout)
		{
			await RecoverCameraAsync(
				"source capture stopped producing current frames",
				rememberNativeFailure: _directX12NativeCamera != null);
			return;
		}

		long nativePresentationTimestamp =
			_directX12NativeCamera?.LastPresentedFrameTimestamp ?? 0L;
		long lastPresentationTimestamp =
			nativePresentationTimestamp != 0L
				? nativePresentationTimestamp
				: Volatile.Read(ref _lastCameraPresentedFrameTimestamp);
		long presentationReferenceTimestamp =
			lastPresentationTimestamp == 0L
				? Volatile.Read(ref _cameraStartedTimestamp)
				: lastPresentationTimestamp;
		if (presentationReferenceTimestamp != 0L
			&& Stopwatch.GetElapsedTime(
				presentationReferenceTimestamp,
				now) >= CameraPresentationStallTimeout)
		{
			await RecoverCameraAsync(
				"display stopped presenting current camera frames",
				rememberNativeFailure: _directX12NativeCamera != null);
		}
	}

	private async Task RecoverCameraAsync(
		string reason,
		bool rememberNativeFailure)
	{
		if (_isClosing
			|| !_cameraShouldRun
			|| _cameraRecoveryPending)
		{
			return;
		}
		_cameraRecoveryPending = true;
		int attempt = Interlocked.Increment(
			ref _consecutiveCameraRecoveryAttempts);
		double backoffSeconds = Math.Min(
			CameraRecoveryMaximumBackoff.TotalSeconds,
			Math.Pow(2.0, Math.Min(4, Math.Max(0, attempt - 1))) * 0.5);
		long nextRecoveryTimestamp = Stopwatch.GetTimestamp()
			+ (long)(Stopwatch.Frequency * backoffSeconds);
		Volatile.Write(
			ref _nextCameraRecoveryTimestamp,
			nextRecoveryTimestamp);
		if (rememberNativeFailure
			&& _directX12NativeCamera != null
			&& CameraComboBox.SelectedItem is CameraDevice camera)
		{
			CameraVideoMode mode = (CameraModeComboBox.SelectedItem as CameraVideoMode) ?? CameraVideoMode.Auto;
			TextureNativePreviewPolicy.RememberPreviewFailure(
				camera,
				mode,
				reason);
		}
		SetStatus(
			$"Camera continuity guard isolated a failed lane ({reason}). " +
			$"Reopening now; recovery {attempt}.");
		try
		{
			await StopPreviewAsync(
				keepToggleChecked: true,
				releaseNativeCameraInBackground: true);
			if (_cameraShouldRun && !_isClosing)
			{
				if (attempt == 2
					|| attempt % 3 == 0
					|| CameraComboBox.SelectedItem == null)
				{
					await RefreshCamerasAsync();
				}
				await StartPreviewAsync();
			}
		}
		catch (Exception ex)
		{
			_isCameraEnabled = false;
			SetCameraToggle(_cameraShouldRun);
			SetStatus(
				"Camera recovery was isolated after an error and will retry: " +
				ex.Message);
		}
		finally
		{
			_cameraRecoveryPending = false;
		}
	}

	private void RecordCameraPresentationHeartbeat()
	{
		Volatile.Write(
			ref _lastCameraPresentedFrameTimestamp,
			Stopwatch.GetTimestamp());
		Interlocked.Exchange(
			ref _consecutiveCameraRecoveryAttempts,
			0);
		Volatile.Write(ref _nextCameraRecoveryTimestamp, 0L);
	}

	private void PreviewFrameAvailable(object? sender, BitmapSource frame)
	{
		if (_isClosing || Interlocked.CompareExchange(ref _uiFrameInFlight, 1, 0) != 0)
		{
			return;
		}
		ProcessReservedPreviewFrame(frame);
	}

	private void ProcessReservedPreviewFrame(BitmapSource frame)
	{
		try
		{
			if (base.Dispatcher.CheckAccess())
			{
				ProcessPreviewFrame(frame);
			}
			else
			{
				base.Dispatcher.BeginInvoke((Action)delegate
				{
					try
					{
						ProcessPreviewFrame(frame);
					}
					catch (Exception ex)
					{
						Interlocked.Exchange(ref _uiFrameInFlight, 0);
						if (!_isClosing)
						{
							ReportRecoverableVisionError("Camera preview skipped one frame and recovered: " + ex.Message);
						}
					}
				}, DispatcherPriority.Background);
			}
		}
		catch (Exception ex)
		{
			Interlocked.Exchange(ref _uiFrameInFlight, 0);
			if (!_isClosing)
			{
				ReportRecoverableVisionError("Camera preview skipped one frame and recovered: " + ex.Message);
			}
		}
	}

	private void PreviewCameraFrameAvailable(object? sender, CameraFrame frame)
	{
		Volatile.Write(ref _lastCameraSourceFrameTimestamp, Stopwatch.GetTimestamp());
		if (ShouldSubmitPersonIdentityObservation(Stopwatch.GetTimestamp()))
		{
			_personIdentityCameraFrameWorker.TryAccept(frame);
		}
		if (!IsDirectX12PreviewEnabled())
		{
			return;
		}
		Direct3D12PreviewHost? directX12PreviewHost;
		lock (_directX12PreviewLock)
		{
			directX12PreviewHost = _directX12PreviewHost;
		}
		if (directX12PreviewHost == null)
		{
			return;
		}
		try
		{
			directX12PreviewHost.RenderBgraFrame(frame, Interlocked.Increment(ref _directX12FrameNumber));
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			base.Dispatcher.InvokeAsync(delegate
			{
				SetStatus("DX12 preview paused: " + ex2.Message);
			}, DispatcherPriority.Background);
		}
	}

	private bool TryStartDirectX12NativeCamera(CameraDevice camera, CameraVideoMode mode)
	{
		if (TextureNativePreviewPolicy.TryGetPreviewFailure(camera, mode, out string reason))
		{
			SetStatus("Native DX12 camera path cooling down after a previous failure: " + reason + ". Falling back to standard camera path.");
			return false;
		}
		DisposeDirectX12NativeCamera();
		DisposeDirectX12PreviewHost();
		DirectX12PreviewLayer.Children.Clear();
		DirectX12PreviewLayer.Visibility = Visibility.Visible;
		try
		{
			Dx12Camera.PreviewTarget target = new Dx12Camera.PreviewTarget(DirectX12PreviewLayer, PreviewImage, PreviewPlaceholder, PreviewStateText, 0, "Avatar Builder");
			_directX12PreviewMaxRenderFramesPerSecond = GetDirectX12PreviewRenderFramesPerSecond(mode, nativeTexturePath: true);
			Dx12CameraOptions options = new Dx12CameraOptions
			{
				Camera = camera,
				Mode = mode,
				MaxPreviewRenderFramesPerSecond = _directX12PreviewMaxRenderFramesPerSecond,
				FrameAvailable = DirectX12NativeFrameAvailable,
				TextureFrameAvailable = DirectX12NativeTextureFrameAvailable,
				DiagnosticsChanged = DirectX12NativeDiagnosticsChanged,
				StatusChanged = DirectX12NativeStatusChanged
			};
			_directX12NativeCamera = WebcamModule.StartDx12Camera(target, options);
			ResetPipelineDiagnostics();
			ApplyDirectX12PreviewPresentationState();
			TextureNativePreviewPolicy.ForgetPreviewFailure(camera, mode);
			PreviewImage.Visibility = Visibility.Collapsed;
			PreviewPlaceholder.Visibility = Visibility.Collapsed;
			UpdateFaceCueGuideOverlay(_latestFrame);
			return true;
		}
		catch (Exception ex)
		{
			TextureNativePreviewPolicy.RememberPreviewFailure(camera, mode, ex.Message);
			DisposeDirectX12NativeCamera();
			DirectX12PreviewLayer.Children.Clear();
			SetStatus("Native DX12 camera path unavailable: " + ex.Message + ". Falling back to standard camera path.");
			return false;
		}
	}

	private void DirectX12NativeStatusChanged(object? sender, string status)
	{
		base.Dispatcher.InvokeAsync(delegate
		{
			SetStatus(status);
		}, DispatcherPriority.Background);
	}

	private void DirectX12NativeDiagnosticsChanged(object? sender, Direct3D12PreviewDiagnostics diagnostics)
	{
		DirectX12PreviewDiagnosticsChanged(sender, diagnostics);
	}

	private void DirectX12NativeFrameAvailable(object? sender, TextureNativeFrameInfo frame)
	{
		long sourceTimestamp = _directX12NativeCamera?.LastSourceFrameTimestamp ?? 0L;
		Volatile.Write(
			ref _lastCameraSourceFrameTimestamp,
			sourceTimestamp != 0L ? sourceTimestamp : Stopwatch.GetTimestamp());
	}

	private void DirectX12NativeTextureFrameAvailable(object? sender, TextureNativeFrameLease frame)
	{
		long capturedAtTimestamp = frame.CapturedAtTimestamp != 0L
			? frame.CapturedAtTimestamp
			: Stopwatch.GetTimestamp();
		if (ShouldSubmitPersonIdentityObservation(capturedAtTimestamp))
		{
			_personIdentityWorker.TryAcceptPreviewData(frame);
		}
		if (!TryBeginDirectX12Analysis())
		{
			return;
		}
		bool handedOff = false;
		try
		{
			handedOff = _directX12AnalysisWorker.TryAcceptTexture(frame);
		}
		catch (Exception ex)
		{
			ReportRecoverableVisionError("DX12 analysis could not retain one frame: " + ex.Message);
		}
		finally
		{
			if (!handedOff)
			{
				Interlocked.Exchange(ref _faceFeatureDetectionInFlight, 0);
				Interlocked.Exchange(ref _directX12AnalysisInFlight, 0);
			}
		}
	}

	private bool ShouldSubmitPersonIdentityObservation(long timestamp)
	{
		if (_isClosing || !_personIdentityMemory.IsAvailable)
		{
			return false;
		}
		long previous = Volatile.Read(
			ref _lastPersonIdentityObservationTimestamp);
		if (previous != 0L
			&& Stopwatch.GetElapsedTime(previous, timestamp)
				< PersonIdentityObservationInterval)
		{
			return false;
		}
		return Interlocked.CompareExchange(
			ref _lastPersonIdentityObservationTimestamp,
			timestamp,
			previous) == previous;
	}

	private void ProcessPersonIdentityFrame(TextureNativeFrameLease frame)
	{
		if (frame.Age > MaximumLiveAwarenessFrameAge)
		{
			return;
		}
		byte[]? nv12 = frame.Nv12PreviewBytes;
		if (nv12 is null)
		{
			return;
		}
		ObservePersonIdentityNv12(
			nv12,
			frame.Nv12PreviewStride,
			frame.Width,
			frame.Height,
			frame.CapturedAtUtc);
	}

	private void ProcessPersonIdentityCameraFrame(CameraFrame frame)
	{
		if (frame.HasBgra)
		{
			_personIdentityMemory.ObserveBgra(
				frame.BgraBytes,
				frame.Width,
				frame.Height,
				frame.Stride,
				DateTime.UtcNow);
			return;
		}
		if (frame.Nv12Bytes is not null && frame.HasNv12)
		{
			ObservePersonIdentityNv12(
				frame.Nv12Bytes,
				frame.Nv12Stride,
				frame.Width,
				frame.Height,
				DateTime.UtcNow);
		}
	}

	private void ObservePersonIdentityNv12(
		byte[] nv12,
		int nv12Stride,
		int width,
		int height,
		DateTime capturedAtUtc)
	{
		const int maximumObservationWidth = 960;
		if (!Nv12FrameConverter.TryGetOutputLayout(
			nv12,
			nv12Stride,
			width,
			height,
			maximumObservationWidth,
			out int outputWidth,
			out int outputHeight,
			out int bgraStride,
			out int requiredLength))
		{
			return;
		}
		byte[] bgra = ArrayPool<byte>.Shared.Rent(requiredLength);
		try
		{
			if (!Nv12FrameConverter.TryConvertToBgra(
				nv12,
				nv12Stride,
				width,
				height,
				outputWidth,
				outputHeight,
				bgraStride,
				bgra.AsSpan(0, requiredLength)))
			{
				return;
			}
			_personIdentityMemory.ObserveBgra(
				bgra,
				outputWidth,
				outputHeight,
				bgraStride,
				capturedAtUtc);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(bgra);
		}
	}

	private void PersonIdentityMemorySnapshotChanged(
		object? sender,
		PersonIdentitySnapshot snapshot)
	{
		if (_isClosing)
		{
			return;
		}
		base.Dispatcher.InvokeAsync(() =>
		{
			if (!_isClosing)
			{
				SetTextIfChanged(
					PeopleMemoryStatusText,
					snapshot.Status);
			}
		}, DispatcherPriority.Background);
	}

	private void ProcessDirectX12AnalysisFrame(TextureNativeFrameLease frame)
	{
		bool bitmapDetectionOwnsGate = false;
		bool completed = false;
		try
		{
			FaceBoxSystem faceBoxSystem = _selectedFaceBoxSystem;
			int faceBoxSystemGeneration = _faceBoxSystemGeneration;
			int faceAnalysisGeneration =
				Volatile.Read(ref _faceAnalysisGeneration);
			if (TryProcessDirectMlTextureFrame(
				frame,
				faceBoxSystem,
				faceBoxSystemGeneration,
				faceAnalysisGeneration))
			{
				completed = true;
				return;
			}

			if (TryCreateBitmapFromDirectX12TextureFrame(frame, out BitmapSource bitmap))
			{
				Volatile.Write(ref _latestFrame, bitmap);
				bitmapDetectionOwnsGate = true;
				ProcessFaceFeatureDetectionFrame(
					bitmap,
					frame.CapturedAtTimestamp,
					frame.CapturedAtUtc,
					faceBoxSystem,
					faceBoxSystemGeneration,
					faceAnalysisGeneration);
				completed = true;
			}
		}
		catch (Exception ex)
		{
			ReportRecoverableVisionError("DX12 analysis skipped one frame and recovered: " + ex.Message);
		}
		finally
		{
			if (completed)
			{
				Interlocked.Increment(ref _directX12AnalysisCompletedFrames);
			}
			if (!bitmapDetectionOwnsGate)
			{
				Interlocked.Exchange(ref _faceFeatureDetectionInFlight, 0);
			}
			Interlocked.Exchange(ref _directX12AnalysisInFlight, 0);
		}
	}

	private bool TryProcessDirectMlTextureFrame(
		TextureNativeFrameLease frame,
		FaceBoxSystem faceBoxSystem,
		int faceBoxSystemGeneration,
		int faceAnalysisGeneration)
	{
		if (faceBoxSystem != FaceBoxSystem.MediaPipe
			|| _mediaPipeExecutionBackend != MediaPipeExecutionBackend.Gpu
			|| !frame.MediaSubtype.Contains(
				"NV12",
				StringComparison.OrdinalIgnoreCase)
			|| (frame.Resource == IntPtr.Zero
				&& frame.D3D12SharedTextureHandle == IntPtr.Zero))
		{
			DisposeDirectMlTextureTracker();
			return false;
		}
		if (IsLiveAwarenessFrameExpired(frame.CapturedAtTimestamp))
		{
			ExpireLiveAwarenessState(frame.CapturedAtTimestamp);
			return true;
		}

		try
		{
			int cameraGeneration =
				Volatile.Read(ref _cameraLifecycleGeneration);
			if (_directMlTextureTracker == null
				|| _directMlTextureTrackerCameraGeneration != cameraGeneration
				|| !_directMlTextureTracker.CanProcess(frame))
			{
				DisposeDirectMlTextureTracker();
				MediaPipeDirectMlModelEnvironment models =
					MediaPipeSidecarPythonEnvironment.DetectDirectMlModels();
				if (!models.IsReady)
				{
					throw new InvalidOperationException(models.Status);
				}
				_directMlTextureTracker =
					new MediaPipeDirectMlTextureTracker(
						frame,
						models.DetectorModelPath,
						models.LandmarkerModelPath);
				_directMlTextureTrackerCameraGeneration = cameraGeneration;
			}

			DateTime capturedAtUtc = frame.CapturedAtUtc == DateTime.MinValue
				? DateTime.UtcNow
				: frame.CapturedAtUtc;
			FaceLandmarkTrackingResult faceTracking =
				_directMlTextureTracker.Detect(frame, capturedAtUtc);
			FaceBoxTrackingFrameResult trackingResult = new(
				faceBoxSystem,
				faceBoxSystemGeneration,
				faceTracking,
				null,
				frame.CapturedAtTimestamp,
				capturedAtUtc,
				frame.Width,
				frame.Height,
				null);
			ProcessFaceTrackingFrameResult(
				trackingResult,
				faceAnalysisGeneration);
			return true;
		}
		catch (Exception ex)
		{
			DisposeDirectMlTextureTracker();
			ReportRecoverableVisionError(
				"GPU texture tracking skipped one frame and recovered: " +
				ex.Message);
			return false;
		}
	}

	private void DisposeDirectMlTextureTracker()
	{
		MediaPipeDirectMlTextureTracker? tracker =
			_directMlTextureTracker;
		_directMlTextureTracker = null;
		_directMlTextureTrackerCameraGeneration = -1;
		tracker?.Dispose();
	}

	private bool TryBeginDirectX12Analysis()
	{
		if (_isClosing
			|| Volatile.Read(ref _faceTrackingEnabled) == 0
			|| !IsSelectedFaceBoxSystemAvailable()
			|| Volatile.Read(ref _faceFeatureDetectionInFlight) != 0
			|| Interlocked.CompareExchange(ref _directX12AnalysisInFlight, 1, 0) != 0)
		{
			return false;
		}
		if (Interlocked.CompareExchange(ref _faceFeatureDetectionInFlight, 1, 0) != 0)
		{
			Interlocked.Exchange(ref _directX12AnalysisInFlight, 0);
			return false;
		}
		return true;
	}

	private bool TryCreateBitmapFromDirectX12TextureFrame(TextureNativeFrameLease frame, out BitmapSource bitmap)
	{
		bitmap = EmptyBgraBitmap;
		int maximumWidth = Math.Clamp(frame.Width, 320, 3840);
		byte[]? nv12PreviewBytes = frame.Nv12PreviewBytes;
		if (nv12PreviewBytes == null
			|| !Nv12FrameConverter.TryGetOutputLayout(
				nv12PreviewBytes,
				frame.Nv12PreviewStride,
				frame.Width,
				frame.Height,
				maximumWidth,
				out int outputWidth,
				out int outputHeight,
				out int bgraStride,
				out int requiredLength))
		{
			return false;
		}
		byte[] bgra = ArrayPool<byte>.Shared.Rent(requiredLength);
		try
		{
			if (!Nv12FrameConverter.TryConvertToBgra(
				nv12PreviewBytes,
				frame.Nv12PreviewStride,
				frame.Width,
				frame.Height,
				outputWidth,
				outputHeight,
				bgraStride,
				bgra.AsSpan(0, requiredLength)))
			{
				return false;
			}
			BitmapSource bitmapSource = BitmapSource.Create(outputWidth, outputHeight, 96.0, 96.0, PixelFormats.Bgra32, null, bgra, bgraStride);
			bitmapSource.Freeze();
			bitmap = bitmapSource;
			return true;
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(bgra);
		}
	}

	private bool IsDirectX12PreviewEnabled()
	{
		return _isDirectX12PreviewEnabled;
	}

	private static double GetDirectX12PreviewRenderFramesPerSecond(CameraVideoMode mode, bool nativeTexturePath)
	{
		return 0.0;
	}

	private string FormatPreviewRenderLimit()
	{
		if (!(_directX12PreviewMaxRenderFramesPerSecond > 0.0))
		{
			return "source fps";
		}
		return $"{_directX12PreviewMaxRenderFramesPerSecond:0.#} fps";
	}

	private void UpdateDirectX12PreviewMode()
	{
		if (IsDirectX12PreviewEnabled())
		{
			if (_directX12NativeCamera != null)
			{
				DirectX12PreviewLayer.Visibility = Visibility.Visible;
			}
			else if (TryEnsureDirectX12PreviewHost())
			{
				DirectX12PreviewLayer.Visibility = Visibility.Visible;
			}
		}
		else
		{
			DisposeDirectX12NativeCamera();
			DirectX12PreviewLayer.Visibility = Visibility.Collapsed;
			DisposeDirectX12PreviewHost();
		}
	}

	private bool TryEnsureDirectX12PreviewHost()
	{
		lock (_directX12PreviewLock)
		{
			if (_directX12PreviewHost != null)
			{
				_directX12PreviewHost.LimitRenderRate(_directX12PreviewMaxRenderFramesPerSecond);
				return true;
			}
			try
			{
				Direct3D12PreviewHost direct3D12PreviewHost = WebcamModule.CreateDirect3D12PreviewHost();
				direct3D12PreviewHost.LimitRenderRate(_directX12PreviewMaxRenderFramesPerSecond);
				direct3D12PreviewHost.HorizontalAlignment = HorizontalAlignment.Stretch;
				direct3D12PreviewHost.VerticalAlignment = VerticalAlignment.Stretch;
				direct3D12PreviewHost.StatusChanged += DirectX12PreviewStatusChanged;
				direct3D12PreviewHost.DiagnosticsChanged += DirectX12PreviewDiagnosticsChanged;
				DirectX12PreviewLayer.Children.Clear();
				DirectX12PreviewLayer.Children.Add(direct3D12PreviewHost);
				_directX12PreviewHost = direct3D12PreviewHost;
				ApplyDirectX12PreviewPresentationState();
				Interlocked.Exchange(ref _directX12FrameNumber, 0L);
				return true;
			}
			catch (Exception ex)
			{
				DirectX12PreviewLayer.Children.Clear();
				DirectX12PreviewLayer.Visibility = Visibility.Collapsed;
				SetStatus("DX12 preview unavailable: " + ex.Message);
				base.Dispatcher.InvokeAsync(delegate
				{
					_isDirectX12PreviewEnabled = false;
					DirectX12PreviewMenuItem.IsChecked = false;
				}, DispatcherPriority.Background);
				return false;
			}
		}
	}

	private void DisposeDirectX12PreviewHost()
	{
		Direct3D12PreviewHost? directX12PreviewHost;
		lock (_directX12PreviewLock)
		{
			directX12PreviewHost = _directX12PreviewHost;
			_directX12PreviewHost = null;
			DirectX12PreviewLayer.Children.Clear();
		}
		if (directX12PreviewHost != null)
		{
			directX12PreviewHost.StatusChanged -= DirectX12PreviewStatusChanged;
			directX12PreviewHost.DiagnosticsChanged -= DirectX12PreviewDiagnosticsChanged;
			directX12PreviewHost.Dispose();
		}
	}

	private void DisposeDirectX12NativeCamera(
		bool disposeInBackground = false)
	{
		Dx12Camera? directX12NativeCamera = _directX12NativeCamera;
		if (directX12NativeCamera != null)
		{
			_directX12NativeCamera = null;
			directX12NativeCamera.FrameAvailable -= DirectX12NativeFrameAvailable;
			directX12NativeCamera.TextureFrameAvailable -= DirectX12NativeTextureFrameAvailable;
			directX12NativeCamera.DiagnosticsChanged -= DirectX12NativeDiagnosticsChanged;
			directX12NativeCamera.StatusChanged -= DirectX12NativeStatusChanged;
			if (disposeInBackground)
			{
				try
				{
					directX12NativeCamera.DetachPreviewHost();
				}
				catch
				{
				}
				Task disposal = Task.Run(() =>
				{
					try
					{
						directX12NativeCamera.Dispose();
					}
					catch
					{
					}
				});
				Volatile.Write(
					ref _pendingNativeCameraDisposal,
					disposal);
			}
			else
			{
				directX12NativeCamera.Dispose();
				Volatile.Write(
					ref _pendingNativeCameraDisposal,
					Task.CompletedTask);
			}
			DirectX12PreviewLayer.Children.Clear();
			_directX12PreviewMaxRenderFramesPerSecond = 0.0;
		}
	}

	private void DirectX12PreviewStatusChanged(object? sender, string status)
	{
		base.Dispatcher.InvokeAsync(delegate
		{
			SetStatus(status);
		}, DispatcherPriority.Background);
	}

	private void DirectX12PreviewDiagnosticsChanged(object? sender, Direct3D12PreviewDiagnostics diagnostics)
	{
		if (diagnostics.RenderedFrames > 0L)
		{
			RecordCameraPresentationHeartbeat();
		}
		base.Dispatcher.InvokeAsync(delegate
		{
			SetStatus(FormatDirectX12DiagnosticsStatus(diagnostics));
		}, DispatcherPriority.Background);
	}

	private string FormatDirectX12DiagnosticsStatus(Direct3D12PreviewDiagnostics diagnostics)
	{
		string text = diagnostics.FormatStatusLine();
		Dx12Camera? camera = _directX12NativeCamera;
		if (camera != null)
		{
			long timestamp = Stopwatch.GetTimestamp();
			long sourceFrames = camera.FramesRead;
			long analysisFrames = Interlocked.Read(ref _directX12AnalysisCompletedFrames);
			long previousTimestamp = Interlocked.Exchange(ref _lastPipelineDiagnosticsTimestamp, timestamp);
			long previousSourceFrames = Interlocked.Exchange(ref _lastPipelineDiagnosticsSourceFrames, sourceFrames);
			long previousAnalysisFrames = Interlocked.Exchange(ref _lastPipelineDiagnosticsAnalysisFrames, analysisFrames);
			if (previousTimestamp != 0L)
			{
				double elapsedSeconds = Math.Max(0.001, Stopwatch.GetElapsedTime(previousTimestamp, timestamp).TotalSeconds);
				_measuredCameraIngestionFramesPerSecond = Math.Max(0L, sourceFrames - previousSourceFrames) / elapsedSeconds;
				_measuredAnalysisFramesPerSecond = Math.Max(0L, analysisFrames - previousAnalysisFrames) / elapsedSeconds;
			}
			long sourceTimestamp = camera.LastSourceFrameTimestamp;
			string sourceAge = sourceTimestamp == 0L
				? "unknown"
				: $"{Stopwatch.GetElapsedTime(sourceTimestamp, timestamp).TotalMilliseconds:0} ms";
			long awarenessTimestamp = Volatile.Read(ref _lastFaceFeatureLockTimestamp);
			string awarenessAge = awarenessTimestamp == 0L || IsLiveAwarenessFrameExpired(awarenessTimestamp)
				? "unknown"
				: $"{Stopwatch.GetElapsedTime(awarenessTimestamp, timestamp).TotalMilliseconds:0} ms";
			text += $"; measured capture {_measuredCameraIngestionFramesPerSecond:0.#} fps"
				+ $"; measured analysis {_measuredAnalysisFramesPerSecond:0.#} fps"
				+ $"; newest source {sourceAge}"
				+ $"; awareness {awarenessAge}"
				+ $"; pre-display busy drops {camera.FramesDroppedWhileProcessingBusy}";
		}
		if (_directX12PreviewMaxRenderFramesPerSecond <= 0.0)
		{
			return text;
		}
		return text + "; preview cap " + FormatPreviewRenderLimit();
	}

	private void ProcessPreviewFrame(BitmapSource frame)
	{
		try
		{
			if (!_isClosing)
			{
				_latestFrame = frame;
				if (!_showLiveWireframePreview && !IsDirectX12PreviewSurfaceActive())
				{
					SetPreviewState("Camera active", frame);
				}
				else if (!string.Equals(PreviewStateText.Text, "Camera active", StringComparison.Ordinal))
				{
					PreviewStateText.Text = "Camera active";
				}
				if (!IsDirectX12PreviewSurfaceActive())
				{
					RecordCameraPresentationHeartbeat();
				}
				ProcessFrame(frame);
				if (FaceAutoFollowCheckBox.IsChecked != true || !IsSelectedFaceBoxSystemAvailable())
				{
					UpdateFaceCueGuideOverlay(frame);
				}
			}
		}
		finally
		{
			Interlocked.Exchange(ref _uiFrameInFlight, 0);
		}
	}

	private void ResetPipelineDiagnostics()
	{
		Volatile.Write(ref _lastPipelineDiagnosticsTimestamp, 0L);
		Interlocked.Exchange(
			ref _lastPipelineDiagnosticsSourceFrames,
			_directX12NativeCamera?.FramesRead ?? 0L);
		Interlocked.Exchange(
			ref _lastPipelineDiagnosticsAnalysisFrames,
			Interlocked.Read(ref _directX12AnalysisCompletedFrames));
		_measuredCameraIngestionFramesPerSecond = 0.0;
		_measuredAnalysisFramesPerSecond = 0.0;
	}

	private void PreviewStatusChanged(object? sender, string status)
	{
		base.Dispatcher.InvokeAsync(delegate
		{
			SetStatus(status);
		});
	}

	private void SetPreviewState(string status, ImageSource? frame)
	{
		PreviewStateText.Text = status;
		if (_showLiveWireframePreview)
		{
			PreviewImage.Source = null;
			PreviewImage.Visibility = Visibility.Collapsed;
			DirectX12PreviewLayer.Visibility = Visibility.Collapsed;
			PreviewPlaceholder.Visibility = Visibility.Collapsed;
			FaceCueGuideCanvas.Children.Clear();
			FaceCueGuideCanvas.Visibility = Visibility.Collapsed;
			LiveWireframeCanvas.Visibility = Visibility.Visible;
			DrawLiveWireframePreview();
			return;
		}
		LiveWireframeCanvas.Visibility = Visibility.Collapsed;
		FaceCueGuideCanvas.Visibility = Visibility.Visible;
		bool flag = IsDirectX12PreviewEnabled();
		if (frame == null)
		{
			PreviewImage.Source = null;
			PreviewImage.Visibility = Visibility.Collapsed;
			DirectX12PreviewLayer.Visibility = ((!IsDirectX12PreviewSurfaceActive()) ? Visibility.Collapsed : Visibility.Visible);
			PreviewPlaceholder.Visibility = ((DirectX12PreviewLayer.Visibility == Visibility.Visible) ? Visibility.Collapsed : Visibility.Visible);
			UpdateFaceCueGuideOverlay(null);
		}
		else if (flag && _directX12NativeCamera != null)
		{
			DirectX12PreviewLayer.Visibility = Visibility.Visible;
			PreviewImage.Source = null;
			PreviewImage.Visibility = Visibility.Collapsed;
			PreviewPlaceholder.Visibility = Visibility.Collapsed;
			UpdateFaceCueGuideOverlay(frame as BitmapSource);
		}
		else if (flag && TryEnsureDirectX12PreviewHost())
		{
			DirectX12PreviewLayer.Visibility = Visibility.Visible;
			PreviewImage.Source = null;
			PreviewImage.Visibility = Visibility.Collapsed;
			PreviewPlaceholder.Visibility = Visibility.Collapsed;
			UpdateFaceCueGuideOverlay(frame as BitmapSource);
		}
		else
		{
			DirectX12PreviewLayer.Visibility = Visibility.Collapsed;
			PreviewImage.Source = frame;
			PreviewImage.Visibility = Visibility.Visible;
			PreviewPlaceholder.Visibility = Visibility.Collapsed;
			UpdateFaceCueGuideOverlay(frame as BitmapSource);
		}
	}

	private bool IsDirectX12PreviewSurfaceActive()
	{
		if (!IsDirectX12PreviewEnabled())
		{
			return false;
		}
		if (_directX12NativeCamera != null)
		{
			return true;
		}
		return GetDirectX12PreviewHost() != null;
	}

	private Direct3D12PreviewHost? GetDirectX12PreviewHost()
	{
		lock (_directX12PreviewLock)
		{
			return _directX12PreviewHost;
		}
	}

	private void PreviewHostSizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (_showLiveWireframePreview)
		{
			DrawLiveWireframePreview();
		}
		else
		{
			UpdateFaceCueGuideOverlay(_latestFrame);
		}
	}

	private void SettingChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!base.IsLoaded)
		{
			return;
		}
		if (!_isSnappingSlider && sender is Slider slider)
		{
			SnapSliderToDefault(slider);
			if (IsFaceFieldSlider(slider))
			{
				_currentFaceFeatureDetection = FaceFeatureDetection.None;
				ResetLandmarkTracking();
				_activeFaceCueLayout = null;
				Volatile.Write(ref _lastFaceFeatureLockTimestamp, 0L);
				UpdateFaceCueGuideOverlay(_latestFrame);
			}
		}
		UpdateSettingLabels();
	}

	private static bool IsFaceFieldSlider(Slider slider)
	{
		switch (slider.Name)
		{
		case "FaceFieldXSlider":
		case "FaceFieldYSlider":
		case "FaceFieldSizeSlider":
			return true;
		default:
			return false;
		}
	}

	private void SnapSliderToDefault(Slider slider)
	{
		var (num, num2) = slider.Name switch
		{
			"FaceFieldXSlider" => (50.0, 2.0),
			"FaceFieldYSlider" => (48.0, 2.0),
			"FaceFieldSizeSlider" => (60.0, 2.0),
			_ => (double.NaN, 0.0),
		};
		if (!double.IsNaN(num) && !(Math.Abs(slider.Value - num) < double.Epsilon) && !(Math.Abs(slider.Value - num) > num2))
		{
			_isSnappingSlider = true;
			slider.Value = num;
			_isSnappingSlider = false;
		}
	}

	private void FaceTrackingFieldChanged(object sender, RoutedEventArgs e)
	{
		Volatile.Write(ref _faceTrackingEnabled, FaceAutoFollowCheckBox.IsChecked == true ? 1 : 0);
		if (base.IsLoaded)
		{
			_currentFaceFeatureDetection = FaceFeatureDetection.None;
			ResetLandmarkTracking();
			_activeFaceCueLayout = null;
			Volatile.Write(ref _lastFaceFeatureLockTimestamp, 0L);
			Volatile.Write(ref _lastFaceAutoFollowTimestamp, 0L);
			MonitorStatusText.Text = "Face tracking field reset. Waiting for a fresh landmark lock.";
		}
	}

}
