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

public partial class MainWindow : Window, IComponentConnector
{
	private sealed record CameraControlBinding(CameraDevice Camera, CameraControlItem Control, TextBlock ValueText, Slider Slider, CheckBox AutoCheckBox);

	private sealed record FaceBoxTrackingFrameResult(
		FaceBoxSystem FaceBoxSystem,
		int FaceBoxSystemGeneration,
		FaceLandmarkTrackingResult TrackingResult,
		ThreeDdfaOnnxSidecarResponse? ThreeDdfaResponse,
		long CapturedAtTimestamp,
		DateTime CapturedAtUtc,
		int SourceWidth,
		int SourceHeight,
		BitmapSource? SourceFrame);

	private sealed record FaceAnalysisResult(FaceLandmarkFrame RawLandmarkFrame, FaceLandmarkFrame ReconstructedLandmarkFrame, FaceLandmarkMetrics Metrics, FaceLockStabilityAnalysis Stability);

	private readonly record struct AvatarLearningState(string Title, string Detail, Brush Accent);

	private sealed record AvatarLoginSelection(string ProfileId, string NewDisplayName);

	private const string DefaultAvatarProfileId = "chris";

	private const string DefaultAvatarProfileDisplayName = "Chris";

	private const string PreferredExternalOutputFolder = "D:\\Avatar Builder Output";

	private const string OutputFolderPointerFileName = "AvatarBuilderOutputFolder.txt";

	private const string AvatarLearningStartButtonText = "Start Avatar Capture";

	private const string AvatarLearningStopButtonText = "Stop Avatar Capture";

	private const string ShutdownLogFileName = "AvatarBuilder-shutdown.log";

	private const double Insta360Link2ProHorizontalFovDegrees = 71.4;

	private static readonly TimeSpan MaximumLiveAwarenessFrameAge = TimeSpan.FromMilliseconds(250L);

	private static readonly TimeSpan RecoverableVisionErrorStatusInterval = TimeSpan.FromSeconds(3L);

	private static readonly SolidColorBrush StartActionButtonBackground = CreateFrozenBrush(31, 122, 67);

	private static readonly SolidColorBrush StartActionButtonBorder = CreateFrozenBrush(82, 196, 123);

	private static readonly SolidColorBrush StopActionButtonBackground = CreateFrozenBrush(157, 47, 47);

	private static readonly SolidColorBrush StopActionButtonBorder = CreateFrozenBrush(224, 105, 105);

	private static readonly SolidColorBrush AvatarStoppedBrush = CreateFrozenBrush(89, 97, 107);

	private static readonly SolidColorBrush AvatarWaitingBrush = CreateFrozenBrush(215, 165, 58);

	private static readonly SolidColorBrush AvatarActiveBrush = CreateFrozenBrush(74, 163, 107);

	private static readonly SolidColorBrush AvatarLoginActiveBrush = CreateFrozenBrush(128, 224, 164);

	private static readonly SolidColorBrush AvatarLoginInactiveBrush = CreateFrozenBrush(byte.MaxValue, 207, 122);

	private static readonly SolidColorBrush GuidanceGoodBrush = CreateFrozenBrush(128, 224, 164);

	private static readonly SolidColorBrush GuidanceWarningBrush = CreateFrozenBrush(byte.MaxValue, 210, 122);

	private static readonly SolidColorBrush GuidanceBlockedBrush = CreateFrozenBrush(byte.MaxValue, 154, 154);

	private static readonly SolidColorBrush GuidanceIdleBrush = CreateFrozenBrush(185, 215, 239);

	private static readonly BitmapSource EmptyBgraBitmap = CreateEmptyBgraBitmap();

	private static readonly SolidColorBrush FaceMeshOverlayBrush = CreateFrozenBrush(53, 155, 193, 120);

	private static readonly SolidColorBrush FaceMeshOverlayPointBrush = CreateFrozenBrush(220, 239, byte.MaxValue, 184);

	private static readonly SolidColorBrush LiveWireframeMeshBrush = CreateFrozenBrush(47, 108, 143, 88);

	private static readonly SolidColorBrush WireframeEyeBrush = CreateFrozenBrush(143, 242, 197, 242);

	private static readonly SolidColorBrush WireframeBrowBrush = CreateFrozenBrush(201, 247, 163, 242);

	private static readonly SolidColorBrush WireframeMouthBrush = CreateFrozenBrush(byte.MaxValue, 159, 189, 242);

	private static readonly SolidColorBrush WireframeJawBrush = CreateFrozenBrush(byte.MaxValue, 209, 102, 242);

	private static readonly SolidColorBrush WireframeNoseBrush = CreateFrozenBrush(217, 232, byte.MaxValue, 242);

	private static readonly SolidColorBrush WireframeFaceBrush = CreateFrozenBrush(101, 200, byte.MaxValue, 242);

	private static readonly SolidColorBrush WireframeDefaultBrush = CreateFrozenBrush(220, 239, byte.MaxValue, 224);

	private readonly FfmpegCameraModeService _cameraModeService = new FfmpegCameraModeService();

	private readonly DirectShowCameraControlService _cameraControlService = new DirectShowCameraControlService();

	private readonly CameraPreviewService _previewService = new CameraPreviewService();

	private CompositeFaceLandmarkTracker? _faceLandmarkTracker = new CompositeFaceLandmarkTracker(MediaPipeExecutionBackend.Gpu);

	private MediaPipeDirectMlTextureTracker? _directMlTextureTracker;

	private int _directMlTextureTrackerCameraGeneration = -1;

	private MediaPipeExecutionBackend _mediaPipeExecutionBackend = MediaPipeExecutionBackend.Gpu;

	private readonly FaceLandmarkTemporalReconstructor _faceLandmarkReconstructor = new FaceLandmarkTemporalReconstructor();

	private readonly FaceLandmarkMetricCalculator _faceLandmarkMetricCalculator = new FaceLandmarkMetricCalculator();

	private readonly FaceLockStabilityAnalyzer _faceLockStabilityAnalyzer = new FaceLockStabilityAnalyzer();

	private readonly ThreeDdfaOnnxModelInfo _threeDdfaOnnxModelInfo;

	private readonly ThreeDdfaOnnxSidecarEnvironment _threeDdfaOnnxEnvironment;

	private ThreeDdfaOnnxReconstructionClient? _threeDdfaAvatarClient;

	private ThreeDdfaOnnxReconstructionClient? _threeDdfaFaceBoxClient;

	private readonly AvatarBuilderStartupOptions _startupOptions;

	private readonly AvatarProfileStore _avatarProfileStore = new AvatarProfileStore();

	private readonly AvatarUserSession _avatarUserSession = new AvatarUserSession();

	private readonly AvatarCaptureQualityAnalyzer _avatarCaptureQualityAnalyzer = new AvatarCaptureQualityAnalyzer();

	private readonly PersonIdentityMemory _personIdentityMemory;

	private readonly MediaPipeGeometryPipeline _mediaPipeGeometryPipeline = new MediaPipeGeometryPipeline();

	private readonly LatestTextureFrameWorker _directX12AnalysisWorker;

	private readonly LatestTextureFrameWorker _personIdentityWorker;

	private readonly LatestCameraFrameWorker _personIdentityCameraFrameWorker;

	private readonly object _faceLandmarkTrackerLock = new object();

	private readonly object _faceAnalysisStateLock = new object();

	private int _faceAnalysisGeneration;

	private readonly object _threeDdfaClientLock = new object();

	private readonly object _directX12PreviewLock = new object();

