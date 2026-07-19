using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AvatarBuilder.Modules.Infrastructure;
using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.Diagnostics;
using AvatarBuilder.Modules.Vision.MediaPipe;
using AvatarBuilder.Modules.Vision.Onnx;
using AvatarBuilder.Modules.Vision.Personalization;
using AvatarBuilder.Modules.Vision.Pipeline;
using AvatarBuilder.Modules.Vision.Reconstruction;
using AvatarBuilder.Modules.Webcam;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectShow;
using AvatarBuilder.Modules.Webcam.DirectX12;
using AvatarBuilder.Modules.Webcam.Ffmpeg;
using AvatarBuilder.Modules.Webcam.MediaFoundation;
using AvatarBuilder.Modules.Webcam.Pipeline;
using Microsoft.Win32;
using Ellipse = System.Windows.Shapes.Ellipse;
using Line = System.Windows.Shapes.Line;
using Polyline = System.Windows.Shapes.Polyline;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace AvatarBuilder;

public partial class MainWindow : Window
{
    private const string DefaultAvatarProfileId = "chris";
    private const string DefaultAvatarProfileDisplayName = "Chris";
    private const string PreferredExternalOutputFolder = @"D:\Avatar Builder Output";
    private const string OutputFolderPointerFileName = "AvatarBuilderOutputFolder.txt";
    private const string AvatarLearningStartButtonText = "Start Avatar Capture";
    private const string AvatarLearningStopButtonText = "Stop Avatar Capture";
    private const double AvatarReportSaveIntervalSeconds = 30d;
    private const double ThreeDdfaDenseSampleIntervalMilliseconds = 10000d;
    private const int LastGoodThreeDdfaRetainedSampleCount = 5;
    private const double Insta360Link2ProHorizontalFovDegrees = 71.4d;
    private const string AvatarArchiveFolderName = "AvatarArchive";
    private static readonly TimeSpan FaceFeatureDetectionTargetInterval = TimeSpan.FromMilliseconds(15);
    private static readonly TimeSpan RecoverableVisionErrorStatusInterval = TimeSpan.FromSeconds(3);
    private static readonly SolidColorBrush StartActionButtonBackground = CreateFrozenBrush(0x1f, 0x7a, 0x43);
    private static readonly SolidColorBrush StartActionButtonBorder = CreateFrozenBrush(0x52, 0xc4, 0x7b);
    private static readonly SolidColorBrush StopActionButtonBackground = CreateFrozenBrush(0x9d, 0x2f, 0x2f);
    private static readonly SolidColorBrush StopActionButtonBorder = CreateFrozenBrush(0xe0, 0x69, 0x69);
    private static readonly int[] DenseMeshEyeA =
    [
        33, 246, 161, 160, 159, 158, 157, 173, 133, 155, 154, 153, 145, 144, 163, 7
    ];
    private static readonly int[] DenseMeshEyeB =
    [
        362, 398, 384, 385, 386, 387, 388, 466, 263, 249, 390, 373, 374, 380, 381, 382
    ];
    private static readonly int[] DenseMeshBrowA = [70, 63, 105, 66, 107, 55, 65, 52, 53, 46];
    private static readonly int[] DenseMeshBrowB = [336, 296, 334, 293, 300, 285, 295, 282, 283, 276];
    private static readonly int[] DenseMeshOuterLip = [61, 185, 40, 39, 37, 0, 267, 269, 270, 409, 291, 375, 321, 405, 314, 17, 84, 181, 91, 146];
    private static readonly int[] DenseMeshInnerLip = [78, 191, 80, 81, 82, 13, 312, 311, 310, 415, 308, 324, 318, 402, 317, 14, 87, 178, 88, 95];
    private static readonly int[] DenseMeshJawContour = [234, 93, 132, 58, 172, 136, 150, 149, 176, 148, 152, 377, 400, 378, 379, 365, 397, 288, 361, 323, 454];
    private static readonly int[] DenseMeshNoseBridge = [168, 6, 197, 195, 5, 4, 1, 19, 94, 2];
    private static readonly int[] DenseMeshNoseBase = [98, 97, 2, 326, 327];
    private static readonly int[] DenseMeshFaceOval = [10, 338, 297, 332, 284, 251, 389, 356, 454, 323, 361, 288, 397, 365, 379, 378, 400, 377, 152, 148, 176, 149, 150, 136, 172, 58, 132, 93, 234, 127, 162, 21, 54, 103, 67, 109];

    private readonly FfmpegCameraModeService _cameraModeService = new();
    private readonly DirectShowCameraControlService _cameraControlService = new();
    private readonly CameraPreviewService _previewService = new();
    private CompositeFaceLandmarkTracker? _faceLandmarkTracker = new();
    private readonly FaceLandmarkTemporalReconstructor _faceLandmarkReconstructor = new();
    private readonly FaceLandmarkMetricCalculator _faceLandmarkMetricCalculator = new();
    private readonly FaceLockStabilityAnalyzer _faceLockStabilityAnalyzer = new();
    private readonly FaceFrameGeometryEstimator _faceFrameGeometryEstimator = new();
    private readonly ThreeDdfaOnnxModelInfo _threeDdfaOnnxModelInfo;
    private readonly ThreeDdfaOnnxSidecarEnvironment _threeDdfaOnnxEnvironment;
    private ThreeDdfaOnnxReconstructionClient? _threeDdfaAvatarClient;
    private ThreeDdfaOnnxReconstructionClient? _threeDdfaFaceBoxClient;
    private readonly AvatarBuilderStartupOptions _startupOptions;
    private readonly AvatarProfileStore _avatarProfileStore = new();
    private readonly AvatarUserSession _avatarUserSession = new();
    private readonly AvatarCaptureQualityAnalyzer _avatarCaptureQualityAnalyzer = new();
    private readonly LastGoodThreeDdfaStore _lastGoodThreeDdfaStore = new();
    private readonly AvatarModelObservationStore _avatarModelObservationStore = new();
    private readonly AvatarModelHistoryStore _avatarModelHistoryStore = new();
    private readonly AvatarModelStore _avatarModelStore = new();
    private readonly AvatarSystemDashboardStore _avatarSystemDashboardStore = new();
    private readonly VisionBenchmarkRecorder _visionBenchmarkRecorder = new();
    private readonly PoseAlignmentAuditor _poseAlignmentAuditor = new();
    private readonly object _faceLandmarkTrackerLock = new();
    private readonly object _threeDdfaClientLock = new();
    private readonly object _previewFramePumpLock = new();
    private readonly object _directX12PreviewLock = new();
    private readonly object _directX12AnalysisFrameLock = new();
    private readonly object _faceFeatureDetectionFrameLock = new();
    private readonly object _personalFaceReportWriterLock = new();
    private readonly object _avatarReportStorageLock = new();
    private readonly object _threeDdfaTopologyLock = new();
    private readonly DispatcherTimer _cameraHealthTimer;
    private readonly List<ThreeDdfaReconstructionSnapshot> _lastGoodThreeDdfaSamples = [];
    private List<MeshTopologyEdge> _threeDdfaDenseTopologyEdges = [];
    private IReadOnlyList<CameraDevice> _cameras = [];
    private CancellationTokenSource? _modeLoadCancellation;
    private string _outputFolder;
    private BitmapSource? _latestFrame;
    private FaceFeatureDetection _currentFaceFeatureDetection = FaceFeatureDetection.None;
    private FaceLandmarkFrame _currentFaceLandmarkFrame = FaceLandmarkFrame.None;
    private FaceLandmarkMetrics _currentFaceLandmarkMetrics = FaceLandmarkMetrics.None;
    private FaceLockStabilityAnalysis _currentFaceLockStabilityAnalysis = FaceLockStabilityAnalysis.Waiting;
    private FaceFrameGeometry _currentFaceFrameGeometry = FaceFrameGeometry.None;
    private ThreeDdfaOnnxSidecarResponse _currentThreeDdfaOnnxResponse = ThreeDdfaOnnxSidecarResponse.Waiting;
    private ThreeDdfaOnnxSidecarFaceBox? _threeDdfaTrackingFaceBox;
    private AvatarCaptureQualityAssessment _currentAvatarCaptureQuality = AvatarCaptureQualityAssessment.Waiting;
    private AvatarProfileRegistry _avatarProfileRegistry = new();
    private AvatarProfile _currentAvatarProfile = new()
    {
        Id = DefaultAvatarProfileId,
        DisplayName = DefaultAvatarProfileDisplayName,
        DataFolderName = ""
    };
    private string _avatarSystemDashboardPath = "";
    private string _avatarModelHtmlPath = "";
    private string _lastGoodThreeDdfaHtmlPath = "";
    private BitmapSource? _pendingPreviewFrame;
    private BitmapSource? _pendingFaceFeatureDetectionFrame;
    private FaceBoxSystem _pendingFaceBoxSystem = FaceBoxSystem.MediaPipe;
    private int _pendingFaceBoxSystemGeneration;
    private TextureNativeFrameLease? _pendingDirectX12AnalysisFrame;
    private AvatarReportSnapshot? _pendingAvatarReportSnapshot;
    private Task? _avatarReportWriterTask;
    private Direct3D12PreviewHost? _directX12PreviewHost;
    private Dx12Camera? _directX12NativeCamera;
    private DateTime _lastPreviewFrameAcceptedAt = DateTime.MinValue;
    private DateTime _cameraStartedAtUtc = DateTime.MinValue;
    private DateTime _lastCameraSourceFrameAtUtc = DateTime.MinValue;
    private DateTime _lastDirectX12DiagnosticsAtUtc = DateTime.MinValue;
    private DateTime _lastDirectX12AnalysisFrameAtUtc = DateTime.MinValue;
    private DateTime _lastAvatarReportSavedAtUtc = DateTime.MinValue;
    private DateTime _previewReplacementWindowStartedAtUtc = DateTime.MinValue;
    private string _avatarCaptureGateReason = "waiting for face landmarks";
    private string _lastFaceBoxBackendStatus = "waiting";
    private double _directX12PreviewMaxRenderFramesPerSecond;
    private TimeSpan _directX12AnalysisFrameInterval = TimeSpan.FromSeconds(1d / 5d);
    private int _directX12AnalysisMaxOutputWidth = 3840;
    private int _previewFramesReplacedSinceWarning;
    private int _uiFramePending;
    private int _previewWarningPending;
    private int _faceFeatureDetectionPending;
    private int _threeDdfaOnnxReconstructionPending;
    private long _lastRecoverableVisionErrorStatusTicks;
    private long _directX12FrameNumber;
    private int _directX12AnalysisWorkerQueued;
    private int _automaticCameraRecoveryAttempts;
    private bool _avatarLearningRequested;
    private bool _avatarCaptureGateAccepted;
    private bool _showLiveWireframePreview;
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
    private bool _startupOptionsApplied;
    private int _selectedTrackingFidelityIndex = 2;
    private int _faceBoxSystemGeneration;
    private FaceBoxSystem _selectedFaceBoxSystem = FaceBoxSystem.MediaPipe;
    private FaceCueGuideLayout? _activeFaceCueLayout;
    private DateTime _lastFaceAutoFollowAt = DateTime.MinValue;
    private DateTime _lastFaceFeatureDetectionAt = DateTime.MinValue;
    private DateTime _pendingFaceFeatureDetectionCapturedAtUtc = DateTime.MinValue;
    private DateTime _lastFaceFeatureLockAt = DateTime.MinValue;
    private DateTime _lastThreeDdfaOnnxRequestAtUtc = DateTime.MinValue;
    private DateTime _lastThreeDdfaFaceBoxesAtUtc = DateTime.MinValue;

    private static readonly IReadOnlyList<TrackingFidelityOption> TrackingFidelityOptions =
    [
        new(960, 15d),
        new(1920, 18d),
        new(3840, 15d)
    ];

    private static readonly TimeSpan CameraStallTimeout = TimeSpan.FromSeconds(6);

