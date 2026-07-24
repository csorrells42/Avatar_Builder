using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Threading;
using AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.Ffmpeg;

namespace AvatarBuilder.Modules.Webcam.DualCamera;

public partial class DualCameraWorkspaceWindow : Window, IComponentConnector
{
	private static readonly TimeSpan DefaultCameraReleaseCooldown = TimeSpan.FromMilliseconds(500L);

	private static readonly TimeSpan PocketCameraReleaseCooldown = TimeSpan.FromSeconds(2L);

	private static readonly TimeSpan PocketCameraRetryDelay = TimeSpan.FromSeconds(2L);

	private readonly FfmpegCameraModeService _modeServiceA = new FfmpegCameraModeService();

	private readonly FfmpegCameraModeService _modeServiceB = new FfmpegCameraModeService();

	private readonly DualCameraLane _laneA;

	private readonly DualCameraLane _laneB;

	private readonly DualCameraRegistrationCoordinator _registrationCoordinator;

	private readonly DualCameraCalibrationCoordinator _calibrationCoordinator;

	private readonly MediaPipeStereoFacePipeline _stereoFacePipeline = new MediaPipeStereoFacePipeline();

	private readonly string _profileFolder;

	private readonly string _subjectId;

	private readonly string _subjectDisplayName;

	private DualCameraCalibrationModel? _savedCalibration;

	private CancellationTokenSource? _modeCancellationA;

	private CancellationTokenSource? _modeCancellationB;

	private bool _initializing;

	private bool _updatingToggles;

	private bool _updatingCalibrationToggle;

	private bool _updatingStereoToggle;

	private bool _shutdownStarted;

	private bool _shutdownCompleted;

	private int _laneARecoveryInProgress;

	private int _laneBRecoveryInProgress;

	private int _stereoReconstructionEnabled;

	private long _lastRegistrationStatusTimestamp;

	private MediaPipeStereoFaceModel _currentStereoFaceModel = MediaPipeStereoFaceModel.Empty;

	public DualCameraWorkspaceWindow(string outputRoot, string? profileFolder = null, string? subjectId = null, string? subjectDisplayName = null)
	{
		InitializeComponent();
		_profileFolder = profileFolder?.Trim() ?? "";
		_subjectId = subjectId?.Trim() ?? "";
		_subjectDisplayName = (string.IsNullOrWhiteSpace(subjectDisplayName) ? _subjectId : subjectDisplayName.Trim());
		_laneA = new DualCameraLane("Camera A", CameraAPreviewPanel, CameraAPlaceholder, CameraAPreviewStateText, delegate(string status)
		{
			CameraAStatusText.Text = status;
		}, delegate(string reason)
		{
			ScheduleLaneRecovery(laneA: true, reason);
		});
		_laneB = new DualCameraLane("Camera B", CameraBPreviewPanel, CameraBPlaceholder, CameraBPreviewStateText, delegate(string status)
		{
			CameraBStatusText.Text = status;
		}, delegate(string reason)
		{
			ScheduleLaneRecovery(laneA: false, reason);
		});
		_registrationCoordinator = new DualCameraRegistrationCoordinator();
		_calibrationCoordinator = new DualCameraCalibrationCoordinator(outputRoot);
		_laneA.ObservationAvailable += delegate(DualCameraObservation observation)
		{
			_registrationCoordinator.Submit(cameraA: true, observation);
		};
		_laneB.ObservationAvailable += delegate(DualCameraObservation observation)
		{
			_registrationCoordinator.Submit(cameraA: false, observation);
		};
		_laneA.CalibrationFrameAvailable += delegate(DualCameraCalibrationFrame frame)
		{
			_calibrationCoordinator.Submit(cameraA: true, frame);
		};
		_laneB.CalibrationFrameAvailable += delegate(DualCameraCalibrationFrame frame)
		{
			_calibrationCoordinator.Submit(cameraA: false, frame);
		};
		_registrationCoordinator.RegistrationAvailable += RegistrationAvailable;
		_registrationCoordinator.StatusChanged += RegistrationStatusChanged;
		_calibrationCoordinator.ProgressChanged += CalibrationProgressChanged;
		_calibrationCoordinator.OverlaysChanged += CalibrationOverlaysChanged;
		_stereoFacePipeline.ModelUpdated += StereoFaceModelUpdated;
		_stereoFacePipeline.ProcessingFailed += StereoFaceProcessingFailed;
		_savedCalibration = DualCameraCalibrationModel.Load(outputRoot);
		if (_savedCalibration is not null)
		{
			CalibrationStatusText.Text = $"Saved calibration available: {_savedCalibration.CalibratedAtUtc.ToLocalTime():g} | baseline {_savedCalibration.BaselineInches:0.00} in | stereo RMS {_savedCalibration.StereoReprojectionErrorPixels:0.00} px";
		}
	}

