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
	private async void LoginLogoutClicked(object sender, RoutedEventArgs e)
	{
		if (_avatarModelInitializationPending)
		{
			SetStatus("Please wait for the stored avatar model calculation to finish.");
			return;
		}
		if (IsAvatarUserLoggedIn)
		{
			LogOutAvatarUser(announce: true);
			return;
		}
		try
		{
			AvatarProfile? avatarProfile = ResolveAvatarLoginSelection(PromptForAvatarLogin(_avatarProfileRegistry));
			if (avatarProfile == null)
			{
				SetStatus("Avatar user login canceled. Avatar capture remains stopped.");
				return;
			}
			LogInAvatarProfile(avatarProfile, loadModel: true, announce: true);
			await InitializeAvatarModelAfterLoginAsync(showPopup: true);
		}
		catch (Exception ex)
		{
			SetStatus("Could not log in avatar user: " + ex.Message);
		}
	}

	private void AvatarLearningToggleClicked(object sender, RoutedEventArgs e)
	{
		if (!IsAvatarUserLoggedIn)
		{
			_avatarLearningRequested = false;
			UpdateAvatarLearningStatusUi();
			SetStatus("Log in an avatar user from the File menu before starting avatar capture.");
			return;
		}
		if (!_avatarModelReadyForCapture || _avatarModelInitializationPending)
		{
			_avatarLearningRequested = false;
			UpdateAvatarLearningStatusUi();
			SetStatus("Please wait for the stored avatar model to finish calculating before starting capture.");
			return;
		}
		if (_avatarLearningRequested)
		{
			_avatarLearningRequested = false;
			UpdateAvatarLearningStatusUi();
			SetStatus("Avatar capture stopped.");
			return;
		}
		AvatarIdentityAuthorization authorization =
			_personIdentityMemory.AuthorizeAvatarCapture(
				CurrentAvatarProfileId,
				CurrentAvatarProfileDisplayName);
		if (!authorization.Allowed)
		{
			_avatarLearningRequested = false;
			UpdateAvatarLearningStatusUi();
			SetStatus(authorization.Status);
			return;
		}
		_avatarLearningRequested = true;
		UpdateAvatarLearningStatusUi();
		SetStatus(
			authorization.Status + " Avatar capture started. " +
			"MediaPipe visible-evidence geometry accepts one frame only " +
			"when its processing slot is empty; " +
			$"{GetFaceBoxSystemDisplayName()} preview remains independent " +
			"and full speed.");
	}

	private void UpdateAvatarLearningStatusUi()
	{
		if (base.IsLoaded)
		{
			_lastAvatarStatusUiRefreshTimestamp = Stopwatch.GetTimestamp();
			_currentAvatarCaptureQuality = AnalyzeAvatarCaptureQuality();
			UpdateAvatarSessionUi();
			AvatarLearningState avatarLearningState = GetAvatarLearningState();
			ApplyStartStopButtonState(AvatarLearningToggleButton, _avatarLearningRequested, "Start Avatar Capture", "Stop Avatar Capture", "Starts " + ActiveAvatarReconstructionName + " avatar capture.", "Stops " + ActiveAvatarReconstructionName + " avatar capture.");
			SetTextIfChanged(AvatarLearningStateText, avatarLearningState.Title);
			SetTextIfChanged(AvatarLearningStatusText, FormatAvatarCaptureStatus());
			SetIfChanged(AvatarLearningIndicator, Panel.BackgroundProperty, avatarLearningState.Accent);
			MediaPipeNormalizedFaceModel model = _currentMediaPipeGeometryModel;
			SetTextIfChanged(AvatarProcessorValueText, $"{GetFaceBoxSystemDisplayName()} {_mediaPipeExecutionBackend.ToDisplayName()}");
			SetTextIfChanged(AvatarAcceptedFramesValueText, $"{model.AcceptedFrameCount:n0}");
			SetTextIfChanged(AvatarConstrainedVerticesValueText, $"{model.ConfidentVertexPercent:0.#}%");
			SetTextIfChanged(AvatarObservationsValueText, $"{model.DirectLandmarkObservationCount:n0}");
			SetTextIfChanged(AvatarDiscardedPredictionsValueText, $"{model.HiddenLandmarkRejectionCount:n0}");
			SetTextIfChanged(AvatarSilhouetteSlicesValueText, $"{model.VisualHullSlices.Count:n0}");
			SetTextIfChanged(AvatarGeometryUpdateValueText, _lastMediaPipeGeometryProcessingDuration is TimeSpan duration
				? $"{duration.TotalMilliseconds:0.#} ms"
				: "--");
			UpdateAvatarCaptureGuidanceUi();
		}
	}

	private string FormatAvatarCaptureStatus()
	{
		if (!IsAvatarUserLoggedIn)
		{
			return "Login required";
		}
		if (!_avatarLearningRequested)
		{
			return "Stopped";
		}
		if (!_isCameraEnabled || _latestFrame == null)
		{
			return "Waiting for camera";
		}
		if (!_currentFaceLandmarkFrame.HasFace || !_currentFaceLandmarkMetrics.HasFace)
		{
			return "Waiting for face lock";
		}
		return _currentAvatarCaptureQuality.CanCollectMeasurements
			? "Active"
			: "Waiting";
	}

	private void UpdateAvatarLearningStatusUiIfDue()
	{
		long timestamp = Stopwatch.GetTimestamp();
		if (timestamp - Volatile.Read(ref _lastAvatarStatusUiRefreshTimestamp) < AvatarStatusUiRefreshIntervalTicks)
		{
			return;
		}

		UpdateAvatarLearningStatusUi();
	}

	private static void ApplyStartStopButtonState(Button button, bool isActive, string startText, string stopText, string startToolTip, string stopToolTip)
	{
		SetContentIfChanged(button, isActive ? stopText : startText);
		SetIfChanged(button, Control.BackgroundProperty, isActive ? StopActionButtonBackground : StartActionButtonBackground);
		SetIfChanged(button, Control.BorderBrushProperty, isActive ? StopActionButtonBorder : StartActionButtonBorder);
		SetIfChanged(button, Control.ForegroundProperty, Brushes.White);
		SetIfChanged(button, FrameworkElement.ToolTipProperty, isActive ? stopToolTip : startToolTip);
	}

	private AvatarLearningState GetAvatarLearningState()
	{
		if (!IsAvatarUserLoggedIn)
		{
			return new AvatarLearningState("Avatar capture stopped", "Not capturing: use File > Login to identify the person in front of the camera.", AvatarStoppedBrush);
		}
		if (!_avatarLearningRequested)
		{
			return new AvatarLearningState("Avatar capture stopped", $"Not capturing: click Start Avatar Capture when {CurrentAvatarProfileDisplayName} is present and you want MediaPipe geometry measurements.", AvatarStoppedBrush);
		}
		if (!_isCameraEnabled || _latestFrame == null)
		{
			return new AvatarLearningState("Avatar capture waiting", "Not capturing yet: turn the camera on and wait for the face tracker to lock.", AvatarWaitingBrush);
		}
		if (!_currentFaceLandmarkFrame.HasFace || !_currentFaceLandmarkMetrics.HasFace)
		{
			return new AvatarLearningState("Avatar capture waiting", "Not capturing yet: keep your full face visible until the eye and mouth overlay locks on.", AvatarWaitingBrush);
		}
		if (_currentAvatarCaptureQuality.CanCollectMeasurements)
		{
			MediaPipeNormalizedFaceModel model = _currentMediaPipeGeometryModel;
			string detail = $"MediaPipe visible-evidence geometry has {model.AcceptedFrameCount:n0} frames; {model.ConfidentVertexPercent:0.#}% directly constrained; {model.HiddenLandmarkRejectionCount:n0} hidden predictions discarded. {_mediaPipeExecutionBackend.ToDisplayName()} tracking keeps eye, jaw, and brow measurements live.";
			return new AvatarLearningState(IsAvatarReconstructionReady ? "Capturing 3D avatar data" : "Avatar capture waiting", detail, IsAvatarReconstructionReady ? AvatarActiveBrush : AvatarWaitingBrush);
		}
		string text2 = _currentAvatarCaptureQuality.Suggestions.Count > 0 ? _currentAvatarCaptureQuality.Suggestions[0] : _currentAvatarCaptureQuality.PrimaryReason ?? "Improve face lock, eye visibility, mouth visibility, lighting, or camera mode.";
		return new AvatarLearningState("Avatar capture waiting", "Not capturing: " + _currentAvatarCaptureQuality.PrimaryReason + ". Fix: " + text2, AvatarWaitingBrush);
	}

	private void UpdateAvatarCaptureGuidanceUi()
	{
		if (base.IsLoaded)
		{
			AvatarCaptureGuidanceState avatarCaptureGuidanceState = GetAvatarCaptureGuidanceState();
			SetTextIfChanged(AvatarCaptureGuidanceTitleText, avatarCaptureGuidanceState.Title);
			SetTextIfChanged(AvatarCaptureGuidanceDetailText, avatarCaptureGuidanceState.Detail);
			SetIfChanged(AvatarCaptureGuidanceTitleText, TextBlock.ForegroundProperty, BrushForAvatarCaptureGuidanceSeverity(avatarCaptureGuidanceState.Severity));
		}
	}

	private AvatarCaptureGuidanceState GetAvatarCaptureGuidanceState()
	{
		return AvatarCaptureGuidanceAdvisor.Create(new AvatarCaptureGuidanceInput
		{
			UserLoggedIn = IsAvatarUserLoggedIn,
			AvatarLearningRequested = _avatarLearningRequested,
			CameraActive = (_isCameraEnabled && _latestFrame != null),
			FaceLocked = (_currentFaceLandmarkFrame.HasFace && _currentFaceLandmarkMetrics.HasFace),
			CaptureQuality = _currentAvatarCaptureQuality
		});
	}

	private static Brush BrushForAvatarCaptureGuidanceSeverity(string severity)
	{
		return severity switch
		{
			"good" => GuidanceGoodBrush,
			"warning" => GuidanceWarningBrush,
			"blocked" => GuidanceBlockedBrush,
			_ => GuidanceIdleBrush,
		};
	}

	private async void OpenMediaPipeGeometryClicked(object sender, RoutedEventArgs e)
	{
		try
		{
			if (!IsAvatarUserLoggedIn)
			{
				SetStatus("Log in an avatar user before opening measured face geometry.");
				return;
			}
			_currentMediaPipeGeometryModel = await _mediaPipeGeometryPipeline.FlushAsync();
			MediaPipeNormalizedFaceStore.WriteViewer(GetAvatarDataFolder(), _currentMediaPipeGeometryModel);
			string viewerPath = MediaPipeNormalizedFaceStore.GetViewerPath(GetAvatarDataFolder());
			OpenLocalFile(viewerPath);
			SetStatus("Opened measured MediaPipe face geometry: " + viewerPath);
		}
		catch (Exception ex)
		{
			SetStatus("Could not open measured MediaPipe face geometry: " + ex.Message);
		}
	}

	private async void BuildDenseWarpClicked(object sender, RoutedEventArgs e)
	{
		if (!IsAvatarUserLoggedIn)
		{
			SetStatus("Log in an avatar user before building a dense MediaPipe warp.");
			MessageBox.Show(this, "Log in an avatar user before building a dense MediaPipe warp.", "Dense MediaPipe Warp", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		string profileFolder = GetAvatarDataFolder();
		BuildDenseWarpButton.IsEnabled = false;
		BuildDenseWarpButton.Content = "Building Dense Warp...";
		SetStatus("Checking the saved MediaPipe model, then building its dense warp...");
		try
		{
			_currentMediaPipeGeometryModel = await _mediaPipeGeometryPipeline.FlushAsync();
			MediaPipeNormalizedFaceModel mediaPipeModel = _currentMediaPipeGeometryModel;
			if (!mediaPipeModel.HasGeometry)
			{
				SetStatus("No saved MediaPipe face model exists for this avatar.");
				MessageBox.Show(this, "No saved MediaPipe face model exists for this avatar.", "Dense MediaPipe Warp", MessageBoxButton.OK, MessageBoxImage.Asterisk);
				return;
			}
			string subjectId = CurrentAvatarProfileId;
			string displayName = CurrentAvatarProfileDisplayName;
			(ThreeDdfaOnnxSidecarResponse, DenseFaceWarpResult, DenseFaceWarpSaveResult) tuple = await Task.Run(delegate
			{
				using ThreeDdfaOnnxReconstructionClient threeDdfaOnnxReconstructionClient = new ThreeDdfaOnnxReconstructionClient(_threeDdfaOnnxEnvironment);
				ThreeDdfaOnnxSidecarResponse threeDdfaOnnxSidecarResponse = threeDdfaOnnxReconstructionClient.Reconstruct(EmptyBgraBitmap, DateTime.UtcNow, null, ThreeDdfaOnnxRequestMode.CanonicalModel, 1);
				DenseFaceWarpResult denseFaceWarpResult = EvidenceWeightedDenseFaceWarper.Warp(ThreeDdfaMediaPipeWarpInputFactory.Create(threeDdfaOnnxSidecarResponse, mediaPipeModel, subjectId, displayName, DateTime.UtcNow));
				DenseFaceWarpSaveResult item = DenseFaceWarpStore.Write(profileFolder, denseFaceWarpResult);
				return (Response: threeDdfaOnnxSidecarResponse, Warp: denseFaceWarpResult, Saved: item);
			});
			OpenLocalFile(tuple.Item3.ViewerPath);
			SetStatus($"Dense MediaPipe warp complete: {tuple.Item2.SourceVertexCount:n0} vertices, {tuple.Item2.AppliedControlPointCount} trusted controls, anchor RMS {tuple.Item2.SourceAnchorRms:0.0000} -> {tuple.Item2.WarpedAnchorRms:0.0000}, movement p95 {tuple.Item2.Percentile95AppliedDisplacement:0.0000}, safety clamp {tuple.Item2.SafetyClampVertexPercent:0.00}%.");
		}
		catch (Exception ex)
		{
			string text = "Could not build the dense MediaPipe warp: " + ex.Message;
			SetStatus(text);
			MessageBox.Show(this, text, "Dense MediaPipe Warp", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
		finally
		{
			BuildDenseWarpButton.Content = "Build Dense MediaPipe Warp";
			BuildDenseWarpButton.IsEnabled = true;
		}
	}

	private async void DeleteAvatarDataClicked(object sender, RoutedEventArgs e)
	{
		if (MessageBox.Show(this, "Are you sure you want to permanently delete all avatar data for " + CurrentAvatarProfileDisplayName + "?\n\nThis deletes measured MediaPipe geometry, dense warp proofs, and generated review files. This cannot be undone.", "Delete Avatar Data?", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
		{
			return;
		}
		try
		{
			string folder = GetAvatarDataFolder();
			_avatarLearningRequested = false;
			DenseFaceWarpStore.Delete(folder);
			_currentMediaPipeGeometryModel = await _mediaPipeGeometryPipeline.ResetProfileAsync();
			DeleteDerivedAvatarFiles(folder);
			Directory.CreateDirectory(folder);
			_currentAvatarCaptureQuality = AvatarCaptureQualityAssessment.Waiting;
			_avatarModelReadyForCapture = IsAvatarUserLoggedIn;
			UpdateAvatarLearningStatusUi();
			SetStatus("Avatar data deleted. Start Avatar Capture when you are ready to begin again.");
		}
		catch (Exception ex)
		{
			SetStatus("Could not delete Avatar data: " + ex.Message);
		}
	}

	private static void DeleteDerivedAvatarFiles(string folder)
	{
		string[] array = new string[12]
		{
			"avatar_model.json", "avatar_model_progress.html", "avatar_model_history.jsonl", "avatar_model_history_latest.json", "avatar_model_history_recent.json", "avatar_model_regression.html", "avatar_system.json", "avatar_system.html", "avatar_model_observations.json", "avatar_model_observations.json.gz",
			"last_5_3ddfa_reconstructions.html", "last_5_3ddfa_reconstructions.json"
		};
		foreach (string path in array)
		{
			string path2 = System.IO.Path.Combine(folder, path);
			if (File.Exists(path2))
			{
				File.Delete(path2);
			}
		}
	}

	private void ProcessFrame(BitmapSource bitmap)
	{
		UpdateFaceTracking(bitmap);
	}

	private void UpdateFaceTracking(BitmapSource bitmap)
	{
		try
		{
			FaceCueGuideLayout manualFaceCueLayout = GetManualFaceCueLayout();
			TryStartFaceFeatureDetection(bitmap);
			bool faceTrackingEnabled = Volatile.Read(ref _faceTrackingEnabled) != 0;
			if (faceTrackingEnabled && HasUsableFaceFeatureLock())
			{
				FaceCueGuideLayout faceCueGuideLayout = _currentFaceFeatureDetection.ToGuideLayout(manualFaceCueLayout);
				FaceCueGuideLayout faceCueGuideLayout2 = _activeFaceCueLayout ?? faceCueGuideLayout;
				_activeFaceCueLayout = new FaceCueGuideLayout(faceCueGuideLayout2.CenterXPercent + (faceCueGuideLayout.CenterXPercent - faceCueGuideLayout2.CenterXPercent) * 0.45, faceCueGuideLayout2.CenterYPercent + (faceCueGuideLayout.CenterYPercent - faceCueGuideLayout2.CenterYPercent) * 0.45, faceCueGuideLayout2.HeightPercent + (faceCueGuideLayout.HeightPercent - faceCueGuideLayout2.HeightPercent) * 0.3);
				manualFaceCueLayout = _activeFaceCueLayout;
			}
			else if (faceTrackingEnabled)
			{
				FaceCueGuideLayout faceCueGuideLayout3 = _activeFaceCueLayout ?? manualFaceCueLayout;
				long timestamp = Stopwatch.GetTimestamp();
				long previousTimestamp = Volatile.Read(ref _lastFaceAutoFollowTimestamp);
				if (previousTimestamp == 0L || Stopwatch.GetElapsedTime(previousTimestamp, timestamp).TotalMilliseconds >= 500.0)
				{
					_activeFaceCueLayout = FaceCueAutoLayoutEstimator.Estimate(bitmap, faceCueGuideLayout3);
					Volatile.Write(ref _lastFaceAutoFollowTimestamp, timestamp);
				}
				manualFaceCueLayout = _activeFaceCueLayout ?? faceCueGuideLayout3;
			}
		}
		catch
		{
			MonitorStatusText.Text = "Face tracking is resyncing with the latest camera frame.";
		}
	}

	private void TryStartFaceFeatureDetection(BitmapSource bitmap)
	{
		if (_isClosing
			|| Volatile.Read(ref _faceTrackingEnabled) == 0
			|| !IsSelectedFaceBoxSystemAvailable()
			|| Volatile.Read(ref _faceFeatureDetectionInFlight) != 0)
		{
			return;
		}

		long timestamp = Stopwatch.GetTimestamp();
		if (Interlocked.CompareExchange(ref _faceFeatureDetectionInFlight, 1, 0) == 0)
		{
			FaceBoxSystem faceBoxSystem = _selectedFaceBoxSystem;
			int faceBoxSystemGeneration = _faceBoxSystemGeneration;
			int faceAnalysisGeneration = Volatile.Read(ref _faceAnalysisGeneration);
			DateTime capturedAtUtc = DateTime.UtcNow;
			_ = Task.Run(() => ProcessFaceFeatureDetectionFrame(bitmap, timestamp, capturedAtUtc, faceBoxSystem, faceBoxSystemGeneration, faceAnalysisGeneration));
		}
	}

	private void ProcessFaceFeatureDetectionFrame(BitmapSource bitmap, long capturedAtTimestamp, DateTime capturedAtUtc, FaceBoxSystem faceBoxSystem, int faceBoxSystemGeneration, int faceAnalysisGeneration)
	{
		try
		{
			if (!_isClosing && faceBoxSystemGeneration == _faceBoxSystemGeneration && faceBoxSystem == _selectedFaceBoxSystem)
			{
				if (IsLiveAwarenessFrameExpired(capturedAtTimestamp))
				{
					ExpireLiveAwarenessState(capturedAtTimestamp);
					return;
				}
				FaceBoxTrackingFrameResult trackingResult = DetectFaceBox(
					bitmap,
					capturedAtTimestamp,
					(capturedAtUtc == DateTime.MinValue) ? DateTime.UtcNow : capturedAtUtc,
					faceBoxSystem,
					faceBoxSystemGeneration);
				ProcessFaceTrackingFrameResult(
					trackingResult,
					faceAnalysisGeneration);
			}
		}
		catch (Exception ex)
		{
			ReportRecoverableVisionError("Landmark tracker skipped one frame and recovered: " + ex.Message);
		}
		finally
		{
			Interlocked.Exchange(ref _faceFeatureDetectionInFlight, 0);
		}
	}

	private void ProcessFaceTrackingFrameResult(
		FaceBoxTrackingFrameResult trackingResult,
		int faceAnalysisGeneration)
	{
		if (_isClosing
			|| trackingResult.FaceBoxSystemGeneration !=
				_faceBoxSystemGeneration
			|| trackingResult.FaceBoxSystem != _selectedFaceBoxSystem
			|| faceAnalysisGeneration !=
				Volatile.Read(ref _faceAnalysisGeneration))
		{
			return;
		}
		if (IsLiveAwarenessFrameExpired(
			trackingResult.CapturedAtTimestamp))
		{
			ExpireLiveAwarenessState(
				trackingResult.CapturedAtTimestamp);
			return;
		}

		FaceAnalysisResult? analysisResult =
			AnalyzeFaceTrackingResult(trackingResult);
		if (faceAnalysisGeneration !=
			Volatile.Read(ref _faceAnalysisGeneration))
		{
			return;
		}
		if (IsLiveAwarenessFrameExpired(
			trackingResult.CapturedAtTimestamp))
		{
			ExpireLiveAwarenessState(
				trackingResult.CapturedAtTimestamp);
			return;
		}

		PreviewTrackingOverlay? nativeOverlay = null;
		if (analysisResult != null
			&& trackingResult.TrackingResult.FeatureDetection.HasFace
			&& !_showLiveWireframePreview
			&& IsDirectX12PreviewSurfaceActive())
		{
			nativeOverlay = CreateNativePreviewTrackingOverlay(
				trackingResult.TrackingResult.FeatureDetection,
				analysisResult.ReconstructedLandmarkFrame,
				_showFaceMeshOverlay) with
			{
				SourceTimestamp =
					trackingResult.CapturedAtTimestamp,
				MaximumAge = MaximumLiveAwarenessFrameAge
			};
		}
		ApplyFaceFeatureDetectionResult(
			trackingResult,
			analysisResult,
			nativeOverlay,
			faceAnalysisGeneration);
	}

	private FaceAnalysisResult? AnalyzeFaceTrackingResult(FaceBoxTrackingFrameResult trackingResult)
	{
		FaceLandmarkTrackingResult faceTracking = trackingResult.TrackingResult;
		FaceFeatureDetection featureDetection = faceTracking.FeatureDetection;
		if (!featureDetection.HasFace)
		{
			return null;
		}

		FaceLandmarkFrame observedFrame = faceTracking.LandmarkFrame.HasFace
			? faceTracking.LandmarkFrame
			: featureDetection.ToLandmarkFrame(trackingResult.CapturedAtUtc);
		FaceAnalysisResult analysis;
		lock (_faceAnalysisStateLock)
		{
			FaceLandmarkFrame reconstructedFrame = trackingResult.FaceBoxSystem == FaceBoxSystem.MediaPipe && observedFrame.HasDenseMesh
				? observedFrame
				: _faceLandmarkReconstructor.Update(observedFrame);
			FaceLandmarkMetrics metrics = _faceLandmarkMetricCalculator.Update(reconstructedFrame);
			FaceLockStabilityAnalysis stability = _faceLockStabilityAnalyzer.Update(featureDetection, reconstructedFrame, metrics);
			analysis = new FaceAnalysisResult(observedFrame, reconstructedFrame, metrics, stability);
		}

		return analysis;
	}

	private void ReportRecoverableVisionError(string status)
	{
		long timestamp = Stopwatch.GetTimestamp();
		long previousTimestamp = Volatile.Read(ref _lastRecoverableVisionErrorStatusTimestamp);
		if ((previousTimestamp == 0L || Stopwatch.GetElapsedTime(previousTimestamp, timestamp) >= RecoverableVisionErrorStatusInterval)
			&& Interlocked.CompareExchange(ref _lastRecoverableVisionErrorStatusTimestamp, timestamp, previousTimestamp) == previousTimestamp)
		{
			base.Dispatcher.InvokeAsync(delegate
			{
				SetStatus(status);
			}, DispatcherPriority.Background);
		}
	}

	private FaceBoxTrackingFrameResult DetectFaceBox(BitmapSource bitmap, long capturedAtTimestamp, DateTime capturedAtUtc, FaceBoxSystem faceBoxSystem, int faceBoxSystemGeneration)
	{
		if (faceBoxSystem == FaceBoxSystem.MediaPipe)
		{
			FaceLandmarkTrackingResult trackingResult;
			lock (_faceLandmarkTrackerLock)
			{
				trackingResult = ((_isClosing || _faceLandmarkTracker == null) ? FaceLandmarkTrackingResult.None : _faceLandmarkTracker.Detect(bitmap, capturedAtUtc));
			}
			return new FaceBoxTrackingFrameResult(faceBoxSystem, faceBoxSystemGeneration, trackingResult, null, capturedAtTimestamp, capturedAtUtc, bitmap.PixelWidth, bitmap.PixelHeight, bitmap);
		}
		ThreeDdfaOnnxReconstructionClient? threeDdfaFaceBoxClient;
		lock (_threeDdfaClientLock)
		{
			threeDdfaFaceBoxClient = _threeDdfaFaceBoxClient;
		}
		ThreeDdfaOnnxSidecarResponse threeDdfaOnnxSidecarResponse;
		try
		{
			bool flag2 = _threeDdfaTrackingFaceBox == null || (capturedAtUtc - _lastThreeDdfaFaceBoxesAtUtc).TotalMilliseconds >= 1000.0;
			ThreeDdfaOnnxSidecarFaceBox? threeDdfaOnnxSidecarFaceBox = flag2 ? null : _threeDdfaTrackingFaceBox;
			if (threeDdfaOnnxSidecarFaceBox == null)
			{
				_lastThreeDdfaFaceBoxesAtUtc = capturedAtUtc;
			}
			threeDdfaOnnxSidecarResponse = ((threeDdfaFaceBoxClient == null) ? new ThreeDdfaOnnxSidecarResponse
			{
				Ok = false,
				Status = "3DDFA-V2 face-box client stopped",
				TrustDecision = "The selected 3DDFA-V2 tracking session is no longer active."
			} : threeDdfaFaceBoxClient.Reconstruct(bitmap, capturedAtUtc, threeDdfaOnnxSidecarFaceBox, ThreeDdfaOnnxRequestMode.Tracking, 200));
			if (threeDdfaOnnxSidecarResponse.Ok && threeDdfaOnnxSidecarResponse.HasFace)
			{
				_threeDdfaTrackingFaceBox = CreateThreeDdfaTrackingFaceBox(threeDdfaOnnxSidecarResponse, bitmap.PixelWidth, bitmap.PixelHeight);
			}
			else if (threeDdfaFaceBoxClient != null && threeDdfaOnnxSidecarFaceBox != null)
			{
				_lastThreeDdfaFaceBoxesAtUtc = capturedAtUtc;
				threeDdfaOnnxSidecarResponse = threeDdfaFaceBoxClient.Reconstruct(bitmap, capturedAtUtc, null, ThreeDdfaOnnxRequestMode.Tracking, 200);
				if (threeDdfaOnnxSidecarResponse.Ok && threeDdfaOnnxSidecarResponse.HasFace)
				{
					_threeDdfaTrackingFaceBox = CreateThreeDdfaTrackingFaceBox(threeDdfaOnnxSidecarResponse, bitmap.PixelWidth, bitmap.PixelHeight);
				}
			}
		}
		catch (Exception ex)
		{
			threeDdfaOnnxSidecarResponse = new ThreeDdfaOnnxSidecarResponse
			{
				Ok = false,
				Status = "3DDFA-V2 face tracking failed: " + ex.Message,
				TrustDecision = "3DDFA-V2 face tracking failed for this frame."
			};
		}
		finally
		{
			if (faceBoxSystemGeneration != _faceBoxSystemGeneration || faceBoxSystem != _selectedFaceBoxSystem)
			{
				threeDdfaFaceBoxClient?.Dispose();
			}
		}
		FaceLandmarkTrackingResult trackingResult2 = ThreeDdfaOnnxFaceTrackingMapper.ToTrackingResult(threeDdfaOnnxSidecarResponse, bitmap.PixelWidth, bitmap.PixelHeight, capturedAtUtc);
		return new FaceBoxTrackingFrameResult(faceBoxSystem, faceBoxSystemGeneration, trackingResult2, threeDdfaOnnxSidecarResponse, capturedAtTimestamp, capturedAtUtc, bitmap.PixelWidth, bitmap.PixelHeight, bitmap);
	}

	private void ApplyFaceFeatureDetectionResult(
		FaceBoxTrackingFrameResult trackingResult,
		FaceAnalysisResult? analysisResult,
		PreviewTrackingOverlay? nativeOverlay,
		int faceAnalysisGeneration)
	{
		if (_isClosing
			|| trackingResult.FaceBoxSystemGeneration != _faceBoxSystemGeneration
			|| trackingResult.FaceBoxSystem != _selectedFaceBoxSystem
			|| faceAnalysisGeneration != Volatile.Read(ref _faceAnalysisGeneration)
			|| IsLiveAwarenessFrameExpired(trackingResult.CapturedAtTimestamp))
		{
			ExpireLiveAwarenessState(trackingResult.CapturedAtTimestamp);
			return;
		}

		FaceFeatureDetection featureDetection = trackingResult.TrackingResult.FeatureDetection;
		if (featureDetection.HasFace && analysisResult != null)
		{
			_currentFaceFeatureDetection = featureDetection;
			_currentFaceLandmarkFrame = analysisResult.ReconstructedLandmarkFrame;
			_currentFaceLandmarkMetrics = analysisResult.Metrics;
			_currentFaceLockStabilityAnalysis = analysisResult.Stability;
			Volatile.Write(ref _lastFaceFeatureLockTimestamp, trackingResult.CapturedAtTimestamp);
		}
		else
		{
			ClearLiveFaceState();
		}

		UpdateDirectX12TrackingOverlay(nativeOverlay ?? PreviewTrackingOverlay.Empty);
		if (Interlocked.CompareExchange(ref _faceResultUiUpdateInFlight, 1, 0) != 0)
		{
			return;
		}

		try
		{
			base.Dispatcher.BeginInvoke((Action)delegate
			{
				try
				{
					if (_isClosing
						|| trackingResult.FaceBoxSystemGeneration != _faceBoxSystemGeneration
						|| trackingResult.FaceBoxSystem != _selectedFaceBoxSystem
						|| faceAnalysisGeneration != Volatile.Read(ref _faceAnalysisGeneration))
					{
						return;
					}
					_lastFaceBoxBackendStatus = trackingResult.TrackingResult.BackendStatus;
					if (trackingResult.ThreeDdfaResponse != null)
					{
						_currentThreeDdfaOnnxResponse = trackingResult.ThreeDdfaResponse;
					}
					bool resultIsStillCurrent = trackingResult.CapturedAtTimestamp == Volatile.Read(ref _lastFaceFeatureLockTimestamp)
						&& !IsLiveAwarenessFrameExpired(trackingResult.CapturedAtTimestamp);
					if (resultIsStillCurrent && featureDetection.HasFace && analysisResult != null)
					{
						UpdateAvatarCaptureState();
						if (_selectedFaceBoxSystem == FaceBoxSystem.MediaPipe && IsAvatarUserLoggedIn && _avatarLearningRequested && analysisResult.RawLandmarkFrame.HasDenseMesh)
						{
							TryStartMediaPipeGeometryUpdate(analysisResult.RawLandmarkFrame, trackingResult.SourceWidth, trackingResult.SourceHeight);
						}
					}

					if (_showLiveWireframePreview)
					{
						DrawLiveWireframePreview();
					}
					if (nativeOverlay != null && IsDirectX12PreviewSurfaceActive())
					{
						_cachedNativeOverlayFeatureDetection = _currentFaceFeatureDetection;
						_cachedNativeOverlayLandmarkFrame = _currentFaceLandmarkFrame;
						_cachedNativeOverlayIncludesFaceMesh = _showFaceMeshOverlay;
						_cachedNativeTrackingOverlay = nativeOverlay;
						if (FaceCueGuideCanvas.Visibility != Visibility.Collapsed)
						{
							FaceCueGuideCanvas.Children.Clear();
							FaceCueGuideCanvas.Visibility = Visibility.Collapsed;
						}
					}
					else
					{
						UpdateFaceCueGuideOverlay(_latestFrame);
					}
				}
				finally
				{
					Volatile.Write(ref _faceResultUiUpdateInFlight, 0);
				}
			}, DispatcherPriority.Render);
		}
		catch
		{
			Volatile.Write(ref _faceResultUiUpdateInFlight, 0);
			throw;
		}
	}

	private void TryStartMediaPipeGeometryUpdate(FaceLandmarkFrame observedLandmarks, int frameWidthPixels, int frameHeightPixels)
	{
		FaceFrameGeometryCalibration currentFaceFrameGeometryCalibration = GetCurrentFaceFrameGeometryCalibration();
		_mediaPipeGeometryPipeline.TryStart(() => MediaPipeGeometryFrame.Create(observedLandmarks, frameWidthPixels, frameHeightPixels, GetSelectedCameraName(), currentFaceFrameGeometryCalibration.CameraHorizontalFovDegrees ?? 70.0));
	}

	private void MediaPipeGeometryPipelineModelUpdated(object? sender, MediaPipeGeometryModelUpdatedEventArgs e)
	{
		if (Interlocked.CompareExchange(ref _mediaPipeGeometryUiUpdateInFlight, 1, 0) != 0)
		{
			return;
		}

		try
		{
			base.Dispatcher.BeginInvoke((Action)delegate
			{
				try
				{
					if (!_isClosing && IsAvatarUserLoggedIn && string.Equals(e.Model.SubjectId, CurrentAvatarProfileId, StringComparison.OrdinalIgnoreCase))
					{
						_currentMediaPipeGeometryModel = e.Model;
						_lastMediaPipeGeometryProcessingDuration = e.ProcessingDuration;
						UpdateAvatarWorkerTimingUi();
						UpdateAvatarLearningStatusUiIfDue();
					}
				}
				finally
				{
					Volatile.Write(ref _mediaPipeGeometryUiUpdateInFlight, 0);
				}
			}, DispatcherPriority.Background);
		}
		catch
		{
			Volatile.Write(ref _mediaPipeGeometryUiUpdateInFlight, 0);
			throw;
		}
	}

	private void UpdateAvatarWorkerTimingUi()
	{
		if (base.IsLoaded)
		{
			string duration = _lastMediaPipeGeometryProcessingDuration.HasValue
				? FormatWorkerDuration(_lastMediaPipeGeometryProcessingDuration.Value)
				: "--";
			SetTextIfChanged(AvatarWorkerTimingText, $"Measured geometry worker: last {duration} | accepted {_mediaPipeGeometryPipeline.SubmittedFrameCount:n0} | busy drops {_mediaPipeGeometryPipeline.BusyDropCount:n0}");
		}
	}

	private static string FormatWorkerDuration(TimeSpan duration)
	{
		if (duration.TotalMinutes >= 1.0)
		{
			return $"{(int)duration.TotalMinutes}:{duration.Seconds:00}.{duration.Milliseconds / 10:00}";
		}
		return $"{duration.TotalSeconds:0.00} s";
	}

	private static ThreeDdfaOnnxSidecarFaceBox? CreateThreeDdfaTrackingFaceBox(ThreeDdfaOnnxSidecarResponse response, int frameWidth, int frameHeight)
	{
		if (!response.Ok || !response.HasFace || response.SparseLandmarks.Count < 20)
		{
			return response.FaceBox;
		}
		ThreeDdfaOnnxSidecarVertex first = response.SparseLandmarks[0];
		double num = first.X;
		double num2 = first.X;
		double num3 = first.Y;
		double num4 = first.Y;
		for (int i = 1; i < response.SparseLandmarks.Count; i++)
		{
			ThreeDdfaOnnxSidecarVertex point = response.SparseLandmarks[i];
			num = Math.Min(num, point.X);
			num2 = Math.Max(num2, point.X);
			num3 = Math.Min(num3, point.Y);
			num4 = Math.Max(num4, point.Y);
		}
		double num5 = num2 - num;
		double num6 = num4 - num3;
		if (num5 <= 1.0 || num6 <= 1.0)
		{
			return response.FaceBox;
		}
		return new ThreeDdfaOnnxSidecarFaceBox
		{
			Left = Math.Clamp(num - num5 * 0.14, 0.0, Math.Max(0.0, (double)frameWidth - 1.0)),
			Top = Math.Clamp(num3 - num6 * 0.3, 0.0, Math.Max(0.0, (double)frameHeight - 1.0)),
			Right = Math.Clamp(num2 + num5 * 0.14, 1.0, Math.Max(1.0, frameWidth)),
			Bottom = Math.Clamp(num4 + num6 * 0.12, 1.0, Math.Max(1.0, frameHeight)),
			Normalized = false,
			Confidence = Math.Clamp(response.ReconstructionConfidencePercent / 100.0, 0.01, 1.0)
		};
	}

	private bool HasUsableFaceFeatureLock()
	{
		long timestamp = Volatile.Read(ref _lastFaceFeatureLockTimestamp);
		return _currentFaceFeatureDetection.HasFace
			&& timestamp != 0L
			&& !IsLiveAwarenessFrameExpired(timestamp);
	}

	private static bool IsLiveAwarenessFrameExpired(long capturedAtTimestamp)
	{
		return capturedAtTimestamp == 0L
			|| Stopwatch.GetElapsedTime(capturedAtTimestamp) > MaximumLiveAwarenessFrameAge;
	}

	private void ExpireLiveAwarenessState(long expiredFrameTimestamp)
	{
		long currentTimestamp = Volatile.Read(ref _lastFaceFeatureLockTimestamp);
		if (currentTimestamp != 0L
			&& currentTimestamp != expiredFrameTimestamp
			&& !IsLiveAwarenessFrameExpired(currentTimestamp))
		{
			return;
		}
		ClearLiveFaceState();
		UpdateDirectX12TrackingOverlay(PreviewTrackingOverlay.Empty);
	}

	private void ClearLiveFaceState()
	{
		Volatile.Write(ref _lastFaceFeatureLockTimestamp, 0L);
		_currentFaceFeatureDetection = FaceFeatureDetection.None;
		_currentFaceLandmarkFrame = FaceLandmarkFrame.None;
		_currentFaceLandmarkMetrics = FaceLandmarkMetrics.None;
		_currentFaceLockStabilityAnalysis = FaceLockStabilityAnalysis.Waiting;
	}

	private void ResetLandmarkTracking()
	{
		Interlocked.Increment(ref _faceAnalysisGeneration);
		_currentFaceLandmarkFrame = FaceLandmarkFrame.None;
		_currentFaceLandmarkMetrics = FaceLandmarkMetrics.None;
		_currentFaceLockStabilityAnalysis = FaceLockStabilityAnalysis.Waiting;
		_currentAvatarCaptureQuality = AvatarCaptureQualityAssessment.Waiting;
		lock (_faceLandmarkTrackerLock)
		{
			_faceLandmarkTracker?.Reset();
		}
		lock (_faceAnalysisStateLock)
		{
			_faceLandmarkReconstructor.Reset();
			_faceLandmarkMetricCalculator.Reset();
			_faceLockStabilityAnalyzer.Reset();
		}
	}

	private void UpdateAvatarCaptureState()
	{
		UpdateAvatarLearningStatusUiIfDue();
	}

	private AvatarCaptureQualityAssessment AnalyzeAvatarCaptureQuality()
	{
		CameraVideoMode? cameraVideoMode = CameraModeComboBox.SelectedItem as CameraVideoMode;
		return _avatarCaptureQualityAnalyzer.Analyze(new AvatarCaptureQualityInput
		{
			VideoWidth = cameraVideoMode?.Width,
			VideoHeight = cameraVideoMode?.Height,
			FramesPerSecond = cameraVideoMode?.FramesPerSecond,
			InputFormat = cameraVideoMode?.InputFormat,
			IsAutoCameraMode = (cameraVideoMode?.IsAuto ?? true),
			LandmarkFrame = _currentFaceLandmarkFrame,
			Metrics = _currentFaceLandmarkMetrics,
			Stability = _currentFaceLockStabilityAnalysis,
			UserLoggedIn = IsAvatarUserLoggedIn,
			AvatarCaptureRequested = _avatarLearningRequested
		});
	}

	private static void OpenLocalFile(string path)
	{
		if (!File.Exists(path))
		{
			throw new FileNotFoundException("Preview file was not created.", path);
		}
		Process.Start(new ProcessStartInfo
		{
			FileName = path,
			UseShellExecute = true
		});
	}

	private void OpenDataFolderClicked(object sender, RoutedEventArgs e)
	{
		AvatarDataFolderDialog avatarDataFolderDialog = new AvatarDataFolderDialog(_outputFolder)
		{
			Owner = this
		};
		if (avatarDataFolderDialog.ShowDialog() != true)
		{
			return;
		}
		string fullPath = System.IO.Path.GetFullPath(avatarDataFolderDialog.SelectedFolder);
		if (string.Equals(fullPath, System.IO.Path.GetFullPath(_outputFolder), StringComparison.OrdinalIgnoreCase))
		{
			SetStatus("Avatar data folder unchanged: " + _outputFolder);
			return;
		}
		bool isAvatarUserLoggedIn = IsAvatarUserLoggedIn;
		if (isAvatarUserLoggedIn)
		{
			LogOutAvatarUser(announce: false);
		}
		_outputFolder = fullPath;
		SaveOutputFolderPointer(_outputFolder);
		_personIdentityMemory.ConfigureOutputFolder(_outputFolder);
		InitializeAvatarProfiles(promptForStartupUser: false);
		string value = PrepareAvatarCaptureFolder(showStatus: false);
		string value2 = (isAvatarUserLoggedIn ? " The previous avatar user was logged out." : "");
		string text = $"Avatar data folder set: {_outputFolder}.{value2} {value}".Trim();
		MonitorStatusText.Text = text;
		SetStatus(text);
	}

	private void CloseClicked(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private async void OpenDualCameraWorkspaceClicked(object sender, RoutedEventArgs e)
	{
		try
		{
			if (_isCameraEnabled)
			{
				SetStatus("Stopping the main camera before opening the dual-camera workspace.");
				await StopPreviewAsync();
			}
			DualCameraWorkspaceWindow dualCameraWorkspaceWindow = new DualCameraWorkspaceWindow(_outputFolder, IsAvatarUserLoggedIn ? GetAvatarDataFolder() : null, IsAvatarUserLoggedIn ? CurrentAvatarProfileId : null, IsAvatarUserLoggedIn ? CurrentAvatarProfileDisplayName : null);
			dualCameraWorkspaceWindow.Owner = this;
			dualCameraWorkspaceWindow.ShowDialog();
			SetStatus("Dual-camera workspace closed. Main camera remains off.");
		}
		catch (Exception ex)
		{
			SetStatus("Could not open the dual-camera workspace: " + ex.Message);
		}
	}

	private FaceCueGuideLayout GetFaceCueLayout()
	{
		if (FaceAutoFollowCheckBox.IsChecked == true && _activeFaceCueLayout != null)
		{
			return _activeFaceCueLayout;
		}
		return GetManualFaceCueLayout();
	}

	private FaceCueGuideLayout GetManualFaceCueLayout()
	{
		return new FaceCueGuideLayout(Math.Clamp(FaceFieldXSlider.Value, 20.0, 80.0), Math.Clamp(FaceFieldYSlider.Value, 20.0, 80.0), Math.Clamp(FaceFieldSizeSlider.Value, 25.0, 90.0));
	}

	private void UpdateSettingLabels()
	{
		FaceFieldXValueText.Text = $"{FaceFieldXSlider.Value:0}%";
		FaceFieldYValueText.Text = $"{FaceFieldYSlider.Value:0}%";
		FaceFieldSizeValueText.Text = $"{FaceFieldSizeSlider.Value:0}%";
	}

	private static string ResolveInitialOutputFolder(string requestedOutputFolder = "")
	{
		if (TryResolveRequestedOutputFolder(requestedOutputFolder, out string outputFolder))
		{
			return outputFolder;
		}
		string text = LoadOutputFolderPointer();
		if (!string.IsNullOrWhiteSpace(text))
		{
			return text;
		}
		string? pathRoot = System.IO.Path.GetPathRoot("D:\\Avatar Builder Output");
		if (!string.IsNullOrWhiteSpace(pathRoot) && Directory.Exists(pathRoot))
		{
			return "D:\\Avatar Builder Output";
		}
		return System.IO.Path.Combine(AppContext.BaseDirectory, "AvatarBuilderSessions");
	}

	private void EnsureOutputFolderConfiguredForLaunch()
	{
		if (TryResolveRequestedOutputFolder(_startupOptions.OutputFolder, out string outputFolder))
		{
			_outputFolder = outputFolder;
			Directory.CreateDirectory(_outputFolder);
			SaveOutputFolderPointer(_outputFolder);
			return;
		}
		string text = LoadOutputFolderPointer();
		if (IsExistingFolder(text))
		{
			_outputFolder = text;
			return;
		}
		EnsureOutputFolderPointerFileExists();
		string message = (string.IsNullOrWhiteSpace(text) ? "Avatar Builder needs a storage folder for avatar profiles, reconstruction observations, models, and review reports." : $"The configured storage folder was not found:{Environment.NewLine}{text}{Environment.NewLine}{Environment.NewLine}Choose where Avatar Builder should store its data.");
		string text2 = PromptForOutputFolder(message, text);
		if (string.IsNullOrWhiteSpace(text2))
		{
			text2 = ResolveFallbackOutputFolder();
		}
		Directory.CreateDirectory(text2);
		_outputFolder = text2;
		SaveOutputFolderPointer(_outputFolder);
	}

	private static bool TryResolveRequestedOutputFolder(string requestedOutputFolder, out string outputFolder)
	{
		outputFolder = "";
		if (string.IsNullOrWhiteSpace(requestedOutputFolder))
		{
			return false;
		}
		try
		{
			string fullPath = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(requestedOutputFolder.Trim().Trim('"')));
			string? pathRoot = System.IO.Path.GetPathRoot(fullPath);
			if (string.IsNullOrWhiteSpace(pathRoot) || !Directory.Exists(pathRoot))
			{
				return false;
			}
			outputFolder = fullPath;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static string LoadOutputFolderPointer()
	{
		try
		{
			string outputFolderPointerPath = GetOutputFolderPointerPath();
			if (!File.Exists(outputFolderPointerPath))
			{
				return "";
			}
			return File.ReadLines(outputFolderPointerPath, Encoding.UTF8).FirstOrDefault((string line) => !string.IsNullOrWhiteSpace(line))?.Trim().Trim('"') ?? "";
		}
		catch
		{
			return "";
		}
	}

	private static void SaveOutputFolderPointer(string outputFolder)
	{
		try
		{
			string outputFolderPointerPath = GetOutputFolderPointerPath();
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputFolderPointerPath) ?? AppContext.BaseDirectory);
			File.WriteAllText(outputFolderPointerPath, (outputFolder ?? "").Trim() + Environment.NewLine, Encoding.UTF8);
		}
		catch
		{
		}
	}

	private static void EnsureOutputFolderPointerFileExists()
	{
		try
		{
			string outputFolderPointerPath = GetOutputFolderPointerPath();
			if (!File.Exists(outputFolderPointerPath))
			{
				Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputFolderPointerPath) ?? AppContext.BaseDirectory);
				File.WriteAllText(outputFolderPointerPath, "", Encoding.UTF8);
			}
		}
		catch
		{
		}
	}

	private static string GetOutputFolderPointerPath()
	{
		return System.IO.Path.Combine(AppContext.BaseDirectory, "AvatarBuilderOutputFolder.txt");
	}

	private static bool IsExistingFolder(string folder)
	{
		if (!string.IsNullOrWhiteSpace(folder))
		{
			return Directory.Exists(folder);
		}
		return false;
	}

	private string PromptForOutputFolder(string message, string configuredFolder)
	{
		MessageBox.Show(this, $"{message}{Environment.NewLine}{Environment.NewLine}The selected folder path will be saved in:{Environment.NewLine}{GetOutputFolderPointerPath()}", "Choose Avatar Builder Storage", MessageBoxButton.OK, MessageBoxImage.Asterisk);
		OpenFolderDialog openFolderDialog = new OpenFolderDialog
		{
			Title = "Choose Avatar Builder storage folder",
			InitialDirectory = GetOutputFolderPickerInitialDirectory(configuredFolder),
			Multiselect = false
		};
		if (openFolderDialog.ShowDialog(this) != true)
		{
			return "";
		}
		return openFolderDialog.FolderName;
	}

	private static string GetOutputFolderPickerInitialDirectory(string configuredFolder)
	{
		if (IsExistingFolder(configuredFolder))
		{
			return configuredFolder;
		}
		if (Directory.Exists("D:\\Avatar Builder Output"))
		{
			return "D:\\Avatar Builder Output";
		}
		string? pathRoot = System.IO.Path.GetPathRoot("D:\\Avatar Builder Output");
		if (!string.IsNullOrWhiteSpace(pathRoot) && Directory.Exists(pathRoot))
		{
			return pathRoot;
		}
		return AppContext.BaseDirectory;
	}

	private static string ResolveFallbackOutputFolder()
	{
		string? pathRoot = System.IO.Path.GetPathRoot("D:\\Avatar Builder Output");
		if (!string.IsNullOrWhiteSpace(pathRoot) && Directory.Exists(pathRoot))
		{
			return "D:\\Avatar Builder Output";
		}
		return System.IO.Path.Combine(AppContext.BaseDirectory, "AvatarBuilderSessions");
	}

	private string PrepareAvatarCaptureFolder(bool showStatus)
	{
		string avatarDataFolder = GetAvatarDataFolder();
		try
		{
			Directory.CreateDirectory(avatarDataFolder);
			UpdateAvatarLearningStatusUi();
			string text = $"Avatar capture folder ready: {avatarDataFolder}. {ActiveAvatarReconstructionName} stores dense reconstruction review data; {GetFaceBoxSystemDisplayName()} eye/jaw/brow tracking remains live.";
			if (showStatus)
			{
				MonitorStatusText.Text = text;
			}
			return text;
		}
		catch (Exception ex)
		{
			string text2 = "Could not prepare Avatar capture folder: " + ex.Message;
			if (showStatus)
			{
				MonitorStatusText.Text = text2;
			}
			return text2;
		}
	}

	private string GetAvatarDataFolder()
	{
		return _avatarProfileStore.GetProfileFolder(_outputFolder, _currentAvatarProfile);
	}

	private string GetSelectedCameraName()
	{
		if (!(CameraComboBox.SelectedItem is CameraDevice cameraDevice))
		{
			return "";
		}
		return cameraDevice.Name;
	}

	private FaceFrameGeometryCalibration GetCurrentFaceFrameGeometryCalibration()
	{
		if (GetSelectedCameraName().Contains("Insta360 Link 2 Pro", StringComparison.OrdinalIgnoreCase))
		{
			return new FaceFrameGeometryCalibration
			{
				CameraHorizontalFovDegrees = 71.4,
				ReferenceSource = "camera FOV estimate"
			};
		}
		return FaceFrameGeometryCalibration.None;
	}

	private void SetStatus(string status)
	{
		if (!string.Equals(TopStatusText.Text, status, StringComparison.Ordinal))
		{
			TopStatusText.Text = status;
		}
		if (!string.Equals(FooterText.Text, status, StringComparison.Ordinal))
		{
			FooterText.Text = status;
		}
	}

}
