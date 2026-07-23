using System;
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
using AvatarBuilder.Modules.Storage.AvatarObservations;
using AvatarBuilder.Modules.Storage.AvatarObservations.Review;
using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.Deca;
using AvatarBuilder.Modules.Vision.Deca.StandardModel;
using AvatarBuilder.Modules.Vision.Diagnostics;
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

	private sealed record TrackingFidelityOption(int MaxOutputWidth, double MaxFramesPerSecond);

	private sealed record LiveWireframeProjectedPoint(int Index, double X, double Y, double Z);

	private sealed record FaceBoxTrackingFrameResult(FaceBoxSystem FaceBoxSystem, int FaceBoxSystemGeneration, FaceLandmarkTrackingResult TrackingResult, ThreeDdfaOnnxSidecarResponse? ThreeDdfaResponse, AvatarReconstructionSnapshot? ThreeDdfaSnapshot, string ProfileId, long SessionGeneration, DateTime CapturedAtUtc, BitmapSource SourceFrame, AvatarCaptureQualityAssessment CaptureQuality, FaceFrameGeometry FaceGeometry, double InferenceMilliseconds);

	private sealed record AvatarLearningState(bool Active, string Title, string Detail, Color Accent);

	private sealed record AvatarTrackingSanityState(string Detail, Color Accent);

	private sealed record AvatarReportSnapshot(string Folder, string SubjectId, string SubjectDisplayName, AvatarCaptureQualityAssessment CaptureQuality, bool UserLoggedIn, bool AvatarCaptureRequested, bool AvatarCaptureActive, string AvatarCaptureStatus, string AvatarCaptureCorrection, FaceFrameGeometry FaceFrameGeometry, FaceReconstructionLaneStatus ReconstructionLane);

	private sealed record AvatarReportSaveResult(string AvatarSystemDashboardPath, string AvatarModelHtmlPath);

	private sealed record AvatarLoginSelection(string ProfileId, string NewDisplayName);

	private const string DefaultAvatarProfileId = "chris";

	private const string DefaultAvatarProfileDisplayName = "Chris";

	private const string PreferredExternalOutputFolder = "D:\\Avatar Builder Output";

	private const string OutputFolderPointerFileName = "AvatarBuilderOutputFolder.txt";

	private const string AvatarLearningStartButtonText = "Start Avatar Capture";

	private const string AvatarLearningStopButtonText = "Stop Avatar Capture";

	private const string ShutdownLogFileName = "AvatarBuilder-shutdown.log";

	private const bool UseDecaAvatarReconstruction = false;

	private const bool UseThreeDdfaAvatarReconstructionFallback = false;

	private const double FallbackAvatarReconstructionSampleIntervalMilliseconds = 1000.0;

	private const double Insta360Link2ProHorizontalFovDegrees = 71.4;

	private static readonly TimeSpan FaceFeatureDetectionTargetInterval = TimeSpan.FromMilliseconds(15L);

	private static readonly TimeSpan RecoverableVisionErrorStatusInterval = TimeSpan.FromSeconds(3L);

	private static readonly HashSet<string> PhotoTrainingImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp" };

	private static readonly SolidColorBrush StartActionButtonBackground = CreateFrozenBrush(31, 122, 67);

	private static readonly SolidColorBrush StartActionButtonBorder = CreateFrozenBrush(82, 196, 123);

	private static readonly SolidColorBrush StopActionButtonBackground = CreateFrozenBrush(157, 47, 47);

	private static readonly SolidColorBrush StopActionButtonBorder = CreateFrozenBrush(224, 105, 105);

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

	private static readonly PreviewOverlayEdge[] MediaPipePreviewMeshEdges = CreateMediaPipePreviewMeshEdges();

	private static readonly PreviewOverlayIndexedPath[] MediaPipePreviewMeshBaseFeaturePaths = CreateMediaPipePreviewMeshBaseFeaturePaths();

	private static readonly bool[] MediaPipePreviewMeshFeaturePointMask = CreateMediaPipePreviewMeshFeaturePointMask();

	private readonly FfmpegCameraModeService _cameraModeService = new FfmpegCameraModeService();

	private readonly DirectShowCameraControlService _cameraControlService = new DirectShowCameraControlService();

	private readonly CameraPreviewService _previewService = new CameraPreviewService();

	private CompositeFaceLandmarkTracker? _faceLandmarkTracker = new CompositeFaceLandmarkTracker();

	private readonly FaceLandmarkTemporalReconstructor _faceLandmarkReconstructor = new FaceLandmarkTemporalReconstructor();

	private readonly FaceLandmarkMetricCalculator _faceLandmarkMetricCalculator = new FaceLandmarkMetricCalculator();

	private readonly FaceLockStabilityAnalyzer _faceLockStabilityAnalyzer = new FaceLockStabilityAnalyzer();

	private readonly FaceFrameGeometryEstimator _faceFrameGeometryEstimator = new FaceFrameGeometryEstimator();

	private readonly ThreeDdfaOnnxModelInfo _threeDdfaOnnxModelInfo;

	private readonly ThreeDdfaOnnxSidecarEnvironment _threeDdfaOnnxEnvironment;

	private readonly DecaSidecarEnvironment _decaEnvironment;

	private ThreeDdfaOnnxReconstructionClient? _threeDdfaAvatarClient;

	private ThreeDdfaOnnxReconstructionClient? _threeDdfaFaceBoxClient;

	private DecaReconstructionClient? _decaAvatarClient;

	private readonly AvatarBuilderStartupOptions _startupOptions;

	private readonly AvatarProfileStore _avatarProfileStore = new AvatarProfileStore();

	private readonly AvatarUserSession _avatarUserSession = new AvatarUserSession();

	private readonly AvatarCaptureQualityAnalyzer _avatarCaptureQualityAnalyzer = new AvatarCaptureQualityAnalyzer();

	private readonly AvatarObservationRepository _avatarObservationRepository = new AvatarObservationRepository();

	private readonly AvatarDataReviewServer _avatarDataReviewServer;

	private readonly AvatarObservationStorageService _avatarObservationStorageService;

	private readonly AvatarStandardModelStore _avatarStandardModelStore = new AvatarStandardModelStore();

	private readonly ManualStandardModelCaptureService _manualStandardModelCaptureService;

	private readonly AvatarModelHistoryStore _avatarModelHistoryStore = new AvatarModelHistoryStore();

	private readonly AvatarModelStore _avatarModelStore = new AvatarModelStore();

	private readonly AvatarSystemDashboardStore _avatarSystemDashboardStore = new AvatarSystemDashboardStore();

	private readonly VisionBenchmarkRecorder _visionBenchmarkRecorder = new VisionBenchmarkRecorder();

	private readonly PoseAlignmentAuditor _poseAlignmentAuditor = new PoseAlignmentAuditor();

	private readonly MediaPipeConvergenceAuditor _mediaPipeConvergenceAuditor = new MediaPipeConvergenceAuditor();

	private readonly MediaPipeGeometryPipeline _mediaPipeGeometryPipeline = new MediaPipeGeometryPipeline();

	private readonly object _faceLandmarkTrackerLock = new object();

	private readonly object _threeDdfaClientLock = new object();

	private readonly object _decaClientLock = new object();

	private readonly object _directX12PreviewLock = new object();

	private readonly object _personalFaceReportWriterLock = new object();

	private readonly object _avatarReportStorageLock = new object();

	private readonly object _threeDdfaTopologyLock = new object();

	private readonly Dictionary<string, AvatarStandardPoseSample> _standardPoseAtlas = new Dictionary<string, AvatarStandardPoseSample>(StringComparer.Ordinal);

	private readonly SemaphoreSlim _cameraLifecycleGate = new SemaphoreSlim(1, 1);

	private readonly DispatcherTimer _cameraHealthTimer;

	private readonly DispatcherTimer _photoTrainingTimer;

	private List<MeshTopologyEdge> _threeDdfaDenseTopologyEdges = new List<MeshTopologyEdge>();

	private IReadOnlyList<CameraDevice> _cameras = Array.Empty<CameraDevice>();

	private IReadOnlyList<string> _photoTrainingImagePaths = Array.Empty<string>();

	private CancellationTokenSource? _modeLoadCancellation;

	private CancellationTokenSource? _cameraStartCancellation;

	private string _standardModelStatus = "Standard model: log in to capture or review.";

	private string _outputFolder;

	private BitmapSource? _latestFrame;

	private BitmapSource? _photoTrainingFrame;

	private FaceFeatureDetection _currentFaceFeatureDetection = FaceFeatureDetection.None;

	private FaceLandmarkFrame _currentFaceLandmarkFrame = FaceLandmarkFrame.None;

	private FaceFeatureDetection? _cachedNativeOverlayFeatureDetection;

	private FaceLandmarkFrame? _cachedNativeOverlayLandmarkFrame;

	private AvatarReconstructionSnapshot? _cachedNativeOverlayAvatarSnapshot;

	private PreviewTrackingOverlay _cachedNativeTrackingOverlay = PreviewTrackingOverlay.Empty;

	private FaceLandmarkMetrics _currentFaceLandmarkMetrics = FaceLandmarkMetrics.None;

	private FaceLockStabilityAnalysis _currentFaceLockStabilityAnalysis = FaceLockStabilityAnalysis.Waiting;

	private FaceFrameGeometry _currentFaceFrameGeometry = FaceFrameGeometry.None;

	private ThreeDdfaOnnxSidecarResponse _currentThreeDdfaOnnxResponse = ThreeDdfaOnnxSidecarResponse.Waiting;

	private AvatarReconstructionSnapshot? _currentAvatarReconstructionSnapshot;

	private MediaPipeNormalizedFaceModel _currentMediaPipeGeometryModel = MediaPipeNormalizedFaceModel.Empty;

	private AvatarObservationCapture? _currentManualStandardModelCandidate;

	private ThreeDdfaOnnxSidecarFaceBox? _threeDdfaTrackingFaceBox;

	private AvatarCaptureQualityAssessment _currentAvatarCaptureQuality = AvatarCaptureQualityAssessment.Waiting;

	private AvatarProfileRegistry _avatarProfileRegistry = new AvatarProfileRegistry();

	private AvatarProfile _currentAvatarProfile = new AvatarProfile
	{
		Id = "chris",
		DisplayName = "Chris",
		DataFolderName = ""
	};

	private string _avatarSystemDashboardPath = "";

	private string _avatarModelHtmlPath = "";

	private AvatarReportSnapshot? _pendingAvatarReportSnapshot;

	private Task? _avatarReportWriterTask;

	private Direct3D12PreviewHost? _directX12PreviewHost;

	private Dx12Camera? _directX12NativeCamera;

	private DateTime _lastPreviewFrameAcceptedAt = DateTime.MinValue;

	private DateTime _cameraStartedAtUtc = DateTime.MinValue;

	private DateTime _lastCameraSourceFrameAtUtc = DateTime.MinValue;

	private DateTime _lastDirectX12DiagnosticsAtUtc = DateTime.MinValue;

	private DateTime _lastDirectX12AnalysisFrameAtUtc = DateTime.MinValue;

	private TimeSpan? _lastAvatarObservationWorkerDuration;

	private TimeSpan? _lastAvatarObservationWorkerStartWait;

	private TimeSpan? _lastMediaPipeGeometryProcessingDuration;

	private DateTime _previewReplacementWindowStartedAtUtc = DateTime.MinValue;

	private string _avatarCaptureGateReason = "waiting for face landmarks";

	private string _lastFaceBoxBackendStatus = "waiting";

	private double _directX12PreviewMaxRenderFramesPerSecond;

	private TimeSpan _directX12AnalysisFrameInterval = TimeSpan.FromSeconds(0.2);

	private int _directX12AnalysisMaxOutputWidth = 3840;

	private int _previewFramesReplacedSinceWarning;

	private int _uiFramePending;

	private int _previewWarningPending;

	private int _faceFeatureDetectionPending;

	private int _avatarReconstructionPending;

	private int _manualStandardModelSavePending;

	private long _lastRecoverableVisionErrorStatusTicks;

	private long _directX12FrameNumber;

	private int _directX12AnalysisWorkerQueued;

	private int _automaticCameraRecoveryAttempts;

	private bool _avatarLearningRequested;

	private bool _liveStandardModelModeActive;

	private bool _avatarModelInitializationPending;

	private bool _avatarModelReadyForCapture;

	private bool _avatarCaptureGateAccepted;

	private bool _showFaceMeshOverlay;

	private bool _showAvatarModelOverlay;

	private bool _showLiveWireframePreview;

	private bool _cachedNativeOverlayIncludesFaceMesh;

	private bool _cachedNativeOverlayIncludesAvatarModel;

	private bool _isDirectX12PreviewEnabled = true;

	private bool _isCameraEnabled;

	private bool _cameraRecoveryPending;

	private bool _isUpdatingCameraToggle;

	private bool _isChoosingCameraModeForFidelity;

	private bool _isRefreshingCameras;

	private bool _isLoadingCameraControls;

	private bool _isUpdatingCameraControlUi;

	private bool _isSnappingSlider;

	private bool _isClosing;

	private bool _shutdownStarted;

	private bool _shutdownCompleted;

	private bool _startupOptionsApplied;

	private bool _photoTrainingModeActive;

	private DecaIdentityFitProfile _photoTrainingIdentityFitProfile;

	private int _photoTrainingImageIndex = -1;

	private int _photoTrainingLoadGeneration;

	private int _previewActivationGeneration;

	private int _cameraLifecycleGeneration;

	private int _selectedTrackingFidelityIndex = 2;

	private int _faceBoxSystemGeneration;

	private FaceBoxSystem _selectedFaceBoxSystem;

	private FaceCueGuideLayout? _activeFaceCueLayout;

	private DateTime _lastFaceAutoFollowAt = DateTime.MinValue;

	private DateTime _lastFaceFeatureDetectionAt = DateTime.MinValue;

	private DateTime _lastFaceFeatureLockAt = DateTime.MinValue;

	private DateTime _lastAvatarReconstructionRequestAtUtc = DateTime.MinValue;

	private DateTime _lastThreeDdfaFaceBoxesAtUtc = DateTime.MinValue;

	private static readonly IReadOnlyList<TrackingFidelityOption> TrackingFidelityOptions = new global::_003C_003Ez__ReadOnlyArray<TrackingFidelityOption>(new TrackingFidelityOption[3]
	{
		new TrackingFidelityOption(960, 15.0),
		new TrackingFidelityOption(1920, 18.0),
		new TrackingFidelityOption(3840, 15.0)
	});

	private static readonly TimeSpan CameraStallTimeout = TimeSpan.FromSeconds(6L);

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

	private bool HasCompleteHumanStandardModel => AvatarStandardPoseGrid.IsComplete(_standardPoseAtlas);

	private bool HasRequiredCaptureFoundation
	{
		get
		{
			if (!IsMediaPipeOnlyGeometryMode)
			{
				return HasCompleteHumanStandardModel;
			}
			return true;
		}
	}

	private bool IsStandardModelBuildModeActive
	{
		get
		{
			if (!_liveStandardModelModeActive)
			{
				return _photoTrainingModeActive;
			}
			return true;
		}
	}

	private bool IsDecaAvatarReconstructionActive => false;

	private bool IsDecaFallbackActive => false;

	private bool IsAvatarReconstructionReady
	{
		get
		{
			if (!IsMediaPipeOnlyGeometryMode || !_mediaPipeGeometryPipeline.IsConfigured)
			{
				return IsDecaAvatarReconstructionActive;
			}
			return true;
		}
	}

	private bool IsMediaPipeOnlyGeometryMode => true;

	private string ActiveAvatarReconstructionBackendId
	{
		get
		{
			if (!IsDecaAvatarReconstructionActive)
			{
				if (!IsMediaPipeOnlyGeometryMode)
				{
					return "3ddfa-v2-onnx-reconstruction";
				}
				return "mediapipe-geometry-measurement-v1";
			}
			return "deca-flame-recurrent-v4";
		}
	}

	private string ActiveAvatarReconstructionName
	{
		get
		{
			if (!IsDecaAvatarReconstructionActive)
			{
				if (!IsDecaFallbackActive)
				{
					if (!IsMediaPipeOnlyGeometryMode)
					{
						return "3DDFA_V2 ONNX";
					}
					return "MediaPipe geometry";
				}
				return "3DDFA_V2 ONNX (DECA fallback)";
			}
			return "DECA/FLAME";
		}
	}

	private string ActiveAvatarReconstructionReadinessStatus
	{
		get
		{
			if (IsMediaPipeOnlyGeometryMode)
			{
				if (!_mediaPipeGeometryPipeline.IsConfigured)
				{
					return "MediaPipe visible-evidence geometry is waiting for an avatar user profile.";
				}
				return "MediaPipe visible-evidence geometry is active and persistent; DECA/FLAME and 3DDFA are paused.";
			}
			if (IsDecaAvatarReconstructionActive)
			{
				return _decaEnvironment.Status;
			}
			return _threeDdfaOnnxEnvironment.Status;
		}
	}

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

	public MainWindow()
		: this(AvatarBuilderStartupOptions.Default)
	{
	}

	public MainWindow(AvatarBuilderStartupOptions startupOptions)
	{
		_startupOptions = startupOptions ?? AvatarBuilderStartupOptions.Default;
		_threeDdfaOnnxModelInfo = ThreeDdfaOnnxModelInfo.Load();
		_threeDdfaOnnxEnvironment = ThreeDdfaOnnxSidecarEnvironment.Detect(_threeDdfaOnnxModelInfo);
		_decaEnvironment = DecaSidecarEnvironment.Detect();
		_threeDdfaAvatarClient = null;
		_decaAvatarClient = null;
		_manualStandardModelCaptureService = new ManualStandardModelCaptureService(_avatarObservationRepository, _avatarStandardModelStore);
		_avatarObservationStorageService = new AvatarObservationStorageService(_avatarObservationRepository, FinalizeAvatarObservationBatchAsync);
		_avatarDataReviewServer = new AvatarDataReviewServer(_avatarObservationRepository);
		_avatarObservationStorageService.BatchCompleted += AvatarObservationBatchCompleted;
		_avatarObservationStorageService.WorkerStarted += AvatarObservationWorkerStarted;
		_avatarObservationStorageService.WorkerCompleted += AvatarObservationWorkerCompleted;
		InitializeComponent();
		_mediaPipeGeometryPipeline.ModelUpdated += MediaPipeGeometryPipelineModelUpdated;
		_outputFolder = ResolveInitialOutputFolder(_startupOptions.OutputFolder);
		_visionBenchmarkRecorder.SetOutputRoot(_outputFolder);
		ResetAvatarCaptureGate("waiting for face landmarks");
		_previewService.FrameAvailable += PreviewFrameAvailable;
		_previewService.CameraFrameAvailable += PreviewCameraFrameAvailable;
		_previewService.StatusChanged += PreviewStatusChanged;
		_cameraHealthTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromSeconds(1L)
		};
		_cameraHealthTimer.Tick += CameraHealthTimerTick;
		_photoTrainingTimer = new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromMilliseconds(30L)
		};
		_photoTrainingTimer.Tick += PhotoTrainingTimerTick;
	}

	private async void WindowLoaded(object sender, RoutedEventArgs e)
	{
		DarkWindowFrame.Apply(this);
		EnsureOutputFolderConfiguredForLaunch();
		InitializeAvatarProfiles(!_startupOptions.SkipLoginPrompt);
		_poseAlignmentAuditor.SetOutputRoot(GetAvatarDataFolder());
		_mediaPipeConvergenceAuditor.SetOutputRoot(GetAvatarDataFolder());
		UpdateFaceBoxSystemMenuChecks();
		UpdateFaceBoxOptionsUi();
		UpdateTrackingFidelityMenuChecks();
		ApplyTrackingFidelity();
		UpdateSettingLabels();
		if (IsAvatarUserLoggedIn)
		{
			await InitializeAvatarModelAfterLoginAsync(showPopup: true);
		}
		PrepareAvatarCaptureFolder(showStatus: false);
		UpdateAvatarLearningStatusUi();
		UpdatePhotoTrainingUi();
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
			_lastDirectX12AnalysisFrameAtUtc = DateTime.MinValue;
			BitmapSource latestFrame = _latestFrame;
			if (latestFrame != null)
			{
				_lastFaceFeatureDetectionAt = DateTime.MinValue;
				QueueFaceFeatureDetection(latestFrame, DateTime.UtcNow);
			}
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
			if (_startupOptions.StartAvatarLearning && IsAvatarUserLoggedIn && _avatarModelReadyForCapture && HasRequiredCaptureFoundation)
			{
				_avatarLearningRequested = true;
			}
			if (_startupOptions.OpenAvatarSystem)
			{
				OpenAvatarSystemClicked(this, new RoutedEventArgs());
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
			ResetAvatarCaptureGate("no avatar user logged in; capture stopped");
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
		_avatarObservationStorageService.DiscardPendingCandidates();
		if (_avatarUserSession.IsLoggedIn && !string.Equals(_avatarUserSession.LoggedInProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))
		{
			_avatarUserSession.LogOut();
		}
		_currentAvatarProfile = profile;
		_avatarLearningRequested = false;
		_liveStandardModelModeActive = false;
		_poseAlignmentAuditor.SetOutputRoot(GetAvatarDataFolder());
		_mediaPipeConvergenceAuditor.SetOutputRoot(GetAvatarDataFolder());
		_avatarProfileStore.SelectProfile(_outputFolder, _avatarProfileRegistry, profile.Id);
		if (loadModel)
		{
			ResetAvatarRuntimeForProfile("selected avatar profile changed; login required before avatar capture");
		}
		UpdateAvatarSessionUi();
		UpdateAvatarLearningStatusUi();
	}

	private void LogInAvatarProfile(AvatarProfile profile, bool loadModel, bool announce)
	{
		StopPhotoTrainingMode(announce: false);
		_avatarObservationStorageService.DiscardPendingCandidates();
		_standardPoseAtlas.Clear();
		_liveStandardModelModeActive = false;
		if (!string.Equals(CurrentAvatarProfileId, profile.Id, StringComparison.OrdinalIgnoreCase) || loadModel)
		{
			ApplyCurrentAvatarProfile(profile, loadModel);
		}
		_avatarLearningRequested = false;
		_avatarModelReadyForCapture = false;
		_avatarUserSession.LogIn(profile.Id);
		_currentThreeDdfaOnnxResponse = ThreeDdfaOnnxSidecarResponse.Waiting;
		_currentAvatarReconstructionSnapshot = null;
		_currentManualStandardModelCandidate = null;
		ResetAvatarCaptureGate("avatar user logged in; waiting for high-confidence face tracking");
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
		StopPhotoTrainingMode(announce: false);
		_avatarObservationStorageService.DiscardPendingCandidates();
		_standardPoseAtlas.Clear();
		_liveStandardModelModeActive = false;
		_avatarLearningRequested = false;
		_avatarModelReadyForCapture = false;
		_avatarUserSession.LogOut();
		_currentThreeDdfaOnnxResponse = ThreeDdfaOnnxSidecarResponse.Waiting;
		_currentAvatarReconstructionSnapshot = null;
		_currentManualStandardModelCandidate = null;
		ResetAvatarCaptureGate("no avatar user logged in; capture stopped");
		UpdateAvatarCaptureQuality();
		UpdateAvatarSessionUi();
		UpdateAvatarLearningStatusUi();
		QueueAvatarSessionStatusReport();
		if (announce)
		{
			SetStatus(currentAvatarProfileDisplayName + " logged out. Avatar capture has stopped.");
		}
	}

	private void UpdateAvatarSessionUi()
	{
		bool isAvatarUserLoggedIn = IsAvatarUserLoggedIn;
		LoginLogoutMenuItem.Header = (isAvatarUserLoggedIn ? ("_Logout " + CurrentAvatarProfileDisplayName) : "_Login...");
		LoginLogoutMenuItem.IsEnabled = !_avatarModelInitializationPending;
		AvatarLoginStatusText.Text = ((!isAvatarUserLoggedIn) ? "No avatar user logged in. Use File > Login to begin a capture session." : (_avatarModelInitializationPending ? ("Logged in as " + CurrentAvatarProfileDisplayName + ". Please wait while the stored avatar model is calculated.") : ((!_avatarModelReadyForCapture) ? ("Logged in as " + CurrentAvatarProfileDisplayName + ", but the avatar model is not ready. Log out and back in to retry.") : ((!HasRequiredCaptureFoundation) ? ("Logged in as " + CurrentAvatarProfileDisplayName + ". Build the 9-view Standard Model before starting Avatar Capture.") : (IsMediaPipeOnlyGeometryMode ? ("Logged in as " + CurrentAvatarProfileDisplayName + ". MediaPipe measured geometry is ready; Avatar Capture can be started.") : ("Logged in as " + CurrentAvatarProfileDisplayName + ". The 9-view Standard Model is ready; Avatar Capture can be started."))))));
		AvatarLoginStatusText.Foreground = new SolidColorBrush(isAvatarUserLoggedIn ? Color.FromRgb(128, 224, 164) : Color.FromRgb(byte.MaxValue, 207, 122));
		AvatarLearningToggleButton.IsEnabled = _avatarLearningRequested || (isAvatarUserLoggedIn && _avatarModelReadyForCapture && !_avatarModelInitializationPending && HasRequiredCaptureFoundation && !IsStandardModelBuildModeActive);
		UpdateStandardModelUi();
		UpdatePhotoTrainingUi();
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
			if (IsMediaPipeOnlyGeometryMode)
			{
				_currentMediaPipeGeometryModel = await _mediaPipeGeometryPipeline.ConfigureProfileAsync(folder, CurrentAvatarProfileId, CurrentAvatarProfileDisplayName);
			}
			else
			{
				AvatarReportSnapshot snapshot = CreateAvatarReportSnapshot(folder);
				ApplyAvatarReportSaveResult(await Task.Run(() => WriteAvatarReports(snapshot, null, forceFullModelRebuild: true)));
				SeedDecaRecurrentModelFromStoredAvatar(folder);
			}
			if (!_isClosing && IsAvatarUserLoggedIn && string.Equals(profileId, CurrentAvatarProfileId, StringComparison.Ordinal))
			{
				_avatarModelReadyForCapture = true;
				SetStatus(IsMediaPipeOnlyGeometryMode ? (CurrentAvatarProfileDisplayName + "'s measured MediaPipe geometry is loaded. Avatar capture is ready.") : (CurrentAvatarProfileDisplayName + "'s stored avatar model is calculated. Avatar capture is ready."));
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
					Text = "Calculating Avatar Model",
					FontSize = 18.0,
					FontWeight = FontWeights.SemiBold,
					Margin = new Thickness(0.0, 0.0, 0.0, 12.0)
				},
				(UIElement)new TextBlock
				{
					Text = "Please wait while the retained scans are calculated into the model. Full recalculation happens once at login; capture uses incremental five-scan updates afterward.",
					Foreground = new SolidColorBrush(Color.FromRgb(185, 215, 239)),
					TextWrapping = TextWrapping.Wrap
				}
			}
		};
		return window;
	}

	private void QueueAvatarSessionStatusReport()
	{
		if (!base.IsLoaded)
		{
			return;
		}
		try
		{
			QueueAvatarReportSave(CreateAvatarReportSnapshot(GetAvatarDataFolder()));
		}
		catch
		{
		}
	}

	private void ResetAvatarCaptureGate(string reason, bool accepted = false)
	{
		_avatarCaptureGateAccepted = accepted;
		_avatarCaptureGateReason = (string.IsNullOrWhiteSpace(reason) ? "waiting for face landmarks" : reason);
	}

	private void ResetAvatarRuntimeForProfile(string reason)
	{
		_liveStandardModelModeActive = false;
		ResetAvatarCaptureGate(reason);
		_currentAvatarCaptureQuality = AvatarCaptureQualityAssessment.Waiting;
		_currentThreeDdfaOnnxResponse = ThreeDdfaOnnxSidecarResponse.Waiting;
		_currentAvatarReconstructionSnapshot = null;
		_currentManualStandardModelCandidate = null;
		_avatarSystemDashboardPath = "";
		_avatarModelHtmlPath = "";
		lock (_decaClientLock)
		{
			_decaAvatarClient?.SetCurrentModelShapeCoefficients(Array.Empty<double>());
		}
	}

	private void SeedDecaRecurrentModelFromStoredAvatar(string folder)
	{
		AvatarStandardModel avatarStandardModel = _avatarStandardModelStore.Read(folder);
		_standardPoseAtlas.Clear();
		if (avatarStandardModel?.PoseAtlas != null)
		{
			foreach (KeyValuePair<string, AvatarStandardPoseSample> poseAtla in avatarStandardModel.PoseAtlas)
			{
				if (AvatarStandardPoseGrid.DirectionKeys.Contains<string>(poseAtla.Key, StringComparer.Ordinal) && AvatarStandardPoseGrid.IsStructurallyComplete(poseAtla.Value))
				{
					_standardPoseAtlas[poseAtla.Key] = poseAtla.Value;
				}
			}
		}
		AvatarModel avatarModel = (((object)avatarStandardModel == null) ? _avatarModelStore.Read(folder) : null);
		IReadOnlyList<double> currentModelShapeCoefficients = avatarStandardModel?.ShapeCoefficients ?? avatarModel?.Identity.MappedShapeCoefficients;
		lock (_decaClientLock)
		{
			_decaAvatarClient?.SetCurrentModelShapeCoefficients(currentModelShapeCoefficients);
		}
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
		Hide();
		WriteShutdownTrace("Shutdown started.");
		TryShutdownStep(_cameraHealthTimer.Stop);
		_cameraHealthTimer.Tick -= CameraHealthTimerTick;
		TryShutdownStep(_photoTrainingTimer.Stop);
		_photoTrainingTimer.Tick -= PhotoTrainingTimerTick;
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
		TryShutdownStep(_previewService.Dispose);
		TryShutdownStep(ResetPreviewFramePump);
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
		DecaReconstructionClient decaClient;
		lock (_decaClientLock)
		{
			decaClient = _decaAvatarClient;
			_decaAvatarClient = null;
		}
		TryShutdownStep(delegate
		{
			decaClient?.Dispose();
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
		_avatarObservationStorageService.BatchCompleted -= AvatarObservationBatchCompleted;
		_avatarObservationStorageService.WorkerStarted -= AvatarObservationWorkerStarted;
		_avatarObservationStorageService.WorkerCompleted -= AvatarObservationWorkerCompleted;
		try
		{
			_ = 2;
			try
			{
				WriteShutdownTrace("Stopping avatar storage worker.");
				await _avatarObservationStorageService.DisposeAsync();
				WriteShutdownTrace("Avatar storage worker stopped.");
			}
			catch (Exception ex3)
			{
				WriteShutdownTrace("Avatar storage shutdown failed: " + ex3.Message);
			}
		}
		finally
		{
			try
			{
				WriteShutdownTrace("Stopping local avatar review server.");
				await _avatarDataReviewServer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3L));
				WriteShutdownTrace("Local avatar review server stopped.");
			}
			catch (Exception ex4)
			{
				WriteShutdownTrace("Local avatar review shutdown failed: " + ex4.Message);
			}
			TryShutdownStep(_visionBenchmarkRecorder.Dispose);
			TryShutdownStep(_mediaPipeConvergenceAuditor.Dispose);
			_shutdownCompleted = true;
			WriteShutdownTrace("Shutdown completed; closing WPF window.");
			Close();
		}
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
			CameraComboBox.SelectedIndex = 0;
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
				SelectRecommendedCameraModeForFidelity(replaceAutoOnly: false);
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
		if (!_isChoosingCameraModeForFidelity && _isCameraEnabled)
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
		else if (_photoTrainingModeActive && result != FaceBoxSystem.MediaPipe)
		{
			UpdateFaceBoxSystemMenuChecks();
			SetStatus("The Picture Standard Model builder keeps MediaPipe active so every still uses the same landmark pipeline.");
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

	private void SwitchFaceBoxSystem(FaceBoxSystem selectedSystem)
	{
		_selectedFaceBoxSystem = selectedSystem;
		Interlocked.Increment(ref _faceBoxSystemGeneration);
		ResetFaceFeatureDetectionFramePump();
		ResetFaceBoxDiagnostics();
		_currentFaceFeatureDetection = FaceFeatureDetection.None;
		_lastFaceFeatureLockAt = DateTime.MinValue;
		_activeFaceCueLayout = null;
		_threeDdfaTrackingFaceBox = null;
		_lastThreeDdfaFaceBoxesAtUtc = DateTime.MinValue;
		CompositeFaceLandmarkTracker faceLandmarkTracker;
		lock (_faceLandmarkTrackerLock)
		{
			faceLandmarkTracker = _faceLandmarkTracker;
			_faceLandmarkTracker = ((selectedSystem == FaceBoxSystem.MediaPipe) ? new CompositeFaceLandmarkTracker() : null);
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
		TrackingFidelityOption selectedTrackingFidelityOption = GetSelectedTrackingFidelityOption();
		if (selectedTrackingFidelityOption.MaxOutputWidth < 3840)
		{
			return Math.Clamp(selectedTrackingFidelityOption.MaxOutputWidth, 640, 960);
		}
		return 1920;
	}

	private void ResetFaceBoxDiagnostics()
	{
		_lastFaceBoxBackendStatus = "waiting";
	}

	private async void TrackingFidelityMenuItemClicked(object sender, RoutedEventArgs e)
	{
		if (!(sender is MenuItem { Tag: var tag }) || !int.TryParse(tag?.ToString(), out var result) || result < 0 || result >= TrackingFidelityOptions.Count)
		{
			UpdateTrackingFidelityMenuChecks();
			return;
		}
		_selectedTrackingFidelityIndex = result;
		UpdateTrackingFidelityMenuChecks();
		ApplyTrackingFidelity();
		SelectRecommendedCameraModeForFidelity(replaceAutoOnly: true);
		if (_isCameraEnabled)
		{
			await RestartPreviewAsync();
		}
	}

	private void UpdateTrackingFidelityMenuChecks()
	{
		TrackingFidelitySafeMenuItem.IsChecked = _selectedTrackingFidelityIndex == 0;
		TrackingFidelityHdMenuItem.IsChecked = _selectedTrackingFidelityIndex == 1;
		TrackingFidelity4KMenuItem.IsChecked = _selectedTrackingFidelityIndex == 2;
	}

	private void ApplyTrackingFidelity()
	{
		TrackingFidelityOption selectedTrackingFidelityOption = GetSelectedTrackingFidelityOption();
		int trackingAnalysisOutputWidth = GetTrackingAnalysisOutputWidth(selectedTrackingFidelityOption);
		_previewService.MaxOutputWidth = trackingAnalysisOutputWidth;
		_previewService.MaxOutputFramesPerSecond = selectedTrackingFidelityOption.MaxFramesPerSecond;
		_directX12AnalysisMaxOutputWidth = trackingAnalysisOutputWidth;
		_directX12AnalysisFrameInterval = TimeSpan.FromSeconds(1.0 / Math.Clamp(selectedTrackingFidelityOption.MaxFramesPerSecond, 1.0, 60.0));
		lock (_faceLandmarkTrackerLock)
		{
			if (_faceLandmarkTracker != null)
			{
				_faceLandmarkTracker.MaxDetectionDimension = GetFaceLandmarkDetectionDimension();
			}
		}
	}

	private static int GetTrackingAnalysisOutputWidth(TrackingFidelityOption option)
	{
		if (option.MaxOutputWidth >= 3840)
		{
			return 1920;
		}
		return option.MaxOutputWidth;
	}

	private TrackingFidelityOption GetSelectedTrackingFidelityOption()
	{
		return TrackingFidelityOptions[Math.Clamp(_selectedTrackingFidelityIndex, 0, TrackingFidelityOptions.Count - 1)];
	}

	private void SelectRecommendedCameraModeForFidelity(bool replaceAutoOnly)
	{
		CameraVideoMode cameraVideoMode = CameraModeComboBox.SelectedItem as CameraVideoMode;
		if (replaceAutoOnly && cameraVideoMode != null && !cameraVideoMode.IsAuto)
		{
			return;
		}
		CameraVideoMode cameraVideoMode2 = FindRecommendedCameraMode(CameraModeComboBox.Items.OfType<CameraVideoMode>().ToList(), GetSelectedTrackingFidelityOption());
		if (cameraVideoMode2 == null || IsSameCameraMode(cameraVideoMode, cameraVideoMode2))
		{
			if (CameraModeComboBox.SelectedIndex < 0 && CameraModeComboBox.Items.Count > 0)
			{
				CameraModeComboBox.SelectedIndex = 0;
			}
			return;
		}
		_isChoosingCameraModeForFidelity = true;
		try
		{
			CameraModeComboBox.SelectedItem = cameraVideoMode2;
		}
		finally
		{
			_isChoosingCameraModeForFidelity = false;
		}
	}

	private static CameraVideoMode? FindRecommendedCameraMode(IReadOnlyList<CameraVideoMode> modes, TrackingFidelityOption option)
	{
		return CameraModeRecommendation.FindRecommendedMode(modes, option.MaxOutputWidth, option.MaxFramesPerSecond);
	}

	private static bool IsSameCameraMode(CameraVideoMode? left, CameraVideoMode right)
	{
		if (left != null && left.IsAuto == right.IsAuto && left.Width == right.Width && left.Height == right.Height && Nullable.Equals(left.FramesPerSecond, right.FramesPerSecond))
		{
			return string.Equals(left.InputFormat, right.InputFormat, StringComparison.OrdinalIgnoreCase);
		}
		return false;
	}

	private async void CameraToggleChanged(object sender, RoutedEventArgs e)
	{
		if (_isUpdatingCameraToggle)
		{
			return;
		}
		if (_photoTrainingModeActive)
		{
			SetCameraToggle(enabled: false);
			SetStatus("Stop the Picture Standard Model builder before starting the webcam.");
			return;
		}
		if (CameraToggle.IsChecked == true)
		{
			_automaticCameraRecoveryAttempts = 0;
			await StartPreviewAsync();
		}
		else
		{
			_currentManualStandardModelCandidate = null;
			await StopPreviewAsync();
		}
		UpdateStandardModelUi();
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

	private void AvatarModelOverlayMenuItemClicked(object sender, RoutedEventArgs e)
	{
		_showAvatarModelOverlay = AvatarModelOverlayMenuItem.IsChecked;
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
			_cameraRecoveryPending = false;
			SetCameraToggle(enabled: false);
			SetStatus("Choose a camera first.");
			return;
		}
		CameraVideoMode mode = (CameraModeComboBox.SelectedItem as CameraVideoMode) ?? CameraVideoMode.Auto;
		_cameraStartedAtUtc = DateTime.UtcNow;
		_lastCameraSourceFrameAtUtc = DateTime.MinValue;
		ApplyTrackingFidelity();
		SetPreviewState($"Starting {camera.Name} ({mode.Label})", null);
		SetStatus($"Opening camera: {camera.Name} ({mode.Label})");
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
			_cameraRecoveryPending = false;
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
		SetCameraToggle(_isCameraEnabled);
		if (_isCameraEnabled)
		{
			SetStatus($"Camera active: {camera.Name} ({mode.Label})");
		}
		else
		{
			SetPreviewState("Camera failed to start", null);
			SetStatus("Camera failed to open. Close other webcam/AI apps and try Auto or a lower mode.");
		}
		_cameraRecoveryPending = false;
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

	private async Task StopPreviewAsync(bool keepToggleChecked = false, TimeSpan? gateTimeout = null)
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
			StopPreviewCore(keepToggleChecked);
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

	private void StopPreviewCore(bool keepToggleChecked = false)
	{
		DisposeDirectX12NativeCamera();
		_previewService.Stop();
		DisposeDirectX12PreviewHost();
		ResetDirectX12AnalysisFramePump();
		ResetPreviewFramePump();
		_isCameraEnabled = false;
		_cameraStartedAtUtc = DateTime.MinValue;
		_lastCameraSourceFrameAtUtc = DateTime.MinValue;
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
		if (_isClosing || !_isCameraEnabled || _cameraRecoveryPending)
		{
			return;
		}
		DateTime utcNow = DateTime.UtcNow;
		DateTime dateTime = ((_lastCameraSourceFrameAtUtc == DateTime.MinValue) ? _cameraStartedAtUtc : _lastCameraSourceFrameAtUtc);
		if (dateTime == DateTime.MinValue || utcNow - dateTime < CameraStallTimeout)
		{
			return;
		}
		if (_automaticCameraRecoveryAttempts >= 1)
		{
			_cameraRecoveryPending = true;
			await StopPreviewAsync();
			SetPreviewState("Camera stream stopped", null);
			SetStatus("The camera stopped delivering frames after one recovery attempt. The camera was turned off; close other camera apps, then turn it on again.");
			_cameraRecoveryPending = false;
			return;
		}
		_automaticCameraRecoveryAttempts++;
		_cameraRecoveryPending = true;
		if (_directX12NativeCamera != null && CameraComboBox.SelectedItem is CameraDevice camera)
		{
			CameraVideoMode mode = (CameraModeComboBox.SelectedItem as CameraVideoMode) ?? CameraVideoMode.Auto;
			TextureNativePreviewPolicy.RememberPreviewFailure(camera, mode, "native texture stream stopped delivering frames");
		}
		SetStatus("Camera stream stalled. Retrying once through the safe camera path...");
		await StopPreviewAsync(keepToggleChecked: true);
		await base.Dispatcher.InvokeAsync((Func<Task>)StartPreviewAsync, DispatcherPriority.ApplicationIdle).Task.Unwrap();
	}

	private void PreviewFrameAvailable(object? sender, BitmapSource frame)
	{
		if (_isClosing || Interlocked.CompareExchange(ref _uiFramePending, 1, 0) != 0)
		{
			TrackPreviewFrameReplacement();
			return;
		}
		base.Dispatcher.InvokeAsync(delegate
		{
			ProcessPreviewFrame(frame);
		}, DispatcherPriority.Background);
	}

	private void PreviewCameraFrameAvailable(object? sender, CameraFrame frame)
	{
		_lastCameraSourceFrameAtUtc = DateTime.UtcNow;
		_cameraRecoveryPending = false;
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
			ApplyDirectX12PreviewPresentationState();
			TextureNativePreviewPolicy.ForgetPreviewFailure(camera, mode);
			_lastDirectX12AnalysisFrameAtUtc = DateTime.MinValue;
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
		_lastCameraSourceFrameAtUtc = DateTime.UtcNow;
		_cameraRecoveryPending = false;
		if (frame.FrameNumber % 120 == 0L && !((DateTime.UtcNow - _lastDirectX12DiagnosticsAtUtc).TotalSeconds < 6.0))
		{
			base.Dispatcher.InvokeAsync(delegate
			{
				SetStatus($"Native DX12 camera: {frame.Width}x{frame.Height}@{frame.FramesPerSecond:0.###} {frame.MediaSubtype} via {frame.DeviceMode}; preview cap {FormatPreviewRenderLimit()}.");
			}, DispatcherPriority.Background);
		}
	}

	private void DirectX12NativeTextureFrameAvailable(object? sender, TextureNativeFrameLease frame)
	{
		if (!TryBeginDirectX12Analysis())
		{
			return;
		}
		TextureNativeFrameLease textureNativeFrameLease = null;
		bool flag = false;
		try
		{
			textureNativeFrameLease = frame.DuplicatePreviewData();
			if (textureNativeFrameLease != null)
			{
				TextureNativeFrameLease ownedFrame = textureNativeFrameLease;
				textureNativeFrameLease = null;
				flag = true;
				Task.Run(delegate
				{
					ProcessDirectX12AnalysisFrame(ownedFrame);
				});
			}
		}
		catch (Exception ex)
		{
			ReportRecoverableVisionError("DX12 analysis could not retain one frame: " + ex.Message);
		}
		finally
		{
			textureNativeFrameLease?.Dispose();
			if (!flag)
			{
				Interlocked.Exchange(ref _directX12AnalysisWorkerQueued, 0);
			}
		}
	}

	private void ProcessDirectX12AnalysisFrame(TextureNativeFrameLease frame)
	{
		try
		{
			if (TryCreateBitmapFromDirectX12TextureFrame(frame, out BitmapSource bitmap))
			{
				PreviewFrameAvailable(this, bitmap);
			}
		}
		catch (Exception ex)
		{
			ReportRecoverableVisionError("DX12 analysis skipped one frame and recovered: " + ex.Message);
		}
		finally
		{
			frame.Dispose();
			Interlocked.Exchange(ref _directX12AnalysisWorkerQueued, 0);
		}
	}

	private bool TryBeginDirectX12Analysis()
	{
		if (_isClosing || Interlocked.CompareExchange(ref _directX12AnalysisWorkerQueued, 1, 0) != 0)
		{
			return false;
		}
		DateTime utcNow = DateTime.UtcNow;
		if (utcNow - _lastDirectX12AnalysisFrameAtUtc < _directX12AnalysisFrameInterval)
		{
			Interlocked.Exchange(ref _directX12AnalysisWorkerQueued, 0);
			return false;
		}
		_lastDirectX12AnalysisFrameAtUtc = utcNow;
		return true;
	}

	private bool TryCreateBitmapFromDirectX12TextureFrame(TextureNativeFrameLease frame, out BitmapSource bitmap)
	{
		bitmap = BitmapSource.Create(1, 1, 96.0, 96.0, PixelFormats.Bgra32, null, new byte[4] { 0, 0, 0, 255 }, 4);
		int maximumWidth = Math.Clamp(_directX12AnalysisMaxOutputWidth, 320, 3840);
		byte[] array = null;
		int bgraStride = 0;
		int outputWidth = frame.Width;
		int outputHeight = frame.Height;
		byte[] nv12PreviewBytes = frame.Nv12PreviewBytes;
		if (nv12PreviewBytes != null && nv12PreviewBytes.Length > 0 && frame.Nv12PreviewStride > 0)
		{
			array = Nv12FrameConverter.ConvertToBgra(nv12PreviewBytes, frame.Nv12PreviewStride, frame.Width, frame.Height, maximumWidth, out outputWidth, out outputHeight, out bgraStride);
		}
		if (array == null || array.Length == 0 || bgraStride <= 0)
		{
			return false;
		}
		CameraFrame frame2 = new CameraFrame(array, outputWidth, outputHeight, bgraStride, null, 0, frame.MediaSubtype + "-analysis");
		return TryCreateBitmapFromBgraCameraFrame(frame2, out bitmap);
	}

	private bool TryCreateBitmapFromBgraCameraFrame(CameraFrame frame, out BitmapSource bitmap)
	{
		bitmap = BitmapSource.Create(1, 1, 96.0, 96.0, PixelFormats.Bgra32, null, new byte[4] { 0, 0, 0, 255 }, 4);
		if (!frame.HasBgra || frame.Width <= 0 || frame.Height <= 0)
		{
			return false;
		}
		BitmapSource bitmapSource = BitmapSource.Create(frame.Width, frame.Height, 96.0, 96.0, PixelFormats.Bgra32, null, frame.BgraBytes, frame.Stride);
		bitmapSource.Freeze();
		int num = Math.Clamp(_directX12AnalysisMaxOutputWidth, 320, 3840);
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

	private void DisposeDirectX12NativeCamera()
	{
		Dx12Camera directX12NativeCamera = _directX12NativeCamera;
		if (directX12NativeCamera != null)
		{
			_directX12NativeCamera = null;
			directX12NativeCamera.FrameAvailable -= DirectX12NativeFrameAvailable;
			directX12NativeCamera.TextureFrameAvailable -= DirectX12NativeTextureFrameAvailable;
			directX12NativeCamera.DiagnosticsChanged -= DirectX12NativeDiagnosticsChanged;
			directX12NativeCamera.StatusChanged -= DirectX12NativeStatusChanged;
			directX12NativeCamera.Dispose();
			ResetDirectX12AnalysisFramePump();
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
		DateTime utcNow = DateTime.UtcNow;
		if (!((utcNow - _lastDirectX12DiagnosticsAtUtc).TotalSeconds < 2.0))
		{
			_lastDirectX12DiagnosticsAtUtc = utcNow;
			base.Dispatcher.InvokeAsync(delegate
			{
				SetStatus(FormatDirectX12DiagnosticsStatus(diagnostics));
			}, DispatcherPriority.Background);
		}
	}

	private string FormatDirectX12DiagnosticsStatus(Direct3D12PreviewDiagnostics diagnostics)
	{
		string text = diagnostics.FormatStatusLine();
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
				_lastPreviewFrameAcceptedAt = DateTime.UtcNow;
				_latestFrame = frame;
				if (!_showLiveWireframePreview && !IsDirectX12PreviewSurfaceActive())
				{
					SetPreviewState("Camera active", frame);
				}
				else if (!string.Equals(PreviewStateText.Text, "Camera active", StringComparison.Ordinal))
				{
					PreviewStateText.Text = "Camera active";
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
			Interlocked.Exchange(ref _uiFramePending, 0);
		}
	}

	private void ResetPreviewFramePump()
	{
		_previewFramesReplacedSinceWarning = 0;
		_previewReplacementWindowStartedAtUtc = DateTime.MinValue;
		Interlocked.Exchange(ref _previewWarningPending, 0);
		ResetFaceFeatureDetectionFramePump();
	}

	private void ResetDirectX12AnalysisFramePump()
	{
		_lastDirectX12AnalysisFrameAtUtc = DateTime.MinValue;
	}

	private void ResetFaceFeatureDetectionFramePump()
	{
		_lastFaceFeatureDetectionAt = DateTime.MinValue;
	}

	private void TrackPreviewFrameReplacement()
	{
		DateTime utcNow = DateTime.UtcNow;
		if (_previewReplacementWindowStartedAtUtc == DateTime.MinValue)
		{
			_previewReplacementWindowStartedAtUtc = utcNow;
		}
		_previewFramesReplacedSinceWarning++;
		if (_previewFramesReplacedSinceWarning >= 50)
		{
			TimeSpan timeSpan = utcNow - _previewReplacementWindowStartedAtUtc;
			_previewFramesReplacedSinceWarning = 0;
			_previewReplacementWindowStartedAtUtc = utcNow;
			if (timeSpan <= TimeSpan.FromSeconds(2L))
			{
				QueuePreviewPumpWarning($"Camera preview dropped 50 incoming analysis frames while its current frame was still in flight ({timeSpan.TotalSeconds:0.0}s).");
			}
		}
	}

	private void QueuePreviewPumpWarning(string warning)
	{
		if (Interlocked.Exchange(ref _previewWarningPending, 1) == 0)
		{
			base.Dispatcher.InvokeAsync(delegate
			{
				Interlocked.Exchange(ref _previewWarningPending, 0);
				SetStatus(warning);
			}, DispatcherPriority.Background);
		}
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
		bool flag = !_photoTrainingModeActive && IsDirectX12PreviewEnabled();
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
				_lastFaceFeatureLockAt = DateTime.MinValue;
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
		if (base.IsLoaded)
		{
			_currentFaceFeatureDetection = FaceFeatureDetection.None;
			ResetLandmarkTracking();
			_activeFaceCueLayout = null;
			_lastFaceFeatureLockAt = DateTime.MinValue;
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
		if (!_avatarLearningRequested && !HasRequiredCaptureFoundation)
		{
			UpdateAvatarLearningStatusUi();
			SetStatus("Avatar Capture is disabled until all 9 human-approved Standard Model directions are complete. Use Live, Pictures, or Pictures + MediaPipe Assist.");
			return;
		}
		if (!_avatarLearningRequested && IsStandardModelBuildModeActive)
		{
			SetStatus("Stop the active Standard Model builder before starting normal Avatar Capture.");
			return;
		}
		_avatarLearningRequested = !_avatarLearningRequested;
		if (_avatarLearningRequested)
		{
			_showAvatarModelOverlay = true;
			AvatarModelOverlayMenuItem.IsChecked = true;
			UpdateFaceCueGuideOverlay(_latestFrame);
		}
		else
		{
			_currentManualStandardModelCandidate = null;
		}
		int num = ((!_avatarLearningRequested) ? _avatarObservationStorageService.DiscardPendingCandidates() : 0);
		UpdateAvatarLearningStatusUi();
		SetStatus((!_avatarLearningRequested) ? ((num > 0) ? $"Avatar capture stopped. {num} incomplete-batch candidate(s) were discarded." : "Avatar capture stopped.") : (IsMediaPipeOnlyGeometryMode ? ("Avatar capture started. MediaPipe visible-evidence geometry keeps one frame in flight and drops new analysis inputs while busy; " + GetFaceBoxSystemDisplayName() + " preview remains independent and full speed.") : ("Avatar capture started. Recurrent DECA runs single-flight at full speed, skips frames while busy, and stores the lowest-delta result from each five-result window; " + GetFaceBoxSystemDisplayName() + " face tracking stays live.")));
		if (!_avatarLearningRequested)
		{
			QueueAvatarSessionStatusReport();
		}
	}

	private void UpdateAvatarLearningStatusUi()
	{
		if (base.IsLoaded)
		{
			UpdateAvatarSessionUi();
			AvatarLearningState avatarLearningState = GetAvatarLearningState();
			ApplyStartStopButtonState(AvatarLearningToggleButton, _avatarLearningRequested, "Start Avatar Capture", "Stop Avatar Capture", "Starts " + ActiveAvatarReconstructionName + " avatar capture.", "Stops " + ActiveAvatarReconstructionName + " avatar capture.");
			if (!_avatarLearningRequested && !HasRequiredCaptureFoundation)
			{
				AvatarLearningToggleButton.ToolTip = "Disabled until the logged-in user has a human-approved 9-view Standard Model. Use either Standard Model builder below.";
			}
			AvatarLearningStateText.Text = avatarLearningState.Title;
			AvatarLearningStatusText.Text = avatarLearningState.Detail;
			AvatarLearningIndicator.Background = new SolidColorBrush(avatarLearningState.Accent);
			AvatarTrackingSanityState avatarTrackingSanityState = GetAvatarTrackingSanityState();
			AvatarTrackingSanityText.Text = avatarTrackingSanityState.Detail;
			AvatarTrackingSanityText.Foreground = new SolidColorBrush(avatarTrackingSanityState.Accent);
			FaceReconstructionLaneStatus faceReconstructionLaneStatus = CreateFaceReconstructionLaneStatus();
			AvatarReconstructionLaneText.Text = faceReconstructionLaneStatus.TrustDecision;
			AvatarReconstructionLaneText.Foreground = new SolidColorBrush(ColorForReconstructionLane(faceReconstructionLaneStatus));
			UpdateAvatarCaptureGuidanceUi();
		}
	}

	private FaceReconstructionLaneStatus CreateFaceReconstructionLaneStatus()
	{
		AvatarReconstructionSnapshot currentAvatarReconstructionSnapshot = _currentAvatarReconstructionSnapshot;
		MediaPipeNormalizedFaceModel currentMediaPipeGeometryModel = _currentMediaPipeGeometryModel;
		string activeAvatarReconstructionName = ActiveAvatarReconstructionName;
		bool isMediaPipeOnlyGeometryMode = IsMediaPipeOnlyGeometryMode;
		bool flag = Interlocked.CompareExchange(ref _avatarReconstructionPending, 0, 0) == 1;
		bool isAvatarReconstructionReady = IsAvatarReconstructionReady;
		string text = ((!IsAvatarUserLoggedIn) ? (activeAvatarReconstructionName + " paused until avatar user login") : ((!isMediaPipeOnlyGeometryMode) ? ((!isAvatarReconstructionReady) ? ActiveAvatarReconstructionReadinessStatus : (flag ? (activeAvatarReconstructionName + " reconstructing latest avatar frame") : ((currentAvatarReconstructionSnapshot != null) ? currentAvatarReconstructionSnapshot.TrustDecision : (activeAvatarReconstructionName + " ready; waiting for avatar frame")))) : ((currentMediaPipeGeometryModel.AcceptedFrameCount > 0) ? $"MediaPipe measured geometry has {currentMediaPipeGeometryModel.AcceptedFrameCount:n0} accepted frames" : "MediaPipe measured geometry ready; waiting for capture frames")));
		bool flag2 = IsSelectedFaceBoxSystemAvailable();
		string text2 = (flag2 ? _lastFaceBoxBackendStatus : (GetFaceBoxSystemDisplayName() + " tracking unavailable"));
		string trustLevel = ((!IsAvatarUserLoggedIn) ? "logged-out" : ((isMediaPipeOnlyGeometryMode && currentMediaPipeGeometryModel.ConfidentVertexPercent >= 35.0) ? "directly-constrained" : ((currentAvatarReconstructionSnapshot != null) ? "cross-checked" : (isAvatarReconstructionReady ? "reconstruction-ready" : "measurement-only"))));
		string text3 = (IsDecaFallbackActive ? (" DECA is selected but unavailable: " + _decaEnvironment.Status) : "");
		string trustDecision = ((!IsAvatarUserLoggedIn) ? ("Avatar reconstruction: logged out; capture stopped. " + GetFaceBoxSystemDisplayName() + " preview tracking remains live." + text3) : ((currentAvatarReconstructionSnapshot == null) ? (isMediaPipeOnlyGeometryMode ? $"Avatar geometry: MediaPipe visible-evidence model has {currentMediaPipeGeometryModel.AcceptedFrameCount:n0} accepted frame(s), {currentMediaPipeGeometryModel.ConfidentVertexPercent:0.#}% directly constrained vertices, {currentMediaPipeGeometryModel.HiddenLandmarkRejectionCount:n0} hidden-side predictions discarded, and {currentMediaPipeGeometryModel.VisualHullSlices.Count:n0} silhouette slices." : (isAvatarReconstructionReady ? $"Avatar reconstruction: {text}. {GetFaceBoxSystemDisplayName()} remains live tracking.{text3}" : $"Avatar reconstruction: {ActiveAvatarReconstructionReadinessStatus} {GetFaceBoxSystemDisplayName()} remains live tracking.")) : (IsDecaAvatarReconstructionActive ? $"Avatar reconstruction: recurrent model {currentAvatarReconstructionSnapshot.CurrentModelSequenceNumber:n0} | coefficient delta RMS {currentAvatarReconstructionSnapshot.CurrentModelCoefficientDeltaRms:0.000000} | measured fit {currentAvatarReconstructionSnapshot.ReconstructionConfidencePercent:0}% | A/B/C {currentAvatarReconstructionSnapshot.ARotationAroundXDegrees:0.#}/{currentAvatarReconstructionSnapshot.BRotationAroundYDegrees:0.#}/{currentAvatarReconstructionSnapshot.CRotationAroundZDegrees:0.#} deg | dense {currentAvatarReconstructionSnapshot.DenseVertexCount:n0} vertices.{text3}" : $"Avatar reconstruction: {currentAvatarReconstructionSnapshot.Source} measured fit {currentAvatarReconstructionSnapshot.ReconstructionConfidencePercent:0}% | A/B/C {currentAvatarReconstructionSnapshot.ARotationAroundXDegrees:0.#}/{currentAvatarReconstructionSnapshot.BRotationAroundYDegrees:0.#}/{currentAvatarReconstructionSnapshot.CRotationAroundZDegrees:0.#} deg | dense {currentAvatarReconstructionSnapshot.DenseVertexCount:n0} vertices.{text3}")));
		List<string> list = new List<string>();
		if (!isAvatarReconstructionReady && !isMediaPipeOnlyGeometryMode)
		{
			list.Add(activeAvatarReconstructionName + " avatar reconstruction is not active yet; avatar output remains measurement-only.");
		}
		if (currentAvatarReconstructionSnapshot != null)
		{
			list.AddRange(currentAvatarReconstructionSnapshot.Warnings);
		}
		bool isDecaAvatarReconstructionActive = IsDecaAvatarReconstructionActive;
		FaceReconstructionLaneStatus faceReconstructionLaneStatus = new FaceReconstructionLaneStatus
		{
			CreatedAtUtc = DateTime.UtcNow,
			FastTrackingLaneName = GetFaceBoxSystemDisplayName() + " face-box tracking lane",
			FastTrackingAvailable = flag2,
			FastTrackingHasDenseFace = _currentFaceLandmarkFrame.HasDenseMesh,
			FastTrackingStatus = (string.IsNullOrWhiteSpace(text2) ? "fast tracking waiting" : text2),
			AvatarReconstructionLaneName = (isMediaPipeOnlyGeometryMode ? "MediaPipe geometry measurement lane" : (activeAvatarReconstructionName + " avatar reconstruction lane")),
			AvatarReconstructionBackendId = ActiveAvatarReconstructionBackendId,
			AvatarReconstructionManifestPresent = (isMediaPipeOnlyGeometryMode || (isDecaAvatarReconstructionActive ? Directory.Exists(_decaEnvironment.RepositoryPath) : _threeDdfaOnnxModelInfo.ManifestExists)),
			AvatarReconstructionModelPresent = (isMediaPipeOnlyGeometryMode ? currentMediaPipeGeometryModel.HasGeometry : (isDecaAvatarReconstructionActive ? File.Exists(_decaEnvironment.ModelPath) : _threeDdfaOnnxModelInfo.IsReady)),
			AvatarReconstructionCanRunInference = isAvatarReconstructionReady,
			AvatarReconstructionStatus = text,
			AvatarReconstructionRuntime = (isMediaPipeOnlyGeometryMode ? "MediaPipe 478-point tracking" : (isDecaAvatarReconstructionActive ? "Python Torch DECA/FLAME" : _threeDdfaOnnxModelInfo.Runtime)),
			AvatarReconstructionModelDirectory = (isMediaPipeOnlyGeometryMode ? MediaPipeNormalizedFaceStore.GetFolder(GetAvatarDataFolder()) : (isDecaAvatarReconstructionActive ? _decaEnvironment.RepositoryPath : _threeDdfaOnnxModelInfo.ModelDirectory)),
			AvatarReconstructionManifestPath = (isMediaPipeOnlyGeometryMode ? MediaPipeNormalizedFaceStore.GetStatePath(GetAvatarDataFolder()) : (isDecaAvatarReconstructionActive ? _decaEnvironment.ModelPath : _threeDdfaOnnxModelInfo.ManifestPath))
		};
		IReadOnlyList<string> avatarReconstructionModelFiles;
		if (!isMediaPipeOnlyGeometryMode)
		{
			if (!isDecaAvatarReconstructionActive)
			{
				avatarReconstructionModelFiles = _threeDdfaOnnxModelInfo.ModelFiles;
			}
			else
			{
				IReadOnlyList<string> readOnlyList = new global::_003C_003Ez__ReadOnlySingleElementList<string>(_decaEnvironment.ModelPath);
				avatarReconstructionModelFiles = readOnlyList;
			}
		}
		else
		{
			IReadOnlyList<string> readOnlyList = new global::_003C_003Ez__ReadOnlyArray<string>(new string[3]
			{
				MediaPipeNormalizedFaceStore.GetStatePath(GetAvatarDataFolder()),
				MediaPipeNormalizedFaceStore.GetModelPath(GetAvatarDataFolder()),
				MediaPipeNormalizedFaceStore.GetViewerPath(GetAvatarDataFolder())
			});
			avatarReconstructionModelFiles = readOnlyList;
		}
		faceReconstructionLaneStatus.AvatarReconstructionModelFiles = avatarReconstructionModelFiles;
		IReadOnlyList<string> avatarReconstructionExpectedOutputs;
		if (!isMediaPipeOnlyGeometryMode)
		{
			if (!isDecaAvatarReconstructionActive)
			{
				avatarReconstructionExpectedOutputs = _threeDdfaOnnxModelInfo.ExpectedOutputs;
			}
			else
			{
				IReadOnlyList<string> readOnlyList = new global::_003C_003Ez__ReadOnlyArray<string>(new string[5] { "projected FLAME vertices", "canonical identity vertices", "FLAME topology", "shape/expression coefficients", "A/B/C pose" });
				avatarReconstructionExpectedOutputs = readOnlyList;
			}
		}
		else
		{
			IReadOnlyList<string> readOnlyList = new global::_003C_003Ez__ReadOnlyArray<string>(new string[5] { "normalized canonical XYZ", "direct visibility evidence", "five-degree silhouette profiles", "visual-hull cross sections", "eye/jaw/brow animation measurements" });
			avatarReconstructionExpectedOutputs = readOnlyList;
		}
		faceReconstructionLaneStatus.AvatarReconstructionExpectedOutputs = avatarReconstructionExpectedOutputs;
		faceReconstructionLaneStatus.TrustLevel = trustLevel;
		faceReconstructionLaneStatus.TrustDecision = trustDecision;
		faceReconstructionLaneStatus.LearningImpact = ((!IsAvatarUserLoggedIn) ? "No avatar observations are accepted until a user logs in and starts capture." : (isMediaPipeOnlyGeometryMode ? "Live MediaPipe tracking supplies visible multiview equations asynchronously. Hidden-side projections have zero reconstruction weight." : ((!isAvatarReconstructionReady) ? $"Live {GetFaceBoxSystemDisplayName()} tracking remains available while avatar reconstruction waits for {activeAvatarReconstructionName}." : ((!isDecaAvatarReconstructionActive && _selectedFaceBoxSystem == FaceBoxSystem.ThreeDdfaV2) ? "The selected 3DDFA-V2 pass owns both live face-box tracking and avatar reconstruction evidence." : (activeAvatarReconstructionName + " runs asynchronously for avatar reconstruction trust and does not block live " + GetFaceBoxSystemDisplayName() + " tracking.")))));
		faceReconstructionLaneStatus.Warnings = list;
		return faceReconstructionLaneStatus;
	}

	private bool HasStrongAvatarPoseLock()
	{
		if (IsMediaPipeOnlyGeometryMode)
		{
			MediaPipeNormalizedFaceModel currentMediaPipeGeometryModel = _currentMediaPipeGeometryModel;
			if (currentMediaPipeGeometryModel.AcceptedFrameCount >= 20 && currentMediaPipeGeometryModel.MaximumBRotationDegrees - currentMediaPipeGeometryModel.MinimumBRotationDegrees >= 20.0)
			{
				return currentMediaPipeGeometryModel.ConfidentVertexPercent >= 25.0;
			}
			return false;
		}
		AvatarReconstructionSnapshot currentAvatarReconstructionSnapshot = _currentAvatarReconstructionSnapshot;
		if (currentAvatarReconstructionSnapshot != null && currentAvatarReconstructionSnapshot.DenseVertexCount >= 1000)
		{
			return currentAvatarReconstructionSnapshot.ReconstructionConfidencePercent >= 70.0;
		}
		return false;
	}

	private string FormatAvatarPoseCrossCheck()
	{
		if (IsMediaPipeOnlyGeometryMode)
		{
			MediaPipeNormalizedFaceModel currentMediaPipeGeometryModel = _currentMediaPipeGeometryModel;
			return $"MediaPipe geometry coverage: A {currentMediaPipeGeometryModel.MinimumARotationDegrees:0.#}..{currentMediaPipeGeometryModel.MaximumARotationDegrees:0.#}, B {currentMediaPipeGeometryModel.MinimumBRotationDegrees:0.#}..{currentMediaPipeGeometryModel.MaximumBRotationDegrees:0.#}, C {currentMediaPipeGeometryModel.MinimumCRotationDegrees:0.#}..{currentMediaPipeGeometryModel.MaximumCRotationDegrees:0.#} deg; {currentMediaPipeGeometryModel.ConfidentVertexPercent:0.#}% directly constrained.";
		}
		AvatarReconstructionSnapshot currentAvatarReconstructionSnapshot = _currentAvatarReconstructionSnapshot;
		if (currentAvatarReconstructionSnapshot != null)
		{
			return $"{currentAvatarReconstructionSnapshot.Source} pose lock: A/B/C {currentAvatarReconstructionSnapshot.ARotationAroundXDegrees:0.#}/{currentAvatarReconstructionSnapshot.BRotationAroundYDegrees:0.#}/{currentAvatarReconstructionSnapshot.CRotationAroundZDegrees:0.#} deg, dense {currentAvatarReconstructionSnapshot.DenseVertexCount:n0} vertices.";
		}
		return ActiveAvatarReconstructionName + " pose waiting.";
	}

	private static Color ColorForReconstructionLane(FaceReconstructionLaneStatus lane)
	{
		if (lane.AvatarReconstructionCanRunInference && lane.TrustLevel == "cross-checked")
		{
			return Color.FromRgb(128, 224, 164);
		}
		if (!lane.AvatarReconstructionCanRunInference)
		{
			return Color.FromRgb(185, 215, 239);
		}
		return Color.FromRgb(byte.MaxValue, 210, 122);
	}

	private static void ApplyStartStopButtonState(Button button, bool isActive, string startText, string stopText, string startToolTip, string stopToolTip)
	{
		button.Content = (isActive ? stopText : startText);
		button.Background = (isActive ? StopActionButtonBackground : StartActionButtonBackground);
		button.BorderBrush = (isActive ? StopActionButtonBorder : StartActionButtonBorder);
		button.Foreground = Brushes.White;
		button.ToolTip = (isActive ? stopToolTip : startToolTip);
	}

	private AvatarLearningState GetAvatarLearningState()
	{
		if (!IsAvatarUserLoggedIn)
		{
			return new AvatarLearningState(Active: false, "Avatar capture stopped", "Not capturing: use File > Login to identify the person in front of the camera.", Color.FromRgb(89, 97, 107));
		}
		if (!_avatarLearningRequested)
		{
			return new AvatarLearningState(Active: false, HasRequiredCaptureFoundation ? "Avatar capture stopped" : "Standard Model required", HasRequiredCaptureFoundation ? $"Not capturing: click Start Avatar Capture when {CurrentAvatarProfileDisplayName} is present and you want {ActiveAvatarReconstructionName} measurements." : "Start Avatar Capture remains disabled until a human accepts all 9 A/B directions in either Standard Model builder.", HasRequiredCaptureFoundation ? Color.FromRgb(89, 97, 107) : Color.FromRgb(215, 165, 58));
		}
		if ((!_isCameraEnabled && !_photoTrainingModeActive) || _latestFrame == null)
		{
			return new AvatarLearningState(Active: false, "Avatar capture waiting", _photoTrainingModeActive ? "Photo training: waiting for MediaPipe to lock onto the selected still image." : "Not capturing yet: turn the camera on and wait for the face tracker to lock.", Color.FromRgb(215, 165, 58));
		}
		if (!_currentFaceLandmarkFrame.HasFace || !_currentFaceLandmarkMetrics.HasFace)
		{
			return new AvatarLearningState(Active: false, "Avatar capture waiting", "Not capturing yet: keep your full face visible until the eye and mouth overlay locks on.", Color.FromRgb(215, 165, 58));
		}
		if (_currentAvatarCaptureQuality.CanCollectMeasurements)
		{
			bool flag = Interlocked.CompareExchange(ref _avatarReconstructionPending, 0, 0) == 1;
			AvatarReconstructionSnapshot currentAvatarReconstructionSnapshot = _currentAvatarReconstructionSnapshot;
			string text = (IsMediaPipeOnlyGeometryMode ? $"MediaPipe visible-evidence geometry has {_currentMediaPipeGeometryModel.AcceptedFrameCount:n0} frames; {_currentMediaPipeGeometryModel.ConfidentVertexPercent:0.#}% directly constrained; {_currentMediaPipeGeometryModel.HiddenLandmarkRejectionCount:n0} hidden predictions discarded." : ((currentAvatarReconstructionSnapshot != null) ? $"{currentAvatarReconstructionSnapshot.Source} measured fit {currentAvatarReconstructionSnapshot.ReconstructionConfidencePercent:0}% with {currentAvatarReconstructionSnapshot.DenseVertexCount:n0} dense vertices; A/B/C {currentAvatarReconstructionSnapshot.ARotationAroundXDegrees:0.#}/{currentAvatarReconstructionSnapshot.BRotationAroundYDegrees:0.#}/{currentAvatarReconstructionSnapshot.CRotationAroundZDegrees:0.#} deg." : (flag ? (ActiveAvatarReconstructionName + " is reconstructing an accepted frame.") : (IsAvatarReconstructionReady ? (ActiveAvatarReconstructionName + " is ready and waiting for the next capture frame.") : ActiveAvatarReconstructionReadinessStatus))));
			return new AvatarLearningState(IsAvatarReconstructionReady, IsAvatarReconstructionReady ? "Capturing 3D avatar data" : "Avatar capture waiting", text + " " + GetFaceBoxSystemDisplayName() + " eye/jaw/brow tracking stays live for overlays and capture measurements.", IsAvatarReconstructionReady ? Color.FromRgb(74, 163, 107) : Color.FromRgb(215, 165, 58));
		}
		string text2 = _currentAvatarCaptureQuality.Suggestions.FirstOrDefault() ?? _currentAvatarCaptureQuality.PrimaryReason ?? "Improve face lock, eye visibility, mouth visibility, lighting, or camera mode.";
		return new AvatarLearningState(Active: false, "Avatar capture waiting", "Not capturing: " + _currentAvatarCaptureQuality.PrimaryReason + ". Fix: " + text2, Color.FromRgb(215, 165, 58));
	}

	private AvatarTrackingSanityState GetAvatarTrackingSanityState()
	{
		if (!IsAvatarUserLoggedIn)
		{
			return new AvatarTrackingSanityState($"Tracking sanity: avatar capture is logged out. {GetFaceBoxSystemDisplayName()} preview tracking remains live, but {ActiveAvatarReconstructionName} observations are not being collected.", Color.FromRgb(185, 215, 239));
		}
		if (IsMediaPipeOnlyGeometryMode)
		{
			MediaPipeNormalizedFaceModel currentMediaPipeGeometryModel = _currentMediaPipeGeometryModel;
			TimeSpan? lastMediaPipeGeometryProcessingDuration = _lastMediaPipeGeometryProcessingDuration;
			object obj;
			if (lastMediaPipeGeometryProcessingDuration.HasValue)
			{
				TimeSpan valueOrDefault = lastMediaPipeGeometryProcessingDuration.GetValueOrDefault();
				obj = $" Last geometry update {valueOrDefault.TotalMilliseconds:0.#} ms.";
			}
			else
			{
				obj = "";
			}
			string value = (string)obj;
			return new AvatarTrackingSanityState($"Tracking sanity: MediaPipe owns live feature tracking and visible-evidence geometry. {currentMediaPipeGeometryModel.AcceptedFrameCount:n0} frame(s) accepted; {currentMediaPipeGeometryModel.ConfidentVertexPercent:0.#}% directly constrained; {currentMediaPipeGeometryModel.HiddenLandmarkRejectionCount:n0} compressed hidden-side predictions discarded.{value}", (currentMediaPipeGeometryModel.AcceptedFrameCount > 0) ? Color.FromRgb(128, 224, 164) : Color.FromRgb(185, 215, 239));
		}
		PoseAlignmentSummary currentSummary = _poseAlignmentAuditor.CurrentSummary;
		if (!IsDecaAvatarReconstructionActive && !currentSummary.ReadyForComparison)
		{
			return new AvatarTrackingSanityState($"Tracking sanity: 3DDFA_V2 ONNX owns avatar A/B/C pose and depth. Pose comparison audit v{currentSummary.PoseConventionVersion} is gathering diagnostic evidence from {currentSummary.SampleCount} exact-frame pair(s); it does not block avatar capture. {currentSummary.Guidance}", Color.FromRgb(185, 215, 239));
		}
		if (HasStrongAvatarPoseLock())
		{
			return new AvatarTrackingSanityState($"Tracking sanity: {ActiveAvatarReconstructionName} owns avatar pose/depth. {GetFaceBoxSystemDisplayName()} eye/jaw/brow tracking remains active for overlays and capture measurements. {FormatAvatarPoseCrossCheck()}", Color.FromRgb(128, 224, 164));
		}
		return new AvatarTrackingSanityState(IsAvatarReconstructionReady ? $"Tracking sanity: {GetFaceBoxSystemDisplayName()} eye/jaw/brow tracking is live; waiting for a strong {ActiveAvatarReconstructionName} pose lock before trusting avatar pose/depth." : ("Tracking sanity: " + GetFaceBoxSystemDisplayName() + " face tracking is selected; avatar reconstruction is waiting. " + ActiveAvatarReconstructionReadinessStatus), Color.FromRgb(185, 215, 239));
	}

	private void UpdateAvatarCaptureGuidanceUi()
	{
		if (base.IsLoaded)
		{
			AvatarCaptureGuidanceState avatarCaptureGuidanceState = GetAvatarCaptureGuidanceState();
			AvatarCaptureGuidanceTitleText.Text = avatarCaptureGuidanceState.Title;
			AvatarCaptureGuidanceDetailText.Text = avatarCaptureGuidanceState.Detail;
			AvatarCaptureGuidanceTitleText.Foreground = new SolidColorBrush(ColorForAvatarCaptureGuidanceSeverity(avatarCaptureGuidanceState.Severity));
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

	private static Color ColorForAvatarCaptureGuidanceSeverity(string severity)
	{
		return severity switch
		{
			"good" => Color.FromRgb(128, 224, 164), 
			"warning" => Color.FromRgb(byte.MaxValue, 210, 122), 
			"blocked" => Color.FromRgb(byte.MaxValue, 154, 154), 
			_ => Color.FromRgb(185, 215, 239), 
		};
	}

	private void OpenAvatarSystemClicked(object sender, RoutedEventArgs e)
	{
		try
		{
			string avatarDataFolder = GetAvatarDataFolder();
			Directory.CreateDirectory(avatarDataFolder);
			AvatarReportSnapshot snapshot = CreateAvatarReportSnapshot(avatarDataFolder);
			QueueAvatarReportSave(snapshot);
			_avatarModelHtmlPath = AvatarModelStore.GetHtmlPath(avatarDataFolder);
			_avatarSystemDashboardPath = GetAvatarSystemDashboardHtmlPath(avatarDataFolder);
			EnsureAvatarSystemPlaceholder(_avatarSystemDashboardPath);
			OpenLocalFile(_avatarSystemDashboardPath);
			string text = ((_currentAvatarReconstructionSnapshot != null) ? ("Opened live Avatar System: " + _avatarSystemDashboardPath) : "Opened live waiting Avatar System. Log in the person at the camera and start avatar capture.");
			MonitorStatusText.Text = text;
			SetStatus(text);
		}
		catch (Exception ex)
		{
			string text2 = "Could not open Avatar System: " + ex.Message;
			MonitorStatusText.Text = text2;
			SetStatus(text2);
		}
	}

	private void OpenAvatarDataReviewClicked(object sender, RoutedEventArgs e)
	{
		try
		{
			string avatarDataFolder = GetAvatarDataFolder();
			Directory.CreateDirectory(avatarDataFolder);
			OpenWebAddress(_avatarDataReviewServer.StartOrUpdate(avatarDataFolder, CurrentAvatarProfileId, CurrentAvatarProfileDisplayName));
			string text = "Opened Avatar Data Review for " + CurrentAvatarProfileDisplayName + ".";
			SetStatus(text);
			MonitorStatusText.Text = text;
		}
		catch (Exception ex)
		{
			string text2 = "Could not open Avatar Data Review: " + ex.Message;
			SetStatus(text2);
			MonitorStatusText.Text = text2;
		}
	}

	private void OpenFlameAvatarDataClicked(object sender, RoutedEventArgs e)
	{
		try
		{
			string avatarDataFolder = GetAvatarDataFolder();
			Directory.CreateDirectory(avatarDataFolder);
			OpenWebAddress(_avatarDataReviewServer.StartOrUpdate(avatarDataFolder, CurrentAvatarProfileId, CurrentAvatarProfileDisplayName, "deca-flame-recurrent-v4"));
			int count = _avatarObservationRepository.ReadDataset(avatarDataFolder, CurrentAvatarProfileId, CurrentAvatarProfileDisplayName, includeDenseTopology: false, "deca-flame-recurrent-v4").Observations.Count;
			string text = ((count == 0) ? ("Opened FLAME reconstruction data for " + CurrentAvatarProfileDisplayName + ". No FLAME scans are stored yet; start Avatar Capture to collect one.") : $"Opened {count:n0} FLAME reconstruction scans for {CurrentAvatarProfileDisplayName}.");
			SetStatus(text);
			MonitorStatusText.Text = text;
		}
		catch (Exception ex)
		{
			string text2 = "Could not open FLAME reconstruction data: " + ex.Message;
			SetStatus(text2);
			MonitorStatusText.Text = text2;
		}
	}

	private async void MainWindowPreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.NumPad0)
		{
			e.Handled = true;
			await CaptureCurrentStandardModelAsync();
			return;
		}
		bool flag = !_photoTrainingModeActive;
		if (!flag)
		{
			Key key = e.Key;
			bool flag2 = ((key == Key.Left || key == Key.Right) ? true : false);
			flag = !flag2;
		}
		if (!flag)
		{
			e.Handled = true;
			await ShowRelativePhotoTrainingImageAsync((e.Key != Key.Left) ? 1 : (-1));
		}
	}

	private async void CaptureStandardModelClicked(object sender, RoutedEventArgs e)
	{
		await CaptureCurrentStandardModelAsync();
	}

	private async void BuildStandardModelLiveClicked(object sender, RoutedEventArgs e)
	{
		if (_liveStandardModelModeActive)
		{
			StopLiveStandardModelMode(announce: true);
			return;
		}
		if (!IsAvatarUserLoggedIn)
		{
			SetStatus("Log in the person whose Standard Model you want to build.");
			return;
		}
		if (!_avatarModelReadyForCapture || _avatarModelInitializationPending)
		{
			SetStatus("Please wait for the stored model and Standard Model atlas to finish loading.");
			return;
		}
		if (!IsDecaAvatarReconstructionActive)
		{
			SetStatus("The Standard Model builder requires the DECA/FLAME runtime. " + ActiveAvatarReconstructionReadinessStatus);
			return;
		}
		if (_photoTrainingModeActive)
		{
			StopPhotoTrainingMode(announce: false);
		}
		if (_avatarLearningRequested)
		{
			_avatarLearningRequested = false;
			_avatarObservationStorageService.DiscardPendingCandidates();
		}
		if (_selectedFaceBoxSystem != FaceBoxSystem.MediaPipe)
		{
			SwitchFaceBoxSystem(FaceBoxSystem.MediaPipe);
		}
		_liveStandardModelModeActive = true;
		_showAvatarModelOverlay = true;
		AvatarModelOverlayMenuItem.IsChecked = true;
		FaceAutoFollowCheckBox.IsChecked = true;
		_currentManualStandardModelCandidate = null;
		if (!_isCameraEnabled)
		{
			SetCameraToggle(enabled: true);
			await StartPreviewAsync();
		}
		if (!_isCameraEnabled)
		{
			_liveStandardModelModeActive = false;
			SetCameraToggle(enabled: false);
			UpdateStandardModelUi();
			UpdateAvatarLearningStatusUi();
			SetStatus("The live Standard Model builder could not start because the camera did not open.");
		}
		else
		{
			UpdateAvatarLearningStatusUi();
			UpdateStandardModelUi();
			SetStatus("Live Standard Model builder started. Point toward " + GetNextStandardPoseTarget() + " and press NumPad 0 only when the overlay has a good lock.");
		}
	}

	private void StopLiveStandardModelMode(bool announce)
	{
		if (_liveStandardModelModeActive)
		{
			_liveStandardModelModeActive = false;
			_currentManualStandardModelCandidate = null;
			UpdateAvatarLearningStatusUi();
			UpdateStandardModelUi();
			if (announce)
			{
				SetStatus("Live Standard Model builder stopped. The camera remains available; no frame was saved unless NumPad 0 was pressed.");
			}
		}
	}

	private async Task CaptureCurrentStandardModelAsync()
	{
		if (!IsAvatarUserLoggedIn)
		{
			UpdateStandardModelUi("Standard model: log in before accepting a model snapshot.");
			SetStatus("Log in the person in front of the camera before using NumPad 0.");
			return;
		}
		if ((!_isCameraEnabled && !_photoTrainingModeActive) || !IsStandardModelBuildModeActive)
		{
			UpdateStandardModelUi("Standard model: start Live, Pictures, or Pictures + MediaPipe Assist, then press NumPad 0 when the overlay has a great lock.");
			SetStatus("Start one of the Standard Model builders before using NumPad 0.");
			return;
		}
		AvatarObservationCapture candidate = _currentManualStandardModelCandidate;
		if ((object)candidate == null || !string.Equals(candidate.SubjectId, CurrentAvatarProfileId, StringComparison.Ordinal) || DateTime.UtcNow - candidate.Reconstruction.CapturedAtUtc > TimeSpan.FromSeconds(5L))
		{
			UpdateStandardModelUi(_photoTrainingModeActive ? "Standard model: waiting for a fresh DECA/FLAME result for the selected photo. Let it settle, then press NumPad 0." : "Standard model: waiting for a fresh DECA/FLAME model and matching camera frame. Keep your face visible, then press NumPad 0.");
			SetStatus("No fresh Standard Model snapshot is ready yet.");
			return;
		}
		if (Interlocked.CompareExchange(ref _manualStandardModelSavePending, 1, 0) != 0)
		{
			SetStatus("The previous NumPad 0 capture is still being saved.");
			return;
		}
		UpdateStandardModelUi("Standard model: saving the exact accepted model and matching camera frame...");
		try
		{
			ManualStandardModelCaptureResult manualStandardModelCaptureResult = await Task.Run(() => _manualStandardModelCaptureService.Save(candidate.ProfileFolder, candidate.SubjectId, candidate.SubjectDisplayName, candidate.Reconstruction, candidate.SourceFrame, candidate.CaptureQuality, candidate.FaceGeometry));
			if (_isClosing)
			{
				return;
			}
			if (!manualStandardModelCaptureResult.WriteResult.Accepted || (object)manualStandardModelCaptureResult.Model == null)
			{
				string status = "Standard model: capture was not saved. " + manualStandardModelCaptureResult.WriteResult.Detail;
				UpdateStandardModelUi(status);
				SetStatus(status);
				return;
			}
			_standardPoseAtlas.Clear();
			foreach (KeyValuePair<string, AvatarStandardPoseSample> poseAtla in manualStandardModelCaptureResult.Model.PoseAtlas)
			{
				_standardPoseAtlas[poseAtla.Key] = poseAtla.Value;
			}
			lock (_decaClientLock)
			{
				_decaAvatarClient?.SetCurrentModelShapeCoefficients(manualStandardModelCaptureResult.Model.ShapeCoefficients);
			}
			string value = manualStandardModelCaptureResult.PoseSample?.DisplayName ?? "unclassified view";
			string status2 = $"Standard model: accepted {value} | {CountStandardPoseIdentityEvidence():n0}/9 directions | fused identity evidence {manualStandardModelCaptureResult.Model.IdentityEvidencePoseCount:n0}/9 | fit {manualStandardModelCaptureResult.Model.LastMeasuredFitPercent:0.#}% | delta {manualStandardModelCaptureResult.Model.CoefficientDeltaRms:0.000000}. " + (HasCompleteHumanStandardModel ? "All 9 directions are complete; additional accepted captures replace their matching direction for refinement." : ("Next: point toward " + GetNextStandardPoseTarget() + " and press NumPad 0 again."));
			UpdateStandardModelUi(status2);
			UpdatePhotoTrainingUi();
			UpdateAvatarLearningStatusUi();
			SetStatus(status2);
		}
		catch (Exception ex)
		{
			string status3 = "Standard model: NumPad 0 capture failed. " + ex.Message;
			UpdateStandardModelUi(status3);
			SetStatus(status3);
		}
		finally
		{
			Interlocked.Exchange(ref _manualStandardModelSavePending, 0);
			if (!_isClosing)
			{
				UpdateStandardModelUi(_standardModelStatus);
			}
		}
	}

	private async void PhotoTrainingToggleClicked(object sender, RoutedEventArgs e)
	{
		if (_photoTrainingModeActive)
		{
			StopPhotoTrainingMode(announce: true);
		}
		else
		{
			await StartPhotoTrainingModeAsync(DecaIdentityFitProfile.Flame68);
		}
	}

	private async void AssistedPhotoTrainingToggleClicked(object sender, RoutedEventArgs e)
	{
		if (_photoTrainingModeActive)
		{
			StopPhotoTrainingMode(announce: true);
		}
		else
		{
			await StartPhotoTrainingModeAsync(DecaIdentityFitProfile.MediaPipeSurfaceAssisted);
		}
	}

	private async void PreviousTrainingPhotoClicked(object sender, RoutedEventArgs e)
	{
		await ShowRelativePhotoTrainingImageAsync(-1);
	}

	private async void NextTrainingPhotoClicked(object sender, RoutedEventArgs e)
	{
		await ShowRelativePhotoTrainingImageAsync(1);
	}

	private async Task StartPhotoTrainingModeAsync(DecaIdentityFitProfile identityFitProfile)
	{
		if (!IsAvatarUserLoggedIn)
		{
			SetStatus("Log in the person whose Standard Model you want to build before choosing pictures.");
			return;
		}
		if (!_avatarModelReadyForCapture || _avatarModelInitializationPending)
		{
			SetStatus("Please wait for the stored avatar model to finish loading before starting the Picture Standard Model builder.");
			return;
		}
		if (!IsDecaAvatarReconstructionActive)
		{
			SetStatus("The picture Standard Model builder requires the DECA/FLAME runtime. " + ActiveAvatarReconstructionReadinessStatus);
			return;
		}
		if (_liveStandardModelModeActive)
		{
			SetStatus("Stop Build Standard Model Live before starting the picture builder.");
			return;
		}
		string text = PromptForStandardModelPictureFolder(identityFitProfile);
		if (string.IsNullOrWhiteSpace(text))
		{
			SetStatus("Picture Standard Model builder was not started.");
			return;
		}
		IReadOnlyList<string> imagePaths;
		try
		{
			imagePaths = (from path in Directory.EnumerateFiles(text, "*", SearchOption.TopDirectoryOnly)
				where PhotoTrainingImageExtensions.Contains(System.IO.Path.GetExtension(path))
				select path).OrderBy<string, string>((string path) => System.IO.Path.GetFileName(path), StringComparer.OrdinalIgnoreCase).ToArray();
		}
		catch (Exception ex)
		{
			SetStatus("Could not read the selected photo folder: " + ex.Message);
			return;
		}
		if (imagePaths.Count == 0)
		{
			SetStatus("The selected folder contains no JPG, JPEG, PNG, or BMP images.");
			return;
		}
		if (_isCameraEnabled)
		{
			await StopPreviewAsync();
		}
		if (_selectedFaceBoxSystem != FaceBoxSystem.MediaPipe)
		{
			SwitchFaceBoxSystem(FaceBoxSystem.MediaPipe);
		}
		_avatarObservationStorageService.DiscardPendingCandidates();
		_photoTrainingImagePaths = imagePaths;
		_photoTrainingImageIndex = -1;
		_photoTrainingFrame = null;
		_photoTrainingIdentityFitProfile = identityFitProfile;
		_photoTrainingModeActive = true;
		_avatarLearningRequested = false;
		_showLiveWireframePreview = false;
		LiveWireframeMenuItem.IsChecked = false;
		_showAvatarModelOverlay = true;
		AvatarModelOverlayMenuItem.IsChecked = true;
		FaceAutoFollowCheckBox.IsChecked = true;
		CameraToggle.IsEnabled = false;
		SetCameraToggle(enabled: false);
		ResetFaceFeatureDetectionFramePump();
		_currentFaceFeatureDetection = FaceFeatureDetection.None;
		_currentManualStandardModelCandidate = null;
		_activeFaceCueLayout = null;
		ResetLandmarkTracking();
		await ShowPhotoTrainingImageAsync(0);
		if (!_photoTrainingModeActive || _photoTrainingFrame == null)
		{
			StopPhotoTrainingMode(announce: false);
			return;
		}
		_photoTrainingTimer.Start();
		UpdateAvatarLearningStatusUi();
		UpdatePhotoTrainingUi();
		string value = ((identityFitProfile == DecaIdentityFitProfile.MediaPipeSurfaceAssisted) ? "MediaPipe-assisted picture builder using 105 embedded surface points plus the jaw contour" : "68-landmark picture builder");
		SetStatus($"{value} started with {imagePaths.Count:n0} image(s). Let each image settle, use Left/Right to navigate, and press NumPad 0 only when you judge the lock worth keeping.");
	}

	private string PromptForStandardModelPictureFolder(DecaIdentityFitProfile identityFitProfile)
	{
		Window window = new Window
		{
			Title = ((identityFitProfile == DecaIdentityFitProfile.MediaPipeSurfaceAssisted) ? "Build From Pictures + MediaPipe Assist" : "Build Standard Model From Pictures"),
			Owner = this,
			Width = 620.0,
			SizeToContent = SizeToContent.Height,
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			ResizeMode = ResizeMode.NoResize,
			ShowInTaskbar = false,
			Background = new SolidColorBrush(Color.FromRgb(8, 13, 18)),
			Foreground = Brushes.White
		};
		StackPanel stackPanel = new StackPanel
		{
			Margin = new Thickness(18.0)
		};
		stackPanel.Children.Add(new TextBlock
		{
			Text = "Choose the folder containing this user's pictures",
			FontSize = 18.0,
			FontWeight = FontWeights.SemiBold,
			TextWrapping = TextWrapping.Wrap
		});
		stackPanel.Children.Add(new TextBlock
		{
			Text = "After you click OK, use Previous/Next and press NumPad 0 only when the current face direction has a good lock. All 9 A/B directions are required before the Standard Model is accepted.",
			Foreground = new SolidColorBrush(Color.FromRgb(185, 215, 239)),
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0.0, 8.0, 0.0, 14.0)
		});
		stackPanel.Children.Add(new TextBlock
		{
			Text = "Picture folder",
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
		});
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		TextBox folderBox = new TextBox
		{
			Text = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
			MinHeight = 34.0,
			VerticalContentAlignment = VerticalAlignment.Center,
			Style = (TryFindResource(typeof(TextBox)) as Style)
		};
		Button button = new Button
		{
			Content = "Choose Folder",
			MinWidth = 120.0,
			Margin = new Thickness(10.0, 0.0, 0.0, 0.0),
			Style = (TryFindResource(typeof(Button)) as Style)
		};
		Grid.SetColumn(folderBox, 0);
		Grid.SetColumn(button, 1);
		grid.Children.Add(folderBox);
		grid.Children.Add(button);
		stackPanel.Children.Add(grid);
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
		Button button2 = new Button
		{
			Content = "Cancel",
			MinWidth = 110.0,
			IsCancel = true,
			Style = (TryFindResource(typeof(Button)) as Style)
		};
		Button button3 = new Button
		{
			Content = "OK",
			MinWidth = 110.0,
			IsDefault = true,
			Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
			Style = (TryFindResource(typeof(Button)) as Style)
		};
		stackPanel2.Children.Add(button2);
		stackPanel2.Children.Add(button3);
		stackPanel.Children.Add(stackPanel2);
		window.Content = stackPanel;
		button.Click += delegate
		{
			string text = folderBox.Text.Trim().Trim('"');
			string initialDirectory = (Directory.Exists(text) ? text : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
			OpenFolderDialog openFolderDialog = new OpenFolderDialog
			{
				Title = "Choose Standard Model picture folder",
				InitialDirectory = initialDirectory,
				Multiselect = false
			};
			if (openFolderDialog.ShowDialog(window) == true)
			{
				folderBox.Text = openFolderDialog.FolderName;
				status.Text = "";
			}
		};
		string selectedFolder = "";
		button3.Click += delegate
		{
			string path = folderBox.Text.Trim().Trim('"');
			if (!Directory.Exists(path))
			{
				status.Text = "Choose an existing folder before continuing.";
			}
			else
			{
				selectedFolder = System.IO.Path.GetFullPath(path);
				window.DialogResult = true;
			}
		};
		button2.Click += delegate
		{
			window.DialogResult = false;
		};
		if (window.ShowDialog() != true)
		{
			return "";
		}
		return selectedFolder;
	}

	private void StopPhotoTrainingMode(bool announce)
	{
		if (_photoTrainingModeActive)
		{
			_photoTrainingTimer.Stop();
			Interlocked.Increment(ref _photoTrainingLoadGeneration);
			_photoTrainingModeActive = false;
			_photoTrainingImagePaths = Array.Empty<string>();
			_photoTrainingImageIndex = -1;
			_photoTrainingFrame = null;
			_photoTrainingIdentityFitProfile = DecaIdentityFitProfile.Flame68;
			_latestFrame = null;
			_avatarLearningRequested = false;
			_currentAvatarReconstructionSnapshot = null;
			_currentManualStandardModelCandidate = null;
			_currentFaceFeatureDetection = FaceFeatureDetection.None;
			_activeFaceCueLayout = null;
			_avatarObservationStorageService.DiscardPendingCandidates();
			ResetFaceFeatureDetectionFramePump();
			ResetLandmarkTracking();
			FaceCueGuideCanvas.Children.Clear();
			CameraToggle.IsEnabled = true;
			SetCameraToggle(enabled: false);
			SetPreviewState("Camera disabled", null);
			UpdateAvatarLearningStatusUi();
			UpdatePhotoTrainingUi();
			UpdateStandardModelUi();
			if (announce)
			{
				SetStatus("Picture Standard Model builder stopped. No additional image or model was saved unless NumPad 0 was pressed.");
			}
		}
	}

	private async Task ShowRelativePhotoTrainingImageAsync(int offset)
	{
		if (_photoTrainingModeActive && _photoTrainingImagePaths.Count != 0)
		{
			int num = (_photoTrainingImageIndex + offset) % _photoTrainingImagePaths.Count;
			if (num < 0)
			{
				num += _photoTrainingImagePaths.Count;
			}
			await ShowPhotoTrainingImageAsync(num);
		}
	}

	private async Task ShowPhotoTrainingImageAsync(int index)
	{
		if (!_photoTrainingModeActive || index < 0 || index >= _photoTrainingImagePaths.Count)
		{
			return;
		}
		int loadGeneration = Interlocked.Increment(ref _photoTrainingLoadGeneration);
		string imagePath = _photoTrainingImagePaths[index];
		BitmapSource bitmapSource;
		try
		{
			bitmapSource = await Task.Run(() => LoadPhotoTrainingBitmap(imagePath));
		}
		catch (Exception ex)
		{
			if (_photoTrainingModeActive)
			{
				SetStatus("Could not load " + System.IO.Path.GetFileName(imagePath) + ": " + ex.Message);
			}
			return;
		}
		if (!_isClosing && _photoTrainingModeActive && loadGeneration == Volatile.Read(in _photoTrainingLoadGeneration))
		{
			_photoTrainingImageIndex = index;
			_photoTrainingFrame = bitmapSource;
			_latestFrame = bitmapSource;
			_currentAvatarReconstructionSnapshot = null;
			_currentManualStandardModelCandidate = null;
			_currentFaceFeatureDetection = FaceFeatureDetection.None;
			_activeFaceCueLayout = null;
			ResetFaceFeatureDetectionFramePump();
			ResetLandmarkTracking();
			lock (_decaClientLock)
			{
				_decaAvatarClient?.ResetCurrentModelToIdentityAnchor();
			}
			SetPreviewState($"Photo training {index + 1:n0} of {_photoTrainingImagePaths.Count:n0}", bitmapSource);
			ProcessFrame(bitmapSource);
			UpdatePhotoTrainingUi();
			UpdateStandardModelUi();
		}
	}

	private void PhotoTrainingTimerTick(object? sender, EventArgs e)
	{
		if (!_isClosing && _photoTrainingModeActive)
		{
			BitmapSource photoTrainingFrame = _photoTrainingFrame;
			if (photoTrainingFrame != null)
			{
				_latestFrame = photoTrainingFrame;
				ProcessFrame(photoTrainingFrame);
			}
		}
	}

	private void UpdatePhotoTrainingUi()
	{
		if (base.IsLoaded)
		{
			ApplyStartStopButtonState(PhotoTrainingToggleButton, _photoTrainingModeActive && _photoTrainingIdentityFitProfile == DecaIdentityFitProfile.Flame68, "Build Standard Model From Pictures", "Stop Picture Standard Model", "Choose a folder and run each selected photo continuously through MediaPipe and recurrent DECA.", "Stops still-image processing without saving anything else.");
			bool flag = IsAvatarUserLoggedIn && _avatarModelReadyForCapture && !_avatarModelInitializationPending && IsDecaAvatarReconstructionActive && !_liveStandardModelModeActive;
			PhotoTrainingToggleButton.IsEnabled = !_isClosing && ((_photoTrainingModeActive && _photoTrainingIdentityFitProfile == DecaIdentityFitProfile.Flame68) || (!_photoTrainingModeActive && flag));
			ApplyStartStopButtonState(AssistedPhotoTrainingToggleButton, _photoTrainingModeActive && _photoTrainingIdentityFitProfile == DecaIdentityFitProfile.MediaPipeSurfaceAssisted, "Build From Pictures + MediaPipe Assist", "Stop MediaPipe-Assisted Pictures", "Uses all bundled MediaPipe-to-FLAME surface correspondences plus the jaw contour during recurrent DECA fitting.", "Stops assisted still-image processing without saving anything else.");
			AssistedPhotoTrainingToggleButton.IsEnabled = !_isClosing && ((_photoTrainingModeActive && _photoTrainingIdentityFitProfile == DecaIdentityFitProfile.MediaPipeSurfaceAssisted) || (!_photoTrainingModeActive && flag));
			PreviousTrainingPhotoButton.IsEnabled = _photoTrainingModeActive && _photoTrainingImagePaths.Count > 1;
			NextTrainingPhotoButton.IsEnabled = _photoTrainingModeActive && _photoTrainingImagePaths.Count > 1;
			CameraToggle.IsEnabled = !_photoTrainingModeActive;
			FaceAutoFollowCheckBox.IsEnabled = !_photoTrainingModeActive;
			ThreeDdfaFaceBoxSystemMenuItem.IsEnabled = !_photoTrainingModeActive;
			if (!_photoTrainingModeActive || _photoTrainingImageIndex < 0)
			{
				PhotoTrainingStatusText.Text = "Picture Standard Model builder: off.";
				return;
			}
			string fileName = System.IO.Path.GetFileName(_photoTrainingImagePaths[_photoTrainingImageIndex]);
			AvatarReconstructionSnapshot avatarReconstructionSnapshot = _currentManualStandardModelCandidate?.Reconstruction;
			string value = ((avatarReconstructionSnapshot == null) ? "waiting for MediaPipe and DECA" : $"current {GetStandardPoseDisplayName(avatarReconstructionSnapshot.ARotationAroundXDegrees, avatarReconstructionSnapshot.BRotationAroundYDegrees)} | fit {avatarReconstructionSnapshot.ReconstructionConfidencePercent:0.#}% | delta {avatarReconstructionSnapshot.CurrentModelCoefficientDeltaRms:0.000000}");
			string value2 = ((_photoTrainingIdentityFitProfile == DecaIdentityFitProfile.MediaPipeSurfaceAssisted) ? "MediaPipe 105-point surface assist" : "FLAME 68-point control");
			PhotoTrainingStatusText.Text = $"{value2} | photo {_photoTrainingImageIndex + 1:n0}/{_photoTrainingImagePaths.Count:n0}: {fileName} | {value} | NumPad 0 accepts";
		}
	}

	private static BitmapSource LoadPhotoTrainingBitmap(string path)
	{
		BitmapImage bitmapImage = new BitmapImage();
		bitmapImage.BeginInit();
		bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
		bitmapImage.CreateOptions = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreImageCache;
		bitmapImage.UriSource = new Uri(System.IO.Path.GetFullPath(path), UriKind.Absolute);
		bitmapImage.EndInit();
		bitmapImage.Freeze();
		return bitmapImage;
	}

	private void ReviewStandardModelClicked(object sender, RoutedEventArgs e)
	{
		if (!IsAvatarUserLoggedIn)
		{
			UpdateStandardModelUi("Standard model: log in before opening checkpoint review.");
			SetStatus("Log in an avatar user before reviewing a standard model.");
			return;
		}
		try
		{
			string avatarDataFolder = GetAvatarDataFolder();
			Directory.CreateDirectory(avatarDataFolder);
			OpenWebAddress(_avatarDataReviewServer.StartOrUpdate(avatarDataFolder, CurrentAvatarProfileId, CurrentAvatarProfileDisplayName, "deca-flame-standard-model-checkpoint-v1"));
			int count = _avatarObservationRepository.ReadDataset(avatarDataFolder, CurrentAvatarProfileId, CurrentAvatarProfileDisplayName, includeDenseTopology: false, "deca-flame-standard-model-checkpoint-v1").Observations.Count;
			string status = ((count == 0) ? ("Opened standard-model review for " + CurrentAvatarProfileDisplayName + "; no checkpoints are stored yet.") : $"Opened {count:n0} standard-model checkpoint(s) for {CurrentAvatarProfileDisplayName}.");
			UpdateStandardModelUi(status);
			SetStatus(status);
		}
		catch (Exception ex)
		{
			UpdateStandardModelUi("Standard model: review failed. " + ex.Message);
			SetStatus("Could not open standard-model review: " + ex.Message);
		}
	}

	private void UpdateStandardModelUi(string? status = null)
	{
		if (status != null)
		{
			_standardModelStatus = status;
		}
		bool flag = Interlocked.CompareExchange(ref _manualStandardModelSavePending, 0, 0) != 0;
		ApplyStartStopButtonState(BuildStandardModelLiveButton, _liveStandardModelModeActive, "Build Standard Model Live", "Stop Live Standard Model", "Runs MediaPipe and recurrent DECA continuously from the webcam for human-approved nine-direction capture.", "Stops Standard Model reconstruction while leaving the camera available.");
		BuildStandardModelLiveButton.IsEnabled = !_isClosing && (_liveStandardModelModeActive || (IsAvatarUserLoggedIn && _avatarModelReadyForCapture && !_avatarModelInitializationPending && IsDecaAvatarReconstructionActive && !_photoTrainingModeActive));
		UpdateStandardPoseGridUi();
		CaptureStandardModelButton.IsEnabled = !_isClosing && !flag && IsAvatarUserLoggedIn && (_isCameraEnabled || _photoTrainingModeActive) && IsStandardModelBuildModeActive && (object)_currentManualStandardModelCandidate != null;
		ReviewStandardModelButton.IsEnabled = !_isClosing && IsAvatarUserLoggedIn && !flag;
		if (flag)
		{
			StandardModelStatusText.Text = _standardModelStatus;
			return;
		}
		if (status != null)
		{
			StandardModelStatusText.Text = status;
			return;
		}
		if (!IsAvatarUserLoggedIn)
		{
			_standardModelStatus = "Standard model: log in to capture or review.";
		}
		else if ((!_isCameraEnabled && !_photoTrainingModeActive) || !IsStandardModelBuildModeActive)
		{
			_standardModelStatus = "Standard model: choose Live, Pictures, or Pictures + MediaPipe Assist. Press NumPad 0 when the aligned overlay has a great lock.";
		}
		else
		{
			AvatarObservationCapture currentManualStandardModelCandidate = _currentManualStandardModelCandidate;
			if ((object)currentManualStandardModelCandidate != null)
			{
				_standardModelStatus = $"Standard model ready: fit {currentManualStandardModelCandidate.Reconstruction.ReconstructionConfidencePercent:0.#}% | delta {currentManualStandardModelCandidate.Reconstruction.CurrentModelCoefficientDeltaRms:0.000000} | current {GetStandardPoseDisplayName(currentManualStandardModelCandidate.Reconstruction.ARotationAroundXDegrees, currentManualStandardModelCandidate.Reconstruction.BRotationAroundYDegrees)} | " + "press NumPad 0 to accept this exact model and frame.";
			}
			else
			{
				AvatarStandardModel avatarStandardModel = _avatarStandardModelStore.Read(GetAvatarDataFolder());
				_standardModelStatus = (((object)avatarStandardModel == null) ? ("Standard model: waiting for the first live DECA/FLAME result for " + CurrentAvatarProfileDisplayName + ".") : $"Standard model: {avatarStandardModel.CompletedImageCount:n0} accepted snapshot(s) stored | waiting for a fresh live result.");
			}
		}
		StandardModelStatusText.Text = _standardModelStatus;
	}

	private void UpdateStandardPoseGridUi()
	{
		if (base.IsLoaded)
		{
			IReadOnlyList<string> directionKeys = AvatarStandardPoseGrid.DirectionKeys;
			StandardPoseGridText.Text = $"Human standard views: {CountStandardPoseIdentityEvidence():n0}/9\n{Mark(HasEvidence(directionKeys[0]), "TL")}  {Mark(HasEvidence(directionKeys[1]), "TM")}  {Mark(HasEvidence(directionKeys[2]), "TR")}\n{Mark(HasEvidence(directionKeys[3]), "LC")}  {Mark(HasEvidence(directionKeys[4]), "C ")}  {Mark(HasEvidence(directionKeys[5]), "MR")}\n{Mark(HasEvidence(directionKeys[6]), "BL")}  {Mark(HasEvidence(directionKeys[7]), "BC")}  {Mark(HasEvidence(directionKeys[8]), "BR")}\n" + (HasCompleteHumanStandardModel ? "Complete. Extra captures refine the matching direction." : ("Next: " + GetNextStandardPoseTarget() + ". Automatic capture remains locked."));
			StandardPoseGridText.Foreground = (HasCompleteHumanStandardModel ? CreateFrozenBrush(128, 224, 164) : CreateFrozenBrush(185, 215, 239));
		}
		bool HasEvidence(string key)
		{
			if (_standardPoseAtlas.TryGetValue(key, out AvatarStandardPoseSample value) && AvatarStandardPoseGrid.IsStructurallyComplete(value))
			{
				return AvatarStandardPoseGrid.HasCompleteIdentityEvidence(value);
			}
			return false;
		}
		static string Mark(bool accepted, string label)
		{
			if (!accepted)
			{
				return "[ ] " + label;
			}
			return "[X] " + label;
		}
	}

	private int CountStandardPoseIdentityEvidence()
	{
		return _standardPoseAtlas.Values.Count((AvatarStandardPoseSample sample) => AvatarStandardPoseGrid.IsStructurallyComplete(sample) && AvatarStandardPoseGrid.HasCompleteIdentityEvidence(sample));
	}

	private string GetNextStandardPoseTarget()
	{
		string nextMissingDirectionKey = AvatarStandardPoseGrid.GetNextMissingDirectionKey(_standardPoseAtlas);
		if (nextMissingDirectionKey != null)
		{
			return AvatarStandardPoseGrid.GetDisplayName(nextMissingDirectionKey);
		}
		return "any direction you want to refine";
	}

	private static string GetStandardPoseDisplayName(double aRotationAroundXDegrees, double bRotationAroundYDegrees)
	{
		string text = AvatarStandardPoseGrid.Classify(aRotationAroundXDegrees, bRotationAroundYDegrees);
		return AvatarStandardPoseGrid.GetDisplayName(text) + " (" + text + ")";
	}

	private async void OpenAvatarModelProgressClicked(object sender, RoutedEventArgs e)
	{
		try
		{
			string avatarDataFolder = GetAvatarDataFolder();
			Directory.CreateDirectory(avatarDataFolder);
			AvatarReportSnapshot snapshot = CreateAvatarReportSnapshot(avatarDataFolder);
			SetStatus("Writing Avatar Model Progress...");
			AvatarReportSaveResult avatarReportSaveResult = await Task.Run(() => WriteAvatarReports(snapshot));
			if (!_isClosing)
			{
				ApplyAvatarReportSaveResult(avatarReportSaveResult);
				OpenLocalFile(avatarReportSaveResult.AvatarModelHtmlPath);
				string text = "Opened Avatar Model Progress: " + avatarReportSaveResult.AvatarModelHtmlPath;
				SetStatus(text);
				MonitorStatusText.Text = text;
			}
		}
		catch (Exception ex)
		{
			string text2 = "Could not open Avatar Model Progress: " + ex.Message;
			MonitorStatusText.Text = text2;
			SetStatus(text2);
		}
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
		if (_latestFrame == null || !_currentFaceFeatureDetection.HasFace)
		{
			SetStatus("Turn the camera on and keep the face visible before building a dense MediaPipe warp.");
			MessageBox.Show(this, "Turn the camera on and keep the face visible before building a dense MediaPipe warp.", "Dense MediaPipe Warp", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		BitmapSource frame = (_latestFrame.IsFrozen ? _latestFrame : _latestFrame.Clone());
		if (!frame.IsFrozen && frame.CanFreeze)
		{
			frame.Freeze();
		}
		ThreeDdfaOnnxSidecarFaceBox faceBox = CreateThreeDdfaFaceBox(_currentFaceFeatureDetection);
		string profileFolder = GetAvatarDataFolder();
		BuildDenseWarpButton.IsEnabled = false;
		BuildDenseWarpButton.Content = "Building Dense Warp...";
		SetStatus("Running one full 3DDFA identity fit, then applying trusted MediaPipe geometry off the camera thread...");
		try
		{
			_currentMediaPipeGeometryModel = await _mediaPipeGeometryPipeline.FlushAsync();
			MediaPipeNormalizedFaceModel mediaPipeModel = _currentMediaPipeGeometryModel;
			if (!mediaPipeModel.HasGeometry)
			{
				SetStatus("No measured MediaPipe face geometry is ready yet. Leave the camera and tracking overlay running for a short time, then try again.");
				MessageBox.Show(this, "No measured MediaPipe face geometry is ready yet. Leave the camera and tracking overlay running for a short time, then try again.", "Dense MediaPipe Warp", MessageBoxButton.OK, MessageBoxImage.Asterisk);
				return;
			}
			string subjectId = CurrentAvatarProfileId;
			string displayName = CurrentAvatarProfileDisplayName;
			(ThreeDdfaOnnxSidecarResponse, DenseFaceWarpResult, DenseFaceWarpSaveResult) tuple = await Task.Run(delegate
			{
				using ThreeDdfaOnnxReconstructionClient threeDdfaOnnxReconstructionClient = new ThreeDdfaOnnxReconstructionClient(_threeDdfaOnnxEnvironment);
				ThreeDdfaOnnxSidecarResponse threeDdfaOnnxSidecarResponse = threeDdfaOnnxReconstructionClient.Reconstruct(frame, DateTime.UtcNow, faceBox, ThreeDdfaOnnxRequestMode.Full, 1);
				DenseFaceWarpResult denseFaceWarpResult = EvidenceWeightedDenseFaceWarper.Warp(ThreeDdfaMediaPipeWarpInputFactory.Create(threeDdfaOnnxSidecarResponse, mediaPipeModel, subjectId, displayName, DateTime.UtcNow));
				DenseFaceWarpSaveResult item = DenseFaceWarpStore.Write(profileFolder, denseFaceWarpResult);
				return (Response: threeDdfaOnnxSidecarResponse, Warp: denseFaceWarpResult, Saved: item);
			});
			_visionBenchmarkRecorder.Record(tuple.Item1.Diagnostics);
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
		if (MessageBox.Show(this, "Are you sure you want to permanently delete all avatar data for " + CurrentAvatarProfileDisplayName + "?\n\nThis deletes retained scans, paired photos, the generated model, the standard model, model history, and review reports. This cannot be undone.", "Delete Avatar Data?", MessageBoxButton.YesNo, MessageBoxImage.Exclamation) != MessageBoxResult.Yes)
		{
			return;
		}
		Task avatarReportWriterTask = _avatarReportWriterTask;
		if (avatarReportWriterTask != null && !avatarReportWriterTask.IsCompleted)
		{
			SetStatus("Avatar report save is still finishing. Try Delete Avatar Data again in a moment.");
			return;
		}
		_avatarObservationStorageService.DiscardPendingCandidates();
		if (_avatarObservationStorageService.IsBusy)
		{
			SetStatus("Avatar batch storage and model work is still finishing. Try Delete Avatar Data again in a moment.");
			return;
		}
		try
		{
			string folder = GetAvatarDataFolder();
			_avatarLearningRequested = false;
			_liveStandardModelModeActive = false;
			int deletedObservationCount = _avatarObservationRepository.ResetProfile(folder, CurrentAvatarProfileId);
			_avatarStandardModelStore.Delete(folder);
			DenseFaceWarpStore.Delete(folder);
			if (IsMediaPipeOnlyGeometryMode)
			{
				_currentMediaPipeGeometryModel = await _mediaPipeGeometryPipeline.ResetProfileAsync();
			}
			_standardPoseAtlas.Clear();
			DeleteDerivedAvatarFiles(folder);
			lock (_decaClientLock)
			{
				_decaAvatarClient?.SetCurrentModelShapeCoefficients(Array.Empty<double>());
			}
			Directory.CreateDirectory(folder);
			ResetAvatarCaptureGate("avatar data deleted; capture resumes when Start Avatar Capture is active");
			_currentAvatarCaptureQuality = AvatarCaptureQualityAssessment.Waiting;
			_avatarSystemDashboardPath = "";
			_avatarModelHtmlPath = "";
			_avatarModelReadyForCapture = IsAvatarUserLoggedIn;
			UpdateAvatarLearningStatusUi();
			UpdateStandardModelUi("Standard model: deleted with avatar data.");
			SetStatus($"Avatar data deleted. Removed {deletedObservationCount} retained observation(s). Start Avatar Capture when you are ready to begin again.");
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
			DateTime utcNow = DateTime.UtcNow;
			QueueFaceFeatureDetection(bitmap, utcNow);
			if (HasUsableFaceFeatureLock(utcNow) && FaceAutoFollowCheckBox.IsChecked == true)
			{
				FaceCueGuideLayout faceCueGuideLayout = _currentFaceFeatureDetection.ToGuideLayout(manualFaceCueLayout);
				FaceCueGuideLayout faceCueGuideLayout2 = _activeFaceCueLayout ?? faceCueGuideLayout;
				_activeFaceCueLayout = new FaceCueGuideLayout(faceCueGuideLayout2.CenterXPercent + (faceCueGuideLayout.CenterXPercent - faceCueGuideLayout2.CenterXPercent) * 0.45, faceCueGuideLayout2.CenterYPercent + (faceCueGuideLayout.CenterYPercent - faceCueGuideLayout2.CenterYPercent) * 0.45, faceCueGuideLayout2.HeightPercent + (faceCueGuideLayout.HeightPercent - faceCueGuideLayout2.HeightPercent) * 0.3);
				manualFaceCueLayout = _activeFaceCueLayout;
			}
			else if (FaceAutoFollowCheckBox.IsChecked == true)
			{
				FaceCueGuideLayout faceCueGuideLayout3 = _activeFaceCueLayout ?? manualFaceCueLayout;
				if ((utcNow - _lastFaceAutoFollowAt).TotalMilliseconds >= 500.0)
				{
					_activeFaceCueLayout = FaceCueAutoLayoutEstimator.Estimate(bitmap, faceCueGuideLayout3);
					_lastFaceAutoFollowAt = utcNow;
				}
				manualFaceCueLayout = _activeFaceCueLayout ?? faceCueGuideLayout3;
			}
		}
		catch
		{
			MonitorStatusText.Text = "Face tracking is resyncing with the latest camera frame.";
		}
	}

	private void QueueFaceFeatureDetection(BitmapSource bitmap, DateTime now)
	{
		if (!_isClosing && IsSelectedFaceBoxSystemAvailable() && FaceAutoFollowCheckBox.IsChecked == true && !(now - _lastFaceFeatureDetectionAt < FaceFeatureDetectionTargetInterval) && Interlocked.CompareExchange(ref _faceFeatureDetectionPending, 1, 0) == 0)
		{
			_lastFaceFeatureDetectionAt = now;
			FaceBoxSystem faceBoxSystem = _selectedFaceBoxSystem;
			int faceBoxSystemGeneration = _faceBoxSystemGeneration;
			Task.Run(() => ProcessFaceFeatureDetectionFrameAsync(bitmap, now, faceBoxSystem, faceBoxSystemGeneration));
		}
	}

	private async Task ProcessFaceFeatureDetectionFrameAsync(BitmapSource bitmap, DateTime capturedAtUtc, FaceBoxSystem faceBoxSystem, int faceBoxSystemGeneration)
	{
		try
		{
			if (!_isClosing && faceBoxSystemGeneration == _faceBoxSystemGeneration && faceBoxSystem == _selectedFaceBoxSystem)
			{
				FaceBoxTrackingFrameResult trackingResult = DetectFaceBox(bitmap, (capturedAtUtc == DateTime.MinValue) ? DateTime.UtcNow : capturedAtUtc, faceBoxSystem, faceBoxSystemGeneration);
				await ApplyFaceFeatureDetectionResultAsync(trackingResult);
			}
		}
		catch (Exception ex)
		{
			ReportRecoverableVisionError("Landmark tracker skipped one frame and recovered: " + ex.Message);
		}
		finally
		{
			Interlocked.Exchange(ref _faceFeatureDetectionPending, 0);
		}
	}

	private void ReportRecoverableVisionError(string status)
	{
		long ticks = DateTime.UtcNow.Ticks;
		long num = Interlocked.Read(in _lastRecoverableVisionErrorStatusTicks);
		if (ticks - num >= RecoverableVisionErrorStatusInterval.Ticks && Interlocked.CompareExchange(ref _lastRecoverableVisionErrorStatusTicks, ticks, num) == num)
		{
			base.Dispatcher.InvokeAsync(delegate
			{
				SetStatus(status);
			}, DispatcherPriority.Background);
		}
	}

	private FaceBoxTrackingFrameResult DetectFaceBox(BitmapSource bitmap, DateTime capturedAtUtc, FaceBoxSystem faceBoxSystem, int faceBoxSystemGeneration)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		AvatarCaptureQualityAssessment captureQuality = CloneCaptureQuality(_currentAvatarCaptureQuality);
		FaceFrameGeometry currentFaceFrameGeometry = _currentFaceFrameGeometry;
		if (faceBoxSystem == FaceBoxSystem.MediaPipe)
		{
			FaceLandmarkTrackingResult trackingResult;
			lock (_faceLandmarkTrackerLock)
			{
				trackingResult = ((_isClosing || _faceLandmarkTracker == null) ? FaceLandmarkTrackingResult.None : _faceLandmarkTracker.Detect(bitmap, capturedAtUtc));
			}
			stopwatch.Stop();
			return new FaceBoxTrackingFrameResult(faceBoxSystem, faceBoxSystemGeneration, trackingResult, null, null, "", 0L, capturedAtUtc, bitmap, captureQuality, currentFaceFrameGeometry, stopwatch.Elapsed.TotalMilliseconds);
		}
		ThreeDdfaOnnxReconstructionClient threeDdfaFaceBoxClient;
		lock (_threeDdfaClientLock)
		{
			threeDdfaFaceBoxClient = _threeDdfaFaceBoxClient;
		}
		AvatarReconstructionSnapshot threeDdfaSnapshot = null;
		string currentAvatarProfileId = CurrentAvatarProfileId;
		long generation = _avatarUserSession.Generation;
		bool flag = !IsDecaAvatarReconstructionActive && IsAvatarUserLoggedIn && _avatarLearningRequested && _avatarCaptureGateAccepted && _avatarObservationStorageService.CanAcceptCandidate && (capturedAtUtc - _lastAvatarReconstructionRequestAtUtc).TotalMilliseconds >= 1000.0;
		if (flag)
		{
			_lastAvatarReconstructionRequestAtUtc = capturedAtUtc;
		}
		Interlocked.Exchange(ref _avatarReconstructionPending, 1);
		ThreeDdfaOnnxSidecarResponse threeDdfaOnnxSidecarResponse;
		try
		{
			bool flag2 = _threeDdfaTrackingFaceBox == null || (capturedAtUtc - _lastThreeDdfaFaceBoxesAtUtc).TotalMilliseconds >= 1000.0;
			ThreeDdfaOnnxSidecarFaceBox threeDdfaOnnxSidecarFaceBox = ((flag || !flag2) ? _threeDdfaTrackingFaceBox : null);
			if (threeDdfaOnnxSidecarFaceBox == null)
			{
				_lastThreeDdfaFaceBoxesAtUtc = capturedAtUtc;
			}
			threeDdfaOnnxSidecarResponse = ((threeDdfaFaceBoxClient == null) ? new ThreeDdfaOnnxSidecarResponse
			{
				Ok = false,
				Status = "3DDFA-V2 face-box client stopped",
				TrustDecision = "The selected 3DDFA-V2 tracking session is no longer active."
			} : threeDdfaFaceBoxClient.Reconstruct(bitmap, capturedAtUtc, threeDdfaOnnxSidecarFaceBox, (!flag) ? ThreeDdfaOnnxRequestMode.Tracking : ThreeDdfaOnnxRequestMode.Full, flag ? 1 : 200));
			if (threeDdfaOnnxSidecarResponse.Ok && threeDdfaOnnxSidecarResponse.HasFace)
			{
				_threeDdfaTrackingFaceBox = CreateThreeDdfaTrackingFaceBox(threeDdfaOnnxSidecarResponse, bitmap.PixelWidth, bitmap.PixelHeight);
			}
			else if (threeDdfaFaceBoxClient != null && threeDdfaOnnxSidecarFaceBox != null)
			{
				_visionBenchmarkRecorder.Record(threeDdfaOnnxSidecarResponse.Diagnostics);
				_lastThreeDdfaFaceBoxesAtUtc = capturedAtUtc;
				threeDdfaOnnxSidecarResponse = threeDdfaFaceBoxClient.Reconstruct(bitmap, capturedAtUtc, null, ThreeDdfaOnnxRequestMode.Tracking, 200);
				if (threeDdfaOnnxSidecarResponse.Ok && threeDdfaOnnxSidecarResponse.HasFace)
				{
					_threeDdfaTrackingFaceBox = CreateThreeDdfaTrackingFaceBox(threeDdfaOnnxSidecarResponse, bitmap.PixelWidth, bitmap.PixelHeight);
				}
			}
			if (flag)
			{
				threeDdfaSnapshot = CreateThreeDdfaLastGoodSnapshot(threeDdfaOnnxSidecarResponse, capturedAtUtc);
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
			Interlocked.Exchange(ref _avatarReconstructionPending, 0);
			if (faceBoxSystemGeneration != _faceBoxSystemGeneration || faceBoxSystem != _selectedFaceBoxSystem)
			{
				threeDdfaFaceBoxClient?.Dispose();
			}
		}
		stopwatch.Stop();
		FaceLandmarkTrackingResult trackingResult2 = ThreeDdfaOnnxFaceTrackingMapper.ToTrackingResult(threeDdfaOnnxSidecarResponse, bitmap.PixelWidth, bitmap.PixelHeight, capturedAtUtc);
		return new FaceBoxTrackingFrameResult(faceBoxSystem, faceBoxSystemGeneration, trackingResult2, threeDdfaOnnxSidecarResponse, threeDdfaSnapshot, currentAvatarProfileId, generation, capturedAtUtc, bitmap, captureQuality, currentFaceFrameGeometry, stopwatch.Elapsed.TotalMilliseconds);
	}

	private Task ApplyFaceFeatureDetectionResultAsync(FaceBoxTrackingFrameResult trackingResult)
	{
		return base.Dispatcher.InvokeAsync(delegate
		{
			if (!_isClosing && trackingResult.FaceBoxSystemGeneration == _faceBoxSystemGeneration && trackingResult.FaceBoxSystem == _selectedFaceBoxSystem && (!_photoTrainingModeActive || trackingResult.SourceFrame == _photoTrainingFrame))
			{
				DateTime utcNow = DateTime.UtcNow;
				RecordFaceBoxDiagnostics(trackingResult);
				if (trackingResult.ThreeDdfaResponse != null)
				{
					_currentThreeDdfaOnnxResponse = trackingResult.ThreeDdfaResponse;
					if (trackingResult.ThreeDdfaSnapshot != null && _avatarUserSession.Matches(trackingResult.ProfileId, trackingResult.SessionGeneration))
					{
						_currentAvatarReconstructionSnapshot = trackingResult.ThreeDdfaSnapshot;
						TrackAvatarObservationSnapshot(trackingResult.ThreeDdfaSnapshot, trackingResult.SourceFrame, trackingResult.CaptureQuality, trackingResult.FaceGeometry);
					}
				}
				FaceLandmarkTrackingResult trackingResult2 = trackingResult.TrackingResult;
				FaceFeatureDetection featureDetection = trackingResult2.FeatureDetection;
				if (featureDetection.HasFace)
				{
					_currentFaceFeatureDetection = featureDetection;
					_lastFaceFeatureLockAt = utcNow;
					FaceLandmarkFrame faceLandmarkFrame = (trackingResult2.LandmarkFrame.HasFace ? trackingResult2.LandmarkFrame : featureDetection.ToLandmarkFrame(utcNow));
					_currentFaceLandmarkFrame = _faceLandmarkReconstructor.Update(faceLandmarkFrame);
					if (_selectedFaceBoxSystem == FaceBoxSystem.MediaPipe)
					{
						_mediaPipeConvergenceAuditor.Record(faceLandmarkFrame, _currentFaceLandmarkFrame, trackingResult.SourceFrame.PixelWidth, trackingResult.SourceFrame.PixelHeight);
					}
					_currentFaceLandmarkMetrics = _faceLandmarkMetricCalculator.Update(_currentFaceLandmarkFrame);
					_currentFaceFrameGeometry = _faceFrameGeometryEstimator.Estimate(new FaceFrameGeometryEstimatorInput
					{
						Frame = _currentFaceLandmarkFrame,
						FrameWidthPixels = _latestFrame?.PixelWidth,
						FrameHeightPixels = _latestFrame?.PixelHeight,
						Calibration = GetCurrentFaceFrameGeometryCalibration()
					});
					_currentFaceLockStabilityAnalysis = _faceLockStabilityAnalyzer.Update(_currentFaceFeatureDetection, _currentFaceLandmarkFrame, _currentFaceLandmarkMetrics);
					UpdateAvatarCaptureState(utcNow);
					if (IsMediaPipeOnlyGeometryMode && _selectedFaceBoxSystem == FaceBoxSystem.MediaPipe && IsAvatarUserLoggedIn && _avatarLearningRequested && faceLandmarkFrame.HasDenseMesh)
					{
						QueueMediaPipeGeometry(faceLandmarkFrame, trackingResult.SourceFrame.PixelWidth, trackingResult.SourceFrame.PixelHeight);
					}
					else if (IsAvatarReconstructionReady && (IsDecaAvatarReconstructionActive || _selectedFaceBoxSystem == FaceBoxSystem.MediaPipe))
					{
						QueueAvatarReconstruction(trackingResult.SourceFrame, trackingResult.CapturedAtUtc, featureDetection, faceLandmarkFrame, new PoseAngles(faceLandmarkFrame.HeadPitchDegrees, faceLandmarkFrame.HeadYawDegrees, faceLandmarkFrame.HeadRollDegrees));
					}
				}
				else
				{
					if (_selectedFaceBoxSystem == FaceBoxSystem.MediaPipe)
					{
						_mediaPipeConvergenceAuditor.RecordMissingFace(trackingResult.CapturedAtUtc);
					}
					if (!HasUsableFaceFeatureLock(utcNow))
					{
						_currentFaceFeatureDetection = FaceFeatureDetection.None;
						ResetLandmarkTracking();
					}
				}
				if (_showLiveWireframePreview)
				{
					DrawLiveWireframePreview();
				}
				UpdateFaceCueGuideOverlay(_latestFrame);
			}
		}, DispatcherPriority.Background).Task;
	}

	private void QueueMediaPipeGeometry(FaceLandmarkFrame observedLandmarks, int frameWidthPixels, int frameHeightPixels)
	{
		FaceFrameGeometryCalibration currentFaceFrameGeometryCalibration = GetCurrentFaceFrameGeometryCalibration();
		_mediaPipeGeometryPipeline.Queue(MediaPipeGeometryFrame.Create(observedLandmarks, frameWidthPixels, frameHeightPixels, GetSelectedCameraName(), currentFaceFrameGeometryCalibration.CameraHorizontalFovDegrees ?? 70.0));
	}

	private void RecordFaceBoxDiagnostics(FaceBoxTrackingFrameResult trackingResult)
	{
		_visionBenchmarkRecorder.Record(trackingResult.TrackingResult.Diagnostics);
		_lastFaceBoxBackendStatus = trackingResult.TrackingResult.BackendStatus;
	}

	private void QueueAvatarReconstruction(BitmapSource? bitmap, DateTime capturedAtUtc, FaceFeatureDetection detection, FaceLandmarkFrame observedLandmarks, PoseAngles mediaPipePose)
	{
		if (_isClosing || (!IsDecaAvatarReconstructionActive && _selectedFaceBoxSystem != FaceBoxSystem.MediaPipe) || bitmap == null || !IsAvatarReconstructionReady || !IsAvatarUserLoggedIn || (!_avatarLearningRequested && !IsStandardModelBuildModeActive) || (!IsDecaAvatarReconstructionActive && !_avatarCaptureGateAccepted) || !detection.HasFace || !observedLandmarks.HasDenseMesh || (!IsDecaAvatarReconstructionActive && (capturedAtUtc - _lastAvatarReconstructionRequestAtUtc).TotalMilliseconds < 1000.0) || Interlocked.CompareExchange(ref _avatarReconstructionPending, 1, 0) != 0)
		{
			return;
		}
		_lastAvatarReconstructionRequestAtUtc = capturedAtUtc;
		BitmapSource frame = (bitmap.IsFrozen ? bitmap : bitmap.Clone());
		if (!frame.IsFrozen && frame.CanFreeze)
		{
			frame.Freeze();
		}
		ThreeDdfaOnnxSidecarFaceBox faceBox = CreateThreeDdfaFaceBox(detection);
		string profileId = CurrentAvatarProfileId;
		long sessionGeneration = _avatarUserSession.Generation;
		FaceBoxSystem faceBoxSystem = _selectedFaceBoxSystem;
		int faceBoxSystemGeneration = _faceBoxSystemGeneration;
		AvatarCaptureQualityAssessment captureQuality = CloneCaptureQuality(_currentAvatarCaptureQuality);
		FaceFrameGeometry faceGeometry = _currentFaceFrameGeometry;
		DecaIdentityFitProfile identityFitProfile = (_photoTrainingModeActive ? _photoTrainingIdentityFitProfile : DecaIdentityFitProfile.Flame68);
		if (IsDecaAvatarReconstructionActive)
		{
			DecaReconstructionClient decaClient;
			lock (_decaClientLock)
			{
				decaClient = _decaAvatarClient;
			}
			if (decaClient == null)
			{
				Interlocked.Exchange(ref _avatarReconstructionPending, 0);
				return;
			}
			DecaSidecarFaceBox decaFaceBox = CreateDecaFaceBox(detection);
			if (decaFaceBox == null)
			{
				Interlocked.Exchange(ref _avatarReconstructionPending, 0);
				return;
			}
			Task.Run(() => ProcessDecaReconstructionAsync(decaClient, frame, capturedAtUtc, decaFaceBox, observedLandmarks, captureQuality, faceGeometry, profileId, sessionGeneration, faceBoxSystem, faceBoxSystemGeneration, identityFitProfile));
			return;
		}
		ThreeDdfaOnnxReconstructionClient threeDdfaClient;
		lock (_threeDdfaClientLock)
		{
			threeDdfaClient = _threeDdfaAvatarClient;
		}
		if (threeDdfaClient == null)
		{
			Interlocked.Exchange(ref _avatarReconstructionPending, 0);
			return;
		}
		Task.Run(() => ProcessThreeDdfaOnnxReconstructionAsync(threeDdfaClient, frame, capturedAtUtc, faceBox, captureQuality, faceGeometry, profileId, sessionGeneration, faceBoxSystem, faceBoxSystemGeneration, mediaPipePose));
	}

	private async Task ProcessDecaReconstructionAsync(DecaReconstructionClient client, BitmapSource bitmap, DateTime capturedAtUtc, DecaSidecarFaceBox faceBox, FaceLandmarkFrame observedLandmarks, AvatarCaptureQualityAssessment captureQuality, FaceFrameGeometry faceGeometry, string profileId, long sessionGeneration, FaceBoxSystem faceBoxSystem, int faceBoxSystemGeneration, DecaIdentityFitProfile identityFitProfile)
	{
		AvatarReconstructionSnapshot snapshot = null;
		try
		{
			snapshot = client.Reconstruct(bitmap, capturedAtUtc, faceBox, observedLandmarks, null, identityFitProfile);
		}
		finally
		{
			Interlocked.Exchange(ref _avatarReconstructionPending, 0);
		}
		await base.Dispatcher.InvokeAsync(delegate
		{
			if (!_isClosing && _avatarUserSession.Matches(profileId, sessionGeneration) && faceBoxSystemGeneration == _faceBoxSystemGeneration && faceBoxSystem == _selectedFaceBoxSystem && (!_photoTrainingModeActive || bitmap == _photoTrainingFrame))
			{
				if (snapshot == null)
				{
					SetStatus(client.Status);
					UpdateAvatarLearningStatusUi();
				}
				else
				{
					_currentAvatarReconstructionSnapshot = snapshot;
					_currentManualStandardModelCandidate = new AvatarObservationCapture(GetAvatarDataFolder(), CurrentAvatarProfileId, CurrentAvatarProfileDisplayName, snapshot, bitmap, captureQuality, faceGeometry);
					UpdateAvatarLearningStatusUi();
					UpdateStandardModelUi();
					UpdatePhotoTrainingUi();
					if (_avatarLearningRequested && HasCompleteHumanStandardModel)
					{
						TrackAvatarObservationSnapshot(snapshot, bitmap, captureQuality, faceGeometry);
					}
					if (_showAvatarModelOverlay)
					{
						UpdateFaceCueGuideOverlay(_latestFrame);
					}
					if (_showLiveWireframePreview)
					{
						DrawLiveWireframePreview();
					}
				}
			}
		}, DispatcherPriority.Background);
	}

	private async Task ProcessThreeDdfaOnnxReconstructionAsync(ThreeDdfaOnnxReconstructionClient client, BitmapSource bitmap, DateTime capturedAtUtc, ThreeDdfaOnnxSidecarFaceBox? faceBox, AvatarCaptureQualityAssessment captureQuality, FaceFrameGeometry faceGeometry, string profileId, long sessionGeneration, FaceBoxSystem faceBoxSystem, int faceBoxSystemGeneration, PoseAngles mediaPipePose)
	{
		AvatarReconstructionSnapshot snapshot = null;
		ThreeDdfaOnnxSidecarResponse response;
		try
		{
			response = client.Reconstruct(bitmap, capturedAtUtc, faceBox, ThreeDdfaOnnxRequestMode.Full, 1);
			_visionBenchmarkRecorder.Record(response.Diagnostics);
			bool flag = faceBoxSystemGeneration == _faceBoxSystemGeneration && faceBoxSystem == _selectedFaceBoxSystem && _avatarUserSession.Matches(profileId, sessionGeneration);
			if (!(response.Ok && response.HasFace && flag))
			{
				_ = _poseAlignmentAuditor.CurrentSummary;
			}
			else
			{
				_poseAlignmentAuditor.Record(capturedAtUtc, mediaPipePose, response.Pose);
			}
			snapshot = CreateThreeDdfaLastGoodSnapshot(response, capturedAtUtc);
		}
		catch (Exception ex)
		{
			response = new ThreeDdfaOnnxSidecarResponse
			{
				Ok = false,
				Status = "3DDFA/ONNX reconstruction failed: " + ex.Message,
				TrustDecision = "3DDFA/ONNX failed; do not use this frame for avatar reconstruction trust."
			};
		}
		finally
		{
			Interlocked.Exchange(ref _avatarReconstructionPending, 0);
		}
		await base.Dispatcher.InvokeAsync(delegate
		{
			if (!_isClosing && _avatarUserSession.Matches(profileId, sessionGeneration) && faceBoxSystemGeneration == _faceBoxSystemGeneration && faceBoxSystem == _selectedFaceBoxSystem)
			{
				_currentThreeDdfaOnnxResponse = response;
				if (snapshot != null)
				{
					_currentAvatarReconstructionSnapshot = snapshot;
				}
				_currentManualStandardModelCandidate = null;
				UpdateAvatarLearningStatusUi();
				UpdateStandardModelUi();
				TrackAvatarObservationSnapshot(snapshot, bitmap, captureQuality, faceGeometry);
				if (_showLiveWireframePreview)
				{
					DrawLiveWireframePreview();
				}
			}
		}, DispatcherPriority.Background);
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

	private void TrackAvatarObservationSnapshot(AvatarReconstructionSnapshot? snapshot, BitmapSource sourceFrame, AvatarCaptureQualityAssessment captureQuality, FaceFrameGeometry faceGeometry)
	{
		if (snapshot != null && _avatarLearningRequested && HasCompleteHumanStandardModel && _avatarObservationStorageService.CanAcceptCandidate)
		{
			BitmapSource bitmapSource = (sourceFrame.IsFrozen ? sourceFrame : sourceFrame.Clone());
			if (!bitmapSource.IsFrozen && bitmapSource.CanFreeze)
			{
				bitmapSource.Freeze();
			}
			AvatarObservationCapture capture = new AvatarObservationCapture(GetAvatarDataFolder(), CurrentAvatarProfileId, CurrentAvatarProfileDisplayName, snapshot, bitmapSource, captureQuality, faceGeometry);
			switch (_avatarObservationStorageService.AddCandidate(capture))
			{
			case AvatarObservationBatchAdmission.HeldWorkerBusy:
				SetStatus("Five recurrent results are safely held in memory. Reconstruction keeps running at full speed while storage waits for its single worker.");
				break;
			case AvatarObservationBatchAdmission.IgnoredWaitingBatch:
				SetStatus("The bounded five-result storage handoff is full. Reconstruction continues in memory; no storage backlog was created.");
				break;
			case AvatarObservationBatchAdmission.Launched:
				SetStatus("Avatar batch worker started selecting the lowest-delta result from five recurrent updates. File and model work remain off the camera thread.");
				break;
			}
		}
	}

	private async Task FinalizeAvatarObservationBatchAsync(AvatarObservationBatch batch, CancellationToken cancellationToken)
	{
		if (!_isClosing && batch.Candidates.Count != 0)
		{
			AvatarReportSnapshot snapshot = await base.Dispatcher.InvokeAsync(() => CreateAvatarReportSnapshotForBatch(batch), DispatcherPriority.Background, cancellationToken).Task.ConfigureAwait(continueOnCapturedContext: false);
			cancellationToken.ThrowIfCancellationRequested();
			AvatarReportSaveResult result = WriteAvatarReports(snapshot, batch);
			lock (_personalFaceReportWriterLock)
			{
				_pendingAvatarReportSnapshot = null;
			}
			await base.Dispatcher.InvokeAsync(delegate
			{
				ApplyAvatarReportSaveResult(result);
			}, DispatcherPriority.Background, cancellationToken).Task.ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private AvatarReportSnapshot CreateAvatarReportSnapshotForBatch(AvatarObservationBatch batch)
	{
		if (string.Equals(batch.SubjectId, CurrentAvatarProfileId, StringComparison.Ordinal))
		{
			return CreateAvatarReportSnapshot(batch.ProfileFolder);
		}
		IReadOnlyList<AvatarObservationCapture> candidates = batch.Candidates;
		AvatarObservationCapture avatarObservationCapture = candidates[candidates.Count - 1];
		return new AvatarReportSnapshot(batch.ProfileFolder, batch.SubjectId, batch.SubjectDisplayName, avatarObservationCapture.CaptureQuality, UserLoggedIn: false, AvatarCaptureRequested: false, AvatarCaptureActive: false, "Avatar capture stopped", "The accepted batch was incorporated after the active user changed.", avatarObservationCapture.FaceGeometry, FaceReconstructionLaneStatus.Waiting);
	}

	private void AvatarObservationBatchCompleted(object? sender, AvatarObservationBatchEventArgs e)
	{
		base.Dispatcher.BeginInvoke((Action)delegate
		{
			if (!_isClosing && string.Equals(e.SubjectId, CurrentAvatarProfileId, StringComparison.Ordinal))
			{
				AvatarObservationBatch batch = e.Batch;
				if (e.FinalizationError != null)
				{
					SetStatus("Avatar batch storage completed, but its model refresh failed: " + e.FinalizationError.Message);
				}
				else
				{
					string value = ((batch.Errors.Count > 0) ? $" {batch.Errors.Count} candidate(s) hit storage errors." : "");
					SetStatus(IsDecaAvatarReconstructionActive ? $"Avatar batch complete: {batch.AcceptedCount}/{batch.Candidates.Count} lowest-delta recurrent DECA/FLAME result retained; {batch.RetainedCount} retained; current model saved.{value}" : $"Avatar batch complete: {batch.AcceptedCount}/{batch.Candidates.Count} improved the retained set; {batch.RetainedCount} retained; model refreshed.{value}");
					StartPendingAvatarReportWriterIfPossible();
				}
			}
		}, DispatcherPriority.Background);
	}

	private void AvatarObservationWorkerCompleted(object? sender, AvatarObservationWorkerCompletedEventArgs e)
	{
		base.Dispatcher.BeginInvoke((Action)delegate
		{
			if (!_isClosing)
			{
				_lastAvatarObservationWorkerDuration = e.Duration;
				UpdateAvatarWorkerTimingUi();
				StartPendingAvatarReportWriterIfPossible();
			}
		}, DispatcherPriority.Background);
	}

	private void AvatarObservationWorkerStarted(object? sender, AvatarObservationWorkerStartedEventArgs e)
	{
		base.Dispatcher.BeginInvoke((Action)delegate
		{
			if (!_isClosing)
			{
				TimeSpan? waitAfterPreviousWorker = e.WaitAfterPreviousWorker;
				if (waitAfterPreviousWorker.HasValue)
				{
					TimeSpan valueOrDefault = waitAfterPreviousWorker.GetValueOrDefault();
					_lastAvatarObservationWorkerStartWait = valueOrDefault;
				}
				UpdateAvatarWorkerTimingUi();
			}
		}, DispatcherPriority.Background);
	}

	private void MediaPipeGeometryPipelineModelUpdated(object? sender, MediaPipeGeometryModelUpdatedEventArgs e)
	{
		base.Dispatcher.BeginInvoke((Action)delegate
		{
			if (!_isClosing && IsAvatarUserLoggedIn && string.Equals(e.Model.SubjectId, CurrentAvatarProfileId, StringComparison.OrdinalIgnoreCase))
			{
				_currentMediaPipeGeometryModel = e.Model;
				_lastMediaPipeGeometryProcessingDuration = e.ProcessingDuration;
				UpdateAvatarLearningStatusUi();
			}
		}, DispatcherPriority.Background);
	}

	private void UpdateAvatarWorkerTimingUi()
	{
		if (base.IsLoaded)
		{
			TimeSpan? lastAvatarObservationWorkerDuration = _lastAvatarObservationWorkerDuration;
			object obj;
			if (lastAvatarObservationWorkerDuration.HasValue)
			{
				TimeSpan valueOrDefault = lastAvatarObservationWorkerDuration.GetValueOrDefault();
				obj = FormatWorkerDuration(valueOrDefault);
			}
			else
			{
				obj = "--";
			}
			string text = (string)obj;
			lastAvatarObservationWorkerDuration = _lastAvatarObservationWorkerStartWait;
			object obj2;
			if (lastAvatarObservationWorkerDuration.HasValue)
			{
				TimeSpan valueOrDefault2 = lastAvatarObservationWorkerDuration.GetValueOrDefault();
				obj2 = FormatWorkerDuration(valueOrDefault2);
			}
			else
			{
				obj2 = "--";
			}
			string text2 = (string)obj2;
			AvatarWorkerTimingText.Text = "Storage/model worker: last " + text + " | wait to next start " + text2;
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
		double num = response.SparseLandmarks.Min((ThreeDdfaOnnxSidecarVertex point) => point.X);
		double num2 = response.SparseLandmarks.Max((ThreeDdfaOnnxSidecarVertex point) => point.X);
		double num3 = response.SparseLandmarks.Min((ThreeDdfaOnnxSidecarVertex point) => point.Y);
		double num4 = response.SparseLandmarks.Max((ThreeDdfaOnnxSidecarVertex point) => point.Y);
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

	private static DecaSidecarFaceBox? CreateDecaFaceBox(FaceFeatureDetection detection)
	{
		if (!detection.HasFace || detection.FaceBox.Width <= 0.0 || detection.FaceBox.Height <= 0.0)
		{
			return null;
		}
		return new DecaSidecarFaceBox
		{
			Left = detection.FaceBox.Left,
			Top = detection.FaceBox.Top,
			Right = detection.FaceBox.Right,
			Bottom = detection.FaceBox.Bottom,
			Normalized = true,
			Confidence = Math.Clamp(detection.TrackingConfidence, 0.01, 1.0)
		};
	}

	private AvatarReconstructionSnapshot? CreateThreeDdfaLastGoodSnapshot(ThreeDdfaOnnxSidecarResponse response, DateTime sampleCapturedAtUtc)
	{
		if (!response.Ok || !response.HasFace || response.DenseVertexCount < 30000 || response.DenseVertices.Count < 30000)
		{
			return null;
		}
		List<MeshTopologyEdge> orCreateThreeDdfaTopology = GetOrCreateThreeDdfaTopology(response.DenseEdges);
		if (orCreateThreeDdfaTopology.Count == 0)
		{
			return null;
		}
		DateTime dateTime = ParseThreeDdfaCapturedAtUtc(response.CapturedAtUtc);
		if (sampleCapturedAtUtc != default(DateTime) && dateTime != default(DateTime) && Math.Abs((dateTime - sampleCapturedAtUtc).TotalMilliseconds) > 1800.0)
		{
			return null;
		}
		double reconstructionConfidencePercent = RoundThreeDdfaValue(response.ReconstructionConfidencePercent);
		return new AvatarReconstructionSnapshot
		{
			BackendId = "3ddfa-v2-onnx-reconstruction",
			RequestId = response.RequestId,
			CapturedAtUtc = ((dateTime == default(DateTime)) ? sampleCapturedAtUtc : dateTime),
			Source = response.Backend,
			DenseVertexCount = response.DenseVertexCount,
			DenseSampleStride = response.DenseSampleStride,
			ReconstructionConfidencePercent = reconstructionConfidencePercent,
			ARotationAroundXDegrees = RoundThreeDdfaValue(response.Pose.ARotationAroundXDegrees),
			BRotationAroundYDegrees = RoundThreeDdfaValue(response.Pose.BRotationAroundYDegrees),
			CRotationAroundZDegrees = RoundThreeDdfaValue(response.Pose.CRotationAroundZDegrees),
			PoseSource = response.Pose.Source,
			TrustDecision = response.TrustDecision,
			Vertices = response.DenseVertices.Select((ThreeDdfaOnnxSidecarVertex vertex) => new FaceMeshLandmarkPoint
			{
				Index = vertex.Index,
				X = RoundThreeDdfaValue(vertex.X),
				Y = RoundThreeDdfaValue(vertex.Y),
				Z = RoundThreeDdfaValue(vertex.Z)
			}).ToList(),
			CanonicalIdentityVertices = response.CanonicalIdentityVertices.Select((ThreeDdfaOnnxSidecarVertex vertex) => new FaceMeshLandmarkPoint
			{
				Index = vertex.Index,
				X = RoundThreeDdfaValue(vertex.X),
				Y = RoundThreeDdfaValue(vertex.Y),
				Z = RoundThreeDdfaValue(vertex.Z)
			}).ToList(),
			TopologyEdges = orCreateThreeDdfaTopology,
			SparseLandmarks = response.SparseLandmarks.Select((ThreeDdfaOnnxSidecarVertex vertex) => new FaceMeshLandmarkPoint
			{
				Index = vertex.Index,
				X = RoundThreeDdfaValue(vertex.X),
				Y = RoundThreeDdfaValue(vertex.Y),
				Z = RoundThreeDdfaValue(vertex.Z)
			}).ToList(),
			CameraMatrixCoefficients = response.CameraMatrixCoefficients.Select(RoundThreeDdfaValue).ToList(),
			ShapeCoefficients = response.ShapeCoefficients.Select(RoundThreeDdfaValue).ToList(),
			ExpressionCoefficients = response.ExpressionCoefficients.Select(RoundThreeDdfaValue).ToList(),
			Warnings = response.Warnings
		};
	}

	private static double RoundThreeDdfaValue(double value)
	{
		if (!double.IsFinite(value))
		{
			return 0.0;
		}
		return Math.Round(value, 6, MidpointRounding.AwayFromZero);
	}

	private static DateTime ParseThreeDdfaCapturedAtUtc(string capturedAtUtc)
	{
		if (!DateTime.TryParse(capturedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var result))
		{
			return default(DateTime);
		}
		return result;
	}

	private bool HasUsableFaceFeatureLock(DateTime now)
	{
		if (_currentFaceFeatureDetection.HasFace)
		{
			return (now - _lastFaceFeatureLockAt).TotalSeconds <= 4.0;
		}
		return false;
	}

	private void ResetLandmarkTracking()
	{
		_currentFaceLandmarkFrame = FaceLandmarkFrame.None;
		_currentFaceLandmarkMetrics = FaceLandmarkMetrics.None;
		_currentFaceLockStabilityAnalysis = FaceLockStabilityAnalysis.Waiting;
		_currentFaceFrameGeometry = FaceFrameGeometry.None;
		_currentAvatarCaptureQuality = AvatarCaptureQualityAssessment.Waiting;
		lock (_faceLandmarkTrackerLock)
		{
			_faceLandmarkTracker?.Reset();
		}
		_faceLandmarkReconstructor.Reset();
		_faceLandmarkMetricCalculator.Reset();
		_faceLockStabilityAnalyzer.Reset();
	}

	private void UpdateAvatarCaptureState(DateTime utcNow)
	{
		if (!IsAvatarUserLoggedIn)
		{
			ResetAvatarCaptureGate("no avatar user logged in; capture stopped");
			UpdateAvatarCaptureQuality();
			UpdateAvatarLearningStatusUi();
			return;
		}
		if (!_avatarLearningRequested && !IsStandardModelBuildModeActive)
		{
			ResetAvatarCaptureGate("avatar capture stopped by user");
			UpdateAvatarCaptureQuality();
			UpdateAvatarLearningStatusUi();
			return;
		}
		ResetAvatarCaptureGate("capture-quality preflight", accepted: true);
		AvatarCaptureQualityAssessment avatarCaptureQualityAssessment = AnalyzeAvatarCaptureQuality();
		if (!avatarCaptureQualityAssessment.CanCollectMeasurements)
		{
			ResetAvatarCaptureGate("capture quality gate: " + avatarCaptureQualityAssessment.PrimaryReason);
			_currentAvatarCaptureQuality = avatarCaptureQualityAssessment;
			UpdateAvatarLearningStatusUi();
		}
		else
		{
			ResetAvatarCaptureGate(IsStandardModelBuildModeActive ? ("human Standard Model builder active; " + GetFaceBoxSystemDisplayName() + " face tracking remains live") : (ActiveAvatarReconstructionName + " avatar capture active; " + GetFaceBoxSystemDisplayName() + " face tracking remains live"), accepted: true);
			_currentAvatarCaptureQuality = avatarCaptureQualityAssessment;
			UpdateAvatarLearningStatusUi();
		}
	}

	private void UpdateAvatarCaptureQuality()
	{
		_currentAvatarCaptureQuality = AnalyzeAvatarCaptureQuality();
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
			AvatarCaptureRequested = _avatarLearningRequested,
			CaptureGateAccepted = _avatarCaptureGateAccepted,
			CaptureGateReason = _avatarCaptureGateReason
		});
	}

	private AvatarReportSnapshot CreateAvatarReportSnapshot(string folder)
	{
		AvatarLearningState avatarLearningState = GetAvatarLearningState();
		bool userLoggedIn = IsAvatarUserLoggedIn && !string.IsNullOrWhiteSpace(CurrentAvatarProfileId);
		return new AvatarReportSnapshot(folder, CurrentAvatarProfileId, CurrentAvatarProfileDisplayName, CloneCaptureQuality(_currentAvatarCaptureQuality), userLoggedIn, _avatarLearningRequested, avatarLearningState.Active, avatarLearningState.Title, avatarLearningState.Detail, _currentFaceFrameGeometry, CreateFaceReconstructionLaneStatus());
	}

	private static AvatarCaptureQualityAssessment CloneCaptureQuality(AvatarCaptureQualityAssessment value)
	{
		return new AvatarCaptureQualityAssessment
		{
			Label = value.Label,
			ScorePercent = value.ScorePercent,
			CanCollectMeasurements = value.CanCollectMeasurements,
			StrongEnoughForAvatarLearning = value.StrongEnoughForAvatarLearning,
			PrimaryReason = value.PrimaryReason,
			StatusLine = value.StatusLine,
			CameraModeScorePercent = value.CameraModeScorePercent,
			FaceScaleScorePercent = value.FaceScaleScorePercent,
			EyeEvidenceScorePercent = value.EyeEvidenceScorePercent,
			MouthEvidenceScorePercent = value.MouthEvidenceScorePercent,
			StabilityScorePercent = value.StabilityScorePercent,
			GlassesRiskScorePercent = value.GlassesRiskScorePercent,
			StorageScorePercent = value.StorageScorePercent,
			FaceWidthPercent = value.FaceWidthPercent,
			FaceHeightPercent = value.FaceHeightPercent,
			Issues = value.Issues.ToList(),
			Suggestions = value.Suggestions.ToList()
		};
	}

	private void QueueAvatarReportSave(AvatarReportSnapshot snapshot)
	{
		lock (_personalFaceReportWriterLock)
		{
			_pendingAvatarReportSnapshot = snapshot;
			Task avatarReportWriterTask = _avatarReportWriterTask;
			if ((avatarReportWriterTask == null || avatarReportWriterTask.IsCompleted) && !_avatarLearningRequested && !IsStandardModelBuildModeActive && !_avatarObservationStorageService.IsBusy)
			{
				_avatarReportWriterTask = Task.Run((Action)ProcessAvatarReportWriterQueue);
			}
		}
	}

	private void StartPendingAvatarReportWriterIfPossible()
	{
		lock (_personalFaceReportWriterLock)
		{
			if ((object)_pendingAvatarReportSnapshot != null)
			{
				Task avatarReportWriterTask = _avatarReportWriterTask;
				if ((avatarReportWriterTask == null || avatarReportWriterTask.IsCompleted) && !_avatarLearningRequested && !IsStandardModelBuildModeActive && !_avatarObservationStorageService.IsBusy)
				{
					_avatarReportWriterTask = Task.Run((Action)ProcessAvatarReportWriterQueue);
				}
			}
		}
	}

	private void ProcessAvatarReportWriterQueue()
	{
		while (true)
		{
			AvatarReportSnapshot pendingAvatarReportSnapshot;
			lock (_personalFaceReportWriterLock)
			{
				pendingAvatarReportSnapshot = _pendingAvatarReportSnapshot;
				_pendingAvatarReportSnapshot = null;
				if ((object)pendingAvatarReportSnapshot == null)
				{
					_avatarReportWriterTask = null;
					break;
				}
			}
			try
			{
				AvatarReportSaveResult result = WriteAvatarReports(pendingAvatarReportSnapshot);
				base.Dispatcher.BeginInvoke((Action)delegate
				{
					ApplyAvatarReportSaveResult(result);
				}, DispatcherPriority.Background);
			}
			catch (Exception ex)
			{
				Exception ex2 = ex;
				Exception ex3 = ex2;
				base.Dispatcher.BeginInvoke((Action)delegate
				{
					if (!_isClosing)
					{
						SetStatus("Live Avatar System save paused: " + ex3.Message);
					}
				}, DispatcherPriority.Background);
			}
		}
	}

	private AvatarReportSaveResult WriteAvatarReports(AvatarReportSnapshot snapshot, AvatarObservationBatch? acceptedBatch = null, bool forceFullModelRebuild = false)
	{
		lock (_avatarReportStorageLock)
		{
			return WriteAvatarReportsCore(snapshot, acceptedBatch, forceFullModelRebuild);
		}
	}

	private AvatarReportSaveResult WriteAvatarReportsCore(AvatarReportSnapshot snapshot, AvatarObservationBatch? acceptedBatch, bool forceFullModelRebuild)
	{
		Directory.CreateDirectory(snapshot.Folder);
		AvatarObservationDataset avatarObservationDataset = _avatarObservationRepository.ReadDataset(snapshot.Folder, snapshot.SubjectId, snapshot.SubjectDisplayName, includeDenseTopology: true, ActiveAvatarReconstructionBackendId);
		AvatarModel avatarModel = _avatarModelStore.Read(snapshot.Folder);
		bool flag = avatarModel?.SourceObservationRevision != avatarObservationDataset.Revision;
		AvatarModel avatarModel2 = avatarModel;
		bool flag2 = avatarModel2 != null && string.Equals(avatarModel2.SchemaVersion, "avatar-model-v9-multiframe-identity-mapping", StringComparison.Ordinal);
		AvatarModelHistoryReport avatarModelHistoryReport;
		if (forceFullModelRebuild)
		{
			avatarModel2 = AvatarModelBuilder.Build(avatarObservationDataset, _avatarObservationRepository);
			if (flag2 && avatarModel.Identity.MappedDenseVertices.Count > 0)
			{
				avatarModel2 = AvatarModelBuilder.ApplyIdentityMapping(avatarModel2, CreatePreservedIdentityMapping(avatarModel.Identity));
			}
			avatarModel2 = ApplyLatestRecurrentDecaIdentity(avatarModel2, avatarObservationDataset, acceptedBatch);
			AvatarModel previousModel = (flag2 ? avatarModel : null);
			avatarModelHistoryReport = _avatarModelHistoryStore.RecordAndWrite(snapshot.Folder, avatarObservationDataset, _avatarObservationRepository, avatarModel2, previousModel);
			_avatarModelStore.Write(snapshot.Folder, avatarModel2);
		}
		else if (flag && (object)acceptedBatch != null)
		{
			AvatarModel previousModel2 = (flag2 ? avatarModel : AvatarModelBuilder.CreateWaiting(avatarObservationDataset));
			avatarModel2 = AvatarModelBuilder.UpdateIncrementally(avatarObservationDataset, _avatarObservationRepository, previousModel2, acceptedBatch.Results);
			avatarModel2 = ApplyLatestRecurrentDecaIdentity(avatarModel2, avatarObservationDataset, acceptedBatch);
			List<AvatarObservation> observationsToAudit = (from result in acceptedBatch.Results
				where (object)result.AcceptedObservation != null
				select result.AcceptedObservation).ToList();
			avatarModelHistoryReport = _avatarModelHistoryStore.RecordAndWrite(snapshot.Folder, avatarObservationDataset, _avatarObservationRepository, avatarModel2, flag2 ? avatarModel : null, observationsToAudit);
			_avatarModelStore.Write(snapshot.Folder, avatarModel2);
		}
		else if (!flag2)
		{
			avatarModel2 = AvatarModelBuilder.CreateWaiting(avatarObservationDataset);
			avatarModelHistoryReport = _avatarModelHistoryStore.ReadReport(snapshot.Folder);
			_avatarModelStore.Write(snapshot.Folder, avatarModel2);
		}
		else if ((object)acceptedBatch != null)
		{
			avatarModel2 = ApplyLatestRecurrentDecaIdentity(avatarModel, avatarObservationDataset, acceptedBatch);
			avatarModelHistoryReport = _avatarModelHistoryStore.RecordAndWrite(snapshot.Folder, avatarObservationDataset, _avatarObservationRepository, avatarModel2, avatarModel, Array.Empty<AvatarObservation>());
			_avatarModelStore.Write(snapshot.Folder, avatarModel2);
		}
		else
		{
			avatarModel2 = avatarModel;
			avatarModelHistoryReport = _avatarModelHistoryStore.ReadReport(snapshot.Folder);
			_avatarModelStore.EnsureViewer(snapshot.Folder, avatarModel2);
		}
		AvatarSystemDashboard dashboard = new AvatarSystemDashboard
		{
			SubjectId = snapshot.SubjectId,
			SubjectDisplayName = snapshot.SubjectDisplayName,
			UserLoggedIn = snapshot.UserLoggedIn,
			AvatarCaptureRequested = snapshot.AvatarCaptureRequested,
			AvatarCaptureActive = snapshot.AvatarCaptureActive,
			AvatarCaptureStatus = snapshot.AvatarCaptureStatus,
			AvatarCaptureCorrection = snapshot.AvatarCaptureCorrection,
			CurrentCaptureQuality = snapshot.CaptureQuality,
			CurrentFaceFrameGeometry = snapshot.FaceFrameGeometry,
			ReconstructionLane = snapshot.ReconstructionLane,
			FastTrackingSummary = snapshot.ReconstructionLane.FastTrackingLaneName + " supplies live face, eye, jaw, brow, mouth, overlay, and capture measurements.",
			AvatarModelStatus = avatarModel2.Status,
			RetainedAvatarObservationCount = avatarObservationDataset.Observations.Count,
			StorageRevision = avatarObservationDataset.Revision,
			LifetimeAcceptedObservationCount = avatarObservationDataset.AcceptedObservationCount,
			LifetimeRejectedObservationCount = avatarObservationDataset.RejectedObservationCount,
			AvatarModelConfidencePercent = avatarModel2.Identity.ConfidencePercent,
			AvatarModelCoveragePercent = avatarModel2.PoseCoverage.CoveragePercent,
			AvatarModelCoverageSummary = avatarModel2.PoseCoverage.Summary,
			AvatarModelConvergencePercent = avatarModel2.Convergence.ScorePercent,
			AvatarModelConvergenceLabel = avatarModel2.Convergence.Label,
			AvatarIdentityMappingStatus = avatarModel2.Identity.MappingStatus,
			AvatarIdentityMappingLandmarkRmsePercent = avatarModel2.Identity.MappingFinalLandmarkRmsePercent,
			AvatarIdentityMappingImprovementPercent = avatarModel2.Identity.MappingImprovementPercent,
			AvatarModelHtmlPath = AvatarModelStore.GetHtmlPath(snapshot.Folder),
			AvatarModelAuditStatus = avatarModelHistoryReport.Latest.Status,
			AvatarModelAuditSummary = avatarModelHistoryReport.Latest.Summary,
			AvatarModelAuditHtmlPath = AvatarModelHistoryStore.GetHtmlPath(snapshot.Folder)
		};
		return new AvatarReportSaveResult(AvatarSystemDashboardStore.GetHtmlPath(_avatarSystemDashboardStore.Write(snapshot.Folder, dashboard)), AvatarModelStore.GetHtmlPath(snapshot.Folder));
	}

	private AvatarModel ApplyLatestRecurrentDecaIdentity(AvatarModel model, AvatarObservationDataset observationSet, AvatarObservationBatch? acceptedBatch)
	{
		if (!IsDecaAvatarReconstructionActive || !string.Equals(ActiveAvatarReconstructionBackendId, "deca-flame-recurrent-v4", StringComparison.Ordinal))
		{
			return model;
		}
		AvatarReconstructionSnapshot avatarReconstructionSnapshot = (from capture in acceptedBatch?.Candidates
			where string.Equals(capture.Reconstruction.BackendId, "deca-flame-recurrent-v4", StringComparison.Ordinal)
			select capture.Reconstruction into reconstruction
			where reconstruction.ShapeCoefficients.Count > 0 && reconstruction.CanonicalIdentityVertices.Count >= 1000
			select reconstruction).MaxBy((AvatarReconstructionSnapshot reconstruction) => reconstruction.CapturedAtUtc);
		IReadOnlyList<double> shapeCoefficients;
		IReadOnlyList<FaceMeshLandmarkPoint> canonicalIdentityVertices;
		DateTime capturedAtUtc;
		if (avatarReconstructionSnapshot != null)
		{
			shapeCoefficients = avatarReconstructionSnapshot.ShapeCoefficients;
			canonicalIdentityVertices = avatarReconstructionSnapshot.CanonicalIdentityVertices;
			capturedAtUtc = avatarReconstructionSnapshot.CapturedAtUtc;
		}
		else
		{
			AvatarObservation avatarObservation = observationSet.Observations.Where((AvatarObservation observation) => string.Equals(observation.BackendId, "deca-flame-recurrent-v4", StringComparison.Ordinal)).MaxBy((AvatarObservation observation) => observation.CapturedAtUtc);
			if ((object)avatarObservation == null)
			{
				return model;
			}
			try
			{
				AvatarObservation avatarObservation2 = _avatarObservationRepository.LoadObservation(observationSet, avatarObservation);
				shapeCoefficients = avatarObservation2.ShapeCoefficients;
				canonicalIdentityVertices = avatarObservation2.CanonicalIdentityVertices;
				capturedAtUtc = avatarObservation2.CapturedAtUtc;
			}
			catch (Exception ex) when (((ex is IOException || ex is InvalidDataException) ? 1 : 0) != 0)
			{
				return model;
			}
		}
		if (shapeCoefficients.Count == 0 || canonicalIdentityVertices.Count < 1000)
		{
			return model;
		}
		return AvatarModelBuilder.ApplyIdentityMapping(model, new AvatarIdentityMappingUpdate
		{
			Accepted = true,
			Status = $"Recurrent DECA/FLAME model from {capturedAtUtc.ToLocalTime():g}; model[n-1] was the exact input seed and no coefficient averaging was applied.",
			UpdatedAtUtc = capturedAtUtc,
			FrameCount = 1,
			IterationCount = 0,
			ShapeCoefficients = shapeCoefficients,
			CanonicalIdentityVertices = canonicalIdentityVertices
		});
	}

	private static AvatarIdentityMappingUpdate CreatePreservedIdentityMapping(AvatarIdentityModel identity)
	{
		return new AvatarIdentityMappingUpdate
		{
			Accepted = true,
			Status = identity.MappingStatus,
			UpdatedAtUtc = (identity.MappingUpdatedAtUtc ?? DateTime.UtcNow),
			FrameCount = identity.MappingFrameCount,
			IterationCount = identity.MappingIterationCount,
			InitialLandmarkRmsePercent = identity.MappingInitialLandmarkRmsePercent,
			FinalLandmarkRmsePercent = identity.MappingFinalLandmarkRmsePercent,
			ImprovementPercent = identity.MappingImprovementPercent,
			GenericIdentityDisplacementPercent = identity.GenericIdentityDisplacementPercent,
			ShapeCoefficients = identity.MappedShapeCoefficients,
			CanonicalIdentityVertices = identity.MappedDenseVertices
		};
	}

	private void ApplyAvatarReportSaveResult(AvatarReportSaveResult result)
	{
		if (!_isClosing)
		{
			_avatarSystemDashboardPath = result.AvatarSystemDashboardPath;
			_avatarModelHtmlPath = result.AvatarModelHtmlPath;
			UpdateAvatarLearningStatusUi();
		}
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

	private static void OpenWebAddress(Uri address)
	{
		Process.Start(new ProcessStartInfo
		{
			FileName = address.AbsoluteUri,
			UseShellExecute = true
		});
	}

	private static string GetAvatarSystemDashboardHtmlPath(string folder)
	{
		return AvatarSystemDashboardStore.GetHtmlPath(System.IO.Path.Combine(folder, "avatar_system.json"));
	}

	private static void EnsureAvatarSystemPlaceholder(string path)
	{
		if (!File.Exists(path))
		{
			string contents = "<!doctype html>\r\n<html lang=\"en\">\r\n<head>\r\n<meta charset=\"utf-8\">\r\n<meta http-equiv=\"refresh\" content=\"2\">\r\n<title>Avatar System</title>\r\n<style>\r\n:root{color-scheme:dark}body{margin:0;background:#080d12;color:#f5f8fb;font-family:Segoe UI,Arial,sans-serif}main{max-width:860px;margin:0 auto;padding:28px}section{border:1px solid #243545;background:#101820;padding:18px}.muted{color:#b9d7ef}\r\n</style>\r\n</head>\r\n<body>\r\n<main>\r\n<section>\r\n<h1>Avatar System</h1>\r\n<p class=\"muted\">Preparing the live report. This page refreshes automatically while the background writer saves the latest measurement snapshot.</p>\r\n</section>\r\n</main>\r\n</body>\r\n</html>";
			AtomicTextFileWriter.WriteAllText(path, contents, Encoding.UTF8);
		}
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
		_visionBenchmarkRecorder.SetOutputRoot(_outputFolder);
		SaveOutputFolderPointer(_outputFolder);
		InitializeAvatarProfiles(promptForStartupUser: false);
		_poseAlignmentAuditor.SetOutputRoot(GetAvatarDataFolder());
		_mediaPipeConvergenceAuditor.SetOutputRoot(GetAvatarDataFolder());
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

	private void OpenPoseAlignmentAuditClicked(object sender, RoutedEventArgs e)
	{
		try
		{
			_poseAlignmentAuditor.EnsureReport();
			string htmlPath = _poseAlignmentAuditor.GetHtmlPath();
			if (string.IsNullOrWhiteSpace(htmlPath))
			{
				SetStatus("A/B/C alignment audit is waiting for a configured data folder.");
				return;
			}
			OpenLocalFile(htmlPath);
			SetStatus("Opened A/B/C alignment audit: " + htmlPath);
		}
		catch (Exception ex)
		{
			SetStatus("Could not open A/B/C alignment audit: " + ex.Message);
		}
	}

	private async void OpenMediaPipeConvergenceAuditClicked(object sender, RoutedEventArgs e)
	{
		try
		{
			await Task.Run((Action)_mediaPipeConvergenceAuditor.EnsureReport);
			string htmlPath = _mediaPipeConvergenceAuditor.GetHtmlPath();
			if (string.IsNullOrWhiteSpace(htmlPath))
			{
				SetStatus("MediaPipe convergence audit is waiting for a configured data folder.");
				return;
			}
			OpenLocalFile(htmlPath);
			SetStatus("Opened MediaPipe convergence audit: " + htmlPath);
		}
		catch (Exception ex)
		{
			SetStatus("Could not open MediaPipe convergence audit: " + ex.Message);
		}
	}

	private async void StartNewMediaPipeConvergenceAuditClicked(object sender, RoutedEventArgs e)
	{
		try
		{
			await Task.Run(delegate
			{
				_mediaPipeConvergenceAuditor.StartNewSession("human-started controlled run");
			});
			SetStatus("Started a clean MediaPipe convergence audit. Tracker and Avatar Builder temporal state were preserved.");
		}
		catch (Exception ex)
		{
			SetStatus("Could not start a new MediaPipe convergence audit: " + ex.Message);
		}
	}

	private void MarkMediaPipeConvergenceObservationClicked(object sender, RoutedEventArgs e)
	{
		_mediaPipeConvergenceAuditor.MarkEvent("Human observation", "User marked visible tracking behavior for later comparison.");
		SetStatus("Marked the current moment in the MediaPipe convergence audit.");
	}

	private async void ResetMediaPipeVideoTrackerClicked(object sender, RoutedEventArgs e)
	{
		if (_selectedFaceBoxSystem != FaceBoxSystem.MediaPipe)
		{
			SetStatus("Select MediaPipe as the Face Box System before resetting its VIDEO tracker.");
			return;
		}
		_mediaPipeConvergenceAuditor.MarkEvent("MediaPipe tracker reset started", "Avatar Builder temporal reconstruction was intentionally preserved.");
		SetStatus("Resetting only the MediaPipe VIDEO tracker...");
		try
		{
			await Task.Run(delegate
			{
				lock (_faceLandmarkTrackerLock)
				{
					_faceLandmarkTracker?.Reset();
				}
			});
			_mediaPipeConvergenceAuditor.MarkEvent("MediaPipe tracker reset completed", "The next frame starts a fresh MediaPipe VIDEO tracking session.");
			SetStatus("MediaPipe VIDEO tracker reset. Avatar Builder temporal reconstruction was preserved.");
		}
		catch (Exception ex)
		{
			_mediaPipeConvergenceAuditor.MarkEvent("MediaPipe tracker reset failed", ex.Message);
			SetStatus("Could not reset the MediaPipe VIDEO tracker: " + ex.Message);
		}
	}

	private void ResetAvatarTemporalLayerClicked(object sender, RoutedEventArgs e)
	{
		_mediaPipeConvergenceAuditor.MarkEvent("Avatar Builder temporal reset", "MediaPipe VIDEO tracker was intentionally preserved.");
		_faceLandmarkReconstructor.Reset();
		_faceLandmarkMetricCalculator.Reset();
		_faceLockStabilityAnalyzer.Reset();
		SetStatus("Avatar Builder temporal reconstruction and metric history reset. MediaPipe VIDEO tracker was preserved.");
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
			ResetAvatarCaptureGate("avatar capture folder ready; " + ActiveAvatarReconstructionName + " owns avatar reconstruction");
			_avatarSystemDashboardPath = GetAvatarSystemDashboardHtmlPath(avatarDataFolder);
			_avatarModelHtmlPath = AvatarModelStore.GetHtmlPath(avatarDataFolder);
			QueueAvatarReportSave(CreateAvatarReportSnapshot(avatarDataFolder));
			UpdateAvatarCaptureQuality();
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
		TopStatusText.Text = status;
		FooterText.Text = status;
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
		IReadOnlyDictionary<int, LiveWireframeProjectedPoint> readOnlyDictionary = BuildCameraRelativeLiveWireframeProjection(frame.DenseMeshPoints, rect);
		SolidColorBrush brush = CreateFrozenBrush(47, 108, 143, 88);
		(int, int)[] tessellationEdges = MediaPipeFaceMeshTopology.TessellationEdges;
		for (int i = 0; i < tessellationEdges.Length; i++)
		{
			var (fromIndex, toIndex) = tessellationEdges[i];
			DrawWireframeEdge(fromIndex, toIndex, readOnlyDictionary, brush, 0.42);
		}
		PreviewOverlayIndexedPath[] array = CreateMediaPipePreviewMeshFeaturePaths(frame.DenseMeshPoints);
		foreach (PreviewOverlayIndexedPath previewOverlayIndexedPath in array)
		{
			DrawWireframePath(previewOverlayIndexedPath.PointIndices, previewOverlayIndexedPath.Closed, readOnlyDictionary, RoleName(previewOverlayIndexedPath.Role));
		}
		SolidColorBrush fill = CreateFrozenBrush(220, 239, byte.MaxValue, 184);
		foreach (FaceMeshLandmarkPoint denseMeshPoint in frame.DenseMeshPoints)
		{
			if (readOnlyDictionary.TryGetValue(denseMeshPoint.Index, out var value))
			{
				double num = (((uint)denseMeshPoint.Index < (uint)MediaPipePreviewMeshFeaturePointMask.Length && MediaPipePreviewMeshFeaturePointMask[denseMeshPoint.Index]) ? 3.2 : 2.0);
				Ellipse element = new Ellipse
				{
					Width = num,
					Height = num,
					Fill = fill,
					IsHitTestVisible = false
				};
				Canvas.SetLeft(element, value.X - num / 2.0);
				Canvas.SetTop(element, value.Y - num / 2.0);
				LiveWireframeCanvas.Children.Add(element);
			}
		}
		AvatarReconstructionSnapshot currentAvatarReconstructionSnapshot = _currentAvatarReconstructionSnapshot;
		string value2 = ((currentAvatarReconstructionSnapshot != null) ? $"{currentAvatarReconstructionSnapshot.Source} A/B/C {currentAvatarReconstructionSnapshot.ARotationAroundXDegrees:0.#}/{currentAvatarReconstructionSnapshot.BRotationAroundYDegrees:0.#}/{currentAvatarReconstructionSnapshot.CRotationAroundZDegrees:0.#} deg" : (ActiveAvatarReconstructionName + " pose waiting"));
		AddWireframeText($"{title}: {frame.DenseMeshPoints.Count} points, {MediaPipeFaceMeshTopology.TessellationEdges.Length} surface edges", $"Camera-relative MediaPipe wireframe. Quality {metrics.OverallMeasurementQualityPercent:0}% | eyes {metrics.EyeMeasurementQualityPercent:0}% | brows {metrics.BrowMeasurementQualityPercent:0}% ({FormatRatioPercent(metrics.AverageBrowHeightRatio)}) | mouth {metrics.MouthMeasurementQualityPercent:0}% | {value2}", rect.X + 18.0, rect.Y + 18.0);
	}

	private void DrawWireframeEdge(int fromIndex, int toIndex, IReadOnlyDictionary<int, LiveWireframeProjectedPoint> points, Brush brush, double thickness)
	{
		if (points.TryGetValue(fromIndex, out LiveWireframeProjectedPoint value) && points.TryGetValue(toIndex, out LiveWireframeProjectedPoint value2))
		{
			Line element = new Line
			{
				X1 = value.X,
				Y1 = value.Y,
				X2 = value2.X,
				Y2 = value2.Y,
				Stroke = brush,
				StrokeThickness = thickness,
				IsHitTestVisible = false
			};
			LiveWireframeCanvas.Children.Add(element);
		}
	}

	private void DrawWireframePath(IReadOnlyList<int> indices, bool closed, IReadOnlyDictionary<int, LiveWireframeProjectedPoint> points, string role)
	{
		SolidColorBrush brush = BrushForWireframeRole(role);
		for (int i = 1; i < indices.Count; i++)
		{
			DrawWireframeEdge(indices[i - 1], indices[i], points, brush, 1.75);
		}
		if (closed && indices.Count > 2)
		{
			DrawWireframeEdge(indices[indices.Count - 1], indices[0], points, brush, 1.75);
		}
	}

	private static IReadOnlyDictionary<int, LiveWireframeProjectedPoint> BuildCameraRelativeLiveWireframeProjection(IReadOnlyList<FaceMeshLandmarkPoint> denseMeshPoints, Rect rect)
	{
		return denseMeshPoints.ToDictionary((FaceMeshLandmarkPoint point) => point.Index, (FaceMeshLandmarkPoint point) => new LiveWireframeProjectedPoint(point.Index, rect.X + Math.Clamp(point.X, 0.0, 1.0) * rect.Width, rect.Y + Math.Clamp(point.Y, 0.0, 1.0) * rect.Height, point.Z));
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

	private static SolidColorBrush BrushForWireframeRole(string role)
	{
		switch (role)
		{
		case "eye":
			return CreateFrozenBrush(143, 242, 197, 242);
		case "brow":
			return CreateFrozenBrush(201, 247, 163, 242);
		case "mouth":
		case "mouth-opening":
			return CreateFrozenBrush(byte.MaxValue, 159, 189, 242);
		case "jaw":
			return CreateFrozenBrush(byte.MaxValue, 209, 102, 242);
		case "nose":
			return CreateFrozenBrush(217, 232, byte.MaxValue, 242);
		case "cheek":
			return CreateFrozenBrush(199, 166, byte.MaxValue, 242);
		case "forehead":
			return CreateFrozenBrush(157, 183, 201, 242);
		case "face":
			return CreateFrozenBrush(101, 200, byte.MaxValue, 242);
		default:
			return CreateFrozenBrush(220, 239, byte.MaxValue, 224);
		}
	}

	private void UpdateFaceCueGuideOverlay(BitmapSource? bitmap)
	{
		FaceCueGuideCanvas.Children.Clear();
		if (_showLiveWireframePreview)
		{
			UpdateDirectX12TrackingOverlay(PreviewTrackingOverlay.Empty);
			FaceCueGuideCanvas.Visibility = Visibility.Collapsed;
			return;
		}
		if (IsDirectX12PreviewSurfaceActive())
		{
			UpdateDirectX12TrackingOverlay(CreateNativePreviewTrackingOverlay());
			FaceCueGuideCanvas.Visibility = Visibility.Collapsed;
			return;
		}
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
		if (_showAvatarModelOverlay)
		{
			AddAvatarModelOverlay(previewDisplayRect, _currentAvatarReconstructionSnapshot);
			return;
		}
		AddFaceMeshOverlay(previewDisplayRect, _currentFaceLandmarkFrame);
		if (!_showFaceMeshOverlay)
		{
			AddGuideRegion(previewDisplayRect, faceBox, Brushes.Transparent, stroke2, 1.0);
			AddGuideRegion(previewDisplayRect, frameRegion, fill, stroke3, 2.0);
			AddGuideLine(previewDisplayRect, frameRegion.Left + frameRegion.Width * 0.16, frameRegion.Top + frameRegion.Height * 0.38, frameRegion.Right - frameRegion.Width * 0.16, frameRegion.Top + frameRegion.Height * 0.38, stroke, 3.0);
			AddGuideLine(previewDisplayRect, faceBox.Left + faceBox.Width * 0.5, faceBox.Top, faceBox.Left + faceBox.Width * 0.5, faceBox.Bottom, stroke2, 1.0);
			if (HasUsableFaceFeatureLock(DateTime.UtcNow))
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
		if (!HasUsableFaceFeatureLock(DateTime.UtcNow))
		{
			return PreviewTrackingOverlay.Empty;
		}
		FaceFeatureDetection currentFaceFeatureDetection = _currentFaceFeatureDetection;
		FaceLandmarkFrame currentFaceLandmarkFrame = _currentFaceLandmarkFrame;
		AvatarReconstructionSnapshot currentAvatarReconstructionSnapshot = _currentAvatarReconstructionSnapshot;
		if (currentFaceFeatureDetection == _cachedNativeOverlayFeatureDetection && currentFaceLandmarkFrame == _cachedNativeOverlayLandmarkFrame && currentAvatarReconstructionSnapshot == _cachedNativeOverlayAvatarSnapshot && _cachedNativeOverlayIncludesFaceMesh == _showFaceMeshOverlay && _cachedNativeOverlayIncludesAvatarModel == _showAvatarModelOverlay)
		{
			return _cachedNativeTrackingOverlay;
		}
		PreviewTrackingOverlay previewTrackingOverlay;
		if (_showAvatarModelOverlay)
		{
			PreviewOverlayMesh previewOverlayMesh = CreateAvatarModelPreviewOverlayMesh(currentAvatarReconstructionSnapshot);
			if ((object)previewOverlayMesh != null)
			{
				previewTrackingOverlay = new PreviewTrackingOverlay
				{
					FaceMesh = previewOverlayMesh
				};
				goto IL_0199;
			}
		}
		if (_showFaceMeshOverlay)
		{
			previewTrackingOverlay = new PreviewTrackingOverlay
			{
				FaceMesh = CreatePreviewOverlayMesh(currentFaceLandmarkFrame.DenseMeshPoints)
			};
		}
		else
		{
			bool eyeArtifactSuppressed = currentFaceLandmarkFrame.EyeArtifactSuppressed;
			IReadOnlyList<Point> points = CreateBrowDisplayOutline(currentFaceLandmarkFrame.LeftBrowContour);
			IReadOnlyList<Point> points2 = CreateBrowDisplayOutline(currentFaceLandmarkFrame.RightBrowContour);
			previewTrackingOverlay = new PreviewTrackingOverlay
			{
				FaceBox = ToPreviewOverlayRect(currentFaceFeatureDetection.FaceBox),
				FaceContour = ToPreviewOverlayPolyline(currentFaceLandmarkFrame.FaceContour, closed: true),
				JawContour = ToPreviewOverlayPolyline(currentFaceLandmarkFrame.JawContour, closed: false),
				LeftEyeContour = ToPreviewOverlayPolyline(currentFaceLandmarkFrame.LeftEyeContour, closed: true, currentFaceLandmarkFrame.LeftEyeReconstructed || eyeArtifactSuppressed),
				RightEyeContour = ToPreviewOverlayPolyline(currentFaceLandmarkFrame.RightEyeContour, closed: true, currentFaceLandmarkFrame.RightEyeReconstructed || eyeArtifactSuppressed),
				LeftBrowContour = ToPreviewOverlayPolyline(points, closed: true),
				RightBrowContour = ToPreviewOverlayPolyline(points2, closed: true),
				OuterLipContour = ToPreviewOverlayPolyline(currentFaceLandmarkFrame.OuterLipContour, closed: true, currentFaceLandmarkFrame.MouthReconstructed),
				InnerLipContour = ToPreviewOverlayPolyline(currentFaceLandmarkFrame.InnerLipContour, closed: true, currentFaceLandmarkFrame.MouthReconstructed)
			};
		}
		goto IL_0199;
		IL_0199:
		_cachedNativeOverlayFeatureDetection = currentFaceFeatureDetection;
		_cachedNativeOverlayLandmarkFrame = currentFaceLandmarkFrame;
		_cachedNativeOverlayAvatarSnapshot = currentAvatarReconstructionSnapshot;
		_cachedNativeOverlayIncludesFaceMesh = _showFaceMeshOverlay;
		_cachedNativeOverlayIncludesAvatarModel = _showAvatarModelOverlay;
		_cachedNativeTrackingOverlay = previewTrackingOverlay;
		return previewTrackingOverlay;
	}

	private static PreviewOverlayMesh? CreateAvatarModelPreviewOverlayMesh(AvatarReconstructionSnapshot? snapshot)
	{
		if (snapshot == null || snapshot.SourceFrameWidthPixels <= 0 || snapshot.SourceFrameHeightPixels <= 0 || snapshot.AlignedIdentityVertices.Count < 1000 || snapshot.TopologyEdges.Count == 0)
		{
			return null;
		}
		int pointCount = snapshot.AlignedIdentityVertices.Max((FaceMeshLandmarkPoint point) => point.Index) + 1;
		PreviewOverlayPoint[] array = new PreviewOverlayPoint[pointCount];
		Array.Fill(array, new PreviewOverlayPoint(double.NaN, double.NaN));
		foreach (FaceMeshLandmarkPoint alignedIdentityVertex in snapshot.AlignedIdentityVertices)
		{
			if ((uint)alignedIdentityVertex.Index < (uint)array.Length && double.IsFinite(alignedIdentityVertex.X) && double.IsFinite(alignedIdentityVertex.Y))
			{
				array[alignedIdentityVertex.Index] = new PreviewOverlayPoint(alignedIdentityVertex.X / (double)snapshot.SourceFrameWidthPixels, alignedIdentityVertex.Y / (double)snapshot.SourceFrameHeightPixels).Clamp();
			}
		}
		PreviewOverlayEdge[] array2 = (from edge in snapshot.TopologyEdges
			where (uint)edge.FromIndex < (uint)pointCount && (uint)edge.ToIndex < (uint)pointCount
			select new PreviewOverlayEdge(edge.FromIndex, edge.ToIndex)).ToArray();
		if (array2.Length != 0)
		{
			return new PreviewOverlayMesh(array, array2, Array.Empty<PreviewOverlayIndexedPath>(), Array.Empty<bool>(), DrawPoints: false);
		}
		return null;
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

	private void AddAvatarModelOverlay(Rect display, AvatarReconstructionSnapshot? snapshot)
	{
		if (snapshot == null || snapshot.SourceFrameWidthPixels <= 0 || snapshot.SourceFrameHeightPixels <= 0 || snapshot.AlignedIdentityVertices.Count < 1000 || snapshot.TopologyEdges.Count == 0)
		{
			return;
		}
		int num = snapshot.AlignedIdentityVertices.Max((FaceMeshLandmarkPoint point) => point.Index) + 1;
		Point[] array = new Point[num];
		bool[] array2 = new bool[num];
		foreach (FaceMeshLandmarkPoint alignedIdentityVertex in snapshot.AlignedIdentityVertices)
		{
			if ((uint)alignedIdentityVertex.Index < (uint)num && double.IsFinite(alignedIdentityVertex.X) && double.IsFinite(alignedIdentityVertex.Y))
			{
				array[alignedIdentityVertex.Index] = new Point(display.Left + Math.Clamp(alignedIdentityVertex.X / (double)snapshot.SourceFrameWidthPixels, 0.0, 1.0) * display.Width, display.Top + Math.Clamp(alignedIdentityVertex.Y / (double)snapshot.SourceFrameHeightPixels, 0.0, 1.0) * display.Height);
				array2[alignedIdentityVertex.Index] = true;
			}
		}
		StreamGeometry streamGeometry = new StreamGeometry();
		using (StreamGeometryContext streamGeometryContext = streamGeometry.Open())
		{
			foreach (MeshTopologyEdge topologyEdge in snapshot.TopologyEdges)
			{
				if ((uint)topologyEdge.FromIndex < (uint)num && (uint)topologyEdge.ToIndex < (uint)num && array2[topologyEdge.FromIndex] && array2[topologyEdge.ToIndex])
				{
					streamGeometryContext.BeginFigure(array[topologyEdge.FromIndex], isFilled: false, isClosed: false);
					streamGeometryContext.LineTo(array[topologyEdge.ToIndex], isStroked: true, isSmoothJoin: false);
				}
			}
		}
		streamGeometry.Freeze();
		FaceCueGuideCanvas.Children.Add(new System.Windows.Shapes.Path
		{
			Data = streamGeometry,
			Stroke = FaceMeshOverlayBrush,
			StrokeThickness = 0.42,
			IsHitTestVisible = false
		});
	}

	private void AddFaceMeshOverlay(Rect display, FaceLandmarkFrame frame)
	{
		if (!_showFaceMeshOverlay || !frame.HasDenseMesh)
		{
			return;
		}
		int count = frame.DenseMeshPoints.Count;
		Point[] array = new Point[count];
		bool[] array2 = new bool[count];
		foreach (FaceMeshLandmarkPoint denseMeshPoint in frame.DenseMeshPoints)
		{
			if ((uint)denseMeshPoint.Index < (uint)count && double.IsFinite(denseMeshPoint.X) && double.IsFinite(denseMeshPoint.Y))
			{
				array[denseMeshPoint.Index] = new Point(display.Left + Math.Clamp(denseMeshPoint.X, 0.0, 1.0) * display.Width, display.Top + Math.Clamp(denseMeshPoint.Y, 0.0, 1.0) * display.Height);
				array2[denseMeshPoint.Index] = true;
			}
		}
		StreamGeometry streamGeometry = new StreamGeometry();
		using (StreamGeometryContext streamGeometryContext = streamGeometry.Open())
		{
			PreviewOverlayEdge[] mediaPipePreviewMeshEdges = MediaPipePreviewMeshEdges;
			for (int i = 0; i < mediaPipePreviewMeshEdges.Length; i++)
			{
				PreviewOverlayEdge previewOverlayEdge = mediaPipePreviewMeshEdges[i];
				if ((uint)previewOverlayEdge.FromIndex < (uint)count && (uint)previewOverlayEdge.ToIndex < (uint)count && array2[previewOverlayEdge.FromIndex] && array2[previewOverlayEdge.ToIndex])
				{
					streamGeometryContext.BeginFigure(array[previewOverlayEdge.FromIndex], isFilled: false, isClosed: false);
					streamGeometryContext.LineTo(array[previewOverlayEdge.ToIndex], isStroked: true, isSmoothJoin: false);
				}
			}
		}
		streamGeometry.Freeze();
		FaceCueGuideCanvas.Children.Add(new System.Windows.Shapes.Path
		{
			Data = streamGeometry,
			Stroke = FaceMeshOverlayBrush,
			StrokeThickness = 0.5,
			IsHitTestVisible = false
		});
		PreviewOverlayIndexedPath[] array3 = CreateMediaPipePreviewMeshFeaturePaths(frame.DenseMeshPoints);
		foreach (PreviewOverlayIndexedPath previewOverlayIndexedPath in array3)
		{
			StreamGeometry streamGeometry2 = new StreamGeometry();
			using (StreamGeometryContext streamGeometryContext2 = streamGeometry2.Open())
			{
				Point? point = null;
				foreach (int pointIndex in previewOverlayIndexedPath.PointIndices)
				{
					if ((uint)pointIndex >= (uint)count || !array2[pointIndex])
					{
						point = null;
						continue;
					}
					Point point2 = array[pointIndex];
					if (point.HasValue)
					{
						Point valueOrDefault = point.GetValueOrDefault();
						streamGeometryContext2.BeginFigure(valueOrDefault, isFilled: false, isClosed: false);
						streamGeometryContext2.LineTo(point2, isStroked: true, isSmoothJoin: false);
					}
					point = point2;
				}
				if (previewOverlayIndexedPath.Closed && previewOverlayIndexedPath.PointIndices.Count > 2 && (uint)previewOverlayIndexedPath.PointIndices[0] < (uint)count)
				{
					IReadOnlyList<int> pointIndices = previewOverlayIndexedPath.PointIndices;
					if ((uint)pointIndices[pointIndices.Count - 1] < (uint)count && array2[previewOverlayIndexedPath.PointIndices[0]])
					{
						IReadOnlyList<int> pointIndices2 = previewOverlayIndexedPath.PointIndices;
						if (array2[pointIndices2[pointIndices2.Count - 1]])
						{
							IReadOnlyList<int> pointIndices3 = previewOverlayIndexedPath.PointIndices;
							streamGeometryContext2.BeginFigure(array[pointIndices3[pointIndices3.Count - 1]], isFilled: false, isClosed: false);
							streamGeometryContext2.LineTo(array[previewOverlayIndexedPath.PointIndices[0]], isStroked: true, isSmoothJoin: false);
						}
					}
				}
			}
			streamGeometry2.Freeze();
			FaceCueGuideCanvas.Children.Add(new System.Windows.Shapes.Path
			{
				Data = streamGeometry2,
				Stroke = BrushForWireframeRole(RoleName(previewOverlayIndexedPath.Role)),
				StrokeThickness = 1.75,
				IsHitTestVisible = false
			});
		}
		for (int j = 0; j < count; j++)
		{
			if (array2[j])
			{
				double num = ((j < MediaPipePreviewMeshFeaturePointMask.Length && MediaPipePreviewMeshFeaturePointMask[j]) ? 3.2 : 2.0);
				Ellipse element = new Ellipse
				{
					Width = num,
					Height = num,
					Fill = FaceMeshOverlayPointBrush,
					IsHitTestVisible = false
				};
				Canvas.SetLeft(element, array[j].X - num / 2.0);
				Canvas.SetTop(element, array[j].Y - num / 2.0);
				FaceCueGuideCanvas.Children.Add(element);
			}
		}
	}

	private static string RoleName(PreviewOverlayMeshFeatureRole role)
	{
		return role switch
		{
			PreviewOverlayMeshFeatureRole.Eye => "eye", 
			PreviewOverlayMeshFeatureRole.Brow => "brow", 
			PreviewOverlayMeshFeatureRole.Mouth => "mouth", 
			PreviewOverlayMeshFeatureRole.Jaw => "jaw", 
			PreviewOverlayMeshFeatureRole.Nose => "nose", 
			PreviewOverlayMeshFeatureRole.Face => "face", 
			_ => "surface", 
		};
	}

	private List<MeshTopologyEdge> GetOrCreateThreeDdfaTopology(IReadOnlyList<ThreeDdfaOnnxSidecarEdge> source)
	{
		lock (_threeDdfaTopologyLock)
		{
			if (_threeDdfaDenseTopologyEdges.Count >= source.Count && _threeDdfaDenseTopologyEdges.Count > 0)
			{
				return _threeDdfaDenseTopologyEdges;
			}
			_threeDdfaDenseTopologyEdges = source.Select((ThreeDdfaOnnxSidecarEdge edge) => new MeshTopologyEdge
			{
				FromIndex = edge.FromIndex,
				ToIndex = edge.ToIndex,
				Role = "surface",
				Source = "3ddfa-full-resolution-topology",
				LengthPercent = 0.0,
				ConfidencePercent = 100.0
			}).ToList();
			return _threeDdfaDenseTopologyEdges;
		}
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
		SolidColorBrush solidColorBrush2 = new SolidColorBrush(Color.FromArgb(245, 238, 174, 74));
		SolidColorBrush stroke = new SolidColorBrush(Color.FromArgb(245, 245, 133, 176));
		SolidColorBrush stroke2 = new SolidColorBrush(Color.FromArgb(245, 196, 247, 163));
		SolidColorBrush stroke3 = new SolidColorBrush(Color.FromArgb(135, 185, 215, 239));
		bool flag = frame.LeftEyeReconstructed || frame.EyeArtifactSuppressed;
		bool flag2 = frame.RightEyeReconstructed || frame.EyeArtifactSuppressed;
		SolidColorBrush solidColorBrush3 = (frame.EyeArtifactSuppressed ? solidColorBrush2 : solidColorBrush);
		AddGuidePolyline(display, frame.FaceContour, stroke3, 1.4, close: true);
		AddGuidePolyline(display, frame.JawContour, stroke3, 1.8, close: false);
		AddGuidePolyline(display, frame.LeftEyeContour, flag ? solidColorBrush3 : solidColorBrush, 2.4, close: true, flag);
		AddGuidePolyline(display, frame.RightEyeContour, flag2 ? solidColorBrush3 : solidColorBrush, 2.4, close: true, flag2);
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
