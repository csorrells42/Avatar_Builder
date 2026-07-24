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

	private static readonly int[] DenseMeshEyeA = new int[16]
	{
		33, 246, 161, 160, 159, 158, 157, 173, 133, 155,
		154, 153, 145, 144, 163, 7
	};

	private static readonly int[] DenseMeshEyeB = new int[16]
	{
		362, 398, 384, 385, 386, 387, 388, 466, 263, 249,
		390, 373, 374, 380, 381, 382
	};

	private static readonly int[] DenseMeshOuterLip = new int[20]
	{
		61, 185, 40, 39, 37, 0, 267, 269, 270, 409,
		291, 375, 321, 405, 314, 17, 84, 181, 91, 146
	};

	private static readonly int[] DenseMeshInnerLip = new int[20]
	{
		78, 191, 80, 81, 82, 13, 312, 311, 310, 415,
		308, 324, 318, 402, 317, 14, 87, 178, 88, 95
	};

	private static readonly int[] DenseMeshJawContour = new int[21]
	{
		234, 93, 132, 58, 172, 136, 150, 149, 176, 148,
		152, 377, 400, 378, 379, 365, 397, 288, 361, 323,
		454
	};

	private static readonly int[] DenseMeshNoseBridge = new int[10] { 168, 6, 197, 195, 5, 4, 1, 19, 94, 2 };

	private static readonly int[] DenseMeshNoseBase = new int[5] { 98, 97, 2, 326, 327 };

	private static readonly int[] DenseMeshFaceOval = new int[36]
	{
		10, 338, 297, 332, 284, 251, 389, 356, 454, 323,
		361, 288, 397, 365, 379, 378, 400, 377, 152, 148,
		176, 149, 150, 136, 172, 58, 132, 93, 234, 127,
		162, 21, 54, 103, 67, 109
	};

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

	private static readonly PreviewOverlayEdge[] MediaPipePreviewMeshEdges = CreateMediaPipePreviewMeshEdges();

	private static readonly PreviewOverlayIndexedPath[] MediaPipePreviewMeshBaseFeaturePaths = CreateMediaPipePreviewMeshBaseFeaturePaths();

	private static readonly bool[] MediaPipePreviewMeshFeaturePointMask = CreateMediaPipePreviewMeshFeaturePointMask();

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

	private readonly MediaPipeGeometryPipeline _mediaPipeGeometryPipeline = new MediaPipeGeometryPipeline();

	private readonly LatestTextureFrameWorker _directX12AnalysisWorker;

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
		base.Dispatcher.InvokeAsync((Func<Task>)async delegate
		{
			await RefreshCamerasAsync();
		}, DispatcherPriority.ApplicationIdle);
		base.Dispatcher.InvokeAsync(ApplyStartupOptionsAfterLoad, DispatcherPriority.ApplicationIdle);
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
				BitmapSource latestFrame = _latestFrame;
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
			if (_startupOptions.StartAvatarLearning && IsAvatarUserLoggedIn && _avatarModelReadyForCapture)
			{
				_avatarLearningRequested = true;
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
		AvatarProfile avatarProfile = null;
		if (promptForStartupUser)
		{
			avatarProfile = ResolveAvatarLoginSelection(PromptForAvatarLogin(_avatarProfileRegistry));
		}
		if (_avatarProfileRegistry.Profiles.Count == 0)
		{
			_avatarProfileStore.AddOrUpdateProfile(_outputFolder, _avatarProfileRegistry, "Chris");
		}
		AvatarProfile avatarProfile2 = ((avatarProfile == null) ? FindSelectedAvatarProfile() : avatarProfile);
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
		Window progressWindow = null;
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
		TryShutdownStep(_directX12AnalysisWorker.Dispose);
		TryShutdownStep(DisposeDirectMlTextureTracker);
		TryShutdownStep(_previewService.Dispose);
		CompositeFaceLandmarkTracker tracker;
		lock (_faceLandmarkTrackerLock)
		{
			tracker = _faceLandmarkTracker;
			_faceLandmarkTracker = null;
		}
		TryShutdownStep(delegate
		{
			tracker?.Dispose();
		});
		ThreeDdfaOnnxReconstructionClient avatarClient;
		ThreeDdfaOnnxReconstructionClient faceBoxClient;
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
		CompositeFaceLandmarkTracker faceLandmarkTracker;
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
		ThreeDdfaOnnxReconstructionClient threeDdfaAvatarClient;
		ThreeDdfaOnnxReconstructionClient threeDdfaFaceBoxClient;
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
		CancellationTokenSource previousCancellation = Interlocked.Exchange(ref _cameraStartCancellation, cancellation);
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
		CancellationTokenSource startCancellation = Interlocked.Exchange(ref _cameraStartCancellation, null);
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
		if (!IsDirectX12PreviewEnabled())
		{
			return;
		}
		Direct3D12PreviewHost directX12PreviewHost;
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
		byte[] nv12PreviewBytes = frame.Nv12PreviewBytes;
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

	private bool TryCreateBitmapFromBgraCameraFrame(CameraFrame frame, out BitmapSource bitmap)
	{
		bitmap = EmptyBgraBitmap;
		if (!frame.HasBgra || frame.Width <= 0 || frame.Height <= 0)
		{
			return false;
		}
		BitmapSource bitmapSource = BitmapSource.Create(frame.Width, frame.Height, 96.0, 96.0, PixelFormats.Bgra32, null, frame.BgraBytes, frame.Stride);
		bitmapSource.Freeze();
		int num = Math.Clamp(frame.Width, 320, 3840);
		if (frame.Width <= num)
		{
			bitmap = bitmapSource;
			return true;
		}
		double num2 = (double)num / (double)frame.Width;
		TransformedBitmap transformedBitmap = new TransformedBitmap(bitmapSource, new ScaleTransform(num2, num2));
		transformedBitmap.Freeze();
		bitmap = transformedBitmap;
		return true;
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
		Direct3D12PreviewHost directX12PreviewHost;
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
		Dx12Camera directX12NativeCamera = _directX12NativeCamera;
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
			AvatarProfile avatarProfile = ResolveAvatarLoginSelection(PromptForAvatarLogin(_avatarProfileRegistry));
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
		_avatarLearningRequested = !_avatarLearningRequested;
		UpdateAvatarLearningStatusUi();
		SetStatus((!_avatarLearningRequested)
			? "Avatar capture stopped."
			: $"Avatar capture started. MediaPipe visible-evidence geometry accepts one frame only when its processing slot is empty; {GetFaceBoxSystemDisplayName()} preview remains independent and full speed.");
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

	private bool HasStrongAvatarPoseLock()
	{
		MediaPipeNormalizedFaceModel model = _currentMediaPipeGeometryModel;
		return model.AcceptedFrameCount >= 20
			&& model.MaximumBRotationDegrees - model.MinimumBRotationDegrees >= 20.0
			&& model.ConfidentVertexPercent >= 25.0;
	}

	private string FormatAvatarPoseCrossCheck()
	{
		MediaPipeNormalizedFaceModel model = _currentMediaPipeGeometryModel;
		return $"MediaPipe geometry coverage: A {model.MinimumARotationDegrees:0.#}..{model.MaximumARotationDegrees:0.#}, B {model.MinimumBRotationDegrees:0.#}..{model.MaximumBRotationDegrees:0.#}, C {model.MinimumCRotationDegrees:0.#}..{model.MaximumCRotationDegrees:0.#} deg; {model.ConfidentVertexPercent:0.#}% directly constrained.";
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
		ThreeDdfaOnnxReconstructionClient threeDdfaFaceBoxClient;
		lock (_threeDdfaClientLock)
		{
			threeDdfaFaceBoxClient = _threeDdfaFaceBoxClient;
		}
		ThreeDdfaOnnxSidecarResponse threeDdfaOnnxSidecarResponse;
		try
		{
			bool flag2 = _threeDdfaTrackingFaceBox == null || (capturedAtUtc - _lastThreeDdfaFaceBoxesAtUtc).TotalMilliseconds >= 1000.0;
			ThreeDdfaOnnxSidecarFaceBox threeDdfaOnnxSidecarFaceBox = flag2 ? null : _threeDdfaTrackingFaceBox;
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

	private static ThreeDdfaOnnxSidecarFaceBox? CreateThreeDdfaFaceBox(FaceFeatureDetection detection)
	{
		if (!detection.HasFace || detection.FaceBox.Width <= 0.0 || detection.FaceBox.Height <= 0.0)
		{
			return null;
		}
		return new ThreeDdfaOnnxSidecarFaceBox
		{
			Left = detection.FaceBox.Left,
			Top = detection.FaceBox.Top,
			Right = detection.FaceBox.Right,
			Bottom = detection.FaceBox.Bottom,
			Normalized = true,
			Confidence = Math.Clamp(detection.TrackingConfidence, 0.01, 1.0)
		};
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
		CameraVideoMode cameraVideoMode = CameraModeComboBox.SelectedItem as CameraVideoMode;
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
		string pathRoot = System.IO.Path.GetPathRoot("D:\\Avatar Builder Output");
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
			string pathRoot = System.IO.Path.GetPathRoot(fullPath);
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
		string pathRoot = System.IO.Path.GetPathRoot("D:\\Avatar Builder Output");
		if (!string.IsNullOrWhiteSpace(pathRoot) && Directory.Exists(pathRoot))
		{
			return pathRoot;
		}
		return AppContext.BaseDirectory;
	}

	private static string ResolveFallbackOutputFolder()
	{
		string pathRoot = System.IO.Path.GetPathRoot("D:\\Avatar Builder Output");
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

	private static BitmapSource CreateEmptyBgraBitmap()
	{
		BitmapSource bitmap = BitmapSource.Create(1, 1, 96.0, 96.0, PixelFormats.Bgra32, null, new byte[4] { 0, 0, 0, 255 }, 4);
		bitmap.Freeze();
		return bitmap;
	}

	private void DrawLiveWireframePreview()
	{
		if (LiveWireframeCanvas != null)
		{
			LiveWireframeCanvas.Children.Clear();
			double width = Math.Max(1.0, LiveWireframeCanvas.ActualWidth);
			double height = Math.Max(1.0, LiveWireframeCanvas.ActualHeight);
			FaceLandmarkFrame currentFaceLandmarkFrame = _currentFaceLandmarkFrame;
			if (!currentFaceLandmarkFrame.HasDenseMesh)
			{
				AddWireframeText("Live wireframe waiting", "Turn on the camera and wait for MediaPipe dense face lock.", 18.0, 18.0);
			}
			else
			{
				DrawMediaPipeLiveWireframeView(currentFaceLandmarkFrame, _currentFaceLandmarkMetrics, new Rect(0.0, 0.0, width, height), "Live wireframe");
			}
		}
	}

	private void DrawMediaPipeLiveWireframeView(FaceLandmarkFrame frame, FaceLandmarkMetrics metrics, Rect rect, string title)
	{
		DrawMediaPipeWireframe(
			LiveWireframeCanvas,
			frame.DenseMeshPoints,
			rect,
			LiveWireframeMeshBrush,
			0.42,
			FaceMeshOverlayPointBrush);
		string value2 = (_currentThreeDdfaOnnxResponse.Ok && _currentThreeDdfaOnnxResponse.HasFace)
			? $"{_currentThreeDdfaOnnxResponse.Backend} A/B/C {_currentThreeDdfaOnnxResponse.Pose.ARotationAroundXDegrees:0.#}/{_currentThreeDdfaOnnxResponse.Pose.BRotationAroundYDegrees:0.#}/{_currentThreeDdfaOnnxResponse.Pose.CRotationAroundZDegrees:0.#} deg"
			: "MediaPipe geometry tracking";
		AddWireframeText($"{title}: {frame.DenseMeshPoints.Count} points, {MediaPipeFaceMeshTopology.TessellationEdges.Length} surface edges", $"Camera-relative MediaPipe wireframe. Quality {metrics.OverallMeasurementQualityPercent:0}% | eyes {metrics.EyeMeasurementQualityPercent:0}% | brows {metrics.BrowMeasurementQualityPercent:0}% ({FormatRatioPercent(metrics.AverageBrowHeightRatio)}) | mouth {metrics.MouthMeasurementQualityPercent:0}% | {value2}", rect.X + 18.0, rect.Y + 18.0);
	}

	private static void DrawMediaPipeWireframe(
		Canvas canvas,
		IReadOnlyList<FaceMeshLandmarkPoint> denseMeshPoints,
		Rect display,
		Brush meshBrush,
		double meshThickness,
		Brush pointBrush)
	{
		int count = denseMeshPoints.Count;
		if (count == 0)
		{
			return;
		}
		Point[] projectedPoints = ArrayPool<Point>.Shared.Rent(count);
		bool[] validPoints = ArrayPool<bool>.Shared.Rent(count);
		Array.Clear(validPoints, 0, count);
		try
		{
			for (int i = 0; i < count; i++)
			{
				FaceMeshLandmarkPoint point = denseMeshPoints[i];
				if ((uint)point.Index >= (uint)count || !double.IsFinite(point.X) || !double.IsFinite(point.Y))
				{
					continue;
				}
				projectedPoints[point.Index] = new Point(
					display.Left + Math.Clamp(point.X, 0.0, 1.0) * display.Width,
					display.Top + Math.Clamp(point.Y, 0.0, 1.0) * display.Height);
				validPoints[point.Index] = true;
			}
			StreamGeometry meshGeometry = new StreamGeometry();
			using (StreamGeometryContext context = meshGeometry.Open())
			{
				PreviewOverlayEdge[] edges = MediaPipePreviewMeshEdges;
				for (int i = 0; i < edges.Length; i++)
				{
					PreviewOverlayEdge edge = edges[i];
					if ((uint)edge.FromIndex >= (uint)count ||
						(uint)edge.ToIndex >= (uint)count ||
						!validPoints[edge.FromIndex] ||
						!validPoints[edge.ToIndex])
					{
						continue;
					}
					context.BeginFigure(projectedPoints[edge.FromIndex], isFilled: false, isClosed: false);
					context.LineTo(projectedPoints[edge.ToIndex], isStroked: true, isSmoothJoin: false);
				}
			}
			meshGeometry.Freeze();
			canvas.Children.Add(new System.Windows.Shapes.Path
			{
				Data = meshGeometry,
				Stroke = meshBrush,
				StrokeThickness = meshThickness,
				IsHitTestVisible = false
			});
			PreviewOverlayIndexedPath[] featurePaths = CreateMediaPipePreviewMeshFeaturePaths(denseMeshPoints);
			DrawMediaPipeFeatureGeometry(canvas, featurePaths, projectedPoints, validPoints, count, PreviewOverlayMeshFeatureRole.Eye);
			DrawMediaPipeFeatureGeometry(canvas, featurePaths, projectedPoints, validPoints, count, PreviewOverlayMeshFeatureRole.Brow);
			DrawMediaPipeFeatureGeometry(canvas, featurePaths, projectedPoints, validPoints, count, PreviewOverlayMeshFeatureRole.Mouth);
			DrawMediaPipeFeatureGeometry(canvas, featurePaths, projectedPoints, validPoints, count, PreviewOverlayMeshFeatureRole.Jaw);
			DrawMediaPipeFeatureGeometry(canvas, featurePaths, projectedPoints, validPoints, count, PreviewOverlayMeshFeatureRole.Nose);
			DrawMediaPipeFeatureGeometry(canvas, featurePaths, projectedPoints, validPoints, count, PreviewOverlayMeshFeatureRole.Face);
			StreamGeometry pointGeometry = new StreamGeometry();
			using (StreamGeometryContext context = pointGeometry.Open())
			{
				for (int i = 0; i < count; i++)
				{
					if (!validPoints[i])
					{
						continue;
					}
					double radius = i < MediaPipePreviewMeshFeaturePointMask.Length && MediaPipePreviewMeshFeaturePointMask[i] ? 1.6 : 1.0;
					Point point = projectedPoints[i];
					context.BeginFigure(new Point(point.X, point.Y - radius), isFilled: true, isClosed: true);
					context.LineTo(new Point(point.X + radius, point.Y), isStroked: true, isSmoothJoin: false);
					context.LineTo(new Point(point.X, point.Y + radius), isStroked: true, isSmoothJoin: false);
					context.LineTo(new Point(point.X - radius, point.Y), isStroked: true, isSmoothJoin: false);
				}
			}
			pointGeometry.Freeze();
			canvas.Children.Add(new System.Windows.Shapes.Path
			{
				Data = pointGeometry,
				Fill = pointBrush,
				IsHitTestVisible = false
			});
		}
		finally
		{
			ArrayPool<Point>.Shared.Return(projectedPoints);
			ArrayPool<bool>.Shared.Return(validPoints);
		}
	}

	private static void DrawMediaPipeFeatureGeometry(
		Canvas canvas,
		IReadOnlyList<PreviewOverlayIndexedPath> featurePaths,
		Point[] projectedPoints,
		bool[] validPoints,
		int pointCount,
		PreviewOverlayMeshFeatureRole role)
	{
		StreamGeometry geometry = new StreamGeometry();
		bool hasSegments = false;
		using (StreamGeometryContext context = geometry.Open())
		{
			for (int i = 0; i < featurePaths.Count; i++)
			{
				PreviewOverlayIndexedPath path = featurePaths[i];
				if (path.Role != role)
				{
					continue;
				}
				int previousIndex = -1;
				for (int j = 0; j < path.PointIndices.Count; j++)
				{
					int index = path.PointIndices[j];
					if ((uint)index >= (uint)pointCount || !validPoints[index])
					{
						previousIndex = -1;
						continue;
					}
					if (previousIndex >= 0)
					{
						context.BeginFigure(projectedPoints[previousIndex], isFilled: false, isClosed: false);
						context.LineTo(projectedPoints[index], isStroked: true, isSmoothJoin: false);
						hasSegments = true;
					}
					previousIndex = index;
				}
				if (path.Closed && path.PointIndices.Count > 2)
				{
					int firstIndex = path.PointIndices[0];
					int lastIndex = path.PointIndices[path.PointIndices.Count - 1];
					if ((uint)firstIndex < (uint)pointCount &&
						(uint)lastIndex < (uint)pointCount &&
						validPoints[firstIndex] &&
						validPoints[lastIndex])
					{
						context.BeginFigure(projectedPoints[lastIndex], isFilled: false, isClosed: false);
						context.LineTo(projectedPoints[firstIndex], isStroked: true, isSmoothJoin: false);
						hasSegments = true;
					}
				}
			}
		}
		if (!hasSegments)
		{
			return;
		}
		geometry.Freeze();
		canvas.Children.Add(new System.Windows.Shapes.Path
		{
			Data = geometry,
			Stroke = BrushForWireframeRole(role),
			StrokeThickness = 1.75,
			IsHitTestVisible = false
		});
	}

	private void AddWireframeText(string title, string detail, double left, double top)
	{
		StackPanel stackPanel = new StackPanel
		{
			Background = CreateFrozenBrush(8, 13, 18, 220),
			IsHitTestVisible = false
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = title,
			FontWeight = FontWeights.SemiBold,
			Foreground = Brushes.White,
			Margin = new Thickness(10.0, 8.0, 10.0, 0.0)
		});
		stackPanel.Children.Add(new TextBlock
		{
			Text = detail,
			Foreground = new SolidColorBrush(Color.FromRgb(185, 215, 239)),
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(10.0, 4.0, 10.0, 8.0),
			MaxWidth = 760.0
		});
		Canvas.SetLeft(stackPanel, left);
		Canvas.SetTop(stackPanel, top);
		LiveWireframeCanvas.Children.Add(stackPanel);
	}

	private static SolidColorBrush BrushForWireframeRole(PreviewOverlayMeshFeatureRole role)
	{
		return role switch
		{
			PreviewOverlayMeshFeatureRole.Eye => WireframeEyeBrush,
			PreviewOverlayMeshFeatureRole.Brow => WireframeBrowBrush,
			PreviewOverlayMeshFeatureRole.Mouth => WireframeMouthBrush,
			PreviewOverlayMeshFeatureRole.Jaw => WireframeJawBrush,
			PreviewOverlayMeshFeatureRole.Nose => WireframeNoseBrush,
			PreviewOverlayMeshFeatureRole.Face => WireframeFaceBrush,
			_ => WireframeDefaultBrush
		};
	}

	private void UpdateFaceCueGuideOverlay(BitmapSource? bitmap)
	{
		if (_showLiveWireframePreview)
		{
			UpdateDirectX12TrackingOverlay(PreviewTrackingOverlay.Empty);
			if (FaceCueGuideCanvas.Visibility != Visibility.Collapsed)
			{
				FaceCueGuideCanvas.Children.Clear();
				FaceCueGuideCanvas.Visibility = Visibility.Collapsed;
			}
			return;
		}
		if (IsDirectX12PreviewSurfaceActive())
		{
			UpdateDirectX12TrackingOverlay(CreateNativePreviewTrackingOverlay());
			if (FaceCueGuideCanvas.Visibility != Visibility.Collapsed)
			{
				FaceCueGuideCanvas.Children.Clear();
				FaceCueGuideCanvas.Visibility = Visibility.Collapsed;
			}
			return;
		}
		FaceCueGuideCanvas.Children.Clear();
		FaceCueGuideCanvas.Visibility = Visibility.Visible;
		if (bitmap == null)
		{
			return;
		}
		Rect previewDisplayRect = GetPreviewDisplayRect(bitmap);
		if (previewDisplayRect.Width <= 0.0 || previewDisplayRect.Height <= 0.0)
		{
			return;
		}
		Color faceCueGuideColor = GetFaceCueGuideColor();
		SolidColorBrush fill = new SolidColorBrush(Color.FromArgb(34, faceCueGuideColor.R, faceCueGuideColor.G, faceCueGuideColor.B));
		SolidColorBrush stroke = new SolidColorBrush(Color.FromArgb(235, faceCueGuideColor.R, faceCueGuideColor.G, faceCueGuideColor.B));
		SolidColorBrush stroke2 = new SolidColorBrush(Color.FromArgb(175, 185, 215, 239));
		SolidColorBrush stroke3 = new SolidColorBrush(Color.FromArgb(150, faceCueGuideColor.R, faceCueGuideColor.G, faceCueGuideColor.B));
		FaceCueGuideLayout faceCueLayout = GetFaceCueLayout();
		Rect faceBox = faceCueLayout.GetFaceBox();
		Rect frameRegion = faceCueLayout.ToFrameRect(faceCueLayout.Jaw);
		AddFaceMeshOverlay(previewDisplayRect, _currentFaceLandmarkFrame);
		if (!_showFaceMeshOverlay)
		{
			AddGuideRegion(previewDisplayRect, faceBox, Brushes.Transparent, stroke2, 1.0);
			AddGuideRegion(previewDisplayRect, frameRegion, fill, stroke3, 2.0);
			AddGuideLine(previewDisplayRect, frameRegion.Left + frameRegion.Width * 0.16, frameRegion.Top + frameRegion.Height * 0.38, frameRegion.Right - frameRegion.Width * 0.16, frameRegion.Top + frameRegion.Height * 0.38, stroke, 3.0);
			AddGuideLine(previewDisplayRect, faceBox.Left + faceBox.Width * 0.5, faceBox.Top, faceBox.Left + faceBox.Width * 0.5, faceBox.Bottom, stroke2, 1.0);
			if (HasUsableFaceFeatureLock())
			{
				SolidColorBrush stroke4 = new SolidColorBrush(Color.FromArgb(230, 244, 211, 94));
				AddGuideRegion(previewDisplayRect, _currentFaceFeatureDetection.FaceBox, Brushes.Transparent, stroke4, 2.0);
			}
			if (_currentFaceLandmarkFrame.HasFace)
			{
				AddLandmarkContours(previewDisplayRect, _currentFaceLandmarkFrame);
			}
		}
	}

	private void UpdateDirectX12TrackingOverlay(PreviewTrackingOverlay overlay)
	{
		_directX12NativeCamera?.UpdateTrackingOverlay(overlay);
		GetDirectX12PreviewHost()?.UpdateTrackingOverlay(overlay);
	}

	private PreviewTrackingOverlay CreateNativePreviewTrackingOverlay()
	{
		if (!HasUsableFaceFeatureLock())
		{
			return PreviewTrackingOverlay.Empty;
		}
		long sourceTimestamp = Volatile.Read(ref _lastFaceFeatureLockTimestamp);
		FaceFeatureDetection currentFaceFeatureDetection = _currentFaceFeatureDetection;
		FaceLandmarkFrame currentFaceLandmarkFrame = _currentFaceLandmarkFrame;
		if (currentFaceFeatureDetection == _cachedNativeOverlayFeatureDetection && currentFaceLandmarkFrame == _cachedNativeOverlayLandmarkFrame && _cachedNativeOverlayIncludesFaceMesh == _showFaceMeshOverlay)
		{
			return _cachedNativeTrackingOverlay with
			{
				SourceTimestamp = sourceTimestamp,
				MaximumAge = MaximumLiveAwarenessFrameAge
			};
		}
		PreviewTrackingOverlay previewTrackingOverlay = CreateNativePreviewTrackingOverlay(
			currentFaceFeatureDetection,
			currentFaceLandmarkFrame,
			_showFaceMeshOverlay) with
		{
			SourceTimestamp = sourceTimestamp,
			MaximumAge = MaximumLiveAwarenessFrameAge
		};
		_cachedNativeOverlayFeatureDetection = currentFaceFeatureDetection;
		_cachedNativeOverlayLandmarkFrame = currentFaceLandmarkFrame;
		_cachedNativeOverlayIncludesFaceMesh = _showFaceMeshOverlay;
		_cachedNativeTrackingOverlay = previewTrackingOverlay;
		return previewTrackingOverlay;
	}

	private static PreviewTrackingOverlay CreateNativePreviewTrackingOverlay(
		FaceFeatureDetection featureDetection,
		FaceLandmarkFrame landmarkFrame,
		bool includeFaceMesh)
	{
		if (includeFaceMesh)
		{
			return new PreviewTrackingOverlay
			{
				FaceMesh = CreatePreviewOverlayMesh(landmarkFrame.DenseMeshPoints)
			};
		}
		IReadOnlyList<Point> leftBrow = CreateBrowDisplayOutline(landmarkFrame.LeftBrowContour);
		IReadOnlyList<Point> rightBrow = CreateBrowDisplayOutline(landmarkFrame.RightBrowContour);
		(IReadOnlyList<Point> Left, IReadOnlyList<Point> Right) eyes = CreateDenseMeshEyeContours(landmarkFrame);
		return new PreviewTrackingOverlay
		{
			FaceBox = ToPreviewOverlayRect(featureDetection.FaceBox),
			FaceContour = ToPreviewOverlayPolyline(landmarkFrame.FaceContour, closed: true),
			JawContour = ToPreviewOverlayPolyline(landmarkFrame.JawContour, closed: false),
			LeftEyeContour = ToPreviewOverlayPolyline(eyes.Left, closed: true),
			RightEyeContour = ToPreviewOverlayPolyline(eyes.Right, closed: true),
			LeftBrowContour = ToPreviewOverlayPolyline(leftBrow, closed: true),
			RightBrowContour = ToPreviewOverlayPolyline(rightBrow, closed: true),
			OuterLipContour = ToPreviewOverlayPolyline(landmarkFrame.OuterLipContour, closed: true, landmarkFrame.MouthReconstructed),
			InnerLipContour = ToPreviewOverlayPolyline(landmarkFrame.InnerLipContour, closed: true, landmarkFrame.MouthReconstructed)
		};
	}

	private static (IReadOnlyList<Point> Left, IReadOnlyList<Point> Right) CreateDenseMeshEyeContours(FaceLandmarkFrame frame)
	{
		IReadOnlyList<Point> readOnlyList = CreateDenseMeshContour(frame.DenseMeshPoints, DenseMeshEyeA);
		IReadOnlyList<Point> readOnlyList2 = CreateDenseMeshContour(frame.DenseMeshPoints, DenseMeshEyeB);
		if (readOnlyList.Count != DenseMeshEyeA.Length || readOnlyList2.Count != DenseMeshEyeB.Length)
		{
			return (frame.LeftEyeContour, frame.RightEyeContour);
		}
		return (MeanX(readOnlyList) <= MeanX(readOnlyList2)) ? (readOnlyList, readOnlyList2) : (readOnlyList2, readOnlyList);
	}

	private static IReadOnlyList<Point> CreateDenseMeshContour(IReadOnlyList<FaceMeshLandmarkPoint> denseMeshPoints, IReadOnlyList<int> indices)
	{
		Point[] array = new Point[indices.Count];
		int num = 0;
		foreach (int index in indices)
		{
			if ((uint)index >= (uint)denseMeshPoints.Count)
			{
				return Array.Empty<Point>();
			}
			FaceMeshLandmarkPoint faceMeshLandmarkPoint = denseMeshPoints[index];
			if (faceMeshLandmarkPoint.Index != index || !double.IsFinite(faceMeshLandmarkPoint.X) || !double.IsFinite(faceMeshLandmarkPoint.Y))
			{
				return Array.Empty<Point>();
			}
			array[num++] = new Point(faceMeshLandmarkPoint.X, faceMeshLandmarkPoint.Y);
		}
		return array;
	}

	private static double MeanX(IReadOnlyList<Point> points)
	{
		double num = 0.0;
		foreach (Point point in points)
		{
			num += point.X;
		}
		return num / (double)points.Count;
	}

	private static PreviewOverlayEdge[] CreateMediaPipePreviewMeshEdges()
	{
		(int, int)[] tessellationEdges = MediaPipeFaceMeshTopology.TessellationEdges;
		PreviewOverlayEdge[] array = new PreviewOverlayEdge[tessellationEdges.Length];
		for (int i = 0; i < tessellationEdges.Length; i++)
		{
			array[i] = new PreviewOverlayEdge(tessellationEdges[i].Item1, tessellationEdges[i].Item2);
		}
		return array;
	}

	private static PreviewOverlayIndexedPath[] CreateMediaPipePreviewMeshBaseFeaturePaths()
	{
		return new PreviewOverlayIndexedPath[8]
		{
			new PreviewOverlayIndexedPath(DenseMeshEyeA, Closed: true, PreviewOverlayMeshFeatureRole.Eye),
			new PreviewOverlayIndexedPath(DenseMeshEyeB, Closed: true, PreviewOverlayMeshFeatureRole.Eye),
			new PreviewOverlayIndexedPath(DenseMeshOuterLip, Closed: true, PreviewOverlayMeshFeatureRole.Mouth),
			new PreviewOverlayIndexedPath(DenseMeshInnerLip, Closed: true, PreviewOverlayMeshFeatureRole.Mouth),
			new PreviewOverlayIndexedPath(DenseMeshJawContour, Closed: false, PreviewOverlayMeshFeatureRole.Jaw),
			new PreviewOverlayIndexedPath(DenseMeshNoseBridge, Closed: false, PreviewOverlayMeshFeatureRole.Nose),
			new PreviewOverlayIndexedPath(DenseMeshNoseBase, Closed: false, PreviewOverlayMeshFeatureRole.Nose),
			new PreviewOverlayIndexedPath(DenseMeshFaceOval, Closed: true, PreviewOverlayMeshFeatureRole.Face)
		};
	}

	private static PreviewOverlayIndexedPath[] CreateMediaPipePreviewMeshFeaturePaths(IReadOnlyList<FaceMeshLandmarkPoint> denseMeshPoints)
	{
		IReadOnlyList<int> readOnlyList = MediaPipeBrowOutlineGeometry.BuildClosedOutlineIndices(denseMeshPoints, MediaPipeBrowOutlineGeometry.BrowAIndices);
		IReadOnlyList<int> readOnlyList2 = MediaPipeBrowOutlineGeometry.BuildClosedOutlineIndices(denseMeshPoints, MediaPipeBrowOutlineGeometry.BrowBIndices);
		int num = ((readOnlyList.Count >= 3) ? 1 : 0) + ((readOnlyList2.Count >= 3) ? 1 : 0);
		PreviewOverlayIndexedPath[] array = new PreviewOverlayIndexedPath[MediaPipePreviewMeshBaseFeaturePaths.Length + num];
		int destinationIndex = 0;
		array[destinationIndex++] = MediaPipePreviewMeshBaseFeaturePaths[0];
		array[destinationIndex++] = MediaPipePreviewMeshBaseFeaturePaths[1];
		if (readOnlyList.Count >= 3)
		{
			array[destinationIndex++] = new PreviewOverlayIndexedPath(readOnlyList, Closed: true, PreviewOverlayMeshFeatureRole.Brow);
		}
		if (readOnlyList2.Count >= 3)
		{
			array[destinationIndex++] = new PreviewOverlayIndexedPath(readOnlyList2, Closed: true, PreviewOverlayMeshFeatureRole.Brow);
		}
		Array.Copy(MediaPipePreviewMeshBaseFeaturePaths, 2, array, destinationIndex, MediaPipePreviewMeshBaseFeaturePaths.Length - 2);
		return array;
	}

	private static bool[] CreateMediaPipePreviewMeshFeaturePointMask()
	{
		bool[] array = new bool[468];
		PreviewOverlayIndexedPath[] mediaPipePreviewMeshBaseFeaturePaths = MediaPipePreviewMeshBaseFeaturePaths;
		foreach (PreviewOverlayIndexedPath previewOverlayIndexedPath in mediaPipePreviewMeshBaseFeaturePaths)
		{
			MarkFeaturePoints(array, previewOverlayIndexedPath.PointIndices);
		}
		MarkFeaturePoints(array, MediaPipeBrowOutlineGeometry.BrowAIndices);
		MarkFeaturePoints(array, MediaPipeBrowOutlineGeometry.BrowBIndices);
		return array;
	}

	private static void MarkFeaturePoints(bool[] featurePointMask, IReadOnlyList<int> pointIndices)
	{
		foreach (int pointIndex in pointIndices)
		{
			if ((uint)pointIndex < (uint)featurePointMask.Length)
			{
				featurePointMask[pointIndex] = true;
			}
		}
	}

	private static PreviewOverlayMesh? CreatePreviewOverlayMesh(IReadOnlyList<FaceMeshLandmarkPoint> denseMeshPoints)
	{
		if (denseMeshPoints.Count < 468)
		{
			return null;
		}
		PreviewOverlayPoint[] array = new PreviewOverlayPoint[denseMeshPoints.Count];
		Array.Fill(array, new PreviewOverlayPoint(double.NaN, double.NaN));
		foreach (FaceMeshLandmarkPoint denseMeshPoint in denseMeshPoints)
		{
			if ((uint)denseMeshPoint.Index < (uint)array.Length && double.IsFinite(denseMeshPoint.X) && double.IsFinite(denseMeshPoint.Y))
			{
				array[denseMeshPoint.Index] = new PreviewOverlayPoint(denseMeshPoint.X, denseMeshPoint.Y).Clamp();
			}
		}
		PreviewOverlayIndexedPath[] featurePaths = CreateMediaPipePreviewMeshFeaturePaths(denseMeshPoints);
		return new PreviewOverlayMesh(array, MediaPipePreviewMeshEdges, featurePaths, MediaPipePreviewMeshFeaturePointMask);
	}

	private void AddFaceMeshOverlay(Rect display, FaceLandmarkFrame frame)
	{
		if (!_showFaceMeshOverlay || !frame.HasDenseMesh)
		{
			return;
		}
		DrawMediaPipeWireframe(
			FaceCueGuideCanvas,
			frame.DenseMeshPoints,
			display,
			FaceMeshOverlayBrush,
			0.5,
			FaceMeshOverlayPointBrush);
	}

	private static PreviewOverlayPolyline? ToPreviewOverlayPolyline(IReadOnlyList<Point> points, bool closed, bool inferred = false)
	{
		if (points.Count < 2)
		{
			return null;
		}
		PreviewOverlayPoint[] array = new PreviewOverlayPoint[points.Count];
		int num = 0;
		foreach (Point point in points)
		{
			if (double.IsFinite(point.X) && double.IsFinite(point.Y))
			{
				array[num++] = new PreviewOverlayPoint(point.X, point.Y).Clamp();
			}
		}
		if (num < 2)
		{
			return null;
		}
		if (num != array.Length)
		{
			Array.Resize(ref array, num);
		}
		return new PreviewOverlayPolyline(array, closed, inferred);
	}

	private static IReadOnlyList<Point> CreateBrowDisplayOutline(IReadOnlyList<Point> points)
	{
		if (points.Count < 3)
		{
			return points;
		}
		Point[] array = new Point[points.Count];
		int num = 0;
		for (int i = 0; i < points.Count; i++)
		{
			Point point = points[i];
			if (double.IsFinite(point.X) && double.IsFinite(point.Y))
			{
				array[num++] = point;
			}
		}
		if (num < 3)
		{
			Array.Resize(ref array, num);
			return array;
		}
		Array.Resize(ref array, num);
		Array.Sort(array, delegate(Point left, Point right)
		{
			int num7 = left.X.CompareTo(right.X);
			return (num7 == 0) ? left.Y.CompareTo(right.Y) : num7;
		});
		int num2 = 1;
		for (int num3 = 1; num3 < array.Length; num3++)
		{
			if (array[num3] != array[num2 - 1])
			{
				array[num2++] = array[num3];
			}
		}
		if (num2 < 3)
		{
			Array.Resize(ref array, num2);
			return array;
		}
		Point[] array2 = new Point[num2 * 2];
		int count = 0;
		for (int num4 = 0; num4 < num2; num4++)
		{
			AppendHullPoint(array2, ref count, array[num4]);
		}
		int num5 = count;
		for (int num6 = num2 - 2; num6 >= 0; num6--)
		{
			while (count > num5 && count >= 2 && Cross(array2[count - 2], array2[count - 1], array[num6]) <= 0.0)
			{
				count--;
			}
			array2[count++] = array[num6];
		}
		count--;
		Array.Resize(ref array2, count);
		return array2;
	}

	private static void AppendHullPoint(Point[] hull, ref int count, Point point)
	{
		while (count >= 2 && Cross(hull[count - 2], hull[count - 1], point) <= 0.0)
		{
			count--;
		}
		hull[count++] = point;
	}

	private static double Cross(Point origin, Point a, Point b)
	{
		return (a.X - origin.X) * (b.Y - origin.Y) - (a.Y - origin.Y) * (b.X - origin.X);
	}

	private static PreviewOverlayRect? ToPreviewOverlayRect(Rect? region)
	{
		if (region.HasValue)
		{
			Rect valueOrDefault = region.GetValueOrDefault();
			if (!valueOrDefault.IsEmpty && !(valueOrDefault.Width <= 0.0) && !(valueOrDefault.Height <= 0.0))
			{
				return new PreviewOverlayRect(valueOrDefault.Left, valueOrDefault.Top, valueOrDefault.Right, valueOrDefault.Bottom).Clamp();
			}
		}
		return null;
	}

	private Rect GetPreviewDisplayRect(BitmapSource bitmap)
	{
		double actualWidth = PreviewHost.ActualWidth;
		double actualHeight = PreviewHost.ActualHeight;
		if (actualWidth <= 0.0 || actualHeight <= 0.0 || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
		{
			return Rect.Empty;
		}
		double num = Math.Min(actualWidth / (double)bitmap.PixelWidth, actualHeight / (double)bitmap.PixelHeight);
		double num2 = (double)bitmap.PixelWidth * num;
		double num3 = (double)bitmap.PixelHeight * num;
		return new Rect((actualWidth - num2) / 2.0, (actualHeight - num3) / 2.0, num2, num3);
	}

	private Color GetFaceCueGuideColor()
	{
		if (!_currentFaceLandmarkMetrics.HasFace)
		{
			return Color.FromRgb(74, 147, 214);
		}
		if (!_currentFaceLandmarkMetrics.IsEyeMeasurementUsable || !_currentFaceLandmarkMetrics.IsMouthMeasurementUsable)
		{
			return Color.FromRgb(215, 165, 58);
		}
		return Color.FromRgb(74, 163, 107);
	}

	private void AddGuideRegion(Rect display, Rect frameRegion, Brush fill, Brush stroke, double thickness)
	{
		Rect rect = ToDisplayRect(display, frameRegion);
		Rectangle element = new Rectangle
		{
			Width = rect.Width,
			Height = rect.Height,
			RadiusX = 3.0,
			RadiusY = 3.0,
			Fill = fill,
			Stroke = stroke,
			StrokeThickness = thickness
		};
		Canvas.SetLeft(element, rect.X);
		Canvas.SetTop(element, rect.Y);
		FaceCueGuideCanvas.Children.Add(element);
	}

	private void AddGuideLine(Rect display, double x1, double y1, double x2, double y2, Brush stroke, double thickness)
	{
		Line element = new Line
		{
			X1 = display.X + display.Width * x1,
			Y1 = display.Y + display.Height * y1,
			X2 = display.X + display.Width * x2,
			Y2 = display.Y + display.Height * y2,
			Stroke = stroke,
			StrokeThickness = thickness,
			StrokeStartLineCap = PenLineCap.Round,
			StrokeEndLineCap = PenLineCap.Round
		};
		FaceCueGuideCanvas.Children.Add(element);
	}

	private void AddLandmarkContours(Rect display, FaceLandmarkFrame frame)
	{
		SolidColorBrush solidColorBrush = new SolidColorBrush(Color.FromArgb(245, 122, 218, byte.MaxValue));
		SolidColorBrush stroke = new SolidColorBrush(Color.FromArgb(245, 245, 133, 176));
		SolidColorBrush stroke2 = new SolidColorBrush(Color.FromArgb(245, 196, 247, 163));
		SolidColorBrush stroke3 = new SolidColorBrush(Color.FromArgb(135, 185, 215, 239));
		AddGuidePolyline(display, frame.FaceContour, stroke3, 1.4, close: true);
		AddGuidePolyline(display, frame.JawContour, stroke3, 1.8, close: false);
		AddGuidePolyline(display, frame.LeftEyeContour, solidColorBrush, 2.4, close: true);
		AddGuidePolyline(display, frame.RightEyeContour, solidColorBrush, 2.4, close: true);
		AddGuidePolyline(display, CreateBrowDisplayOutline(frame.LeftBrowContour), stroke2, 0.5, close: true);
		AddGuidePolyline(display, CreateBrowDisplayOutline(frame.RightBrowContour), stroke2, 0.5, close: true);
		AddGuidePolyline(display, frame.OuterLipContour, stroke, 2.2, close: true, frame.MouthReconstructed);
		AddGuidePolyline(display, frame.InnerLipContour, stroke, 1.8, close: true, frame.MouthReconstructed);
	}

	private void AddGuidePolyline(Rect display, IReadOnlyList<Point> points, Brush stroke, double thickness, bool close, bool inferred = false)
	{
		if (points.Count < 2)
		{
			return;
		}
		Polyline polyline = new Polyline
		{
			Stroke = stroke,
			StrokeThickness = thickness,
			StrokeStartLineCap = PenLineCap.Round,
			StrokeEndLineCap = PenLineCap.Round,
			StrokeLineJoin = PenLineJoin.Round
		};
		if (inferred)
		{
			polyline.StrokeDashArray = CreateInferenceDashArray();
		}
		foreach (Point point in points)
		{
			polyline.Points.Add(ToDisplayPoint(display, point));
		}
		if (close)
		{
			polyline.Points.Add(ToDisplayPoint(display, points[0]));
		}
		FaceCueGuideCanvas.Children.Add(polyline);
	}

	private static DoubleCollection CreateInferenceDashArray()
	{
		return new DoubleCollection { 5.0, 3.0 };
	}

	private static Point ToDisplayPoint(Rect display, Point framePoint)
	{
		return new Point(display.X + display.Width * framePoint.X, display.Y + display.Height * framePoint.Y);
	}

	private static Rect ToDisplayRect(Rect display, double left, double top, double right, double bottom)
	{
		return new Rect(display.X + display.Width * left, display.Y + display.Height * top, display.Width * (right - left), display.Height * (bottom - top));
	}

	private static Rect ToDisplayRect(Rect display, Rect frameRegion)
	{
		return ToDisplayRect(display, frameRegion.Left, frameRegion.Top, frameRegion.Right, frameRegion.Bottom);
	}

	private static string FormatRatioPercent(double? value)
	{
		if (value.HasValue)
		{
			double valueOrDefault = value.GetValueOrDefault();
			return $"{valueOrDefault * 100.0:0}%";
		}
		return "--";
	}
}