	private readonly SemaphoreSlim _cameraLifecycleGate = new SemaphoreSlim(1, 1);

	private readonly DispatcherTimer _cameraHealthTimer;

	private IReadOnlyList<CameraDevice> _cameras = Array.Empty<CameraDevice>();

	private string _preferredCameraDevicePath = string.Empty;

	private string _preferredCameraName = string.Empty;

	private CancellationTokenSource? _modeLoadCancellation;

	private CancellationTokenSource? _cameraStartCancellation;

	private string _outputFolder;

	private BitmapSource? _latestFrame;

	private FaceFeatureDetection _currentFaceFeatureDetection = FaceFeatureDetection.None;

	private FaceLandmarkFrame _currentFaceLandmarkFrame = FaceLandmarkFrame.None;

	private FaceFeatureDetection? _cachedNativeOverlayFeatureDetection;

	private FaceLandmarkFrame? _cachedNativeOverlayLandmarkFrame;

	private PreviewTrackingOverlay _cachedNativeTrackingOverlay = PreviewTrackingOverlay.Empty;

	private FaceLandmarkMetrics _currentFaceLandmarkMetrics = FaceLandmarkMetrics.None;

	private FaceLockStabilityAnalysis _currentFaceLockStabilityAnalysis = FaceLockStabilityAnalysis.Waiting;

	private ThreeDdfaOnnxSidecarResponse _currentThreeDdfaOnnxResponse = ThreeDdfaOnnxSidecarResponse.Waiting;

	private MediaPipeNormalizedFaceModel _currentMediaPipeGeometryModel = MediaPipeNormalizedFaceModel.Empty;

	private ThreeDdfaOnnxSidecarFaceBox? _threeDdfaTrackingFaceBox;

	private AvatarCaptureQualityAssessment _currentAvatarCaptureQuality = AvatarCaptureQualityAssessment.Waiting;

	private AvatarProfileRegistry _avatarProfileRegistry = new AvatarProfileRegistry();

	private AvatarProfile _currentAvatarProfile = new AvatarProfile
	{
		Id = "chris",
		DisplayName = "Chris",
		DataFolderName = ""
	};

	private Direct3D12PreviewHost? _directX12PreviewHost;

	private Dx12Camera? _directX12NativeCamera;

	private long _cameraStartedTimestamp;

	private long _lastCameraSourceFrameTimestamp;

	private long _lastCameraPresentedFrameTimestamp;

	private long _nextCameraRecoveryTimestamp;

	private TimeSpan? _lastMediaPipeGeometryProcessingDuration;

	private long _lastAvatarStatusUiRefreshTimestamp;

	private string _lastFaceBoxBackendStatus = "waiting";

	private double _directX12PreviewMaxRenderFramesPerSecond;

	private int _uiFrameInFlight;

	private int _faceFeatureDetectionInFlight;

	private int _faceTrackingEnabled = 1;

	private int _mediaPipeGeometryUiUpdateInFlight;

	private int _faceResultUiUpdateInFlight;

	private long _lastRecoverableVisionErrorStatusTimestamp;

	private long _directX12FrameNumber;

	private long _directX12AnalysisCompletedFrames;

	private long _lastPipelineDiagnosticsTimestamp;

	private long _lastPipelineDiagnosticsSourceFrames;

	private long _lastPipelineDiagnosticsAnalysisFrames;

	private long _lastPersonIdentityObservationTimestamp;

	private double _measuredCameraIngestionFramesPerSecond;

	private double _measuredAnalysisFramesPerSecond;

	private int _directX12AnalysisInFlight;

	private int _consecutiveCameraRecoveryAttempts;

	private bool _avatarLearningRequested;

	private bool _avatarModelInitializationPending;

	private bool _avatarModelReadyForCapture;

	private bool _showFaceMeshOverlay;

	private bool _showLiveWireframePreview;

	private bool _cachedNativeOverlayIncludesFaceMesh;

	private bool _isDirectX12PreviewEnabled = true;

	private bool _isCameraEnabled;

	private bool _cameraShouldRun;

	private bool _cameraRecoveryPending;

	private bool _isUpdatingCameraToggle;

	private bool _isRefreshingCameras;

	private bool _isLoadingCameraControls;

	private bool _isUpdatingCameraControlUi;

	private bool _isSnappingSlider;

	private bool _isClosing;

	private bool _shutdownStarted;

	private bool _shutdownCompleted;

	private bool _startupOptionsApplied;

	private int _previewActivationGeneration;

	private int _cameraLifecycleGeneration;

	private int _faceBoxSystemGeneration;

	private FaceBoxSystem _selectedFaceBoxSystem;

	private FaceCueGuideLayout? _activeFaceCueLayout;

	private long _lastFaceAutoFollowTimestamp;

	private long _lastFaceFeatureLockTimestamp;

	private DateTime _lastThreeDdfaFaceBoxesAtUtc = DateTime.MinValue;

	private static readonly TimeSpan CameraStallTimeout = TimeSpan.FromSeconds(6L);

	private static readonly TimeSpan CameraPresentationStallTimeout =
		TimeSpan.FromSeconds(4L);

	private static readonly TimeSpan CameraRecoveryMaximumBackoff =
		TimeSpan.FromSeconds(5L);

	private static readonly TimeSpan CameraRecoveryNativeReleaseWait =
		TimeSpan.FromMilliseconds(750L);

	private Task _pendingNativeCameraDisposal = Task.CompletedTask;

	private static readonly long AvatarStatusUiRefreshIntervalTicks = Stopwatch.Frequency;