	private async void WindowLoaded(object sender, RoutedEventArgs e)
	{
		_initializing = true;
		try
		{
			await ConfigureStereoFacePipelineAsync();
			IReadOnlyList<CameraDevice> cameras = await CameraDiscoveryService.GetVideoInputDevicesAsync();
			CameraAComboBox.ItemsSource = cameras;
			CameraBComboBox.ItemsSource = cameras;
			if (cameras.Count == 0)
			{
				WorkspaceStatusText.Text = "No cameras found.";
				return;
			}
			CameraAComboBox.SelectedItem = FindPreferredCamera(cameras, "Insta360 Link 2 Pro") ?? cameras[0];
			CameraBComboBox.SelectedItem = FindPreferredCamera(cameras, "OsmoPocket3") ?? cameras.FirstOrDefault((CameraDevice camera) => !SamePhysicalCamera(camera, (CameraDevice)CameraAComboBox.SelectedItem)) ?? cameras[0];
			InlineArray2<Task> buffer = default(InlineArray2<Task>);
			buffer[0] = LoadModesAsync(CameraAComboBox, ModeAComboBox, laneA: true);
			buffer[1] = LoadModesAsync(CameraBComboBox, ModeBComboBox, laneA: false);
			await Task.WhenAll(buffer);
			ApplyMatchingSavedCalibration();
			WorkspaceStatusText.Text = $"{cameras.Count} cameras found.";
		}
		catch (Exception ex)
		{
			WorkspaceStatusText.Text = "Camera discovery failed: " + ex.Message;
		}
		finally
		{
			_initializing = false;
		}
	}

