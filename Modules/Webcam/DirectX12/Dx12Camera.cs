using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AvatarBuilder.Modules.Webcam.Common;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed class Dx12Camera : IDisposable
{
    private static readonly object ActiveLock = new();
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);
    private static Dx12Camera? _active;

    private readonly object _stateLock = new();
    private readonly CameraDevice _camera;
    private readonly CameraVideoMode _mode;
    private readonly PreviewTarget _target;
    private readonly Dispatcher _dispatcher;
    private TextureNativeCameraStream? _stream;
    private Direct3D12PreviewHost? _previewHost;
    private bool _textureFrameLeaseActive;
    private bool _denoiseEnabled;
    private double _denoiseStrength = 2d;
    private double _maxPreviewRenderFramesPerSecond;
    private DateTime _lastPreviewRenderFrameUtc = DateTime.MinValue;
    private VideoFrameColorSettings _colorSettings = VideoFrameColorSettings.Off;
    private string _recordingMode = "not recording";
    private bool _disposed;

    private Dx12Camera(CameraDevice camera, CameraVideoMode? mode, PreviewTarget target)
    {
        if (!target.PreviewWindow.Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("Dx12Camera must be constructed on the preview window's UI thread.");
        }

        _camera = camera;
        _mode = mode ?? CameraVideoMode.Auto;
        _target = target;
        _dispatcher = target.PreviewWindow.Dispatcher;
        lock (ActiveLock)
        {
            _active?.Dispose();
            _active = this;
        }

        try
        {
            Initialize();
        }
        catch
        {
            lock (ActiveLock)
            {
                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }
            }

            Dispose();
            throw;
        }
    }

    ~Dx12Camera()
    {
        Dispose(disposing: false);
    }

    public event EventHandler<TextureNativeFrameInfo>? FrameAvailable;
    public event EventHandler<TextureNativeFrameLease>? TextureFrameAvailable;
    public event EventHandler<Direct3D12PreviewDiagnostics>? DiagnosticsChanged;
    public event EventHandler<string>? StatusChanged;

    public static Dx12Camera Start(PreviewTarget target, Dx12CameraOptions? options)
    {
        var dx12Camera = OpenTextureNative(
            options?.Camera ?? CameraSourceSelection.RequireDefaultCamera(),
            options?.Mode ?? CameraVideoMode.Auto,
            target,
            options?.DenoiseEnabled == true,
            options?.DenoiseStrength ?? 2d);
        dx12Camera.ColorPolish(options?.ColorSettings ?? VideoFrameColorSettings.Off);
        dx12Camera.LimitPreviewRenderRate(options?.MaxPreviewRenderFramesPerSecond ?? 0d);
        dx12Camera.AttachStartupHandlers(options);
        return dx12Camera;
    }

    internal static Dx12Camera OpenTextureNative(
        CameraDevice camera,
        CameraVideoMode mode,
        PreviewTarget target,
        bool denoiseEnabled,
        double denoiseStrength)
    {
        if (!target.PreviewWindow.Dispatcher.CheckAccess())
        {
            return target.PreviewWindow.Dispatcher.Invoke(
                () => OpenTextureNative(camera, mode, target, denoiseEnabled, denoiseStrength));
        }

        var dx12Camera = new Dx12Camera(camera, mode, target);
        dx12Camera.Denoise(denoiseEnabled, denoiseStrength);
        return dx12Camera;
    }

    public bool IsRecording => _stream?.IsRecording == true;

    public string RecordingMode => _recordingMode;

    private void AttachStartupHandlers(Dx12CameraOptions? options)
    {
        if (options?.FrameAvailable is not null)
        {
            FrameAvailable += options.FrameAvailable;
        }

        if (options?.TextureFrameAvailable is not null)
        {
            TextureFrameAvailable += options.TextureFrameAvailable;
        }

        if (options?.DiagnosticsChanged is not null)
        {
            DiagnosticsChanged += options.DiagnosticsChanged;
        }

        if (options?.StatusChanged is not null)
        {
            StatusChanged += options.StatusChanged;
        }
    }

    public void Denoise(bool enabled, double strength)
    {
        _denoiseEnabled = enabled;
        _denoiseStrength = Math.Clamp(strength, 0.5d, 5d);
    }

    public void LimitPreviewRenderRate(double maxFramesPerSecond)
    {
        _maxPreviewRenderFramesPerSecond = maxFramesPerSecond <= 0d
            ? 0d
            : Math.Clamp(maxFramesPerSecond, 1d, 120d);
        _lastPreviewRenderFrameUtc = DateTime.MinValue;
    }

    public void UpdateTrackingOverlay(PreviewTrackingOverlay? overlay)
    {
        _previewHost?.UpdateTrackingOverlay(overlay);
    }

    public void ResumePreview()
    {
        if (_disposed)
        {
            return;
        }

        _lastPreviewRenderFrameUtc = DateTime.MinValue;
        _previewHost?.ResumeRendering();
    }

    public void ColorPolish(VideoFrameColorSettings settings)
    {
        _colorSettings = settings;
    }

    public bool WriteMP4(string path)
    {
        return WriteMP4(
            path,
            processedOutputEnabled: _denoiseEnabled,
            denoiseEnabled: _denoiseEnabled,
            denoiseStrength: _denoiseStrength);
    }

    public bool WriteMP4(
        string path,
        bool processedOutputEnabled,
        bool denoiseEnabled,
        double denoiseStrength)
    {
        return WriteMP4(
            path,
            new TextureNativeRecordingOptions(
                processedOutputEnabled,
                denoiseEnabled,
                denoiseStrength,
                _colorSettings));
    }

    public bool WriteMP4(string path, TextureNativeRecordingOptions options)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("An MP4 file path is required.", nameof(path));
        }

        return StartRecording(path, options);
    }

    public bool StartRecording(
        string path,
        bool processedOutputEnabled = false,
        bool denoiseEnabled = false,
        double denoiseStrength = 2d)
    {
        return StartRecording(
            path,
            new TextureNativeRecordingOptions(
                processedOutputEnabled,
                denoiseEnabled,
                denoiseStrength,
                _colorSettings));
    }

    public bool StartRecording(string path, TextureNativeRecordingOptions options)
    {
        var stream = _stream ?? throw new InvalidOperationException("DX12 camera stream is not initialized.");
        var started = stream.StartRecording(path, options);
        if (started)
        {
            SetPreviewRecordingMode(FormatTextureRecordingMode(options));
        }

        return started;
    }

    public void PauseRecording()
    {
        _stream?.PauseRecording();
        if (IsRecording)
        {
            _previewHost?.SetRecordingMode("recording paused");
        }
    }

    public void ResumeRecording()
    {
        _stream?.ResumeRecording();
        if (IsRecording)
        {
            _previewHost?.SetRecordingMode(_recordingMode);
        }
    }

    public TextureNativeRecordingResult? StopRecording()
    {
        var result = _stream?.StopRecording();
        SetPreviewRecordingMode("not recording");
        return result;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void Initialize()
    {
        _target.PreviewImage?.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        _target.Placeholder?.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
        _target.StatusText?.SetCurrentValue(TextBlock.TextProperty, $"DX12 camera starting for {_target.Name}.");

        var stream = new TextureNativeCameraStream(_camera, _mode, startImmediately: false);
        _stream = stream;
        stream.FrameAvailable += StreamFrameAvailable;
        stream.TextureFrameAvailable += StreamTextureFrameAvailable;
        stream.StatusChanged += StreamStatusChanged;

        stream.Start();
        ShowPreviewHost(stream.DuplicateNativeD3D12Device());
        if (!WaitForFirstFrame(stream))
        {
            throw new TimeoutException(
                $"No DX12 texture frames arrived within {FirstFrameTimeout.TotalSeconds:0.#} seconds ({stream.DeviceMode}, {stream.Width}x{stream.Height}@{stream.FramesPerSecond:0.###}, {stream.MediaSubtype}).");
        }
    }

    private static bool WaitForFirstFrame(TextureNativeCameraStream stream)
    {
        var deadline = DateTimeOffset.UtcNow + FirstFrameTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (stream.FramesRead > 0)
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return stream.FramesRead > 0;
    }

    private void ShowPreviewHost(IntPtr nativeD3D12Device)
    {
        try
        {
            var host = new Direct3D12PreviewHost(nativeD3D12Device)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            nativeD3D12Device = IntPtr.Zero;
            host.StatusChanged += PreviewHostStatusChanged;
            host.DiagnosticsChanged += PreviewHostDiagnosticsChanged;
            host.SetRecordingMode(_recordingMode);
            _previewHost = host;
            var insertIndex = Math.Min(_target.HostInsertIndex, _target.PreviewWindow.Children.Count);
            _target.PreviewWindow.Children.Insert(insertIndex, host);
            host.Visibility = Visibility.Visible;
        }
        finally
        {
            if (nativeD3D12Device != IntPtr.Zero)
            {
                System.Runtime.InteropServices.Marshal.Release(nativeD3D12Device);
            }
        }
    }

    private void HidePreviewHost()
    {
        var host = _previewHost;
        if (host is null)
        {
            return;
        }

        _previewHost = null;
        host.StatusChanged -= PreviewHostStatusChanged;
        host.DiagnosticsChanged -= PreviewHostDiagnosticsChanged;
        _target.PreviewWindow.Children.Remove(host);
        host.Dispose();
    }

    private void StreamFrameAvailable(object? sender, TextureNativeFrameInfo frame)
    {
        NotifyFrameAvailable(frame);
    }

    private void StreamTextureFrameAvailable(object? sender, TextureNativeFrameLease frame)
    {
        _textureFrameLeaseActive = frame.IsValid;
        NotifyTextureFrameAvailable(frame);

        if (!ShouldAcceptPreviewRenderFrame())
        {
            return;
        }

        try
        {
            _previewHost?.RenderTextureFrame(frame, _denoiseEnabled, _denoiseStrength, _colorSettings);
        }
        catch (Exception ex)
        {
            NotifyStatusChanged($"DX12 preview submission failed: {ex.Message}");
        }
    }

    private bool ShouldAcceptPreviewRenderFrame()
    {
        var maxFramesPerSecond = _maxPreviewRenderFramesPerSecond;
        if (maxFramesPerSecond <= 0d)
        {
            return true;
        }

        var now = DateTime.UtcNow;
        if (now - _lastPreviewRenderFrameUtc < TimeSpan.FromSeconds(1d / maxFramesPerSecond))
        {
            return false;
        }

        _lastPreviewRenderFrameUtc = now;
        return true;
    }

    private void StreamStatusChanged(object? sender, string status)
    {
        NotifyStatusChanged(status);
    }

    private void PreviewHostStatusChanged(object? sender, string status)
    {
        NotifyStatusChanged(status);
    }

    private void PreviewHostDiagnosticsChanged(object? sender, Direct3D12PreviewDiagnostics diagnostics)
    {
        var handlers = DiagnosticsChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (var callback in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<Direct3D12PreviewDiagnostics>)callback)(this, diagnostics);
            }
            catch (Exception ex)
            {
                NotifyStatusChanged($"DX12 diagnostics observer failed: {ex.Message}");
            }
        }
    }

    private void NotifyFrameAvailable(TextureNativeFrameInfo frame)
    {
        var handlers = FrameAvailable;
        if (handlers is null)
        {
            return;
        }

        foreach (var callback in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<TextureNativeFrameInfo>)callback)(this, frame);
            }
            catch (Exception ex)
            {
                NotifyStatusChanged($"DX12 frame observer failed: {ex.Message}");
            }
        }
    }

    private void NotifyTextureFrameAvailable(TextureNativeFrameLease frame)
    {
        var handlers = TextureFrameAvailable;
        if (handlers is null)
        {
            return;
        }

        foreach (var callback in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<TextureNativeFrameLease>)callback)(this, frame);
            }
            catch (Exception ex)
            {
                NotifyStatusChanged($"DX12 texture observer failed: {ex.Message}");
            }
        }
    }

    private void NotifyStatusChanged(string status)
    {
        var handlers = StatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (var callback in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<string>)callback)(this, status);
            }
            catch
            {
                // Status observers cannot be allowed to stop the camera or render workers.
            }
        }
    }

    private void SetPreviewRecordingMode(string recordingMode)
    {
        _recordingMode = string.IsNullOrWhiteSpace(recordingMode) ? "not recording" : recordingMode;
        _previewHost?.SetRecordingMode(_recordingMode);
    }

    private static string FormatTextureRecordingMode(TextureNativeRecordingOptions options)
    {
        if (options.ProcessedOutputEnabled)
        {
            return options.ColorSettings.HasVisibleAdjustments || options.DenoiseEnabled
                ? "recording processed texture output"
                : "recording processed texture bridge";
        }

        return "recording raw texture-native samples";
    }

    private void Dispose(bool disposing)
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        var stream = _stream;
        _stream = null;
        if (stream is not null)
        {
            stream.FrameAvailable -= StreamFrameAvailable;
            stream.TextureFrameAvailable -= StreamTextureFrameAvailable;
            stream.StatusChanged -= StreamStatusChanged;
        }

        try
        {
            stream?.Dispose();
        }
        catch
        {
        }

        _textureFrameLeaseActive = false;

        if (disposing && _dispatcher.CheckAccess())
        {
            HidePreviewHost();
        }
        else if (disposing)
        {
            try
            {
                _dispatcher.Invoke(HidePreviewHost);
            }
            catch
            {
            }
        }

        if (disposing)
        {
            lock (ActiveLock)
            {
                if (ReferenceEquals(_active, this))
                {
                    _active = null;
                }
            }
        }
    }

    public sealed class PreviewTarget
    {
        public PreviewTarget(
            Panel previewWindow,
            UIElement? previewImage = null,
            UIElement? placeholder = null,
            TextBlock? statusText = null,
            int hostInsertIndex = 0,
            string name = "Camera")
        {
            PreviewWindow = previewWindow;
            PreviewImage = previewImage;
            Placeholder = placeholder;
            StatusText = statusText;
            HostInsertIndex = Math.Max(0, hostInsertIndex);
            Name = name;
        }

        public Panel PreviewWindow { get; }

        public UIElement? PreviewImage { get; }

        public UIElement? Placeholder { get; }

        public TextBlock? StatusText { get; }

        public int HostInsertIndex { get; }

        public string Name { get; }
    }
}