    private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
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
        _threeDdfaAvatarClient = new ThreeDdfaOnnxReconstructionClient(_threeDdfaOnnxEnvironment);
        InitializeComponent();
        _outputFolder = ResolveInitialOutputFolder(_startupOptions.OutputFolder);
        _visionBenchmarkRecorder.SetOutputRoot(_outputFolder);
        ResetAvatarCaptureGate("waiting for face landmarks");
        _previewService.FrameAvailable += PreviewFrameAvailable;
        _previewService.CameraFrameAvailable += PreviewCameraFrameAvailable;
        _previewService.StatusChanged += PreviewStatusChanged;
        _cameraHealthTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _cameraHealthTimer.Tick += CameraHealthTimerTick;
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
        DarkWindowFrame.Apply(this);
        EnsureOutputFolderConfiguredForLaunch();
        InitializeAvatarProfiles(promptForStartupUser: true);
        _poseAlignmentAuditor.SetOutputRoot(GetAvatarDataFolder());
        UpdateFaceBoxSystemMenuChecks();
        UpdateFaceBoxOptionsUi();
        UpdateTrackingFidelityMenuChecks();
        ApplyTrackingFidelity();
        UpdateSettingLabels();
        PrepareAvatarCaptureFolder(showStatus: false);
        UpdateAvatarLearningStatusUi();
        _cameraHealthTimer.Start();
        Dispatcher.InvokeAsync(async () => await RefreshCamerasAsync(), DispatcherPriority.ApplicationIdle);
        Dispatcher.InvokeAsync(ApplyStartupOptionsAfterLoad, DispatcherPriority.ApplicationIdle);
    }

    private void WindowActivated(object? sender, EventArgs e)
    {
        if (!IsLoaded || _isClosing || !_isCameraEnabled)
        {
            return;
        }

        _directX12NativeCamera?.ResumePreview();
        _lastDirectX12AnalysisFrameAtUtc = DateTime.MinValue;

        var frame = _latestFrame;
        if (frame is not null)
        {
            _lastFaceFeatureDetectionAt = DateTime.MinValue;
            QueueFaceFeatureDetection(frame, DateTime.UtcNow);
        }

        UpdateFaceCueGuideOverlay(frame);
    }

    private void ApplyStartupOptionsAfterLoad()
    {
        if (_startupOptionsApplied)
        {
            return;
        }

        _startupOptionsApplied = true;
        if (_startupOptions.StartAvatarLearning && IsAvatarUserLoggedIn)
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
            var state = GetAvatarCaptureGuidanceState();
            var status = $"Avatar capture guidance: {state.Title}. {state.Detail}";
            SetStatus(status);
            MonitorStatusText.Text = status;
        }
    }

    private void InitializeAvatarProfiles(bool promptForStartupUser)
    {
        _avatarLearningRequested = false;
        _avatarUserSession.LogOut();
        _avatarProfileRegistry = _avatarProfileStore.Load(_outputFolder);

        AvatarProfile? loginProfile = null;
        if (promptForStartupUser)
        {
            loginProfile = ResolveAvatarLoginSelection(PromptForAvatarLogin(_avatarProfileRegistry));
        }

        if (_avatarProfileRegistry.Profiles.Count == 0)
        {
            _avatarProfileStore.AddOrUpdateProfile(_outputFolder, _avatarProfileRegistry, DefaultAvatarProfileDisplayName);
        }

        var selected = loginProfile is null
            ? FindSelectedAvatarProfile()
            : loginProfile;
        if (selected is null)
        {
            return;
        }

        ApplyCurrentAvatarProfile(selected, loadModel: false);
        if (loginProfile is not null)
        {
            LogInAvatarProfile(selected, loadModel: false, announce: false);
        }
        else
        {
            ResetAvatarCaptureGate("no avatar user logged in; capture stopped");
            UpdateAvatarSessionUi();
        }
    }

    private AvatarLoginSelection PromptForAvatarLogin(AvatarProfileRegistry registry)
    {
        var profiles = registry.Profiles.ToList();
        var window = new Window
        {
            Title = "Avatar User Login",
            Owner = this,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(8, 13, 18)),
            Foreground = Brushes.White
        };

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "Who is in front of the camera?",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Log in a remembered user, or type a new consenting user's name. Avatar capture remains stopped until login succeeds, and data stays isolated by profile.",
            Foreground = new SolidColorBrush(Color.FromRgb(185, 215, 239)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var combo = new ComboBox
        {
            ItemsSource = profiles,
            DisplayMemberPath = nameof(AvatarProfile.DisplayName),
            MinHeight = 34,
            Style = TryFindResource(typeof(ComboBox)) as Style,
            ItemContainerStyle = TryFindResource(typeof(ComboBoxItem)) as Style,
            IsEnabled = profiles.Count > 0,
            SelectedItem = profiles.FirstOrDefault(profile => string.Equals(profile.Id, registry.SelectedProfileId, StringComparison.OrdinalIgnoreCase))
                ?? profiles.OrderByDescending(static profile => profile.LastSelectedAtUtc ?? DateTime.MinValue).FirstOrDefault()
        };
        panel.Children.Add(combo);

        var nameBox = new TextBox
        {
            MinHeight = 34,
            Margin = new Thickness(0, 10, 0, 0),
            Style = TryFindResource(typeof(TextBox)) as Style
        };
        panel.Children.Add(nameBox);

        var status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(255, 154, 154)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        };
        panel.Children.Add(status);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        var continueButton = new Button
        {
            Content = "Login",
            MinWidth = 110,
            Margin = new Thickness(8, 0, 0, 0),
            Style = TryFindResource(typeof(Button)) as Style
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 110,
            Style = TryFindResource(typeof(Button)) as Style
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(continueButton);
        panel.Children.Add(buttons);
        window.Content = panel;

        AvatarLoginSelection selection = new("", "");
        continueButton.Click += (_, _) =>
        {
            var typedName = nameBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(typedName))
            {
                selection = new AvatarLoginSelection("", typedName);
                window.DialogResult = true;
                return;
            }

            if (combo.SelectedItem is AvatarProfile profile)
            {
                selection = new AvatarLoginSelection(profile.Id, "");
                window.DialogResult = true;
                return;
            }

            status.Text = "Type the user's name before continuing.";
        };
        cancelButton.Click += (_, _) => window.DialogResult = false;

        var result = window.ShowDialog();
        return result == true ? selection : new AvatarLoginSelection("", "");
    }

    private AvatarProfile? ResolveAvatarLoginSelection(AvatarLoginSelection selection)
    {
        if (!string.IsNullOrWhiteSpace(selection.NewDisplayName))
        {
            return _avatarProfileStore.AddOrUpdateProfile(
                _outputFolder,
                _avatarProfileRegistry,
                selection.NewDisplayName);
        }

        return string.IsNullOrWhiteSpace(selection.ProfileId)
            ? null
            : _avatarProfileStore.SelectProfile(
                _outputFolder,
                _avatarProfileRegistry,
                selection.ProfileId);
    }

    private AvatarProfile? FindSelectedAvatarProfile()
    {
        return _avatarProfileRegistry.Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, _avatarProfileRegistry.SelectedProfileId, StringComparison.OrdinalIgnoreCase))
            ?? _avatarProfileRegistry.Profiles
                .OrderByDescending(static profile => profile.LastSelectedAtUtc ?? DateTime.MinValue)
                .FirstOrDefault();
    }

    private void ApplyCurrentAvatarProfile(AvatarProfile profile, bool loadModel)
    {
        if (_avatarUserSession.IsLoggedIn
            && !string.Equals(_avatarUserSession.LoggedInProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            _avatarUserSession.LogOut();
        }

        _currentAvatarProfile = profile;
        _avatarLearningRequested = false;
        _poseAlignmentAuditor.SetOutputRoot(GetAvatarDataFolder());

        _avatarProfileStore.SelectProfile(_outputFolder, _avatarProfileRegistry, profile.Id);
        if (loadModel)
        {
            ResetAvatarRuntimeForProfile("selected avatar profile changed; login required before avatar capture");
            PrepareAvatarCaptureFolder(showStatus: true);
        }

        UpdateAvatarSessionUi();
        UpdateAvatarLearningStatusUi();
    }

    private string CurrentAvatarProfileId => string.IsNullOrWhiteSpace(_currentAvatarProfile.Id)
        ? DefaultAvatarProfileId
        : _currentAvatarProfile.Id;

    private string CurrentAvatarProfileDisplayName => string.IsNullOrWhiteSpace(_currentAvatarProfile.DisplayName)
        ? DefaultAvatarProfileDisplayName
        : _currentAvatarProfile.DisplayName;

    private bool IsAvatarUserLoggedIn => _avatarUserSession.IsLoggedIn
        && string.Equals(
            _avatarUserSession.LoggedInProfileId,
            CurrentAvatarProfileId,
            StringComparison.OrdinalIgnoreCase);

    private void LogInAvatarProfile(AvatarProfile profile, bool loadModel, bool announce)
    {
        if (!string.Equals(CurrentAvatarProfileId, profile.Id, StringComparison.OrdinalIgnoreCase) || loadModel)
        {
            ApplyCurrentAvatarProfile(profile, loadModel);
        }

        _avatarLearningRequested = false;
        _avatarUserSession.LogIn(profile.Id);
        _currentThreeDdfaOnnxResponse = ThreeDdfaOnnxSidecarResponse.Waiting;
        ResetAvatarCaptureGate("avatar user logged in; waiting for high-confidence face tracking");
        _currentAvatarCaptureQuality = AvatarCaptureQualityAssessment.Waiting;
        UpdateAvatarSessionUi();
        UpdateAvatarLearningStatusUi();
        QueueAvatarSessionStatusReport();

        if (announce)
        {
            SetStatus($"Logged in as {CurrentAvatarProfileDisplayName}. Avatar capture is ready to start.");
        }
    }

    private void LogOutAvatarUser(bool announce)
    {
        var displayName = CurrentAvatarProfileDisplayName;
        _avatarLearningRequested = false;
        _avatarUserSession.LogOut();
        _currentThreeDdfaOnnxResponse = ThreeDdfaOnnxSidecarResponse.Waiting;
        ResetAvatarCaptureGate("no avatar user logged in; capture stopped");
        UpdateAvatarCaptureQuality();
        UpdateAvatarSessionUi();
        UpdateAvatarLearningStatusUi();
        QueueAvatarSessionStatusReport();

        if (announce)
        {
            SetStatus($"{displayName} logged out. Avatar capture has stopped.");
        }
    }

    private void UpdateAvatarSessionUi()
    {
        var loggedIn = IsAvatarUserLoggedIn;
        LoginLogoutMenuItem.Header = loggedIn
            ? $"_Logout {CurrentAvatarProfileDisplayName}"
            : "_Login...";
        AvatarLoginStatusText.Text = loggedIn
            ? $"Logged in as {CurrentAvatarProfileDisplayName}. Avatar capture can be started."
            : "No avatar user logged in. Use File > Login to begin a capture session.";
        AvatarLoginStatusText.Foreground = new SolidColorBrush(loggedIn
            ? Color.FromRgb(128, 224, 164)
            : Color.FromRgb(255, 207, 122));
        AvatarLearningToggleButton.IsEnabled = loggedIn;
    }

    private void QueueAvatarSessionStatusReport()
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            QueueAvatarReportSave(CreateAvatarReportSnapshot(GetAvatarDataFolder()));
        }
        catch
        {
            // Login and logout must remain reliable when the data folder is unavailable.
        }
    }

    private void ResetAvatarCaptureGate(string reason, bool accepted = false)
    {
        _avatarCaptureGateAccepted = accepted;
        _avatarCaptureGateReason = string.IsNullOrWhiteSpace(reason) ? "waiting for face landmarks" : reason;
    }

    private void ResetAvatarRuntimeForProfile(string reason)
    {
        ResetAvatarCaptureGate(reason);
        _currentAvatarCaptureQuality = AvatarCaptureQualityAssessment.Waiting;
        _currentThreeDdfaOnnxResponse = ThreeDdfaOnnxSidecarResponse.Waiting;
        _avatarSystemDashboardPath = "";
        _avatarModelHtmlPath = "";
        _lastGoodThreeDdfaHtmlPath = "";
        _lastGoodThreeDdfaSamples.Clear();
    }

    private void WindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _isClosing = true;
        _cameraHealthTimer.Stop();
        _cameraHealthTimer.Tick -= CameraHealthTimerTick;
        _modeLoadCancellation?.Cancel();
        _modeLoadCancellation?.Dispose();
        _previewService.FrameAvailable -= PreviewFrameAvailable;
        _previewService.CameraFrameAvailable -= PreviewCameraFrameAvailable;
        _previewService.StatusChanged -= PreviewStatusChanged;
        DisposeDirectX12NativeCamera();
        DisposeDirectX12PreviewHost();
        _previewService.Dispose();
        ResetPreviewFramePump();
        CompositeFaceLandmarkTracker? tracker;
        lock (_faceLandmarkTrackerLock)
        {
            tracker = _faceLandmarkTracker;
            _faceLandmarkTracker = null;
        }
        tracker?.Dispose();
        ThreeDdfaOnnxReconstructionClient? avatarClient;
        ThreeDdfaOnnxReconstructionClient? faceBoxClient;
        lock (_threeDdfaClientLock)
        {
            avatarClient = _threeDdfaAvatarClient;
            faceBoxClient = _threeDdfaFaceBoxClient;
            _threeDdfaAvatarClient = null;
            _threeDdfaFaceBoxClient = null;
        }
        avatarClient?.Dispose();
        faceBoxClient?.Dispose();
        _visionBenchmarkRecorder.Dispose();
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
            var cameraTask = GetVideoInputDevicesAsync();
            cameras = await cameraTask.WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (TimeoutException)
        {
            SetStatus("Camera scan is taking longer than expected. The window is ready; try Refresh in a moment.");
            _isRefreshingCameras = false;
            return;
        }
        catch (Exception ex)
        {
            SetStatus($"Could not scan cameras: {ex.Message}");
            _isRefreshingCameras = false;
            return;
        }
        finally
        {
            _isRefreshingCameras = false;
        }

        _cameras = cameras;
        CameraComboBox.ItemsSource = _cameras;
        CameraComboBox.DisplayMemberPath = nameof(CameraDevice.DisplayName);

        if (_cameras.Count > 0)
        {
            CameraComboBox.SelectedIndex = 0;
            SetStatus($"Found {_cameras.Count} camera{(_cameras.Count == 1 ? "" : "s")}.");
        }
        else
        {
            CameraModeComboBox.ItemsSource = new[] { CameraVideoMode.Auto };
            CameraModeComboBox.SelectedIndex = 0;
            CameraControlsPanel.Children.Clear();
            CameraControlsStatusText.Text = CameraControlText.FormatChooseCameraControlsStatus();
            SetStatus("No cameras found.");
            SetPreviewState("No camera source found", null);
        }
    }

    private static Task<IReadOnlyList<CameraDevice>> GetVideoInputDevicesAsync()
    {
        var completion = new TaskCompletionSource<IReadOnlyList<CameraDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
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
                    mediaFoundationDevices = [];
                }

                IReadOnlyList<CameraDevice> directShowDevices;
                try
                {
                    directShowDevices = DirectShowCameraEnumerator.GetVideoInputDevices();
                }
                catch
                {
                    directShowDevices = [];
                }

                completion.SetResult(CameraDeviceCatalog.MergeDevices(mediaFoundationDevices, directShowDevices));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Avatar Builder Camera Enumerator"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private async void CameraSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CameraComboBox.SelectedItem is not CameraDevice camera)
        {
            return;
        }

        await LoadCameraModesAsync(camera);
        await LoadCameraControlsAsync(camera);
        if (_isCameraEnabled)
        {
            RestartPreview();
        }
    }

    private async Task LoadCameraModesAsync(CameraDevice camera)
    {
        _modeLoadCancellation?.Cancel();
        _modeLoadCancellation?.Dispose();
        _modeLoadCancellation = new CancellationTokenSource();
        var cancellationToken = _modeLoadCancellation.Token;

        CameraModeComboBox.ItemsSource = new[] { CameraVideoMode.Auto };
        CameraModeComboBox.SelectedIndex = 0;
        SetStatus($"Loading modes for {camera.Name}...");

        try
        {
            var modes = await _cameraModeService.GetModesAsync(camera, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            CameraModeComboBox.ItemsSource = modes;
            SelectRecommendedCameraModeForFidelity(replaceAutoOnly: false);
            var selectedMode = CameraModeComboBox.SelectedItem as CameraVideoMode ?? CameraVideoMode.Auto;
            SetStatus($"Loaded {modes.Count} mode{(modes.Count == 1 ? "" : "s")} for {camera.Name}. Selected {selectedMode.Label}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetStatus($"Could not load camera modes: {ex.Message}");
        }
    }

    private async void RefreshCameraControlsClicked(object sender, RoutedEventArgs e)
    {
        if (CameraComboBox.SelectedItem is not CameraDevice camera)
        {
            CameraControlsPanel.Children.Clear();
            CameraControlsStatusText.Text = CameraControlText.FormatChooseCameraControlsStatus();
            return;
        }

        await LoadCameraControlsAsync(camera);
    }

    private async Task LoadCameraControlsAsync(CameraDevice camera)
    {
        if (_isLoadingCameraControls)
        {
            return;
        }

        _isLoadingCameraControls = true;
        CameraControlsPanel.Children.Clear();
        CameraControlsStatusText.Text = $"Loading controls for {camera.Name}...";

        try
        {
            var controls = await GetCameraControlsAsync(camera).WaitAsync(TimeSpan.FromSeconds(5));
            if (!ReferenceEquals(CameraComboBox.SelectedItem, camera))
            {
                return;
            }

            BuildCameraControlRows(camera, controls);
            CameraControlsStatusText.Text = controls.Count == 0
                ? CameraControlText.FormatNoCameraControlsStatus()
                : CameraControlText.FormatCameraControlsLoadedStatus(camera, controls.Count);
        }
        catch (TimeoutException)
        {
            CameraControlsStatusText.Text = "Camera controls are taking longer than expected. Try Refresh after the camera is idle.";
        }
        catch (Exception ex)
        {
            CameraControlsStatusText.Text = $"Could not load camera controls: {ex.Message}";
        }
        finally
        {
            _isLoadingCameraControls = false;
        }
    }

    private Task<IReadOnlyList<CameraControlItem>> GetCameraControlsAsync(CameraDevice camera)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<CameraControlItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(_cameraControlService.GetControls(camera));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Avatar Builder Camera Controls"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private void BuildCameraControlRows(CameraDevice camera, IReadOnlyList<CameraControlItem> controls)
    {
        CameraControlsPanel.Children.Clear();
        foreach (var control in controls.OrderBy(static control => control.Kind).ThenBy(static control => control.Name))
        {
            CameraControlsPanel.Children.Add(CreateCameraControlRow(camera, control));
        }
    }

    private UIElement CreateCameraControlRow(CameraDevice camera, CameraControlItem control)
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 12)
        };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameText = new TextBlock
        {
            Text = control.Name,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        header.Children.Add(nameText);

        var valueText = new TextBlock
        {
            Text = CameraControlText.FormatCameraControlValue(control.Value),
            Foreground = new SolidColorBrush(Color.FromRgb(185, 215, 239)),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(valueText, 1);
        header.Children.Add(valueText);

        var autoCheckBox = new CheckBox
        {
            Content = "Auto",
            IsChecked = control.IsAuto,
            IsEnabled = control.SupportsAuto,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(autoCheckBox, 2);
        header.Children.Add(autoCheckBox);

        var slider = new Slider
        {
            Minimum = control.Minimum,
            Maximum = control.Maximum,
            Value = Math.Clamp(control.Value, control.Minimum, control.Maximum),
            TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
            IsSnapToTickEnabled = false,
            Ticks = new DoubleCollection { control.DefaultValue },
            ToolTip = $"Default: {CameraControlText.FormatCameraControlValue(control.DefaultValue)}",
            IsEnabled = !control.IsAuto || !control.SupportsAuto
        };

        var binding = new CameraControlBinding(camera, control, valueText, slider, autoCheckBox);
        slider.Tag = binding;
        autoCheckBox.Tag = binding;
        slider.ValueChanged += CameraControlSliderChanged;
        autoCheckBox.Checked += CameraControlAutoChanged;
        autoCheckBox.Unchecked += CameraControlAutoChanged;

        panel.Children.Add(header);
        panel.Children.Add(slider);
        return panel;
    }

    private void CameraControlSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingCameraControlUi
            || sender is not Slider slider
            || slider.Tag is not CameraControlBinding binding)
        {
            return;
        }

        var value = CameraControlText.RoundCameraControlToStep(slider.Value, binding.Control);
        value = CameraControlText.ApplyCameraControlDefaultMagnet(value, binding.Control);
        _isUpdatingCameraControlUi = true;
        try
        {
            if (Math.Abs(slider.Value - value) > 0.001d)
            {
                slider.Value = value;
            }

            if (binding.AutoCheckBox is not null)
            {
                binding.AutoCheckBox.IsChecked = false;
            }
        }
        finally
        {
            _isUpdatingCameraControlUi = false;
        }

        ApplyCameraControl(binding, value, isAuto: false);
    }

    private void CameraControlAutoChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingCameraControlUi
            || sender is not CheckBox checkBox
            || checkBox.Tag is not CameraControlBinding binding)
        {
            return;
        }

        var isAuto = checkBox.IsChecked == true;
        var value = CameraControlText.RoundCameraControlToStep(binding.Slider.Value, binding.Control);
        binding.Slider.IsEnabled = !isAuto;
        ApplyCameraControl(binding, value, isAuto);
    }

    private void ApplyCameraControl(CameraControlBinding binding, int value, bool isAuto)
    {
        if (CameraComboBox.SelectedItem is not CameraDevice selectedCamera
            || !string.Equals(selectedCamera.DevicePath, binding.Camera.DevicePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        binding.Control.Value = value;
        binding.Control.IsAuto = isAuto;
        binding.ValueText.Text = isAuto
            ? "Auto"
            : CameraControlText.FormatCameraControlValue(value);

        var success = _cameraControlService.SetControl(binding.Camera, binding.Control, value, isAuto);
        CameraControlsStatusText.Text = CameraControlText.FormatCameraControlSetStatus(binding.Control, value, isAuto, success);
    }

    private void CameraModeSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isChoosingCameraModeForFidelity)
        {
            return;
        }

        if (_isCameraEnabled)
        {
            RestartPreview();
        }
    }

    private void FaceBoxSystemMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem
            || !Enum.TryParse(menuItem.Tag?.ToString(), out FaceBoxSystem selectedSystem))
        {
            UpdateFaceBoxSystemMenuChecks();
            return;
        }

        if (selectedSystem == _selectedFaceBoxSystem)
        {
            UpdateFaceBoxSystemMenuChecks();
            return;
        }

        SwitchFaceBoxSystem(selectedSystem);
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

        CompositeFaceLandmarkTracker? previousTracker;
        lock (_faceLandmarkTrackerLock)
        {
            previousTracker = _faceLandmarkTracker;
            _faceLandmarkTracker = selectedSystem == FaceBoxSystem.MediaPipe
                ? new CompositeFaceLandmarkTracker()
                : null;
            if (_faceLandmarkTracker is not null)
            {
                _faceLandmarkTracker.MaxDetectionDimension = GetFaceLandmarkDetectionDimension();
            }
        }

        ResetLandmarkTracking();
        if (previousTracker is not null)
        {
            previousTracker.Dispose();
        }

        ThreeDdfaOnnxReconstructionClient? previousAvatarClient;
        ThreeDdfaOnnxReconstructionClient? previousFaceBoxClient;
        lock (_threeDdfaClientLock)
        {
            previousAvatarClient = _threeDdfaAvatarClient;
            previousFaceBoxClient = _threeDdfaFaceBoxClient;
            _threeDdfaAvatarClient = selectedSystem == FaceBoxSystem.MediaPipe
                ? new ThreeDdfaOnnxReconstructionClient(_threeDdfaOnnxEnvironment)
                : null;
            _threeDdfaFaceBoxClient = selectedSystem == FaceBoxSystem.ThreeDdfaV2
                ? new ThreeDdfaOnnxReconstructionClient(_threeDdfaOnnxEnvironment)
                : null;
        }
        previousAvatarClient?.Dispose();
        previousFaceBoxClient?.Dispose();

        if (selectedSystem == FaceBoxSystem.ThreeDdfaV2 && _showLiveWireframePreview)
        {
            _showLiveWireframePreview = false;
            LiveWireframeMenuItem.IsChecked = false;
            SetPreviewState("Camera active", _latestFrame);
        }

        UpdateFaceBoxSystemMenuChecks();
        UpdateFaceBoxOptionsUi();
        UpdateAvatarLearningStatusUi();
        SetStatus($"Face Box System changed to {GetFaceBoxSystemDisplayName()}. The previous tracking backend has been stopped.");
        UpdateFaceCueGuideOverlay(_latestFrame);
    }

    private void UpdateFaceBoxSystemMenuChecks()
    {
        MediaPipeFaceBoxSystemMenuItem.IsChecked = _selectedFaceBoxSystem == FaceBoxSystem.MediaPipe;
        ThreeDdfaFaceBoxSystemMenuItem.IsChecked = _selectedFaceBoxSystem == FaceBoxSystem.ThreeDdfaV2;
    }

    private void UpdateFaceBoxOptionsUi()
    {
        var mediaPipeSelected = _selectedFaceBoxSystem == FaceBoxSystem.MediaPipe;
        FaceTrackingFieldExpander.Header = $"Face Box Options ({GetFaceBoxSystemDisplayName()})";
        LiveWireframeMenuItem.IsEnabled = mediaPipeSelected;
        LiveWireframeMenuItem.ToolTip = mediaPipeSelected
            ? "Hides the webcam image and shows the current camera-relative MediaPipe face mesh while analysis keeps running."
            : "Live wireframe is MediaPipe-specific and is unavailable while 3DDFA-V2 owns the face box.";
    }

    private string GetFaceBoxSystemDisplayName()
    {
        return _selectedFaceBoxSystem == FaceBoxSystem.ThreeDdfaV2
            ? "3DDFA-V2"
            : "MediaPipe";
    }

    private bool IsSelectedFaceBoxSystemAvailable()
    {
        if (_selectedFaceBoxSystem == FaceBoxSystem.ThreeDdfaV2)
        {
            return _threeDdfaOnnxEnvironment.IsReady;
        }

        lock (_faceLandmarkTrackerLock)
        {
            return _faceLandmarkTracker?.IsAvailable == true;
        }
    }

    private int GetFaceLandmarkDetectionDimension()
    {
        var option = GetSelectedTrackingFidelityOption();
        return option.MaxOutputWidth >= 3840
            ? 1920
            : Math.Clamp(option.MaxOutputWidth, 640, 960);
    }

    private void ResetFaceBoxDiagnostics()
    {
        _lastFaceBoxBackendStatus = "waiting";
    }

    private void TrackingFidelityMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem
            || !int.TryParse(menuItem.Tag?.ToString(), out var selectedIndex)
            || selectedIndex < 0
            || selectedIndex >= TrackingFidelityOptions.Count)
        {
            UpdateTrackingFidelityMenuChecks();
            return;
        }

        _selectedTrackingFidelityIndex = selectedIndex;
        UpdateTrackingFidelityMenuChecks();
        ApplyTrackingFidelity();
        SelectRecommendedCameraModeForFidelity(replaceAutoOnly: true);
        if (_isCameraEnabled)
        {
            RestartPreview();
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
        var option = GetSelectedTrackingFidelityOption();
        var analysisOutputWidth = GetTrackingAnalysisOutputWidth(option);

        _previewService.MaxOutputWidth = analysisOutputWidth;
        _previewService.MaxOutputFramesPerSecond = option.MaxFramesPerSecond;
        _directX12AnalysisMaxOutputWidth = analysisOutputWidth;
        _directX12AnalysisFrameInterval = TimeSpan.FromSeconds(1d / Math.Clamp(option.MaxFramesPerSecond, 1d, 60d));
        lock (_faceLandmarkTrackerLock)
        {
            if (_faceLandmarkTracker is not null)
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
        var currentMode = CameraModeComboBox.SelectedItem as CameraVideoMode;
        if (replaceAutoOnly && currentMode is { IsAuto: false })
        {
            return;
        }

        var modes = CameraModeComboBox.Items.OfType<CameraVideoMode>().ToList();
        var recommended = FindRecommendedCameraMode(modes, GetSelectedTrackingFidelityOption());
        if (recommended is null || IsSameCameraMode(currentMode, recommended))
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
            CameraModeComboBox.SelectedItem = recommended;
        }
        finally
        {
            _isChoosingCameraModeForFidelity = false;
        }
    }

    private static CameraVideoMode? FindRecommendedCameraMode(IReadOnlyList<CameraVideoMode> modes, TrackingFidelityOption option)
    {
        return CameraModeRecommendation.FindRecommendedMode(
            modes,
            option.MaxOutputWidth,
            option.MaxFramesPerSecond);
    }

    private static bool IsSameCameraMode(CameraVideoMode? left, CameraVideoMode right)
    {
        return left is not null
            && left.IsAuto == right.IsAuto
            && left.Width == right.Width
            && left.Height == right.Height
            && Nullable.Equals(left.FramesPerSecond, right.FramesPerSecond)
            && string.Equals(left.InputFormat, right.InputFormat, StringComparison.OrdinalIgnoreCase);
    }

    private void CameraToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingCameraToggle)
        {
            return;
        }

        if (CameraToggle.IsChecked == true)
        {
            _automaticCameraRecoveryAttempts = 0;
            StartPreview();
        }
        else
        {
            StopPreview();
        }
    }

    private void DirectX12PreviewMenuItemClicked(object sender, RoutedEventArgs e)
    {
        _isDirectX12PreviewEnabled = DirectX12PreviewMenuItem.IsChecked;
        if (_isCameraEnabled)
        {
            RestartPreview();
            return;
        }

        UpdateDirectX12PreviewMode();
        if (_latestFrame is not null)
        {
            SetPreviewState("Camera active", _latestFrame);
        }
    }

    private void LiveWireframeMenuItemClicked(object sender, RoutedEventArgs e)
    {
        _showLiveWireframePreview = LiveWireframeMenuItem.IsChecked;
        SetPreviewState(_showLiveWireframePreview ? "Live wireframe preview" : "Camera active", _latestFrame);
    }

    private async void StartPreview()
    {
        if (CameraComboBox.SelectedItem is not CameraDevice camera)
        {
            _cameraRecoveryPending = false;
            SetCameraToggle(false);
            SetStatus("Choose a camera first.");
            return;
        }

        var mode = CameraModeComboBox.SelectedItem as CameraVideoMode ?? CameraVideoMode.Auto;
        _cameraStartedAtUtc = DateTime.UtcNow;
        _lastCameraSourceFrameAtUtc = DateTime.MinValue;
        ApplyTrackingFidelity();
        SetPreviewState($"Starting {camera.Name} ({mode.Label})", null);
        SetStatus($"Opening camera: {camera.Name} ({mode.Label})");

        if (IsDirectX12PreviewEnabled() && TryStartDirectX12NativeCamera(camera, mode))
        {
            _isCameraEnabled = true;
            SetCameraToggle(true);
            SetStatus($"Camera active through native DX12 texture path: {camera.Name} ({mode.Label})");
            _cameraRecoveryPending = false;
            return;
        }

        _directX12PreviewMaxRenderFramesPerSecond = IsDirectX12PreviewEnabled()
            ? GetDirectX12PreviewRenderFramesPerSecond(mode, nativeTexturePath: false)
            : 0d;
        UpdateDirectX12PreviewMode();
        _isCameraEnabled = await _previewService.StartAsync(camera, mode);

        if (!_isCameraEnabled && !mode.IsAuto)
        {
            SetStatus("Selected camera mode failed. Retrying with Auto safe mode...");
            SetPreviewState("Retrying camera with Auto safe mode", null);
            CameraModeComboBox.SelectedItem = CameraVideoMode.Auto;
            _isCameraEnabled = await _previewService.StartAsync(camera, CameraVideoMode.Auto);
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

    private void RestartPreview()
    {
        if (!_isCameraEnabled)
        {
            return;
        }

        StopPreview(keepToggleChecked: true);
        StartPreview();
    }

    private void StopPreview(bool keepToggleChecked = false)
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
            SetCameraToggle(false);
            SetPreviewState("Camera disabled", null);
        }
    }

    private void SetCameraToggle(bool enabled)
    {
        _isUpdatingCameraToggle = true;
        CameraToggle.IsChecked = enabled;
        CameraToggle.Content = enabled ? "Camera On" : "Camera Off";
        _isUpdatingCameraToggle = false;
    }

    private void CameraHealthTimerTick(object? sender, EventArgs e)
    {
        if (_isClosing || !_isCameraEnabled || _cameraRecoveryPending)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var latestActivity = _lastCameraSourceFrameAtUtc == DateTime.MinValue
            ? _cameraStartedAtUtc
            : _lastCameraSourceFrameAtUtc;
        if (latestActivity == DateTime.MinValue || now - latestActivity < CameraStallTimeout)
        {
            return;
        }

        if (_automaticCameraRecoveryAttempts >= 1)
        {
            _cameraRecoveryPending = true;
            StopPreview();
            SetPreviewState("Camera stream stopped", null);
            SetStatus("The camera stopped delivering frames after one recovery attempt. The camera was turned off; close other camera apps, then turn it on again.");
            _cameraRecoveryPending = false;
            return;
        }

        _automaticCameraRecoveryAttempts++;
        _cameraRecoveryPending = true;
        if (_directX12NativeCamera is not null
            && CameraComboBox.SelectedItem is CameraDevice camera)
        {
            var mode = CameraModeComboBox.SelectedItem as CameraVideoMode ?? CameraVideoMode.Auto;
            TextureNativePreviewPolicy.RememberPreviewFailure(
                camera,
                mode,
                "native texture stream stopped delivering frames");
        }

        SetStatus("Camera stream stalled. Retrying once through the safe camera path...");
        StopPreview(keepToggleChecked: true);
        Dispatcher.InvokeAsync(StartPreview, DispatcherPriority.ApplicationIdle);
    }

    private void PreviewFrameAvailable(object? sender, BitmapSource frame)
    {
        lock (_previewFramePumpLock)
        {
            if (_pendingPreviewFrame is not null)
            {
                TrackPreviewFrameReplacement();
            }

            _pendingPreviewFrame = frame;
        }

        QueuePreviewFrameProcessing();
    }

    private void PreviewCameraFrameAvailable(object? sender, CameraFrame frame)
    {
        _lastCameraSourceFrameAtUtc = DateTime.UtcNow;
        _cameraRecoveryPending = false;
        if (!IsDirectX12PreviewEnabled())
        {
            return;
        }

        Direct3D12PreviewHost? host;
        lock (_directX12PreviewLock)
        {
            host = _directX12PreviewHost;
        }

        if (host is null)
        {
            return;
        }

        try
        {
            host.RenderBgraFrame(frame, Interlocked.Increment(ref _directX12FrameNumber));
        }
        catch (Exception ex)
        {
            Dispatcher.InvokeAsync(() => SetStatus($"DX12 preview paused: {ex.Message}"), DispatcherPriority.Background);
        }
    }

    private bool TryStartDirectX12NativeCamera(CameraDevice camera, CameraVideoMode mode)
    {
        if (TextureNativePreviewPolicy.TryGetPreviewFailure(camera, mode, out var cachedFailure))
        {
            SetStatus($"Native DX12 camera path cooling down after a previous failure: {cachedFailure}. Falling back to standard camera path.");
            return false;
        }

        DisposeDirectX12NativeCamera();
        DisposeDirectX12PreviewHost();
        DirectX12PreviewLayer.Children.Clear();
        DirectX12PreviewLayer.Visibility = Visibility.Visible;

        try
        {
            var target = new Dx12Camera.PreviewTarget(
                DirectX12PreviewLayer,
                PreviewImage,
                PreviewPlaceholder,
                PreviewStateText,
                hostInsertIndex: 0,
                name: "Avatar Builder");
            _directX12PreviewMaxRenderFramesPerSecond = GetDirectX12PreviewRenderFramesPerSecond(mode, nativeTexturePath: true);
            var options = new Dx12CameraOptions
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
            SetStatus($"Native DX12 camera path unavailable: {ex.Message}. Falling back to standard camera path.");
            return false;
        }
    }

    private void DirectX12NativeStatusChanged(object? sender, string status)
    {
        Dispatcher.InvokeAsync(() => SetStatus(status), DispatcherPriority.Background);
    }

    private void DirectX12NativeDiagnosticsChanged(object? sender, Direct3D12PreviewDiagnostics diagnostics)
    {
        DirectX12PreviewDiagnosticsChanged(sender, diagnostics);
    }

    private void DirectX12NativeFrameAvailable(object? sender, TextureNativeFrameInfo frame)
    {
        _lastCameraSourceFrameAtUtc = DateTime.UtcNow;
        _cameraRecoveryPending = false;
        if (frame.FrameNumber % 120 != 0)
        {
            return;
        }

        if ((DateTime.UtcNow - _lastDirectX12DiagnosticsAtUtc).TotalSeconds < 6d)
        {
            return;
        }

        Dispatcher.InvokeAsync(
            () => SetStatus($"Native DX12 camera: {frame.Width}x{frame.Height}@{frame.FramesPerSecond:0.###} {frame.MediaSubtype} via {frame.DeviceMode}; preview cap {FormatPreviewRenderLimit()}."),
            DispatcherPriority.Background);
    }

    private void DirectX12NativeTextureFrameAvailable(object? sender, TextureNativeFrameLease frame)
    {
        if (!ShouldAcceptDirectX12AnalysisFrame())
        {
            return;
        }

        TextureNativeFrameLease? analysisFrame = null;
        try
        {
            analysisFrame = frame.DuplicatePreviewData();
            if (analysisFrame is null)
            {
                return;
            }

            QueueDirectX12AnalysisFrame(analysisFrame);
            analysisFrame = null;
        }
        finally
        {
            analysisFrame?.Dispose();
        }
    }

    private void QueueDirectX12AnalysisFrame(TextureNativeFrameLease frame)
    {
        TextureNativeFrameLease? replacedFrame;
        lock (_directX12AnalysisFrameLock)
        {
            replacedFrame = _pendingDirectX12AnalysisFrame;
            _pendingDirectX12AnalysisFrame = frame;
        }

        replacedFrame?.Dispose();
        StartDirectX12AnalysisWorkerIfNeeded();
    }

    private void StartDirectX12AnalysisWorkerIfNeeded()
    {
        if (!_isClosing
            && Interlocked.CompareExchange(ref _directX12AnalysisWorkerQueued, 1, 0) == 0)
        {
            _ = Task.Run(ProcessPendingDirectX12AnalysisFrames);
        }
    }

    private void ProcessPendingDirectX12AnalysisFrames()
    {
        try
        {
            while (!_isClosing)
            {
                TextureNativeFrameLease? frame;
                lock (_directX12AnalysisFrameLock)
                {
                    frame = _pendingDirectX12AnalysisFrame;
                    _pendingDirectX12AnalysisFrame = null;
                }

                if (frame is null)
                {
                    break;
                }

                try
                {
                    if (TryCreateBitmapFromDirectX12TextureFrame(frame, out var bitmap))
                    {
                        PreviewFrameAvailable(this, bitmap);
                    }
                }
                catch (Exception ex)
                {
                    ReportRecoverableVisionError($"DX12 analysis skipped one frame and recovered: {ex.Message}");
                }
                finally
                {
                    frame.Dispose();
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _directX12AnalysisWorkerQueued, 0);
            bool hasPendingFrame;
            lock (_directX12AnalysisFrameLock)
            {
                hasPendingFrame = _pendingDirectX12AnalysisFrame is not null;
            }

            if (hasPendingFrame)
            {
                StartDirectX12AnalysisWorkerIfNeeded();
            }
        }
    }

    private bool ShouldAcceptDirectX12AnalysisFrame()
    {
        var now = DateTime.UtcNow;
        if (now - _lastDirectX12AnalysisFrameAtUtc < _directX12AnalysisFrameInterval)
        {
            return false;
        }

        _lastDirectX12AnalysisFrameAtUtc = now;
        return true;
    }

    private bool TryCreateBitmapFromDirectX12TextureFrame(TextureNativeFrameLease frame, out BitmapSource bitmap)
    {
        bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);

        var maximumWidth = Math.Clamp(_directX12AnalysisMaxOutputWidth, 320, 3840);
        byte[]? bgraBytes = null;
        var bgraStride = 0;
        var bitmapWidth = frame.Width;
        var bitmapHeight = frame.Height;
        if (frame.Nv12PreviewBytes is { Length: > 0 } nv12Bytes
            && frame.Nv12PreviewStride > 0)
        {
            bgraBytes = Nv12FrameConverter.ConvertToBgra(
                nv12Bytes,
                frame.Nv12PreviewStride,
                frame.Width,
                frame.Height,
                maximumWidth,
                out bitmapWidth,
                out bitmapHeight,
                out bgraStride);
        }

        if (bgraBytes is null || bgraBytes.Length == 0 || bgraStride <= 0)
        {
            return false;
        }

        var cameraFrame = new CameraFrame(
            bgraBytes,
            bitmapWidth,
            bitmapHeight,
            bgraStride,
            null,
            0,
            $"{frame.MediaSubtype}-analysis");
        return TryCreateBitmapFromBgraCameraFrame(cameraFrame, out bitmap);
    }

    private bool TryCreateBitmapFromBgraCameraFrame(CameraFrame frame, out BitmapSource bitmap)
    {
        bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);
        if (!frame.HasBgra || frame.Width <= 0 || frame.Height <= 0)
        {
            return false;
        }

        var source = BitmapSource.Create(
            frame.Width,
            frame.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            frame.BgraBytes,
            frame.Stride);
        source.Freeze();

        var maximumWidth = Math.Clamp(_directX12AnalysisMaxOutputWidth, 320, 3840);
        if (frame.Width <= maximumWidth)
        {
            bitmap = source;
            return true;
        }

        var scale = maximumWidth / (double)frame.Width;
        var transformed = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        transformed.Freeze();
        bitmap = transformed;
        return true;
    }

    private bool IsDirectX12PreviewEnabled()
    {
        return _isDirectX12PreviewEnabled;
    }

    private static double GetDirectX12PreviewRenderFramesPerSecond(CameraVideoMode mode, bool nativeTexturePath)
    {
        return 0d;
    }

    private string FormatPreviewRenderLimit()
    {
        return _directX12PreviewMaxRenderFramesPerSecond > 0d
            ? $"{_directX12PreviewMaxRenderFramesPerSecond:0.#} fps"
            : "source fps";
    }

    private void UpdateDirectX12PreviewMode()
    {
        if (IsDirectX12PreviewEnabled())
        {
            if (_directX12NativeCamera is not null)
            {
                DirectX12PreviewLayer.Visibility = Visibility.Visible;
                return;
            }

            if (TryEnsureDirectX12PreviewHost())
            {
                DirectX12PreviewLayer.Visibility = Visibility.Visible;
            }

            return;
        }

        DisposeDirectX12NativeCamera();
        DirectX12PreviewLayer.Visibility = Visibility.Collapsed;
        DisposeDirectX12PreviewHost();
    }

    private bool TryEnsureDirectX12PreviewHost()
    {
        lock (_directX12PreviewLock)
        {
            if (_directX12PreviewHost is not null)
            {
                _directX12PreviewHost.LimitRenderRate(_directX12PreviewMaxRenderFramesPerSecond);
                return true;
            }

            try
            {
                var host = WebcamModule.CreateDirect3D12PreviewHost();
                host.LimitRenderRate(_directX12PreviewMaxRenderFramesPerSecond);
                host.HorizontalAlignment = HorizontalAlignment.Stretch;
                host.VerticalAlignment = VerticalAlignment.Stretch;
                host.StatusChanged += DirectX12PreviewStatusChanged;
                host.DiagnosticsChanged += DirectX12PreviewDiagnosticsChanged;
                DirectX12PreviewLayer.Children.Clear();
                DirectX12PreviewLayer.Children.Add(host);
                _directX12PreviewHost = host;
                Interlocked.Exchange(ref _directX12FrameNumber, 0);
                return true;
            }
            catch (Exception ex)
            {
                DirectX12PreviewLayer.Children.Clear();
                DirectX12PreviewLayer.Visibility = Visibility.Collapsed;
                SetStatus($"DX12 preview unavailable: {ex.Message}");
                Dispatcher.InvokeAsync(() =>
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
        Direct3D12PreviewHost? host;
        lock (_directX12PreviewLock)
        {
            host = _directX12PreviewHost;
            _directX12PreviewHost = null;
            DirectX12PreviewLayer.Children.Clear();
        }

        if (host is null)
        {
            return;
        }

        host.StatusChanged -= DirectX12PreviewStatusChanged;
        host.DiagnosticsChanged -= DirectX12PreviewDiagnosticsChanged;
        host.Dispose();
    }

    private void DisposeDirectX12NativeCamera()
    {
        var camera = _directX12NativeCamera;
        if (camera is null)
        {
            return;
        }

        _directX12NativeCamera = null;
        camera.FrameAvailable -= DirectX12NativeFrameAvailable;
        camera.TextureFrameAvailable -= DirectX12NativeTextureFrameAvailable;
        camera.DiagnosticsChanged -= DirectX12NativeDiagnosticsChanged;
        camera.StatusChanged -= DirectX12NativeStatusChanged;
        camera.Dispose();
        ResetDirectX12AnalysisFramePump();
        DirectX12PreviewLayer.Children.Clear();
        _directX12PreviewMaxRenderFramesPerSecond = 0d;
    }

    private void DirectX12PreviewStatusChanged(object? sender, string status)
    {
        Dispatcher.InvokeAsync(() => SetStatus(status), DispatcherPriority.Background);
    }

    private void DirectX12PreviewDiagnosticsChanged(object? sender, Direct3D12PreviewDiagnostics diagnostics)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastDirectX12DiagnosticsAtUtc).TotalSeconds < 2d)
        {
            return;
        }

        _lastDirectX12DiagnosticsAtUtc = now;
        Dispatcher.InvokeAsync(
            () => SetStatus(FormatDirectX12DiagnosticsStatus(diagnostics)),
            DispatcherPriority.Background);
    }

    private string FormatDirectX12DiagnosticsStatus(Direct3D12PreviewDiagnostics diagnostics)
    {
        var status = diagnostics.FormatStatusLine();
        if (_directX12PreviewMaxRenderFramesPerSecond <= 0d)
        {
            return status;
        }

        return $"{status}; preview cap {FormatPreviewRenderLimit()}";
    }

    private void QueuePreviewFrameProcessing()
    {
        if (Interlocked.Exchange(ref _uiFramePending, 1) != 0)
        {
            return;
        }

        Dispatcher.InvokeAsync(ProcessPendingPreviewFrame, DispatcherPriority.Background);
    }

    private void ProcessPendingPreviewFrame()
    {
        BitmapSource? frame;
        lock (_previewFramePumpLock)
        {
            frame = _pendingPreviewFrame;
            _pendingPreviewFrame = null;
        }

        try
        {
            if (frame is not null)
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

            lock (_previewFramePumpLock)
            {
                frame = _pendingPreviewFrame;
            }

            if (frame is not null)
            {
                QueuePreviewFrameProcessing();
            }
        }
    }

    private void ResetPreviewFramePump()
    {
        lock (_previewFramePumpLock)
        {
            _pendingPreviewFrame = null;
            _previewFramesReplacedSinceWarning = 0;
            _previewReplacementWindowStartedAtUtc = DateTime.MinValue;
        }

        Interlocked.Exchange(ref _uiFramePending, 0);
        Interlocked.Exchange(ref _previewWarningPending, 0);
        ResetFaceFeatureDetectionFramePump();
    }

    private void ResetDirectX12AnalysisFramePump()
    {
        TextureNativeFrameLease? frame;
        lock (_directX12AnalysisFrameLock)
        {
            frame = _pendingDirectX12AnalysisFrame;
            _pendingDirectX12AnalysisFrame = null;
        }

        frame?.Dispose();
    }

    private void ResetFaceFeatureDetectionFramePump()
    {
        lock (_faceFeatureDetectionFrameLock)
        {
            _pendingFaceFeatureDetectionFrame = null;
            _pendingFaceFeatureDetectionCapturedAtUtc = DateTime.MinValue;
            _pendingFaceBoxSystem = _selectedFaceBoxSystem;
            _pendingFaceBoxSystemGeneration = _faceBoxSystemGeneration;
        }

        _lastFaceFeatureDetectionAt = DateTime.MinValue;
    }

    private void TrackPreviewFrameReplacement()
    {
        var now = DateTime.UtcNow;
        if (_previewReplacementWindowStartedAtUtc == DateTime.MinValue)
        {
            _previewReplacementWindowStartedAtUtc = now;
        }

        _previewFramesReplacedSinceWarning++;
        if (_previewFramesReplacedSinceWarning < 50)
        {
            return;
        }

        var elapsed = now - _previewReplacementWindowStartedAtUtc;
        _previewFramesReplacedSinceWarning = 0;
        _previewReplacementWindowStartedAtUtc = now;

        if (elapsed <= TimeSpan.FromSeconds(2))
        {
            QueuePreviewPumpWarning($"Camera preview kept the latest frame and skipped 50 stale frames in {elapsed.TotalSeconds:0.0}s.");
        }
    }

    private void QueuePreviewPumpWarning(string warning)
    {
        if (Interlocked.Exchange(ref _previewWarningPending, 1) != 0)
        {
            return;
        }

        Dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _previewWarningPending, 0);
            SetStatus(warning);
        }, DispatcherPriority.Background);
    }

    private void PreviewStatusChanged(object? sender, string status)
    {
        Dispatcher.InvokeAsync(() => SetStatus(status));
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
        var directX12Enabled = IsDirectX12PreviewEnabled();
        if (frame is null)
        {
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            DirectX12PreviewLayer.Visibility = IsDirectX12PreviewSurfaceActive()
                ? Visibility.Visible
                : Visibility.Collapsed;
            PreviewPlaceholder.Visibility = DirectX12PreviewLayer.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            UpdateFaceCueGuideOverlay(null);
            return;
        }

        if (directX12Enabled && _directX12NativeCamera is not null)
        {
            DirectX12PreviewLayer.Visibility = Visibility.Visible;
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            UpdateFaceCueGuideOverlay(frame as BitmapSource);
            return;
        }

        if (directX12Enabled && TryEnsureDirectX12PreviewHost())
        {
            DirectX12PreviewLayer.Visibility = Visibility.Visible;
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            UpdateFaceCueGuideOverlay(frame as BitmapSource);
            return;
        }

        DirectX12PreviewLayer.Visibility = Visibility.Collapsed;
        PreviewImage.Source = frame;
        PreviewImage.Visibility = Visibility.Visible;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        UpdateFaceCueGuideOverlay(frame as BitmapSource);
    }

    private bool IsDirectX12PreviewSurfaceActive()
    {
        if (!IsDirectX12PreviewEnabled())
        {
            return false;
        }

        if (_directX12NativeCamera is not null)
        {
            return true;
        }

        return GetDirectX12PreviewHost() is not null;
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
            return;
        }

        UpdateFaceCueGuideOverlay(_latestFrame);
    }

    private void SettingChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
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
        return slider.Name is nameof(FaceFieldXSlider)
            or nameof(FaceFieldYSlider)
            or nameof(FaceFieldSizeSlider);
    }

    private void SnapSliderToDefault(Slider slider)
    {
        var (defaultValue, snapDistance) = slider.Name switch
        {
            nameof(FaceFieldXSlider) => (50d, 2d),
            nameof(FaceFieldYSlider) => (48d, 2d),
            nameof(FaceFieldSizeSlider) => (60d, 2d),
            _ => (double.NaN, 0d)
        };

        if (double.IsNaN(defaultValue)
            || Math.Abs(slider.Value - defaultValue) < double.Epsilon
            || Math.Abs(slider.Value - defaultValue) > snapDistance)
        {
            return;
        }

        _isSnappingSlider = true;
        slider.Value = defaultValue;
        _isSnappingSlider = false;
    }

    private void FaceTrackingFieldChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _currentFaceFeatureDetection = FaceFeatureDetection.None;
        ResetLandmarkTracking();
        _activeFaceCueLayout = null;
        _lastFaceFeatureLockAt = DateTime.MinValue;
        MonitorStatusText.Text = "Face tracking field reset. Waiting for a fresh landmark lock.";
    }

    private void LoginLogoutClicked(object sender, RoutedEventArgs e)
    {
        if (IsAvatarUserLoggedIn)
        {
            LogOutAvatarUser(announce: true);
            return;
        }

        try
        {
            var profile = ResolveAvatarLoginSelection(PromptForAvatarLogin(_avatarProfileRegistry));
            if (profile is null)
            {
                SetStatus("Avatar user login canceled. Avatar capture remains stopped.");
                return;
            }

            LogInAvatarProfile(profile, loadModel: true, announce: true);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not log in avatar user: {ex.Message}");
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

        _avatarLearningRequested = !_avatarLearningRequested;
        UpdateAvatarLearningStatusUi();
        SetStatus(_avatarLearningRequested
            ? $"Avatar capture started. 3DDFA owns dense avatar reconstruction; {GetFaceBoxSystemDisplayName()} face tracking stays live for overlays and capture measurements."
            : "Avatar capture stopped.");
    }

    private void UpdateAvatarLearningStatusUi()
    {
        if (!IsLoaded)
        {
            return;
        }

        UpdateAvatarSessionUi();
        var state = GetAvatarLearningState();
        ApplyStartStopButtonState(
            AvatarLearningToggleButton,
            _avatarLearningRequested,
            AvatarLearningStartButtonText,
            AvatarLearningStopButtonText,
            "Starts 3DDFA avatar capture.",
            "Stops 3DDFA avatar capture.");
        AvatarLearningStateText.Text = state.Title;
        AvatarLearningStatusText.Text = state.Detail;
        AvatarLearningIndicator.Background = new SolidColorBrush(state.Accent);

        var trackingSanity = GetAvatarTrackingSanityState();
        AvatarTrackingSanityText.Text = trackingSanity.Detail;
        AvatarTrackingSanityText.Foreground = new SolidColorBrush(trackingSanity.Accent);
        var reconstructionLane = CreateFaceReconstructionLaneStatus();
        AvatarReconstructionLaneText.Text = reconstructionLane.TrustDecision;
        AvatarReconstructionLaneText.Foreground = new SolidColorBrush(ColorForReconstructionLane(reconstructionLane));
        UpdateAvatarCaptureGuidanceUi();
    }

    private FaceReconstructionLaneStatus CreateFaceReconstructionLaneStatus()
    {
        var response = _currentThreeDdfaOnnxResponse;
        var pending = Interlocked.CompareExchange(ref _threeDdfaOnnxReconstructionPending, 0, 0) == 1;
        var canRun = _threeDdfaOnnxEnvironment.IsReady;
        var reconstructionStatus = !IsAvatarUserLoggedIn
            ? "3DDFA/ONNX paused until avatar user login"
            : canRun
            ? pending
                ? "3DDFA/ONNX reconstructing latest avatar frame"
                : !string.IsNullOrWhiteSpace(response.Status) ? response.Status : "3DDFA/ONNX ready; waiting for avatar frame"
            : _threeDdfaOnnxEnvironment.Status;
        var fastTrackingAvailable = IsSelectedFaceBoxSystemAvailable();
        var fastStatus = fastTrackingAvailable
            ? _lastFaceBoxBackendStatus
            : $"{GetFaceBoxSystemDisplayName()} tracking unavailable";
        var trustLevel = !IsAvatarUserLoggedIn
            ? "logged-out"
            : response.Ok && response.HasFace
            ? "cross-checked"
            : canRun ? "3DDFA-ready" : "measurement-only";
        var trustDecision = !IsAvatarUserLoggedIn
            ? $"Avatar reconstruction: logged out; capture stopped. {GetFaceBoxSystemDisplayName()} preview tracking remains live."
            : response.Ok && response.HasFace
            ? $"Avatar reconstruction: 3DDFA lock {response.ReconstructionConfidencePercent:0}% | A/B/C {response.Pose.ARotationAroundXDegrees:0.#}/{response.Pose.BRotationAroundYDegrees:0.#}/{response.Pose.CRotationAroundZDegrees:0.#} deg | dense {response.DenseVertexCount} vertices."
            : canRun
                ? $"Avatar reconstruction: {reconstructionStatus}. {GetFaceBoxSystemDisplayName()} remains live tracking."
                : $"Avatar reconstruction: waiting for 3DDFA/ONNX install. {GetFaceBoxSystemDisplayName()} tracking status: {_threeDdfaOnnxEnvironment.Status}";

        var warnings = new List<string>();
        if (!canRun)
        {
            warnings.Add("3DDFA/ONNX avatar reconstruction is not active yet; avatar output remains measurement-only.");
        }

        warnings.AddRange(response.Warnings);
        return new FaceReconstructionLaneStatus
        {
            CreatedAtUtc = DateTime.UtcNow,
            FastTrackingLaneName = $"{GetFaceBoxSystemDisplayName()} face-box tracking lane",
            FastTrackingAvailable = fastTrackingAvailable,
            FastTrackingHasDenseFace = _currentFaceLandmarkFrame.HasDenseMesh,
            FastTrackingStatus = string.IsNullOrWhiteSpace(fastStatus) ? "fast tracking waiting" : fastStatus,
            AvatarReconstructionManifestPresent = _threeDdfaOnnxModelInfo.ManifestExists,
            AvatarReconstructionModelPresent = _threeDdfaOnnxModelInfo.IsReady,
            AvatarReconstructionCanRunInference = canRun,
            AvatarReconstructionStatus = reconstructionStatus,
            AvatarReconstructionRuntime = _threeDdfaOnnxModelInfo.Runtime,
            AvatarReconstructionModelDirectory = _threeDdfaOnnxModelInfo.ModelDirectory,
            AvatarReconstructionManifestPath = _threeDdfaOnnxModelInfo.ManifestPath,
            AvatarReconstructionModelFiles = _threeDdfaOnnxModelInfo.ModelFiles,
            AvatarReconstructionExpectedOutputs = _threeDdfaOnnxModelInfo.ExpectedOutputs,
            TrustLevel = trustLevel,
            TrustDecision = trustDecision,
            LearningImpact = !IsAvatarUserLoggedIn
                ? "No avatar observations are accepted until a user logs in and starts capture."
                : canRun
                ? _selectedFaceBoxSystem == FaceBoxSystem.ThreeDdfaV2
                    ? "The selected 3DDFA-V2 pass owns both live face-box tracking and avatar reconstruction evidence."
                    : "3DDFA/ONNX runs asynchronously for avatar reconstruction trust and does not block live MediaPipe tracking."
                : $"Live {GetFaceBoxSystemDisplayName()} tracking remains available while avatar reconstruction waits for the 3DDFA/ONNX bundle.",
            Warnings = warnings
        };
    }

    private bool HasStrongThreeDdfaPoseLock()
    {
        var response = _currentThreeDdfaOnnxResponse;
        return response.Ok
            && response.HasFace
            && response.DenseVertexCount >= 30000
            && response.ReconstructionConfidencePercent >= 70d;
    }

    private string FormatThreeDdfaPoseCrossCheck()
    {
        var response = _currentThreeDdfaOnnxResponse;
        return $"3DDFA pose cross-check strong: A/B/C {response.Pose.ARotationAroundXDegrees:0.#}/{response.Pose.BRotationAroundYDegrees:0.#}/{response.Pose.CRotationAroundZDegrees:0.#} deg, dense {response.DenseVertexCount} vertices.";
    }

    private static Color ColorForReconstructionLane(FaceReconstructionLaneStatus lane)
    {
        if (lane.AvatarReconstructionCanRunInference && lane.TrustLevel == "cross-checked")
        {
            return Color.FromRgb(128, 224, 164);
        }

        return lane.AvatarReconstructionCanRunInference
            ? Color.FromRgb(255, 210, 122)
            : Color.FromRgb(185, 215, 239);
    }

    private static void ApplyStartStopButtonState(
        Button button,
        bool isActive,
        string startText,
        string stopText,
        string startToolTip,
        string stopToolTip)
    {
        button.Content = isActive ? stopText : startText;
        button.Background = isActive ? StopActionButtonBackground : StartActionButtonBackground;
        button.BorderBrush = isActive ? StopActionButtonBorder : StartActionButtonBorder;
        button.Foreground = Brushes.White;
        button.ToolTip = isActive ? stopToolTip : startToolTip;
    }

    private AvatarLearningState GetAvatarLearningState()
    {
        if (!IsAvatarUserLoggedIn)
        {
            return new AvatarLearningState(
                false,
                "Avatar capture stopped",
                "Not capturing: use File > Login to identify the person in front of the camera.",
                Color.FromRgb(89, 97, 107));
        }

        if (!_avatarLearningRequested)
        {
            return new AvatarLearningState(
                false,
                "Avatar capture stopped",
                $"Not capturing: click Start Avatar Capture when {CurrentAvatarProfileDisplayName} is present and you want 3DDFA avatar reconstruction samples.",
                Color.FromRgb(89, 97, 107));
        }

        if (!_isCameraEnabled || _latestFrame is null)
        {
            return new AvatarLearningState(
                false,
                "Avatar capture waiting",
                "Not capturing yet: turn the camera on and wait for the face tracker to lock.",
                Color.FromRgb(215, 165, 58));
        }

        if (!_currentFaceLandmarkFrame.HasFace || !_currentFaceLandmarkMetrics.HasFace)
        {
            return new AvatarLearningState(
                false,
                "Avatar capture waiting",
                "Not capturing yet: keep your full face visible until the eye and mouth overlay locks on.",
                Color.FromRgb(215, 165, 58));
        }

        if (_currentAvatarCaptureQuality.CanCollectMeasurements)
        {
            var pending = Interlocked.CompareExchange(ref _threeDdfaOnnxReconstructionPending, 0, 0) == 1;
            var response = _currentThreeDdfaOnnxResponse;
            var reconstruction = response.Ok && response.HasFace
                ? $"3DDFA_V2 ONNX lock {response.ReconstructionConfidencePercent:0}% with {response.DenseVertexCount:n0} dense vertices; A/B/C {response.Pose.ARotationAroundXDegrees:0.#}/{response.Pose.BRotationAroundYDegrees:0.#}/{response.Pose.CRotationAroundZDegrees:0.#} deg."
                : pending
                    ? "3DDFA_V2 ONNX is reconstructing the latest frame."
                    : _threeDdfaOnnxEnvironment.IsReady
                        ? "3DDFA_V2 ONNX is ready and waiting for the next capture frame."
                        : $"3DDFA_V2 ONNX is not ready: {_threeDdfaOnnxEnvironment.Status}";
            return new AvatarLearningState(
                _threeDdfaOnnxEnvironment.IsReady,
                _threeDdfaOnnxEnvironment.IsReady ? "Capturing 3D avatar data" : "Avatar capture waiting",
                $"{reconstruction} {GetFaceBoxSystemDisplayName()} eye/jaw/brow tracking stays live for overlays and capture measurements.",
                _threeDdfaOnnxEnvironment.IsReady ? Color.FromRgb(74, 163, 107) : Color.FromRgb(215, 165, 58));
        }

        var captureFix = _currentAvatarCaptureQuality.Suggestions.FirstOrDefault()
            ?? _currentAvatarCaptureQuality.PrimaryReason
            ?? "Improve face lock, eye visibility, mouth visibility, lighting, or camera mode.";
        return new AvatarLearningState(
            false,
            "Avatar capture waiting",
            $"Not capturing: {_currentAvatarCaptureQuality.PrimaryReason}. Fix: {captureFix}",
            Color.FromRgb(215, 165, 58));
    }

    private AvatarTrackingSanityState GetAvatarTrackingSanityState()
    {
        if (!IsAvatarUserLoggedIn)
        {
            return new AvatarTrackingSanityState(
                $"Tracking sanity: avatar capture is logged out. {GetFaceBoxSystemDisplayName()} preview tracking remains live, but 3DDFA avatar observations are not being collected.",
                Color.FromRgb(185, 215, 239));
        }

        var alignment = _poseAlignmentAuditor.CurrentSummary;
        if (!alignment.ReadyForComparison)
        {
            return new AvatarTrackingSanityState(
                $"Tracking sanity: 3DDFA_V2 ONNX owns avatar A/B/C pose and depth. Pose comparison audit v{alignment.PoseConventionVersion} is gathering diagnostic evidence from {alignment.SampleCount} exact-frame pair(s); it does not block avatar capture. {alignment.Guidance}",
                Color.FromRgb(185, 215, 239));
        }

        if (HasStrongThreeDdfaPoseLock())
        {
            return new AvatarTrackingSanityState(
                $"Tracking sanity: 3DDFA_V2 ONNX owns avatar pose/depth. {GetFaceBoxSystemDisplayName()} eye/jaw/brow tracking remains active for overlays and capture measurements. {FormatThreeDdfaPoseCrossCheck()}",
                Color.FromRgb(128, 224, 164));
        }

        var detail = _threeDdfaOnnxEnvironment.IsReady
            ? $"Tracking sanity: {GetFaceBoxSystemDisplayName()} eye/jaw/brow tracking is live; waiting for a strong 3DDFA_V2 ONNX pose lock before trusting avatar pose/depth."
            : $"Tracking sanity: {GetFaceBoxSystemDisplayName()} face tracking is selected; 3DDFA_V2 ONNX avatar reconstruction is waiting. {_threeDdfaOnnxEnvironment.Status}";
        return new AvatarTrackingSanityState(detail, Color.FromRgb(185, 215, 239));
    }

    private void UpdateAvatarCaptureGuidanceUi()
    {
        if (!IsLoaded)
        {
            return;
        }

        var state = GetAvatarCaptureGuidanceState();
        AvatarCaptureGuidanceTitleText.Text = state.Title;
        AvatarCaptureGuidanceDetailText.Text = state.Detail;
        AvatarCaptureGuidanceTitleText.Foreground = new SolidColorBrush(ColorForAvatarCaptureGuidanceSeverity(state.Severity));
    }

    private AvatarCaptureGuidanceState GetAvatarCaptureGuidanceState()
    {
        var state = AvatarCaptureGuidanceAdvisor.Create(new AvatarCaptureGuidanceInput
        {
            UserLoggedIn = IsAvatarUserLoggedIn,
            AvatarLearningRequested = _avatarLearningRequested,
            CameraActive = _isCameraEnabled && _latestFrame is not null,
            FaceLocked = _currentFaceLandmarkFrame.HasFace && _currentFaceLandmarkMetrics.HasFace,
            CaptureQuality = _currentAvatarCaptureQuality
        });
        return state;
    }

    private static Color ColorForAvatarCaptureGuidanceSeverity(string severity)
    {
        return severity switch
        {
            AvatarCaptureGuidanceSeverity.Good => Color.FromRgb(128, 224, 164),
            AvatarCaptureGuidanceSeverity.Warning => Color.FromRgb(255, 210, 122),
            AvatarCaptureGuidanceSeverity.Blocked => Color.FromRgb(255, 154, 154),
            _ => Color.FromRgb(185, 215, 239)
        };
    }

    private void OpenAvatarSystemClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = GetAvatarDataFolder();
            Directory.CreateDirectory(folder);
            var snapshot = CreateAvatarReportSnapshot(folder);
            QueueAvatarReportSave(snapshot);
            _lastGoodThreeDdfaHtmlPath = LastGoodThreeDdfaStore.GetHtmlPath(folder);
            _avatarModelHtmlPath = AvatarModelStore.GetHtmlPath(folder);
            _avatarSystemDashboardPath = GetAvatarSystemDashboardHtmlPath(folder);
            EnsureAvatarSystemPlaceholder(_avatarSystemDashboardPath);
            OpenLocalFile(_avatarSystemDashboardPath);
            var status = _currentThreeDdfaOnnxResponse is { Ok: true, HasFace: true }
                ? $"Opened live Avatar System: {_avatarSystemDashboardPath}"
                : "Opened live waiting Avatar System. Log in the person at the camera and start avatar capture.";
            MonitorStatusText.Text = status;
            SetStatus(status);
        }
        catch (Exception ex)
        {
            var status = $"Could not open Avatar System: {ex.Message}";
            MonitorStatusText.Text = status;
            SetStatus(status);
        }
    }

    private async void OpenLastGoodThreeDdfaClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = GetAvatarDataFolder();
            Directory.CreateDirectory(folder);
            var currentSamples = _lastGoodThreeDdfaSamples.ToList();
            var subjectId = CurrentAvatarProfileId;
            var subjectDisplayName = CurrentAvatarProfileDisplayName;
            var reconstructionLane = CreateFaceReconstructionLaneStatus();
            SetStatus("Writing 3DDFA Last 5 Dense Reconstructions...");
            var result = await Task.Run(() =>
            {
                lock (_avatarReportStorageLock)
                {
                    var observationSet = _avatarModelObservationStore.Read(folder);
                    var samples = LastGoodThreeDdfaStore.CreateSamples(observationSet, currentSamples);
                    var report = new LastGoodThreeDdfaReport
                    {
                        SubjectId = subjectId,
                        SubjectDisplayName = subjectDisplayName,
                        AvatarModelProgressHtmlPath = AvatarModelStore.GetHtmlPath(folder),
                        ReconstructionLane = reconstructionLane,
                        DenseTopologyEdges = LastGoodThreeDdfaStore.SelectSharedTopology(observationSet, currentSamples),
                        Samples = samples
                    };
                    var htmlPath = _lastGoodThreeDdfaStore.Write(folder, report);
                    return (HtmlPath: htmlPath, SampleCount: samples.Count);
                }
            });
            if (_isClosing)
            {
                return;
            }

            _lastGoodThreeDdfaHtmlPath = result.HtmlPath;
            OpenLocalFile(result.HtmlPath);
            var status = result.SampleCount > 0
                ? $"Opened 3DDFA Last 5 Dense Reconstructions: {result.HtmlPath}"
                : $"Opened 3DDFA Last 5 Dense Reconstructions. Start Avatar Capture and wait for the 3DDFA lane to lock.";
            SetStatus(status);
            MonitorStatusText.Text = status;
        }
        catch (Exception ex)
        {
            var status = $"Could not open 3DDFA Last 5 Dense Reconstructions: {ex.Message}";
            SetStatus(status);
            MonitorStatusText.Text = status;
        }
    }

    private async void OpenAvatarModelProgressClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = GetAvatarDataFolder();
            Directory.CreateDirectory(folder);
            var snapshot = CreateAvatarReportSnapshot(folder);
            SetStatus("Writing Avatar Model Progress...");
            var result = await Task.Run(() => WriteAvatarReports(snapshot));
            if (_isClosing)
            {
                return;
            }

            ApplyAvatarReportSaveResult(result);
            OpenLocalFile(result.AvatarModelHtmlPath);
            var status = $"Opened Avatar Model Progress: {result.AvatarModelHtmlPath}";
            SetStatus(status);
            MonitorStatusText.Text = status;
        }
        catch (Exception ex)
        {
            var status = $"Could not open Avatar Model Progress: {ex.Message}";
            MonitorStatusText.Text = status;
            SetStatus(status);
        }
    }

    private void RebuildAvatarDataClicked(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "Archive the current Avatar review/capture files and start fresh? The old files will be moved to an archive folder, not deleted.",
            "Rebuild Avatar Data?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        if (_avatarReportWriterTask is { IsCompleted: false })
        {
            SetStatus("Avatar report save is still finishing. Try Rebuild Avatar Data again in a moment.");
            return;
        }

        try
        {
            var folder = GetAvatarDataFolder();
            var archivePath = ArchiveCurrentAvatarProfileFolder(folder, DateTime.UtcNow);

            Directory.CreateDirectory(folder);
            ResetAvatarCaptureGate("avatar data rebuilt; capture resumes when Start Avatar Capture is active");
            _currentAvatarCaptureQuality = AvatarCaptureQualityAssessment.Waiting;
            _avatarSystemDashboardPath = "";
            _avatarModelHtmlPath = "";
            _lastGoodThreeDdfaHtmlPath = "";
            _lastGoodThreeDdfaSamples.Clear();
            _avatarLearningRequested = false;
            UpdateAvatarLearningStatusUi();
            SetStatus(string.IsNullOrWhiteSpace(archivePath)
                ? "Avatar data reset. Start Avatar Capture to collect fresh 3DDFA samples."
                : $"Avatar data archived to {archivePath}. Start Avatar Capture to collect fresh 3DDFA samples.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not rebuild Avatar data: {ex.Message}");
        }
    }

    private string ArchiveCurrentAvatarProfileFolder(string folder, DateTime utcNow)
    {
        if (!Directory.Exists(folder))
        {
            return "";
        }

        var archiveRoot = Path.Combine(_outputFolder, AvatarArchiveFolderName, CurrentAvatarProfileId);
        Directory.CreateDirectory(archiveRoot);
        var archivePath = CreateUniqueArchivePath(archiveRoot, utcNow);
        var avatarRoot = _avatarProfileStore.GetRootFolder(_outputFolder);
        if (!IsSameDirectory(folder, avatarRoot))
        {
            Directory.Move(folder, archivePath);
            return archivePath;
        }

        Directory.CreateDirectory(archivePath);
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var fileName = Path.GetFileName(file);
            if (string.Equals(fileName, AvatarProfileStore.RegistryFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Move(file, Path.Combine(archivePath, fileName));
        }

        foreach (var directory in Directory.EnumerateDirectories(folder))
        {
            var directoryName = Path.GetFileName(directory);
            if (string.Equals(directoryName, AvatarProfileStore.PeopleFolderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.Move(directory, Path.Combine(archivePath, directoryName));
        }

        return Directory.EnumerateFileSystemEntries(archivePath).Any() ? archivePath : "";
    }

    private static bool IsSameDirectory(string left, string right)
    {
        var normalizedLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private void ProcessFrame(BitmapSource bitmap)
    {
        UpdateFaceTracking(bitmap);
    }

    private void UpdateFaceTracking(BitmapSource bitmap)
    {
        try
        {
            var layout = GetManualFaceCueLayout();
            var now = DateTime.UtcNow;
            QueueFaceFeatureDetection(bitmap, now);

            if (HasUsableFaceFeatureLock(now) && FaceAutoFollowCheckBox.IsChecked == true)
            {
                var detectedLayout = _currentFaceFeatureDetection.ToGuideLayout(layout);
                var current = _activeFaceCueLayout ?? detectedLayout;
                _activeFaceCueLayout = new FaceCueGuideLayout(
                    current.CenterXPercent + (detectedLayout.CenterXPercent - current.CenterXPercent) * 0.45d,
                    current.CenterYPercent + (detectedLayout.CenterYPercent - current.CenterYPercent) * 0.45d,
                    current.HeightPercent + (detectedLayout.HeightPercent - current.HeightPercent) * 0.30d);
                layout = _activeFaceCueLayout;
            }
            else if (FaceAutoFollowCheckBox.IsChecked == true)
            {
                var current = _activeFaceCueLayout ?? layout;
                if ((now - _lastFaceAutoFollowAt).TotalMilliseconds >= 500d)
                {
                    _activeFaceCueLayout = FaceCueAutoLayoutEstimator.Estimate(bitmap, current);
                    _lastFaceAutoFollowAt = now;
                }

                layout = _activeFaceCueLayout ?? current;
            }
        }
        catch
        {
            MonitorStatusText.Text = "Face tracking is resyncing with the latest camera frame.";
        }
    }

    private void QueueFaceFeatureDetection(BitmapSource bitmap, DateTime now)
    {
        if (_isClosing
            || !IsSelectedFaceBoxSystemAvailable()
            || FaceAutoFollowCheckBox.IsChecked != true
            || now - _lastFaceFeatureDetectionAt < FaceFeatureDetectionTargetInterval)
        {
            return;
        }

        _lastFaceFeatureDetectionAt = now;
        lock (_faceFeatureDetectionFrameLock)
        {
            _pendingFaceFeatureDetectionFrame = bitmap;
            _pendingFaceFeatureDetectionCapturedAtUtc = now;
            _pendingFaceBoxSystem = _selectedFaceBoxSystem;
            _pendingFaceBoxSystemGeneration = _faceBoxSystemGeneration;
        }

        StartFaceFeatureDetectionWorkerIfNeeded();
    }

    private void StartFaceFeatureDetectionWorkerIfNeeded()
    {
        if (!_isClosing
            && Interlocked.CompareExchange(ref _faceFeatureDetectionPending, 1, 0) == 0)
        {
            _ = Task.Run(ProcessPendingFaceFeatureDetectionFramesAsync);
        }
    }

    private async Task ProcessPendingFaceFeatureDetectionFramesAsync()
    {
        try
        {
            while (!_isClosing)
            {
                BitmapSource? bitmap;
                DateTime capturedAtUtc;
                FaceBoxSystem faceBoxSystem;
                int faceBoxSystemGeneration;
                lock (_faceFeatureDetectionFrameLock)
                {
                    bitmap = _pendingFaceFeatureDetectionFrame;
                    capturedAtUtc = _pendingFaceFeatureDetectionCapturedAtUtc;
                    faceBoxSystem = _pendingFaceBoxSystem;
                    faceBoxSystemGeneration = _pendingFaceBoxSystemGeneration;
                    _pendingFaceFeatureDetectionFrame = null;
                    _pendingFaceFeatureDetectionCapturedAtUtc = DateTime.MinValue;
                }

                if (bitmap is null)
                {
                    break;
                }

                try
                {
                    if (faceBoxSystemGeneration != _faceBoxSystemGeneration
                        || faceBoxSystem != _selectedFaceBoxSystem)
                    {
                        continue;
                    }

                    var trackingResult = DetectFaceBox(
                        bitmap,
                        capturedAtUtc == DateTime.MinValue ? DateTime.UtcNow : capturedAtUtc,
                        faceBoxSystem,
                        faceBoxSystemGeneration);
                    await ApplyFaceFeatureDetectionResultAsync(trackingResult);
                }
                catch (Exception ex)
                {
                    ReportRecoverableVisionError($"Landmark tracker skipped one frame and recovered: {ex.Message}");
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _faceFeatureDetectionPending, 0);
            bool hasPendingFrame;
            lock (_faceFeatureDetectionFrameLock)
            {
                hasPendingFrame = _pendingFaceFeatureDetectionFrame is not null;
            }

            if (hasPendingFrame)
            {
                StartFaceFeatureDetectionWorkerIfNeeded();
            }
        }
    }

    private void ReportRecoverableVisionError(string status)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var previousTicks = Interlocked.Read(ref _lastRecoverableVisionErrorStatusTicks);
        if (nowTicks - previousTicks < RecoverableVisionErrorStatusInterval.Ticks
            || Interlocked.CompareExchange(
                ref _lastRecoverableVisionErrorStatusTicks,
                nowTicks,
                previousTicks) != previousTicks)
        {
            return;
        }

        Dispatcher.InvokeAsync(() => SetStatus(status), DispatcherPriority.Background);
    }

    private FaceBoxTrackingFrameResult DetectFaceBox(
        BitmapSource bitmap,
        DateTime capturedAtUtc,
        FaceBoxSystem faceBoxSystem,
        int faceBoxSystemGeneration)
    {
        var stopwatch = Stopwatch.StartNew();
        if (faceBoxSystem == FaceBoxSystem.MediaPipe)
        {
            FaceLandmarkTrackingResult result;
            lock (_faceLandmarkTrackerLock)
            {
                result = _isClosing || _faceLandmarkTracker is null
                    ? FaceLandmarkTrackingResult.None
                    : _faceLandmarkTracker.Detect(bitmap, capturedAtUtc);
            }

            stopwatch.Stop();
            return new FaceBoxTrackingFrameResult(
                faceBoxSystem,
                faceBoxSystemGeneration,
                result,
                null,
                null,
                "",
                0L,
                capturedAtUtc,
                bitmap,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        ThreeDdfaOnnxReconstructionClient? client;
        lock (_threeDdfaClientLock)
        {
            client = _threeDdfaFaceBoxClient;
        }

        ThreeDdfaOnnxSidecarResponse response;
        ThreeDdfaReconstructionSnapshot? snapshot = null;
        var profileId = CurrentAvatarProfileId;
        var sessionGeneration = _avatarUserSession.Generation;
        var requestDenseAvatarSample = IsAvatarUserLoggedIn
            && _avatarLearningRequested
            && _avatarCaptureGateAccepted
            && (capturedAtUtc - _lastThreeDdfaOnnxRequestAtUtc).TotalMilliseconds >= ThreeDdfaDenseSampleIntervalMilliseconds;
        if (requestDenseAvatarSample)
        {
            _lastThreeDdfaOnnxRequestAtUtc = capturedAtUtc;
        }

        Interlocked.Exchange(ref _threeDdfaOnnxReconstructionPending, 1);
        try
        {
            var refreshFaceBoxes = _threeDdfaTrackingFaceBox is null
                || (capturedAtUtc - _lastThreeDdfaFaceBoxesAtUtc).TotalMilliseconds >= 1000d;
            var trackingFaceBox = requestDenseAvatarSample || !refreshFaceBoxes
                ? _threeDdfaTrackingFaceBox
                : null;
            if (trackingFaceBox is null)
            {
                _lastThreeDdfaFaceBoxesAtUtc = capturedAtUtc;
            }

            response = client is null
                ? new ThreeDdfaOnnxSidecarResponse
                {
                    Ok = false,
                    Status = "3DDFA-V2 face-box client stopped",
                    TrustDecision = "The selected 3DDFA-V2 tracking session is no longer active."
                }
                : client.Reconstruct(
                    bitmap,
                    capturedAtUtc,
                    faceBox: trackingFaceBox,
                    mode: requestDenseAvatarSample
                        ? ThreeDdfaOnnxRequestMode.Full
                        : ThreeDdfaOnnxRequestMode.Tracking,
                    denseSampleStride: requestDenseAvatarSample ? 1 : 200);
            if (response.Ok && response.HasFace)
            {
                _threeDdfaTrackingFaceBox = CreateThreeDdfaTrackingFaceBox(
                    response,
                    bitmap.PixelWidth,
                    bitmap.PixelHeight);
            }
            else if (client is not null && trackingFaceBox is not null)
            {
                _visionBenchmarkRecorder.Record(response.Diagnostics);
                _lastThreeDdfaFaceBoxesAtUtc = capturedAtUtc;
                response = client.Reconstruct(
                    bitmap,
                    capturedAtUtc,
                    faceBox: null,
                    mode: ThreeDdfaOnnxRequestMode.Tracking,
                    denseSampleStride: 200);
                if (response.Ok && response.HasFace)
                {
                    _threeDdfaTrackingFaceBox = CreateThreeDdfaTrackingFaceBox(
                        response,
                        bitmap.PixelWidth,
                        bitmap.PixelHeight);
                }
            }

            if (requestDenseAvatarSample)
            {
                snapshot = CreateThreeDdfaLastGoodSnapshot(response, capturedAtUtc);
            }
        }
        catch (Exception ex)
        {
            response = new ThreeDdfaOnnxSidecarResponse
            {
                Ok = false,
                Status = $"3DDFA-V2 face tracking failed: {ex.Message}",
                TrustDecision = "3DDFA-V2 face tracking failed for this frame."
            };
        }
        finally
        {
            Interlocked.Exchange(ref _threeDdfaOnnxReconstructionPending, 0);
            if (faceBoxSystemGeneration != _faceBoxSystemGeneration
                || faceBoxSystem != _selectedFaceBoxSystem)
            {
                client?.Dispose();
            }
        }

        stopwatch.Stop();
        var tracking = ThreeDdfaOnnxFaceTrackingMapper.ToTrackingResult(
            response,
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            capturedAtUtc);
        return new FaceBoxTrackingFrameResult(
            faceBoxSystem,
            faceBoxSystemGeneration,
            tracking,
            response,
            snapshot,
            profileId,
            sessionGeneration,
            capturedAtUtc,
            bitmap,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    private Task ApplyFaceFeatureDetectionResultAsync(FaceBoxTrackingFrameResult trackingResult)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            if (_isClosing
                || trackingResult.FaceBoxSystemGeneration != _faceBoxSystemGeneration
                || trackingResult.FaceBoxSystem != _selectedFaceBoxSystem)
            {
                return;
            }

            var now = DateTime.UtcNow;
            RecordFaceBoxDiagnostics(trackingResult);
            if (trackingResult.ThreeDdfaResponse is not null)
            {
                _currentThreeDdfaOnnxResponse = trackingResult.ThreeDdfaResponse;
                if (trackingResult.ThreeDdfaSnapshot is not null
                    && _avatarUserSession.Matches(trackingResult.ProfileId, trackingResult.SessionGeneration))
                {
                    TrackLastGoodThreeDdfaSnapshot(trackingResult.ThreeDdfaSnapshot);
                }
            }

            var result = trackingResult.TrackingResult;
            var detection = result.FeatureDetection;
            if (detection.HasFace)
            {
                _currentFaceFeatureDetection = detection;
                _lastFaceFeatureLockAt = now;
                var rawLandmarkFrame = result.LandmarkFrame.HasFace
                    ? result.LandmarkFrame
                    : detection.ToLandmarkFrame(now);
                _currentFaceLandmarkFrame = _faceLandmarkReconstructor.Update(rawLandmarkFrame);
                _currentFaceLandmarkMetrics = _faceLandmarkMetricCalculator.Update(_currentFaceLandmarkFrame);
                _currentFaceFrameGeometry = _faceFrameGeometryEstimator.Estimate(new FaceFrameGeometryEstimatorInput
                {
                    Frame = _currentFaceLandmarkFrame,
                    FrameWidthPixels = _latestFrame?.PixelWidth,
                    FrameHeightPixels = _latestFrame?.PixelHeight,
                    Calibration = GetCurrentFaceFrameGeometryCalibration()
                });
                _currentFaceLockStabilityAnalysis = _faceLockStabilityAnalyzer.Update(
                    _currentFaceFeatureDetection,
                    _currentFaceLandmarkFrame,
                    _currentFaceLandmarkMetrics);
                UpdateAvatarCaptureState(now);
                if (_selectedFaceBoxSystem == FaceBoxSystem.MediaPipe)
                {
                    QueueThreeDdfaOnnxReconstruction(
                        trackingResult.SourceFrame,
                        trackingResult.CapturedAtUtc,
                        new PoseAngles(
                            _currentFaceLandmarkFrame.HeadPitchDegrees,
                            _currentFaceLandmarkFrame.HeadYawDegrees,
                            _currentFaceLandmarkFrame.HeadRollDegrees));
                }
            }
            else if (!HasUsableFaceFeatureLock(now))
            {
                _currentFaceFeatureDetection = FaceFeatureDetection.None;
                ResetLandmarkTracking();
            }

            if (_showLiveWireframePreview)
            {
                DrawLiveWireframePreview();
            }

            UpdateFaceCueGuideOverlay(_latestFrame);
        }, DispatcherPriority.Background).Task;
    }

    private void RecordFaceBoxDiagnostics(FaceBoxTrackingFrameResult trackingResult)
    {
        _visionBenchmarkRecorder.Record(trackingResult.TrackingResult.Diagnostics);
        _lastFaceBoxBackendStatus = trackingResult.TrackingResult.BackendStatus;
    }

    private void QueueThreeDdfaOnnxReconstruction(
        BitmapSource? bitmap,
        DateTime capturedAtUtc,
        PoseAngles mediaPipePose)
    {
        if (_isClosing
            || _selectedFaceBoxSystem != FaceBoxSystem.MediaPipe
            || bitmap is null
            || !_threeDdfaOnnxEnvironment.IsReady
            || !IsAvatarUserLoggedIn
            || !_avatarLearningRequested
            || !_avatarCaptureGateAccepted
            || !_currentFaceFeatureDetection.HasFace
            || (capturedAtUtc - _lastThreeDdfaOnnxRequestAtUtc).TotalMilliseconds < ThreeDdfaDenseSampleIntervalMilliseconds)
        {
            return;
        }

        ThreeDdfaOnnxReconstructionClient? client;
        lock (_threeDdfaClientLock)
        {
            client = _threeDdfaAvatarClient;
        }
        if (client is null)
        {
            return;
        }

        _lastThreeDdfaOnnxRequestAtUtc = capturedAtUtc;
        if (Interlocked.Exchange(ref _threeDdfaOnnxReconstructionPending, 1) != 0)
        {
            return;
        }

        var frame = bitmap.IsFrozen ? bitmap : bitmap.Clone();
        if (!frame.IsFrozen && frame.CanFreeze)
        {
            frame.Freeze();
        }

        var faceBox = CreateThreeDdfaFaceBox(_currentFaceFeatureDetection);
        var profileId = CurrentAvatarProfileId;
        var sessionGeneration = _avatarUserSession.Generation;
        var faceBoxSystem = _selectedFaceBoxSystem;
        var faceBoxSystemGeneration = _faceBoxSystemGeneration;
        _ = Task.Run(() => ProcessThreeDdfaOnnxReconstructionAsync(
            client,
            frame,
            capturedAtUtc,
            faceBox,
            profileId,
            sessionGeneration,
            faceBoxSystem,
            faceBoxSystemGeneration,
            mediaPipePose));
    }

    private async Task ProcessThreeDdfaOnnxReconstructionAsync(
        ThreeDdfaOnnxReconstructionClient client,
        BitmapSource bitmap,
        DateTime capturedAtUtc,
        ThreeDdfaOnnxSidecarFaceBox? faceBox,
        string profileId,
        long sessionGeneration,
        FaceBoxSystem faceBoxSystem,
        int faceBoxSystemGeneration,
        PoseAngles mediaPipePose)
    {
        ThreeDdfaOnnxSidecarResponse response;
        ThreeDdfaReconstructionSnapshot? snapshot = null;
        try
        {
            response = client.Reconstruct(
                bitmap,
                capturedAtUtc,
                faceBox,
                mode: ThreeDdfaOnnxRequestMode.Full,
                denseSampleStride: 1);
            _visionBenchmarkRecorder.Record(response.Diagnostics);
            var requestStillOwned = faceBoxSystemGeneration == _faceBoxSystemGeneration
                && faceBoxSystem == _selectedFaceBoxSystem
                && _avatarUserSession.Matches(profileId, sessionGeneration);
            var alignment = response.Ok && response.HasFace && requestStillOwned
                ? _poseAlignmentAuditor.Record(capturedAtUtc, mediaPipePose, response.Pose)
                : _poseAlignmentAuditor.CurrentSummary;
            snapshot = CreateThreeDdfaLastGoodSnapshot(response, capturedAtUtc);
        }
        catch (Exception ex)
        {
            response = new ThreeDdfaOnnxSidecarResponse
            {
                Ok = false,
                Status = $"3DDFA/ONNX reconstruction failed: {ex.Message}",
                TrustDecision = "3DDFA/ONNX failed; do not use this frame for avatar reconstruction trust."
            };
        }
        finally
        {
            Interlocked.Exchange(ref _threeDdfaOnnxReconstructionPending, 0);
            if (faceBoxSystemGeneration != _faceBoxSystemGeneration
                || faceBoxSystem != _selectedFaceBoxSystem)
            {
                client.Dispose();
            }
        }

        await Dispatcher.InvokeAsync(() =>
        {
            if (_isClosing
                || !_avatarUserSession.Matches(profileId, sessionGeneration)
                || faceBoxSystemGeneration != _faceBoxSystemGeneration
                || faceBoxSystem != _selectedFaceBoxSystem)
            {
                return;
            }

            _currentThreeDdfaOnnxResponse = response;
            UpdateAvatarLearningStatusUi();
            TrackLastGoodThreeDdfaSnapshot(snapshot);
            if (_showLiveWireframePreview)
            {
                DrawLiveWireframePreview();
            }
        }, DispatcherPriority.Background);
    }

    private static ThreeDdfaOnnxSidecarFaceBox? CreateThreeDdfaFaceBox(FaceFeatureDetection detection)
    {
        if (!detection.HasFace || detection.FaceBox.Width <= 0d || detection.FaceBox.Height <= 0d)
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
            Confidence = Math.Clamp(detection.TrackingConfidence, 0.01d, 1d)
        };
    }

    private void TrackLastGoodThreeDdfaSnapshot(ThreeDdfaReconstructionSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        var existingIndex = _lastGoodThreeDdfaSamples.FindIndex(
            item => string.Equals(item.RequestId, snapshot.RequestId, StringComparison.Ordinal));
        if (existingIndex >= 0)
        {
            _lastGoodThreeDdfaSamples[existingIndex] = snapshot;
        }
        else
        {
            _lastGoodThreeDdfaSamples.Add(snapshot);
        }

        if (_lastGoodThreeDdfaSamples.Count > LastGoodThreeDdfaRetainedSampleCount)
        {
            _lastGoodThreeDdfaSamples.RemoveRange(0, _lastGoodThreeDdfaSamples.Count - LastGoodThreeDdfaRetainedSampleCount);
        }

    }

    private static ThreeDdfaOnnxSidecarFaceBox? CreateThreeDdfaTrackingFaceBox(
        ThreeDdfaOnnxSidecarResponse response,
        int frameWidth,
        int frameHeight)
    {
        if (!response.Ok || !response.HasFace || response.SparseLandmarks.Count < 20)
        {
            return response.FaceBox;
        }

        var minX = response.SparseLandmarks.Min(static point => point.X);
        var maxX = response.SparseLandmarks.Max(static point => point.X);
        var minY = response.SparseLandmarks.Min(static point => point.Y);
        var maxY = response.SparseLandmarks.Max(static point => point.Y);
        var width = maxX - minX;
        var height = maxY - minY;
        if (width <= 1d || height <= 1d)
        {
            return response.FaceBox;
        }

        return new ThreeDdfaOnnxSidecarFaceBox
        {
            Left = Math.Clamp(minX - width * 0.14d, 0d, Math.Max(0d, frameWidth - 1d)),
            Top = Math.Clamp(minY - height * 0.30d, 0d, Math.Max(0d, frameHeight - 1d)),
            Right = Math.Clamp(maxX + width * 0.14d, 1d, Math.Max(1d, frameWidth)),
            Bottom = Math.Clamp(maxY + height * 0.12d, 1d, Math.Max(1d, frameHeight)),
            Normalized = false,
            Confidence = Math.Clamp(response.ReconstructionConfidencePercent / 100d, 0.01d, 1d)
        };
    }

    private ThreeDdfaReconstructionSnapshot? CreateThreeDdfaLastGoodSnapshot(
        ThreeDdfaOnnxSidecarResponse response,
        DateTime sampleCapturedAtUtc)
    {
        if (!response.Ok
            || !response.HasFace
            || response.DenseVertexCount < 30000
            || response.DenseVertices.Count < 30000)
        {
            return null;
        }

        var topologyEdges = GetOrCreateThreeDdfaTopology(response.DenseEdges);
        if (topologyEdges.Count == 0)
        {
            return null;
        }

        var capturedAtUtc = ParseThreeDdfaCapturedAtUtc(response.CapturedAtUtc);
        if (sampleCapturedAtUtc != default
            && capturedAtUtc != default
            && Math.Abs((capturedAtUtc - sampleCapturedAtUtc).TotalMilliseconds) > 1800d)
        {
            return null;
        }

        var confidencePercent = RoundThreeDdfaValue(response.ReconstructionConfidencePercent);
        return new ThreeDdfaReconstructionSnapshot
        {
            RequestId = response.RequestId,
            CapturedAtUtc = capturedAtUtc == default ? sampleCapturedAtUtc : capturedAtUtc,
            Source = response.Backend,
            DenseVertexCount = response.DenseVertexCount,
            DenseSampleStride = response.DenseSampleStride,
            ReconstructionConfidencePercent = confidencePercent,
            ARotationAroundXDegrees = RoundThreeDdfaValue(response.Pose.ARotationAroundXDegrees),
            BRotationAroundYDegrees = RoundThreeDdfaValue(response.Pose.BRotationAroundYDegrees),
            CRotationAroundZDegrees = RoundThreeDdfaValue(response.Pose.CRotationAroundZDegrees),
            PoseSource = response.Pose.Source,
            TrustDecision = response.TrustDecision,
            Vertices = response.DenseVertices
                .Select(static vertex => new FaceMeshLandmarkPoint
                {
                    Index = vertex.Index,
                    X = RoundThreeDdfaValue(vertex.X),
                    Y = RoundThreeDdfaValue(vertex.Y),
                    Z = RoundThreeDdfaValue(vertex.Z)
                })
                .ToList(),
            CanonicalIdentityVertices = response.CanonicalIdentityVertices
                .Select(static vertex => new FaceMeshLandmarkPoint
                {
                    Index = vertex.Index,
                    X = RoundThreeDdfaValue(vertex.X),
                    Y = RoundThreeDdfaValue(vertex.Y),
                    Z = RoundThreeDdfaValue(vertex.Z)
                })
                .ToList(),
            TopologyEdges = topologyEdges,
            SparseLandmarks = response.SparseLandmarks
                .Select(static vertex => new FaceMeshLandmarkPoint
                {
                    Index = vertex.Index,
                    X = RoundThreeDdfaValue(vertex.X),
                    Y = RoundThreeDdfaValue(vertex.Y),
                    Z = RoundThreeDdfaValue(vertex.Z)
                })
                .ToList(),
            CameraMatrixCoefficients = response.CameraMatrixCoefficients.Select(RoundThreeDdfaValue).ToList(),
            ShapeCoefficients = response.ShapeCoefficients.Select(RoundThreeDdfaValue).ToList(),
            ExpressionCoefficients = response.ExpressionCoefficients.Select(RoundThreeDdfaValue).ToList(),
            Warnings = response.Warnings
        };
    }

    private static double RoundThreeDdfaValue(double value)
    {
        return double.IsFinite(value)
            ? Math.Round(value, 6, MidpointRounding.AwayFromZero)
            : 0d;
    }

    private static DateTime ParseThreeDdfaCapturedAtUtc(string capturedAtUtc)
    {
        return DateTime.TryParse(
                capturedAtUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed)
            ? parsed
            : default;
    }

    private bool HasUsableFaceFeatureLock(DateTime now)
    {
        return _currentFaceFeatureDetection.HasFace
            && (now - _lastFaceFeatureLockAt).TotalSeconds <= 4d;
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

        if (!_avatarLearningRequested)
        {
            ResetAvatarCaptureGate("avatar capture stopped by user");
            UpdateAvatarCaptureQuality();
            UpdateAvatarLearningStatusUi();
            return;
        }

        ResetAvatarCaptureGate("capture-quality preflight", accepted: true);
        var preflightCaptureQuality = AnalyzeAvatarCaptureQuality();
        if (!preflightCaptureQuality.CanCollectMeasurements)
        {
            ResetAvatarCaptureGate($"capture quality gate: {preflightCaptureQuality.PrimaryReason}");
            _currentAvatarCaptureQuality = preflightCaptureQuality;
            UpdateAvatarLearningStatusUi();
            return;
        }

        ResetAvatarCaptureGate(
            $"3DDFA avatar capture active; {GetFaceBoxSystemDisplayName()} face tracking remains live",
            accepted: true);
        _currentAvatarCaptureQuality = preflightCaptureQuality;
        SaveAvatarReportsIfDue(utcNow);
        UpdateAvatarLearningStatusUi();
    }

    private void UpdateAvatarCaptureQuality()
    {
        _currentAvatarCaptureQuality = AnalyzeAvatarCaptureQuality();
    }

    private AvatarCaptureQualityAssessment AnalyzeAvatarCaptureQuality()
    {
        var mode = CameraModeComboBox.SelectedItem as CameraVideoMode;
        return _avatarCaptureQualityAnalyzer.Analyze(new AvatarCaptureQualityInput
        {
            VideoWidth = mode?.Width,
            VideoHeight = mode?.Height,
            FramesPerSecond = mode?.FramesPerSecond,
            InputFormat = mode?.InputFormat,
            IsAutoCameraMode = mode?.IsAuto != false,
            LandmarkFrame = _currentFaceLandmarkFrame,
            Metrics = _currentFaceLandmarkMetrics,
            Stability = _currentFaceLockStabilityAnalysis,
            UserLoggedIn = IsAvatarUserLoggedIn,
            AvatarCaptureRequested = _avatarLearningRequested,
            CaptureGateAccepted = _avatarCaptureGateAccepted,
            CaptureGateReason = _avatarCaptureGateReason
        });
    }

    private void SaveAvatarReportsIfDue(DateTime utcNow)
    {
        if (!_avatarLearningRequested
            || !IsAvatarUserLoggedIn
            || (utcNow - _lastAvatarReportSavedAtUtc).TotalSeconds < AvatarReportSaveIntervalSeconds)
        {
            return;
        }

        try
        {
            var folder = GetAvatarDataFolder();
            QueueAvatarReportSave(CreateAvatarReportSnapshot(folder));
            _lastAvatarReportSavedAtUtc = utcNow;
        }
        catch (Exception ex)
        {
            SetStatus($"Avatar report save paused: {ex.Message}");
        }
    }

    private AvatarReportSnapshot CreateAvatarReportSnapshot(string folder)
    {
        var state = GetAvatarLearningState();
        var userLoggedIn = IsAvatarUserLoggedIn
            && !string.IsNullOrWhiteSpace(CurrentAvatarProfileId);

        return new AvatarReportSnapshot(
            folder,
            CurrentAvatarProfileId,
            CurrentAvatarProfileDisplayName,
            CloneCaptureQuality(_currentAvatarCaptureQuality),
            userLoggedIn,
            _avatarLearningRequested,
            state.Active,
            state.Title,
            state.Detail,
            _currentFaceFrameGeometry,
            _lastGoodThreeDdfaSamples.ToList(),
            CreateFaceReconstructionLaneStatus());
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
            if (_avatarReportWriterTask is { IsCompleted: false })
            {
                return;
            }

            _avatarReportWriterTask = Task.Run(ProcessAvatarReportWriterQueue);
        }
    }

    private void ProcessAvatarReportWriterQueue()
    {
        while (true)
        {
            AvatarReportSnapshot? snapshot;
            lock (_personalFaceReportWriterLock)
            {
                snapshot = _pendingAvatarReportSnapshot;
                _pendingAvatarReportSnapshot = null;
                if (snapshot is null)
                {
                    _avatarReportWriterTask = null;
                    return;
                }
            }

            try
            {
                var result = WriteAvatarReports(snapshot);
                Dispatcher.BeginInvoke(() => ApplyAvatarReportSaveResult(result), DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (!_isClosing)
                    {
                        SetStatus($"Live Avatar System save paused: {ex.Message}");
                    }
                }, DispatcherPriority.Background);
            }
        }
    }

    private AvatarReportSaveResult WriteAvatarReports(AvatarReportSnapshot snapshot)
    {
        lock (_avatarReportStorageLock)
        {
            return WriteAvatarReportsCore(snapshot);
        }
    }

    private AvatarReportSaveResult WriteAvatarReportsCore(AvatarReportSnapshot snapshot)
    {
        Directory.CreateDirectory(snapshot.Folder);

        var observationMerge = _avatarModelObservationStore.MergeAndWrite(
            snapshot.Folder,
            snapshot.SubjectId,
            snapshot.SubjectDisplayName,
            snapshot.LastGoodThreeDdfaSamples);
        var avatarModelObservations = observationMerge.ObservationSet;
        var lastGoodSamples = LastGoodThreeDdfaStore.CreateSamples(avatarModelObservations);
        var lastGoodHtmlPath = LastGoodThreeDdfaStore.GetHtmlPath(snapshot.Folder);
        if (observationMerge.Changed || !File.Exists(lastGoodHtmlPath))
        {
            lastGoodHtmlPath = _lastGoodThreeDdfaStore.Write(
                snapshot.Folder,
                new LastGoodThreeDdfaReport
                {
                    SubjectId = snapshot.SubjectId,
                    SubjectDisplayName = snapshot.SubjectDisplayName,
                    AvatarModelProgressHtmlPath = AvatarModelStore.GetHtmlPath(snapshot.Folder),
                    ReconstructionLane = snapshot.ReconstructionLane,
                    DenseTopologyEdges = LastGoodThreeDdfaStore.SelectSharedTopology(avatarModelObservations),
                    Samples = lastGoodSamples
                });
        }

        var storedAvatarModel = _avatarModelStore.Read(snapshot.Folder);
        var avatarModel = storedAvatarModel;
        AvatarModelHistoryReport avatarModelHistory;
        if (observationMerge.Changed
            || avatarModel is null
            || !string.Equals(
                avatarModel.SchemaVersion,
                AvatarModel.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            avatarModel = AvatarModelBuilder.Build(avatarModelObservations);
            var comparablePreviousModel = storedAvatarModel is not null
                && string.Equals(storedAvatarModel.SchemaVersion, AvatarModel.CurrentSchemaVersion, StringComparison.Ordinal)
                    ? storedAvatarModel
                    : null;
            avatarModelHistory = _avatarModelHistoryStore.RecordAndWrite(
                snapshot.Folder,
                avatarModelObservations,
                avatarModel,
                comparablePreviousModel);
            _avatarModelStore.Write(snapshot.Folder, avatarModel);
        }
        else
        {
            avatarModelHistory = _avatarModelHistoryStore.ReadReport(snapshot.Folder);
            _avatarModelStore.EnsureViewer(snapshot.Folder, avatarModel);
        }

        var dashboard = new AvatarSystemDashboard
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
            FastTrackingSummary = $"{snapshot.ReconstructionLane.FastTrackingLaneName} supplies live face, eye, jaw, brow, mouth, overlay, and capture measurements.",
            LastGoodThreeDdfaSampleCount = lastGoodSamples.Count,
            LastGoodThreeDdfaHtmlPath = lastGoodHtmlPath,
            AvatarModelStatus = avatarModel.Status,
            AvatarModelObservationCount = avatarModelObservations.Observations.Count,
            AvatarModelConfidencePercent = avatarModel.Identity.ConfidencePercent,
            AvatarModelCoveragePercent = avatarModel.PoseCoverage.CoveragePercent,
            AvatarModelCoverageSummary = avatarModel.PoseCoverage.Summary,
            AvatarModelHtmlPath = AvatarModelStore.GetHtmlPath(snapshot.Folder),
            AvatarModelAuditStatus = avatarModelHistory.Latest.Status,
            AvatarModelAuditSummary = avatarModelHistory.Latest.Summary,
            AvatarModelAuditHtmlPath = AvatarModelHistoryStore.GetHtmlPath(snapshot.Folder)
        };

        var dashboardJsonPath = _avatarSystemDashboardStore.Write(snapshot.Folder, dashboard);
        return new AvatarReportSaveResult(
            lastGoodHtmlPath,
            AvatarSystemDashboardStore.GetHtmlPath(dashboardJsonPath),
            AvatarModelStore.GetHtmlPath(snapshot.Folder));
    }

    private void ApplyAvatarReportSaveResult(AvatarReportSaveResult result)
    {
        if (_isClosing)
        {
            return;
        }

        _lastGoodThreeDdfaHtmlPath = result.LastGoodThreeDdfaHtmlPath;
        _avatarSystemDashboardPath = result.AvatarSystemDashboardPath;
        _avatarModelHtmlPath = result.AvatarModelHtmlPath;
        UpdateAvatarLearningStatusUi();
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

    private static string GetAvatarSystemDashboardHtmlPath(string folder)
    {
        var dashboardJsonPath = Path.Combine(folder, AvatarSystemDashboardStore.DefaultJsonFileName);
        return AvatarSystemDashboardStore.GetHtmlPath(dashboardJsonPath);
    }

    private static void EnsureAvatarSystemPlaceholder(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        var html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta http-equiv="refresh" content="2">
<title>Avatar System</title>
<style>
:root{color-scheme:dark}body{margin:0;background:#080d12;color:#f5f8fb;font-family:Segoe UI,Arial,sans-serif}main{max-width:860px;margin:0 auto;padding:28px}section{border:1px solid #243545;background:#101820;padding:18px}.muted{color:#b9d7ef}
</style>
</head>
<body>
<main>
<section>
<h1>Avatar System</h1>
<p class="muted">Preparing the live report. This page refreshes automatically while the background writer saves the latest measurement snapshot.</p>
</section>
</main>
</body>
</html>
""";
        AtomicTextFileWriter.WriteAllText(path, html, Encoding.UTF8);
    }

    private void OpenDataFolderClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new AvatarDataFolderDialog(_outputFolder)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var selectedFolder = Path.GetFullPath(dialog.SelectedFolder);
        if (string.Equals(selectedFolder, Path.GetFullPath(_outputFolder), StringComparison.OrdinalIgnoreCase))
        {
            SetStatus($"Avatar data folder unchanged: {_outputFolder}");
            return;
        }

        var userWasLoggedIn = IsAvatarUserLoggedIn;
        if (userWasLoggedIn)
        {
            LogOutAvatarUser(announce: false);
        }

        _outputFolder = selectedFolder;
        _visionBenchmarkRecorder.SetOutputRoot(_outputFolder);
        SaveOutputFolderPointer(_outputFolder);
        InitializeAvatarProfiles(promptForStartupUser: false);
        _poseAlignmentAuditor.SetOutputRoot(GetAvatarDataFolder());
        var avatarStatus = PrepareAvatarCaptureFolder(showStatus: false);
        var loginStatus = userWasLoggedIn ? " The previous avatar user was logged out." : "";
        var status = $"Avatar data folder set: {_outputFolder}.{loginStatus} {avatarStatus}".Trim();
        MonitorStatusText.Text = status;
        SetStatus(status);
    }

    private void CloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OpenPoseAlignmentAuditClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            _poseAlignmentAuditor.EnsureReport();
            var path = _poseAlignmentAuditor.GetHtmlPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                SetStatus("A/B/C alignment audit is waiting for a configured data folder.");
                return;
            }

            OpenLocalFile(path);
            SetStatus($"Opened A/B/C alignment audit: {path}");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open A/B/C alignment audit: {ex.Message}");
        }
    }

    private FaceCueGuideLayout GetFaceCueLayout()
    {
        if (FaceAutoFollowCheckBox.IsChecked == true && _activeFaceCueLayout is not null)
        {
            return _activeFaceCueLayout;
        }

        return GetManualFaceCueLayout();
    }

    private FaceCueGuideLayout GetManualFaceCueLayout()
    {
        return new FaceCueGuideLayout(
            Math.Clamp(FaceFieldXSlider.Value, 20d, 80d),
            Math.Clamp(FaceFieldYSlider.Value, 20d, 80d),
            Math.Clamp(FaceFieldSizeSlider.Value, 25d, 90d));
    }

    private void UpdateSettingLabels()
    {
        FaceFieldXValueText.Text = $"{FaceFieldXSlider.Value:0}%";
        FaceFieldYValueText.Text = $"{FaceFieldYSlider.Value:0}%";
        FaceFieldSizeValueText.Text = $"{FaceFieldSizeSlider.Value:0}%";
    }

    private static string ResolveInitialOutputFolder(string requestedOutputFolder = "")
    {
        if (TryResolveRequestedOutputFolder(requestedOutputFolder, out var requested))
        {
            return requested;
        }

        var saved = LoadOutputFolderPointer();
        if (!string.IsNullOrWhiteSpace(saved))
        {
            return saved;
        }

        var preferredRoot = Path.GetPathRoot(PreferredExternalOutputFolder);
        if (!string.IsNullOrWhiteSpace(preferredRoot) && Directory.Exists(preferredRoot))
        {
            return PreferredExternalOutputFolder;
        }

        return Path.Combine(AppContext.BaseDirectory, "AvatarBuilderSessions");
    }

    private void EnsureOutputFolderConfiguredForLaunch()
    {
        if (TryResolveRequestedOutputFolder(_startupOptions.OutputFolder, out var requested))
        {
            _outputFolder = requested;
            Directory.CreateDirectory(_outputFolder);
            SaveOutputFolderPointer(_outputFolder);
            return;
        }

        var configured = LoadOutputFolderPointer();
        if (IsExistingFolder(configured))
        {
            _outputFolder = configured;
            return;
        }

        EnsureOutputFolderPointerFileExists();
        var reason = string.IsNullOrWhiteSpace(configured)
            ? "Avatar Builder needs a storage folder for avatar profiles, reconstruction observations, models, and review reports."
            : $"The configured storage folder was not found:{Environment.NewLine}{configured}{Environment.NewLine}{Environment.NewLine}Choose where Avatar Builder should store its data.";
        var selected = PromptForOutputFolder(reason, configured);
        if (string.IsNullOrWhiteSpace(selected))
        {
            selected = ResolveFallbackOutputFolder();
        }

        Directory.CreateDirectory(selected);
        _outputFolder = selected;
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
            var expanded = Environment.ExpandEnvironmentVariables(requestedOutputFolder.Trim().Trim('"'));
            var fullPath = Path.GetFullPath(expanded);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
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
            var path = GetOutputFolderPointerPath();
            if (!File.Exists(path))
            {
                return "";
            }

            return File.ReadLines(path, Encoding.UTF8)
                .FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line))
                ?.Trim()
                .Trim('"') ?? "";
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
            var path = GetOutputFolderPointerPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
            File.WriteAllText(path, (outputFolder ?? "").Trim() + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Output folder persistence should never interrupt monitoring.
        }
    }

    private static void EnsureOutputFolderPointerFileExists()
    {
        try
        {
            var path = GetOutputFolderPointerPath();
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
                File.WriteAllText(path, "", Encoding.UTF8);
            }
        }
        catch
        {
            // The folder picker can still run even if the pointer file could not be pre-created.
        }
    }

    private static string GetOutputFolderPointerPath()
    {
        return Path.Combine(AppContext.BaseDirectory, OutputFolderPointerFileName);
    }

    private static bool IsExistingFolder(string folder)
    {
        return !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder);
    }

    private string PromptForOutputFolder(string message, string configuredFolder)
    {
        MessageBox.Show(
            this,
            $"{message}{Environment.NewLine}{Environment.NewLine}The selected folder path will be saved in:{Environment.NewLine}{GetOutputFolderPointerPath()}",
            "Choose Avatar Builder Storage",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        var dialog = new OpenFolderDialog
        {
            Title = "Choose Avatar Builder storage folder",
            InitialDirectory = GetOutputFolderPickerInitialDirectory(configuredFolder),
            Multiselect = false
        };

        return dialog.ShowDialog(this) == true ? dialog.FolderName : "";
    }

    private static string GetOutputFolderPickerInitialDirectory(string configuredFolder)
    {
        if (IsExistingFolder(configuredFolder))
        {
            return configuredFolder;
        }

        if (Directory.Exists(PreferredExternalOutputFolder))
        {
            return PreferredExternalOutputFolder;
        }

        var preferredRoot = Path.GetPathRoot(PreferredExternalOutputFolder);
        if (!string.IsNullOrWhiteSpace(preferredRoot) && Directory.Exists(preferredRoot))
        {
            return preferredRoot;
        }

        return AppContext.BaseDirectory;
    }

    private static string ResolveFallbackOutputFolder()
    {
        var preferredRoot = Path.GetPathRoot(PreferredExternalOutputFolder);
        if (!string.IsNullOrWhiteSpace(preferredRoot) && Directory.Exists(preferredRoot))
        {
            return PreferredExternalOutputFolder;
        }

        return Path.Combine(AppContext.BaseDirectory, "AvatarBuilderSessions");
    }

    private string PrepareAvatarCaptureFolder(bool showStatus)
    {
        var folder = GetAvatarDataFolder();
        try
        {
            Directory.CreateDirectory(folder);
            ResetAvatarCaptureGate("avatar capture folder ready; 3DDFA_V2 ONNX owns avatar reconstruction");
            _avatarSystemDashboardPath = GetAvatarSystemDashboardHtmlPath(folder);
            _avatarModelHtmlPath = AvatarModelStore.GetHtmlPath(folder);
            QueueAvatarReportSave(CreateAvatarReportSnapshot(folder));
            UpdateAvatarCaptureQuality();
            UpdateAvatarLearningStatusUi();

            var status = $"Avatar capture folder ready: {folder}. 3DDFA_V2 ONNX stores dense reconstruction review data; {GetFaceBoxSystemDisplayName()} eye/jaw/brow tracking remains live.";
            if (showStatus)
            {
                MonitorStatusText.Text = status;
            }

            return status;
        }
        catch (Exception ex)
        {
            var status = $"Could not prepare Avatar capture folder: {ex.Message}";
            if (showStatus)
            {
                MonitorStatusText.Text = status;
            }

            return status;
        }
    }

    private string GetAvatarDataFolder()
    {
        return _avatarProfileStore.GetProfileFolder(_outputFolder, _currentAvatarProfile);
    }

    private static string CreateUniqueArchivePath(string archiveRoot, DateTime utcNow)
    {
        var basePath = Path.Combine(archiveRoot, $"avatar-data-{utcNow:yyyyMMddTHHmmssZ}");
        var path = basePath;
        for (var index = 2; Directory.Exists(path); index++)
        {
            path = $"{basePath}-{index}";
        }

        return path;
    }

    private string GetSelectedCameraName()
    {
        return CameraComboBox.SelectedItem is CameraDevice camera ? camera.Name : "";
    }

    private FaceFrameGeometryCalibration GetCurrentFaceFrameGeometryCalibration()
    {
        var cameraName = GetSelectedCameraName();
        if (cameraName.Contains("Insta360 Link 2 Pro", StringComparison.OrdinalIgnoreCase))
        {
            return new FaceFrameGeometryCalibration
            {
                CameraHorizontalFovDegrees = Insta360Link2ProHorizontalFovDegrees,
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
        if (LiveWireframeCanvas is null)
        {
            return;
        }

        LiveWireframeCanvas.Children.Clear();
        var width = Math.Max(1d, LiveWireframeCanvas.ActualWidth);
        var height = Math.Max(1d, LiveWireframeCanvas.ActualHeight);
        var frame = _currentFaceLandmarkFrame;
        if (!frame.HasDenseMesh)
        {
            AddWireframeText(
                "Live wireframe waiting",
                "Turn on the camera and wait for MediaPipe dense face lock.",
                18,
                18);
            return;
        }

        DrawMediaPipeLiveWireframeView(frame, _currentFaceLandmarkMetrics, new Rect(0d, 0d, width, height), "Live wireframe");
    }

    private void DrawMediaPipeLiveWireframeView(
        FaceLandmarkFrame frame,
        FaceLandmarkMetrics metrics,
        Rect rect,
        string title)
    {
        var pointMap = BuildCameraRelativeLiveWireframeProjection(frame.DenseMeshPoints, rect);
        var surfaceBrush = CreateFrozenBrush(0x2f, 0x6c, 0x8f, 0x58);
        foreach (var (fromIndex, toIndex) in MediaPipeFaceMeshTopology.TessellationEdges)
        {
            DrawWireframeEdge(fromIndex, toIndex, pointMap, surfaceBrush, 0.42d);
        }

        DrawWireframePath(DenseMeshEyeA, true, pointMap, "eye");
        DrawWireframePath(DenseMeshEyeB, true, pointMap, "eye");
        DrawWireframePath(DenseMeshBrowA, false, pointMap, "brow");
        DrawWireframePath(DenseMeshBrowB, false, pointMap, "brow");
        DrawWireframePath(DenseMeshOuterLip, true, pointMap, "mouth");
        DrawWireframePath(DenseMeshInnerLip, true, pointMap, "mouth-opening");
        DrawWireframePath(DenseMeshJawContour, false, pointMap, "jaw");
        DrawWireframePath(DenseMeshNoseBridge, false, pointMap, "nose");
        DrawWireframePath(DenseMeshNoseBase, false, pointMap, "nose");
        DrawWireframePath(DenseMeshFaceOval, true, pointMap, "face");

        var featureIndexes = DenseMeshEyeA
            .Concat(DenseMeshEyeB)
            .Concat(DenseMeshBrowA)
            .Concat(DenseMeshBrowB)
            .Concat(DenseMeshOuterLip)
            .Concat(DenseMeshInnerLip)
            .Concat(DenseMeshJawContour)
            .Concat(DenseMeshNoseBridge)
            .Concat(DenseMeshNoseBase)
            .Concat(DenseMeshFaceOval)
            .ToHashSet();
        var pointBrush = CreateFrozenBrush(0xdc, 0xef, 0xff, 0xb8);
        foreach (var point in frame.DenseMeshPoints)
        {
            if (!pointMap.TryGetValue(point.Index, out var projectedPoint))
            {
                continue;
            }

            var dotSize = featureIndexes.Contains(point.Index) ? 3.2d : 2.0d;
            var ellipse = new Ellipse
            {
                Width = dotSize,
                Height = dotSize,
                Fill = pointBrush,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(ellipse, projectedPoint.X - dotSize / 2d);
            Canvas.SetTop(ellipse, projectedPoint.Y - dotSize / 2d);
            LiveWireframeCanvas.Children.Add(ellipse);
        }

        var response = _currentThreeDdfaOnnxResponse;
        var poseSummary = response is { Ok: true, HasFace: true }
            ? $"3DDFA A/B/C {response.Pose.ARotationAroundXDegrees:0.#}/{response.Pose.BRotationAroundYDegrees:0.#}/{response.Pose.CRotationAroundZDegrees:0.#} deg"
            : "3DDFA pose waiting";
        AddWireframeText(
            $"{title}: {frame.DenseMeshPoints.Count} points, {MediaPipeFaceMeshTopology.TessellationEdges.Length} surface edges",
            $"Camera-relative MediaPipe wireframe. Quality {metrics.OverallMeasurementQualityPercent:0}% | eyes {metrics.EyeMeasurementQualityPercent:0}% | brows {metrics.BrowMeasurementQualityPercent:0}% ({FormatRatioPercent(metrics.AverageBrowHeightRatio)}) | mouth {metrics.MouthMeasurementQualityPercent:0}% | {poseSummary}",
            rect.X + 18,
            rect.Y + 18);
    }

    private void DrawWireframeEdge(
        int fromIndex,
        int toIndex,
        IReadOnlyDictionary<int, LiveWireframeProjectedPoint> points,
        Brush brush,
        double thickness)
    {
        if (!points.TryGetValue(fromIndex, out var from)
            || !points.TryGetValue(toIndex, out var to))
        {
            return;
        }

        var line = new Line
        {
            X1 = from.X,
            Y1 = from.Y,
            X2 = to.X,
            Y2 = to.Y,
            Stroke = brush,
            StrokeThickness = thickness,
            IsHitTestVisible = false
        };
        LiveWireframeCanvas.Children.Add(line);
    }

    private void DrawWireframePath(
        IReadOnlyList<int> indices,
        bool closed,
        IReadOnlyDictionary<int, LiveWireframeProjectedPoint> points,
        string role)
    {
        var brush = BrushForWireframeRole(role);
        for (var index = 1; index < indices.Count; index++)
        {
            DrawWireframeEdge(indices[index - 1], indices[index], points, brush, 1.75d);
        }

        if (closed && indices.Count > 2)
        {
            DrawWireframeEdge(indices[^1], indices[0], points, brush, 1.75d);
        }
    }

    private static IReadOnlyDictionary<int, LiveWireframeProjectedPoint> BuildCameraRelativeLiveWireframeProjection(
        IReadOnlyList<FaceMeshLandmarkPoint> denseMeshPoints,
        Rect rect)
    {
        return denseMeshPoints.ToDictionary(
            static point => point.Index,
            point => new LiveWireframeProjectedPoint(
                point.Index,
                rect.X + Math.Clamp(point.X, 0d, 1d) * rect.Width,
                rect.Y + Math.Clamp(point.Y, 0d, 1d) * rect.Height,
                point.Z));
    }

    private void AddWireframeText(string title, string detail, double left, double top)
    {
        var panel = new StackPanel
        {
            Background = CreateFrozenBrush(0x08, 0x0d, 0x12, 0xdc),
            IsHitTestVisible = false
        };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(10, 8, 10, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = new SolidColorBrush(Color.FromRgb(185, 215, 239)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 4, 10, 8),
            MaxWidth = 760
        });
        Canvas.SetLeft(panel, left);
        Canvas.SetTop(panel, top);
        LiveWireframeCanvas.Children.Add(panel);
    }

    private static SolidColorBrush BrushForWireframeRole(string role)
    {
        return role switch
        {
            "eye" => CreateFrozenBrush(0x8f, 0xf2, 0xc5, 0xf2),
            "brow" => CreateFrozenBrush(0xc9, 0xf7, 0xa3, 0xf2),
            "mouth" or "mouth-opening" => CreateFrozenBrush(0xff, 0x9f, 0xbd, 0xf2),
            "jaw" => CreateFrozenBrush(0xff, 0xd1, 0x66, 0xf2),
            "nose" => CreateFrozenBrush(0xd9, 0xe8, 0xff, 0xf2),
            "cheek" => CreateFrozenBrush(0xc7, 0xa6, 0xff, 0xf2),
            "forehead" => CreateFrozenBrush(0x9d, 0xb7, 0xc9, 0xf2),
            "face" => CreateFrozenBrush(0x65, 0xc8, 0xff, 0xf2),
            _ => CreateFrozenBrush(0xdc, 0xef, 0xff, 0xe0)
        };
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
            // HwndHost owns native airspace, so every DX12 route composites
            // tracking inside the swap chain instead of using this WPF canvas.
            UpdateDirectX12TrackingOverlay(CreateNativePreviewTrackingOverlay());
            FaceCueGuideCanvas.Visibility = Visibility.Collapsed;
            return;
        }

        FaceCueGuideCanvas.Visibility = Visibility.Visible;
        if (bitmap is null)
        {
            return;
        }

        var display = GetPreviewDisplayRect(bitmap);
        if (display.Width <= 0d || display.Height <= 0d)
        {
            return;
        }

        var accent = GetFaceCueGuideColor();
        var regionBrush = new SolidColorBrush(Color.FromArgb(34, accent.R, accent.G, accent.B));
        var lineBrush = new SolidColorBrush(Color.FromArgb(235, accent.R, accent.G, accent.B));
        var supportBrush = new SolidColorBrush(Color.FromArgb(175, 185, 215, 239));
        var regionPen = new SolidColorBrush(Color.FromArgb(150, accent.R, accent.G, accent.B));
        var layout = GetFaceCueLayout();
        var face = layout.GetFaceBox();
        var leftEye = layout.ToFrameRect(layout.LeftEye);
        var rightEye = layout.ToFrameRect(layout.RightEye);
        var jaw = layout.ToFrameRect(layout.Jaw);

        AddGuideRegion(display, face, Brushes.Transparent, supportBrush, 1d);
        AddGuideRegion(display, leftEye, regionBrush, regionPen, 2d);
        AddGuideRegion(display, rightEye, regionBrush, regionPen, 2d);
        AddGuideRegion(display, jaw, regionBrush, regionPen, 2d);

        AddGuideLine(display, leftEye.Left, leftEye.Top + leftEye.Height * 0.50d, leftEye.Right, leftEye.Top + leftEye.Height * 0.50d, lineBrush, 3d);
        AddGuideLine(display, rightEye.Left, rightEye.Top + rightEye.Height * 0.50d, rightEye.Right, rightEye.Top + rightEye.Height * 0.50d, lineBrush, 3d);
        AddGuideLine(display, jaw.Left + jaw.Width * 0.16d, jaw.Top + jaw.Height * 0.38d, jaw.Right - jaw.Width * 0.16d, jaw.Top + jaw.Height * 0.38d, lineBrush, 3d);
        AddGuideLine(display, face.Left + face.Width * 0.50d, face.Top, face.Left + face.Width * 0.50d, face.Bottom, supportBrush, 1d);

        if (HasUsableFaceFeatureLock(DateTime.UtcNow))
        {
            var detectorBrush = new SolidColorBrush(Color.FromArgb(230, 244, 211, 94));
            AddGuideRegion(display, _currentFaceFeatureDetection.FaceBox, Brushes.Transparent, detectorBrush, 2d);
            if (_currentFaceFeatureDetection.LeftEyeBox is Rect leftEyeBox)
            {
                AddGuideRegion(display, leftEyeBox, Brushes.Transparent, detectorBrush, 2d);
            }

            if (_currentFaceFeatureDetection.RightEyeBox is Rect rightEyeBox)
            {
                AddGuideRegion(display, rightEyeBox, Brushes.Transparent, detectorBrush, 2d);
            }

            if (_currentFaceFeatureDetection.MouthBox is Rect mouthBox)
            {
                AddGuideRegion(display, mouthBox, Brushes.Transparent, detectorBrush, 2d);
            }
        }

        if (_currentFaceLandmarkFrame.HasFace)
        {
            AddLandmarkContours(display, _currentFaceLandmarkFrame);
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

        var frame = _currentFaceLandmarkFrame;
        var inferredEyes = frame.EyeArtifactSuppressed;
        return new PreviewTrackingOverlay
        {
            FaceBox = ToPreviewOverlayRect(_currentFaceFeatureDetection.FaceBox),
            LeftEyeBox = ToPreviewOverlayRect(_currentFaceFeatureDetection.LeftEyeBox),
            RightEyeBox = ToPreviewOverlayRect(_currentFaceFeatureDetection.RightEyeBox),
            MouthBox = ToPreviewOverlayRect(_currentFaceFeatureDetection.MouthBox),
            FaceContour = ToPreviewOverlayPolyline(frame.FaceContour, closed: true),
            JawContour = ToPreviewOverlayPolyline(frame.JawContour, closed: false),
            LeftEyeContour = ToPreviewOverlayPolyline(
                frame.LeftEyeContour,
                closed: true,
                inferred: frame.LeftEyeReconstructed || inferredEyes),
            RightEyeContour = ToPreviewOverlayPolyline(
                frame.RightEyeContour,
                closed: true,
                inferred: frame.RightEyeReconstructed || inferredEyes),
            LeftBrowContour = ToPreviewOverlayPolyline(frame.LeftBrowContour, closed: false),
            RightBrowContour = ToPreviewOverlayPolyline(frame.RightBrowContour, closed: false),
            OuterLipContour = ToPreviewOverlayPolyline(
                frame.OuterLipContour,
                closed: true,
                inferred: frame.MouthReconstructed),
            InnerLipContour = ToPreviewOverlayPolyline(
                frame.InnerLipContour,
                closed: true,
                inferred: frame.MouthReconstructed)
        };
    }

    private List<MeshTopologyEdge> GetOrCreateThreeDdfaTopology(
        IReadOnlyList<ThreeDdfaOnnxSidecarEdge> source)
    {
        lock (_threeDdfaTopologyLock)
        {
            if (_threeDdfaDenseTopologyEdges.Count >= source.Count
                && _threeDdfaDenseTopologyEdges.Count > 0)
            {
                return _threeDdfaDenseTopologyEdges;
            }

            _threeDdfaDenseTopologyEdges = source
                .Select(static edge => new MeshTopologyEdge
                {
                    FromIndex = edge.FromIndex,
                    ToIndex = edge.ToIndex,
                    Role = "surface",
                    Source = "3ddfa-full-resolution-topology",
                    LengthPercent = 0d,
                    ConfidencePercent = 100d
                })
                .ToList();
            return _threeDdfaDenseTopologyEdges;
        }
    }

    private static PreviewOverlayPolyline? ToPreviewOverlayPolyline(
        IReadOnlyList<Point> points,
        bool closed,
        bool inferred = false)
    {
        if (points.Count < 2)
        {
            return null;
        }

        var normalized = points
            .Where(static point => double.IsFinite(point.X) && double.IsFinite(point.Y))
            .Select(static point => new PreviewOverlayPoint(point.X, point.Y).Clamp())
            .ToArray();
        return normalized.Length >= 2
            ? new PreviewOverlayPolyline(normalized, closed, inferred)
            : null;
    }

    private static PreviewOverlayRect? ToPreviewOverlayRect(Rect? region)
    {
        if (region is not Rect rect || rect.IsEmpty || rect.Width <= 0d || rect.Height <= 0d)
        {
            return null;
        }

        return new PreviewOverlayRect(rect.Left, rect.Top, rect.Right, rect.Bottom).Clamp();
    }

    private Rect GetPreviewDisplayRect(BitmapSource bitmap)
    {
        var hostWidth = PreviewHost.ActualWidth;
        var hostHeight = PreviewHost.ActualHeight;
        if (hostWidth <= 0d || hostHeight <= 0d || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            return Rect.Empty;
        }

        var scale = Math.Min(hostWidth / bitmap.PixelWidth, hostHeight / bitmap.PixelHeight);
        var width = bitmap.PixelWidth * scale;
        var height = bitmap.PixelHeight * scale;
        return new Rect((hostWidth - width) / 2d, (hostHeight - height) / 2d, width, height);
    }

    private Color GetFaceCueGuideColor()
    {
        if (!_currentFaceLandmarkMetrics.HasFace)
        {
            return Color.FromRgb(74, 147, 214);
        }

        if (!_currentFaceLandmarkMetrics.IsEyeMeasurementUsable
            || !_currentFaceLandmarkMetrics.IsMouthMeasurementUsable)
        {
            return Color.FromRgb(215, 165, 58);
        }

        return Color.FromRgb(74, 163, 107);
    }

    private void AddGuideRegion(Rect display, Rect frameRegion, Brush fill, Brush stroke, double thickness)
    {
        var rect = ToDisplayRect(display, frameRegion);
        var shape = new Rectangle
        {
            Width = rect.Width,
            Height = rect.Height,
            RadiusX = 3,
            RadiusY = 3,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = thickness
        };

        Canvas.SetLeft(shape, rect.X);
        Canvas.SetTop(shape, rect.Y);
        FaceCueGuideCanvas.Children.Add(shape);
    }

    private void AddGuideLine(Rect display, double x1, double y1, double x2, double y2, Brush stroke, double thickness)
    {
        var line = new Line
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

        FaceCueGuideCanvas.Children.Add(line);
    }

    private void AddLandmarkContours(Rect display, FaceLandmarkFrame frame)
    {
        var eyeBrush = new SolidColorBrush(Color.FromArgb(245, 122, 218, 255));
        var inferredEyeBrush = new SolidColorBrush(Color.FromArgb(245, 238, 174, 74));
        var lipBrush = new SolidColorBrush(Color.FromArgb(245, 255, 190, 110));
        var browBrush = new SolidColorBrush(Color.FromArgb(245, 196, 247, 163));
        var faceBrush = new SolidColorBrush(Color.FromArgb(135, 185, 215, 239));
        var leftEyeInferred = frame.LeftEyeReconstructed || frame.EyeArtifactSuppressed;
        var rightEyeInferred = frame.RightEyeReconstructed || frame.EyeArtifactSuppressed;
        var eyeInferenceBrush = frame.EyeArtifactSuppressed ? inferredEyeBrush : eyeBrush;

        AddGuidePolyline(display, frame.FaceContour, faceBrush, 1.4d, close: true);
        AddGuidePolyline(display, frame.JawContour, faceBrush, 1.8d, close: false);
        AddGuidePolyline(display, frame.LeftEyeContour, leftEyeInferred ? eyeInferenceBrush : eyeBrush, 2.4d, close: true, inferred: leftEyeInferred);
        AddGuidePolyline(display, frame.RightEyeContour, rightEyeInferred ? eyeInferenceBrush : eyeBrush, 2.4d, close: true, inferred: rightEyeInferred);
        AddGuidePolyline(display, frame.LeftBrowContour, browBrush, 2.0d, close: false);
        AddGuidePolyline(display, frame.RightBrowContour, browBrush, 2.0d, close: false);
        AddGuidePolyline(display, frame.OuterLipContour, lipBrush, 2.2d, close: true, inferred: frame.MouthReconstructed);
        AddGuidePolyline(display, frame.InnerLipContour, lipBrush, 1.8d, close: true, inferred: frame.MouthReconstructed);
    }

    private void AddGuidePolyline(Rect display, IReadOnlyList<Point> points, Brush stroke, double thickness, bool close, bool inferred = false)
    {
        if (points.Count < 2)
        {
            return;
        }

        var shape = new Polyline
        {
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        if (inferred)
        {
            shape.StrokeDashArray = CreateInferenceDashArray();
        }

        foreach (var point in points)
        {
            shape.Points.Add(ToDisplayPoint(display, point));
        }

        if (close)
        {
            shape.Points.Add(ToDisplayPoint(display, points[0]));
        }

        FaceCueGuideCanvas.Children.Add(shape);
    }

    private static DoubleCollection CreateInferenceDashArray()
    {
        return new DoubleCollection { 5d, 3d };
    }

    private static Point ToDisplayPoint(Rect display, Point framePoint)
    {
        return new Point(
            display.X + display.Width * framePoint.X,
            display.Y + display.Height * framePoint.Y);
    }

    private static Rect ToDisplayRect(Rect display, double left, double top, double right, double bottom)
    {
        return new Rect(
            display.X + display.Width * left,
            display.Y + display.Height * top,
            display.Width * (right - left),
            display.Height * (bottom - top));
    }

    private static Rect ToDisplayRect(Rect display, Rect frameRegion)
    {
        return ToDisplayRect(display, frameRegion.Left, frameRegion.Top, frameRegion.Right, frameRegion.Bottom);
    }

    private static string FormatRatioPercent(double? value)
    {
        return value is double number ? $"{number * 100d:0}%" : "--";
    }

    private sealed record CameraControlBinding(
        CameraDevice Camera,
        CameraControlItem Control,
        TextBlock ValueText,
        Slider Slider,
        CheckBox AutoCheckBox);

    private sealed record LiveWireframeProjectedPoint(int Index, double X, double Y, double Z);

    private sealed record TrackingFidelityOption(int MaxOutputWidth, double MaxFramesPerSecond);

    private sealed record FaceBoxTrackingFrameResult(
        FaceBoxSystem FaceBoxSystem,
        int FaceBoxSystemGeneration,
        FaceLandmarkTrackingResult TrackingResult,
        ThreeDdfaOnnxSidecarResponse? ThreeDdfaResponse,
        ThreeDdfaReconstructionSnapshot? ThreeDdfaSnapshot,
        string ProfileId,
        long SessionGeneration,
        DateTime CapturedAtUtc,
        BitmapSource SourceFrame,
        double InferenceMilliseconds);

    private sealed record AvatarLearningState(bool Active, string Title, string Detail, Color Accent);

    private sealed record AvatarTrackingSanityState(string Detail, Color Accent);

    private sealed record AvatarReportSnapshot(
        string Folder,
        string SubjectId,
        string SubjectDisplayName,
        AvatarCaptureQualityAssessment CaptureQuality,
        bool UserLoggedIn,
        bool AvatarCaptureRequested,
        bool AvatarCaptureActive,
        string AvatarCaptureStatus,
        string AvatarCaptureCorrection,
        FaceFrameGeometry FaceFrameGeometry,
        IReadOnlyList<ThreeDdfaReconstructionSnapshot> LastGoodThreeDdfaSamples,
        FaceReconstructionLaneStatus ReconstructionLane);

    private sealed record AvatarReportSaveResult(
        string LastGoodThreeDdfaHtmlPath,
        string AvatarSystemDashboardPath,
        string AvatarModelHtmlPath);

    private sealed record AvatarLoginSelection(string ProfileId, string NewDisplayName);

}
