using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EpisodeMonitor.Video;
using Microsoft.Win32;

namespace EpisodeMonitor;

public partial class MainWindow : Window
{
    private readonly FfmpegCameraModeService _cameraModeService = new();
    private readonly FfmpegCameraPreviewService _previewService = new();
    private readonly ObservableCollection<EpisodeMonitorEvent> _events = [];
    private readonly string _defaultOutputFolder = Path.Combine(AppContext.BaseDirectory, "EpisodeMonitorSessions");
    private readonly object _frameLock = new();

    private IReadOnlyList<CameraDevice> _cameras = [];
    private CancellationTokenSource? _modeLoadCancellation;
    private string _outputFolder;
    private byte[]? _previousSample;
    private DateTime? _lowMotionStartedAt;
    private DateTime? _activeEpisodeStartedAt;
    private string _episodeStartSnapshot = "";
    private double _episodeMotionSum;
    private int _episodeMotionSamples;
    private bool _isCameraEnabled;
    private bool _isUpdatingCameraToggle;

    public MainWindow()
    {
        InitializeComponent();
        _outputFolder = _defaultOutputFolder;
        _previewService.FrameAvailable += PreviewFrameAvailable;
        _previewService.StatusChanged += PreviewStatusChanged;
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
        EventGrid.ItemsSource = _events;
        UpdateOutputFolderText();
        UpdateSettingLabels();
        RefreshCameras();
    }

