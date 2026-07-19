using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AvatarBuilder.Modules.Episodes;
using AvatarBuilder.Modules.Infrastructure;
using AvatarBuilder.Modules.Recording;
using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
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
    private const double EventVideoFramesPerSecond = 10d;
    private const int PreEventVideoSeconds = 60;
    private const string DefaultAvatarProfileId = "chris";
    private const string DefaultAvatarProfileDisplayName = "Chris";
    private const string PreferredExternalOutputFolder = @"D:\Avatar Builder Output";
    private const string OutputFolderPointerFileName = "AvatarBuilderOutputFolder.txt";
    private const string AlertBaselineFolderName = "AlertBaseline";
    private const string AlertBaselineFileName = "alert_baseline.json";
    private const string AlertBaselineStartButtonText = "Calibrate Alert Baseline";
    private const string AlertBaselineInProgressButtonText = "Calibrating...";
    private const string SleepEventWatchStartButtonText = "Start Sleep Event Watch";
    private const string SleepEventWatchStopButtonText = "Stop Sleep Event Watch";
    private const string SymptomCaptureStartButtonText = "Capture Symptoms";
    private const string SymptomCaptureStopButtonText = "Stop Capture";
    private const string AvatarLearningStartButtonText = "Start Avatar Capture";
    private const string AvatarLearningStopButtonText = "Stop Avatar Capture";
    private const string AvatarCaptureStatusReason = "3DDFA avatar capture active; MediaPipe cue tracking remains live";
    private const double AvatarReportSaveIntervalSeconds = 30d;
    private const int LastGoodFeatureMeshRetainedSampleCount = 5;
    private const int LastGoodThreeDdfaRetainedSampleCount = 5;
    private const int LastGoodFeatureMeshStabilityMinimumSamplesToHold = 3;
    private const double LastGoodFeatureMeshStabilityHoldThresholdPercent = 62d;
    private const double LastGoodFeatureMeshBRotationMinimumRangeDegrees = 14d;
    private const double Insta360Link2ProHorizontalFovDegrees = 71.4d;
    private const string AvatarArchiveFolderName = "AvatarArchive";
    private static readonly TimeSpan CalibrationSymptomFreeWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan TrackingOverlayRefreshInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FaceFeatureDetectionTargetInterval = TimeSpan.FromMilliseconds(120);
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
    private static readonly int[] DenseMeshJawCenter =
    [
        152, 148, 176, 149, 150, 377, 400, 378, 379
    ];

    private readonly FfmpegCameraModeService _cameraModeService = new();
    private readonly DirectShowCameraControlService _cameraControlService = new();
    private readonly CameraPreviewService _previewService = new();
    private readonly FfmpegEventRecorderService _eventRecorder = new();
    private readonly IFaceCueAnalyzer _faceCueAnalyzer = new FaceCueAnalyzer();
    private readonly CompositeFaceLandmarkTracker _faceLandmarkTracker = new();
    private readonly FaceLandmarkTemporalReconstructor _faceLandmarkReconstructor = new();
    private readonly FaceLandmarkMetricCalculator _faceLandmarkMetricCalculator = new();
    private readonly FaceLandmarkCueAnalyzer _faceLandmarkCueAnalyzer = new();
    private readonly FaceLandmarkTrendAnalyzer _faceLandmarkTrendAnalyzer = new();
    private readonly FaceLockStabilityAnalyzer _faceLockStabilityAnalyzer = new();
    private readonly FaceFrameGeometryEstimator _faceFrameGeometryEstimator = new();
    private readonly ThreeDdfaOnnxModelInfo _threeDdfaOnnxModelInfo;
    private readonly ThreeDdfaOnnxSidecarEnvironment _threeDdfaOnnxEnvironment;
    private readonly ThreeDdfaOnnxReconstructionClient _threeDdfaOnnxClient;
    private readonly AvatarBuilderStartupOptions _startupOptions;
    private readonly AvatarProfileStore _avatarProfileStore = new();
    private readonly AvatarCaptureQualityAnalyzer _avatarCaptureQualityAnalyzer = new();
    private readonly LandmarkEventAggregate _landmarkEventAggregate = new();
    private readonly LandmarkEventTimeline _landmarkEventTimeline = new();
    private readonly EpisodeEventDatabase _eventDatabase = new();
    private readonly LastGoodFeatureMeshStore _lastGoodFeatureMeshStore = new();
    private readonly LastGoodThreeDdfaStore _lastGoodThreeDdfaStore = new();
    private readonly object _faceLandmarkTrackerLock = new();
    private readonly ObservableCollection<AvatarBuilderEvent> _events = [];
    private readonly object _frameLock = new();
    private readonly object _previewFramePumpLock = new();
    private readonly object _directX12PreviewLock = new();
    private readonly object _directX12AnalysisFrameLock = new();
    private readonly object _faceFeatureDetectionFrameLock = new();
    private readonly object _personalFaceReportWriterLock = new();
    private readonly Queue<BufferedVideoFrame> _preEventVideoFrames = new();
    private readonly List<LastGoodFeatureMeshSample> _lastGoodFeatureMeshSamples = [];
    private readonly List<LastGoodFeatureThreeDdfaSnapshot> _lastGoodThreeDdfaSamples = [];
    private readonly ObservableCollection<AvatarProfile> _avatarProfiles = [];
    private readonly DispatcherTimer _calibrationGuardTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    private IReadOnlyList<CameraDevice> _cameras = [];
    private CancellationTokenSource? _modeLoadCancellation;
    private string _outputFolder;
    private byte[]? _previousSample;
    private BitmapSource? _latestFrame;
    private FaceCueAnalysis? _currentFaceAnalysis;
    private FaceFeatureDetection _currentFaceFeatureDetection = FaceFeatureDetection.None;
    private FaceLandmarkFrame _currentFaceLandmarkFrame = FaceLandmarkFrame.None;
    private FaceLandmarkMetrics _currentFaceLandmarkMetrics = FaceLandmarkMetrics.None;
    private FaceLandmarkCueAnalysis? _currentFaceLandmarkCueAnalysis;
    private FaceLandmarkTrendAnalysis _currentFaceLandmarkTrendAnalysis = FaceLandmarkTrendAnalysis.Waiting;
    private FaceLockStabilityAnalysis _currentFaceLockStabilityAnalysis = FaceLockStabilityAnalysis.Waiting;
    private FaceFrameGeometry _currentFaceFrameGeometry = FaceFrameGeometry.None;
    private ThreeDdfaOnnxSidecarResponse _currentThreeDdfaOnnxResponse = ThreeDdfaOnnxSidecarResponse.Waiting;
    private AvatarCaptureQualityAssessment _currentAvatarCaptureQuality = AvatarCaptureQualityAssessment.Waiting;
    private AvatarProfileRegistry _avatarProfileRegistry = new();
    private AvatarProfile _currentAvatarProfile = new()
    {
        Id = DefaultAvatarProfileId,
        DisplayName = DefaultAvatarProfileDisplayName,
        DataFolderName = ""
    };
    private DateTime? _lowMotionStartedAt;
    private DateTime? _eyeCueStartedAt;
    private DateTime? _jawCueStartedAt;
    private DateTime? _eyeTrendCueStartedAt;
    private DateTime? _lastSymptomAt;
    private DateTime? _calibrationHoldUntil;
    private DateTime? _activeEpisodeStartedAt;
    private DateTime? _activeEpisodeEarliestAutoEndAt;
    private string _episodeStartSnapshot = "";
    private string _activeEventFolder = "";
    private string _activeEventVideo = "";
    private string _avatarSystemDashboardPath = "";
    private string _avatarModelHtmlPath = "";
    private string _lastGoodFeatureMeshJsonPath = "";
    private string _lastGoodFeatureMeshHtmlPath = "";
    private string _lastGoodThreeDdfaJsonPath = "";
    private string _lastGoodThreeDdfaHtmlPath = "";
    private DateTime? _alertBaselineSavedAtUtc;
    private string _alertBaselineCameraName = "";
    private string _alertBaselineModeLabel = "";
    private List<string> _activeTriggerReasons = [];
    private BitmapSource? _pendingPreviewFrame;
    private BitmapSource? _pendingFaceFeatureDetectionFrame;
    private TextureNativeFrameLease? _pendingDirectX12AnalysisFrame;
    private AvatarReportSnapshot? _pendingAvatarReportSnapshot;
    private Task? _avatarReportWriterTask;
    private Direct3D12PreviewHost? _directX12PreviewHost;
    private Dx12Camera? _directX12NativeCamera;
    private double _episodeMotionSum;
    private int _episodeMotionSamples;
    private DateTime _lastBufferedVideoFrameAt = DateTime.MinValue;
    private DateTime _lastRecordedVideoFrameAt = DateTime.MinValue;
    private DateTime _lastPreviewFrameAcceptedAt = DateTime.MinValue;
    private DateTime _lastDirectX12DiagnosticsAtUtc = DateTime.MinValue;
    private DateTime _lastDirectX12AnalysisFrameAtUtc = DateTime.MinValue;
    private DateTime _lastAvatarReportSavedAtUtc = DateTime.MinValue;
    private DateTime _lastTrackingOverlayUpdateAtUtc = DateTime.MinValue;
    private DateTime _previewReplacementWindowStartedAtUtc = DateTime.MinValue;
    private string _lastTrackingOverlayState = "";
    private string _lastTrackingOverlayMetrics = "";
    private string _lastTrackingOverlayTrigger = "";
    private string _lastTrackingOverlayAccentColor = "";
    private string _avatarRecentMeshStabilitySummary = "";
    private string _avatarCaptureGateReason = "waiting for face landmarks";
    private string _trackingFidelityConfigurationStatus = "";
    private string _lastGoodFeatureMeshStatus = "last good feature mesh waiting";
    private LastGoodFeatureMeshStabilityReport _lastGoodFeatureMeshStability = new();
    private SolidColorBrush? _lastTrackingOverlayAccentBrush;
    private double _directX12PreviewMaxRenderFramesPerSecond;
    private double _cameraDisplayFramesPerSecond;
    private double _featureOverlayFramesPerSecond;
    private double _directX12RenderFramesPerSecond;
    private TimeSpan _directX12AnalysisFrameInterval = TimeSpan.FromSeconds(1d / 5d);
    private int _directX12AnalysisMaxOutputWidth = 3840;
    private int _previewFramesReplacedSinceWarning;
    private int _cameraDisplayFramesInHealthWindow;
    private int _featureOverlayFramesInHealthWindow;
    private int _uiFramePending;
    private int _previewWarningPending;
    private int _faceFeatureDetectionPending;
    private int _threeDdfaOnnxReconstructionPending;
    private bool _avatarRecentMeshStabilityHold;
    private long _directX12FrameNumber;
    private int _directX12AnalysisWorkerQueued;
    private bool _alertBaselineCalibrationActive;
    private bool _sleepEventWatchActive;
    private bool _symptomCaptureActive;
    private bool _avatarLearningRequested;
    private bool _avatarCaptureGateAccepted;
    private bool _showLiveWireframePreview;
    private bool _isCameraEnabled;
    private bool _isUpdatingCameraToggle;
    private bool _isChoosingCameraModeForFidelity;
    private bool _isRefreshingCameras;
    private bool _isLoadingCameraControls;
    private bool _isUpdatingCameraControlUi;
    private bool _isSnappingSlider;
    private bool _isUpdatingAvatarProfileUi;
    private bool _isClosing;
    private bool _startupOptionsApplied;
    private FaceCueGuideLayout? _activeFaceCueLayout;
    private DateTime _lastFaceAutoFollowAt = DateTime.MinValue;
    private DateTime _lastFaceFeatureDetectionAt = DateTime.MinValue;
    private DateTime _pendingFaceFeatureDetectionCapturedAtUtc = DateTime.MinValue;
    private DateTime _lastFaceFeatureLockAt = DateTime.MinValue;
    private DateTime _cameraHealthWindowStartedAtUtc = DateTime.MinValue;
    private DateTime _featureOverlayHealthWindowStartedAtUtc = DateTime.MinValue;
    private DateTime _lastGoodFeatureMeshCapturedAtUtc = DateTime.MinValue;
    private DateTime _lastThreeDdfaOnnxRequestAtUtc = DateTime.MinValue;

    private static readonly IReadOnlyList<TrackingFidelityOption> TrackingFidelityOptions =
    [
        new("Safe preview - 960px / 15 fps", 960, 15d),
        new("HD camera - 1920px / 18 fps", 1920, 18d),
        new("4K camera - 1920px analysis / 15 fps", 3840, 15d)
    ];

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
        _threeDdfaOnnxClient = new ThreeDdfaOnnxReconstructionClient(_threeDdfaOnnxEnvironment);
        InitializeComponent();
        _outputFolder = ResolveInitialOutputFolder(_startupOptions.OutputFolder);
        ResetAvatarCaptureGate("waiting for face landmarks");
        _previewService.FrameAvailable += PreviewFrameAvailable;
        _previewService.CameraFrameAvailable += PreviewCameraFrameAvailable;
        _previewService.StatusChanged += PreviewStatusChanged;
        _eventRecorder.StatusChanged += PreviewStatusChanged;
        _calibrationGuardTimer.Tick += CalibrationGuardTick;
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
        EnableDarkWindowFrame();
        EventGrid.ItemsSource = _events;
        EnsureOutputFolderConfiguredForLaunch();
        InitializeAvatarProfiles(promptForStartupUser: true);
        PruneOldEventData();
        LoadTodaysEventListFromOutputFolder();
        TrackingFidelityComboBox.ItemsSource = TrackingFidelityOptions;
        TrackingFidelityComboBox.DisplayMemberPath = nameof(TrackingFidelityOption.Label);
        TrackingFidelityComboBox.SelectedIndex = 2;
        ApplyTrackingFidelity();
        UpdateOutputFolderText();
        UpdateSettingLabels();
        UpdateSleepEventWatchButtonState();
        LoadAlertBaselineFromOutputFolder(showStatus: false);
        PrepareAvatarCaptureFolder(showStatus: false);
        UpdateCalibrationGuard();
        UpdateAvatarLearningStatusUi();
        _calibrationGuardTimer.Start();
        Dispatcher.InvokeAsync(async () => await RefreshCamerasAsync(), DispatcherPriority.ApplicationIdle);
        Dispatcher.InvokeAsync(ApplyStartupOptionsAfterLoad, DispatcherPriority.ApplicationIdle);
    }

    private void ApplyStartupOptionsAfterLoad()
    {
        if (_startupOptionsApplied)
        {
            return;
        }

        _startupOptionsApplied = true;
        if (_startupOptions.StartAvatarLearning)
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
        _avatarProfileRegistry = _avatarProfileStore.Load(_outputFolder);
        AvatarProfileComboBox.ItemsSource ??= _avatarProfiles;

        if (promptForStartupUser)
        {
            var startupSelection = PromptForStartupAvatarProfile(_avatarProfileRegistry);
            if (!string.IsNullOrWhiteSpace(startupSelection.NewDisplayName))
            {
                _avatarProfileStore.AddOrUpdateProfile(_outputFolder, _avatarProfileRegistry, startupSelection.NewDisplayName);
            }
            else if (!string.IsNullOrWhiteSpace(startupSelection.ProfileId))
            {
                _avatarProfileStore.SelectProfile(_outputFolder, _avatarProfileRegistry, startupSelection.ProfileId);
            }
        }

        if (_avatarProfileRegistry.Profiles.Count == 0)
        {
            _avatarProfileStore.AddOrUpdateProfile(_outputFolder, _avatarProfileRegistry, DefaultAvatarProfileDisplayName);
        }

        RefreshAvatarProfileList();
        var selected = FindSelectedAvatarProfile() ?? _avatarProfiles.FirstOrDefault();
        if (selected is null)
        {
            return;
        }

        ApplyCurrentAvatarProfile(selected, loadModel: false, stopLearning: false);
        ResetAvatarCaptureGate("waiting for face landmarks");
    }

    private AvatarStartupSelection PromptForStartupAvatarProfile(AvatarProfileRegistry registry)
    {
        var profiles = registry.Profiles.ToList();
        var window = new Window
        {
            Title = "Choose Avatar User",
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
            Text = "Who is in front of the camera for avatar capture?",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Pick a remembered user, or type a new consenting user's name. Narcolepsy tracking still works either way.",
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
            Content = "Continue",
            MinWidth = 110,
            Margin = new Thickness(8, 0, 0, 0),
            Style = TryFindResource(typeof(Button)) as Style
        };
        var cancelButton = new Button
        {
            Content = "Use Selected",
            MinWidth = 110,
            Style = TryFindResource(typeof(Button)) as Style,
            IsEnabled = profiles.Count > 0
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(continueButton);
        panel.Children.Add(buttons);
        window.Content = panel;

        AvatarStartupSelection selection = new("", "");
        continueButton.Click += (_, _) =>
        {
            var typedName = nameBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(typedName))
            {
                selection = new AvatarStartupSelection("", typedName);
                window.DialogResult = true;
                return;
            }

            if (combo.SelectedItem is AvatarProfile profile)
            {
                selection = new AvatarStartupSelection(profile.Id, "");
                window.DialogResult = true;
                return;
            }

            status.Text = "Type the user's name before continuing.";
        };
        cancelButton.Click += (_, _) =>
        {
            if (combo.SelectedItem is AvatarProfile profile)
            {
                selection = new AvatarStartupSelection(profile.Id, "");
                window.DialogResult = true;
            }
        };

        var result = window.ShowDialog();
        if (result == true)
        {
            return selection;
        }

        var fallback = profiles.FirstOrDefault(profile => string.Equals(profile.Id, registry.SelectedProfileId, StringComparison.OrdinalIgnoreCase))
            ?? profiles.FirstOrDefault();
        return fallback is null
            ? new AvatarStartupSelection("", DefaultAvatarProfileDisplayName)
            : new AvatarStartupSelection(fallback.Id, "");
    }

    private void RefreshAvatarProfileList()
    {
        _isUpdatingAvatarProfileUi = true;
        try
        {
            _avatarProfiles.Clear();
            foreach (var profile in _avatarProfileRegistry.Profiles.OrderBy(static profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                _avatarProfiles.Add(profile);
            }
        }
        finally
        {
            _isUpdatingAvatarProfileUi = false;
        }
    }

    private AvatarProfile? FindSelectedAvatarProfile()
    {
        return _avatarProfiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, _avatarProfileRegistry.SelectedProfileId, StringComparison.OrdinalIgnoreCase))
            ?? _avatarProfiles.OrderByDescending(static profile => profile.LastSelectedAtUtc ?? DateTime.MinValue).FirstOrDefault();
    }

    private void ApplyCurrentAvatarProfile(AvatarProfile profile, bool loadModel, bool stopLearning)
    {
        _currentAvatarProfile = profile;
        if (stopLearning)
        {
            _avatarLearningRequested = false;
        }

        _isUpdatingAvatarProfileUi = true;
        try
        {
            AvatarProfileComboBox.SelectedItem = _avatarProfiles.FirstOrDefault(item =>
                string.Equals(item.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
            AvatarProfileNameTextBox.Text = "";
            var displayName = CurrentAvatarProfileDisplayName;
            AvatarSubjectCheckBox.Content = $"This is {displayName} - allow avatar capture";
            AvatarSubjectCheckBox.ToolTip = $"Turn this on only when {displayName} is in front of the camera.";
            AvatarProfileStatusText.Text = $"Selected user: {displayName}. Data folder: {GetAvatarDataFolder()}";
        }
        finally
        {
            _isUpdatingAvatarProfileUi = false;
        }

        _avatarProfileStore.SelectProfile(_outputFolder, _avatarProfileRegistry, profile.Id);
        if (loadModel)
        {
            AvatarSubjectCheckBox.IsChecked = false;
            ResetAvatarRuntimeForProfile("selected avatar user changed; confirm the subject before avatar capture");
            PrepareAvatarCaptureFolder(showStatus: true);
        }

        UpdateAvatarLearningStatusUi();
    }

    private string CurrentAvatarProfileId => string.IsNullOrWhiteSpace(_currentAvatarProfile.Id)
        ? DefaultAvatarProfileId
        : _currentAvatarProfile.Id;

    private string CurrentAvatarProfileDisplayName => string.IsNullOrWhiteSpace(_currentAvatarProfile.DisplayName)
        ? DefaultAvatarProfileDisplayName
        : _currentAvatarProfile.DisplayName;

    private void ResetAvatarCaptureGate(string reason, bool accepted = false)
    {
        _avatarCaptureGateAccepted = accepted;
        _avatarCaptureGateReason = string.IsNullOrWhiteSpace(reason) ? "waiting for face landmarks" : reason;
    }

    private void ResetAvatarRuntimeForProfile(string reason)
    {
        ResetAvatarCaptureGate(reason);
        _currentAvatarCaptureQuality = AvatarCaptureQualityAssessment.Waiting;
        _avatarSystemDashboardPath = "";
        _avatarModelHtmlPath = "";
        _lastGoodFeatureMeshJsonPath = "";
        _lastGoodFeatureMeshHtmlPath = "";
        _lastGoodThreeDdfaJsonPath = "";
        _lastGoodThreeDdfaHtmlPath = "";
        _lastGoodFeatureMeshSamples.Clear();
        _lastGoodThreeDdfaSamples.Clear();
        RefreshLastGoodFeatureMeshStabilityAudit();
        _lastGoodFeatureMeshStatus = "last good feature mesh waiting";
        _avatarRecentMeshStabilityHold = false;
        _avatarRecentMeshStabilitySummary = "";
    }

    private void PruneOldEventData()
    {
        try
        {
            var cutoff = DateTime.Today.AddDays(-30);
            var deleted = _eventDatabase.DeleteEventsOlderThan(_outputFolder, cutoff);
            DeleteEventArtifacts(deleted);
            if (deleted.Count > 0)
            {
                SetStatus($"Pruned {deleted.Count} event{(deleted.Count == 1 ? "" : "s")} older than 30 days.");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Could not prune old event data: {ex.Message}");
        }
    }

    private void WindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _isClosing = true;
        _modeLoadCancellation?.Cancel();
        _modeLoadCancellation?.Dispose();
        EndActiveEpisode(DateTime.Now, null, "App closing");
        _calibrationGuardTimer.Stop();
        _previewService.CameraFrameAvailable -= PreviewCameraFrameAvailable;
        SaveAlertBaselineToOutputFolder();
        lock (_faceLandmarkTrackerLock)
        {
            _faceLandmarkTracker.Dispose();
        }
        _threeDdfaOnnxClient.Dispose();
        DisposeDirectX12PreviewHost();
        _eventRecorder.Dispose();
        _previewService.Dispose();
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
        UpdateCalibrationGuard();
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
        UpdateCalibrationGuard();
    }

    private void TrackingFidelitySelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ApplyTrackingFidelity();
        SelectRecommendedCameraModeForFidelity(replaceAutoOnly: true);
        if (_isCameraEnabled)
        {
            RestartPreview();
        }
    }

    private void ApplyTrackingFidelity()
    {
        var option = GetSelectedTrackingFidelityOption();
        var analysisOutputWidth = GetTrackingAnalysisOutputWidth(option);

        _previewService.MaxOutputWidth = analysisOutputWidth;
        _previewService.MaxOutputFramesPerSecond = option.MaxFramesPerSecond;
        _directX12AnalysisMaxOutputWidth = analysisOutputWidth;
        _directX12AnalysisFrameInterval = TimeSpan.FromSeconds(1d / Math.Clamp(option.MaxFramesPerSecond, 1d, 60d));
        _faceLandmarkTracker.MaxDetectionDimension = option.MaxOutputWidth >= 3840 ? 1920 : Math.Clamp(option.MaxOutputWidth, 640, 960);
        TrackingFidelityValueText.Text = option.ShortLabel;
        _trackingFidelityConfigurationStatus = $"Camera mode target: {option.MaxOutputWidth}px. Analysis frames: {analysisOutputWidth}px max at {option.MaxFramesPerSecond:0.###} fps; landmark detector max {_faceLandmarkTracker.MaxDetectionDimension}px.";
        UpdateTrackingFidelityHealthText(force: true);
    }

    private void TrackCameraDisplayFrame(DateTime utcNow)
    {
        if (_cameraHealthWindowStartedAtUtc == DateTime.MinValue)
        {
            _cameraHealthWindowStartedAtUtc = utcNow;
        }

        _cameraDisplayFramesInHealthWindow++;
        var elapsed = utcNow - _cameraHealthWindowStartedAtUtc;
        if (elapsed.TotalSeconds < 2d)
        {
            return;
        }

        _cameraDisplayFramesPerSecond = _cameraDisplayFramesInHealthWindow / Math.Max(0.001d, elapsed.TotalSeconds);
        _cameraDisplayFramesInHealthWindow = 0;
        _cameraHealthWindowStartedAtUtc = utcNow;
        UpdateTrackingFidelityHealthText();
    }

    private void TrackFeatureOverlayFrame(DateTime utcNow)
    {
        if (_featureOverlayHealthWindowStartedAtUtc == DateTime.MinValue)
        {
            _featureOverlayHealthWindowStartedAtUtc = utcNow;
        }

        _featureOverlayFramesInHealthWindow++;
        var elapsed = utcNow - _featureOverlayHealthWindowStartedAtUtc;
        if (elapsed.TotalSeconds < 2d)
        {
            return;
        }

        _featureOverlayFramesPerSecond = _featureOverlayFramesInHealthWindow / Math.Max(0.001d, elapsed.TotalSeconds);
        _featureOverlayFramesInHealthWindow = 0;
        _featureOverlayHealthWindowStartedAtUtc = utcNow;
        UpdateTrackingFidelityHealthText();
    }

    private void UpdateTrackingFidelityHealthText(bool force = false)
    {
        if (TrackingFidelityStatusText is null)
        {
            return;
        }

        var option = GetSelectedTrackingFidelityOption();
        var cameraRate = _directX12RenderFramesPerSecond > 0d
            ? _directX12RenderFramesPerSecond
            : _cameraDisplayFramesPerSecond;
        var cameraLabel = cameraRate > 0d
            ? $"{cameraRate:0.#} fps"
            : "warming";
        var featureLabel = _featureOverlayFramesPerSecond > 0d
            ? $"{_featureOverlayFramesPerSecond:0.#} fps"
            : "warming";
        var featureTarget = Math.Min(
            option.MaxFramesPerSecond,
            1d / Math.Max(0.001d, FaceFeatureDetectionTargetInterval.TotalSeconds));
        var health = "health ok";
        if (cameraRate > 0d && cameraRate < option.MaxFramesPerSecond * 0.55d)
        {
            health = "camera/render rate low";
        }
        else if (_featureOverlayFramesPerSecond > 0d && _featureOverlayFramesPerSecond < featureTarget * 0.45d)
        {
            health = _avatarReportWriterTask is { IsCompleted: false }
                ? "feature overlay low while report writer is active"
                : "feature overlay rate low";
        }

        var text = $"{_trackingFidelityConfigurationStatus} Live health: camera/render {cameraLabel}; feature overlay {featureLabel} target {featureTarget:0.#} fps; {health}. {_lastGoodFeatureMeshStatus}.";
        if (force || !string.Equals(TrackingFidelityStatusText.Text, text, StringComparison.Ordinal))
        {
            TrackingFidelityStatusText.Text = text;
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
        return TrackingFidelityComboBox.SelectedItem as TrackingFidelityOption
            ?? TrackingFidelityOptions.ElementAtOrDefault(1)
            ?? TrackingFidelityOptions[0];
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
            StartPreview();
        }
        else
        {
            StopPreview();
        }
    }

    private void DirectX12PreviewChanged(object sender, RoutedEventArgs e)
    {
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

    private void LiveWireframePreviewChanged(object sender, RoutedEventArgs e)
    {
        _showLiveWireframePreview = LiveWireframePreviewToggle.IsChecked == true;
        LiveWireframePreviewToggle.Content = _showLiveWireframePreview
            ? "Show Webcam Preview"
            : "Show Live Wireframe";
        SetPreviewState(_showLiveWireframePreview ? "Live wireframe preview" : "Camera active", _latestFrame);
    }

    private void LiveWireframeHeadLockChanged(object sender, RoutedEventArgs e)
    {
        if (_showLiveWireframePreview)
        {
            DrawLiveWireframePreview();
        }
    }

    private async void StartPreview()
    {
        if (CameraComboBox.SelectedItem is not CameraDevice camera)
        {
            SetCameraToggle(false);
            SetStatus("Choose a camera first.");
            return;
        }

        var mode = CameraModeComboBox.SelectedItem as CameraVideoMode ?? CameraVideoMode.Auto;
        ApplyTrackingFidelity();
        _previousSample = null;
        SetPreviewState($"Starting {camera.Name} ({mode.Label})", null);
        SetStatus($"Opening camera: {camera.Name} ({mode.Label})");

        if (IsDirectX12PreviewEnabled() && TryStartDirectX12NativeCamera(camera, mode))
        {
            _isCameraEnabled = true;
            SetCameraToggle(true);
            SetStatus($"Camera active through native DX12 texture path: {camera.Name} ({mode.Label})");
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
            _previousSample = null;
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

        var analysisFrame = frame.Duplicate();
        if (analysisFrame is null)
        {
            return;
        }

        QueueDirectX12AnalysisFrame(analysisFrame);
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
        if (Interlocked.Exchange(ref _directX12AnalysisWorkerQueued, 1) == 0)
        {
            _ = Task.Run(ProcessPendingDirectX12AnalysisFrames);
        }
    }

    private void ProcessPendingDirectX12AnalysisFrames()
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
            finally
            {
                frame.Dispose();
            }
        }

        Interlocked.Exchange(ref _directX12AnalysisWorkerQueued, 0);
        lock (_directX12AnalysisFrameLock)
        {
            if (_pendingDirectX12AnalysisFrame is not null
                && Interlocked.Exchange(ref _directX12AnalysisWorkerQueued, 1) == 0)
            {
                _ = Task.Run(ProcessPendingDirectX12AnalysisFrames);
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
        var bgraBytes = frame.BgraPreviewBytes;
        var bgraStride = frame.BgraPreviewStride;
        var bitmapWidth = frame.Width;
        var bitmapHeight = frame.Height;
        if ((bgraBytes is null || bgraBytes.Length == 0 || bgraStride <= 0)
            && frame.Nv12PreviewBytes is { Length: > 0 } nv12Bytes
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
        return DirectX12PreviewCheckBox.IsChecked == true;
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
                Dispatcher.InvokeAsync(() => DirectX12PreviewCheckBox.IsChecked = false, DispatcherPriority.Background);
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
        _directX12RenderFramesPerSecond = diagnostics.RenderFramesPerSecond;
        if ((now - _lastDirectX12DiagnosticsAtUtc).TotalSeconds < 2d)
        {
            return;
        }

        _lastDirectX12DiagnosticsAtUtc = now;
        UpdateTrackingFidelityHealthText();
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
                TrackCameraDisplayFrame(_lastPreviewFrameAcceptedAt);
                _latestFrame = frame;
                SetPreviewState("Camera active", frame);
                ProcessFrame(frame);
                UpdateFaceCueGuideOverlay(frame);
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
            DirectX12PreviewLayer.Visibility = directX12Enabled
                && (_directX12PreviewHost is not null || _directX12NativeCamera is not null)
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
            if (ShouldUseWpfTrackingPreview(frame))
            {
                DirectX12PreviewLayer.Visibility = Visibility.Collapsed;
                PreviewImage.Source = frame;
                PreviewImage.Visibility = Visibility.Visible;
            }
            else
            {
                DirectX12PreviewLayer.Visibility = Visibility.Visible;
                PreviewImage.Source = null;
                PreviewImage.Visibility = Visibility.Collapsed;
            }

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

    private bool ShouldUseWpfTrackingPreview(ImageSource frame)
    {
        return frame is BitmapSource
            && _directX12NativeCamera is not null;
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

    private void SleepEventWatchClicked(object sender, RoutedEventArgs e)
    {
        _sleepEventWatchActive = !_sleepEventWatchActive;
        UpdateSleepEventWatchButtonState();
        if (_sleepEventWatchActive)
        {
            Directory.CreateDirectory(_outputFolder);
            ResetEpisodeState();
            MonitorStatusText.Text = "Episode monitor watching for sustained low motion.";
            UpdateTrackingOverlay("Tracking armed", $"Motion -- | Threshold {GetMotionThreshold():0.0}%", "Waiting for a motion baseline.", "#37506a");
        }
        else
        {
            EndActiveEpisode(DateTime.Now, null, "Monitoring stopped");
            ResetEpisodeState();
            MonitorStatusText.Text = "Episode monitor idle.";
            UpdateTrackingOverlay("Tracking idle", "Motion -- | Threshold --", "Enable episode watch to arm tracking.", "#37506a");
        }
    }

    private void UpdateSleepEventWatchButtonState()
    {
        ApplyStartStopButtonState(
            SleepEventWatchButton,
            _sleepEventWatchActive,
            SleepEventWatchStartButtonText,
            SleepEventWatchStopButtonText,
            "Starts automatic event watching from low motion, eye cues, mouth cues, and trends.",
            "Stops automatic event watching and closes any active sleep event capture.");
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
                _faceCueAnalyzer.Reset();
                _faceLandmarkCueAnalyzer.Reset();
                _eyeCueStartedAt = null;
                _jawCueStartedAt = null;
                _currentFaceAnalysis = null;
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
            nameof(ThresholdSlider) => (1.5d, 0.15d),
            nameof(StillnessSlider) => (120d, 10d),
            nameof(EyeCueSlider) => (35d, 2.5d),
            nameof(JawCueSlider) => (35d, 2.5d),
            nameof(FaceCueTimeSlider) => (3d, 0.5d),
            nameof(CompositeCueSlider) => (55d, 2.5d),
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

    private void FaceCueChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _faceCueAnalyzer.Reset();
        _faceLandmarkCueAnalyzer.Reset();
        _eyeCueStartedAt = null;
        _jawCueStartedAt = null;
        _currentFaceAnalysis = null;
        _currentFaceFeatureDetection = FaceFeatureDetection.None;
        ResetLandmarkTracking();
        _activeFaceCueLayout = null;
        _lastFaceFeatureLockAt = DateTime.MinValue;
        MonitorStatusText.Text = "Face tracking reset. Use Calibrate Alert Baseline when you are alert and symptom-free.";
        UpdateCalibrationGuard();
    }

    private void AvatarProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingAvatarProfileUi || AvatarProfileComboBox.SelectedItem is not AvatarProfile profile)
        {
            return;
        }

        ApplyCurrentAvatarProfile(profile, loadModel: true, stopLearning: true);
        SetStatus($"Avatar user switched to {CurrentAvatarProfileDisplayName}. Confirm the subject before starting avatar capture.");
    }

    private void AddAvatarProfileClicked(object sender, RoutedEventArgs e)
    {
        var displayName = AvatarProfileNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            SetStatus("Type the avatar user's name before adding a profile.");
            return;
        }

        try
        {
            var profile = _avatarProfileStore.AddOrUpdateProfile(_outputFolder, _avatarProfileRegistry, displayName);
            RefreshAvatarProfileList();
            ApplyCurrentAvatarProfile(profile, loadModel: true, stopLearning: true);
            SetStatus($"Avatar user {profile.DisplayName} is selected. Confirm the subject before starting avatar capture.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not add avatar user: {ex.Message}");
        }
    }

    private void AvatarSubjectChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingAvatarProfileUi)
        {
            return;
        }

        var displayName = CurrentAvatarProfileDisplayName;
        if (AvatarSubjectCheckBox.IsChecked == true)
        {
            ResetAvatarCaptureGate("subject confirmed; waiting for high-confidence face tracking");
            SetStatus($"{displayName} confirmed for Avatar System capture.");
        }
        else
        {
            ResetAvatarCaptureGate("subject not confirmed; avatar capture paused");
            SetStatus($"Avatar System capture paused until {displayName} is confirmed.");
        }

        UpdateAvatarLearningStatusUi();
    }

    private void AvatarLearningToggleClicked(object sender, RoutedEventArgs e)
    {
        _avatarLearningRequested = !_avatarLearningRequested;
        UpdateAvatarLearningStatusUi();
        SetStatus(_avatarLearningRequested
            ? "Avatar capture started. 3DDFA owns dense avatar reconstruction; MediaPipe eye/jaw/brow tracking stays live for overlays and narcolepsy cues."
            : "Avatar capture stopped.");
    }

    private void UpdateAvatarLearningStatusUi()
    {
        if (!IsLoaded)
        {
            return;
        }

        var state = GetAvatarLearningState();
        ApplyStartStopButtonState(
            AvatarLearningToggleButton,
            _avatarLearningRequested,
            AvatarLearningStartButtonText,
            AvatarLearningStopButtonText,
            "Starts 3DDFA avatar capture. Event recording is separate.",
            "Stops 3DDFA avatar capture. Event recording is separate.");
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
        var reconstructionStatus = canRun
            ? pending
                ? "3DDFA/ONNX reconstructing latest avatar frame"
                : !string.IsNullOrWhiteSpace(response.Status) ? response.Status : "3DDFA/ONNX ready; waiting for avatar frame"
            : _threeDdfaOnnxEnvironment.Status;
        var fastStatus = _faceLandmarkTracker.IsAvailable
            ? _faceLandmarkTracker.LastBackendStatus
            : "fast tracking lane unavailable";
        var trustLevel = response.Ok && response.HasFace
            ? "cross-checked"
            : canRun ? "3DDFA-ready" : "measurement-only";
        var trustDecision = response.Ok && response.HasFace
            ? $"Avatar reconstruction: 3DDFA lock {response.ReconstructionConfidencePercent:0}% | A/B/C {response.Pose.ARotationAroundXDegrees:0.#}/{response.Pose.BRotationAroundYDegrees:0.#}/{response.Pose.CRotationAroundZDegrees:0.#} deg | dense {response.DenseVertexCount} vertices."
            : canRun
                ? $"Avatar reconstruction: {reconstructionStatus}. MediaPipe remains live tracking."
                : $"Avatar reconstruction: waiting for 3DDFA/ONNX install. MediaPipe remains live tracking. {_threeDdfaOnnxEnvironment.Status}";

        var warnings = new List<string>();
        if (!canRun)
        {
            warnings.Add("3DDFA/ONNX avatar reconstruction is not active yet; avatar output remains measurement-only.");
        }

        warnings.AddRange(response.Warnings);
        return new FaceReconstructionLaneStatus
        {
            CreatedAtUtc = DateTime.UtcNow,
            FastTrackingAvailable = _faceLandmarkTracker.IsAvailable,
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
            LearningImpact = canRun
                ? "3DDFA/ONNX runs asynchronously for avatar reconstruction trust and does not block narcolepsy tracking."
                : "Narcolepsy tracking keeps using MediaPipe/OpenCV. Avatar reconstruction waits for the 3DDFA/ONNX bundle.",
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

    private static bool IsLegacyMissingBRotationFinding(string? text)
    {
        return !string.IsNullOrWhiteSpace(text)
            && (text.Contains("C rotation changes but no B rotation changes", StringComparison.OrdinalIgnoreCase)
                || text.Contains("head A/B rotations are not moving", StringComparison.OrdinalIgnoreCase));
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
        var subjectConfirmed = AvatarSubjectCheckBox.IsChecked == true;
        if (!subjectConfirmed)
        {
            return new AvatarLearningState(
                false,
                "Avatar capture stopped",
                $"Not capturing: check 'This is {CurrentAvatarProfileDisplayName}' only when that person is in front of the camera.",
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
                $"{reconstruction} MediaPipe eye/jaw/brow tracking stays live for overlays and narcolepsy cues.",
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
        if (HasStrongThreeDdfaPoseLock())
        {
            return new AvatarTrackingSanityState(
                $"Tracking sanity: 3DDFA_V2 ONNX owns avatar pose/depth. MediaPipe eye/jaw/brow tracking remains active for overlays and narcolepsy cues. {FormatThreeDdfaPoseCrossCheck()}",
                Color.FromRgb(128, 224, 164));
        }

        var detail = _threeDdfaOnnxEnvironment.IsReady
            ? "Tracking sanity: MediaPipe eye/jaw/brow tracking is live; waiting for a strong 3DDFA_V2 ONNX pose lock before trusting avatar pose/depth."
            : $"Tracking sanity: MediaPipe eye/jaw/brow tracking is live; 3DDFA_V2 ONNX avatar reconstruction is waiting. {_threeDdfaOnnxEnvironment.Status}";
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
            SubjectConfirmed = AvatarSubjectCheckBox.IsChecked == true,
            AvatarLearningRequested = _avatarLearningRequested,
            CameraActive = _isCameraEnabled && _latestFrame is not null,
            FaceLocked = _currentFaceLandmarkFrame.HasFace && _currentFaceLandmarkMetrics.HasFace,
            TrackingAuditHold = _avatarRecentMeshStabilityHold,
            TrackingAuditHoldSummary = _avatarRecentMeshStabilitySummary,
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

    private void CalibrateAlertBaselineClicked(object sender, RoutedEventArgs e)
    {
        if (!IsCalibrationAllowed())
        {
            MonitorStatusText.Text = "Alert baseline calibration is blocked until you have been symptom-free for one hour.";
            UpdateCalibrationGuard();
            return;
        }

        if (_latestFrame is null)
        {
            MonitorStatusText.Text = "Turn the camera on before calibrating the alert baseline.";
            CalibrationGuardText.Text = "Alert baseline needs a live camera view. Turn the camera on, sit alert, then calibrate.";
            return;
        }

        _faceCueAnalyzer.Reset();
        _faceLandmarkCueAnalyzer.Reset();
        _eyeCueStartedAt = null;
        _jawCueStartedAt = null;
        _currentFaceAnalysis = null;
        _currentFaceFeatureDetection = FaceFeatureDetection.None;
        _alertBaselineSavedAtUtc = null;
        _alertBaselineCameraName = "";
        _alertBaselineModeLabel = "";
        _alertBaselineCalibrationActive = true;
        ResetLandmarkTracking();
        _activeFaceCueLayout = null;
        _lastFaceFeatureLockAt = DateTime.MinValue;
        CalibrateAlertBaselineButton.Content = AlertBaselineInProgressButtonText;
        CalibrateAlertBaselineButton.IsEnabled = false;
        CalibrationGuardText.Text = "Calibrating alert baseline: stay awake, alert, symptom-free, and naturally relaxed.";
        MonitorStatusText.Text = "Calibrating alert baseline. Keep eyes naturally open and mouth relaxed.";
    }

    private void OpenAvatarSystemClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = GetAvatarDataFolder();
            Directory.CreateDirectory(folder);
            var snapshot = CreateAvatarReportSnapshot(folder);
            QueueAvatarReportSave(snapshot);
            _lastGoodFeatureMeshHtmlPath = LastGoodFeatureMeshStore.GetHtmlPath(folder);
            _lastGoodThreeDdfaHtmlPath = LastGoodThreeDdfaStore.GetHtmlPath(folder);
            _avatarModelHtmlPath = AvatarModelStore.GetHtmlPath(folder);
            _avatarSystemDashboardPath = GetAvatarSystemDashboardHtmlPath(folder);
            EnsureAvatarSystemPlaceholder(_avatarSystemDashboardPath);
            OpenLocalFile(_avatarSystemDashboardPath);
            var status = _lastGoodFeatureMeshSamples.Count > 0 || _currentThreeDdfaOnnxResponse is { Ok: true, HasFace: true }
                ? $"Opened live Avatar System: {_avatarSystemDashboardPath}"
                : $"Opened live waiting Avatar System. Confirm {CurrentAvatarProfileDisplayName} and start avatar capture.";
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

    private void OpenLastGoodFeaturesClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = GetAvatarDataFolder();
            Directory.CreateDirectory(folder);
            var files = _lastGoodFeatureMeshStore.Write(
                folder,
                new LastGoodFeatureMeshReport
                {
                    SubjectId = CurrentAvatarProfileId,
                    SubjectDisplayName = CurrentAvatarProfileDisplayName,
                    AvatarModelProgressHtmlPath = AvatarModelStore.GetHtmlPath(folder),
                    ReconstructionLane = CreateFaceReconstructionLaneStatus(),
                    Samples = _lastGoodFeatureMeshSamples.ToList()
                });
            _lastGoodFeatureMeshJsonPath = files.JsonPath;
            _lastGoodFeatureMeshHtmlPath = files.HtmlPath;
            OpenLocalFile(files.HtmlPath);
            var status = _lastGoodFeatureMeshSamples.Count > 0
                ? $"Opened MediaPipe Last 5 Feature Locks: {files.HtmlPath}"
                : $"Opened MediaPipe Last 5 Feature Locks. Confirm {CurrentAvatarProfileDisplayName} and let the fast tracker get a good eye/mouth lock to populate it.";
            SetStatus(status);
            MonitorStatusText.Text = status;
        }
        catch (Exception ex)
        {
            var status = $"Could not open MediaPipe Last 5 Feature Locks: {ex.Message}";
            SetStatus(status);
            MonitorStatusText.Text = status;
        }
    }

    private async void OpenLastGoodThreeDdfaClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = GetAvatarDataFolder();
            Directory.CreateDirectory(folder);
            var report = new LastGoodThreeDdfaReport
            {
                SubjectId = CurrentAvatarProfileId,
                SubjectDisplayName = CurrentAvatarProfileDisplayName,
                AvatarModelProgressHtmlPath = AvatarModelStore.GetHtmlPath(folder),
                ReconstructionLane = CreateFaceReconstructionLaneStatus(),
                Samples = _lastGoodThreeDdfaSamples.ToList()
            };
            SetStatus("Writing 3DDFA Last 5 Dense Reconstructions...");
            var files = await Task.Run(() => _lastGoodThreeDdfaStore.Write(folder, report));
            if (_isClosing)
            {
                return;
            }

            _lastGoodThreeDdfaJsonPath = files.JsonPath;
            _lastGoodThreeDdfaHtmlPath = files.HtmlPath;
            OpenLocalFile(files.HtmlPath);
            var status = _lastGoodThreeDdfaSamples.Count > 0
                ? $"Opened 3DDFA Last 5 Dense Reconstructions: {files.HtmlPath}"
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
            _lastGoodFeatureMeshJsonPath = "";
            _lastGoodFeatureMeshHtmlPath = "";
            _lastGoodThreeDdfaJsonPath = "";
            _lastGoodThreeDdfaHtmlPath = "";
            _lastGoodFeatureMeshSamples.Clear();
            _lastGoodThreeDdfaSamples.Clear();
            RefreshLastGoodFeatureMeshStabilityAudit();
            _lastGoodFeatureMeshStatus = "last good feature mesh waiting";
            _avatarRecentMeshStabilityHold = false;
            _avatarRecentMeshStabilitySummary = "";
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

    private void SymptomCaptureClicked(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        if (_symptomCaptureActive && _activeEpisodeStartedAt is not null)
        {
            EndActiveEpisode(now, _latestFrame, "Symptom capture stopped");
            ResetEpisodeState();
            MonitorStatusText.Text = "Symptom capture stopped.";
            return;
        }

        MarkSymptomActivity(now, "Symptoms marked; alert-baseline calibration delayed for one symptom-free hour.");

        if (_activeEpisodeStartedAt is not null)
        {
            const string symptomConfirmation = "User marked symptoms during active event capture";
            if (!_activeTriggerReasons.Any(existing => string.Equals(existing, symptomConfirmation, StringComparison.OrdinalIgnoreCase)))
            {
                _activeTriggerReasons.Add(symptomConfirmation);
            }

            MonitorStatusText.Text = "Symptoms marked during active capture. Alert baseline is held and event evidence is recording.";
            return;
        }

        if (_latestFrame is null)
        {
            MonitorStatusText.Text = "Symptoms marked. Turn the camera on to record event video.";
            return;
        }

        Directory.CreateDirectory(_outputFolder);
        _symptomCaptureActive = true;
        SymptomCaptureButton.Content = SymptomCaptureStopButtonText;
        var started = StartActiveEpisode(
            _latestFrame,
            now,
            GetMotionThreshold(),
            GetStillnessSeconds(),
            "Symptom capture",
            [
                "User started symptom capture before or during a possible episode",
                "Alert-baseline calibration held so this state is not learned as the alert reference"
            ]);
        if (!started)
        {
            _symptomCaptureActive = false;
            SymptomCaptureButton.Content = SymptomCaptureStartButtonText;
            return;
        }

        MonitorStatusText.Text = "Symptom capture started. Alert baseline is held and sleepy-state evidence is recording.";
    }

    private void ProcessFrame(BitmapSource bitmap)
    {
        if (!_sleepEventWatchActive && _activeEpisodeStartedAt is null)
        {
            _currentFaceAnalysis = AnalyzeFaceCues(bitmap);
            var idleMetrics = CreateOverlayMetrics(null);
            var idleTrigger = "Preview only. Face tracking overlay is active; start Sleep Event Watch to log events.";
            UpdateTrackingOverlay("Tracking idle", idleMetrics, idleTrigger, "#37506a");
            UpdateAlertBaselineCalibrationStatus();
            return;
        }

        byte[] sample;
        lock (_frameLock)
        {
            sample = CreateFrameSample(bitmap);
        }

        if (_previousSample is null)
        {
            _previousSample = sample;
            MonitorStatusText.Text = "Episode monitor armed. Building a motion baseline.";
            _currentFaceAnalysis = AnalyzeFaceCues(bitmap);
            var metrics = CreateOverlayMetrics(null);
            var trigger = "Building a motion baseline from the current camera view.";
            UpdateTrackingOverlay("Tracking armed", metrics, trigger, "#37506a");
            if (_activeEpisodeStartedAt is null)
            {
                BufferAnnotatedFrame(bitmap, DateTime.Now, "Tracking armed", metrics, trigger, "#37506a");
            }
            else
            {
                WriteAnnotatedVideoFrame(bitmap, DateTime.Now, "Event recording", metrics, trigger, "#dc5b5b");
            }
            UpdateAlertBaselineCalibrationStatus();
            return;
        }

        var now = DateTime.Now;
        var motion = CalculateFrameMotionPercent(_previousSample, sample);
        _previousSample = sample;
        _currentFaceAnalysis = AnalyzeFaceCues(bitmap);
        if (_activeEpisodeStartedAt is not null)
        {
            UpdateLandmarkEventEvidence(motion);
        }

        ProcessEpisodeMotion(bitmap, now, motion);
        UpdateAlertBaselineCalibrationStatus();
    }

    private void ProcessEpisodeMotion(BitmapSource bitmap, DateTime now, double motion)
    {
        var threshold = GetMotionThreshold();
        var stillnessSeconds = GetStillnessSeconds();
        var faceCueSeconds = GetFaceCueSeconds();
        var faceCueReasons = ProcessFaceCues(now);

        if (_activeEpisodeStartedAt is null && faceCueReasons.Count > 0)
        {
            StartActiveEpisode(bitmap, now, threshold, stillnessSeconds, "Face cue event", faceCueReasons);
        }

        if (motion <= threshold)
        {
            _lowMotionStartedAt ??= now;
            _episodeMotionSum += motion;
            _episodeMotionSamples++;

            var lowMotionFor = now - _lowMotionStartedAt.Value;
            if (_activeEpisodeStartedAt is null && lowMotionFor.TotalSeconds >= stillnessSeconds)
            {
                StartActiveEpisode(
                    bitmap,
                    _lowMotionStartedAt.Value,
                    threshold,
                    stillnessSeconds,
                    "Possible sleep onset",
                    [$"Low motion persisted for {stillnessSeconds:0}s at or below {threshold:0.0}%"]);
            }

            MonitorStatusText.Text = _activeEpisodeStartedAt is null
                ? $"Very still: {lowMotionFor.TotalSeconds:0}s of {stillnessSeconds:0}s needed. Motion: {motion:0.0}%"
                : $"Possible episode active. Duration: {(now - _activeEpisodeStartedAt.Value).TotalMinutes:0.0} min. Motion: {motion:0.0}%";
            var state = _activeEpisodeStartedAt is null ? "Possible event cue" : "Event recording";
            var metrics = CreateOverlayMetrics(motion);
            var trigger = _activeEpisodeStartedAt is null
                ? $"Low motion for {lowMotionFor.TotalSeconds:0}s of {stillnessSeconds:0}s required. Face cue time {faceCueSeconds:0}s."
                : $"Event duration {(now - _activeEpisodeStartedAt.Value).TotalSeconds:0}s. Trigger: {string.Join("; ", _activeTriggerReasons)}";
            var accent = _activeEpisodeStartedAt is null ? "#d7a53a" : "#dc5b5b";
            UpdateTrackingOverlay(state, metrics, trigger, accent);
            WriteAnnotatedVideoFrame(bitmap, now, state, metrics, trigger, accent);
            return;
        }

        if (_activeEpisodeStartedAt is not null)
        {
            if (_activeEpisodeEarliestAutoEndAt is DateTime earliestEnd && now < earliestEnd)
            {
                var holdState = _symptomCaptureActive ? "Symptom capture" : "Event recording";
                var holdMetrics = CreateOverlayMetrics(motion);
                var holdTrigger = $"Capture active. Auto-close available in {(earliestEnd - now).TotalSeconds:0}s. Trigger: {string.Join("; ", _activeTriggerReasons)}";
                UpdateTrackingOverlay(holdState, holdMetrics, holdTrigger, "#dc5b5b");
                WriteAnnotatedVideoFrame(bitmap, now, holdState, holdMetrics, holdTrigger, "#dc5b5b");
                return;
            }

            var endingState = "Event ending cue";
            var endingMetrics = CreateOverlayMetrics(motion);
            var endingTrigger = $"Motion returned above threshold. Event will close as: Motion returned.";
            UpdateTrackingOverlay(endingState, endingMetrics, endingTrigger, "#4aa36b");
            WriteAnnotatedVideoFrame(bitmap, now, endingState, endingMetrics, endingTrigger, "#4aa36b");
        }

        EndActiveEpisode(now, bitmap, "Motion returned");
        ResetEpisodeState();
        MonitorStatusText.Text = $"Awake/moving baseline. Motion: {motion:0.0}%";
        var baselineMetrics = CreateOverlayMetrics(motion);
        UpdateTrackingOverlay("Acceptable baseline", baselineMetrics, "Motion is above the event threshold.", "#4aa36b");
        BufferAnnotatedFrame(bitmap, now, "Acceptable baseline", baselineMetrics, "Motion is above the event threshold.", "#4aa36b");
    }

    private static byte[] CreateFrameSample(BitmapSource bitmap)
    {
        var scale = Math.Min(1d, 96d / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
        var scaled = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        var converted = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = Math.Max(1, (width * converted.Format.BitsPerPixel + 7) / 8);
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static double CalculateFrameMotionPercent(byte[] previous, byte[] current)
    {
        var length = Math.Min(previous.Length, current.Length);
        if (length == 0)
        {
            return 0d;
        }

        long total = 0;
        for (var i = 0; i < length; i++)
        {
            total += Math.Abs(previous[i] - current[i]);
        }

        return total / (double)(length * 255) * 100d;
    }

    private FaceCueAnalysis? AnalyzeFaceCues(BitmapSource bitmap)
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

            return _faceCueAnalyzer.Analyze(bitmap, layout);
        }
        catch
        {
            MonitorStatusText.Text = "Face tracking is resyncing with the latest camera frame.";
            return null;
        }
    }

    private void QueueFaceFeatureDetection(BitmapSource bitmap, DateTime now)
    {
        if (_isClosing
            || !_faceLandmarkTracker.IsAvailable
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
        }

        if (Interlocked.Exchange(ref _faceFeatureDetectionPending, 1) == 0)
        {
            _ = Task.Run(ProcessPendingFaceFeatureDetectionFramesAsync);
        }
    }

    private async Task ProcessPendingFaceFeatureDetectionFramesAsync()
    {
        while (!_isClosing)
        {
            BitmapSource? bitmap;
            DateTime capturedAtUtc;
            lock (_faceFeatureDetectionFrameLock)
            {
                bitmap = _pendingFaceFeatureDetectionFrame;
                capturedAtUtc = _pendingFaceFeatureDetectionCapturedAtUtc;
                _pendingFaceFeatureDetectionFrame = null;
                _pendingFaceFeatureDetectionCapturedAtUtc = DateTime.MinValue;
            }

            if (bitmap is null)
            {
                break;
            }

            var result = FaceLandmarkTrackingResult.None;
            try
            {
                lock (_faceLandmarkTrackerLock)
                {
                    result = _isClosing
                        ? FaceLandmarkTrackingResult.None
                        : _faceLandmarkTracker.Detect(bitmap, capturedAtUtc == DateTime.MinValue ? DateTime.UtcNow : capturedAtUtc);
                }

                await ApplyFaceFeatureDetectionResultAsync(result);
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => SetStatus($"Landmark tracker paused: {ex.Message}"), DispatcherPriority.Background);
            }
        }

        Interlocked.Exchange(ref _faceFeatureDetectionPending, 0);
        lock (_faceFeatureDetectionFrameLock)
        {
            if (_pendingFaceFeatureDetectionFrame is not null
                && Interlocked.Exchange(ref _faceFeatureDetectionPending, 1) == 0)
            {
                _ = Task.Run(ProcessPendingFaceFeatureDetectionFramesAsync);
            }
        }
    }

    private Task ApplyFaceFeatureDetectionResultAsync(FaceLandmarkTrackingResult result)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            if (_isClosing)
            {
                return;
            }

            var now = DateTime.UtcNow;
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
                _currentFaceLandmarkCueAnalysis = _faceLandmarkCueAnalyzer.Analyze(_currentFaceLandmarkMetrics);
                _currentFaceLandmarkTrendAnalysis = _faceLandmarkTrendAnalyzer.Update(_currentFaceLandmarkMetrics);
                _currentFaceLockStabilityAnalysis = _faceLockStabilityAnalyzer.Update(
                    _currentFaceFeatureDetection,
                    _currentFaceLandmarkFrame,
                    _currentFaceLandmarkMetrics);
                TrackFeatureOverlayFrame(now);
                TrackLastGoodFeatureMeshSample(now);
                QueueThreeDdfaOnnxReconstruction(_latestFrame, now);
                UpdateAvatarCaptureState(now);
                UpdateAlertBaselineCalibrationStatus();
            }
            else if (!HasUsableFaceFeatureLock(now))
            {
                _currentFaceFeatureDetection = FaceFeatureDetection.None;
                ResetLandmarkTracking();
            }

            UpdateFaceCueGuideOverlay(_latestFrame);
        }, DispatcherPriority.Background).Task;
    }

    private void QueueThreeDdfaOnnxReconstruction(BitmapSource? bitmap, DateTime capturedAtUtc)
    {
        if (_isClosing
            || bitmap is null
            || !_threeDdfaOnnxEnvironment.IsReady
            || AvatarSubjectCheckBox.IsChecked != true
            || !_avatarLearningRequested
            || !_currentFaceFeatureDetection.HasFace
            || (capturedAtUtc - _lastThreeDdfaOnnxRequestAtUtc).TotalMilliseconds < 900d)
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
        _ = Task.Run(() => ProcessThreeDdfaOnnxReconstructionAsync(frame, capturedAtUtc, faceBox));
    }

    private async Task ProcessThreeDdfaOnnxReconstructionAsync(
        BitmapSource bitmap,
        DateTime capturedAtUtc,
        ThreeDdfaOnnxSidecarFaceBox? faceBox)
    {
        ThreeDdfaOnnxSidecarResponse response;
        LastGoodFeatureThreeDdfaSnapshot? snapshot = null;
        try
        {
            response = _threeDdfaOnnxClient.Reconstruct(
                bitmap,
                capturedAtUtc,
                faceBox,
                returnDenseVertices: true,
                denseSampleStride: 1);
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
        }

        await Dispatcher.InvokeAsync(() =>
        {
            if (_isClosing)
            {
                return;
            }

            _currentThreeDdfaOnnxResponse = response;
            UpdateAvatarLearningStatusUi();
            TrackLastGoodThreeDdfaSnapshot(snapshot);
            UpdateTrackingFidelityHealthText();
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

    private void TrackLastGoodThreeDdfaSnapshot(LastGoodFeatureThreeDdfaSnapshot? snapshot)
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

        _lastGoodFeatureMeshStatus =
            $"Review caches: MediaPipe {_lastGoodFeatureMeshSamples.Count}/{LastGoodFeatureMeshRetainedSampleCount}; 3DDFA {_lastGoodThreeDdfaSamples.Count}/{LastGoodThreeDdfaRetainedSampleCount} ({snapshot.VertexCount:n0} vertices).";
    }

    private static LastGoodFeatureThreeDdfaSnapshot? CreateThreeDdfaLastGoodSnapshot(
        ThreeDdfaOnnxSidecarResponse response,
        DateTime sampleCapturedAtUtc)
    {
        if (!response.Ok
            || !response.HasFace
            || response.DenseVertexCount < 30000
            || response.DenseVertices.Count < 30000
            || response.DenseEdges.Count == 0)
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
        return new LastGoodFeatureThreeDdfaSnapshot
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
            TopologyEdges = response.DenseEdges
                .Select(edge => new LastGoodFeatureMeshWireframeEdge
                {
                    FromIndex = edge.FromIndex,
                    ToIndex = edge.ToIndex,
                    Role = "surface",
                    Source = "3ddfa-full-resolution-topology",
                    LengthPercent = 0d,
                    ConfidencePercent = confidencePercent
                })
                .ToList(),
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

    private void TrackLastGoodFeatureMeshSample(DateTime utcNow)
    {
        if (AvatarSubjectCheckBox.IsChecked != true)
        {
            _lastGoodFeatureMeshStatus = $"Last good mesh waiting: confirm {CurrentAvatarProfileDisplayName}";
            RefreshLastGoodFeatureMeshStabilityAudit();
            UpdateTrackingFidelityHealthText();
            return;
        }

        if ((utcNow - _lastGoodFeatureMeshCapturedAtUtc).TotalMilliseconds < 100d)
        {
            return;
        }

        if (!LastGoodFeatureMeshSampleFactory.TryCreate(
                _currentFaceLandmarkFrame,
                _currentFaceLandmarkMetrics,
                _currentFaceLockStabilityAnalysis,
                _currentAvatarCaptureQuality,
                out var sample,
                out var reason,
                faceGeometry: _currentFaceFrameGeometry))
        {
            _lastGoodFeatureMeshStatus = $"Last good mesh waiting: {reason}";
            RefreshLastGoodFeatureMeshStabilityAudit();
            UpdateTrackingFidelityHealthText();
            return;
        }

        if (_lastGoodFeatureMeshSamples.Count > 0
            && string.Equals(_lastGoodFeatureMeshSamples[^1].SampleId, sample.SampleId, StringComparison.Ordinal))
        {
            return;
        }

        _lastGoodFeatureMeshSamples.Add(sample);
        if (_lastGoodFeatureMeshSamples.Count > LastGoodFeatureMeshRetainedSampleCount)
        {
            _lastGoodFeatureMeshSamples.RemoveRange(0, _lastGoodFeatureMeshSamples.Count - LastGoodFeatureMeshRetainedSampleCount);
        }

        _lastGoodFeatureMeshCapturedAtUtc = utcNow;
        _lastGoodFeatureMeshStatus =
            $"Review caches: MediaPipe {_lastGoodFeatureMeshSamples.Count}/{LastGoodFeatureMeshRetainedSampleCount}; 3DDFA {_lastGoodThreeDdfaSamples.Count}/{LastGoodThreeDdfaRetainedSampleCount}.";
        RefreshLastGoodFeatureMeshStabilityAudit();
        if (_showLiveWireframePreview)
        {
            DrawLiveWireframePreview();
        }

        UpdateTrackingFidelityHealthText();
    }

    private void RefreshLastGoodFeatureMeshStabilityAudit()
    {
        _lastGoodFeatureMeshStability = LastGoodFeatureMeshStabilityAnalyzer.Analyze(_lastGoodFeatureMeshSamples);
        _avatarRecentMeshStabilityHold =
            _lastGoodFeatureMeshStability.HeadLockedSampleCount >= LastGoodFeatureMeshStabilityMinimumSamplesToHold
            && _lastGoodFeatureMeshStability.YawRangeDegrees >= LastGoodFeatureMeshBRotationMinimumRangeDegrees
            && _lastGoodFeatureMeshStability.YawHealthPercent > 0d
            && _lastGoodFeatureMeshStability.YawHealthPercent < LastGoodFeatureMeshStabilityHoldThresholdPercent;
        _avatarRecentMeshStabilitySummary = _avatarRecentMeshStabilityHold
            ? FormatLastGoodFeatureMeshStabilitySummary(_lastGoodFeatureMeshStability)
            : "";
    }

    private static string FormatLastGoodFeatureMeshStabilitySummary(LastGoodFeatureMeshStabilityReport stability)
    {
        var finding = stability.YawFindings.FirstOrDefault() ?? stability.Findings.FirstOrDefault();
        var findingText = string.IsNullOrWhiteSpace(finding) ? "" : $" Finding: {finding}";
        return $"Recent B head-turn lock failed ({stability.YawHealthPercent:0.#}% health; B range {stability.YawRangeDegrees:0.#} deg; worst B-axis feature drift {stability.YawWorstFeatureDriftPercent:0.#}%; left/right samples {stability.YawLeftSampleCount}/{stability.YawRightSampleCount}).{findingText}";
    }

    private bool HasUsableFaceFeatureLock(DateTime now)
    {
        return _currentFaceFeatureDetection.HasFace
            && (now - _lastFaceFeatureLockAt).TotalSeconds <= 4d;
    }

    private bool HasFreshFaceFeatureLock(DateTime now)
    {
        return _currentFaceFeatureDetection.HasFace
            && (now - _lastFaceFeatureLockAt).TotalSeconds <= 1.5d;
    }

    private void ResetLandmarkTracking()
    {
        _currentFaceLandmarkFrame = FaceLandmarkFrame.None;
        _currentFaceLandmarkMetrics = FaceLandmarkMetrics.None;
        _currentFaceLandmarkCueAnalysis = null;
        _currentFaceLandmarkTrendAnalysis = FaceLandmarkTrendAnalysis.Waiting;
        _currentFaceLockStabilityAnalysis = FaceLockStabilityAnalysis.Waiting;
        _currentFaceFrameGeometry = FaceFrameGeometry.None;
        _currentAvatarCaptureQuality = AvatarCaptureQualityAssessment.Waiting;
        _faceLandmarkTracker.Reset();
        _faceLandmarkReconstructor.Reset();
        _faceLandmarkMetricCalculator.Reset();
        _faceLandmarkTrendAnalyzer.Reset();
        _faceLockStabilityAnalyzer.Reset();
    }

    private void UpdateAvatarCaptureState(DateTime utcNow)
    {
        if (AvatarSubjectCheckBox.IsChecked != true)
        {
            ResetAvatarCaptureGate("subject not confirmed; avatar capture paused");
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

        ResetAvatarCaptureGate(AvatarCaptureStatusReason, accepted: true);
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
            SubjectConfirmed = AvatarSubjectCheckBox.IsChecked == true,
            AvatarCaptureRequested = _avatarLearningRequested,
            CaptureGateAccepted = _avatarCaptureGateAccepted,
            CaptureGateReason = _avatarCaptureGateReason
        });
    }

    private void SaveAvatarReportsIfDue(DateTime utcNow)
    {
        if (!_avatarLearningRequested
            || AvatarSubjectCheckBox.IsChecked != true
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
        var subjectConfirmed = AvatarSubjectCheckBox.IsChecked == true
            && !string.IsNullOrWhiteSpace(CurrentAvatarProfileId);

        return new AvatarReportSnapshot(
            folder,
            CurrentAvatarProfileId,
            CurrentAvatarProfileDisplayName,
            CloneCaptureQuality(_currentAvatarCaptureQuality),
            subjectConfirmed,
            _avatarLearningRequested,
            state.Active,
            state.Title,
            state.Detail,
            CloneForBackgroundSave(_currentFaceFrameGeometry),
            _lastGoodFeatureMeshStability,
            _lastGoodFeatureMeshSamples.ToList(),
            _lastGoodThreeDdfaSamples.ToList(),
            CloneForBackgroundSave(CreateFaceReconstructionLaneStatus()));
    }

    private static T CloneForBackgroundSave<T>(T value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json) ?? value;
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

    private static AvatarReportSaveResult WriteAvatarReports(AvatarReportSnapshot snapshot)
    {
        Directory.CreateDirectory(snapshot.Folder);

        var avatarSystemDashboardStore = new AvatarSystemDashboardStore();
        var lastGoodFeatureMeshStore = new LastGoodFeatureMeshStore();
        var lastGoodThreeDdfaStore = new LastGoodThreeDdfaStore();
        var avatarModelObservationStore = new AvatarModelObservationStore();
        var avatarModelHistoryStore = new AvatarModelHistoryStore();
        var avatarModelStore = new AvatarModelStore();

        var lastGoodFeatureMeshFiles = lastGoodFeatureMeshStore.Write(
            snapshot.Folder,
            new LastGoodFeatureMeshReport
            {
                SubjectId = snapshot.SubjectId,
                SubjectDisplayName = snapshot.SubjectDisplayName,
                AvatarModelProgressHtmlPath = AvatarModelStore.GetHtmlPath(snapshot.Folder),
                ReconstructionLane = snapshot.ReconstructionLane,
                Samples = snapshot.LastGoodFeatureMeshSamples
            });
        var lastGoodThreeDdfaFiles = lastGoodThreeDdfaStore.Write(
            snapshot.Folder,
            new LastGoodThreeDdfaReport
            {
                SubjectId = snapshot.SubjectId,
                SubjectDisplayName = snapshot.SubjectDisplayName,
                AvatarModelProgressHtmlPath = AvatarModelStore.GetHtmlPath(snapshot.Folder),
                ReconstructionLane = snapshot.ReconstructionLane,
                Samples = snapshot.LastGoodThreeDdfaSamples
            });
        var avatarModelObservations = avatarModelObservationStore.MergeAndWrite(
            snapshot.Folder,
            snapshot.SubjectId,
            snapshot.SubjectDisplayName,
            snapshot.LastGoodFeatureMeshSamples,
            snapshot.LastGoodThreeDdfaSamples);
        var avatarModel = AvatarModelBuilder.Build(avatarModelObservations);
        var avatarModelHistory = avatarModelHistoryStore.RecordAndWrite(
            snapshot.Folder,
            avatarModelObservations,
            avatarModel);
        var avatarModelJsonPath = avatarModelStore.Write(snapshot.Folder, avatarModel);

        var dashboard = new AvatarSystemDashboard
        {
            SubjectId = snapshot.SubjectId,
            SubjectDisplayName = snapshot.SubjectDisplayName,
            SubjectConfirmed = snapshot.SubjectConfirmed,
            AvatarCaptureRequested = snapshot.AvatarCaptureRequested,
            AvatarCaptureActive = snapshot.AvatarCaptureActive,
            AvatarCaptureStatus = snapshot.AvatarCaptureStatus,
            AvatarCaptureCorrection = snapshot.AvatarCaptureCorrection,
            CurrentCaptureQuality = snapshot.CaptureQuality,
            CurrentFaceFrameGeometry = snapshot.FaceFrameGeometry,
            LastGoodFeatureStability = snapshot.LastGoodFeatureMeshStability,
            ReconstructionLane = snapshot.ReconstructionLane,
            LastGoodFeatureSampleCount = snapshot.LastGoodFeatureMeshSamples.Count,
            LastGoodThreeDdfaSampleCount = snapshot.LastGoodThreeDdfaSamples.Count,
            LastGoodFeatureStatus = snapshot.LastGoodFeatureMeshSamples.Count > 0
                ? $"Retained {snapshot.LastGoodFeatureMeshSamples.Count} MediaPipe feature lock sample(s) and {snapshot.LastGoodThreeDdfaSamples.Count} 3DDFA dense sample(s) for separate review pages."
                : "Waiting for direct eye/mouth lock before retaining MediaPipe feature-lock samples.",
            LastGoodFeaturesHtmlPath = lastGoodFeatureMeshFiles.HtmlPath,
            LastGoodThreeDdfaHtmlPath = lastGoodThreeDdfaFiles.HtmlPath,
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

        var dashboardJsonPath = avatarSystemDashboardStore.Write(snapshot.Folder, dashboard);
        return new AvatarReportSaveResult(
            lastGoodFeatureMeshFiles.JsonPath,
            lastGoodFeatureMeshFiles.HtmlPath,
            lastGoodThreeDdfaFiles.JsonPath,
            lastGoodThreeDdfaFiles.HtmlPath,
            AvatarSystemDashboardStore.GetHtmlPath(dashboardJsonPath),
            avatarModelJsonPath,
            AvatarModelStore.GetHtmlPath(snapshot.Folder),
            AvatarModelObservationStore.GetJsonPath(snapshot.Folder));
    }

    private void ApplyAvatarReportSaveResult(AvatarReportSaveResult result)
    {
        if (_isClosing)
        {
            return;
        }

        _lastGoodFeatureMeshJsonPath = result.LastGoodFeatureMeshJsonPath;
        _lastGoodFeatureMeshHtmlPath = result.LastGoodFeatureMeshHtmlPath;
        _lastGoodThreeDdfaJsonPath = result.LastGoodThreeDdfaJsonPath;
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

    private List<string> ProcessFaceCues(DateTime now)
    {
        var reasons = new List<string>();
        var regionAnalysis = _currentFaceAnalysis is { BaselineReady: true } readyRegionAnalysis
            ? readyRegionAnalysis
            : null;
        var landmarkAnalysis = _currentFaceLandmarkCueAnalysis is { BaselineReady: true } readyLandmarkAnalysis
            ? readyLandmarkAnalysis
            : null;
        var trendAnalysis = _currentFaceLandmarkTrendAnalysis.HasUsableTrend
            ? _currentFaceLandmarkTrendAnalysis
            : null;

        if (regionAnalysis is null && landmarkAnalysis is null && trendAnalysis is null)
        {
            _eyeCueStartedAt = null;
            _jawCueStartedAt = null;
            _eyeTrendCueStartedAt = null;
            return reasons;
        }

        var cueSeconds = GetFaceCueSeconds();
        var eyeDropPercent = MaxSignal(regionAnalysis?.EyeDropPercent, landmarkAnalysis?.EyeClosurePercent);
        var jawChangePercent = MaxSignal(
            regionAnalysis?.JawChangePercent,
            landmarkAnalysis?.MouthOpeningChangePercent,
            landmarkAnalysis?.JawDroopChangePercent);
        var qualityPercent = MaxSignal(regionAnalysis?.QualityPercent, landmarkAnalysis?.QualityPercent);
        var compositeCuePercent = MaxSignal(regionAnalysis?.CompositeCuePercent, landmarkAnalysis?.CompositeCuePercent);
        var trendCuePercent = trendAnalysis?.TrendCuePercent ?? 0d;
        var eyeCueActive = false;
        if (eyeDropPercent >= GetEyeCueThreshold())
        {
            _eyeCueStartedAt ??= now;
            if ((now - _eyeCueStartedAt.Value).TotalSeconds >= cueSeconds)
            {
                eyeCueActive = true;
                reasons.Add($"Primary eye cue persisted for {cueSeconds:0}s: {DescribeEyeSignal(regionAnalysis, landmarkAnalysis, _currentFaceLandmarkMetrics, eyeDropPercent)}");
            }
        }
        else
        {
            _eyeCueStartedAt = null;
        }

        if (jawChangePercent >= GetJawCueThreshold())
        {
            _jawCueStartedAt ??= now;
            if ((now - _jawCueStartedAt.Value).TotalSeconds >= cueSeconds)
            {
                if (eyeCueActive || eyeDropPercent >= GetEyeCueThreshold() * 0.55d)
                {
                    reasons.Add($"Supporting mouth/jaw cue persisted for {cueSeconds:0}s: {DescribeMouthSignal(regionAnalysis, landmarkAnalysis, _currentFaceLandmarkMetrics, jawChangePercent)}");
                }
            }
        }
        else
        {
            _jawCueStartedAt = null;
        }

        if (trendAnalysis is not null
            && trendAnalysis.QualityPercent >= 50d
            && trendAnalysis.EyeClosingTrendPercent is double eyeTrend
            && eyeTrend >= GetEyeCueThreshold() * 0.45d
            && trendCuePercent >= GetCompositeCueThreshold() * 0.60d)
        {
            _eyeTrendCueStartedAt ??= now;
            if ((now - _eyeTrendCueStartedAt.Value).TotalSeconds >= Math.Max(1d, cueSeconds * 0.5d))
            {
                reasons.Add($"Primary eye trend persisted: eyelid aperture fell {eyeTrend:0}% over {trendAnalysis.WindowSeconds:0}s");
            }
        }
        else
        {
            _eyeTrendCueStartedAt = null;
        }

        if (reasons.Count == 0
            && qualityPercent >= 50d
            && compositeCuePercent >= GetCompositeCueThreshold()
            && eyeDropPercent >= GetEyeCueThreshold() * 0.70d)
        {
            reasons.Add($"Primary eye-led cue score reached {compositeCuePercent:0}% with eye closure signal {eyeDropPercent:0}%{DescribeMediaPipeEyeCorroboration(_currentFaceLandmarkCueAnalysis, _currentFaceLandmarkMetrics)}");
        }

        return reasons;
    }

    private static double MaxSignal(params double?[] values)
    {
        var maximum = 0d;
        foreach (var value in values)
        {
            if (value is double number)
            {
                maximum = Math.Max(maximum, number);
            }
        }

        return maximum;
    }

    private static string DescribeEyeSignal(
        FaceCueAnalysis? regionAnalysis,
        FaceLandmarkCueAnalysis? landmarkAnalysis,
        FaceLandmarkMetrics metrics,
        double selectedSignal)
    {
        if (landmarkAnalysis?.EyeClosurePercent is double landmarkEye
            && landmarkEye >= selectedSignal - 0.1d)
        {
            return $"landmark eyelid aperture closed {landmarkEye:0}% from awake baseline{DescribeMediaPipeEyeCorroboration(landmarkAnalysis, metrics)}";
        }

        if (regionAnalysis is not null)
        {
            return $"eye-region openness dropped {regionAnalysis.EyeDropPercent:0}% from awake baseline{DescribeMediaPipeEyeCorroboration(landmarkAnalysis, metrics)}";
        }

        return $"eye signal reached {selectedSignal:0}%{DescribeMediaPipeEyeCorroboration(landmarkAnalysis, metrics)}";
    }

    private static string DescribeMouthSignal(
        FaceCueAnalysis? regionAnalysis,
        FaceLandmarkCueAnalysis? landmarkAnalysis,
        FaceLandmarkMetrics metrics,
        double selectedSignal)
    {
        if (landmarkAnalysis?.MouthOpeningChangePercent is double mouthChange
            && mouthChange >= selectedSignal - 0.1d)
        {
            return $"landmark mouth opening increased {mouthChange:0}% from awake baseline{DescribeMediaPipeMouthCorroboration(landmarkAnalysis, metrics)}";
        }

        if (landmarkAnalysis?.JawDroopChangePercent is double jawDroopChange
            && jawDroopChange >= selectedSignal - 0.1d)
        {
            return $"landmark jaw contour dropped {jawDroopChange:0}% from awake baseline{DescribeMediaPipeMouthCorroboration(landmarkAnalysis, metrics)}";
        }

        if (regionAnalysis is not null)
        {
            return $"lower-face/jaw change measured {regionAnalysis.JawChangePercent:0}% from awake baseline{DescribeMediaPipeMouthCorroboration(landmarkAnalysis, metrics)}";
        }

        return $"mouth/jaw signal reached {selectedSignal:0}%{DescribeMediaPipeMouthCorroboration(landmarkAnalysis, metrics)}";
    }

    private static string DescribeMediaPipeEyeCorroboration(FaceLandmarkCueAnalysis? analysis, FaceLandmarkMetrics metrics)
    {
        if (analysis?.MediaPipeBlinkChangePercent is double blinkChange)
        {
            var raw = metrics.MediaPipeAverageEyeBlinkPercent is double rawBlink
                ? $", raw {rawBlink:0}%"
                : "";
            return $" (MediaPipe blink +{blinkChange:0}% from awake baseline{raw})";
        }

        return metrics.MediaPipeAverageEyeBlinkPercent is double blink
            ? $" (MediaPipe blink {blink:0}%)"
            : "";
    }

    private static string DescribeMediaPipeMouthCorroboration(FaceLandmarkCueAnalysis? analysis, FaceLandmarkMetrics metrics)
    {
        if (analysis?.MediaPipeMouthOpeningEvidencePercent is double mouthEvidence)
        {
            var jawChange = analysis.MediaPipeJawOpenChangePercent is double jaw
                ? $"jaw +{jaw:0}%"
                : "";
            var closeDrop = analysis.MediaPipeMouthCloseDropPercent is double close
                ? $"mouth-close drop {close:0}%"
                : "";
            var separator = jawChange.Length > 0 && closeDrop.Length > 0 ? ", " : "";
            return $" (MediaPipe mouth +{mouthEvidence:0}% from awake baseline: {jawChange}{separator}{closeDrop})";
        }

        if (metrics.MediaPipeJawOpenPercent is double jawOpen && metrics.MediaPipeMouthClosePercent is double mouthClose)
        {
            return $" (MediaPipe jaw open {jawOpen:0}%, mouth close {mouthClose:0}%)";
        }

        if (metrics.MediaPipeJawOpenPercent is double jawOnly)
        {
            return $" (MediaPipe jaw open {jawOnly:0}%)";
        }

        if (metrics.MediaPipeMouthClosePercent is double mouthOnly)
        {
            return $" (MediaPipe mouth close {mouthOnly:0}%)";
        }

        return "";
    }

    private bool StartActiveEpisode(
        BitmapSource bitmap,
        DateTime startedAt,
        double threshold,
        double stillnessSeconds,
        string eventName,
        IReadOnlyList<string>? triggerReasons = null)
    {
        var eventFolder = CreateEventFolder(startedAt);
        var videoPath = "";
        if (EventVideoCheckBox.IsChecked == true)
        {
            var requestedVideoPath = Path.Combine(eventFolder, "event_video.mp4");
            if (_eventRecorder.Start(requestedVideoPath, _preEventVideoFrames.Select(frame => frame.JpegBytes).ToArray()))
            {
                videoPath = requestedVideoPath;
            }
            else
            {
                MonitorStatusText.Text = "Event capture started without video. The event database and summaries will still be saved.";
            }
        }

        _activeEpisodeStartedAt = startedAt;
        _activeEpisodeEarliestAutoEndAt = DateTime.Now.AddSeconds(8);
        MarkSymptomActivity(startedAt, "Event capture started; alert-baseline calibration delayed for one symptom-free hour.");
        _activeEventFolder = eventFolder;
        _activeEventVideo = videoPath;
        _activeTriggerReasons = triggerReasons?.ToList() ??
        [
            $"Low motion persisted for {stillnessSeconds:0}s at or below {threshold:0.0}%"
        ];
        _landmarkEventAggregate.Reset();
        _landmarkEventTimeline.Reset();
        UpdateLandmarkEventEvidence(null);

        _episodeStartSnapshot = SnapshotCheckBox.IsChecked == true
            ? SaveSnapshot(bitmap, startedAt, "start", _activeEventFolder)
            : "";

        return true;
    }

    private string CreateEventFolder(DateTime timestamp)
    {
        Directory.CreateDirectory(_outputFolder);
        var folder = Path.Combine(_outputFolder, $"Episode_{timestamp:yyyy-MM-dd_HH-mm-ss}");
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
            return folder;
        }

        folder = Path.Combine(_outputFolder, $"Episode_{timestamp:yyyy-MM-dd_HH-mm-ss-fff}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private void TryDeleteEventFolder(string folder)
    {
        try
        {
            if (IsSafeEventFolderPath(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch
        {
        }
    }

    private string SaveSnapshot(BitmapSource bitmap, DateTime timestamp, string kind, string? folder = null)
    {
        try
        {
            var targetFolder = folder ?? Path.Combine(_outputFolder, $"AvatarBuilder_{timestamp:yyyy-MM-dd}");
            Directory.CreateDirectory(targetFolder);
            var path = Path.Combine(targetFolder, $"episode_{kind}_{timestamp:HH-mm-ss-fff}.jpg");
            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
            return path;
        }
        catch (Exception ex)
        {
            MonitorStatusText.Text = $"Episode event logged, but snapshot failed: {ex.Message}";
            return "";
        }
    }

    private void EndActiveEpisode(DateTime endedAt, BitmapSource? bitmap, string reason)
    {
        if (_activeEpisodeStartedAt is null)
        {
            return;
        }

        var endSnapshot = bitmap is not null && SnapshotCheckBox.IsChecked == true
            ? SaveSnapshot(bitmap, endedAt, "end", string.IsNullOrWhiteSpace(_activeEventFolder) ? null : _activeEventFolder)
            : "";
        _eventRecorder.Stop();

        var summaryFiles = WriteEventSummary(_activeEpisodeStartedAt.Value, endedAt, reason, endSnapshot);
        var files = string.Join(" | ", new[] { _activeEventVideo, _episodeStartSnapshot, endSnapshot, summaryFiles.JsonPath, summaryFiles.CsvPath, summaryFiles.TimelineJsonPath, summaryFiles.TimelineCsvPath }
            .Where(static path => !string.IsNullOrWhiteSpace(path)));
        var notes = $"Triggers: {string.Join("; ", _activeTriggerReasons)}. Ended: {reason}.";
        AddEpisodeEvent(_activeEpisodeStartedAt.Value, endedAt, reason, GetAverageMotionLabel(), files, notes, _activeEventFolder, _activeEventVideo, _episodeStartSnapshot, endSnapshot);

        MarkSymptomActivity(endedAt, "Event capture ended; alert-baseline calibration delayed for one symptom-free hour.");
    }

    private void ResetEpisodeState()
    {
        _lowMotionStartedAt = null;
        _eyeCueStartedAt = null;
        _jawCueStartedAt = null;
        _activeEpisodeStartedAt = null;
        _activeEpisodeEarliestAutoEndAt = null;
        _episodeStartSnapshot = "";
        _activeEventFolder = "";
        _activeEventVideo = "";
        _activeTriggerReasons = [];
        _episodeMotionSum = 0d;
        _episodeMotionSamples = 0;
        _previousSample = null;
        _lastRecordedVideoFrameAt = DateTime.MinValue;
        _symptomCaptureActive = false;
        SymptomCaptureButton.Content = SymptomCaptureStartButtonText;
        _landmarkEventAggregate.Reset();
        _landmarkEventTimeline.Reset();
    }

    private void UpdateLandmarkEventEvidence(double? motionPercent)
    {
        _landmarkEventAggregate.Update(
            _currentFaceLandmarkMetrics,
            _currentFaceLandmarkCueAnalysis,
            _currentFaceLandmarkTrendAnalysis,
            _currentFaceLockStabilityAnalysis,
            _faceLandmarkTracker.LastBackendStatus,
            _currentAvatarCaptureQuality);
        if (_activeEpisodeStartedAt is DateTime startedAt)
        {
            _landmarkEventTimeline.Add(
                startedAt,
                motionPercent,
                _currentFaceLandmarkMetrics,
                _currentFaceLandmarkCueAnalysis,
                _currentFaceLandmarkTrendAnalysis,
                _currentFaceLockStabilityAnalysis,
                _faceLandmarkTracker.LastBackendStatus,
                _currentAvatarCaptureQuality);
        }
    }

    private void ExportClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_outputFolder);
            var folder = Path.Combine(_outputFolder, "EpisodeLogs");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"episode_log_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv");
            var builder = new StringBuilder();
            builder.AppendLine("Started,Ended,Duration,Event,AverageMotion,Notes,Files");
            foreach (var item in _events.Reverse())
            {
                builder.AppendLine(string.Join(",", [
                    Csv(item.StartLabel),
                    Csv(item.EndLabel),
                    Csv(item.Duration),
                    Csv(item.Event),
                    Csv(item.AvgMotion),
                    Csv(item.Notes),
                    Csv(item.File)
                ]));
            }

            File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
            MonitorStatusText.Text = $"Episode log exported: {path}";
        }
        catch (Exception ex)
        {
            MonitorStatusText.Text = $"Could not export episode log: {ex.Message}";
        }
    }

    private void OpenEventLogClicked(object sender, RoutedEventArgs e)
    {
        var cutoff = DateTime.Today.AddDays(-29);
        var logEvents = new ObservableCollection<AvatarBuilderEvent>(
            _eventDatabase.LoadEventsSince(_outputFolder, cutoff)
                .Where(HasEventEvidenceFolder));
        var grid = CreateEventLogGrid(logEvents);
        var closeButton = new Button
        {
            Content = "Close",
            Width = 110,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var panel = new DockPanel { Margin = new Thickness(14) };
        var header = new TextBlock
        {
            Text = "Event Log - Last 30 Days",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        DockPanel.SetDock(closeButton, Dock.Bottom);
        panel.Children.Add(closeButton);
        panel.Children.Add(grid);

        var window = new Window
        {
            Owner = this,
            Title = "Avatar Builder Event Log",
            Width = 1100,
            Height = 620,
            MinWidth = 820,
            MinHeight = 460,
            Background = (Brush)FindResource("PanelBrush"),
            Foreground = Foreground,
            Content = panel
        };
        closeButton.Click += (_, _) => window.Close();
        window.Show();
    }

    private DataGrid CreateEventLogGrid(ObservableCollection<AvatarBuilderEvent> events)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            ItemsSource = events
        };
        grid.PreviewMouseRightButtonDown += EventGridRightClick;
        grid.Columns.Add(new DataGridTextColumn { Header = "Started", Binding = new System.Windows.Data.Binding(nameof(AvatarBuilderEvent.StartLabel)), Width = 150 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Ended", Binding = new System.Windows.Data.Binding(nameof(AvatarBuilderEvent.EndLabel)), Width = 150 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Duration", Binding = new System.Windows.Data.Binding(nameof(AvatarBuilderEvent.Duration)), Width = 95 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Event", Binding = new System.Windows.Data.Binding(nameof(AvatarBuilderEvent.Event)), Width = 180 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Avg Motion", Binding = new System.Windows.Data.Binding(nameof(AvatarBuilderEvent.AvgMotion)), Width = 110 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Notes", Binding = new System.Windows.Data.Binding(nameof(AvatarBuilderEvent.Notes)), Width = 280 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Files", Binding = new System.Windows.Data.Binding(nameof(AvatarBuilderEvent.File)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        var deleteItem = new MenuItem { Header = "Delete Event" };
        deleteItem.Click += (_, _) =>
        {
            if (grid.SelectedItem is AvatarBuilderEvent item && DeleteEventWithConfirmation(item, grid))
            {
                events.Remove(item);
            }
        };
        grid.ContextMenu = new ContextMenu();
        grid.ContextMenu.Items.Add(deleteItem);
        return grid;
    }

    private void DeleteSelectedEventClicked(object sender, RoutedEventArgs e)
    {
        if (EventGrid.SelectedItem is AvatarBuilderEvent item)
        {
            DeleteEventWithConfirmation(item, EventGrid);
        }
    }

    private void EventGridRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var row = FindAncestor<DataGridRow>(source);
        if (row is null)
        {
            return;
        }

        row.IsSelected = true;
        grid.SelectedItem = row.Item;
    }

    private static T? FindAncestor<T>(DependencyObject source)
        where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private bool DeleteEventWithConfirmation(AvatarBuilderEvent item, FrameworkElement owner)
    {
        var result = MessageBox.Show(
            Window.GetWindow(owner) ?? this,
            $"Delete the event from {item.StartLabel}? This removes the database row and deletes the event video/folder tied to it.",
            "Delete Event?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return false;
        }

        try
        {
            var deleted = _eventDatabase.DeleteEvent(_outputFolder, item.EventId) ?? item;
            DeleteEventArtifacts([deleted]);
            RemoveEventFromMainGrid(item.EventId);
            MonitorStatusText.Text = $"Deleted event from {item.StartLabel}.";
            return true;
        }
        catch (Exception ex)
        {
            MonitorStatusText.Text = $"Could not delete event: {ex.Message}";
            return false;
        }
    }

    private void RemoveEventFromMainGrid(string eventId)
    {
        var match = _events.FirstOrDefault(item => string.Equals(item.EventId, eventId, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            _events.Remove(match);
        }
    }

    private void BrowseOutputClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose episode output folder",
            InitialDirectory = Directory.Exists(_outputFolder) ? _outputFolder : AppContext.BaseDirectory,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _outputFolder = dialog.FolderName;
        SaveOutputFolderPointer(_outputFolder);
        var savedCurrentBaseline = SaveAlertBaselineToOutputFolder();
        var loadedBaseline = savedCurrentBaseline || LoadAlertBaselineFromOutputFolder(showStatus: false);
        UpdateOutputFolderText();
        var eventListStatus = SyncTodaysEventListAfterOutputFolderChange();
        InitializeAvatarProfiles(promptForStartupUser: false);
        var avatarStatus = PrepareAvatarCaptureFolder(showStatus: false);
        UpdateCalibrationGuard();
        var baselineStatus = loadedBaseline
            ? $"Output folder set: {_outputFolder}. Alert baseline is saved there."
            : $"Output folder set: {_outputFolder}. Calibrate Alert Baseline once when you are alert and symptom-free.";
        MonitorStatusText.Text = $"{baselineStatus} {eventListStatus} {avatarStatus}".Trim();
    }

    private void CloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AddEpisodeEvent(
        DateTime startedAt,
        DateTime? endedAt,
        string eventName,
        string averageMotion,
        string file,
        string notes,
        string eventFolder,
        string videoFile,
        string startSnapshot,
        string endSnapshot)
    {
        if (string.IsNullOrWhiteSpace(eventFolder) || !Directory.Exists(eventFolder))
        {
            return;
        }

        var item = new AvatarBuilderEvent
        {
            StartedAt = startedAt,
            EndedAt = endedAt,
            Event = eventName,
            AvgMotion = averageMotion,
            File = file,
            Notes = notes,
            EventFolder = eventFolder,
            VideoFile = videoFile,
            StartSnapshot = startSnapshot,
            EndSnapshot = endSnapshot
        };
        _events.Insert(0, item);
        SaveEventListItem(item);
    }

    private double GetMotionThreshold()
    {
        return Math.Clamp(ThresholdSlider.Value, 0.2d, 8d);
    }

    private double GetStillnessSeconds()
    {
        return Math.Clamp(StillnessSlider.Value, 15d, 900d);
    }

    private double GetEyeCueThreshold()
    {
        return Math.Clamp(EyeCueSlider.Value, 15d, 80d);
    }

    private double GetJawCueThreshold()
    {
        return Math.Clamp(JawCueSlider.Value, 15d, 120d);
    }

    private double GetFaceCueSeconds()
    {
        return Math.Clamp(FaceCueTimeSlider.Value, 1d, 20d);
    }

    private double GetCompositeCueThreshold()
    {
        return Math.Clamp(CompositeCueSlider.Value, 25d, 90d);
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

    private string GetAverageMotionLabel()
    {
        return _episodeMotionSamples <= 0 ? "" : $"{_episodeMotionSum / _episodeMotionSamples:0.0}%";
    }

    private void UpdateSettingLabels()
    {
        ThresholdValueText.Text = $"{GetMotionThreshold():0.0}%";
        StillnessValueText.Text = $"{GetStillnessSeconds():0}s";
        EyeCueValueText.Text = $"{GetEyeCueThreshold():0}%";
        JawCueValueText.Text = $"{GetJawCueThreshold():0}%";
        FaceCueTimeValueText.Text = $"{GetFaceCueSeconds():0}s";
        CompositeCueValueText.Text = $"{GetCompositeCueThreshold():0}%";
        FaceFieldXValueText.Text = $"{FaceFieldXSlider.Value:0}%";
        FaceFieldYValueText.Text = $"{FaceFieldYSlider.Value:0}%";
        FaceFieldSizeValueText.Text = $"{FaceFieldSizeSlider.Value:0}%";
    }

    private void UpdateOutputFolderText()
    {
        OutputFolderText.Text = $"{_outputFolder}{Environment.NewLine}{GetStorageLabel(_outputFolder)}";
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
            ? "Avatar Builder needs a storage folder for events, videos, calibration data, and avatar measurements."
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

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024L)
        {
            return $"{Math.Max(0L, bytes).ToString(CultureInfo.InvariantCulture)} B";
        }

        var size = (double)Math.Max(0L, bytes);
        var units = new[] { "KB", "MB", "GB", "TB" };
        var unitIndex = -1;
        do
        {
            size /= 1024d;
            unitIndex++;
        }
        while (size >= 1024d && unitIndex < units.Length - 1);

        return $"{size:0.##} {units[unitIndex]}";
    }

    private void SaveEventListItem(AvatarBuilderEvent item)
    {
        if (!HasEventEvidenceFolder(item))
        {
            return;
        }

        try
        {
            _eventDatabase.SaveEvent(_outputFolder, item);
        }
        catch (Exception ex)
        {
            MonitorStatusText.Text = $"Event was captured, but the event database could not be updated: {ex.Message}";
        }
    }

    private int LoadTodaysEventListFromOutputFolder()
    {
        _events.Clear();
        var loaded = _eventDatabase.LoadEventsForDate(_outputFolder, DateTime.Today);
        foreach (var item in loaded)
        {
            if (HasEventEvidenceFolder(item))
            {
                _events.Add(item);
            }
        }

        return _events.Count;
    }

    private string SyncTodaysEventListAfterOutputFolderChange()
    {
        var databasePath = _eventDatabase.GetDatabasePath(_outputFolder);
        if (File.Exists(databasePath))
        {
            var loaded = LoadTodaysEventListFromOutputFolder();
            return loaded == 1
                ? "Loaded 1 database event for today."
                : $"Loaded {loaded} database events for today.";
        }

        _events.Clear();
        return "No event database found for today in the new output folder.";
    }

    private void DeleteEventArtifacts(IEnumerable<AvatarBuilderEvent> events)
    {
        var folders = events
            .Select(static item => item.EventFolder)
            .Where(IsSafeEventFolderPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var folder in folders)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; the database row is already gone.
            }
        }
    }

    private static bool HasEventEvidenceFolder(AvatarBuilderEvent item)
    {
        return !string.IsNullOrWhiteSpace(item.EventFolder)
            && Directory.Exists(item.EventFolder);
    }

    private bool IsSafeEventFolderPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var outputRoot = Path.GetFullPath(_outputFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return fullPath.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase)
            && name.StartsWith("Episode_", StringComparison.OrdinalIgnoreCase);
    }

    private bool SaveAlertBaselineToOutputFolder()
    {
        try
        {
            var regionBaseline = _faceCueAnalyzer.ExportBaseline();
            var landmarkBaseline = _faceLandmarkCueAnalyzer.ExportBaseline();
            if (!IsFaceCueBaselineReady(regionBaseline) && !IsLandmarkCueBaselineReady(landmarkBaseline))
            {
                return false;
            }

            var cameraName = GetSelectedCameraName();
            var modeLabel = GetSelectedCameraModeLabel();
            var savedAtUtc = DateTime.UtcNow;
            var file = new AlertBaselineFile
            {
                Version = 1,
                SavedAtUtc = savedAtUtc,
                CameraName = cameraName,
                CameraModeLabel = modeLabel,
                RegionBaseline = regionBaseline,
                LandmarkBaseline = landmarkBaseline
            };

            var path = GetAlertBaselinePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _outputFolder);
            var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, Encoding.UTF8);
            _alertBaselineSavedAtUtc = savedAtUtc;
            _alertBaselineCameraName = cameraName;
            _alertBaselineModeLabel = modeLabel;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool LoadAlertBaselineFromOutputFolder(bool showStatus)
    {
        try
        {
            var path = GetAlertBaselinePath();
            if (!File.Exists(path))
            {
                _alertBaselineSavedAtUtc = null;
                _alertBaselineCameraName = "";
                _alertBaselineModeLabel = "";
                if (showStatus)
                {
                    MonitorStatusText.Text = "No saved alert baseline found in this output folder.";
                }

                return false;
            }

            var file = JsonSerializer.Deserialize<AlertBaselineFile>(File.ReadAllText(path, Encoding.UTF8));
            if (file is null)
            {
                return false;
            }

            var loadedRegion = _faceCueAnalyzer.TryImportBaseline(file.RegionBaseline);
            var loadedLandmark = _faceLandmarkCueAnalyzer.TryImportBaseline(file.LandmarkBaseline);
            if ((!loadedRegion && !loadedLandmark)
                || (!IsFaceCueBaselineReady(file.RegionBaseline) && !IsLandmarkCueBaselineReady(file.LandmarkBaseline)))
            {
                return false;
            }

            _alertBaselineCalibrationActive = false;
            _alertBaselineSavedAtUtc = file.SavedAtUtc == default
                ? File.GetLastWriteTimeUtc(path)
                : file.SavedAtUtc;
            _alertBaselineCameraName = file.CameraName ?? "";
            _alertBaselineModeLabel = file.CameraModeLabel ?? "";
            CalibrateAlertBaselineButton.Content = AlertBaselineStartButtonText;
            if (showStatus)
            {
                MonitorStatusText.Text = $"Loaded saved alert baseline from {path}.";
            }

            return true;
        }
        catch (Exception ex)
        {
            if (showStatus)
            {
                MonitorStatusText.Text = $"Could not load saved alert baseline: {ex.Message}";
            }

            return false;
        }
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

            var status = $"Avatar capture folder ready: {folder}. 3DDFA_V2 ONNX stores dense reconstruction review data; MediaPipe eye/jaw/brow tracking remains live.";
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

    private string GetAlertBaselinePath()
    {
        return Path.Combine(_outputFolder, AlertBaselineFolderName, AlertBaselineFileName);
    }

    private string GetSelectedCameraName()
    {
        return CameraComboBox.SelectedItem is CameraDevice camera ? camera.Name : "";
    }

    private string GetSelectedCameraModeLabel()
    {
        return CameraModeComboBox.SelectedItem is CameraVideoMode mode ? mode.Label : "";
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

    private bool IsAlertBaselineCameraSetupDifferent()
    {
        var cameraName = GetSelectedCameraName();
        var modeLabel = GetSelectedCameraModeLabel();
        var cameraChanged = !string.IsNullOrWhiteSpace(_alertBaselineCameraName)
            && !string.IsNullOrWhiteSpace(cameraName)
            && !string.Equals(_alertBaselineCameraName, cameraName, StringComparison.OrdinalIgnoreCase);
        var modeChanged = !string.IsNullOrWhiteSpace(_alertBaselineModeLabel)
            && !string.IsNullOrWhiteSpace(modeLabel)
            && !string.Equals(_alertBaselineModeLabel, modeLabel, StringComparison.OrdinalIgnoreCase);
        return cameraChanged || modeChanged;
    }

    private string GetAlertBaselineDurabilityLabel()
    {
        var regionBaseline = _faceCueAnalyzer.ExportBaseline();
        var landmarkBaseline = _faceLandmarkCueAnalyzer.ExportBaseline();
        var regionReady = IsFaceCueBaselineReady(regionBaseline);
        var landmarkReady = IsLandmarkCueBaselineReady(landmarkBaseline);
        var landmarkPartial = IsLandmarkCueBaselineUsable(landmarkBaseline);
        return (regionReady, landmarkReady, landmarkPartial) switch
        {
            (_, true, _) => "strong saved baseline",
            (true, false, true) => "usable saved baseline; landmark cues will keep strengthening",
            (true, false, false) => "usable saved fallback baseline",
            _ => "partial baseline"
        };
    }

    private string GetAlertBaselineStatusText()
    {
        if (_alertBaselineSavedAtUtc is DateTime savedAtUtc)
        {
            var savedText = savedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            var setupNote = IsAlertBaselineCameraSetupDifferent()
                ? " Current camera setup differs from the saved setup; recalibrate when convenient if tracking looks off."
                : " Recalibrate only if lighting, glasses, seating distance, or camera setup changed noticeably.";
            return $"Alert baseline ready ({GetAlertBaselineDurabilityLabel()}, saved {savedText}).{setupNote}";
        }

        return _lastSymptomAt is DateTime lastSymptom
            ? $"No saved alert baseline yet. Calibrate when alert and symptom-free. Last symptom marker: {lastSymptom:g}."
            : "No saved alert baseline yet. Calibrate once while alert and symptom-free; it will be reused from this output folder.";
    }

    private static bool IsFaceCueBaselineReady(FaceCueBaselineSnapshot? baseline)
    {
        return baseline is not null && baseline.BaselineSamples >= 30;
    }

    private static bool IsLandmarkCueBaselineReady(FaceLandmarkCueBaselineSnapshot? baseline)
    {
        return baseline is not null
            && (baseline.EyeBaselineSamples >= 20
                || baseline.MediaPipeBlinkBaselineSamples >= 20);
    }

    private static bool IsLandmarkCueBaselineUsable(FaceLandmarkCueBaselineSnapshot? baseline)
    {
        return baseline is not null
            && (baseline.EyeBaselineSamples > 0
            || baseline.MouthBaselineSamples > 0
            || baseline.JawDroopBaselineSamples > 0
            || baseline.MediaPipeBlinkBaselineSamples > 0
            || baseline.MediaPipeJawOpenBaselineSamples > 0
            || baseline.MediaPipeMouthCloseBaselineSamples > 0);
    }

    private void SetStatus(string status)
    {
        TopStatusText.Text = status;
        FooterText.Text = status;
    }

    private void EnableDarkWindowFrame()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var enabled = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
            }
        }
        catch
        {
        }
    }

    private static string Csv(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string FormatOptional(double? value)
    {
        return value is null ? "" : value.Value.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static string FormatOptional(bool? value)
    {
        return value?.ToString() ?? "";
    }

    private (string JsonPath, string CsvPath, string TimelineJsonPath, string TimelineCsvPath) WriteEventSummary(DateTime startedAt, DateTime endedAt, string endReason, string endSnapshot)
    {
        if (string.IsNullOrWhiteSpace(_activeEventFolder))
        {
            return ("", "", "", "");
        }

        try
        {
            Directory.CreateDirectory(_activeEventFolder);
            var duration = endedAt - startedAt;
            var timelineFiles = _landmarkEventTimeline.Write(_activeEventFolder);
            var summary = new EventSummary
            {
                StartedAt = startedAt,
                EndedAt = endedAt,
                DurationSeconds = duration.TotalSeconds,
                EndReason = endReason,
                TriggerReasons = _activeTriggerReasons,
                AverageMotion = GetAverageMotionLabel(),
                FaceCueStatus = _currentFaceAnalysis?.Status ?? "",
                FaceCueQuality = _currentFaceAnalysis?.QualityStatus ?? "",
                FaceCueScore = _currentFaceAnalysis?.CompositeCuePercent,
                EyeOpenness = _currentFaceAnalysis?.EyeOpennessPercent,
                EyeDrop = _currentFaceAnalysis?.EyeDropPercent,
                EyeAsymmetry = _currentFaceAnalysis?.EyeAsymmetryPercent,
                JawChange = _currentFaceAnalysis?.JawChangePercent,
                JawAsymmetry = _currentFaceAnalysis?.JawAsymmetryPercent,
                LowerFaceDrop = _currentFaceAnalysis?.LowerFaceDropPercent,
                HeadDrift = _currentFaceAnalysis?.HeadDriftPercent,
                LandmarkSource = _currentFaceLandmarkMetrics.Source,
                LandmarkConfidence = _currentFaceLandmarkMetrics.ConfidenceLabel,
                LandmarkTrackingConfidence = _currentFaceLandmarkMetrics.TrackingConfidence,
                LandmarkEyeConfidence = _currentFaceLandmarkMetrics.EyeConfidence,
                LandmarkMouthConfidence = _currentFaceLandmarkMetrics.MouthConfidence,
                LandmarkEyeQuality = _currentFaceLandmarkMetrics.EyeMeasurementQualityPercent,
                LandmarkMouthQuality = _currentFaceLandmarkMetrics.MouthMeasurementQualityPercent,
                LandmarkOverallQuality = _currentFaceLandmarkMetrics.OverallMeasurementQualityPercent,
                CaptureQualityLabel = _currentAvatarCaptureQuality.Label,
                CaptureQualityScore = _currentAvatarCaptureQuality.ScorePercent,
                CaptureQualityCanCollect = _currentAvatarCaptureQuality.CanCollectMeasurements,
                CaptureQualityAvatarGrade = _currentAvatarCaptureQuality.StrongEnoughForAvatarLearning,
                CaptureQualityReason = _currentAvatarCaptureQuality.PrimaryReason,
                CaptureQualityCameraModeScore = _currentAvatarCaptureQuality.CameraModeScorePercent,
                CaptureQualityFaceScaleScore = _currentAvatarCaptureQuality.FaceScaleScorePercent,
                CaptureQualityEyeScore = _currentAvatarCaptureQuality.EyeEvidenceScorePercent,
                CaptureQualityMouthScore = _currentAvatarCaptureQuality.MouthEvidenceScorePercent,
                CaptureQualityStabilityScore = _currentAvatarCaptureQuality.StabilityScorePercent,
                CaptureQualityGlassesScore = _currentAvatarCaptureQuality.GlassesRiskScorePercent,
                CaptureQualityStorageScore = _currentAvatarCaptureQuality.StorageScorePercent,
                CaptureQualityFaceWidth = _currentAvatarCaptureQuality.FaceWidthPercent,
                CaptureQualityFaceHeight = _currentAvatarCaptureQuality.FaceHeightPercent,
                CaptureQualityIssues = _currentAvatarCaptureQuality.Issues,
                CaptureQualitySuggestions = _currentAvatarCaptureQuality.Suggestions,
                LandmarkFaceReliabilityStatus = _currentFaceLockStabilityAnalysis.Status,
                LandmarkFaceReliabilitySamples = _currentFaceLockStabilityAnalysis.SampleCount,
                LandmarkFaceReliability = _currentFaceLockStabilityAnalysis.CompositeReliabilityPercent,
                LandmarkFaceContinuity = _currentFaceLockStabilityAnalysis.FaceContinuityPercent,
                LandmarkEyeReliability = _currentFaceLockStabilityAnalysis.EyeReliabilityPercent,
                LandmarkMouthReliability = _currentFaceLockStabilityAnalysis.MouthReliabilityPercent,
                LandmarkFaceBoundsRate = _currentFaceLockStabilityAnalysis.FaceBoundsRatePercent,
                LandmarkEyeUsableRate = _currentFaceLockStabilityAnalysis.EyeUsableRatePercent,
                LandmarkMouthUsableRate = _currentFaceLockStabilityAnalysis.MouthUsableRatePercent,
                LandmarkEyeImageQualityAvailable = _currentFaceLandmarkMetrics.EyeImageQualityAvailable,
                LandmarkMouthImageQualityAvailable = _currentFaceLandmarkMetrics.MouthImageQualityAvailable,
                LandmarkEyeGlare = _currentFaceLandmarkMetrics.EyeGlarePercent,
                LandmarkMouthGlare = _currentFaceLandmarkMetrics.MouthGlarePercent,
                LandmarkEyeContrast = _currentFaceLandmarkMetrics.EyeContrastPercent,
                LandmarkMouthContrast = _currentFaceLandmarkMetrics.MouthContrastPercent,
                LandmarkEyeSharpness = _currentFaceLandmarkMetrics.EyeSharpnessPercent,
                LandmarkMouthSharpness = _currentFaceLandmarkMetrics.MouthSharpnessPercent,
                LandmarkEyeDarkCoverage = _currentFaceLandmarkMetrics.EyeDarkCoveragePercent,
                LandmarkMouthDarkCoverage = _currentFaceLandmarkMetrics.MouthDarkCoveragePercent,
                LandmarkRawEyeAsymmetry = _currentFaceLandmarkMetrics.RawEyeAsymmetryPercent,
                LandmarkEyeAsymmetry = _currentFaceLandmarkMetrics.EyeAsymmetryPercent,
                LandmarkEyeAgreement = _currentFaceLandmarkMetrics.EyeAgreementPercent,
                LandmarkPossibleOneEyeArtifact = _currentFaceLandmarkMetrics.PossibleOneEyeArtifact,
                LandmarkLeftEyeReconstructed = _currentFaceLandmarkMetrics.LeftEyeReconstructed,
                LandmarkRightEyeReconstructed = _currentFaceLandmarkMetrics.RightEyeReconstructed,
                LandmarkMouthReconstructed = _currentFaceLandmarkMetrics.MouthReconstructed,
                LandmarkEyeArtifactSuppressed = _currentFaceLandmarkMetrics.EyeArtifactSuppressed,
                LandmarkRawEyeOpening = _currentFaceLandmarkMetrics.RawAverageEyeOpeningRatio,
                LandmarkRawMouthOpening = _currentFaceLandmarkMetrics.RawMouthOpeningRatio,
                LandmarkRawJawDroop = _currentFaceLandmarkMetrics.RawJawDroopRatio,
                LandmarkEyeOpening = _currentFaceLandmarkMetrics.AverageEyeOpeningRatio,
                LandmarkMouthOpening = _currentFaceLandmarkMetrics.MouthOpeningRatio,
                LandmarkMouthOpeningVelocity = _currentFaceLandmarkMetrics.MouthOpeningVelocityPerSecond,
                LandmarkJawDroop = _currentFaceLandmarkMetrics.JawDroopRatio,
                LandmarkJawDroopVelocity = _currentFaceLandmarkMetrics.JawDroopVelocityPerSecond,
                LandmarkMediaPipeLeftEyeBlink = _currentFaceLandmarkMetrics.MediaPipeLeftEyeBlinkPercent,
                LandmarkMediaPipeRightEyeBlink = _currentFaceLandmarkMetrics.MediaPipeRightEyeBlinkPercent,
                LandmarkMediaPipeAverageEyeBlink = _currentFaceLandmarkMetrics.MediaPipeAverageEyeBlinkPercent,
                LandmarkMediaPipeJawOpen = _currentFaceLandmarkMetrics.MediaPipeJawOpenPercent,
                LandmarkMediaPipeMouthClose = _currentFaceLandmarkMetrics.MediaPipeMouthClosePercent,
                LandmarkMediaPipeEyeOpeningCorrection = _currentFaceLandmarkMetrics.MediaPipeEyeOpeningCorrectionRatio,
                LandmarkMediaPipeMouthOpeningCorrection = _currentFaceLandmarkMetrics.MediaPipeMouthOpeningCorrectionRatio,
                LandmarkMediaPipeEyeOpeningCorrected = _currentFaceLandmarkMetrics.MediaPipeEyeOpeningCorrected,
                LandmarkMediaPipeMouthOpeningCorrected = _currentFaceLandmarkMetrics.MediaPipeMouthOpeningCorrected,
                LandmarkCueStatus = _currentFaceLandmarkCueAnalysis?.Status ?? "",
                LandmarkCueScore = _currentFaceLandmarkCueAnalysis?.CompositeCuePercent,
                LandmarkEyeCueEligible = _currentFaceLandmarkCueAnalysis?.EyeCueEligible,
                LandmarkMouthCueEligible = _currentFaceLandmarkCueAnalysis?.MouthCueEligible,
                LandmarkEyeClosure = _currentFaceLandmarkCueAnalysis?.EyeClosurePercent,
                LandmarkMouthOpeningChange = _currentFaceLandmarkCueAnalysis?.MouthOpeningChangePercent,
                LandmarkJawDroopBaseline = _currentFaceLandmarkCueAnalysis?.JawDroopBaselineRatio,
                LandmarkJawDroopChange = _currentFaceLandmarkCueAnalysis?.JawDroopChangePercent,
                LandmarkMediaPipeBlinkBaselineReady = _currentFaceLandmarkCueAnalysis?.MediaPipeBlinkBaselineReady,
                LandmarkMediaPipeMouthBaselineReady = _currentFaceLandmarkCueAnalysis?.MediaPipeMouthBaselineReady,
                LandmarkMediaPipeBlinkBaseline = _currentFaceLandmarkCueAnalysis?.MediaPipeBlinkBaselinePercent,
                LandmarkMediaPipeJawOpenBaseline = _currentFaceLandmarkCueAnalysis?.MediaPipeJawOpenBaselinePercent,
                LandmarkMediaPipeMouthCloseBaseline = _currentFaceLandmarkCueAnalysis?.MediaPipeMouthCloseBaselinePercent,
                LandmarkMediaPipeBlinkChange = _currentFaceLandmarkCueAnalysis?.MediaPipeBlinkChangePercent,
                LandmarkMediaPipeJawOpenChange = _currentFaceLandmarkCueAnalysis?.MediaPipeJawOpenChangePercent,
                LandmarkMediaPipeMouthCloseDrop = _currentFaceLandmarkCueAnalysis?.MediaPipeMouthCloseDropPercent,
                LandmarkMediaPipeMouthOpeningEvidence = _currentFaceLandmarkCueAnalysis?.MediaPipeMouthOpeningEvidencePercent,
                LandmarkTrendStatus = _currentFaceLandmarkTrendAnalysis.Status,
                LandmarkTrendScore = _currentFaceLandmarkTrendAnalysis.TrendCuePercent,
                LandmarkTrendWindowSeconds = _currentFaceLandmarkTrendAnalysis.WindowSeconds,
                LandmarkEyeClosingTrend = _currentFaceLandmarkTrendAnalysis.EyeClosingTrendPercent,
                LandmarkMouthOpeningTrend = _currentFaceLandmarkTrendAnalysis.MouthOpeningTrendPercent,
                LandmarkEyeOpeningSlope = _currentFaceLandmarkTrendAnalysis.EyeOpeningSlopePerSecond,
                LandmarkMouthOpeningSlope = _currentFaceLandmarkTrendAnalysis.MouthOpeningSlopePerSecond,
                LandmarkEventSamples = _landmarkEventAggregate.SampleCount,
                LandmarkEventSources = _landmarkEventAggregate.Sources,
                LandmarkEventBackendStatuses = _landmarkEventAggregate.BackendStatuses,
                LandmarkEventMinimumEyeQuality = _landmarkEventAggregate.MinimumEyeQualityPercent,
                LandmarkEventMinimumMouthQuality = _landmarkEventAggregate.MinimumMouthQualityPercent,
                LandmarkEventMinimumOverallQuality = _landmarkEventAggregate.MinimumOverallQualityPercent,
                LandmarkEventAverageOverallQuality = _landmarkEventAggregate.AverageOverallQualityPercent,
                LandmarkEventCaptureQualitySamples = _landmarkEventAggregate.CaptureQualitySamples,
                LandmarkEventCaptureQualityCanCollectSamples = _landmarkEventAggregate.CaptureQualityCanCollectSamples,
                LandmarkEventCaptureQualityAvatarGradeSamples = _landmarkEventAggregate.CaptureQualityAvatarGradeSamples,
                LandmarkEventMinimumCaptureQualityScore = _landmarkEventAggregate.MinimumCaptureQualityScore,
                LandmarkEventMaximumCaptureQualityScore = _landmarkEventAggregate.MaximumCaptureQualityScore,
                LandmarkEventAverageCaptureQualityScore = _landmarkEventAggregate.AverageCaptureQualityScore,
                LandmarkEventCaptureQualityLabels = _landmarkEventAggregate.CaptureQualityLabels,
                LandmarkEventCaptureQualityIssues = _landmarkEventAggregate.CaptureQualityIssues,
                LandmarkEventFaceReliabilitySamples = _landmarkEventAggregate.FaceReliabilitySamples,
                LandmarkEventFaceReliabilityUsableSamples = _landmarkEventAggregate.FaceReliabilityUsableSamples,
                LandmarkEventMinimumFaceReliability = _landmarkEventAggregate.MinimumFaceReliabilityPercent,
                LandmarkEventAverageFaceReliability = _landmarkEventAggregate.AverageFaceReliabilityPercent,
                LandmarkEventMinimumFaceContinuity = _landmarkEventAggregate.MinimumFaceContinuityPercent,
                LandmarkEventAverageFaceContinuity = _landmarkEventAggregate.AverageFaceContinuityPercent,
                LandmarkEventMinimumEyeReliability = _landmarkEventAggregate.MinimumEyeReliabilityPercent,
                LandmarkEventAverageEyeReliability = _landmarkEventAggregate.AverageEyeReliabilityPercent,
                LandmarkEventMinimumMouthReliability = _landmarkEventAggregate.MinimumMouthReliabilityPercent,
                LandmarkEventAverageMouthReliability = _landmarkEventAggregate.AverageMouthReliabilityPercent,
                LandmarkEventMinimumEyeOpening = _landmarkEventAggregate.MinimumEyeOpeningRatio,
                LandmarkEventMaximumEyeClosure = _landmarkEventAggregate.MaximumEyeClosurePercent,
                LandmarkEventMaximumMouthOpening = _landmarkEventAggregate.MaximumMouthOpeningRatio,
                LandmarkEventMaximumMouthOpeningChange = _landmarkEventAggregate.MaximumMouthOpeningChangePercent,
                LandmarkEventMaximumMouthOpeningVelocity = _landmarkEventAggregate.MaximumMouthOpeningVelocityPerSecond,
                LandmarkEventMaximumJawDroop = _landmarkEventAggregate.MaximumJawDroopRatio,
                LandmarkEventMaximumJawDroopChange = _landmarkEventAggregate.MaximumJawDroopChangePercent,
                LandmarkEventMaximumJawDroopVelocity = _landmarkEventAggregate.MaximumJawDroopVelocityPerSecond,
                LandmarkEventMaximumMediaPipeAverageEyeBlink = _landmarkEventAggregate.MaximumMediaPipeAverageEyeBlinkPercent,
                LandmarkEventMaximumMediaPipeJawOpen = _landmarkEventAggregate.MaximumMediaPipeJawOpenPercent,
                LandmarkEventMinimumMediaPipeMouthClose = _landmarkEventAggregate.MinimumMediaPipeMouthClosePercent,
                LandmarkEventMaximumMediaPipeBlinkChange = _landmarkEventAggregate.MaximumMediaPipeBlinkChangePercent,
                LandmarkEventMaximumMediaPipeJawOpenChange = _landmarkEventAggregate.MaximumMediaPipeJawOpenChangePercent,
                LandmarkEventMaximumMediaPipeMouthCloseDrop = _landmarkEventAggregate.MaximumMediaPipeMouthCloseDropPercent,
                LandmarkEventMaximumMediaPipeMouthOpeningEvidence = _landmarkEventAggregate.MaximumMediaPipeMouthOpeningEvidencePercent,
                LandmarkEventMediaPipeEyeOpeningCorrectedSamples = _landmarkEventAggregate.MediaPipeEyeOpeningCorrectedSamples,
                LandmarkEventMediaPipeMouthOpeningCorrectedSamples = _landmarkEventAggregate.MediaPipeMouthOpeningCorrectedSamples,
                LandmarkEventMaximumAbsoluteMediaPipeEyeOpeningCorrection = _landmarkEventAggregate.MaximumAbsoluteMediaPipeEyeOpeningCorrection,
                LandmarkEventMaximumAbsoluteMediaPipeMouthOpeningCorrection = _landmarkEventAggregate.MaximumAbsoluteMediaPipeMouthOpeningCorrection,
                LandmarkEventMaximumCueScore = _landmarkEventAggregate.MaximumLandmarkCueScore,
                LandmarkEventMaximumEyeClosingTrend = _landmarkEventAggregate.MaximumEyeClosingTrendPercent,
                LandmarkEventMaximumMouthOpeningTrend = _landmarkEventAggregate.MaximumMouthOpeningTrendPercent,
                LandmarkEventMinimumEyeOpeningSlope = _landmarkEventAggregate.MinimumEyeOpeningSlopePerSecond,
                LandmarkEventMaximumMouthOpeningSlope = _landmarkEventAggregate.MaximumMouthOpeningSlopePerSecond,
                LandmarkEventMaximumTrendScore = _landmarkEventAggregate.MaximumLandmarkTrendScore,
                LandmarkEventMaximumEyeGlare = _landmarkEventAggregate.MaximumEyeGlarePercent,
                LandmarkEventMaximumMouthGlare = _landmarkEventAggregate.MaximumMouthGlarePercent,
                LandmarkEventMinimumEyeContrast = _landmarkEventAggregate.MinimumEyeContrastPercent,
                LandmarkEventMinimumMouthContrast = _landmarkEventAggregate.MinimumMouthContrastPercent,
                LandmarkEventMinimumEyeSharpness = _landmarkEventAggregate.MinimumEyeSharpnessPercent,
                LandmarkEventMinimumMouthSharpness = _landmarkEventAggregate.MinimumMouthSharpnessPercent,
                LandmarkEventMaximumRawEyeAsymmetry = _landmarkEventAggregate.MaximumRawEyeAsymmetryPercent,
                LandmarkEventMaximumEyeAsymmetry = _landmarkEventAggregate.MaximumEyeAsymmetryPercent,
                LandmarkEventPossibleOneEyeArtifactSamples = _landmarkEventAggregate.PossibleOneEyeArtifactSamples,
                LandmarkEventLeftEyeReconstructedSamples = _landmarkEventAggregate.LeftEyeReconstructedSamples,
                LandmarkEventRightEyeReconstructedSamples = _landmarkEventAggregate.RightEyeReconstructedSamples,
                LandmarkEventMouthReconstructedSamples = _landmarkEventAggregate.MouthReconstructedSamples,
                LandmarkEventEyeArtifactSuppressedSamples = _landmarkEventAggregate.EyeArtifactSuppressedSamples,
                VideoFile = _activeEventVideo,
                VideoOverlayBurnedIn = !string.IsNullOrWhiteSpace(_activeEventVideo),
                PreEventVideoSeconds = PreEventVideoSeconds,
                LandmarkTimelineSamples = _landmarkEventTimeline.Count,
                LandmarkTimelineJsonFile = timelineFiles.JsonPath,
                LandmarkTimelineCsvFile = timelineFiles.CsvPath,
                StartSnapshot = _episodeStartSnapshot,
                EndSnapshot = endSnapshot,
                OutputFolder = _activeEventFolder
            };

            var jsonPath = Path.Combine(_activeEventFolder, "event_summary.json");
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

            var csvPath = Path.Combine(_activeEventFolder, "event_summary.csv");
            var builder = new StringBuilder();
            builder.AppendLine("Started,Ended,DurationSeconds,EndReason,TriggerReasons,AverageMotion,FaceCueStatus,FaceCueQuality,FaceCueScore,EyeOpenness,EyeDrop,EyeAsymmetry,JawChange,JawAsymmetry,LowerFaceDrop,HeadDrift,LandmarkSource,LandmarkConfidence,LandmarkTrackingConfidence,LandmarkEyeConfidence,LandmarkMouthConfidence,LandmarkEyeQuality,LandmarkMouthQuality,LandmarkOverallQuality,CaptureQualityLabel,CaptureQualityScore,CaptureQualityCanCollect,CaptureQualityAvatarGrade,CaptureQualityReason,CaptureQualityCameraModeScore,CaptureQualityFaceScaleScore,CaptureQualityEyeScore,CaptureQualityMouthScore,CaptureQualityStabilityScore,CaptureQualityGlassesScore,CaptureQualityStorageScore,CaptureQualityFaceWidth,CaptureQualityFaceHeight,CaptureQualityIssues,CaptureQualitySuggestions,LandmarkFaceReliabilityStatus,LandmarkFaceReliabilitySamples,LandmarkFaceReliability,LandmarkFaceContinuity,LandmarkEyeReliability,LandmarkMouthReliability,LandmarkFaceBoundsRate,LandmarkEyeUsableRate,LandmarkMouthUsableRate,LandmarkEyeImageQualityAvailable,LandmarkMouthImageQualityAvailable,LandmarkEyeGlare,LandmarkMouthGlare,LandmarkEyeContrast,LandmarkMouthContrast,LandmarkEyeSharpness,LandmarkMouthSharpness,LandmarkEyeDarkCoverage,LandmarkMouthDarkCoverage,LandmarkRawEyeAsymmetry,LandmarkEyeAsymmetry,LandmarkEyeAgreement,LandmarkPossibleOneEyeArtifact,LandmarkLeftEyeReconstructed,LandmarkRightEyeReconstructed,LandmarkMouthReconstructed,LandmarkEyeArtifactSuppressed,LandmarkRawEyeOpening,LandmarkRawMouthOpening,LandmarkRawJawDroop,LandmarkEyeOpening,LandmarkMouthOpening,LandmarkMouthOpeningVelocity,LandmarkJawDroop,LandmarkJawDroopVelocity,LandmarkMediaPipeLeftEyeBlink,LandmarkMediaPipeRightEyeBlink,LandmarkMediaPipeAverageEyeBlink,LandmarkMediaPipeJawOpen,LandmarkMediaPipeMouthClose,LandmarkMediaPipeEyeOpeningCorrection,LandmarkMediaPipeMouthOpeningCorrection,LandmarkMediaPipeEyeOpeningCorrected,LandmarkMediaPipeMouthOpeningCorrected,LandmarkMediaPipeBlinkBaselineReady,LandmarkMediaPipeMouthBaselineReady,LandmarkMediaPipeBlinkBaseline,LandmarkMediaPipeJawOpenBaseline,LandmarkMediaPipeMouthCloseBaseline,LandmarkMediaPipeBlinkChange,LandmarkMediaPipeJawOpenChange,LandmarkMediaPipeMouthCloseDrop,LandmarkMediaPipeMouthOpeningEvidence,LandmarkCueStatus,LandmarkCueScore,LandmarkEyeCueEligible,LandmarkMouthCueEligible,LandmarkEyeClosure,LandmarkMouthOpeningChange,LandmarkJawDroopBaseline,LandmarkJawDroopChange,LandmarkTrendStatus,LandmarkTrendScore,LandmarkTrendWindowSeconds,LandmarkEyeClosingTrend,LandmarkMouthOpeningTrend,LandmarkEyeOpeningSlope,LandmarkMouthOpeningSlope,LandmarkEventSamples,LandmarkEventSources,LandmarkEventBackendStatuses,LandmarkEventMinimumEyeQuality,LandmarkEventMinimumMouthQuality,LandmarkEventMinimumOverallQuality,LandmarkEventAverageOverallQuality,LandmarkEventCaptureQualitySamples,LandmarkEventCaptureQualityCanCollectSamples,LandmarkEventCaptureQualityAvatarGradeSamples,LandmarkEventMinimumCaptureQualityScore,LandmarkEventMaximumCaptureQualityScore,LandmarkEventAverageCaptureQualityScore,LandmarkEventCaptureQualityLabels,LandmarkEventCaptureQualityIssues,LandmarkEventFaceReliabilitySamples,LandmarkEventFaceReliabilityUsableSamples,LandmarkEventMinimumFaceReliability,LandmarkEventAverageFaceReliability,LandmarkEventMinimumFaceContinuity,LandmarkEventAverageFaceContinuity,LandmarkEventMinimumEyeReliability,LandmarkEventAverageEyeReliability,LandmarkEventMinimumMouthReliability,LandmarkEventAverageMouthReliability,LandmarkEventMinimumEyeOpening,LandmarkEventMaximumEyeClosure,LandmarkEventMaximumMouthOpening,LandmarkEventMaximumMouthOpeningChange,LandmarkEventMaximumMouthOpeningVelocity,LandmarkEventMaximumJawDroop,LandmarkEventMaximumJawDroopChange,LandmarkEventMaximumJawDroopVelocity,LandmarkEventMaximumMediaPipeAverageEyeBlink,LandmarkEventMaximumMediaPipeJawOpen,LandmarkEventMinimumMediaPipeMouthClose,LandmarkEventMaximumMediaPipeBlinkChange,LandmarkEventMaximumMediaPipeJawOpenChange,LandmarkEventMaximumMediaPipeMouthCloseDrop,LandmarkEventMaximumMediaPipeMouthOpeningEvidence,LandmarkEventMediaPipeEyeOpeningCorrectedSamples,LandmarkEventMediaPipeMouthOpeningCorrectedSamples,LandmarkEventMaximumAbsoluteMediaPipeEyeOpeningCorrection,LandmarkEventMaximumAbsoluteMediaPipeMouthOpeningCorrection,LandmarkEventMaximumCueScore,LandmarkEventMaximumEyeClosingTrend,LandmarkEventMaximumMouthOpeningTrend,LandmarkEventMinimumEyeOpeningSlope,LandmarkEventMaximumMouthOpeningSlope,LandmarkEventMaximumTrendScore,LandmarkEventMaximumEyeGlare,LandmarkEventMaximumMouthGlare,LandmarkEventMinimumEyeContrast,LandmarkEventMinimumMouthContrast,LandmarkEventMinimumEyeSharpness,LandmarkEventMinimumMouthSharpness,LandmarkEventMaximumRawEyeAsymmetry,LandmarkEventMaximumEyeAsymmetry,LandmarkEventPossibleOneEyeArtifactSamples,LandmarkEventLeftEyeReconstructedSamples,LandmarkEventRightEyeReconstructedSamples,LandmarkEventMouthReconstructedSamples,LandmarkEventEyeArtifactSuppressedSamples,VideoFile,VideoOverlayBurnedIn,PreEventVideoSeconds,LandmarkTimelineSamples,LandmarkTimelineJsonFile,LandmarkTimelineCsvFile,StartSnapshot,EndSnapshot,OutputFolder");
            builder.AppendLine(string.Join(",", [
                Csv(startedAt.ToString("O")),
                Csv(endedAt.ToString("O")),
                Csv(duration.TotalSeconds.ToString("0.0")),
                Csv(endReason),
                Csv(string.Join("; ", _activeTriggerReasons)),
                Csv(GetAverageMotionLabel()),
                Csv(_currentFaceAnalysis?.Status ?? ""),
                Csv(_currentFaceAnalysis?.QualityStatus ?? ""),
                Csv(FormatOptional(_currentFaceAnalysis?.CompositeCuePercent)),
                Csv(FormatOptional(_currentFaceAnalysis?.EyeOpennessPercent)),
                Csv(FormatOptional(_currentFaceAnalysis?.EyeDropPercent)),
                Csv(FormatOptional(_currentFaceAnalysis?.EyeAsymmetryPercent)),
                Csv(FormatOptional(_currentFaceAnalysis?.JawChangePercent)),
                Csv(FormatOptional(_currentFaceAnalysis?.JawAsymmetryPercent)),
                Csv(FormatOptional(_currentFaceAnalysis?.LowerFaceDropPercent)),
                Csv(FormatOptional(_currentFaceAnalysis?.HeadDriftPercent)),
                Csv(_currentFaceLandmarkMetrics.Source),
                Csv(_currentFaceLandmarkMetrics.ConfidenceLabel),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.TrackingConfidence)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.EyeConfidence)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.MouthConfidence)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.EyeMeasurementQualityPercent)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.MouthMeasurementQualityPercent)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.OverallMeasurementQualityPercent)),
                Csv(_currentAvatarCaptureQuality.Label),
                Csv(FormatOptional(_currentAvatarCaptureQuality.ScorePercent)),
                Csv(FormatOptional(_currentAvatarCaptureQuality.CanCollectMeasurements)),
                Csv(FormatOptional(_currentAvatarCaptureQuality.StrongEnoughForAvatarLearning)),
                Csv(_currentAvatarCaptureQuality.PrimaryReason),
                Csv(FormatOptional(_currentAvatarCaptureQuality.CameraModeScorePercent)),
                Csv(FormatOptional(_currentAvatarCaptureQuality.FaceScaleScorePercent)),
                Csv(FormatOptional(_currentAvatarCaptureQuality.EyeEvidenceScorePercent)),
                Csv(FormatOptional(_currentAvatarCaptureQuality.MouthEvidenceScorePercent)),
                Csv(FormatOptional(_currentAvatarCaptureQuality.StabilityScorePercent)),
                Csv(FormatOptional(_currentAvatarCaptureQuality.GlassesRiskScorePercent)),
                Csv(FormatOptional(_currentAvatarCaptureQuality.StorageScorePercent)),
                Csv(FormatOptional(_currentAvatarCaptureQuality.FaceWidthPercent)),
                Csv(FormatOptional(_currentAvatarCaptureQuality.FaceHeightPercent)),
                Csv(string.Join("; ", _currentAvatarCaptureQuality.Issues)),
                Csv(string.Join("; ", _currentAvatarCaptureQuality.Suggestions)),
                Csv(_currentFaceLockStabilityAnalysis.Status),
                Csv(_currentFaceLockStabilityAnalysis.SampleCount.ToString(CultureInfo.InvariantCulture)),
                Csv(FormatOptional(_currentFaceLockStabilityAnalysis.CompositeReliabilityPercent)),
                Csv(FormatOptional(_currentFaceLockStabilityAnalysis.FaceContinuityPercent)),
                Csv(FormatOptional(_currentFaceLockStabilityAnalysis.EyeReliabilityPercent)),
                Csv(FormatOptional(_currentFaceLockStabilityAnalysis.MouthReliabilityPercent)),
                Csv(FormatOptional(_currentFaceLockStabilityAnalysis.FaceBoundsRatePercent)),
                Csv(FormatOptional(_currentFaceLockStabilityAnalysis.EyeUsableRatePercent)),
                Csv(FormatOptional(_currentFaceLockStabilityAnalysis.MouthUsableRatePercent)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.EyeImageQualityAvailable)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.MouthImageQualityAvailable)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.EyeGlarePercent)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.MouthGlarePercent)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.EyeContrastPercent)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.MouthContrastPercent)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.EyeSharpnessPercent)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.MouthSharpnessPercent)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.EyeDarkCoveragePercent)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.MouthDarkCoveragePercent)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.RawEyeAsymmetryPercent)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.EyeAsymmetryPercent)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.EyeAgreementPercent)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.PossibleOneEyeArtifact)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.LeftEyeReconstructed)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.RightEyeReconstructed)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.MouthReconstructed)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.EyeArtifactSuppressed)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.RawAverageEyeOpeningRatio)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.RawMouthOpeningRatio)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.RawJawDroopRatio)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.AverageEyeOpeningRatio)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.MouthOpeningRatio)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.MouthOpeningVelocityPerSecond)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.JawDroopRatio)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.JawDroopVelocityPerSecond)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.MediaPipeLeftEyeBlinkPercent)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.MediaPipeRightEyeBlinkPercent)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.MediaPipeAverageEyeBlinkPercent)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.MediaPipeJawOpenPercent)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.MediaPipeMouthClosePercent)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.MediaPipeEyeOpeningCorrectionRatio)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.MediaPipeMouthOpeningCorrectionRatio)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.MediaPipeEyeOpeningCorrected)),
                Csv(FormatOptional(_currentFaceLandmarkMetrics.MediaPipeMouthOpeningCorrected)),
                Csv(FormatOptional(_currentFaceLandmarkCueAnalysis?.MediaPipeBlinkBaselineReady)),
                Csv(FormatOptional(_currentFaceLandmarkCueAnalysis?.MediaPipeMouthBaselineReady)),
                Csv(FormatOptional(_currentFaceLandmarkCueAnalysis?.MediaPipeBlinkBaselinePercent)),
                Csv(FormatOptional(_currentFaceLandmarkCueAnalysis?.MediaPipeJawOpenBaselinePercent)),
                Csv(FormatOptional(_currentFaceLandmarkCueAnalysis?.MediaPipeMouthCloseBaselinePercent)),
                Csv(FormatOptional(_currentFaceLandmarkCueAnalysis?.MediaPipeBlinkChangePercent)),
                Csv(FormatOptional(_currentFaceLandmarkCueAnalysis?.MediaPipeJawOpenChangePercent)),
                Csv(FormatOptional(_currentFaceLandmarkCueAnalysis?.MediaPipeMouthCloseDropPercent)),
                Csv(FormatOptional(_currentFaceLandmarkCueAnalysis?.MediaPipeMouthOpeningEvidencePercent)),
                Csv(_currentFaceLandmarkCueAnalysis?.Status ?? ""),
                Csv(FormatOptional(_currentFaceLandmarkCueAnalysis?.CompositeCuePercent)),
                Csv(FormatOptional(_currentFaceLandmarkCueAnalysis?.EyeCueEligible)),
                Csv(FormatOptional(_currentFaceLandmarkCueAnalysis?.MouthCueEligible)),
                Csv(FormatOptional(_currentFaceLandmarkCueAnalysis?.EyeClosurePercent)),
                Csv(FormatOptional(_currentFaceLandmarkCueAnalysis?.MouthOpeningChangePercent)),
                Csv(FormatOptional(_currentFaceLandmarkCueAnalysis?.JawDroopBaselineRatio)),
                Csv(FormatOptional(_currentFaceLandmarkCueAnalysis?.JawDroopChangePercent)),
                Csv(_currentFaceLandmarkTrendAnalysis.Status),
                Csv(FormatOptional(_currentFaceLandmarkTrendAnalysis.TrendCuePercent)),
                Csv(FormatOptional(_currentFaceLandmarkTrendAnalysis.WindowSeconds)),
                Csv(FormatOptional(_currentFaceLandmarkTrendAnalysis.EyeClosingTrendPercent)),
                Csv(FormatOptional(_currentFaceLandmarkTrendAnalysis.MouthOpeningTrendPercent)),
                Csv(FormatOptional(_currentFaceLandmarkTrendAnalysis.EyeOpeningSlopePerSecond)),
                Csv(FormatOptional(_currentFaceLandmarkTrendAnalysis.MouthOpeningSlopePerSecond)),
                Csv(_landmarkEventAggregate.SampleCount.ToString(CultureInfo.InvariantCulture)),
                Csv(string.Join("; ", _landmarkEventAggregate.Sources)),
                Csv(string.Join("; ", _landmarkEventAggregate.BackendStatuses)),
                Csv(FormatOptional(_landmarkEventAggregate.MinimumEyeQualityPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MinimumMouthQualityPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MinimumOverallQualityPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.AverageOverallQualityPercent)),
                Csv(_landmarkEventAggregate.CaptureQualitySamples.ToString(CultureInfo.InvariantCulture)),
                Csv(_landmarkEventAggregate.CaptureQualityCanCollectSamples.ToString(CultureInfo.InvariantCulture)),
                Csv(_landmarkEventAggregate.CaptureQualityAvatarGradeSamples.ToString(CultureInfo.InvariantCulture)),
                Csv(FormatOptional(_landmarkEventAggregate.MinimumCaptureQualityScore)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumCaptureQualityScore)),
                Csv(FormatOptional(_landmarkEventAggregate.AverageCaptureQualityScore)),
                Csv(string.Join("; ", _landmarkEventAggregate.CaptureQualityLabels)),
                Csv(string.Join("; ", _landmarkEventAggregate.CaptureQualityIssues)),
                Csv(_landmarkEventAggregate.FaceReliabilitySamples.ToString(CultureInfo.InvariantCulture)),
                Csv(_landmarkEventAggregate.FaceReliabilityUsableSamples.ToString(CultureInfo.InvariantCulture)),
                Csv(FormatOptional(_landmarkEventAggregate.MinimumFaceReliabilityPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.AverageFaceReliabilityPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MinimumFaceContinuityPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.AverageFaceContinuityPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MinimumEyeReliabilityPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.AverageEyeReliabilityPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MinimumMouthReliabilityPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.AverageMouthReliabilityPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MinimumEyeOpeningRatio)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumEyeClosurePercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumMouthOpeningRatio)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumMouthOpeningChangePercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumMouthOpeningVelocityPerSecond)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumJawDroopRatio)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumJawDroopChangePercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumJawDroopVelocityPerSecond)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumMediaPipeAverageEyeBlinkPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumMediaPipeJawOpenPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MinimumMediaPipeMouthClosePercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumMediaPipeBlinkChangePercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumMediaPipeJawOpenChangePercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumMediaPipeMouthCloseDropPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumMediaPipeMouthOpeningEvidencePercent)),
                Csv(_landmarkEventAggregate.MediaPipeEyeOpeningCorrectedSamples.ToString(CultureInfo.InvariantCulture)),
                Csv(_landmarkEventAggregate.MediaPipeMouthOpeningCorrectedSamples.ToString(CultureInfo.InvariantCulture)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumAbsoluteMediaPipeEyeOpeningCorrection)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumAbsoluteMediaPipeMouthOpeningCorrection)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumLandmarkCueScore)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumEyeClosingTrendPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumMouthOpeningTrendPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MinimumEyeOpeningSlopePerSecond)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumMouthOpeningSlopePerSecond)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumLandmarkTrendScore)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumEyeGlarePercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumMouthGlarePercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MinimumEyeContrastPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MinimumMouthContrastPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MinimumEyeSharpnessPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MinimumMouthSharpnessPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumRawEyeAsymmetryPercent)),
                Csv(FormatOptional(_landmarkEventAggregate.MaximumEyeAsymmetryPercent)),
                Csv(_landmarkEventAggregate.PossibleOneEyeArtifactSamples.ToString(CultureInfo.InvariantCulture)),
                Csv(_landmarkEventAggregate.LeftEyeReconstructedSamples.ToString(CultureInfo.InvariantCulture)),
                Csv(_landmarkEventAggregate.RightEyeReconstructedSamples.ToString(CultureInfo.InvariantCulture)),
                Csv(_landmarkEventAggregate.MouthReconstructedSamples.ToString(CultureInfo.InvariantCulture)),
                Csv(_landmarkEventAggregate.EyeArtifactSuppressedSamples.ToString(CultureInfo.InvariantCulture)),
                Csv(_activeEventVideo),
                Csv((!string.IsNullOrWhiteSpace(_activeEventVideo)).ToString()),
                Csv(PreEventVideoSeconds.ToString(CultureInfo.InvariantCulture)),
                Csv(_landmarkEventTimeline.Count.ToString(CultureInfo.InvariantCulture)),
                Csv(timelineFiles.JsonPath),
                Csv(timelineFiles.CsvPath),
                Csv(_episodeStartSnapshot),
                Csv(endSnapshot),
                Csv(_activeEventFolder)
            ]));
            File.WriteAllText(csvPath, builder.ToString(), Encoding.UTF8);
            return (jsonPath, csvPath, timelineFiles.JsonPath, timelineFiles.CsvPath);
        }
        catch (Exception ex)
        {
            MonitorStatusText.Text = $"Episode ended, but summary failed: {ex.Message}";
            return ("", "", "", "");
        }
    }

    private string GetStorageLabel(string folder)
    {
        try
        {
            var fullPath = Path.GetFullPath(folder);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return "Storage: unknown drive.";
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return $"Storage: {root} is not ready.";
            }

            var freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
            var windowsRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var driveNote = root.Equals(windowsRoot, StringComparison.OrdinalIgnoreCase)
                ? "Windows drive"
                : "off-system drive";
            return $"Storage: {drive.Name} {freeGb:0.0} GB free ({driveNote}).";
        }
        catch (Exception ex)
        {
            return $"Storage: unavailable ({ex.Message}).";
        }
    }

    private void CalibrationGuardTick(object? sender, EventArgs e)
    {
        UpdateCalibrationGuard();
    }

    private void UpdateAlertBaselineCalibrationStatus()
    {
        if (!_alertBaselineCalibrationActive || !IsLoaded)
        {
            return;
        }

        var regionReady = _currentFaceAnalysis?.BaselineReady == true;
        var landmarkReady = _currentFaceLandmarkCueAnalysis?.BaselineReady == true;
        if (regionReady || landmarkReady)
        {
            _alertBaselineCalibrationActive = false;
            CalibrateAlertBaselineButton.Content = AlertBaselineStartButtonText;
            CalibrateAlertBaselineButton.IsEnabled = true;
            var saved = SaveAlertBaselineToOutputFolder();
            var saveText = saved
                ? $" Saved to {GetAlertBaselinePath()}."
                : " The baseline is active for this session, but could not be saved to the output folder.";
            CalibrationGuardText.Text = saved
                ? GetAlertBaselineStatusText()
                : "Alert baseline ready for this session. Check output folder permissions if you want it reused next time.";
            MonitorStatusText.Text = $"Alert baseline ready. Eye, mouth, and jaw cues now have an alert reference.{saveText}";
            return;
        }

        CalibrateAlertBaselineButton.Content = AlertBaselineInProgressButtonText;
        CalibrateAlertBaselineButton.IsEnabled = false;
        var progress = GetAlertBaselineProgressText();
        CalibrationGuardText.Text = $"Calibrating alert baseline: {progress}. Stay alert, symptom-free, and naturally relaxed.";
        MonitorStatusText.Text = $"Calibrating alert baseline: {progress}. Keep eyes naturally open and mouth relaxed.";
    }

    private string GetAlertBaselineProgressText()
    {
        var parts = new List<string>();
        if (_currentFaceAnalysis is FaceCueAnalysis regionAnalysis)
        {
            parts.Add($"fallback {Math.Min(regionAnalysis.BaselineSamples, 30)}/30");
        }
        else
        {
            parts.Add("fallback waiting");
        }

        if (_currentFaceLandmarkCueAnalysis is { HasUsableMeasurements: true } landmarkAnalysis)
        {
            parts.Add($"landmarks {Math.Min(landmarkAnalysis.BaselineSamples, 20)}/20");
        }
        else
        {
            parts.Add("landmarks waiting for face lock");
        }

        return string.Join(", ", parts);
    }

    private void MarkSymptomActivity(DateTime timestamp, string message)
    {
        CancelActiveAlertBaselineCalibration();
        _lastSymptomAt = timestamp;
        _calibrationHoldUntil = timestamp + CalibrationSymptomFreeWindow;
        UpdateCalibrationGuard();
        MonitorStatusText.Text = message;
    }

    private void CancelActiveAlertBaselineCalibration()
    {
        if (!_alertBaselineCalibrationActive)
        {
            return;
        }

        _alertBaselineCalibrationActive = false;
        _faceCueAnalyzer.Reset();
        _faceLandmarkCueAnalyzer.Reset();
        _currentFaceAnalysis = null;
        _currentFaceLandmarkCueAnalysis = null;
        CalibrateAlertBaselineButton.Content = AlertBaselineStartButtonText;
        LoadAlertBaselineFromOutputFolder(showStatus: false);
    }

    private bool IsCalibrationAllowed()
    {
        return _calibrationHoldUntil is null || DateTime.Now >= _calibrationHoldUntil.Value;
    }

    private void UpdateCalibrationGuard()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (_calibrationHoldUntil is DateTime holdUntil && DateTime.Now < holdUntil)
        {
            var remaining = holdUntil - DateTime.Now;
            CalibrateAlertBaselineButton.IsEnabled = false;
            CalibrateAlertBaselineButton.Content = AlertBaselineStartButtonText;
            CalibrationGuardText.Text = $"Alert baseline locked: wait {FormatRemaining(remaining)} symptom-free. Last symptom marker: {_lastSymptomAt:g}.";
            return;
        }

        if (_alertBaselineCalibrationActive)
        {
            CalibrateAlertBaselineButton.IsEnabled = false;
            CalibrateAlertBaselineButton.Content = AlertBaselineInProgressButtonText;
            return;
        }

        CalibrateAlertBaselineButton.IsEnabled = true;
        CalibrateAlertBaselineButton.Content = AlertBaselineStartButtonText;
        CalibrationGuardText.Text = GetAlertBaselineStatusText();
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining.TotalMinutes >= 1d)
        {
            return $"{Math.Ceiling(remaining.TotalMinutes):0} min";
        }

        return $"{Math.Max(0, Math.Ceiling(remaining.TotalSeconds)):0}s";
    }

    private void UpdateTrackingOverlay(string state, string metrics, string trigger, string accentColor)
    {
        var now = DateTime.UtcNow;
        var accentChanged = !string.Equals(accentColor, _lastTrackingOverlayAccentColor, StringComparison.OrdinalIgnoreCase);
        var stateChanged = !string.Equals(state, _lastTrackingOverlayState, StringComparison.Ordinal)
            || !string.Equals(trigger, _lastTrackingOverlayTrigger, StringComparison.Ordinal)
            || accentChanged;
        var metricsChanged = !string.Equals(metrics, _lastTrackingOverlayMetrics, StringComparison.Ordinal);
        if (!stateChanged
            && metricsChanged
            && now - _lastTrackingOverlayUpdateAtUtc < TrackingOverlayRefreshInterval)
        {
            return;
        }

        if (!stateChanged && !metricsChanged)
        {
            return;
        }

        _lastTrackingOverlayUpdateAtUtc = now;
        _lastTrackingOverlayState = state;
        _lastTrackingOverlayMetrics = metrics;
        _lastTrackingOverlayTrigger = trigger;
        _lastTrackingOverlayAccentColor = accentColor;

        var availableWidth = Math.Max(360d, PreviewHost.ActualWidth - 36d);
        TrackingOverlay.MaxWidth = Math.Min(860d, availableWidth);
        if (!string.Equals(OverlayStateText.Text, state, StringComparison.Ordinal))
        {
            OverlayStateText.Text = state;
        }

        if (!string.Equals(OverlayMetricText.Text, metrics, StringComparison.Ordinal))
        {
            OverlayMetricText.Text = metrics;
        }

        if (!string.Equals(OverlayTriggerText.Text, trigger, StringComparison.Ordinal))
        {
            OverlayTriggerText.Text = trigger;
        }

        if (_lastTrackingOverlayAccentBrush is null || accentChanged)
        {
            _lastTrackingOverlayAccentBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(accentColor)!;
            _lastTrackingOverlayAccentBrush.Freeze();
        }

        TrackingOverlay.BorderBrush = _lastTrackingOverlayAccentBrush;
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
        var sample = _lastGoodFeatureMeshSamples.Count > 0 ? _lastGoodFeatureMeshSamples[^1] : null;
        if (sample is null)
        {
            AddWireframeText(
                "Live wireframe waiting",
                $"Confirm {CurrentAvatarProfileDisplayName}, start avatar capture, and let MediaPipe capture a strong eye/mouth lock.",
                18,
                18);
            return;
        }

        DrawMediaPipeLiveWireframeView(sample, new Rect(0d, 0d, width, height), "Live wireframe");
    }

    private void DrawMediaPipeLiveWireframeView(LastGoodFeatureMeshSample sample, Rect rect, string title)
    {
        var projection = BuildLiveWireframeProjection(sample, rect.Width, rect.Height);
        projection = OffsetProjection(projection, rect.X, rect.Y);
        var pointMap = projection.Points;
        var surfaceBrush = CreateFrozenBrush(0x2f, 0x6c, 0x8f, 0x58);
        foreach (var edge in sample.WireframeEdges.Where(static edge => edge.Role == "surface"))
        {
            DrawWireframeEdge(edge, pointMap, rect.Width, rect.Height, surfaceBrush, 0.42d);
        }

        foreach (var edge in sample.WireframeEdges.Where(static edge => edge.Role != "surface"))
        {
            DrawWireframeEdge(edge, pointMap, rect.Width, rect.Height, BrushForWireframeRole(edge.Role), 1.75d);
        }

        var featureIndexes = sample.FeatureGroups
            .SelectMany(static group => group.LandmarkIndices)
            .ToHashSet();
        var pointBrush = CreateFrozenBrush(0xdc, 0xef, 0xff, 0xb8);
        foreach (var point in sample.Points)
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

        if (projection.HeadLocked)
        {
            DrawLiveWireframeHeadAxes(projection);
        }

        AddWireframeText(
            $"{title}: {sample.PointCount} points, {sample.WireframeEdges.Count} edges",
            $"{projection.Mode}. Quality {sample.OverallQualityPercent:0}% | eyes {sample.EyeQualityPercent:0}% | brows {sample.BrowQualityPercent:0}% ({FormatRatioPercent(sample.AverageBrowHeightRatio)}) | mouth {sample.MouthQualityPercent:0}% | A/B/C {sample.HeadPitchDegrees:0}/{sample.HeadYawDegrees:0}/{sample.HeadRollDegrees:0} | B lock {_lastGoodFeatureMeshStability.YawStatus} ({_lastGoodFeatureMeshStability.YawRangeDegrees:0.#} deg)",
            rect.X + 18,
            rect.Y + 18);
    }

    private void DrawWireframeEdge(
        LastGoodFeatureMeshWireframeEdge edge,
        IReadOnlyDictionary<int, LiveWireframeProjectedPoint> points,
        double width,
        double height,
        Brush brush,
        double thickness)
    {
        if (!points.TryGetValue(edge.FromIndex, out var from)
            || !points.TryGetValue(edge.ToIndex, out var to))
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

    private static LiveWireframeProjection OffsetProjection(
        LiveWireframeProjection projection,
        double offsetX,
        double offsetY)
    {
        if (Math.Abs(offsetX) < 0.001d && Math.Abs(offsetY) < 0.001d)
        {
            return projection;
        }

        var points = projection.Points.ToDictionary(
            static pair => pair.Key,
            pair => pair.Value with
            {
                X = pair.Value.X + offsetX,
                Y = pair.Value.Y + offsetY
            });
        return projection with
        {
            Points = points,
            AxisOriginX = projection.AxisOriginX + offsetX,
            AxisOriginY = projection.AxisOriginY + offsetY
        };
    }

    private LiveWireframeProjection BuildLiveWireframeProjection(
        LastGoodFeatureMeshSample sample,
        double width,
        double height)
    {
        if (LiveWireframeHeadLockCheckBox?.IsChecked == true
            && TryBuildHeadLockedLiveWireframeProjection(sample, width, height, out var headLockedProjection))
        {
            return headLockedProjection;
        }

        var points = sample.Points.ToDictionary(
            static point => point.Index,
            point => new LiveWireframeProjectedPoint(
                point.Index,
                Math.Clamp(point.X, 0d, 1d) * width,
                Math.Clamp(point.Y, 0d, 1d) * height,
                point.Z));
        var mode = LiveWireframeHeadLockCheckBox?.IsChecked == true
            ? "Camera-normalized live wireframe fallback"
            : "Camera-normalized live wireframe";
        return new LiveWireframeProjection(
            points,
            mode,
            HeadLocked: false,
            AxisOriginX: width * 0.5d,
            AxisOriginY: height * 0.5d,
            AxisUnitPixels: Math.Min(width, height) * 0.20d);
    }

    private bool TryBuildHeadLockedLiveWireframeProjection(
        LastGoodFeatureMeshSample sample,
        double width,
        double height,
        out LiveWireframeProjection projection)
    {
        projection = new LiveWireframeProjection(
            new Dictionary<int, LiveWireframeProjectedPoint>(),
            "Camera-normalized live wireframe fallback",
            HeadLocked: false,
            AxisOriginX: width * 0.5d,
            AxisOriginY: height * 0.5d,
            AxisUnitPixels: Math.Min(width, height) * 0.20d);

        var rawPoints = sample.Points.ToDictionary(static point => point.Index, ToMeshPoint);
        if (!TryGetFeatureCenter(sample, rawPoints, "left_eye", DenseMeshEyeA, out var leftEye)
            || !TryGetFeatureCenter(sample, rawPoints, "right_eye", DenseMeshEyeB, out var rightEye)
            || !TryGetFeatureCenter(sample, rawPoints, "jaw", DenseMeshJawCenter, out var chin))
        {
            return false;
        }

        var eyeMid = Multiply(Add(leftEye, rightEye), 0.5d);
        var xAxis = Subtract(rightEye, leftEye);
        var interEyeDistance = Length(xAxis);
        if (interEyeDistance < 0.0001d)
        {
            return false;
        }

        xAxis = Normalize(xAxis);
        var yAxis = Subtract(chin, eyeMid);
        yAxis = Subtract(yAxis, Multiply(xAxis, Dot(yAxis, xAxis)));
        if (Length(yAxis) < 0.0001d)
        {
            return false;
        }

        yAxis = Normalize(yAxis);
        var zAxis = Normalize(Cross(xAxis, yAxis));
        if (Length(zAxis) < 0.0001d)
        {
            return false;
        }

        yAxis = Normalize(Cross(zAxis, xAxis));
        var faceHeight = Distance(eyeMid, chin);
        var faceScale = Math.Max(0.0001d, Math.Max(interEyeDistance * 2.35d, faceHeight * 1.28d));
        var screenScale = Math.Min(width, height) * 0.50d;
        var originX = width * 0.5d;
        var originY = height * 0.50d;
        var projectedPoints = new Dictionary<int, LiveWireframeProjectedPoint>(rawPoints.Count);

        foreach (var (index, point) in rawPoints)
        {
            var relative = Subtract(point, eyeMid);
            var localX = Dot(relative, xAxis) / faceScale;
            var localY = Dot(relative, yAxis) / faceScale;
            var localZ = Dot(relative, zAxis) / faceScale;
            projectedPoints[index] = new LiveWireframeProjectedPoint(
                index,
                originX + localX * screenScale,
                originY + localY * screenScale,
                localZ);
        }

        projection = new LiveWireframeProjection(
            projectedPoints,
            $"Head-locked live wireframe | eye scale {interEyeDistance * 100d:0.#}%",
            HeadLocked: true,
            AxisOriginX: originX,
            AxisOriginY: originY,
            AxisUnitPixels: screenScale);
        return true;
    }

    private void DrawLiveWireframeHeadAxes(LiveWireframeProjection projection)
    {
        var xBrush = CreateFrozenBrush(0x65, 0xc8, 0xff, 0xe8);
        var yBrush = CreateFrozenBrush(0xff, 0xd1, 0x66, 0xe8);
        var axisLength = Math.Max(28d, projection.AxisUnitPixels * 0.16d);
        AddLiveWireframeAxis(projection.AxisOriginX, projection.AxisOriginY, projection.AxisOriginX + axisLength, projection.AxisOriginY, xBrush);
        AddLiveWireframeAxis(projection.AxisOriginX, projection.AxisOriginY, projection.AxisOriginX, projection.AxisOriginY + axisLength, yBrush);
    }

    private void AddLiveWireframeAxis(double x1, double y1, double x2, double y2, Brush brush)
    {
        LiveWireframeCanvas.Children.Add(new Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = 2d,
            IsHitTestVisible = false
        });
    }

    private static bool TryGetFeatureCenter(
        LastGoodFeatureMeshSample sample,
        IReadOnlyDictionary<int, MeshPoint3D> points,
        string featureGroupId,
        IReadOnlyList<int> fallbackIndices,
        out MeshPoint3D center)
    {
        var group = sample.FeatureGroups.FirstOrDefault(group =>
            string.Equals(group.Id, featureGroupId, StringComparison.OrdinalIgnoreCase));
        if (group is not null && TryGetCenter(points, group.LandmarkIndices, out center))
        {
            return true;
        }

        return TryGetCenter(points, fallbackIndices, out center);
    }

    private static bool TryGetCenter(
        IReadOnlyDictionary<int, MeshPoint3D> points,
        IReadOnlyList<int> indices,
        out MeshPoint3D center)
    {
        var values = indices
            .Where(points.ContainsKey)
            .Select(index => points[index])
            .ToList();
        if (values.Count == 0)
        {
            center = default;
            return false;
        }

        center = new MeshPoint3D(
            values.Average(static point => point.X),
            values.Average(static point => point.Y),
            values.Average(static point => point.Z));
        return true;
    }

    private static MeshPoint3D ToMeshPoint(FaceMeshLandmarkPoint point)
    {
        return new MeshPoint3D(point.X, point.Y, point.Z);
    }

    private static MeshPoint3D Add(MeshPoint3D first, MeshPoint3D second)
    {
        return new MeshPoint3D(first.X + second.X, first.Y + second.Y, first.Z + second.Z);
    }

    private static MeshPoint3D Subtract(MeshPoint3D first, MeshPoint3D second)
    {
        return new MeshPoint3D(first.X - second.X, first.Y - second.Y, first.Z - second.Z);
    }

    private static MeshPoint3D Multiply(MeshPoint3D point, double scale)
    {
        return new MeshPoint3D(point.X * scale, point.Y * scale, point.Z * scale);
    }

    private static double Dot(MeshPoint3D first, MeshPoint3D second)
    {
        return first.X * second.X + first.Y * second.Y + first.Z * second.Z;
    }

    private static MeshPoint3D Cross(MeshPoint3D first, MeshPoint3D second)
    {
        return new MeshPoint3D(
            first.Y * second.Z - first.Z * second.Y,
            first.Z * second.X - first.X * second.Z,
            first.X * second.Y - first.Y * second.X);
    }

    private static double Length(MeshPoint3D point)
    {
        return Math.Sqrt(Dot(point, point));
    }

    private static double Distance(MeshPoint3D first, MeshPoint3D second)
    {
        return Length(Subtract(first, second));
    }

    private static MeshPoint3D Normalize(MeshPoint3D point)
    {
        var length = Math.Max(0.000001d, Length(point));
        return Multiply(point, 1d / length);
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
        if (_currentFaceAnalysis is not { BaselineReady: true } analysis)
        {
            return Color.FromRgb(74, 147, 214);
        }

        if (analysis.EyeDropPercent >= GetEyeCueThreshold()
            || analysis.CompositeCuePercent >= GetCompositeCueThreshold())
        {
            return Color.FromRgb(220, 91, 91);
        }

        if (analysis.JawChangePercent >= GetJawCueThreshold())
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
        var faceBrush = new SolidColorBrush(Color.FromArgb(135, 185, 215, 239));
        var leftEyeInferred = frame.LeftEyeReconstructed || frame.EyeArtifactSuppressed;
        var rightEyeInferred = frame.RightEyeReconstructed || frame.EyeArtifactSuppressed;
        var eyeInferenceBrush = frame.EyeArtifactSuppressed ? inferredEyeBrush : eyeBrush;

        AddGuidePolyline(display, frame.FaceContour, faceBrush, 1.4d, close: true);
        AddGuidePolyline(display, frame.JawContour, faceBrush, 1.8d, close: false);
        AddGuidePolyline(display, frame.LeftEyeContour, leftEyeInferred ? eyeInferenceBrush : eyeBrush, 2.4d, close: true, inferred: leftEyeInferred);
        AddGuidePolyline(display, frame.RightEyeContour, rightEyeInferred ? eyeInferenceBrush : eyeBrush, 2.4d, close: true, inferred: rightEyeInferred);
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

    private string CreateOverlayMetrics(double? motion)
    {
        return string.Join(Environment.NewLine, CreateOverlayMetricLines(motion));
    }

    private IReadOnlyList<string> CreateOverlayMetricLines(double? motion)
    {
        var motionLabel = motion is double value
            ? $"Motion {value:0.0}% / {GetMotionThreshold():0.0}%"
            : $"Motion -- / {GetMotionThreshold():0.0}%";

        var lines = new List<string>();
        if (_faceLandmarkTracker.IsAvailable && FaceAutoFollowCheckBox.IsChecked == true)
        {
            var now = DateTime.UtcNow;
            var detectionLabel = GetFaceFeatureTrackerStatus(now);
            lines.Add($"{motionLabel} | {detectionLabel}");
        }
        else
        {
            lines.Add(motionLabel);
        }

        lines.Add(FormatCompactFaceSignalLine(_currentFaceLandmarkMetrics));
        lines.Add(FormatCompactFaceFrameGeometryLine(_currentFaceFrameGeometry));
        lines.Add(FormatCompactReconstructionLaneLine());
        lines.Add(FormatCueLine(_currentFaceLandmarkCueAnalysis, _currentFaceLandmarkTrendAnalysis, _currentFaceAnalysis));
        lines.Add(FormatCompactAvatarLine());
        return lines;
    }

    private string FormatCompactFaceSignalLine(FaceLandmarkMetrics metrics)
    {
        if (!metrics.HasFace)
        {
            return "Face: waiting for landmarks";
        }

        var eyeLock = metrics.IsEyeMeasurementUsable ? "ok" : "limited";
        var mouthLock = metrics.IsMouthMeasurementUsable ? "ok" : "limited";
        var browLock = metrics.IsBrowMeasurementUsable ? "ok" : "limited";
        var blink = metrics.MediaPipeAverageEyeBlinkPercent is double blinkPercent
            ? $" | blink {blinkPercent:0}%"
            : "";
        var jawOpen = metrics.MediaPipeJawOpenPercent is double jawOpenPercent
            ? $" | MP jaw {jawOpenPercent:0}%"
            : "";
        var artifact = metrics.PossibleOneEyeArtifact
            ? " | eye artifact"
            : metrics.AnyEyeReconstructed || metrics.MouthReconstructed || metrics.EyeArtifactSuppressed
                ? " | reconstructed"
                : "";
        return $"Eyes {FormatRatioPercent(metrics.AverageEyeOpeningRatio)} ({eyeLock} q{metrics.EyeMeasurementQualityPercent:0}%) | brow {FormatRatioPercent(metrics.AverageBrowHeightRatio)} ({browLock} q{metrics.BrowMeasurementQualityPercent:0}%) | mouth {FormatRatioPercent(metrics.MouthOpeningRatio)} ({mouthLock} q{metrics.MouthMeasurementQualityPercent:0}%) | jaw {FormatRatioPercent(metrics.JawDroopRatio)}{blink}{jawOpen}{artifact}";
    }

    private static string FormatCompactFaceFrameGeometryLine(FaceFrameGeometry pose)
    {
        if (!pose.HasFace)
        {
            return "Face frame: waiting for face lock";
        }

        var distance = pose.ApparentDistanceUnits is double units
            ? $"Z {units:0.##}"
            : "Z --";
        if (pose.ZRelativeToReference is double relative)
        {
            distance += $" ({relative:0.##}x ref)";
        }

        var fill = pose.FaceFillWidthPercent is double width && pose.FaceFillHeightPercent is double height
            ? $" | fill {width:0.#}% x {height:0.#}%"
            : "";
        return $"Face frame: X {pose.XHorizontalPercent:0.#}% | Y {pose.YVerticalPercent:0.#}% | {distance}{fill} | MP A/X {pose.ARotationAroundXDegrees:0.#} deg | B/Y {pose.BRotationAroundYDegrees:0.#} deg | C/Z {pose.CRotationAroundZDegrees:0.#} deg";
    }

    private string FormatCompactReconstructionLaneLine()
    {
        var pending = Interlocked.CompareExchange(ref _threeDdfaOnnxReconstructionPending, 0, 0) == 1;
        if (!_threeDdfaOnnxEnvironment.IsReady)
        {
            return "3DDFA avatar lane: waiting for install";
        }

        if (pending)
        {
            return "3DDFA avatar lane: reconstructing";
        }

        var response = _currentThreeDdfaOnnxResponse;
        return response.Ok && response.HasFace
            ? $"3DDFA avatar lane: dense {response.DenseVertexCount} | q{response.ReconstructionConfidencePercent:0}% | A/B/C {response.Pose.ARotationAroundXDegrees:0.#}/{response.Pose.BRotationAroundYDegrees:0.#}/{response.Pose.CRotationAroundZDegrees:0.#}"
            : "3DDFA avatar lane: ready, waiting";
    }

    private string FormatCompactAvatarLine()
    {
        var quality = _currentAvatarCaptureQuality.ScorePercent > 0d
            ? $"quality {_currentAvatarCaptureQuality.Label} {_currentAvatarCaptureQuality.ScorePercent:0}%"
            : "quality waiting";
        if (AvatarSubjectCheckBox.IsChecked != true)
        {
            return $"Avatar: subject off | {quality}";
        }

        if (!_avatarLearningRequested)
        {
            return $"Avatar: capture stopped | 3DDFA_V2 ONNX idle | {quality}";
        }

        var captureState = _currentAvatarCaptureQuality.CanCollectMeasurements
            ? "3D capture"
            : $"waiting ({_currentAvatarCaptureQuality.Label})";
        return $"Avatar: {captureState} | {FormatCompactReconstructionLaneLine()} | {quality}";
    }

    private static string FormatFaceReliabilityLine(FaceLockStabilityAnalysis stability)
    {
        if (stability.SampleCount <= 0)
        {
            return "Face reliability: waiting for temporal lock";
        }

        if (stability.SampleCount < 3)
        {
            return $"Face reliability: warming {stability.SampleCount} sample(s)";
        }

        return $"Face reliability: {stability.Label} {stability.CompositeReliabilityPercent:0}% | continuity {stability.FaceContinuityPercent:0}% | eye {stability.EyeReliabilityPercent:0}% | mouth {stability.MouthReliabilityPercent:0}%";
    }

    private static string FormatStorageSize(long bytes)
    {
        if (bytes < 1024L)
        {
            return $"{Math.Max(0L, bytes)} B";
        }

        var size = (double)bytes;
        string[] units = ["KB", "MB", "GB"];
        var unitIndex = -1;
        while (size >= 1024d && unitIndex < units.Length - 1)
        {
            size /= 1024d;
            unitIndex++;
        }

        return $"{size:0.#} {units[unitIndex]}";
    }

    private static string FormatEyeLockLine(FaceLandmarkMetrics metrics)
    {
        if (!metrics.HasFace)
        {
            return "Eye lock: waiting for face landmarks";
        }

        var lockLabel = metrics.IsEyeMeasurementUsable ? "usable" : "limited";
        var artifact = metrics.PossibleOneEyeArtifact ? "; possible one-eye artifact" : "";
        var reconstructed = metrics.AnyEyeReconstructed || metrics.EyeArtifactSuppressed
            ? "; reconstruction used"
            : "";
        var mediaPipe = metrics.MediaPipeAverageEyeBlinkPercent is double blink
            ? $"; MP blink {blink:0}%"
            : "";
        return $"Eye lock: {lockLabel} q{metrics.EyeMeasurementQualityPercent:0}% | open {FormatRatioPercent(metrics.AverageEyeOpeningRatio)} | agree {metrics.EyeAgreementPercent:0}%{mediaPipe}{artifact}{reconstructed}";
    }

    private static string FormatMouthLockLine(FaceLandmarkMetrics metrics)
    {
        if (!metrics.HasFace)
        {
            return "Mouth lock: waiting for face landmarks";
        }

        var lockLabel = metrics.IsMouthMeasurementUsable ? "usable" : "limited";
        var reconstructed = metrics.MouthReconstructed ? "; reconstruction used" : "";
        var mediaPipe = CreateMediaPipeMouthLabel(metrics);
        return $"Mouth lock: {lockLabel} q{metrics.MouthMeasurementQualityPercent:0}% | open {FormatRatioPercent(metrics.MouthOpeningRatio)} | jaw drop {FormatRatioPercent(metrics.JawDroopRatio)}{mediaPipe}{reconstructed}";
    }

    private static string CreateMediaPipeMouthLabel(FaceLandmarkMetrics metrics)
    {
        var parts = new List<string>();
        if (metrics.MediaPipeJawOpenPercent is double jawOpen)
        {
            parts.Add($"jaw {jawOpen:0}%");
        }

        if (metrics.MediaPipeMouthClosePercent is double mouthClose)
        {
            parts.Add($"close {mouthClose:0}%");
        }

        if (metrics.MediaPipeMouthOpeningCorrectionRatio is double correction)
        {
            parts.Add($"corr {FormatSigned(correction)}");
        }

        return parts.Count == 0 ? "" : $"; MP {string.Join(", ", parts)}";
    }

    private static string FormatCueLine(
        FaceLandmarkCueAnalysis? landmarkCue,
        FaceLandmarkTrendAnalysis trend,
        FaceCueAnalysis? regionCue)
    {
        if (landmarkCue is null && regionCue is null)
        {
            return "Cue: waiting for baseline";
        }

        var cueScore = landmarkCue?.CompositeCuePercent ?? regionCue?.CompositeCuePercent;
        var eyeCue = landmarkCue?.EyeClosurePercent ?? regionCue?.EyeDropPercent;
        var mouthCue = MaxSignal(landmarkCue?.MouthOpeningChangePercent, landmarkCue?.JawDroopChangePercent, regionCue?.JawChangePercent);
        var trendLabel = trend.HasUsableTrend
            ? $" | trend eye {FormatPercent(trend.EyeClosingTrendPercent)}, mouth {FormatPercent(trend.MouthOpeningTrendPercent)}"
            : "";
        return $"Cue: score {FormatPercent(cueScore)} | eye close {FormatPercent(eyeCue)} | mouth/jaw {FormatPercent(mouthCue)}{trendLabel}";
    }

    private static string FormatRatioPercent(double? value)
    {
        return value is double number ? $"{number * 100d:0}%" : "--";
    }

    private static string FormatPercent(double? value)
    {
        return value is double number ? $"{number:0}%" : "--";
    }

    private static string FormatSigned(double value)
    {
        return value.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture);
    }

    private string GetFaceFeatureTrackerStatus(DateTime now)
    {
        if (!_faceLandmarkTracker.IsAvailable)
        {
            return "landmark tracker unavailable";
        }

        if (HasFreshFaceFeatureLock(now))
        {
            return "landmark face lock";
        }

        if (HasUsableFaceFeatureLock(now))
        {
            return "landmark face hold";
        }

        var trackerStatus = _faceLandmarkTracker.LastBackendStatus;
        if (!string.IsNullOrWhiteSpace(trackerStatus) && trackerStatus.Contains("dense", StringComparison.OrdinalIgnoreCase))
        {
            return Interlocked.CompareExchange(ref _faceFeatureDetectionPending, 0, 0) == 1
                ? $"landmark search ({trackerStatus})"
                : $"landmark waiting ({trackerStatus})";
        }

        return Interlocked.CompareExchange(ref _faceFeatureDetectionPending, 0, 0) == 1
            ? "landmark search"
            : "landmark waiting";
    }

    private void WriteAnnotatedVideoFrame(BitmapSource bitmap, DateTime timestamp, string state, string metrics, string trigger, string accentColor)
    {
        if (_activeEpisodeStartedAt is null)
        {
            if ((timestamp - _lastBufferedVideoFrameAt).TotalSeconds < 1d / EventVideoFramesPerSecond)
            {
                return;
            }

            var bufferedJpeg = CreateAnnotatedJpeg(bitmap, timestamp, state, metrics, trigger, accentColor, _currentFaceAnalysis, _currentFaceFeatureDetection, _currentFaceLandmarkFrame, GetFaceCueLayout(), GetEyeCueThreshold(), GetJawCueThreshold(), GetCompositeCueThreshold());
            AddPreEventVideoFrame(timestamp, bufferedJpeg);
            return;
        }

        if ((timestamp - _lastRecordedVideoFrameAt).TotalSeconds < 1d / EventVideoFramesPerSecond)
        {
            return;
        }

        _lastRecordedVideoFrameAt = timestamp;
        var jpeg = CreateAnnotatedJpeg(bitmap, timestamp, state, metrics, trigger, accentColor, _currentFaceAnalysis, _currentFaceFeatureDetection, _currentFaceLandmarkFrame, GetFaceCueLayout(), GetEyeCueThreshold(), GetJawCueThreshold(), GetCompositeCueThreshold());
        _eventRecorder.AddFrame(jpeg);
    }

    private void BufferAnnotatedFrame(BitmapSource bitmap, DateTime timestamp, string state, string metrics, string trigger, string accentColor)
    {
        if ((timestamp - _lastBufferedVideoFrameAt).TotalSeconds < 1d / EventVideoFramesPerSecond)
        {
            return;
        }

        var jpeg = CreateAnnotatedJpeg(bitmap, timestamp, state, metrics, trigger, accentColor, _currentFaceAnalysis, _currentFaceFeatureDetection, _currentFaceLandmarkFrame, GetFaceCueLayout(), GetEyeCueThreshold(), GetJawCueThreshold(), GetCompositeCueThreshold());
        AddPreEventVideoFrame(timestamp, jpeg);
    }

    private void AddPreEventVideoFrame(DateTime timestamp, byte[] jpeg)
    {
        _lastBufferedVideoFrameAt = timestamp;
        _preEventVideoFrames.Enqueue(new BufferedVideoFrame(timestamp, jpeg));
        var oldestAllowed = timestamp.AddSeconds(-PreEventVideoSeconds);
        var maximumFrames = (int)(PreEventVideoSeconds * EventVideoFramesPerSecond);
        while (_preEventVideoFrames.Count > 0
            && (_preEventVideoFrames.Peek().Timestamp < oldestAllowed || _preEventVideoFrames.Count > maximumFrames))
        {
            _preEventVideoFrames.Dequeue();
        }
    }

    private static byte[] CreateAnnotatedJpeg(
        BitmapSource bitmap,
        DateTime timestamp,
        string state,
        string metrics,
        string trigger,
        string accentColor,
        FaceCueAnalysis? faceAnalysis,
        FaceFeatureDetection featureDetection,
        FaceLandmarkFrame landmarkFrame,
        FaceCueGuideLayout layout,
        double eyeCueThreshold,
        double jawCueThreshold,
        double compositeCueThreshold)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.DrawImage(bitmap, new Rect(0, 0, width, height));
            DrawFaceCueGuides(context, new Rect(0, 0, width, height), faceAnalysis, featureDetection, landmarkFrame, layout, eyeCueThreshold, jawCueThreshold, compositeCueThreshold);

            var dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow);
            var pixelsPerDip = dpi.PixelsPerDip;
            var font = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var smallFont = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var margin = Math.Max(16d, width * 0.018d);
            var boxWidth = Math.Min(width - margin * 2d, Math.Max(560d, width * 0.72d));
            var lineHeight = Math.Max(22d, height * 0.032d);
            var metricLines = SplitOverlayLines(metrics, 4);
            var triggerLines = SplitOverlayLines(trigger, 2);
            var boxLineCount = 2 + metricLines.Count + triggerLines.Count;
            var boxHeight = Math.Min(height - margin * 2d, Math.Max(lineHeight * 4.6d, boxLineCount * lineHeight + 30d));
            var box = new Rect(margin, margin, boxWidth, boxHeight);
            var accent = (Color)ColorConverter.ConvertFromString(accentColor);

            context.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(218, 8, 13, 18)), new Pen(new SolidColorBrush(accent), 3), box, 4, 4);

            var cursorY = box.Y + 10d;
            DrawText(context, state, font, Math.Max(18d, height * 0.028d), Brushes.White, box.X + 14, cursorY, box.Width - 28, pixelsPerDip, maxLineCount: 1);
            cursorY += lineHeight;
            DrawText(context, timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture), smallFont, Math.Max(14d, height * 0.021d), Brushes.WhiteSmoke, box.X + 14, cursorY, box.Width - 28, pixelsPerDip, maxLineCount: 1);
            cursorY += lineHeight;

            var metricBrush = new SolidColorBrush(Color.FromRgb(185, 215, 239));
            foreach (var line in metricLines)
            {
                DrawText(context, line, smallFont, Math.Max(14d, height * 0.020d), metricBrush, box.X + 14, cursorY, box.Width - 28, pixelsPerDip, maxLineCount: 1);
                cursorY += lineHeight;
            }

            var triggerBrush = new SolidColorBrush(Color.FromRgb(220, 231, 239));
            foreach (var line in triggerLines)
            {
                DrawText(context, line, smallFont, Math.Max(13d, height * 0.019d), triggerBrush, box.X + 14, cursorY, box.Width - 28, pixelsPerDip, maxLineCount: 1);
                cursorY += lineHeight;
            }
        }

        var render = new RenderTargetBitmap(width, height, bitmap.DpiX, bitmap.DpiY, PixelFormats.Pbgra32);
        render.Render(visual);
        render.Freeze();

        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(render));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void DrawFaceCueGuides(
        DrawingContext context,
        Rect display,
        FaceCueAnalysis? analysis,
        FaceFeatureDetection featureDetection,
        FaceLandmarkFrame landmarkFrame,
        FaceCueGuideLayout layout,
        double eyeCueThreshold,
        double jawCueThreshold,
        double compositeCueThreshold)
    {
        var accent = GetFaceCueGuideColor(analysis, eyeCueThreshold, jawCueThreshold, compositeCueThreshold);
        var regionBrush = new SolidColorBrush(Color.FromArgb(34, accent.R, accent.G, accent.B));
        var lineBrush = new SolidColorBrush(Color.FromArgb(235, accent.R, accent.G, accent.B));
        var supportBrush = new SolidColorBrush(Color.FromArgb(175, 185, 215, 239));
        var regionPen = new Pen(new SolidColorBrush(Color.FromArgb(170, accent.R, accent.G, accent.B)), Math.Max(2d, display.Height * 0.004d));
        var linePen = new Pen(lineBrush, Math.Max(3d, display.Height * 0.006d))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        var supportPen = new Pen(supportBrush, Math.Max(1d, display.Height * 0.002d));

        var face = layout.GetFaceBox();
        var leftEye = layout.ToFrameRect(layout.LeftEye);
        var rightEye = layout.ToFrameRect(layout.RightEye);
        var jaw = layout.ToFrameRect(layout.Jaw);

        context.DrawRoundedRectangle(Brushes.Transparent, supportPen, ToDisplayRect(display, face), 3, 3);
        context.DrawRoundedRectangle(regionBrush, regionPen, ToDisplayRect(display, leftEye), 3, 3);
        context.DrawRoundedRectangle(regionBrush, regionPen, ToDisplayRect(display, rightEye), 3, 3);
        context.DrawRoundedRectangle(regionBrush, regionPen, ToDisplayRect(display, jaw), 3, 3);

        DrawRelativeLine(context, display, leftEye.Left, leftEye.Top + leftEye.Height * 0.50d, leftEye.Right, leftEye.Top + leftEye.Height * 0.50d, linePen);
        DrawRelativeLine(context, display, rightEye.Left, rightEye.Top + rightEye.Height * 0.50d, rightEye.Right, rightEye.Top + rightEye.Height * 0.50d, linePen);
        DrawRelativeLine(context, display, jaw.Left + jaw.Width * 0.16d, jaw.Top + jaw.Height * 0.38d, jaw.Right - jaw.Width * 0.16d, jaw.Top + jaw.Height * 0.38d, linePen);
        DrawRelativeLine(context, display, face.Left + face.Width * 0.50d, face.Top, face.Left + face.Width * 0.50d, face.Bottom, supportPen);

        if (featureDetection.HasFace)
        {
            var detectorPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 244, 211, 94)), Math.Max(2d, display.Height * 0.004d));
            context.DrawRoundedRectangle(Brushes.Transparent, detectorPen, ToDisplayRect(display, featureDetection.FaceBox), 3, 3);
            if (featureDetection.LeftEyeBox is Rect leftEyeBox)
            {
                context.DrawRoundedRectangle(Brushes.Transparent, detectorPen, ToDisplayRect(display, leftEyeBox), 3, 3);
            }

            if (featureDetection.RightEyeBox is Rect rightEyeBox)
            {
                context.DrawRoundedRectangle(Brushes.Transparent, detectorPen, ToDisplayRect(display, rightEyeBox), 3, 3);
            }

            if (featureDetection.MouthBox is Rect mouthBox)
            {
                context.DrawRoundedRectangle(Brushes.Transparent, detectorPen, ToDisplayRect(display, mouthBox), 3, 3);
            }
        }

        if (landmarkFrame.HasFace)
        {
            DrawLandmarkContours(context, display, landmarkFrame);
        }
    }

    private static void DrawLandmarkContours(DrawingContext context, Rect display, FaceLandmarkFrame frame)
    {
        var eyePen = CreateLandmarkPen(Color.FromArgb(245, 122, 218, 255), Math.Max(2d, display.Height * 0.004d));
        var inferredEyePen = CreateLandmarkPen(Color.FromArgb(245, 238, 174, 74), Math.Max(2d, display.Height * 0.004d), inferred: true);
        var reconstructedEyePen = CreateLandmarkPen(Color.FromArgb(245, 122, 218, 255), Math.Max(2d, display.Height * 0.004d), inferred: true);
        var lipPen = CreateLandmarkPen(Color.FromArgb(245, 255, 190, 110), Math.Max(2d, display.Height * 0.0035d));
        var reconstructedLipPen = CreateLandmarkPen(Color.FromArgb(245, 255, 190, 110), Math.Max(2d, display.Height * 0.0035d), inferred: true);
        var browPen = CreateLandmarkPen(Color.FromArgb(245, 196, 247, 163), Math.Max(2d, display.Height * 0.0035d));
        var facePen = CreateLandmarkPen(Color.FromArgb(135, 185, 215, 239), Math.Max(1d, display.Height * 0.002d));
        var leftEyePen = frame.EyeArtifactSuppressed
            ? inferredEyePen
            : frame.LeftEyeReconstructed ? reconstructedEyePen : eyePen;
        var rightEyePen = frame.EyeArtifactSuppressed
            ? inferredEyePen
            : frame.RightEyeReconstructed ? reconstructedEyePen : eyePen;
        var mouthPen = frame.MouthReconstructed ? reconstructedLipPen : lipPen;

        DrawRelativePolyline(context, display, frame.FaceContour, facePen, close: true);
        DrawRelativePolyline(context, display, frame.JawContour, facePen, close: false);
        DrawRelativePolyline(context, display, frame.LeftEyeContour, leftEyePen, close: true);
        DrawRelativePolyline(context, display, frame.RightEyeContour, rightEyePen, close: true);
        DrawRelativePolyline(context, display, frame.LeftBrowContour, browPen, close: false);
        DrawRelativePolyline(context, display, frame.RightBrowContour, browPen, close: false);
        DrawRelativePolyline(context, display, frame.OuterLipContour, mouthPen, close: true);
        DrawRelativePolyline(context, display, frame.InnerLipContour, mouthPen, close: true);
    }

    private static Pen CreateLandmarkPen(Color color, double thickness, bool inferred = false)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        if (inferred)
        {
            pen.DashStyle = new DashStyle(new[] { 5d, 3d }, 0d);
        }

        return pen;
    }

    private static void DrawRelativePolyline(DrawingContext context, Rect display, IReadOnlyList<Point> points, Pen pen, bool close)
    {
        if (points.Count < 2)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            geometryContext.BeginFigure(ToDisplayPoint(display, points[0]), isFilled: false, isClosed: close);
            for (var index = 1; index < points.Count; index++)
            {
                geometryContext.LineTo(ToDisplayPoint(display, points[index]), isStroked: true, isSmoothJoin: true);
            }
        }

        geometry.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    private static Color GetFaceCueGuideColor(FaceCueAnalysis? analysis, double eyeCueThreshold, double jawCueThreshold, double compositeCueThreshold)
    {
        if (analysis is not { BaselineReady: true })
        {
            return Color.FromRgb(74, 147, 214);
        }

        if (analysis.EyeDropPercent >= eyeCueThreshold
            || analysis.CompositeCuePercent >= compositeCueThreshold)
        {
            return Color.FromRgb(220, 91, 91);
        }

        if (analysis.JawChangePercent >= jawCueThreshold)
        {
            return Color.FromRgb(215, 165, 58);
        }

        return Color.FromRgb(74, 163, 107);
    }

    private static void DrawRelativeLine(DrawingContext context, Rect display, double x1, double y1, double x2, double y2, Pen pen)
    {
        context.DrawLine(
            pen,
            new Point(display.X + display.Width * x1, display.Y + display.Height * y1),
            new Point(display.X + display.Width * x2, display.Y + display.Height * y2));
    }

    private static IReadOnlyList<string> SplitOverlayLines(string text, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(text) || maxLines <= 0)
        {
            return [];
        }

        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(static line => line.Trim())
            .Where(static line => line.Length > 0)
            .ToList();
        if (lines.Count <= maxLines)
        {
            return lines;
        }

        var clipped = lines.Take(maxLines).ToList();
        clipped[^1] = clipped[^1].TrimEnd('.', ' ') + "...";
        return clipped;
    }

    private static void DrawText(DrawingContext context, string text, Typeface font, double size, Brush brush, double x, double y, double maxWidth, double pixelsPerDip, int maxLineCount = 2)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            font,
            size,
            brush,
            pixelsPerDip)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = Math.Max(1, maxLineCount),
            Trimming = TextTrimming.CharacterEllipsis
        };

        context.DrawText(formatted, new Point(x, y));
    }

    private sealed class EventSummary
    {
        public DateTime StartedAt { get; init; }

        public DateTime EndedAt { get; init; }

        public double DurationSeconds { get; init; }

        public string EndReason { get; init; } = "";

        public IReadOnlyList<string> TriggerReasons { get; init; } = [];

        public string AverageMotion { get; init; } = "";

        public string FaceCueStatus { get; init; } = "";

        public string FaceCueQuality { get; init; } = "";

        public double? FaceCueScore { get; init; }

        public double? EyeOpenness { get; init; }

        public double? EyeDrop { get; init; }

        public double? EyeAsymmetry { get; init; }

        public double? JawChange { get; init; }

        public double? JawAsymmetry { get; init; }

        public double? LowerFaceDrop { get; init; }

        public double? HeadDrift { get; init; }

        public string LandmarkSource { get; init; } = "";

        public string LandmarkConfidence { get; init; } = "";

        public double? LandmarkTrackingConfidence { get; init; }

        public double? LandmarkEyeConfidence { get; init; }

        public double? LandmarkMouthConfidence { get; init; }

        public double? LandmarkEyeQuality { get; init; }

        public double? LandmarkMouthQuality { get; init; }

        public double? LandmarkOverallQuality { get; init; }

        public string CaptureQualityLabel { get; init; } = "";

        public double? CaptureQualityScore { get; init; }

        public bool? CaptureQualityCanCollect { get; init; }

        public bool? CaptureQualityAvatarGrade { get; init; }

        public string CaptureQualityReason { get; init; } = "";

        public double? CaptureQualityCameraModeScore { get; init; }

        public double? CaptureQualityFaceScaleScore { get; init; }

        public double? CaptureQualityEyeScore { get; init; }

        public double? CaptureQualityMouthScore { get; init; }

        public double? CaptureQualityStabilityScore { get; init; }

        public double? CaptureQualityGlassesScore { get; init; }

        public double? CaptureQualityStorageScore { get; init; }

        public double? CaptureQualityFaceWidth { get; init; }

        public double? CaptureQualityFaceHeight { get; init; }

        public IReadOnlyList<string> CaptureQualityIssues { get; init; } = [];

        public IReadOnlyList<string> CaptureQualitySuggestions { get; init; } = [];

        public string LandmarkFaceReliabilityStatus { get; init; } = "";

        public int LandmarkFaceReliabilitySamples { get; init; }

        public double? LandmarkFaceReliability { get; init; }

        public double? LandmarkFaceContinuity { get; init; }

        public double? LandmarkEyeReliability { get; init; }

        public double? LandmarkMouthReliability { get; init; }

        public double? LandmarkFaceBoundsRate { get; init; }

        public double? LandmarkEyeUsableRate { get; init; }

        public double? LandmarkMouthUsableRate { get; init; }

        public bool? LandmarkEyeImageQualityAvailable { get; init; }

        public bool? LandmarkMouthImageQualityAvailable { get; init; }

        public double? LandmarkEyeGlare { get; init; }

        public double? LandmarkMouthGlare { get; init; }

        public double? LandmarkEyeContrast { get; init; }

        public double? LandmarkMouthContrast { get; init; }

        public double? LandmarkEyeSharpness { get; init; }

        public double? LandmarkMouthSharpness { get; init; }

        public double? LandmarkEyeDarkCoverage { get; init; }

        public double? LandmarkMouthDarkCoverage { get; init; }

        public double? LandmarkRawEyeAsymmetry { get; init; }

        public double? LandmarkEyeAsymmetry { get; init; }

        public double? LandmarkEyeAgreement { get; init; }

        public bool? LandmarkPossibleOneEyeArtifact { get; init; }

        public bool? LandmarkLeftEyeReconstructed { get; init; }

        public bool? LandmarkRightEyeReconstructed { get; init; }

        public bool? LandmarkMouthReconstructed { get; init; }

        public bool? LandmarkEyeArtifactSuppressed { get; init; }

        public double? LandmarkRawEyeOpening { get; init; }

        public double? LandmarkRawMouthOpening { get; init; }

        public double? LandmarkRawJawDroop { get; init; }

        public double? LandmarkEyeOpening { get; init; }

        public double? LandmarkMouthOpening { get; init; }

        public double? LandmarkMouthOpeningVelocity { get; init; }

        public double? LandmarkJawDroop { get; init; }

        public double? LandmarkJawDroopVelocity { get; init; }

        public double? LandmarkMediaPipeLeftEyeBlink { get; init; }

        public double? LandmarkMediaPipeRightEyeBlink { get; init; }

        public double? LandmarkMediaPipeAverageEyeBlink { get; init; }

        public double? LandmarkMediaPipeJawOpen { get; init; }

        public double? LandmarkMediaPipeMouthClose { get; init; }

        public double? LandmarkMediaPipeEyeOpeningCorrection { get; init; }

        public double? LandmarkMediaPipeMouthOpeningCorrection { get; init; }

        public bool? LandmarkMediaPipeEyeOpeningCorrected { get; init; }

        public bool? LandmarkMediaPipeMouthOpeningCorrected { get; init; }

        public bool? LandmarkMediaPipeBlinkBaselineReady { get; init; }

        public bool? LandmarkMediaPipeMouthBaselineReady { get; init; }

        public double? LandmarkMediaPipeBlinkBaseline { get; init; }

        public double? LandmarkMediaPipeJawOpenBaseline { get; init; }

        public double? LandmarkMediaPipeMouthCloseBaseline { get; init; }

        public double? LandmarkMediaPipeBlinkChange { get; init; }

        public double? LandmarkMediaPipeJawOpenChange { get; init; }

        public double? LandmarkMediaPipeMouthCloseDrop { get; init; }

        public double? LandmarkMediaPipeMouthOpeningEvidence { get; init; }

        public string LandmarkCueStatus { get; init; } = "";

        public double? LandmarkCueScore { get; init; }

        public bool? LandmarkEyeCueEligible { get; init; }

        public bool? LandmarkMouthCueEligible { get; init; }

        public double? LandmarkEyeClosure { get; init; }

        public double? LandmarkMouthOpeningChange { get; init; }

        public double? LandmarkJawDroopBaseline { get; init; }

        public double? LandmarkJawDroopChange { get; init; }

        public string LandmarkTrendStatus { get; init; } = "";

        public double? LandmarkTrendScore { get; init; }

        public double? LandmarkTrendWindowSeconds { get; init; }

        public double? LandmarkEyeClosingTrend { get; init; }

        public double? LandmarkMouthOpeningTrend { get; init; }

        public double? LandmarkEyeOpeningSlope { get; init; }

        public double? LandmarkMouthOpeningSlope { get; init; }

        public int LandmarkEventSamples { get; init; }

        public IReadOnlyList<string> LandmarkEventSources { get; init; } = [];

        public IReadOnlyList<string> LandmarkEventBackendStatuses { get; init; } = [];

        public double? LandmarkEventMinimumEyeQuality { get; init; }

        public double? LandmarkEventMinimumMouthQuality { get; init; }

        public double? LandmarkEventMinimumOverallQuality { get; init; }

        public double? LandmarkEventAverageOverallQuality { get; init; }

        public int LandmarkEventCaptureQualitySamples { get; init; }

        public int LandmarkEventCaptureQualityCanCollectSamples { get; init; }

        public int LandmarkEventCaptureQualityAvatarGradeSamples { get; init; }

        public double? LandmarkEventMinimumCaptureQualityScore { get; init; }

        public double? LandmarkEventMaximumCaptureQualityScore { get; init; }

        public double? LandmarkEventAverageCaptureQualityScore { get; init; }

        public IReadOnlyList<string> LandmarkEventCaptureQualityLabels { get; init; } = [];

        public IReadOnlyList<string> LandmarkEventCaptureQualityIssues { get; init; } = [];

        public int LandmarkEventFaceReliabilitySamples { get; init; }

        public int LandmarkEventFaceReliabilityUsableSamples { get; init; }

        public double? LandmarkEventMinimumFaceReliability { get; init; }

        public double? LandmarkEventAverageFaceReliability { get; init; }

        public double? LandmarkEventMinimumFaceContinuity { get; init; }

        public double? LandmarkEventAverageFaceContinuity { get; init; }

        public double? LandmarkEventMinimumEyeReliability { get; init; }

        public double? LandmarkEventAverageEyeReliability { get; init; }

        public double? LandmarkEventMinimumMouthReliability { get; init; }

        public double? LandmarkEventAverageMouthReliability { get; init; }

        public double? LandmarkEventMinimumEyeOpening { get; init; }

        public double? LandmarkEventMaximumEyeClosure { get; init; }

        public double? LandmarkEventMaximumMouthOpening { get; init; }

        public double? LandmarkEventMaximumMouthOpeningChange { get; init; }

        public double? LandmarkEventMaximumMouthOpeningVelocity { get; init; }

        public double? LandmarkEventMaximumJawDroop { get; init; }

        public double? LandmarkEventMaximumJawDroopChange { get; init; }

        public double? LandmarkEventMaximumJawDroopVelocity { get; init; }

        public double? LandmarkEventMaximumMediaPipeAverageEyeBlink { get; init; }

        public double? LandmarkEventMaximumMediaPipeJawOpen { get; init; }

        public double? LandmarkEventMinimumMediaPipeMouthClose { get; init; }

        public double? LandmarkEventMaximumMediaPipeBlinkChange { get; init; }

        public double? LandmarkEventMaximumMediaPipeJawOpenChange { get; init; }

        public double? LandmarkEventMaximumMediaPipeMouthCloseDrop { get; init; }

        public double? LandmarkEventMaximumMediaPipeMouthOpeningEvidence { get; init; }

        public int LandmarkEventMediaPipeEyeOpeningCorrectedSamples { get; init; }

        public int LandmarkEventMediaPipeMouthOpeningCorrectedSamples { get; init; }

        public double? LandmarkEventMaximumAbsoluteMediaPipeEyeOpeningCorrection { get; init; }

        public double? LandmarkEventMaximumAbsoluteMediaPipeMouthOpeningCorrection { get; init; }

        public double? LandmarkEventMaximumCueScore { get; init; }

        public double? LandmarkEventMaximumEyeClosingTrend { get; init; }

        public double? LandmarkEventMaximumMouthOpeningTrend { get; init; }

        public double? LandmarkEventMinimumEyeOpeningSlope { get; init; }

        public double? LandmarkEventMaximumMouthOpeningSlope { get; init; }

        public double? LandmarkEventMaximumTrendScore { get; init; }

        public double? LandmarkEventMaximumEyeGlare { get; init; }

        public double? LandmarkEventMaximumMouthGlare { get; init; }

        public double? LandmarkEventMinimumEyeContrast { get; init; }

        public double? LandmarkEventMinimumMouthContrast { get; init; }

        public double? LandmarkEventMinimumEyeSharpness { get; init; }

        public double? LandmarkEventMinimumMouthSharpness { get; init; }

        public double? LandmarkEventMaximumRawEyeAsymmetry { get; init; }

        public double? LandmarkEventMaximumEyeAsymmetry { get; init; }

        public int LandmarkEventPossibleOneEyeArtifactSamples { get; init; }

        public int LandmarkEventLeftEyeReconstructedSamples { get; init; }

        public int LandmarkEventRightEyeReconstructedSamples { get; init; }

        public int LandmarkEventMouthReconstructedSamples { get; init; }

        public int LandmarkEventEyeArtifactSuppressedSamples { get; init; }

        public string VideoFile { get; init; } = "";

        public bool VideoOverlayBurnedIn { get; init; }

        public int PreEventVideoSeconds { get; init; }

        public int LandmarkTimelineSamples { get; init; }

        public string LandmarkTimelineJsonFile { get; init; } = "";

        public string LandmarkTimelineCsvFile { get; init; } = "";

        public string StartSnapshot { get; init; } = "";

        public string EndSnapshot { get; init; } = "";

        public string OutputFolder { get; init; } = "";
    }

    private sealed record CameraControlBinding(
        CameraDevice Camera,
        CameraControlItem Control,
        TextBlock ValueText,
        Slider Slider,
        CheckBox AutoCheckBox);

    private readonly record struct MeshPoint3D(double X, double Y, double Z);

    private sealed record LiveWireframeProjectedPoint(int Index, double X, double Y, double Z);

    private sealed record LiveWireframeProjection(
        IReadOnlyDictionary<int, LiveWireframeProjectedPoint> Points,
        string Mode,
        bool HeadLocked,
        double AxisOriginX,
        double AxisOriginY,
        double AxisUnitPixels);

    private sealed record TrackingFidelityOption(string Label, int MaxOutputWidth, double MaxFramesPerSecond)
    {
        public string ShortLabel
        {
            get
            {
                if (MaxOutputWidth >= 3840)
                {
                    return "4K";
                }

                if (MaxOutputWidth >= 1920)
                {
                    return "HD";
                }

                return "Safe";
            }
        }
    }

    private sealed record AvatarLearningState(bool Active, string Title, string Detail, Color Accent);

    private sealed record AvatarTrackingSanityState(string Detail, Color Accent);

    private sealed record AvatarReportSnapshot(
        string Folder,
        string SubjectId,
        string SubjectDisplayName,
        AvatarCaptureQualityAssessment CaptureQuality,
        bool SubjectConfirmed,
        bool AvatarCaptureRequested,
        bool AvatarCaptureActive,
        string AvatarCaptureStatus,
        string AvatarCaptureCorrection,
        FaceFrameGeometry FaceFrameGeometry,
        LastGoodFeatureMeshStabilityReport LastGoodFeatureMeshStability,
        IReadOnlyList<LastGoodFeatureMeshSample> LastGoodFeatureMeshSamples,
        IReadOnlyList<LastGoodFeatureThreeDdfaSnapshot> LastGoodThreeDdfaSamples,
        FaceReconstructionLaneStatus ReconstructionLane);

    private sealed record AvatarReportSaveResult(
        string LastGoodFeatureMeshJsonPath,
        string LastGoodFeatureMeshHtmlPath,
        string LastGoodThreeDdfaJsonPath,
        string LastGoodThreeDdfaHtmlPath,
        string AvatarSystemDashboardPath,
        string AvatarModelJsonPath,
        string AvatarModelHtmlPath,
        string AvatarModelObservationJsonPath);

    private sealed record AvatarStartupSelection(string ProfileId, string NewDisplayName);

    private sealed class AlertBaselineFile
    {
        public int Version { get; set; }

        public DateTime SavedAtUtc { get; set; }

        public string CameraName { get; set; } = "";

        public string CameraModeLabel { get; set; } = "";

        public FaceCueBaselineSnapshot? RegionBaseline { get; set; }

        public FaceLandmarkCueBaselineSnapshot? LandmarkBaseline { get; set; }
    }

    private sealed record BufferedVideoFrame(DateTime Timestamp, byte[] JpegBytes);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);
}