	private string CurrentAvatarProfileId
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(_currentAvatarProfile.Id))
			{
				return _currentAvatarProfile.Id;
			}
			return "chris";
		}
	}

	private string CurrentAvatarProfileDisplayName
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(_currentAvatarProfile.DisplayName))
			{
				return _currentAvatarProfile.DisplayName;
			}
			return "Chris";
		}
	}

	private bool IsAvatarUserLoggedIn
	{
		get
		{
			if (_avatarUserSession.IsLoggedIn)
			{
				return string.Equals(_avatarUserSession.LoggedInProfileId, CurrentAvatarProfileId, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
	}

	private bool IsAvatarReconstructionReady => _mediaPipeGeometryPipeline.IsConfigured;

	private string ActiveAvatarReconstructionBackendId => "mediapipe-geometry-measurement-v1";

	private string ActiveAvatarReconstructionName => "MediaPipe geometry";

	private string ActiveAvatarReconstructionReadinessStatus => _mediaPipeGeometryPipeline.IsConfigured
		? "MediaPipe visible-evidence geometry is active and persistent."
		: "MediaPipe visible-evidence geometry is waiting for an avatar user profile.";

	private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue)
	{
		SolidColorBrush solidColorBrush = new SolidColorBrush(Color.FromRgb(red, green, blue));
		solidColorBrush.Freeze();
		return solidColorBrush;
	}

	private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue, byte alpha)
	{
		SolidColorBrush solidColorBrush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
		solidColorBrush.Freeze();
		return solidColorBrush;
	}

	private static void SetTextIfChanged(TextBlock textBlock, string text)
	{
		if (!string.Equals(textBlock.Text, text, StringComparison.Ordinal))
		{
			textBlock.Text = text;
		}
	}

	private static void SetContentIfChanged(ContentControl control, object content)
	{
		if (!Equals(control.Content, content))
		{
			control.Content = content;
		}
	}

	private static void SetHeaderIfChanged(HeaderedItemsControl control, object header)
	{
		if (!Equals(control.Header, header))
		{
			control.Header = header;
		}
	}

	private static void SetIfChanged(DependencyObject target, DependencyProperty property, object value)
	{
		if (!Equals(target.GetValue(property), value))
		{
			target.SetValue(property, value);
		}
	}

	public MainWindow()
		: this(AvatarBuilderStartupOptions.Default)
	{
	}

	public MainWindow(AvatarBuilderStartupOptions startupOptions)
	{
		_startupOptions = startupOptions ?? AvatarBuilderStartupOptions.Default;
		_threeDdfaOnnxModelInfo = ThreeDdfaOnnxModelInfo.Load();
		_threeDdfaOnnxEnvironment = ThreeDdfaOnnxSidecarEnvironment.Detect(_threeDdfaOnnxModelInfo);
		InitializeComponent();
		_personIdentityMemory = new PersonIdentityMemory();
		_personIdentityWorker = new LatestTextureFrameWorker(
			"Avatar Builder people-memory observation",
			ProcessPersonIdentityFrame,
			ThreadPriority.BelowNormal);
		_personIdentityCameraFrameWorker = new LatestCameraFrameWorker(
			"Avatar Builder people-memory CPU observation",
			ProcessPersonIdentityCameraFrame,
			ThreadPriority.BelowNormal);
		_personIdentityMemory.SnapshotChanged +=
			PersonIdentityMemorySnapshotChanged;
		_directX12AnalysisWorker = new LatestTextureFrameWorker(
			"Avatar Builder latest-frame analysis",
			ProcessDirectX12AnalysisFrame);
		_mediaPipeGeometryPipeline.ModelUpdated += MediaPipeGeometryPipelineModelUpdated;
		_outputFolder = ResolveInitialOutputFolder(_startupOptions.OutputFolder);
		_previewService.FrameAvailable += PreviewFrameAvailable;
		_previewService.CameraFrameAvailable += PreviewCameraFrameAvailable;
		_previewService.StatusChanged += PreviewStatusChanged;
		_cameraHealthTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(1L)
		};
		_cameraHealthTimer.Tick += CameraHealthTimerTick;
	}

	private async void WindowLoaded(object sender, RoutedEventArgs e)
	{
		DarkWindowFrame.Apply(this);
		EnsureOutputFolderConfiguredForLaunch();
		_personIdentityMemory.ConfigureOutputFolder(_outputFolder);
		SetTextIfChanged(
			PeopleMemoryStatusText,
			_personIdentityMemory.Status);
		InitializeAvatarProfiles(!_startupOptions.SkipLoginPrompt);
		UpdateFaceBoxSystemMenuChecks();
		UpdateMediaPipeProcessorMenuChecks();
		UpdateFaceBoxOptionsUi();
		UpdateSettingLabels();
		if (IsAvatarUserLoggedIn)
		{
			await InitializeAvatarModelAfterLoginAsync(showPopup: true);
		}
		PrepareAvatarCaptureFolder(showStatus: false);
		UpdateAvatarLearningStatusUi();
		_cameraHealthTimer.Start();
		await base.Dispatcher.InvokeAsync(RefreshCamerasAsync, DispatcherPriority.ApplicationIdle).Task.Unwrap();
		await base.Dispatcher.InvokeAsync(ApplyStartupOptionsAfterLoad, DispatcherPriority.ApplicationIdle);
	}

	private async void WindowActivated(object? sender, EventArgs e)
	{
		if (!base.IsLoaded || _isClosing)
		{
			return;
		}
		int activationGeneration = Interlocked.Increment(ref _previewActivationGeneration);
		await Task.Delay(150);
		if (_isClosing || !_isCameraEnabled || !base.IsActive || activationGeneration != Volatile.Read(in _previewActivationGeneration))
		{
			return;
		}
			ResumeDirectX12PreviewPresentation();
			if (_isCameraEnabled)
			{
				BitmapSource? latestFrame = _latestFrame;
				UpdateFaceCueGuideOverlay(latestFrame);
			}
	}

	private void ResumeDirectX12PreviewPresentation()
	{
		_directX12NativeCamera?.ResumePreview();
		GetDirectX12PreviewHost()?.ResumeRendering();
	}

	private void ApplyDirectX12PreviewPresentationState()
	{
		ResumeDirectX12PreviewPresentation();
	}

	private void ScheduleDirectX12PreviewWakeAfterCameraStart()
	{
		int activationGeneration = Volatile.Read(in _previewActivationGeneration);
		base.Dispatcher.InvokeAsync((Func<Task>)async delegate
		{
			await Task.Delay(250);
			if (!_isClosing && _isCameraEnabled && activationGeneration == Volatile.Read(in _previewActivationGeneration))
			{
				ResumeDirectX12PreviewPresentation();
			}
		}, DispatcherPriority.Background);
	}

	private void ApplyStartupOptionsAfterLoad()
	{
		if (!_startupOptionsApplied)
		{
			_startupOptionsApplied = true;
			if (_startupOptions.StartAvatarLearning
				&& IsAvatarUserLoggedIn
				&& _avatarModelReadyForCapture)
			{
				AvatarIdentityAuthorization authorization =
					_personIdentityMemory.AuthorizeAvatarCapture(
						CurrentAvatarProfileId,
						CurrentAvatarProfileDisplayName);
				_avatarLearningRequested = authorization.Allowed;
				if (!authorization.Allowed)
				{
					SetStatus(authorization.Status);
				}
			}
			if (_startupOptions.OpenAvatarSystem)
			{
				OpenMediaPipeGeometryClicked(this, new RoutedEventArgs());
			}
			UpdateAvatarLearningStatusUi();
			if (_startupOptions.EasyAvatarMode)
			{
				AvatarCaptureGuidanceState avatarCaptureGuidanceState = GetAvatarCaptureGuidanceState();
				string text = "Avatar capture guidance: " + avatarCaptureGuidanceState.Title + ". " + avatarCaptureGuidanceState.Detail;
				SetStatus(text);
				MonitorStatusText.Text = text;
			}
		}
	}

	private void InitializeAvatarProfiles(bool promptForStartupUser)
	{
		_avatarLearningRequested = false;
		_avatarUserSession.LogOut();
		_avatarProfileRegistry = _avatarProfileStore.Load(_outputFolder);
		AvatarProfile? avatarProfile = null;
		if (promptForStartupUser)
		{
			avatarProfile = ResolveAvatarLoginSelection(PromptForAvatarLogin(_avatarProfileRegistry));
		}
		if (_avatarProfileRegistry.Profiles.Count == 0)
		{
			_avatarProfileStore.AddOrUpdateProfile(_outputFolder, _avatarProfileRegistry, "Chris");
		}
		AvatarProfile? avatarProfile2 = ((avatarProfile == null) ? FindSelectedAvatarProfile() : avatarProfile);
		if (avatarProfile2 != null)
		{
			ApplyCurrentAvatarProfile(avatarProfile2, loadModel: false);
			if (avatarProfile != null)
			{
				LogInAvatarProfile(avatarProfile2, loadModel: false, announce: false);
				return;
			}
			UpdateAvatarSessionUi();
		}
	}

	private AvatarLoginSelection PromptForAvatarLogin(AvatarProfileRegistry registry)
	{
		List<AvatarProfile> list = registry.Profiles.ToList();
		Window window = new Window
		{
			Title = "Avatar User Login",
			Owner = this,
			Width = 430.0,
			SizeToContent = SizeToContent.Height,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			ResizeMode = ResizeMode.NoResize,
			Background = new SolidColorBrush(Color.FromRgb(8, 13, 18)),
			Foreground = Brushes.White
		};
		StackPanel stackPanel = new StackPanel
		{
			Margin = new Thickness(18.0)
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = "Who is in front of the camera?",
			FontSize = 18.0,
			FontWeight = FontWeights.SemiBold,
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0)
		});
		stackPanel.Children.Add(new TextBlock
		{
			Text = "Log in a remembered user, or type a new consenting user's name. Avatar capture remains stopped until login succeeds, and data stays isolated by profile.",
			Foreground = new SolidColorBrush(Color.FromRgb(185, 215, 239)),
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 0.0, 0.0, 14.0)
		});
		ComboBox combo = new ComboBox
		{
			ItemsSource = list,
			DisplayMemberPath = "DisplayName",
			MinHeight = 34.0,
			Style = (TryFindResource(typeof(ComboBox)) as Style),
			ItemContainerStyle = (TryFindResource(typeof(ComboBoxItem)) as Style),
			IsEnabled = (list.Count > 0),
			SelectedItem = (list.FirstOrDefault((AvatarProfile profile) => string.Equals(profile.Id, registry.SelectedProfileId, StringComparison.OrdinalIgnoreCase)) ?? list.OrderByDescending((AvatarProfile profile) => profile.LastSelectedAtUtc ?? DateTime.MinValue).FirstOrDefault())
		};
		stackPanel.Children.Add(combo);
		TextBox nameBox = new TextBox
		{
			MinHeight = 34.0,
			Margin = new Thickness(0.0, 10.0, 0.0, 0.0),
			Style = (TryFindResource(typeof(TextBox)) as Style)
		};
		stackPanel.Children.Add(nameBox);
		TextBlock status = new TextBlock
		{
			Foreground = new SolidColorBrush(Color.FromRgb(byte.MaxValue, 154, 154)),
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
		};
		stackPanel.Children.Add(status);
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0.0, 16.0, 0.0, 0.0)
		};
		Button button = new Button
		{
			Content = "Login",
			MinWidth = 110.0,
			Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
			Style = (TryFindResource(typeof(Button)) as Style)
		};
		Button button2 = new Button
		{
			Content = "Cancel",
			MinWidth = 110.0,
			Style = (TryFindResource(typeof(Button)) as Style)
		};
		stackPanel2.Children.Add(button2);
		stackPanel2.Children.Add(button);
		stackPanel.Children.Add(stackPanel2);
		window.Content = stackPanel;
		AvatarLoginSelection selection = new AvatarLoginSelection("", "");
		button.Click += delegate
		{
			string text = nameBox.Text.Trim();
			if (!string.IsNullOrWhiteSpace(text))
			{
				selection = new AvatarLoginSelection("", text);
				window.DialogResult = true;
			}
			else if (combo.SelectedItem is AvatarProfile avatarProfile)
			{
				selection = new AvatarLoginSelection(avatarProfile.Id, "");
				window.DialogResult = true;
			}
			else
			{
				status.Text = "Type the user's name before continuing.";
			}
		};
		button2.Click += delegate
		{
			window.DialogResult = false;
		};
		if (window.ShowDialog() != true)
		{
			return new AvatarLoginSelection("", "");
		}
		return selection;
	}

	private AvatarProfile? ResolveAvatarLoginSelection(AvatarLoginSelection selection)
	{
		if (!string.IsNullOrWhiteSpace(selection.NewDisplayName))
		{
			string linkedProfileId =
				_personIdentityMemory.GetActiveAvatarProfileId();
			if (!string.IsNullOrWhiteSpace(linkedProfileId))
			{
				AvatarProfile? existing =
					_avatarProfileRegistry.Profiles.FirstOrDefault(profile =>
						string.Equals(
							profile.Id,
							linkedProfileId,
							StringComparison.OrdinalIgnoreCase));
				if (existing is not null)
				{
					return _avatarProfileStore.SelectProfile(
						_outputFolder,
						_avatarProfileRegistry,
						existing.Id);
				}
			}
			return _avatarProfileStore.AddOrUpdateProfile(_outputFolder, _avatarProfileRegistry, selection.NewDisplayName);
		}
		if (!string.IsNullOrWhiteSpace(selection.ProfileId))
		{
			return _avatarProfileStore.SelectProfile(_outputFolder, _avatarProfileRegistry, selection.ProfileId);
		}
		return null;
	}

	private AvatarProfile? FindSelectedAvatarProfile()
	{
		return _avatarProfileRegistry.Profiles.FirstOrDefault((AvatarProfile profile) => string.Equals(profile.Id, _avatarProfileRegistry.SelectedProfileId, StringComparison.OrdinalIgnoreCase)) ?? _avatarProfileRegistry.Profiles.OrderByDescending((AvatarProfile profile) => profile.LastSelectedAtUtc ?? DateTime.MinValue).FirstOrDefault();
	}

	private void ApplyCurrentAvatarProfile(AvatarProfile profile, bool loadModel)
	{
		if (_avatarUserSession.IsLoggedIn && !string.Equals(_avatarUserSession.LoggedInProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))
		{
			_avatarUserSession.LogOut();
		}
		_currentAvatarProfile = profile;
		_avatarLearningRequested = false;
		_avatarProfileStore.SelectProfile(_outputFolder, _avatarProfileRegistry, profile.Id);
		if (loadModel)
		{
			ResetAvatarRuntimeForProfile();
		}
		UpdateAvatarSessionUi();
		UpdateAvatarLearningStatusUi();
	}

	private void LogInAvatarProfile(AvatarProfile profile, bool loadModel, bool announce)
	{
		if (!string.Equals(CurrentAvatarProfileId, profile.Id, StringComparison.OrdinalIgnoreCase) || loadModel)
		{
			ApplyCurrentAvatarProfile(profile, loadModel);
		}
		_avatarLearningRequested = false;
		_avatarModelReadyForCapture = false;
		_avatarUserSession.LogIn(profile.Id);
		_currentThreeDdfaOnnxResponse = ThreeDdfaOnnxSidecarResponse.Waiting;
		_currentAvatarCaptureQuality = AvatarCaptureQualityAssessment.Waiting;
		UpdateAvatarSessionUi();
		UpdateAvatarLearningStatusUi();
		if (announce)
		{
			SetStatus("Logged in as " + CurrentAvatarProfileDisplayName + ". Calculating the stored avatar model before capture starts.");
		}
	}

	private void LogOutAvatarUser(bool announce)
	{
		string currentAvatarProfileDisplayName = CurrentAvatarProfileDisplayName;
		_avatarLearningRequested = false;
		_avatarModelReadyForCapture = false;
		_avatarUserSession.LogOut();
		_currentThreeDdfaOnnxResponse = ThreeDdfaOnnxSidecarResponse.Waiting;
		UpdateAvatarSessionUi();
		UpdateAvatarLearningStatusUi();
		if (announce)
		{
			SetStatus(currentAvatarProfileDisplayName + " logged out. Avatar capture has stopped.");
		}
	}

	private void UpdateAvatarSessionUi()
	{
		bool isAvatarUserLoggedIn = IsAvatarUserLoggedIn;
		SetHeaderIfChanged(LoginLogoutMenuItem, isAvatarUserLoggedIn ? "_Logout " + CurrentAvatarProfileDisplayName : "_Login...");
		SetIfChanged(LoginLogoutMenuItem, MenuItem.IsEnabledProperty, !_avatarModelInitializationPending);
		SetTextIfChanged(AvatarLoginStatusText, (!isAvatarUserLoggedIn) ? "No avatar user logged in. Use File > Login to begin a capture session." : (_avatarModelInitializationPending ? ("Logged in as " + CurrentAvatarProfileDisplayName + ". Please wait while measured face geometry is loaded.") : ((!_avatarModelReadyForCapture) ? ("Logged in as " + CurrentAvatarProfileDisplayName + ", but measured geometry is not ready. Log out and back in to retry.") : ("Logged in as " + CurrentAvatarProfileDisplayName + ". MediaPipe measured geometry is ready; Avatar Capture can be started."))));
		SetIfChanged(AvatarLoginStatusText, TextBlock.ForegroundProperty, isAvatarUserLoggedIn ? AvatarLoginActiveBrush : AvatarLoginInactiveBrush);
		SetIfChanged(AvatarLearningToggleButton, Button.IsEnabledProperty, _avatarLearningRequested || (isAvatarUserLoggedIn && _avatarModelReadyForCapture && !_avatarModelInitializationPending));
	}

	private async Task InitializeAvatarModelAfterLoginAsync(bool showPopup)
	{
		if (!IsAvatarUserLoggedIn || _avatarModelInitializationPending)
		{
			return;
		}
		string profileId = CurrentAvatarProfileId;
		_avatarModelInitializationPending = true;
		_avatarModelReadyForCapture = false;
		UpdateAvatarSessionUi();
		UpdateAvatarLearningStatusUi();
		Window? progressWindow = null;
		if (showPopup)
		{
			progressWindow = CreateAvatarModelInitializationWindow();
			progressWindow.Show();
		}
		try
		{
			string folder = GetAvatarDataFolder();
			Directory.CreateDirectory(folder);
			_currentMediaPipeGeometryModel = await _mediaPipeGeometryPipeline.ConfigureProfileAsync(folder, CurrentAvatarProfileId, CurrentAvatarProfileDisplayName);
			if (!_isClosing && IsAvatarUserLoggedIn && string.Equals(profileId, CurrentAvatarProfileId, StringComparison.Ordinal))
			{
				_avatarModelReadyForCapture = true;
				SetStatus(CurrentAvatarProfileDisplayName + "'s measured MediaPipe geometry is loaded. Avatar capture is ready.");
			}
		}
		catch (Exception ex)
		{
			_avatarModelReadyForCapture = false;
			SetStatus("Could not calculate the stored avatar model: " + ex.Message);
		}
		finally
		{
			_avatarModelInitializationPending = false;
			if (progressWindow != null && !_isClosing)
			{
				progressWindow.Close();
				Activate();
			}
			UpdateAvatarSessionUi();
			UpdateAvatarLearningStatusUi();
		}
	}

	private Window CreateAvatarModelInitializationWindow()
	{
		Window window = new Window();
		window.Title = "Preparing Avatar Model";
		window.Owner = this;
		window.Width = 430.0;
		window.Height = 170.0;
		window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
		window.ResizeMode = ResizeMode.NoResize;
		window.ShowInTaskbar = false;
		window.Background = new SolidColorBrush(Color.FromRgb(8, 13, 18));
		window.Foreground = Brushes.White;
		window.Closing += delegate(object? _, CancelEventArgs args)
		{
			args.Cancel = _avatarModelInitializationPending && !_isClosing;
		};
		window.Content = new StackPanel
		{
			Margin = new Thickness(22.0),
			Children = 
			{
				(UIElement)new TextBlock
				{
					Text = "Loading Measured Face Geometry",
					FontSize = 18.0,
					FontWeight = FontWeights.SemiBold,
					Margin = new Thickness(0.0, 0.0, 0.0, 12.0)
				},
				(UIElement)new TextBlock
				{
					Text = "Please wait while the saved MediaPipe measurements and dense warp data are loaded for this user.",
					Foreground = new SolidColorBrush(Color.FromRgb(185, 215, 239)),
					TextWrapping = TextWrapping.Wrap
				}
			}
		};
		return window;
	}

	private void ResetAvatarRuntimeForProfile()
	{
		_currentAvatarCaptureQuality = AvatarCaptureQualityAssessment.Waiting;
		_currentThreeDdfaOnnxResponse = ThreeDdfaOnnxSidecarResponse.Waiting;
		_currentMediaPipeGeometryModel = MediaPipeNormalizedFaceModel.Empty;
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
		_isClosing = true;
		_cameraShouldRun = false;
		Hide();
		WriteShutdownTrace("Shutdown started.");
		TryShutdownStep(_cameraHealthTimer.Stop);
		_cameraHealthTimer.Tick -= CameraHealthTimerTick;
		TryShutdownStep(delegate
		{
			_modeLoadCancellation?.Cancel();
		});
		TryShutdownStep(delegate
		{
			_modeLoadCancellation?.Dispose();
		});
		_previewService.FrameAvailable -= PreviewFrameAvailable;
		_previewService.CameraFrameAvailable -= PreviewCameraFrameAvailable;
		_previewService.StatusChanged -= PreviewStatusChanged;
		try
		{
			WriteShutdownTrace("Stopping camera preview.");
			await StopPreviewAsync(keepToggleChecked: false, TimeSpan.FromSeconds(3L));
			WriteShutdownTrace("Camera preview stopped.");
		}
		catch (Exception ex)
		{
			WriteShutdownTrace("Camera preview stop failed: " + ex.Message);
		}
		WriteShutdownTrace("Disposing camera and vision services.");
		TryShutdownStep(_personIdentityWorker.Dispose);
		TryShutdownStep(_personIdentityCameraFrameWorker.Dispose);
		_personIdentityMemory.SnapshotChanged -=
			PersonIdentityMemorySnapshotChanged;
		TryShutdownStep(_personIdentityMemory.Dispose);
		TryShutdownStep(_directX12AnalysisWorker.Dispose);
		TryShutdownStep(DisposeDirectMlTextureTracker);
		TryShutdownStep(_previewService.Dispose);
		CompositeFaceLandmarkTracker? tracker;
		lock (_faceLandmarkTrackerLock)
		{
			tracker = _faceLandmarkTracker;
			_faceLandmarkTracker = null;
		}
		TryShutdownStep(delegate
		{
			tracker?.Dispose();
		});
		ThreeDdfaOnnxReconstructionClient? avatarClient;
		ThreeDdfaOnnxReconstructionClient? faceBoxClient;
		lock (_threeDdfaClientLock)
		{
			avatarClient = _threeDdfaAvatarClient;
			faceBoxClient = _threeDdfaFaceBoxClient;
			_threeDdfaAvatarClient = null;
			_threeDdfaFaceBoxClient = null;
		}
		TryShutdownStep(delegate
		{
			avatarClient?.Dispose();
		});
		TryShutdownStep(delegate
		{
			faceBoxClient?.Dispose();
		});
		WriteShutdownTrace("Camera and vision services disposed.");
		_mediaPipeGeometryPipeline.ModelUpdated -= MediaPipeGeometryPipelineModelUpdated;
		try
		{
			WriteShutdownTrace("Stopping MediaPipe geometry worker.");
			await _mediaPipeGeometryPipeline.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3L));
			WriteShutdownTrace("MediaPipe geometry worker stopped.");
		}
		catch (Exception ex2)
		{
			WriteShutdownTrace("MediaPipe geometry shutdown failed: " + ex2.Message);
		}
		_shutdownCompleted = true;
		WriteShutdownTrace("Shutdown completed; closing WPF window.");
		Close();
	}

	private static void TryShutdownStep(Action cleanup)
	{
		try
		{
			cleanup();
		}
		catch
		{
		}
	}

	private static void WriteShutdownTrace(string message)
	{
		try
		{
			File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "AvatarBuilder-shutdown.log"), $"{DateTime.Now:O} {message}{Environment.NewLine}");
		}
		catch
		{
		}
	}

	private async void RefreshCamerasClicked(object sender, RoutedEventArgs e)
	{
		await RefreshCamerasAsync();
	}

	private async Task RefreshCamerasAsync()
	{
		if (_isRefreshingCameras)
		{
			return;
		}
		_isRefreshingCameras = true;
		SetStatus("Scanning cameras...");
		IReadOnlyList<CameraDevice> cameras;
		try
		{
			cameras = await GetVideoInputDevicesAsync().WaitAsync(TimeSpan.FromSeconds(8L));
		}
		catch (TimeoutException)
		{
			SetStatus("Camera scan is taking longer than expected. The window is ready; try Refresh in a moment.");
			_isRefreshingCameras = false;
			return;
		}
		catch (Exception ex2)
		{
			SetStatus("Could not scan cameras: " + ex2.Message);
			_isRefreshingCameras = false;
			return;
		}
		finally
		{
			_isRefreshingCameras = false;
		}
		_cameras = cameras;
		CameraComboBox.ItemsSource = _cameras;
		CameraComboBox.DisplayMemberPath = "DisplayName";
		if (_cameras.Count > 0)
		{
			int preferredIndex = -1;
			if (!string.IsNullOrWhiteSpace(
				_preferredCameraDevicePath))
			{
				preferredIndex = _cameras
					.Select((camera, index) => (camera, index))
					.Where(item => string.Equals(
						item.camera.DevicePath,
						_preferredCameraDevicePath,
						StringComparison.OrdinalIgnoreCase))
					.Select(item => item.index)
					.DefaultIfEmpty(-1)
					.First();
			}
			if (preferredIndex < 0
				&& !string.IsNullOrWhiteSpace(_preferredCameraName))
			{
				preferredIndex = _cameras
					.Select((camera, index) => (camera, index))
					.Where(item => string.Equals(
						item.camera.Name,
						_preferredCameraName,
						StringComparison.OrdinalIgnoreCase))
					.Select(item => item.index)
					.DefaultIfEmpty(-1)
					.First();
			}
			CameraComboBox.SelectedIndex =
				preferredIndex >= 0 ? preferredIndex : 0;
			SetStatus($"Found {_cameras.Count} camera{((_cameras.Count == 1) ? "" : "s")}.");
		}
		else
		{
			CameraModeComboBox.ItemsSource = new CameraVideoMode[1] { CameraVideoMode.Auto };
			CameraModeComboBox.SelectedIndex = 0;
			CameraControlsPanel.Children.Clear();
			CameraControlsStatusText.Text = CameraControlText.FormatChooseCameraControlsStatus();
			SetStatus("No cameras found.");
			SetPreviewState("No camera source found", null);
		}
		if (_isCameraEnabled)
		{
			ResumeDirectX12PreviewPresentation();
		}
	}

	private static Task<IReadOnlyList<CameraDevice>> GetVideoInputDevicesAsync()
	{
		TaskCompletionSource<IReadOnlyList<CameraDevice>> completion = new TaskCompletionSource<IReadOnlyList<CameraDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
		Thread thread = new Thread((ThreadStart)delegate
		{
			try
			{
				IReadOnlyList<CameraDevice> mediaFoundationDevices;
				try
				{
					mediaFoundationDevices = MediaFoundationCameraEnumerator.GetVideoInputDevices();
				}
				catch
				{
					mediaFoundationDevices = Array.Empty<CameraDevice>();
				}
				IReadOnlyList<CameraDevice> directShowDevices;
				try
				{
					directShowDevices = DirectShowCameraEnumerator.GetVideoInputDevices();
				}
				catch
				{
					directShowDevices = Array.Empty<CameraDevice>();
				}
				completion.SetResult(CameraDeviceCatalog.MergeDevices(mediaFoundationDevices, directShowDevices));
			}
			catch (Exception exception)
			{
				completion.SetException(exception);
			}
		});
		thread.IsBackground = true;
		thread.Name = "Avatar Builder Camera Enumerator";
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		return completion.Task;
	}

	private async void CameraSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		object selectedItem = CameraComboBox.SelectedItem;
		if (selectedItem is CameraDevice camera)
		{
			if (!_cameraRecoveryPending)
			{
				_preferredCameraDevicePath =
					camera.DevicePath ?? string.Empty;
				_preferredCameraName =
					camera.Name ?? string.Empty;
			}
			await LoadCameraModesAsync(camera);
			await LoadCameraControlsAsync(camera);
			if (_isCameraEnabled)
			{
				await RestartPreviewAsync();
			}
		}
	}

	private async Task LoadCameraModesAsync(CameraDevice camera)
	{
		_modeLoadCancellation?.Cancel();
		_modeLoadCancellation?.Dispose();
		_modeLoadCancellation = new CancellationTokenSource();
		CancellationToken cancellationToken = _modeLoadCancellation.Token;
		CameraModeComboBox.ItemsSource = new CameraVideoMode[1] { CameraVideoMode.Auto };
		CameraModeComboBox.SelectedIndex = 0;
		SetStatus("Loading modes for " + camera.Name + "...");
		try
		{
			IReadOnlyList<CameraVideoMode> readOnlyList = await _cameraModeService.GetModesAsync(camera, cancellationToken);
			if (!cancellationToken.IsCancellationRequested)
			{
				CameraModeComboBox.ItemsSource = readOnlyList;
				CameraModeComboBox.SelectedIndex = readOnlyList.Count > 0 ? 0 : -1;
				CameraVideoMode cameraVideoMode = (CameraModeComboBox.SelectedItem as CameraVideoMode) ?? CameraVideoMode.Auto;
				SetStatus($"Loaded {readOnlyList.Count} mode{((readOnlyList.Count == 1) ? "" : "s")} for {camera.Name}. Selected {cameraVideoMode.Label}.");
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			SetStatus("Could not load camera modes: " + ex2.Message);
		}
	}

	private async void RefreshCameraControlsClicked(object sender, RoutedEventArgs e)
	{
		if (!(CameraComboBox.SelectedItem is CameraDevice camera))
		{
			CameraControlsPanel.Children.Clear();
			CameraControlsStatusText.Text = CameraControlText.FormatChooseCameraControlsStatus();
		}
		else
		{
			await LoadCameraControlsAsync(camera);
		}
	}

	private async Task LoadCameraControlsAsync(CameraDevice camera)
	{
		if (_isLoadingCameraControls)
		{
			return;
		}
		_isLoadingCameraControls = true;
		CameraControlsPanel.Children.Clear();
		CameraControlsStatusText.Text = "Loading controls for " + camera.Name + "...";
		try
		{
			IReadOnlyList<CameraControlItem> readOnlyList = await GetCameraControlsAsync(camera).WaitAsync(TimeSpan.FromSeconds(5L));
			if (CameraComboBox.SelectedItem == camera)
			{
				BuildCameraControlRows(camera, readOnlyList);
				CameraControlsStatusText.Text = ((readOnlyList.Count == 0) ? CameraControlText.FormatNoCameraControlsStatus() : CameraControlText.FormatCameraControlsLoadedStatus(camera, readOnlyList.Count));
			}
		}
		catch (TimeoutException)
		{
			CameraControlsStatusText.Text = "Camera controls are taking longer than expected. Try Refresh after the camera is idle.";
		}
		catch (Exception ex2)
		{
			CameraControlsStatusText.Text = "Could not load camera controls: " + ex2.Message;
		}
		finally
		{
			_isLoadingCameraControls = false;
		}
	}

	private Task<IReadOnlyList<CameraControlItem>> GetCameraControlsAsync(CameraDevice camera)
	{
		TaskCompletionSource<IReadOnlyList<CameraControlItem>> completion = new TaskCompletionSource<IReadOnlyList<CameraControlItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
		Thread thread = new Thread((ThreadStart)delegate
		{
			try
			{
				completion.SetResult(_cameraControlService.GetControls(camera));
			}
			catch (Exception exception)
			{
				completion.SetException(exception);
			}
		});
		thread.IsBackground = true;
		thread.Name = "Avatar Builder Camera Controls";
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		return completion.Task;
	}

	private void BuildCameraControlRows(CameraDevice camera, IReadOnlyList<CameraControlItem> controls)
	{
		CameraControlsPanel.Children.Clear();
		foreach (CameraControlItem item in from control in controls
			orderby control.Kind, control.Name
			select control)
		{
			CameraControlsPanel.Children.Add(CreateCameraControlRow(camera, item));
		}
	}

	private UIElement CreateCameraControlRow(CameraDevice camera, CameraControlItem control)
	{
		StackPanel obj = new StackPanel
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 12.0)
		};
		Grid grid = new Grid
		{
			ColumnDefinitions = 
			{
				new ColumnDefinition
				{
					Width = new GridLength(1.0, GridUnitType.Star)
				},
				new ColumnDefinition
				{
					Width = GridLength.Auto
				},
				new ColumnDefinition
				{
					Width = GridLength.Auto
				}
			}
		};
		TextBlock element = new TextBlock
		{
			Text = control.Name,
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center
		};
		grid.Children.Add(element);
		TextBlock textBlock = new TextBlock
		{
			Text = CameraControlText.FormatCameraControlValue(control.Value),
			Foreground = new SolidColorBrush(Color.FromRgb(185, 215, 239)),
			Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid.SetColumn(textBlock, 1);
		grid.Children.Add(textBlock);
		CheckBox checkBox = new CheckBox
		{
			Content = "Auto",
			IsChecked = control.IsAuto,
			IsEnabled = control.SupportsAuto,
			Margin = new Thickness(10.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid.SetColumn(checkBox, 2);
		grid.Children.Add(checkBox);
		Slider slider = new Slider
		{
			Minimum = control.Minimum,
			Maximum = control.Maximum,
			Value = Math.Clamp(control.Value, control.Minimum, control.Maximum),
			TickPlacement = TickPlacement.BottomRight,
			IsSnapToTickEnabled = false,
			Ticks = new DoubleCollection { control.DefaultValue },
			ToolTip = "Default: " + CameraControlText.FormatCameraControlValue(control.DefaultValue),
			IsEnabled = (!control.IsAuto || !control.SupportsAuto)
		};
		CameraControlBinding tag = (CameraControlBinding)(slider.Tag = new CameraControlBinding(camera, control, textBlock, slider, checkBox));
		checkBox.Tag = tag;
		slider.ValueChanged += CameraControlSliderChanged;
		checkBox.Checked += CameraControlAutoChanged;
		checkBox.Unchecked += CameraControlAutoChanged;
		obj.Children.Add(grid);
		obj.Children.Add(slider);
		return obj;
	}

	private void CameraControlSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (_isUpdatingCameraControlUi || !(sender is Slider { Tag: CameraControlBinding tag } slider))
		{
			return;
		}
		int value = CameraControlText.RoundCameraControlToStep(slider.Value, tag.Control);
		value = CameraControlText.ApplyCameraControlDefaultMagnet(value, tag.Control);
		_isUpdatingCameraControlUi = true;
		try
		{
			if (Math.Abs(slider.Value - (double)value) > 0.001)
			{
				slider.Value = value;
			}
			if (tag.AutoCheckBox != null)
			{
				tag.AutoCheckBox.IsChecked = false;
			}
		}
		finally
		{
			_isUpdatingCameraControlUi = false;
		}
		ApplyCameraControl(tag, value, isAuto: false);
	}

	private void CameraControlAutoChanged(object sender, RoutedEventArgs e)
	{
		if (!_isUpdatingCameraControlUi && sender is CheckBox { Tag: CameraControlBinding tag, IsChecked: var isChecked })
		{
			bool valueOrDefault = isChecked == true;
			int value = CameraControlText.RoundCameraControlToStep(tag.Slider.Value, tag.Control);
			tag.Slider.IsEnabled = !valueOrDefault;
			ApplyCameraControl(tag, value, valueOrDefault);
		}
	}

	private void ApplyCameraControl(CameraControlBinding binding, int value, bool isAuto)
	{
		if (CameraComboBox.SelectedItem is CameraDevice cameraDevice && string.Equals(cameraDevice.DevicePath, binding.Camera.DevicePath, StringComparison.OrdinalIgnoreCase))
		{
			binding.Control.Value = value;
			binding.Control.IsAuto = isAuto;
			binding.ValueText.Text = (isAuto ? "Auto" : CameraControlText.FormatCameraControlValue(value));
			bool success = _cameraControlService.SetControl(binding.Camera, binding.Control, value, isAuto);
			CameraControlsStatusText.Text = CameraControlText.FormatCameraControlSetStatus(binding.Control, value, isAuto, success);
		}
	}

	private async void CameraModeSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_isCameraEnabled)
		{
			await RestartPreviewAsync();
		}
	}

	private void FaceBoxSystemMenuItemClicked(object sender, RoutedEventArgs e)
	{
		if (!(sender is MenuItem { Tag: var tag }) || !Enum.TryParse<FaceBoxSystem>(tag?.ToString(), out var result))
		{
			UpdateFaceBoxSystemMenuChecks();
		}
		else if (result == _selectedFaceBoxSystem)
		{
			UpdateFaceBoxSystemMenuChecks();
		}
		else
		{
			SwitchFaceBoxSystem(result);
		}
	}

	private async void MediaPipeProcessorMenuItemClicked(object sender, RoutedEventArgs e)
	{
		if (!(sender is MenuItem { Tag: var tag }) || !Enum.TryParse<MediaPipeExecutionBackend>(tag?.ToString(), out var requestedBackend))
		{
			UpdateMediaPipeProcessorMenuChecks();
			return;
		}
		if (requestedBackend == _mediaPipeExecutionBackend)
		{
			UpdateMediaPipeProcessorMenuChecks();
			return;
		}

		MediaPipeCpuProcessorMenuItem.IsEnabled = false;
		MediaPipeGpuProcessorMenuItem.IsEnabled = false;
		try
		{
			if (requestedBackend == MediaPipeExecutionBackend.Gpu)
			{
				SetStatus("Checking the DirectML MediaPipe detector and 478-point landmark models...");
				MediaPipeDelegateProbeResult probe = await Task.Run(delegate
				{
					MediaPipeDirectMlModelEnvironment models =
						MediaPipeSidecarPythonEnvironment.DetectDirectMlModels();
					return new MediaPipeDelegateProbeResult(
						models.IsReady,
						models.Status);
				});
				if (_isClosing)
				{
					return;
				}
				if (!probe.IsAvailable)
				{
					UpdateMediaPipeProcessorMenuChecks();
					SetStatus(probe.Status + " CPU remains active; landmark behavior was not changed.");
					return;
				}
			}

			ApplyMediaPipeExecutionBackend(requestedBackend);
		}
		finally
		{
			if (!_isClosing)
			{
				MediaPipeCpuProcessorMenuItem.IsEnabled = true;
				MediaPipeGpuProcessorMenuItem.IsEnabled = true;
			}
		}
	}

	private void ApplyMediaPipeExecutionBackend(MediaPipeExecutionBackend backend)
	{
		_mediaPipeExecutionBackend = backend;
		CompositeFaceLandmarkTracker? previousTracker = null;
		if (_selectedFaceBoxSystem == FaceBoxSystem.MediaPipe)
		{
			lock (_faceLandmarkTrackerLock)
			{
				previousTracker = _faceLandmarkTracker;
				_faceLandmarkTracker = new CompositeFaceLandmarkTracker(backend)
				{
					MaxDetectionDimension = GetFaceLandmarkDetectionDimension()
				};
			}
			Interlocked.Increment(ref _faceBoxSystemGeneration);
			ResetLandmarkTracking();
			previousTracker?.Dispose();
		}

		UpdateMediaPipeProcessorMenuChecks();
		SetStatus(backend == MediaPipeExecutionBackend.Gpu
			? "MediaPipe processor changed to GPU texture DirectML. Native camera frames now stay on the GPU through crop, resize, normalization, and inference."
			: "MediaPipe processor changed to CPU (Stable). The official MediaPipe Tasks graph is active; the DirectML sidecar was stopped.");
	}

	private void UpdateMediaPipeProcessorMenuChecks()
	{
		MediaPipeCpuProcessorMenuItem.IsChecked = _mediaPipeExecutionBackend == MediaPipeExecutionBackend.Cpu;
		MediaPipeGpuProcessorMenuItem.IsChecked = _mediaPipeExecutionBackend == MediaPipeExecutionBackend.Gpu;
	}

	private void SwitchFaceBoxSystem(FaceBoxSystem selectedSystem)
	{
		_selectedFaceBoxSystem = selectedSystem;
		Interlocked.Increment(ref _faceBoxSystemGeneration);
		ResetFaceBoxDiagnostics();
		_currentFaceFeatureDetection = FaceFeatureDetection.None;
		Volatile.Write(ref _lastFaceFeatureLockTimestamp, 0L);
		_activeFaceCueLayout = null;
		_threeDdfaTrackingFaceBox = null;
		_lastThreeDdfaFaceBoxesAtUtc = DateTime.MinValue;
		CompositeFaceLandmarkTracker? faceLandmarkTracker;
		lock (_faceLandmarkTrackerLock)
		{
			faceLandmarkTracker = _faceLandmarkTracker;
			_faceLandmarkTracker = ((selectedSystem == FaceBoxSystem.MediaPipe) ? new CompositeFaceLandmarkTracker(_mediaPipeExecutionBackend) : null);
			if (_faceLandmarkTracker != null)
			{
				_faceLandmarkTracker.MaxDetectionDimension = GetFaceLandmarkDetectionDimension();
			}
		}
		ResetLandmarkTracking();
		faceLandmarkTracker?.Dispose();
		ThreeDdfaOnnxReconstructionClient? threeDdfaAvatarClient;
		ThreeDdfaOnnxReconstructionClient? threeDdfaFaceBoxClient;
		lock (_threeDdfaClientLock)
		{
			threeDdfaAvatarClient = _threeDdfaAvatarClient;
			threeDdfaFaceBoxClient = _threeDdfaFaceBoxClient;
			_threeDdfaAvatarClient = ((selectedSystem == FaceBoxSystem.MediaPipe) ? new ThreeDdfaOnnxReconstructionClient(_threeDdfaOnnxEnvironment) : null);
			_threeDdfaFaceBoxClient = ((selectedSystem == FaceBoxSystem.ThreeDdfaV2) ? new ThreeDdfaOnnxReconstructionClient(_threeDdfaOnnxEnvironment) : null);
		}
		threeDdfaAvatarClient?.Dispose();
		threeDdfaFaceBoxClient?.Dispose();
		if (selectedSystem == FaceBoxSystem.ThreeDdfaV2 && _showFaceMeshOverlay)
		{
			_showFaceMeshOverlay = false;
			FaceMeshOverlayMenuItem.IsChecked = false;
		}
		if (selectedSystem == FaceBoxSystem.ThreeDdfaV2 && _showLiveWireframePreview)
		{
			_showLiveWireframePreview = false;
			LiveWireframeMenuItem.IsChecked = false;
			SetPreviewState("Camera active", _latestFrame);
		}
		UpdateFaceBoxSystemMenuChecks();
		UpdateFaceBoxOptionsUi();
		UpdateAvatarLearningStatusUi();
		SetStatus("Face Box System changed to " + GetFaceBoxSystemDisplayName() + ". The previous tracking backend has been stopped.");
		UpdateFaceCueGuideOverlay(_latestFrame);
	}

	private void UpdateFaceBoxSystemMenuChecks()
	{
		MediaPipeFaceBoxSystemMenuItem.IsChecked = _selectedFaceBoxSystem == FaceBoxSystem.MediaPipe;
		ThreeDdfaFaceBoxSystemMenuItem.IsChecked = _selectedFaceBoxSystem == FaceBoxSystem.ThreeDdfaV2;
	}

	private void UpdateFaceBoxOptionsUi()
	{
		bool flag = _selectedFaceBoxSystem == FaceBoxSystem.MediaPipe;
		FaceTrackingFieldExpander.Header = "Face Box Options (" + GetFaceBoxSystemDisplayName() + ")";
		FaceMeshOverlayMenuItem.IsEnabled = flag;
		FaceMeshOverlayMenuItem.ToolTip = (flag ? "Draws the current MediaPipe face mesh over the live webcam image." : "The face mesh overlay is MediaPipe-specific and is unavailable while 3DDFA-V2 owns the face box.");
		LiveWireframeMenuItem.IsEnabled = flag;
		LiveWireframeMenuItem.ToolTip = (flag ? "Hides the webcam image and shows the current camera-relative MediaPipe face mesh while analysis keeps running." : "Live wireframe is MediaPipe-specific and is unavailable while 3DDFA-V2 owns the face box.");
	}

	private string GetFaceBoxSystemDisplayName()
	{
		if (_selectedFaceBoxSystem != FaceBoxSystem.ThreeDdfaV2)
		{
			return "MediaPipe";
		}
		return "3DDFA-V2";
	}

	private bool IsSelectedFaceBoxSystemAvailable()
	{
		if (_selectedFaceBoxSystem == FaceBoxSystem.ThreeDdfaV2)
		{
			return _threeDdfaOnnxEnvironment.IsReady;
		}
		lock (_faceLandmarkTrackerLock)
		{
			return _faceLandmarkTracker?.IsAvailable ?? false;
		}
	}

	private int GetFaceLandmarkDetectionDimension()
	{
		CameraVideoMode mode = (CameraModeComboBox.SelectedItem as CameraVideoMode) ?? CameraVideoMode.Auto;
		return Math.Clamp(mode.Width ?? 3840, 320, 3840);
	}

	private void ApplySelectedCameraMode(CameraVideoMode mode)
	{
		int width = Math.Clamp(mode.Width ?? 3840, 320, 3840);
		double framesPerSecond = Math.Clamp(mode.FramesPerSecond ?? 1000.0, 1.0, 1000.0);
		_previewService.MaxOutputWidth = width;
		_previewService.MaxOutputFramesPerSecond = framesPerSecond;
		lock (_faceLandmarkTrackerLock)
		{
			if (_faceLandmarkTracker != null)
			{
				_faceLandmarkTracker.MaxDetectionDimension = width;
			}
		}
	}

	private void ResetFaceBoxDiagnostics()
	{
		_lastFaceBoxBackendStatus = "waiting";
	}

}
