using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private readonly Dx12CameraKind _kind;
    private readonly Action? _fallbackStop;
    private readonly string _fallbackDescription;
    private TextureNativeCameraStream? _stream;
    private Direct3D12PreviewHost? _previewHost;
    private TextureNativeFrameLease? _pendingPreviewFrame;
    private WriteableBitmap? _fallbackPreviewBitmap;
    private int _previewRenderQueued;
    private long _fallbackFrameNumber;
    private bool _textureFrameLeaseActive;
    private bool _denoiseEnabled;
    private double _denoiseStrength = 2d;
    private double _maxPreviewRenderFramesPerSecond;
    private DateTime _lastPreviewRenderFrameUtc = DateTime.MinValue;
    private VideoFrameColorSettings _colorSettings = VideoFrameColorSettings.Off;
    private string _recordingMode = "not recording";
    private bool _disposed;

    // Drop-in path:
    // var camera = Dx12Camera.Start(previewPanel, new Dx12CameraOptions { DenoiseEnabled = true });
    // camera.WriteMP4(path); camera.StopMP4(); camera.Close();
    public Dx12Camera(Panel previewWindow)
        : this(CameraSourceSelection.RequireDefaultCamera(), CameraVideoMode.Auto, new PreviewTarget(previewWindow))
    {
    }

    public Dx12Camera(Panel previewWindow, Dx12CameraOptions? options)
        : this(
            options?.Camera ?? CameraSourceSelection.RequireDefaultCamera(),
            options?.Mode ?? CameraVideoMode.Auto,
            new PreviewTarget(previewWindow))
    {
        ApplyStartupOptions(options);
    }

    public Dx12Camera(Panel previewWindow, CameraVideoMode? mode)
        : this(CameraSourceSelection.RequireDefaultCamera(), mode, new PreviewTarget(previewWindow))
    {
    }

    public Dx12Camera(CameraDevice camera, Panel previewWindow)
        : this(camera, CameraVideoMode.Auto, new PreviewTarget(previewWindow))
    {
    }

    public Dx12Camera(CameraDevice camera, Panel previewWindow, Dx12CameraOptions? options)
        : this(camera, options?.Mode ?? CameraVideoMode.Auto, new PreviewTarget(previewWindow))
    {
        ApplyStartupOptions(options);
    }

    public Dx12Camera(CameraDevice camera, CameraVideoMode? mode, Panel previewWindow)
        : this(camera, mode, new PreviewTarget(previewWindow))
    {
    }

    public Dx12Camera(CameraDevice camera, CameraVideoMode? mode, Panel previewWindow, Dx12CameraOptions? options)
        : this(camera, mode ?? options?.Mode ?? CameraVideoMode.Auto, new PreviewTarget(previewWindow))
    {
        ApplyStartupOptions(options);
    }

    public Dx12Camera(CameraDevice camera, PreviewTarget target)
        : this(camera, CameraVideoMode.Auto, target)
    {
    }

    public Dx12Camera(CameraDevice camera, CameraVideoMode? mode, PreviewTarget target)
    {
        if (!target.PreviewWindow.Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException("Dx12Camera must be constructed on the preview window's UI thread.");
        }

        _camera = camera;
        _mode = mode ?? CameraVideoMode.Auto;
        _target = target;
        _dispatcher = target.PreviewWindow.Dispatcher;
        _kind = Dx12CameraKind.TextureNative;
        _fallbackDescription = string.Empty;
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

    private Dx12Camera(
        CameraDevice camera,
        CameraVideoMode mode,
        PreviewTarget target,
        Dx12CameraKind kind,
        string fallbackDescription,
        Action fallbackStop)
    {
        _camera = camera;
        _mode = mode;
        _target = target;
        _dispatcher = target.PreviewWindow.Dispatcher;
        _kind = kind;
        _fallbackDescription = fallbackDescription;
        _fallbackStop = fallbackStop;
        InitializeFallbackPreview();
    }

    ~Dx12Camera()
    {
        Dispose(disposing: false);
    }

    public event EventHandler<TextureNativeFrameInfo>? FrameAvailable;
    public event EventHandler<TextureNativeFrameLease>? TextureFrameAvailable;
    public event EventHandler<Direct3D12PreviewDiagnostics>? DiagnosticsChanged;
    public event EventHandler<string>? StatusChanged;

    public static IReadOnlyList<CameraDevice> GetCameras()
    {
        return CameraSourceSelection.GetCameras();
    }

    public static CameraDevice? GetDefaultCamera()
    {
        return CameraSourceSelection.GetDefaultCamera();
    }

    public static CameraDevice RequireDefaultCamera()
    {
        return CameraSourceSelection.RequireDefaultCamera();
    }

    public static CameraDevice? FindCamera(
        IReadOnlyList<CameraDevice> cameras,
        string? devicePath,
        string? source,
        string? name)
    {
        return CameraSourceSelection.FindCamera(cameras, devicePath, source, name);
    }

    public static bool IsDirectShowCamera(CameraDevice camera)
    {
        return CameraSourceSelection.IsDirectShowCamera(camera);
    }

    internal static Dx12Camera GetOrCreate(CameraDevice camera, CameraVideoMode mode, PreviewTarget target)
    {
        if (!target.PreviewWindow.Dispatcher.CheckAccess())
        {
            return target.PreviewWindow.Dispatcher.Invoke(() => GetOrCreate(camera, mode, target));
        }

        lock (ActiveLock)
        {
            if (_active is not null
                && !_active._disposed
                && _active._kind == Dx12CameraKind.TextureNative
                && _active.IsInitializedFor(target.PreviewWindow)
                && _active.Matches(camera, mode))
            {
                return _active;
            }

            _active?.Dispose();
            _active = new Dx12Camera(camera, mode, target);
            return _active;
        }
    }

    public static Dx12Camera StartDefault(Panel previewWindow)
    {
        return StartDefault(new PreviewTarget(previewWindow));
    }

    public static Dx12Camera StartDefault(Panel previewWindow, CameraVideoMode? mode)
    {
        return StartDefault(new PreviewTarget(previewWindow), mode);
    }

    public static Dx12Camera StartDefault(
        PreviewTarget target,
        CameraVideoMode? mode = null,
        bool denoiseEnabled = false,
        double denoiseStrength = 2d)
    {
        return OpenTextureNative(
            CameraSourceSelection.RequireDefaultCamera(),
            mode ?? CameraVideoMode.Auto,
            target,
            denoiseEnabled,
            denoiseStrength);
    }

    public static Dx12Camera Start(Panel previewWindow)
    {
        return StartDefault(previewWindow);
    }

    public static Dx12Camera Start(Panel previewWindow, Dx12CameraOptions? options)
    {
        return Start(new PreviewTarget(previewWindow), options);
    }

    public static Dx12Camera Start(PreviewTarget target, Dx12CameraOptions? options)
    {
        var dx12Camera = OpenTextureNative(
            options?.Camera ?? CameraSourceSelection.RequireDefaultCamera(),
            options?.Mode ?? CameraVideoMode.Auto,
            target,
            options?.DenoiseEnabled == true,
            options?.DenoiseStrength ?? 2d);
        dx12Camera.ColorPolish(options?.ColorSettings ?? VideoFrameColorSettings.Off);
        dx12Camera.AttachStartupHandlers(options);
        return dx12Camera;
    }

    public static Dx12Camera Start(CameraDevice camera, Panel previewWindow)
    {
        return OpenTextureNative(
            camera,
            CameraVideoMode.Auto,
            new PreviewTarget(previewWindow),
            denoiseEnabled: false,
            denoiseStrength: 2d);
    }

    public static Dx12Camera Start(
        CameraDevice camera,
        CameraVideoMode? mode,
        Panel previewWindow,
        Dx12CameraOptions? options = null)
    {
        var dx12Camera = OpenTextureNative(
            camera,
            mode ?? options?.Mode ?? CameraVideoMode.Auto,
            new PreviewTarget(previewWindow),
            options?.DenoiseEnabled == true,
            options?.DenoiseStrength ?? 2d);
        dx12Camera.ColorPolish(options?.ColorSettings ?? VideoFrameColorSettings.Off);
        dx12Camera.AttachStartupHandlers(options);
        return dx12Camera;
    }

    internal static Dx12Camera ClaimFallback(
        CameraDevice camera,
        CameraVideoMode mode,
        PreviewTarget target,
        string fallbackDescription,
        Action fallbackStop)
    {
        if (!target.PreviewWindow.Dispatcher.CheckAccess())
        {
            return target.PreviewWindow.Dispatcher.Invoke(() => ClaimFallback(camera, mode, target, fallbackDescription, fallbackStop));
        }

        var kind = fallbackDescription.Contains("DirectShow", StringComparison.OrdinalIgnoreCase)
            ? Dx12CameraKind.DirectShowFallback
            : Dx12CameraKind.MediaFoundationFallback;

        lock (ActiveLock)
        {
            if (_active is not null
                && !_active._disposed
                && _active._kind == kind
                && _active.IsInitializedFor(target.PreviewWindow)
                && _active.Matches(camera, mode))
            {
                return _active;
            }

            _active?.Dispose();
            _active = new Dx12Camera(camera, mode, target, kind, fallbackDescription, fallbackStop);
            return _active;
        }
    }

    internal static Dx12Camera OpenFallback(
        CameraDevice camera,
        CameraVideoMode mode,
        PreviewTarget target,
        string fallbackDescription,
        Action fallbackStop)
    {
        return ClaimFallback(camera, mode, target, fallbackDescription, fallbackStop);
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

    public static void DestroyActive(bool collectGarbage = false)
    {
        lock (ActiveLock)
        {
            _active?.Dispose();
            _active = null;
        }

        if (collectGarbage)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    public static void CloseActive(bool collectGarbage = true)
    {
        DestroyActive(collectGarbage);
    }

    internal static TextureNativeRecordingResult? CloseActiveCamera(
        Dx12Camera? camera,
        Action<Dx12Camera>? detach = null,
        bool collectGarbage = true)
    {
        if (camera is null)
        {
            return null;
        }

        detach?.Invoke(camera);

        TextureNativeRecordingResult? result = null;
        try
        {
            result = camera.StopRecordingIfActive();
        }
        finally
        {
            camera.Close(collectGarbage);
        }

        return result;
    }

    public bool IsInitializedFor(Panel previewWindow)
    {
        return ReferenceEquals(_target.PreviewWindow, previewWindow);
    }

    public CameraDevice Camera => _camera;

    public CameraVideoMode Mode => _mode;

    public string Name => _camera.Name;

    public bool IsRecording => _stream?.IsRecording == true;

    public string RecordingMode => _recordingMode;

    public bool IsTextureNative => _kind == Dx12CameraKind.TextureNative;

    public bool IsFallback => _kind is Dx12CameraKind.MediaFoundationFallback or Dx12CameraKind.DirectShowFallback;

    public PipelineKind Pipeline => _kind switch
    {
        Dx12CameraKind.TextureNative => PipelineKind.TextureNative,
        Dx12CameraKind.DirectShowFallback => PipelineKind.DirectShowFallback,
        _ => PipelineKind.MediaFoundationFallback
    };

    public int Width => _stream?.Width ?? 0;

    public int Height => _stream?.Height ?? 0;

    public double FramesPerSecond => _stream?.FramesPerSecond ?? 0d;

    public string DeviceMode => _stream?.DeviceMode ?? "none";

    public string MediaSubtype => _stream?.MediaSubtype ?? "none";

    public int SamplesWritten => _stream?.SamplesWritten ?? 0;

    public bool IsReady => _previewHost?.IsReady == true;

    public Direct3D12PreviewDiagnostics PreviewDiagnostics => _previewHost?.Diagnostics ?? Direct3D12PreviewDiagnostics.Empty;

    public VideoFrameColorSettings ColorSettings => _colorSettings;

    internal static void CollectReleasedCamera()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    public bool TextureFrameLeaseActive => _textureFrameLeaseActive;

    public string PreviewPathDescription => IsTextureNative
        ? _previewHost?.PreviewPathDescription ?? "DX12 preview path pending"
        : _fallbackDescription;

    internal static void AttachToSlot(
        ref Dx12Camera? slot,
        Dx12Camera camera,
        EventHandler<TextureNativeFrameInfo> frameAvailable,
        EventHandler<string> statusChanged)
    {
        if (ReferenceEquals(slot, camera))
        {
            return;
        }

        DetachFromSlot(ref slot, frameAvailable, statusChanged);
        slot = camera;
        camera.AttachPreviewHandlers(frameAvailable, statusChanged);
    }

    internal static bool TryOpenTextureNativeIntoSlot(
        ref Dx12Camera? slot,
        CameraDevice camera,
        CameraVideoMode mode,
        PreviewTarget target,
        bool denoiseEnabled,
        double denoiseStrength,
        EventHandler<TextureNativeFrameInfo> frameAvailable,
        EventHandler<string> statusChanged)
    {
        if (slot is not null)
        {
            return false;
        }

        var dx12Camera = OpenTextureNative(camera, mode, target, denoiseEnabled, denoiseStrength);
        AttachToSlot(ref slot, dx12Camera, frameAvailable, statusChanged);
        return true;
    }

    internal static bool TryOpenFallbackIntoSlot(
        ref Dx12Camera? slot,
        CameraDevice camera,
        CameraVideoMode mode,
        PreviewTarget target,
        string fallbackDescription,
        Action fallbackStop,
        EventHandler<TextureNativeFrameInfo> frameAvailable,
        EventHandler<string> statusChanged)
    {
        if (slot is not null)
        {
            return false;
        }

        var owner = OpenFallback(camera, mode, target, fallbackDescription, fallbackStop);
        AttachToSlot(ref slot, owner, frameAvailable, statusChanged);
        return true;
    }

    internal static void DetachFromSlot(
        ref Dx12Camera? slot,
        EventHandler<TextureNativeFrameInfo> frameAvailable,
        EventHandler<string> statusChanged)
    {
        var camera = slot;
        if (camera is null)
        {
            return;
        }

        camera.DetachPreviewHandlers(frameAvailable, statusChanged);
        slot = null;
    }

    internal static TextureNativeRecordingResult? CloseSlot(
        ref Dx12Camera? slot,
        EventHandler<TextureNativeFrameInfo> frameAvailable,
        EventHandler<string> statusChanged,
        bool collectGarbage = true)
    {
        var camera = slot;
        if (camera is null)
        {
            return null;
        }

        DetachFromSlot(ref slot, frameAvailable, statusChanged);

        TextureNativeRecordingResult? result = null;
        try
        {
            result = camera.StopRecordingIfActive();
        }
        finally
        {
            camera.Close(collectGarbage);
        }

        return result;
    }

    internal void AttachPreviewHandlers(
        EventHandler<TextureNativeFrameInfo> frameAvailable,
        EventHandler<string> statusChanged)
    {
        FrameAvailable += frameAvailable;
        StatusChanged += statusChanged;
    }

    internal void DetachPreviewHandlers(
        EventHandler<TextureNativeFrameInfo> frameAvailable,
        EventHandler<string> statusChanged)
    {
        FrameAvailable -= frameAvailable;
        StatusChanged -= statusChanged;
    }

    private void ApplyStartupOptions(Dx12CameraOptions? options)
    {
        if (options is null)
        {
            return;
        }

        Denoise(options.DenoiseEnabled, options.DenoiseStrength);
        LimitPreviewRenderRate(options.MaxPreviewRenderFramesPerSecond);
        ColorPolish(options.ColorSettings);
        AttachStartupHandlers(options);
    }

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

    public void Denoise(int magnitude)
    {
        Denoise(magnitude > 0, magnitude);
    }

    public void Denoise(double strength)
    {
        Denoise(strength > 0d, strength);
    }

    public void Denoise(bool enabled, double strength)
    {
        _denoiseEnabled = enabled;
        _denoiseStrength = Math.Clamp(strength, 0.5d, 5d);
    }

    public void UpdateRenderSettings(bool denoiseEnabled, double denoiseStrength)
    {
        Denoise(denoiseEnabled, denoiseStrength);
    }

    public void LimitPreviewRenderRate(double maxFramesPerSecond)
    {
        _maxPreviewRenderFramesPerSecond = maxFramesPerSecond <= 0d
            ? 0d
            : Math.Clamp(maxFramesPerSecond, 1d, 120d);
        _lastPreviewRenderFrameUtc = DateTime.MinValue;
    }

    public void UpdateRenderSettings(
        bool denoiseEnabled,
        double denoiseStrength,
        VideoFrameColorSettings colorSettings)
    {
        Denoise(denoiseEnabled, denoiseStrength);
        ColorPolish(colorSettings);
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

    public void PauseMP4()
    {
        PauseRecording();
    }

    public void ResumeRecording()
    {
        _stream?.ResumeRecording();
        if (IsRecording)
        {
            _previewHost?.SetRecordingMode(_recordingMode);
        }
    }

    public void ResumeMP4()
    {
        ResumeRecording();
    }

    public TextureNativeRecordingResult? StopRecording()
    {
        var result = _stream?.StopRecording();
        SetPreviewRecordingMode("not recording");
        return result;
    }

    public TextureNativeRecordingResult? StopRecordingIfActive()
    {
        return IsRecording ? StopRecording() : null;
    }

    public TextureNativeRecordingResult? StopMP4()
    {
        return StopRecording();
    }

    public void Close(bool collectGarbage = true)
    {
        Dispose();
        if (collectGarbage)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    public void Stop(bool collectGarbage = true)
    {
        Close(collectGarbage);
    }

    internal void RenderProofFrame(TextureNativeFrameInfo frame)
    {
        _previewHost?.RenderProofFrame(frame);
    }

    internal void RenderFallbackFrame(
        CameraFrame frame,
        VideoFrameColorSettings colorSettings = default,
        bool denoiseEnabled = false,
        double denoiseStrength = 0d)
    {
        if (_disposed || !IsFallback)
        {
            return;
        }

        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => RenderFallbackFrame(frame, colorSettings, denoiseEnabled, denoiseStrength), DispatcherPriority.Background);
            return;
        }

        _target.Placeholder?.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        if (_previewHost?.IsReady == true)
        {
            _previewHost.RenderBgraFrame(
                frame,
                Interlocked.Increment(ref _fallbackFrameNumber),
                colorSettings,
                denoiseEnabled,
                denoiseStrength);
            _target.PreviewImage?.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            return;
        }

        if (_target.PreviewImage is not Image image)
        {
            return;
        }

        var bgraBytes = frame.BgraBytes;
        var stride = frame.Stride;
        if (!frame.HasBgra)
        {
            if (!frame.HasNv12 || frame.Nv12Bytes is not { Length: > 0 } nv12Bytes)
            {
                return;
            }

            var converted = Nv12FrameConverter.ConvertToBgra(
                nv12Bytes,
                frame.Nv12Stride,
                frame.Width,
                frame.Height,
                out var convertedStride);
            if (converted is not { Length: > 0 } || convertedStride <= 0)
            {
                return;
            }

            bgraBytes = converted;
            stride = convertedStride;
        }

        if (_fallbackPreviewBitmap is null
            || _fallbackPreviewBitmap.PixelWidth != frame.Width
            || _fallbackPreviewBitmap.PixelHeight != frame.Height)
        {
            _fallbackPreviewBitmap = new WriteableBitmap(
                frame.Width,
                frame.Height,
                96,
                96,
                System.Windows.Media.PixelFormats.Bgra32,
                null);
            image.Source = _fallbackPreviewBitmap;
        }

        _fallbackPreviewBitmap.WritePixels(
            new Int32Rect(0, 0, frame.Width, frame.Height),
            bgraBytes,
            stride,
            0);
        image.Visibility = Visibility.Visible;
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

    private void InitializeFallbackPreview()
    {
        _target.PreviewImage?.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        _target.Placeholder?.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
        _target.StatusText?.SetCurrentValue(TextBlock.TextProperty, $"{_fallbackDescription} starting for {_target.Name}.");
        ShowPreviewHost(IntPtr.Zero);
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

    private bool Matches(CameraDevice camera, CameraVideoMode mode)
    {
        return string.Equals(_camera.Name, camera.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_camera.DevicePath, camera.DevicePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_camera.Source, camera.Source, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_mode.Label, mode.Label, StringComparison.Ordinal)
            && _mode.Width == mode.Width
            && _mode.Height == mode.Height
            && Nullable.Equals(_mode.FramesPerSecond, mode.FramesPerSecond)
            && string.Equals(_mode.InputFormat, mode.InputFormat, StringComparison.OrdinalIgnoreCase)
            && _mode.IsAuto == mode.IsAuto;
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
        FrameAvailable?.Invoke(this, frame);
    }

    private void StreamTextureFrameAvailable(object? sender, TextureNativeFrameLease frame)
    {
        _textureFrameLeaseActive = frame.IsValid;
        TextureFrameAvailable?.Invoke(this, frame);

        if (!ShouldAcceptPreviewRenderFrame())
        {
            return;
        }

        var pendingFrame = frame.Duplicate();
        if (pendingFrame is null)
        {
            return;
        }

        Interlocked.Exchange(ref _pendingPreviewFrame, pendingFrame)?.Dispose();
        if (Interlocked.Exchange(ref _previewRenderQueued, 1) != 0)
        {
            return;
        }

        _dispatcher.BeginInvoke((Action)ProcessPendingPreviewFrame, DispatcherPriority.Background);
    }

    private void ProcessPendingPreviewFrame()
    {
        var frame = Interlocked.Exchange(ref _pendingPreviewFrame, null);
        if (frame is not null)
        {
            try
            {
                if (!_disposed)
                {
                    _previewHost?.RenderTextureFrame(frame, _denoiseEnabled, _denoiseStrength, _colorSettings);
                }
            }
            finally
            {
                frame.Dispose();
            }
        }

        Volatile.Write(ref _previewRenderQueued, 0);
        if (Volatile.Read(ref _pendingPreviewFrame) is not null
            && Interlocked.Exchange(ref _previewRenderQueued, 1) == 0)
        {
            _dispatcher.BeginInvoke((Action)ProcessPendingPreviewFrame, DispatcherPriority.Background);
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
        StatusChanged?.Invoke(this, status);
    }

    private void PreviewHostStatusChanged(object? sender, string status)
    {
        StatusChanged?.Invoke(this, status);
    }

    private void PreviewHostDiagnosticsChanged(object? sender, Direct3D12PreviewDiagnostics diagnostics)
    {
        DiagnosticsChanged?.Invoke(this, diagnostics);
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

        if (disposing && _fallbackStop is not null)
        {
            try
            {
                _fallbackStop();
            }
            catch
            {
            }
        }

        Interlocked.Exchange(ref _pendingPreviewFrame, null)?.Dispose();
        _textureFrameLeaseActive = false;
        Volatile.Write(ref _previewRenderQueued, 0);

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

    private enum Dx12CameraKind
    {
        TextureNative,
        MediaFoundationFallback,
        DirectShowFallback
    }

    public enum PipelineKind
    {
        TextureNative,
        MediaFoundationFallback,
        DirectShowFallback
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