	private async void CameraASelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_initializing)
		{
			await HandleSelectionChangedAsync(laneA: true);
			ApplyMatchingSavedCalibration();
		}
	}

	private async void CameraBSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_initializing)
		{
			await HandleSelectionChangedAsync(laneA: false);
			ApplyMatchingSavedCalibration();
		}
	}

	private async void ModeASelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_initializing && _laneA.IsRunning)
		{
			await RestartLaneAsync(laneA: true);
		}
	}

	private async void ModeBSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (!_initializing && _laneB.IsRunning)
		{
			await RestartLaneAsync(laneA: false);
		}
	}

	private async void CameraAToggleChanged(object sender, RoutedEventArgs e)
	{
		if (!_updatingToggles)
		{
			await SetLaneStateAsync(laneA: true, CameraAToggle.IsChecked == true);
		}
	}

	private async void CameraBToggleChanged(object sender, RoutedEventArgs e)
	{
		if (!_updatingToggles)
		{
			await SetLaneStateAsync(laneA: false, CameraBToggle.IsChecked == true);
		}
	}

	private void TranslationViewToggleChanged(object sender, RoutedEventArgs e)
	{
		bool valueOrDefault = TranslationViewToggle.IsChecked == true;
		TranslationViewToggle.Content = (valueOrDefault ? "Coordinate Translation On" : "Coordinate Translation Off");
		_laneA.SetTranslationViewEnabled(valueOrDefault);
		_laneB.SetTranslationViewEnabled(valueOrDefault);
		UpdateRegistrationCoordinatorState();
		TranslationStatusText.Text = (valueOrDefault ? "Waiting for a timestamp-matched face lock from both cameras..." : "Own mesh: cyan | translated partner: amber | direct-view fusion: green");
	}

	private void StereoReconstructionToggleChanged(object sender, RoutedEventArgs e)
	{
		if (!_updatingStereoToggle)
		{
			bool valueOrDefault = StereoReconstructionToggle.IsChecked == true;
			if (valueOrDefault && (!_stereoFacePipeline.IsConfigured || !_laneA.IsRunning || !_laneB.IsRunning || !HasMatchingSavedCalibration()))
			{
				SetStereoReconstructionToggle(enabled: false);
				StereoReconstructionStatusText.Text = ((!_stereoFacePipeline.IsConfigured) ? "Log in to an avatar profile before building its calibrated 3D face." : ((!HasMatchingSavedCalibration()) ? "Select the calibrated cameras in their saved A/B order first." : "Turn on both cameras before building the 3D face."));
				UpdateRegistrationCoordinatorState();
			}
			else
			{
				SetStereoReconstructionToggle(valueOrDefault);
				StereoReconstructionStatusText.Text = (valueOrDefault ? "Stereo face capture running: direct calibrated points are being fused off the camera threads." : CreateStereoModelStatus(_currentStereoFaceModel));
				UpdateRegistrationCoordinatorState();
			}
		}
	}

	private void PhysicalCalibrationToggleChanged(object sender, RoutedEventArgs e)
	{
		if (!_updatingCalibrationToggle)
		{
			bool valueOrDefault = PhysicalCalibrationToggle.IsChecked == true;
			if (valueOrDefault && (!_laneA.IsRunning || !_laneB.IsRunning))
			{
				SetCalibrationToggle(enabled: false);
				CalibrationStatusText.Text = "Turn on both cameras before starting physical calibration.";
			}
			else if (!valueOrDefault)
			{
				_calibrationCoordinator.Stop();
				_laneA.SetCalibrationCaptureEnabled(enabled: false);
				_laneB.SetCalibrationCaptureEnabled(enabled: false);
				SetCalibrationToggle(enabled: false);
			}
			else if (!(CameraAComboBox.SelectedItem is CameraDevice cameraDevice) || !(CameraBComboBox.SelectedItem is CameraDevice cameraDevice2))
			{
				SetCalibrationToggle(enabled: false);
				CalibrationStatusText.Text = "Select both cameras first.";
			}
			else
			{
				_laneA.SetCalibrationCaptureEnabled(enabled: true);
				_laneB.SetCalibrationCaptureEnabled(enabled: true);
				_calibrationCoordinator.Start(cameraDevice.Name, cameraDevice2.Name);
				SetCalibrationToggle(enabled: true);
			}
		}
	}

	private void CalibrationOverlaysChanged(DualCameraCalibrationOverlay? cameraA, DualCameraCalibrationOverlay? cameraB)
	{
		_laneA.ApplyCalibrationOverlay(cameraA);
		_laneB.ApplyCalibrationOverlay(cameraB);
	}

	private void CalibrationProgressChanged(DualCameraCalibrationProgress progress)
	{
		base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
		{
			if (!_shutdownStarted)
			{
				CalibrationStatusText.Text = progress.Status;
				WorkspaceStatusText.Text = ((!progress.Completed) ? $"Calibrating cameras: {progress.AcceptedPairCount}/{progress.RequiredPairCount} paired views." : ((progress.Calibration is not null) ? "Physical camera calibration complete." : "Physical camera calibration rejected."));
				DualCameraCalibrationModel? calibration = progress.Calibration;
				if (calibration is not null)
				{
					_savedCalibration = calibration;
					ApplyMatchingSavedCalibration();
				}
				if (progress.Completed)
				{
					_laneA.SetCalibrationCaptureEnabled(enabled: false);
					_laneB.SetCalibrationCaptureEnabled(enabled: false);
					SetCalibrationToggle(enabled: false);
				}
			}
		});
	}

	private void RegistrationAvailable(DualCameraRegistrationFrame cameraA, DualCameraRegistrationFrame cameraB, DualCameraRegistrationDiagnostics diagnostics)
	{
		_laneA.ApplyRegistration(cameraA);
		_laneB.ApplyRegistration(cameraB);
		if (Volatile.Read(in _stereoReconstructionEnabled) == 0)
		{
			return;
		}
		double? physicalBaselineInches = diagnostics.PhysicalBaselineInches;
		double baselineInches = physicalBaselineInches.GetValueOrDefault();
		if (baselineInches > 0.0 && cameraA.TriangulatedRigPoints.Count >= 468)
		{
			_stereoFacePipeline.TryStart(delegate
			{
				MediaPipeStereoRigLandmark[] array = new MediaPipeStereoRigLandmark[cameraA.TriangulatedRigPoints.Count];
				for (int i = 0; i < array.Length; i++)
				{
					DualCameraRigPoint dualCameraRigPoint = cameraA.TriangulatedRigPoints[i];
					array[i] = new MediaPipeStereoRigLandmark(i, dualCameraRigPoint.XInches, dualCameraRigPoint.YInches, dualCameraRigPoint.ZInches, dualCameraRigPoint.IsValid, dualCameraRigPoint.ReprojectionResidualPercent, dualCameraRigPoint.CameraADirectnessRatio, dualCameraRigPoint.CameraBDirectnessRatio, dualCameraRigPoint.IsDirectlyMeasured);
				}
				return new MediaPipeStereoGeometryFrame
				{
					CalibrationId = (_savedCalibration?.ReconstructionId ?? ""),
					CapturedAtUtc = cameraA.TargetCapturedAtUtc,
					PairSkew = diagnostics.PairSkew,
					BaselineInches = baselineInches,
					FrameReprojectionResidualPercent = diagnostics.RootMeanSquareResidualPercent,
					CameraATrackingConfidence = diagnostics.CameraATrackingConfidence,
					CameraBTrackingConfidence = diagnostics.CameraBTrackingConfidence,
					Landmarks = array,
					ImagePair = CreateStereoImagePair(diagnostics.DenseStereoSource)
				};
			});
		}
	}

	private static MediaPipeStereoImagePair? CreateStereoImagePair(DualCameraDenseStereoSource? source)
	{
		DualCameraDenseStereoSource? denseSource = source;
		byte[]? array = denseSource?.CameraA.BgraPixels;
		if (denseSource is not null && array is { Length: > 0 })
		{
			byte[]? bgraPixels = denseSource.CameraB.BgraPixels;
			if (bgraPixels is { Length: > 0 } && denseSource.CameraA.BgraStride > 0 && denseSource.CameraB.BgraStride > 0)
			{
				return new MediaPipeStereoImagePair
				{
					CameraAWidth = denseSource.CameraA.FrameWidth,
					CameraAHeight = denseSource.CameraA.FrameHeight,
					CameraAStride = denseSource.CameraA.BgraStride,
					CameraABgraPixels = array,
					CameraBWidth = denseSource.CameraB.FrameWidth,
					CameraBHeight = denseSource.CameraB.FrameHeight,
					CameraBStride = denseSource.CameraB.BgraStride,
					CameraBBgraPixels = bgraPixels,
					CameraALandmarks = CreateStereoImageLandmarks(denseSource.CameraA.Landmarks),
					CameraBLandmarks = CreateStereoImageLandmarks(denseSource.CameraB.Landmarks),
					CalibrationWidth = denseSource.Calibration.ImageWidth,
					CalibrationHeight = denseSource.Calibration.ImageHeight,
					CameraAMatrix = denseSource.Calibration.CameraAMatrix,
					CameraADistortion = denseSource.Calibration.CameraADistortion,
					CameraBMatrix = denseSource.Calibration.CameraBMatrix,
					CameraBDistortion = denseSource.Calibration.CameraBDistortion,
					CameraAToBRotation = denseSource.Calibration.CameraAToBRotation,
					CameraAToBTranslationInches = denseSource.Calibration.CameraAToBTranslationInches,
					FundamentalMatrix = denseSource.Calibration.FundamentalMatrix
				};
			}
		}
		return null;
	}

	private static MediaPipeStereoImageLandmark[] CreateStereoImageLandmarks(IReadOnlyList<DualCameraLandmark> source)
	{
		MediaPipeStereoImageLandmark[] array = new MediaPipeStereoImageLandmark[source.Count];
		for (int i = 0; i < array.Length; i++)
		{
			DualCameraLandmark dualCameraLandmark = source[i];
			array[i] = new MediaPipeStereoImageLandmark(dualCameraLandmark.X, dualCameraLandmark.Y, dualCameraLandmark.IsValid);
		}
		return array;
	}

	private void StereoFaceModelUpdated(object? sender, MediaPipeStereoModelUpdatedEventArgs e)
	{
		_currentStereoFaceModel = e.Model;
		base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
		{
			if (!_shutdownStarted)
			{
				StereoReconstructionStatusText.Text = CreateStereoModelStatus(e.Model) + $" | worker {e.ProcessingDuration.TotalMilliseconds:0} ms | busy drops {e.BusyDropCount:n0}";
				ViewStereoFaceButton.IsEnabled = e.Model.HasGeometry;
				ViewRawStereoPointsButton.IsEnabled = e.Model.RawPointBinCount > 0;
				ViewProbabilityFaceButton.IsEnabled = e.Model.RawPointBinCount >= 100;
			}
		});
	}

	private void StereoFaceProcessingFailed(object? sender, string message)
	{
		base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
		{
			if (!_shutdownStarted)
			{
				StereoReconstructionStatusText.Text = message;
			}
		});
	}

	private async Task ConfigureStereoFacePipelineAsync()
	{
		if (string.IsNullOrWhiteSpace(_profileFolder) || string.IsNullOrWhiteSpace(_subjectId))
		{
			StereoReconstructionToggle.IsEnabled = false;
			ViewStereoFaceButton.IsEnabled = false;
			ViewRawStereoPointsButton.IsEnabled = false;
			ViewProbabilityFaceButton.IsEnabled = false;
			StereoReconstructionStatusText.Text = "Log in to an avatar profile in the main window to own persistent stereo geometry.";
		}
		else
		{
			_currentStereoFaceModel = await _stereoFacePipeline.ConfigureProfileAsync(_profileFolder, _subjectId, _subjectDisplayName);
			StereoReconstructionToggle.IsEnabled = true;
			ViewStereoFaceButton.IsEnabled = _currentStereoFaceModel.HasGeometry;
			ViewRawStereoPointsButton.IsEnabled = _currentStereoFaceModel.RawPointBinCount > 0;
			ViewProbabilityFaceButton.IsEnabled = _currentStereoFaceModel.RawPointBinCount >= 100;
			StereoReconstructionStatusText.Text = CreateStereoModelStatus(_currentStereoFaceModel);
		}
	}

	private async void ViewStereoFaceClicked(object sender, RoutedEventArgs e)
	{
		try
		{
			_currentStereoFaceModel = await _stereoFacePipeline.FlushAsync(writeViewers: true);
			Process.Start(new ProcessStartInfo(MediaPipeStereoFaceStore.GetViewerPath(_profileFolder))
			{
				UseShellExecute = true
			});
			StereoReconstructionStatusText.Text = "Opened calibrated 3D face for " + _subjectDisplayName + ".";
		}
		catch (Exception ex)
		{
			StereoReconstructionStatusText.Text = "Could not open calibrated 3D face: " + ex.Message;
		}
	}

	private async void ViewRawStereoPointsClicked(object sender, RoutedEventArgs e)
	{
		try
		{
			_currentStereoFaceModel = await _stereoFacePipeline.FlushAsync(writeViewers: true);
			Process.Start(new ProcessStartInfo(MediaPipeStereoFaceStore.GetRawViewerPath(_profileFolder))
			{
				UseShellExecute = true
			});
			StereoReconstructionStatusText.Text = "Opened raw stereo evidence for " + _subjectDisplayName + ".";
		}
		catch (Exception ex)
		{
			StereoReconstructionStatusText.Text = "Could not open raw stereo evidence: " + ex.Message;
		}
	}

	private async void ViewProbabilityFaceClicked(object sender, RoutedEventArgs e)
	{
		ViewProbabilityFaceButton.IsEnabled = false;
		try
		{
			StereoReconstructionStatusText.Text = "Extracting the most likely face surface from repeated stereo evidence...";
			MediaPipeStereoProbabilityFaceModel mediaPipeStereoProbabilityFaceModel = await _stereoFacePipeline.BuildProbabilityFaceAsync();
			if (!mediaPipeStereoProbabilityFaceModel.HasSurface)
			{
				StereoReconstructionStatusText.Text = mediaPipeStereoProbabilityFaceModel.Status;
				return;
			}
			Process.Start(new ProcessStartInfo(MediaPipeStereoFaceStore.GetProbabilityViewerPath(_profileFolder))
			{
				UseShellExecute = true
			});
			StereoReconstructionStatusText.Text = $"Opened probability face with {mediaPipeStereoProbabilityFaceModel.Vertices.Count:n0} vertices and {mediaPipeStereoProbabilityFaceModel.Triangles.Count:n0} measured triangles.";
		}
		catch (Exception ex)
		{
			StereoReconstructionStatusText.Text = "Could not build the probability face: " + ex.Message;
		}
		finally
		{
			ViewProbabilityFaceButton.IsEnabled = _currentStereoFaceModel.RawPointBinCount >= 100;
		}
	}

	private static string CreateStereoModelStatus(MediaPipeStereoFaceModel model)
	{
		if (model.AcceptedFrameCount == 0L)
		{
			return $"3D face waiting: {model.Status} Rejected pairs: {model.RejectedFrameCount:n0}.";
		}
		return $"3D face: {model.AcceptedFrameCount:n0} frames | dense {model.DenseMeasuredVertexCount:n0}/{model.DenseMaximumVertexCount:n0} ({model.DenseStableVertexPercent:0.#}% stable) | raw {model.RawPointBinCount:n0} bins / {model.RawTriangulatedObservationCount:n0} votes | anchors {model.ConfidentVertexPercent:0.#}% stable | spread {model.MedianVertexDeviationInches:0.000} in | width {model.FaceWidthInches:0.00} in | depth {model.MeasuredDepthInches:0.00} in";
	}

	private void UpdateRegistrationCoordinatorState()
	{
		_registrationCoordinator.SetEnabled(TranslationViewToggle.IsChecked == true || StereoReconstructionToggle.IsChecked == true);
	}

	private void SetStereoReconstructionToggle(bool enabled)
	{
		Volatile.Write(ref _stereoReconstructionEnabled, enabled ? 1 : 0);
		_updatingStereoToggle = true;
		StereoReconstructionToggle.IsChecked = enabled;
		StereoReconstructionToggle.Content = (enabled ? "Stop Building 3D Face" : "Build 3D Face");
		_updatingStereoToggle = false;
	}

	private void RegistrationStatusChanged(string status)
	{
		long timestamp = Stopwatch.GetTimestamp();
		long num = Interlocked.Read(in _lastRegistrationStatusTimestamp);
		if (num != 0L && Stopwatch.GetElapsedTime(num, timestamp) < TimeSpan.FromMilliseconds(500L))
		{
			return;
		}
		Interlocked.Exchange(ref _lastRegistrationStatusTimestamp, timestamp);
		base.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)delegate
		{
			if (!_shutdownStarted && TranslationViewToggle.IsChecked == true)
			{
				TranslationStatusText.Text = status;
			}
		});
	}

	private async Task HandleSelectionChangedAsync(bool laneA)
	{
		DualCameraLane dualCameraLane = (laneA ? _laneA : _laneB);
		bool wasRunning = dualCameraLane.IsRunning;
		if (wasRunning)
		{
			await dualCameraLane.StopAsync();
		}
		await LoadModesAsync(laneA ? CameraAComboBox : CameraBComboBox, laneA ? ModeAComboBox : ModeBComboBox, laneA);
		if (wasRunning)
		{
			await SetLaneStateAsync(laneA, enabled: true);
		}
	}

	private async Task LoadModesAsync(ComboBox cameraComboBox, ComboBox modeComboBox, bool laneA)
	{
		object selectedItem = cameraComboBox.SelectedItem;
		if (!(selectedItem is CameraDevice camera))
		{
			modeComboBox.ItemsSource = new CameraVideoMode[1] { CameraVideoMode.Auto };
			modeComboBox.SelectedIndex = 0;
			return;
		}
		CancellationTokenSource? obj = (laneA ? Interlocked.Exchange(ref _modeCancellationA, new CancellationTokenSource()) : Interlocked.Exchange(ref _modeCancellationB, new CancellationTokenSource()));
		obj?.Cancel();
		obj?.Dispose();
		CancellationTokenSource cancellation = (laneA ? _modeCancellationA : _modeCancellationB)
			?? throw new InvalidOperationException("Mode discovery cancellation source was not initialized.");
		try
		{
			SetLaneStatus(laneA, (laneA ? "Camera A" : "Camera B") + ": loading modes for " + camera.Name + ".");
			IReadOnlyList<CameraVideoMode> readOnlyList = await (laneA ? _modeServiceA : _modeServiceB).GetModesAsync(camera, cancellation.Token);
			if (!cancellation.IsCancellationRequested)
			{
				modeComboBox.ItemsSource = readOnlyList;
				modeComboBox.SelectedItem = ChoosePreferredMode(camera, readOnlyList);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			modeComboBox.ItemsSource = new CameraVideoMode[1] { CameraVideoMode.Auto };
			modeComboBox.SelectedIndex = 0;
			SetLaneStatus(laneA, "Mode discovery failed: " + ex2.Message);
		}
	}

	private async Task RestartLaneAsync(bool laneA)
	{
		await (laneA ? _laneA : _laneB).StopAsync();
		await SetLaneStateAsync(laneA, enabled: true);
	}

	private void ScheduleLaneRecovery(bool laneA, string reason)
	{
		if (!_shutdownStarted)
		{
			_ = RecoverLaneAsync(laneA, reason);
		}
	}

	private async Task RecoverLaneAsync(bool laneA, string reason)
	{
		if (laneA)
		{
			if (Interlocked.CompareExchange(ref _laneARecoveryInProgress, 1, 0) != 0)
			{
				return;
			}
		}
		else if (Interlocked.CompareExchange(ref _laneBRecoveryInProgress, 1, 0) != 0)
		{
			return;
		}
		DualCameraLane lane = (laneA ? _laneA : _laneB);
		ComboBox cameraCombo = (laneA ? CameraAComboBox : CameraBComboBox);
		ComboBox modeCombo = (laneA ? ModeAComboBox : ModeBComboBox);
		ToggleButton toggle = (laneA ? CameraAToggle : CameraBToggle);
		try
		{
			if (_shutdownStarted || !lane.IsRunning)
			{
				return;
			}
			toggle.IsEnabled = false;
			cameraCombo.IsEnabled = false;
			modeCombo.IsEnabled = false;
			SetLaneStatus(laneA, (laneA ? "Camera A" : "Camera B") + ": " + reason + "; recovering...");
			await lane.StopAsync();
			if (!_shutdownStarted)
			{
				object selectedItem = cameraCombo.SelectedItem;
				if (selectedItem is CameraDevice camera && modeCombo.SelectedItem is CameraVideoMode mode)
				{
					await StartLaneWithRetryAsync(laneA, lane, camera, mode);
					SetToggle(toggle, enabled: true);
					SetLaneStatus(laneA, (laneA ? "Camera A" : "Camera B") + ": recovered " + camera.Name + ".");
					return;
				}
			}
			SetToggle(toggle, enabled: false);
		}
		catch (Exception ex)
		{
			SetToggle(toggle, enabled: false);
			SetLaneStatus(laneA, "Automatic camera recovery failed: " + ex.Message);
		}
		finally
		{
			toggle.IsEnabled = true;
			cameraCombo.IsEnabled = true;
			modeCombo.IsEnabled = true;
			if (laneA)
			{
				Interlocked.Exchange(ref _laneARecoveryInProgress, 0);
			}
			else
			{
				Interlocked.Exchange(ref _laneBRecoveryInProgress, 0);
			}
		}
	}

	private async Task SetLaneStateAsync(bool laneA, bool enabled)
	{
		DualCameraLane dualCameraLane = (laneA ? _laneA : _laneB);
		ComboBox cameraCombo = (laneA ? CameraAComboBox : CameraBComboBox);
		ComboBox modeCombo = (laneA ? ModeAComboBox : ModeBComboBox);
		ToggleButton toggle = (laneA ? CameraAToggle : CameraBToggle);
		DualCameraLane dualCameraLane2 = (laneA ? _laneB : _laneA);
		ComboBox comboBox = (laneA ? CameraBComboBox : CameraAComboBox);
		if (!enabled)
		{
			if (StereoReconstructionToggle.IsChecked == true)
			{
				SetStereoReconstructionToggle(enabled: false);
				StereoReconstructionStatusText.Text = "3D face capture stopped because a camera was turned off.";
				UpdateRegistrationCoordinatorState();
			}
			if (_calibrationCoordinator.IsEnabled)
			{
				_calibrationCoordinator.Stop("Calibration stopped because a camera was turned off.");
				_laneA.SetCalibrationCaptureEnabled(enabled: false);
				_laneB.SetCalibrationCaptureEnabled(enabled: false);
				SetCalibrationToggle(enabled: false);
			}
			toggle.IsEnabled = false;
			try
			{
				await dualCameraLane.StopAsync();
				SetToggle(toggle, enabled: false);
				SetLaneStatus(laneA, (laneA ? "Camera A" : "Camera B") + ": camera off.");
				return;
			}
			finally
			{
				toggle.IsEnabled = true;
			}
		}
		object selectedItem = cameraCombo.SelectedItem;
		if (!(selectedItem is CameraDevice camera) || !(modeCombo.SelectedItem is CameraVideoMode mode))
		{
			SetToggle(toggle, enabled: false);
			SetLaneStatus(laneA, "Select a camera and mode first.");
			return;
		}
		if (dualCameraLane2.IsRunning && comboBox.SelectedItem is CameraDevice right && SamePhysicalCamera(camera, right))
		{
			SetToggle(toggle, enabled: false);
			SetLaneStatus(laneA, camera.Name + " is already active in the other viewport.");
			return;
		}
		toggle.IsEnabled = false;
		toggle.Content = "Starting...";
		cameraCombo.IsEnabled = false;
		modeCombo.IsEnabled = false;
		try
		{
			await StartLaneWithRetryAsync(laneA, dualCameraLane, camera, mode);
			SetToggle(toggle, enabled: true);
		}
		catch (Exception ex)
		{
			SetToggle(toggle, enabled: false);
			SetLaneStatus(laneA, "Could not start " + camera.Name + ": " + ex.Message);
		}
		finally
		{
			toggle.IsEnabled = true;
			cameraCombo.IsEnabled = true;
			modeCombo.IsEnabled = true;
		}
	}

	private async Task StartLaneWithRetryAsync(bool laneA, DualCameraLane lane, CameraDevice camera, CameraVideoMode mode)
	{
		await WaitForCameraReleaseAsync(lane, camera);
		try
		{
			await lane.StartAsync(camera, mode);
		}
		catch (Exception exception) when (IsPocketCamera(camera) && IsTransientCameraStartFailure(exception) && !_shutdownStarted)
		{
			SetLaneStatus(laneA, camera.Name + " is still releasing; retrying once...");
			await Task.Delay(PocketCameraRetryDelay);
			await lane.StartAsync(camera, mode);
		}
	}

	private static async Task WaitForCameraReleaseAsync(DualCameraLane lane, CameraDevice camera)
	{
		TimeSpan cooldown = (IsPocketCamera(camera) ? PocketCameraReleaseCooldown : DefaultCameraReleaseCooldown);
		TimeSpan remainingReleaseCooldown = lane.GetRemainingReleaseCooldown(cooldown);
		if (remainingReleaseCooldown > TimeSpan.Zero)
		{
			await Task.Delay(remainingReleaseCooldown);
		}
	}

	private void SetToggle(ToggleButton toggle, bool enabled)
	{
		_updatingToggles = true;
		toggle.IsChecked = enabled;
		toggle.Content = (enabled ? "Camera On" : "Camera Off");
		_updatingToggles = false;
	}

	private void SetCalibrationToggle(bool enabled)
	{
		_updatingCalibrationToggle = true;
		PhysicalCalibrationToggle.IsChecked = enabled;
		PhysicalCalibrationToggle.Content = (enabled ? "Stop Physical Calibration" : "Start Physical Calibration");
		_updatingCalibrationToggle = false;
	}

	private void SetLaneStatus(bool laneA, string status)
	{
		(laneA ? CameraAStatusText : CameraBStatusText).Text = status;
	}

	private void ApplyMatchingSavedCalibration()
	{
		if (_savedCalibration is null)
		{
			_registrationCoordinator.SetPhysicalCalibration(null);
			return;
		}
		if (CameraAComboBox.SelectedItem is CameraDevice cameraDevice && CameraBComboBox.SelectedItem is CameraDevice cameraDevice2 && string.Equals(cameraDevice.Name, _savedCalibration.CameraAName, StringComparison.OrdinalIgnoreCase) && string.Equals(cameraDevice2.Name, _savedCalibration.CameraBName, StringComparison.OrdinalIgnoreCase))
		{
			_registrationCoordinator.SetPhysicalCalibration(_savedCalibration);
			CalibrationStatusText.Text = $"Saved calibration active: {_savedCalibration.CalibratedAtUtc.ToLocalTime():g} | baseline {_savedCalibration.BaselineInches:0.00} in | stereo RMS {_savedCalibration.StereoReprojectionErrorPixels:0.00} px";
			return;
		}
		_registrationCoordinator.SetPhysicalCalibration(null);
		if (StereoReconstructionToggle.IsChecked == true)
		{
			SetStereoReconstructionToggle(enabled: false);
			UpdateRegistrationCoordinatorState();
		}
		CalibrationStatusText.Text = $"Saved calibration belongs to A: {_savedCalibration.CameraAName} and B: {_savedCalibration.CameraBName}. Select that same order or calibrate this pair.";
	}

	private bool HasMatchingSavedCalibration()
	{
		if (_savedCalibration is not null && CameraAComboBox.SelectedItem is CameraDevice cameraDevice && CameraBComboBox.SelectedItem is CameraDevice cameraDevice2 && string.Equals(cameraDevice.Name, _savedCalibration.CameraAName, StringComparison.OrdinalIgnoreCase))
		{
			return string.Equals(cameraDevice2.Name, _savedCalibration.CameraBName, StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private async void WindowClosing(object? sender, CancelEventArgs e)
	{
		if (_shutdownCompleted)
		{
			return;
		}
		e.Cancel = true;
		if (_shutdownStarted)
		{
			return;
		}
		_shutdownStarted = true;
		base.IsEnabled = false;
		_registrationCoordinator.SetEnabled(enabled: false);
		_calibrationCoordinator.Stop("Physical calibration stopped during shutdown.");
		WorkspaceStatusText.Text = "Stopping both camera lanes...";
		_modeCancellationA?.Cancel();
		_modeCancellationB?.Cancel();
		try
		{
			InlineArray5<Task> buffer = default(InlineArray5<Task>);
			buffer[0] = _calibrationCoordinator.DisposeAsync().AsTask();
			buffer[1] = _registrationCoordinator.DisposeAsync().AsTask();
			buffer[2] = _stereoFacePipeline.DisposeAsync().AsTask();
			buffer[3] = _laneA.DisposeAsync().AsTask();
			buffer[4] = _laneB.DisposeAsync().AsTask();
			await Task.WhenAll(buffer);
		}
		catch (Exception ex)
		{
			WorkspaceStatusText.Text = "Camera shutdown completed with an error: " + ex.Message;
		}
		finally
		{
			_modeCancellationA?.Dispose();
			_modeCancellationB?.Dispose();
			_shutdownCompleted = true;
			_ = base.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(base.Close));
		}
	}

	private void CloseClicked(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private static CameraDevice? FindPreferredCamera(IReadOnlyList<CameraDevice> cameras, string name)
	{
		return cameras.FirstOrDefault((CameraDevice camera) => camera.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
	}

	private static CameraVideoMode ChoosePreferredMode(CameraDevice camera, IReadOnlyList<CameraVideoMode> modes)
	{
		return (from mode in modes
			where mode.Width == 3840 && mode.Height == 2160
			orderby Math.Abs((mode.FramesPerSecond ?? 30.0) - 30.0), (!IsMjpeg(mode.InputFormat)) ? 1 : 0
			select mode).FirstOrDefault() ?? modes.FirstOrDefault((CameraVideoMode mode) => !mode.IsAuto) ?? CameraVideoMode.Auto;
	}

	private static bool IsMjpeg(string? inputFormat)
	{
		if (inputFormat == null || !inputFormat.Contains("mjpg", StringComparison.OrdinalIgnoreCase))
		{
			return inputFormat?.Contains("mjpeg", StringComparison.OrdinalIgnoreCase) ?? false;
		}
		return true;
	}

	private static bool IsPocketCamera(CameraDevice camera)
	{
		if (!camera.Name.Contains("OsmoPocket3", StringComparison.OrdinalIgnoreCase))
		{
			return camera.Name.Contains("Pocket 3", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static bool IsTransientCameraStartFailure(Exception exception)
	{
		for (Exception? ex = exception; ex != null; ex = ex.InnerException)
		{
			if (ex is TimeoutException || ex.Message.Contains("did not initialize", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("No DX12 texture frames arrived", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private static bool SamePhysicalCamera(CameraDevice left, CameraDevice right)
	{
		string? text = CameraDeviceCatalog.TryCreatePhysicalDeviceKey(left);
		string? text2 = CameraDeviceCatalog.TryCreatePhysicalDeviceKey(right);
		if (!string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(text2))
		{
			return string.Equals(text, text2, StringComparison.OrdinalIgnoreCase);
		}
		return string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
	}
}