    private void WindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _modeLoadCancellation?.Cancel();
        _modeLoadCancellation?.Dispose();
        _previewService.Dispose();
    }

    private void RefreshCamerasClicked(object sender, RoutedEventArgs e)
    {
        RefreshCameras();
    }

    private void RefreshCameras()
    {
        _cameras = DirectShowCameraEnumerator.GetVideoInputDevices();
        CameraComboBox.ItemsSource = _cameras;
        CameraComboBox.DisplayMemberPath = nameof(CameraDevice.Name);

        if (_cameras.Count > 0)
        {
            CameraComboBox.SelectedIndex = 0;
            SetStatus($"Found {_cameras.Count} camera{(_cameras.Count == 1 ? "" : "s")}.");
        }
        else
        {
            CameraModeComboBox.ItemsSource = new[] { CameraVideoMode.Auto };
            CameraModeComboBox.SelectedIndex = 0;
            SetStatus("No cameras found.");
            SetPreviewState("No camera source found", null);
        }
    }

    private async void CameraSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CameraComboBox.SelectedItem is not CameraDevice camera)
        {
            return;
        }

        await LoadCameraModesAsync(camera);
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
            var modes = await _cameraModeService.GetModesAsync(camera.Name, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            CameraModeComboBox.ItemsSource = modes;
            CameraModeComboBox.SelectedIndex = 0;
            SetStatus($"Loaded {modes.Count} mode{(modes.Count == 1 ? "" : "s")} for {camera.Name}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetStatus($"Could not load camera modes: {ex.Message}");
        }
    }

    private void CameraModeSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isCameraEnabled)
        {
            RestartPreview();
        }
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

    private void StartPreview()
    {
        if (CameraComboBox.SelectedItem is not CameraDevice camera)
        {
            SetCameraToggle(false);
            SetStatus("Choose a camera first.");
            return;
        }

        var mode = CameraModeComboBox.SelectedItem as CameraVideoMode ?? CameraVideoMode.Auto;
        _previousSample = null;
        _isCameraEnabled = _previewService.Start(camera.Name, mode);
        SetCameraToggle(_isCameraEnabled);

        if (_isCameraEnabled)
        {
            SetPreviewState($"Starting {camera.Name} ({mode.Label})", null);
        }
        else
        {
            SetPreviewState("Camera failed to start", null);
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
        _previewService.Stop();
        _isCameraEnabled = false;
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
        Dispatcher.InvokeAsync(() =>
        {
            SetPreviewState("Camera active", frame);
            ProcessFrame(frame);
        });
    }

    private void PreviewStatusChanged(object? sender, string status)
    {
        Dispatcher.InvokeAsync(() => SetStatus(status));
    }

    private void SetPreviewState(string status, ImageSource? frame)
    {
        PreviewStateText.Text = status;
        if (frame is null)
        {
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        PreviewImage.Source = frame;
        PreviewImage.Visibility = Visibility.Visible;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void WatchEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (WatchEnabledCheckBox.IsChecked == true)
        {
            Directory.CreateDirectory(_outputFolder);
            ResetEpisodeState();
            AddEpisodeEvent(DateTime.Now, null, "Episode watch started", "", "", "");
            MonitorStatusText.Text = "Episode monitor watching for sustained low motion.";
        }
        else
        {
            EndActiveEpisode(DateTime.Now, null, "Monitoring stopped");
            ResetEpisodeState();
            AddEpisodeEvent(DateTime.Now, null, "Episode watch stopped", "", "", "");
            MonitorStatusText.Text = "Episode monitor idle.";
        }
    }

    private void SettingChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
        {
            return;
        }

        UpdateSettingLabels();
    }

    private void ProcessFrame(BitmapSource bitmap)
    {
        if (WatchEnabledCheckBox.IsChecked != true)
        {
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
            return;
        }

        var now = DateTime.Now;
        var motion = CalculateFrameMotionPercent(_previousSample, sample);
        _previousSample = sample;
        ProcessEpisodeMotion(bitmap, now, motion);
    }

    private void ProcessEpisodeMotion(BitmapSource bitmap, DateTime now, double motion)
    {
        var threshold = GetMotionThreshold();
        var stillnessSeconds = GetStillnessSeconds();

        if (motion <= threshold)
        {
            _lowMotionStartedAt ??= now;
            _episodeMotionSum += motion;
            _episodeMotionSamples++;

            var lowMotionFor = now - _lowMotionStartedAt.Value;
            if (_activeEpisodeStartedAt is null && lowMotionFor.TotalSeconds >= stillnessSeconds)
            {
                _activeEpisodeStartedAt = _lowMotionStartedAt;
                _episodeStartSnapshot = SnapshotCheckBox.IsChecked == true
                    ? SaveSnapshot(bitmap, _activeEpisodeStartedAt.Value, "start")
                    : "";
                AddEpisodeEvent(_activeEpisodeStartedAt.Value, null, "Possible sleep onset", GetAverageMotionLabel(), _episodeStartSnapshot, "");
            }

            MonitorStatusText.Text = _activeEpisodeStartedAt is null
                ? $"Very still: {lowMotionFor.TotalSeconds:0}s of {stillnessSeconds:0}s needed. Motion: {motion:0.0}%"
                : $"Possible episode active. Duration: {(now - _activeEpisodeStartedAt.Value).TotalMinutes:0.0} min. Motion: {motion:0.0}%";
            return;
        }

        EndActiveEpisode(now, bitmap, "Motion returned");
        ResetEpisodeState();
        MonitorStatusText.Text = $"Awake/moving baseline. Motion: {motion:0.0}%";
    }

    private static byte[] CreateFrameSample(BitmapSource bitmap)
    {
        var scale = Math.Min(1d, 96d / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
        var width = Math.Max(1, (int)(bitmap.PixelWidth * scale));
        var height = Math.Max(1, (int)(bitmap.PixelHeight * scale));
        var scaled = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        var converted = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);
        var stride = width;
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

    private string SaveSnapshot(BitmapSource bitmap, DateTime timestamp, string kind)
    {
        try
        {
            var folder = Path.Combine(_outputFolder, $"EpisodeMonitor_{timestamp:yyyy-MM-dd}");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"episode_{kind}_{timestamp:HH-mm-ss-fff}.jpg");
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
            ? SaveSnapshot(bitmap, endedAt, "end")
            : "";
        var files = string.Join(" | ", new[] { _episodeStartSnapshot, endSnapshot }.Where(static path => !string.IsNullOrWhiteSpace(path)));
        AddEpisodeEvent(_activeEpisodeStartedAt.Value, endedAt, reason, GetAverageMotionLabel(), files, "");
    }

    private void ResetEpisodeState()
    {
        _lowMotionStartedAt = null;
        _activeEpisodeStartedAt = null;
        _episodeStartSnapshot = "";
        _episodeMotionSum = 0d;
        _episodeMotionSamples = 0;
        _previousSample = null;
    }

    private void MarkNowClicked(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        AddEpisodeEvent(now, null, "Manual marker", "", "", "User marked an event");
        MonitorStatusText.Text = $"Manual marker added at {now:g}.";
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

    private void ClearClicked(object sender, RoutedEventArgs e)
    {
        _events.Clear();
        ResetEpisodeState();
        MonitorStatusText.Text = "Episode log cleared.";
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
        UpdateOutputFolderText();
    }

    private void CloseClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AddEpisodeEvent(DateTime startedAt, DateTime? endedAt, string eventName, string averageMotion, string file, string notes)
    {
        _events.Insert(0, new EpisodeMonitorEvent
        {
            StartedAt = startedAt,
            EndedAt = endedAt,
            Event = eventName,
            AvgMotion = averageMotion,
            File = file,
            Notes = notes
        });
    }

    private double GetMotionThreshold()
    {
        return Math.Clamp(ThresholdSlider.Value, 0.2d, 8d);
    }

    private double GetStillnessSeconds()
    {
        return Math.Clamp(StillnessSlider.Value, 15d, 900d);
    }

    private string GetAverageMotionLabel()
    {
        return _episodeMotionSamples <= 0 ? "" : $"{_episodeMotionSum / _episodeMotionSamples:0.0}%";
    }

    private void UpdateSettingLabels()
    {
        ThresholdValueText.Text = $"{GetMotionThreshold():0.0}%";
        StillnessValueText.Text = $"{GetStillnessSeconds():0}s";
    }

    private void UpdateOutputFolderText()
    {
        OutputFolderText.Text = _outputFolder;
    }

    private void SetStatus(string status)
    {
        TopStatusText.Text = status;
        FooterText.Text = status;
    }

    private static string Csv(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);
}
