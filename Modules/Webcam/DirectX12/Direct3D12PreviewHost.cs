using System.Runtime.InteropServices;
using System.Threading;
using AvatarBuilder.Modules.Webcam.Common;
using SharpGen.Runtime;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed class Direct3D12PreviewHost : WebcamDirectX12ViewportHost, ICameraPreviewPresenter
{
    private static readonly TimeSpan RendererDisposeLockTimeout = TimeSpan.FromMilliseconds(250);

    private IntPtr _nativeD3D12Device;
    private readonly object _rendererLock = new();
    private readonly object _renderWorkerLock = new();
    private readonly object _renderThrottleLock = new();
    private readonly AutoResetEvent _renderFrameReady = new(false);
    private Direct3D12SwapChainRenderer? _renderer;
    private Thread? _renderThread;
    private QueuedCameraFrame? _pendingCameraFrame;
    private string _previewPathDescription = "DX12 preview path pending";
    private readonly object _diagnosticsLock = new();
    private Direct3D12PreviewDiagnostics _diagnostics = Direct3D12PreviewDiagnostics.Empty;
    private DateTimeOffset _diagnosticsFpsWindowStartUtc = DateTimeOffset.UtcNow;
    private long _diagnosticsFpsWindowStartRenderedFrames;
    private long _submittedFrames;
    private long _renderedFrames;
    private long _droppedFrames;
    private double _renderFramesPerSecond;
    private string _recordingMode = "not recording";
    private DateTime _lastAcceptedRenderFrameUtc = DateTime.MinValue;
    private double _maxRenderFramesPerSecond;
    private bool _renderWorkerStopping;
    private bool _disposed;

    public Direct3D12PreviewHost(IntPtr nativeD3D12Device = default)
        : base("Could not create DX12 preview child window.")
    {
        _nativeD3D12Device = nativeD3D12Device;
        _renderThread = new Thread(RenderWorkerLoop)
        {
            IsBackground = true,
            Name = "Avatar Builder DX12 preview"
        };
        _renderThread.Start();
    }

    public event EventHandler<string>? StatusChanged;

    public event EventHandler<Direct3D12PreviewDiagnostics>? DiagnosticsChanged;

    public bool IsReady => _renderer is not null;

    public string DeviceDescription => _renderer?.DeviceDescription ?? "DX12 preview not initialized";

    public string PreviewPathDescription => _previewPathDescription;

    public string RecordingMode => Volatile.Read(ref _recordingMode);

    public Direct3D12PreviewDiagnostics Diagnostics
    {
        get
        {
            lock (_diagnosticsLock)
            {
                return _diagnostics;
            }
        }
    }

    public void SetRecordingMode(string recordingMode)
    {
        Volatile.Write(
            ref _recordingMode,
            string.IsNullOrWhiteSpace(recordingMode) ? "not recording" : recordingMode.Trim());
    }

    public void LimitRenderRate(double maxFramesPerSecond)
    {
        lock (_renderThrottleLock)
        {
            _maxRenderFramesPerSecond = maxFramesPerSecond <= 0d
                ? 0d
                : Math.Clamp(maxFramesPerSecond, 1d, 120d);
            _lastAcceptedRenderFrameUtc = DateTime.MinValue;
        }
    }

    public void RenderBgraFrame(
        CameraFrame frame,
        long frameNumber,
        VideoFrameColorSettings colorSettings = default,
        bool denoiseEnabled = false,
        double denoiseStrength = 0d)
    {
        if (_disposed)
        {
            return;
        }

        RecordSubmittedFrame();
        if (!ShouldAcceptRenderFrame())
        {
            RecordDroppedFrame();
            return;
        }

        lock (_renderWorkerLock)
        {
            if (_pendingCameraFrame is not null)
            {
                RecordDroppedFrame();
            }

            _pendingCameraFrame = new QueuedCameraFrame(
                frame.BgraBytes,
                frame.Width,
                frame.Height,
                frame.Stride,
                frame.Nv12Bytes,
                frame.Nv12Stride,
                frame.Format,
                colorSettings,
                denoiseEnabled,
                denoiseStrength,
                frameNumber);
        }

        _renderFrameReady.Set();
    }

    public void RenderProofFrame(TextureNativeFrameInfo frame)
    {
        if (_renderer is null)
        {
            return;
        }

        try
        {
            lock (_rendererLock)
            {
                _renderer.RenderProofFrame(frame.FrameNumber);
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"DX12 preview render failed: {ex.Message}");
        }
    }

    public void RenderTextureFrame(
        TextureNativeFrameLease frame,
        bool denoiseEnabled,
        double denoiseStrength,
        VideoFrameColorSettings colorSettings = default)
    {
        RecordSubmittedFrame();
        if (_renderer is null)
        {
            RecordDroppedFrame();
            return;
        }

        try
        {
            lock (_rendererLock)
            {
                string? directTextureFailureReason = null;
                if (frame.IsValid
                    && string.Equals(frame.DeviceMode, "D3D12", StringComparison.OrdinalIgnoreCase)
                    && _renderer.RenderNativeTextureFrame(frame, colorSettings, denoiseEnabled, denoiseStrength, out directTextureFailureReason))
                {
                    ReportPreviewPath("direct DX12 texture");
                    RecordRenderedFrame("direct DX12 texture", frame.MediaSubtype, frame.Width, frame.Height, frame.FramesPerSecond, denoiseEnabled, denoiseStrength, colorSettings, RecordingMode, null, frame.FrameNumber);
                    return;
                }

                string? sharedBridgeFailureReason = null;
                if (frame.D3D12SharedTextureHandle != IntPtr.Zero
                    && _renderer.RenderSharedD3D11BridgeFrame(frame, colorSettings, denoiseEnabled, denoiseStrength, out sharedBridgeFailureReason))
                {
                    ReportPreviewPath("DX12 D3D11 bridge texture preview");
                    RecordRenderedFrame("DX12 D3D11 bridge texture preview", frame.MediaSubtype, frame.Width, frame.Height, frame.FramesPerSecond, denoiseEnabled, denoiseStrength, colorSettings, RecordingMode, directTextureFailureReason, frame.FrameNumber);
                    return;
                }
                else if (frame.D3D12SharedTextureHandle == IntPtr.Zero
                    && !string.Equals(frame.DeviceMode, "D3D12", StringComparison.OrdinalIgnoreCase))
                {
                    sharedBridgeFailureReason = "D3D11 bridge shared texture handle missing";
                }

                var textureFailureReason = CombineTextureFailureReasons(directTextureFailureReason, sharedBridgeFailureReason);
                if (TryRenderNv12UploadFallback(_renderer, frame, textureFailureReason, colorSettings, denoiseEnabled, denoiseStrength))
                {
                    return;
                }

                if (frame.BgraPreviewBytes is not null && frame.BgraPreviewStride > 0)
                {
                    _renderer.RenderBgraFrame(
                        frame.BgraPreviewBytes,
                        frame.Width,
                        frame.Height,
                        frame.BgraPreviewStride,
                        frame.FrameNumber,
                        colorSettings,
                        denoiseEnabled: denoiseEnabled,
                        denoiseStrength: denoiseStrength);
                    var path = FormatUploadFallbackPath("DX12 BGRA upload fallback", textureFailureReason);
                    ReportPreviewPath(path);
                    RecordRenderedFrame(path, "BGRA upload fallback", frame.Width, frame.Height, frame.FramesPerSecond, denoiseEnabled, denoiseStrength, colorSettings, RecordingMode, textureFailureReason, frame.FrameNumber);
                    return;
                }

                _renderer.RenderProofFrame(frame.FrameNumber);
                var proofPath = FormatUploadFallbackPath("DX12 proof-frame fallback", textureFailureReason);
                ReportPreviewPath(proofPath);
                RecordDroppedFrame();
            }
        }
        catch (Exception ex)
        {
            RecordDroppedFrame();
            StatusChanged?.Invoke(this, $"DX12 camera frame upload failed: {ex.Message}");
        }
    }

    private void ReportPreviewPath(string description)
    {
        if (string.Equals(_previewPathDescription, description, StringComparison.Ordinal))
        {
            return;
        }

        _previewPathDescription = description;
        StatusChanged?.Invoke(this, $"DX12 preview path: {description}");
    }

    private void RenderWorkerLoop()
    {
        try
        {
            while (true)
            {
                _renderFrameReady.WaitOne();

                while (true)
                {
                    QueuedCameraFrame? frame;
                    lock (_renderWorkerLock)
                    {
                        if (_renderWorkerStopping)
                        {
                            return;
                        }

                        frame = _pendingCameraFrame;
                        _pendingCameraFrame = null;
                    }

                    if (frame is null)
                    {
                        break;
                    }

                    try
                    {
                        lock (_rendererLock)
                        {
                            if (_renderer is null)
                            {
                                break;
                            }

                            if (frame.Nv12Bytes is { Length: > 0 } nv12Bytes && frame.Nv12Stride > 0)
                            {
                                if (_renderer.RenderNv12Frame(
                                    nv12Bytes,
                                    frame.Width,
                                    frame.Height,
                                    frame.Nv12Stride,
                                    frame.FrameNumber,
                                    frame.ColorSettings,
                                    frame.DenoiseEnabled,
                                    frame.DenoiseStrength))
                                {
                                    ReportPreviewPath("DX12 NV12 upload preview");
                                    RecordRenderedFrame(
                                        "DX12 NV12 upload preview",
                                        frame.FrameFormat,
                                        frame.Width,
                                        frame.Height,
                                        0d,
                                        frame.DenoiseEnabled,
                                        frame.DenoiseStrength,
                                        frame.ColorSettings,
                                        RecordingMode,
                                        null,
                                        frame.FrameNumber);
                                    continue;
                                }

                                StatusChanged?.Invoke(
                                    this,
                                    $"DX12 NV12 preview renderer refused {frame.Width}x{frame.Height}, stride {frame.Nv12Stride}, bytes {nv12Bytes.Length}: {_renderer.LastNv12PreviewFailureReason}");
                            }

                            if (frame.BgraBytes.Length <= 0 || frame.Stride <= 0)
                            {
                                StatusChanged?.Invoke(this, "DX12 preview skipped: frame had no renderable BGRA or NV12 payload.");
                                break;
                            }

                            _renderer.RenderBgraFrame(
                                frame.BgraBytes,
                                frame.Width,
                                frame.Height,
                                frame.Stride,
                                frame.FrameNumber,
                                frame.ColorSettings,
                                frame.DenoiseEnabled,
                                frame.DenoiseStrength);
                        }

                        ReportPreviewPath("DX12 BGRA upload preview");
                        RecordRenderedFrame(
                            "DX12 BGRA upload preview",
                            frame.FrameFormat,
                            frame.Width,
                            frame.Height,
                            0d,
                            frame.DenoiseEnabled,
                            frame.DenoiseStrength,
                            frame.ColorSettings,
                            RecordingMode,
                            null,
                            frame.FrameNumber);
                    }
                    catch (Exception ex)
                    {
                        RecordDroppedFrame();
                        StatusChanged?.Invoke(this, $"DX12 BGRA preview upload failed: {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            DisposeRenderer();
        }
    }

    private bool StopRenderWorker()
    {
        lock (_renderWorkerLock)
        {
            _renderWorkerStopping = true;
            _pendingCameraFrame = null;
        }

        _renderFrameReady.Set();
        var stopped = _renderThread?.Join(TimeSpan.FromSeconds(2)) != false;
        _renderThread = null;
        _renderFrameReady.Dispose();
        return stopped;
    }

    private sealed record QueuedCameraFrame(
        byte[] BgraBytes,
        int Width,
        int Height,
        int Stride,
        byte[]? Nv12Bytes,
        int Nv12Stride,
        string FrameFormat,
        VideoFrameColorSettings ColorSettings,
        bool DenoiseEnabled,
        double DenoiseStrength,
        long FrameNumber);

    private void RecordSubmittedFrame()
    {
        Interlocked.Increment(ref _submittedFrames);
    }

    private bool ShouldAcceptRenderFrame()
    {
        lock (_renderThrottleLock)
        {
            if (_maxRenderFramesPerSecond <= 0d)
            {
                return true;
            }

            var now = DateTime.UtcNow;
            if (now - _lastAcceptedRenderFrameUtc < TimeSpan.FromSeconds(1d / _maxRenderFramesPerSecond))
            {
                return false;
            }

            _lastAcceptedRenderFrameUtc = now;
            return true;
        }
    }

    private void RecordDroppedFrame()
    {
        Interlocked.Increment(ref _droppedFrames);
    }

    private void RecordRenderedFrame(
        string previewPath,
        string frameFormat,
        int width,
        int height,
        double sourceFramesPerSecond,
        bool denoiseEnabled,
        double denoiseStrength,
        VideoFrameColorSettings colorSettings,
        string recordingMode,
        string? fallbackReason,
        long frameNumber)
    {
        var rendered = Interlocked.Increment(ref _renderedFrames);
        var submitted = Interlocked.Read(ref _submittedFrames);
        var dropped = Interlocked.Read(ref _droppedFrames);
        var now = DateTimeOffset.UtcNow;
        Direct3D12PreviewDiagnostics snapshot;
        lock (_diagnosticsLock)
        {
            var elapsed = now - _diagnosticsFpsWindowStartUtc;
            if (elapsed.TotalSeconds >= 1d)
            {
                var framesSinceWindowStart = Math.Max(0, rendered - _diagnosticsFpsWindowStartRenderedFrames);
                _renderFramesPerSecond = framesSinceWindowStart / Math.Max(0.001d, elapsed.TotalSeconds);
                _diagnosticsFpsWindowStartUtc = now;
                _diagnosticsFpsWindowStartRenderedFrames = rendered;
            }

            snapshot = new Direct3D12PreviewDiagnostics(
                previewPath,
                DeviceDescription,
                string.IsNullOrWhiteSpace(frameFormat) ? "unknown" : frameFormat,
                width,
                height,
                sourceFramesPerSecond,
                submitted,
                rendered,
                dropped,
                _renderFramesPerSecond,
                denoiseEnabled,
                denoiseStrength,
                colorSettings.HasVisibleAdjustments,
                recordingMode,
                fallbackReason,
                frameNumber,
                now);
            _diagnostics = snapshot;
        }

        DiagnosticsChanged?.Invoke(this, snapshot);
    }

    private static string FormatUploadFallbackPath(string path, string? directTextureFailureReason)
    {
        if (string.IsNullOrWhiteSpace(directTextureFailureReason))
        {
            return path;
        }

        var reason = directTextureFailureReason.Trim();
        if (reason.Length > 160)
        {
            reason = reason[..157] + "...";
        }

        return $"{path}; texture unavailable: {reason}";
    }

    private static string? CombineTextureFailureReasons(string? directTextureFailureReason, string? sharedBridgeFailureReason)
    {
        var direct = string.IsNullOrWhiteSpace(directTextureFailureReason)
            ? null
            : $"direct: {directTextureFailureReason.Trim()}";
        var bridge = string.IsNullOrWhiteSpace(sharedBridgeFailureReason)
            ? null
            : $"bridge: {sharedBridgeFailureReason.Trim()}";
        return (direct, bridge) switch
        {
            (not null, not null) => $"{direct}; {bridge}",
            (not null, null) => direct,
            (null, not null) => bridge,
            _ => null
        };
    }

    private bool TryRenderNv12UploadFallback(
        Direct3D12SwapChainRenderer renderer,
        TextureNativeFrameLease frame,
        string? textureFailureReason,
        VideoFrameColorSettings colorSettings,
        bool denoiseEnabled,
        double denoiseStrength)
    {
        if (frame.Nv12PreviewBytes is not { Length: > 0 } nv12Bytes || frame.Nv12PreviewStride <= 0)
        {
            return false;
        }

        if (!renderer.RenderNv12Frame(
            nv12Bytes,
            frame.Width,
            frame.Height,
            frame.Nv12PreviewStride,
            frame.FrameNumber,
            colorSettings,
            denoiseEnabled,
            denoiseStrength))
        {
            return false;
        }

        var path = FormatUploadFallbackPath("DX12 NV12 upload fallback", textureFailureReason);
        ReportPreviewPath(path);
        RecordRenderedFrame(
            path,
            "NV12 upload fallback",
            frame.Width,
            frame.Height,
            frame.FramesPerSecond,
            denoiseEnabled,
            denoiseStrength,
            colorSettings,
            RecordingMode,
            textureFailureReason,
            frame.FrameNumber);
        return true;
    }

    protected override void OnViewportCreated(IntPtr hwnd, int width, int height)
    {
        Direct3D12SwapChainRenderer renderer;
        var nativeDevice = Interlocked.Exchange(ref _nativeD3D12Device, IntPtr.Zero);
        try
        {
            lock (_rendererLock)
            {
                renderer = new Direct3D12SwapChainRenderer(hwnd, width, height, nativeDevice);
                _renderer = renderer;
            }

            nativeDevice = IntPtr.Zero;
        }
        finally
        {
            if (nativeDevice != IntPtr.Zero)
            {
                Marshal.Release(nativeDevice);
            }
        }

        StatusChanged?.Invoke(this, $"{renderer.DeviceDescription} preview surface ready.");
        lock (_rendererLock)
        {
            renderer.RenderProofFrame(0);
        }
    }

    protected override void OnViewportCreateFailed(Exception ex)
    {
        StatusChanged?.Invoke(this, $"DX12 preview surface unavailable: {ex.Message}");
    }

    protected override void OnViewportDestroying()
    {
        TryDisposeRenderer("window destroy");
    }

    protected override void OnViewportResized(int width, int height)
    {
        lock (_rendererLock)
        {
            _renderer?.Resize(width, height);
        }
    }

    protected override void OnViewportResizeFailed(Exception ex)
    {
        StatusChanged?.Invoke(this, $"DX12 preview resize failed: {ex.Message}");
    }

    public new void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!StopRenderWorker())
        {
            StatusChanged?.Invoke(this, "DX12 preview stop is waiting for the render worker to leave the driver.");
        }

        var nativeDevice = Interlocked.Exchange(ref _nativeD3D12Device, IntPtr.Zero);
        if (nativeDevice != IntPtr.Zero)
        {
            Marshal.Release(nativeDevice);
        }

        base.Dispose();
    }

    private void DisposeRenderer()
    {
        lock (_rendererLock)
        {
            DisposeRendererCore();
        }
    }

    private void TryDisposeRenderer(string context)
    {
        if (!Monitor.TryEnter(_rendererLock, RendererDisposeLockTimeout))
        {
            StatusChanged?.Invoke(this, $"DX12 preview {context} deferred because the renderer is busy.");
            return;
        }

        try
        {
            DisposeRendererCore();
        }
        finally
        {
            Monitor.Exit(_rendererLock);
        }
    }

    private void DisposeRendererCore()
    {
        _renderer?.Dispose();
        _renderer = null;
    }

    private sealed class Direct3D12SwapChainRenderer : IDisposable
    {
        private const int FrameCount = 3;
        private const int D3D12DefaultShader4ComponentMappingValue = 5768;
        private const int BgraColorSettingsDescriptorStart = 3;
        private const int BgraColorSettingsBufferBytes = 256;

        private readonly ID3D12Device _device;
        private readonly ID3D12CommandQueue _commandQueue;
        private readonly IDXGIFactory4 _factory;
        private readonly ID3D12GraphicsCommandList _commandList;
        private readonly ID3D12Fence _fence;
        private readonly AutoResetEvent _fenceEvent = new(false);
        private readonly ID3D12DescriptorHeap _rtvHeap;
        private readonly ID3D12DescriptorHeap _srvHeap;
        private readonly int _rtvDescriptorSize;
        private readonly int _srvDescriptorSize;
        private readonly ID3D12Resource?[] _renderTargets = new ID3D12Resource?[FrameCount];
        private ID3D12RootSignature? _previewRootSignature;
        private ID3D12PipelineState? _previewPipelineState;
        private ID3D12RootSignature? _nv12PreviewRootSignature;
        private ID3D12PipelineState? _nv12PreviewPipelineState;
        private readonly FrameResource[] _frameResources = new FrameResource[FrameCount];
        private ID3D12Resource? _cameraTexture;
        private PlacedSubresourceFootPrint _cameraTextureFootprint;
        private ResourceStates _cameraTextureState;
        private ID3D12Resource? _nv12YTexture;
        private ID3D12Resource? _nv12UvTexture;
        private PlacedSubresourceFootPrint _nv12YFootprint;
        private PlacedSubresourceFootPrint _nv12UvFootprint;
        private ResourceStates _nv12YTextureState;
        private ResourceStates _nv12UvTextureState;
        private IDXGISwapChain3 _swapChain;
        private ulong _fenceValue;
        private int _cameraTextureWidth;
        private int _cameraTextureHeight;
        private int _nv12TextureWidth;
        private int _nv12TextureHeight;
        private int _width;
        private int _height;
        private bool _disposed;
        private bool _shaderPreviewUnavailable;
        private bool _nv12PreviewUnavailable;
        private string? _nv12PreviewFailureReason;
        private bool _nativeTexturePreviewUnavailable;
        private string? _nativeTexturePreviewFailureReason;
        private bool _sharedD3D11BridgePreviewUnavailable;
        private string? _sharedD3D11BridgePreviewFailureReason;
        private readonly bool _usesSharedCaptureDevice;

        public Direct3D12SwapChainRenderer(IntPtr hwnd, int width, int height, IntPtr nativeD3D12Device = default)
        {
            _width = width;
            _height = height;
            if (nativeD3D12Device != IntPtr.Zero)
            {
                _device = new ID3D12Device(nativeD3D12Device);
                _usesSharedCaptureDevice = true;
            }
            else
            {
                _device = D3D12CreateDevice<ID3D12Device>(null, FeatureLevel.Level_12_0);
            }

            _commandQueue = _device.CreateCommandQueue<ID3D12CommandQueue>(
                new CommandQueueDescription(CommandListType.Direct));
            _factory = CreateDXGIFactory2<IDXGIFactory4>(false);

            var swapChainDescription = new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = FrameCount,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.None
            };
            using var swapChain = _factory.CreateSwapChainForHwnd(
                _commandQueue,
                hwnd,
                swapChainDescription,
                null,
                null);
            _swapChain = swapChain.QueryInterface<IDXGISwapChain3>();
            _factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);

            _rtvHeap = _device.CreateDescriptorHeap<ID3D12DescriptorHeap>(
                new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, FrameCount));
            _srvHeap = _device.CreateDescriptorHeap<ID3D12DescriptorHeap>(
                new DescriptorHeapDescription(
                    DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                    BgraColorSettingsDescriptorStart + FrameCount,
                    DescriptorHeapFlags.ShaderVisible));
            _rtvDescriptorSize = (int)_device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
            _srvDescriptorSize = (int)_device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            CreateRenderTargetViews();
            TryCreatePreviewShaderPipeline();
            TryCreateNv12PreviewShaderPipeline();

            for (var i = 0; i < FrameCount; i++)
            {
                _frameResources[i] = new FrameResource(
                    _device.CreateCommandAllocator<ID3D12CommandAllocator>(CommandListType.Direct));
                _frameResources[i].CreateBgraColorSettingsBuffer(_device, BgraColorSettingsBufferBytes);
                _device.CreateConstantBufferView(
                    new ConstantBufferViewDescription
                    {
                        BufferLocation = _frameResources[i].BgraColorSettingsBuffer!.GPUVirtualAddress,
                        SizeInBytes = BgraColorSettingsBufferBytes
                    },
                    GetSrvCpuHandle(BgraColorSettingsDescriptorStart + i));
            }

            _commandList = _device.CreateCommandList<ID3D12GraphicsCommandList>(
                0,
                CommandListType.Direct,
                _frameResources[0].CommandAllocator,
                null);
            _commandList.Close();
            _fence = _device.CreateFence<ID3D12Fence>(0);
        }

        public string DeviceDescription => _usesSharedCaptureDevice
            ? "Direct3D 12 / DXGI flip model on shared capture device"
            : "Direct3D 12 / DXGI flip model";

        public string LastNv12PreviewFailureReason => _nv12PreviewFailureReason ?? "no NV12 failure detail";

        public unsafe void RenderProofFrame(long frameNumber)
        {
            if (_disposed)
            {
                return;
            }

            var frameResource = BeginFrame(out var frameIndex);
            var renderTarget = _renderTargets[frameIndex] ?? throw new InvalidOperationException("DX12 render target is not ready.");

            var toRenderTarget = ResourceBarrier.BarrierTransition(
                renderTarget,
                ResourceStates.Present,
                ResourceStates.RenderTarget);
            _commandList.ResourceBarrier([toRenderTarget]);

            var rtvHandle = GetRtvHandle(frameIndex);
            var pulse = (float)((frameNumber % 120) / 120d);
            _commandList.OMSetRenderTargets(rtvHandle, null);
            _commandList.ClearRenderTargetView(
                rtvHandle,
                new Color4(0.02f + pulse * 0.08f, 0.08f, 0.12f + pulse * 0.18f, 1f),
                0,
                null!);

            var toPresent = ResourceBarrier.BarrierTransition(
                renderTarget,
                ResourceStates.RenderTarget,
                ResourceStates.Present);
            _commandList.ResourceBarrier([toPresent]);
            ExecuteAndPresent(frameResource);
        }

        public unsafe void RenderBgraFrame(
            byte[] bgraBytes,
            int width,
            int height,
            int stride,
            long frameNumber,
            VideoFrameColorSettings colorSettings = default,
            bool denoiseEnabled = false,
            double denoiseStrength = 0d)
        {
            if (_disposed || bgraBytes.Length < stride * height)
            {
                return;
            }

            if (width != _width || height != _height)
            {
                Resize(width, height);
            }

            if (TryRenderBgraFrameWithShader(bgraBytes, width, height, stride, colorSettings, denoiseEnabled, denoiseStrength))
            {
                return;
            }

            RenderBgraFrameToBackBuffer(bgraBytes, height, stride);
        }

        public unsafe bool RenderNv12Frame(
            byte[] nv12Bytes,
            int width,
            int height,
            int stride,
            long frameNumber,
            VideoFrameColorSettings colorSettings,
            bool denoiseEnabled,
            double denoiseStrength)
        {
            var uvHeight = (height + 1) / 2;
            if (_disposed || stride < width || nv12Bytes.Length < stride * height + stride * uvHeight)
            {
                _nv12PreviewFailureReason = _disposed
                    ? "renderer disposed"
                    : $"invalid NV12 payload: stride {stride}, width {width}, bytes {nv12Bytes.Length}, expected {stride * height + stride * uvHeight}";
                return false;
            }

            if (width != _width || height != _height)
            {
                Resize(width, height);
            }

            var rendered = TryRenderNv12FrameWithShader(nv12Bytes, width, height, stride, colorSettings, denoiseEnabled, denoiseStrength);
            if (rendered)
            {
                _nv12PreviewFailureReason = null;
            }

            return rendered;
        }

        public bool RenderNativeTextureFrame(
            TextureNativeFrameLease frame,
            VideoFrameColorSettings colorSettings,
            bool denoiseEnabled,
            double denoiseStrength,
            out string? failureReason)
        {
            failureReason = null;
            if (_disposed)
            {
                failureReason = "renderer disposed";
                return false;
            }

            if (!_usesSharedCaptureDevice)
            {
                failureReason = "presenter is not using the capture D3D12 device";
                return false;
            }

            if (_nativeTexturePreviewUnavailable)
            {
                failureReason = _nativeTexturePreviewFailureReason ?? "direct texture rendering disabled after an earlier failure";
                return false;
            }

            if (_nv12PreviewRootSignature is null || _nv12PreviewPipelineState is null)
            {
                failureReason = "NV12 shader pipeline unavailable";
                return false;
            }

            if (frame.Resource == IntPtr.Zero)
            {
                failureReason = "frame texture resource is missing";
                return false;
            }

            if (!frame.MediaSubtype.Contains("NV12", StringComparison.OrdinalIgnoreCase))
            {
                failureReason = $"media subtype {frame.MediaSubtype} is not NV12";
                return false;
            }

            if (frame.Width != _width || frame.Height != _height)
            {
                Resize(frame.Width, frame.Height);
            }

            try
            {
                Marshal.AddRef(frame.Resource);
                using var nativeResource = new ID3D12Resource(frame.Resource);
                RenderNativeNv12Resource(nativeResource, frame.Width, frame.Height, colorSettings, denoiseEnabled, denoiseStrength);
                _nativeTexturePreviewFailureReason = null;
                return true;
            }
            catch (Exception ex)
            {
                _nativeTexturePreviewUnavailable = true;
                _nativeTexturePreviewFailureReason = ex.Message;
                failureReason = ex.Message;
                return false;
            }
        }

        public bool RenderSharedD3D11BridgeFrame(
            TextureNativeFrameLease frame,
            VideoFrameColorSettings colorSettings,
            bool denoiseEnabled,
            double denoiseStrength,
            out string? failureReason)
        {
            failureReason = null;
            if (_disposed)
            {
                failureReason = "renderer disposed";
                return false;
            }

            if (_sharedD3D11BridgePreviewUnavailable)
            {
                failureReason = _sharedD3D11BridgePreviewFailureReason ?? "D3D11 bridge texture rendering disabled after an earlier failure";
                return false;
            }

            if (_nv12PreviewRootSignature is null || _nv12PreviewPipelineState is null)
            {
                failureReason = "NV12 shader pipeline unavailable";
                return false;
            }

            if (frame.D3D12SharedTextureHandle == IntPtr.Zero)
            {
                failureReason = "D3D11 bridge shared texture handle is missing";
                return false;
            }

            if (!frame.MediaSubtype.Contains("NV12", StringComparison.OrdinalIgnoreCase))
            {
                failureReason = $"media subtype {frame.MediaSubtype} is not NV12";
                return false;
            }

            if (frame.Width != _width || frame.Height != _height)
            {
                Resize(frame.Width, frame.Height);
            }

            try
            {
                using var sharedResource = _device.OpenSharedHandle<ID3D12Resource>(frame.D3D12SharedTextureHandle);
                RenderNativeNv12Resource(sharedResource, frame.Width, frame.Height, colorSettings, denoiseEnabled, denoiseStrength);
                WaitForGpu();
                _sharedD3D11BridgePreviewFailureReason = null;
                return true;
            }
            catch (Exception ex)
            {
                _sharedD3D11BridgePreviewUnavailable = true;
                _sharedD3D11BridgePreviewFailureReason = ex.Message;
                failureReason = ex.Message;
                return false;
            }
        }

        private void RenderNativeNv12Resource(
            ID3D12Resource cameraResource,
            int width,
            int height,
            VideoFrameColorSettings colorSettings,
            bool denoiseEnabled,
            double denoiseStrength)
        {
            var ySrvDescription = new ShaderResourceViewDescription
            {
                Format = Format.R8_UNorm,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = D3D12DefaultShader4ComponentMappingValue,
                Texture2D = new Texture2DShaderResourceView
                {
                    MipLevels = 1,
                    PlaneSlice = 0
                }
            };
            var uvSrvDescription = new ShaderResourceViewDescription
            {
                Format = Format.R8G8_UNorm,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = D3D12DefaultShader4ComponentMappingValue,
                Texture2D = new Texture2DShaderResourceView
                {
                    MipLevels = 1,
                    PlaneSlice = 1
                }
            };
            _device.CreateShaderResourceView(cameraResource, ySrvDescription, GetSrvCpuHandle(1));
            _device.CreateShaderResourceView(cameraResource, uvSrvDescription, GetSrvCpuHandle(2));

            var frameResource = BeginFrame(out var frameIndex);
            var renderTarget = _renderTargets[frameIndex] ?? throw new InvalidOperationException("DX12 render target is not ready.");

            var cameraToPixelShaderResource = ResourceBarrier.BarrierTransition(
                cameraResource,
                ResourceStates.Common,
                ResourceStates.PixelShaderResource);
            var toRenderTarget = ResourceBarrier.BarrierTransition(
                renderTarget,
                ResourceStates.Present,
                ResourceStates.RenderTarget);
            _commandList.ResourceBarrier([cameraToPixelShaderResource, toRenderTarget]);

            var rtvHandle = GetRtvHandle(frameIndex);
            _commandList.SetGraphicsRootSignature(_nv12PreviewRootSignature);
            _commandList.SetPipelineState(_nv12PreviewPipelineState);
            _commandList.SetDescriptorHeaps([_srvHeap]);
            _commandList.SetGraphicsRootDescriptorTable(0, GetSrvGpuHandle(1));
            SetNv12ShaderConstants(width, height, colorSettings, denoiseEnabled, denoiseStrength);
            var viewport = new Viewport(0, 0, _width, _height);
            var scissor = new RawRect(0, 0, _width, _height);
            _commandList.RSSetViewports(viewport);
            _commandList.RSSetScissorRects(scissor);
            _commandList.OMSetRenderTargets(rtvHandle, null);
            _commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _commandList.DrawInstanced(3, 1, 0, 0);

            var toPresent = ResourceBarrier.BarrierTransition(
                renderTarget,
                ResourceStates.RenderTarget,
                ResourceStates.Present);
            var cameraToCommon = ResourceBarrier.BarrierTransition(
                cameraResource,
                ResourceStates.PixelShaderResource,
                ResourceStates.Common);
            _commandList.ResourceBarrier([toPresent, cameraToCommon]);
            ExecuteAndPresent(frameResource);
        }

        private unsafe bool TryRenderNv12FrameWithShader(
            byte[] nv12Bytes,
            int width,
            int height,
            int stride,
            VideoFrameColorSettings colorSettings,
            bool denoiseEnabled,
            double denoiseStrength)
        {
            if (_nv12PreviewUnavailable || _nv12PreviewRootSignature is null || _nv12PreviewPipelineState is null)
            {
                _nv12PreviewFailureReason = _nv12PreviewUnavailable
                    ? _nv12PreviewFailureReason ?? "NV12 preview disabled after earlier failure"
                    : "NV12 shader pipeline unavailable";
                return false;
            }

            try
            {
                EnsureNv12Textures(width, height);
                var yTexture = _nv12YTexture;
                var uvTexture = _nv12UvTexture;
                var frameResource = BeginFrame(out var frameIndex);
                var yUploadBuffer = frameResource.Nv12YUploadBuffer;
                var uvUploadBuffer = frameResource.Nv12UvUploadBuffer;
                if (yTexture is null || uvTexture is null || yUploadBuffer is null || uvUploadBuffer is null)
                {
                    return false;
                }

                CopyNv12FrameToUploadBuffers(frameResource, nv12Bytes, width, height, stride);

                var renderTarget = _renderTargets[frameIndex] ?? throw new InvalidOperationException("DX12 render target is not ready.");

                if (_nv12YTextureState != ResourceStates.CopyDest)
                {
                    var toCopyDestination = ResourceBarrier.BarrierTransition(
                        yTexture,
                        _nv12YTextureState,
                        ResourceStates.CopyDest);
                    _commandList.ResourceBarrier([toCopyDestination]);
                    _nv12YTextureState = ResourceStates.CopyDest;
                }

                if (_nv12UvTextureState != ResourceStates.CopyDest)
                {
                    var toCopyDestination = ResourceBarrier.BarrierTransition(
                        uvTexture,
                        _nv12UvTextureState,
                        ResourceStates.CopyDest);
                    _commandList.ResourceBarrier([toCopyDestination]);
                    _nv12UvTextureState = ResourceStates.CopyDest;
                }

                _commandList.CopyTextureRegion(
                    new TextureCopyLocation(yTexture, 0),
                    0,
                    0,
                    0,
                    new TextureCopyLocation(yUploadBuffer, _nv12YFootprint),
                    null);
                _commandList.CopyTextureRegion(
                    new TextureCopyLocation(uvTexture, 0),
                    0,
                    0,
                    0,
                    new TextureCopyLocation(uvUploadBuffer, _nv12UvFootprint),
                    null);

                var yToPixelShaderResource = ResourceBarrier.BarrierTransition(
                    yTexture,
                    ResourceStates.CopyDest,
                    ResourceStates.PixelShaderResource);
                var uvToPixelShaderResource = ResourceBarrier.BarrierTransition(
                    uvTexture,
                    ResourceStates.CopyDest,
                    ResourceStates.PixelShaderResource);
                _commandList.ResourceBarrier([yToPixelShaderResource, uvToPixelShaderResource]);
                _nv12YTextureState = ResourceStates.PixelShaderResource;
                _nv12UvTextureState = ResourceStates.PixelShaderResource;

                var toRenderTarget = ResourceBarrier.BarrierTransition(
                    renderTarget,
                    ResourceStates.Present,
                    ResourceStates.RenderTarget);
                _commandList.ResourceBarrier([toRenderTarget]);

                var rtvHandle = GetRtvHandle(frameIndex);
                _commandList.SetGraphicsRootSignature(_nv12PreviewRootSignature);
                _commandList.SetPipelineState(_nv12PreviewPipelineState);
                _commandList.SetDescriptorHeaps([_srvHeap]);
                _commandList.SetGraphicsRootDescriptorTable(0, GetSrvGpuHandle(1));
                SetNv12ShaderConstants(width, height, colorSettings, denoiseEnabled, denoiseStrength);
                var viewport = new Viewport(0, 0, _width, _height);
                var scissor = new RawRect(0, 0, _width, _height);
                _commandList.RSSetViewports(viewport);
                _commandList.RSSetScissorRects(scissor);
                _commandList.OMSetRenderTargets(rtvHandle, null);
                _commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                _commandList.DrawInstanced(3, 1, 0, 0);

                var toPresent = ResourceBarrier.BarrierTransition(
                    renderTarget,
                    ResourceStates.RenderTarget,
                    ResourceStates.Present);
                _commandList.ResourceBarrier([toPresent]);
                ExecuteAndPresent(frameResource);
                return true;
            }
            catch (Exception ex)
            {
                _nv12PreviewUnavailable = true;
                _nv12PreviewFailureReason = ex.Message;
                return false;
            }
        }

        private unsafe bool TryRenderBgraFrameWithShader(
            byte[] bgraBytes,
            int width,
            int height,
            int stride,
            VideoFrameColorSettings colorSettings,
            bool denoiseEnabled,
            double denoiseStrength)
        {
            if (_shaderPreviewUnavailable || _previewRootSignature is null || _previewPipelineState is null)
            {
                return false;
            }

            try
            {
                EnsureCameraTexture(width, height);
                var cameraTexture = _cameraTexture;
                var frameResource = BeginFrame(out var frameIndex);
                var uploadBuffer = frameResource.CameraUploadBuffer;
                if (cameraTexture is null || uploadBuffer is null)
                {
                    return false;
                }

                CopyBgraFrameToUploadBuffer(frameResource, bgraBytes, width, height, stride);
                WriteBgraColorSettings(frameResource, colorSettings, denoiseEnabled, denoiseStrength, width, height);

                var renderTarget = _renderTargets[frameIndex] ?? throw new InvalidOperationException("DX12 render target is not ready.");

                if (_cameraTextureState != ResourceStates.CopyDest)
                {
                    var toCopyDestination = ResourceBarrier.BarrierTransition(
                        cameraTexture,
                        _cameraTextureState,
                        ResourceStates.CopyDest);
                    _commandList.ResourceBarrier([toCopyDestination]);
                    _cameraTextureState = ResourceStates.CopyDest;
                }

                _commandList.CopyTextureRegion(
                    new TextureCopyLocation(cameraTexture, 0),
                    0,
                    0,
                    0,
                    new TextureCopyLocation(uploadBuffer, _cameraTextureFootprint),
                    null);

                var toPixelShaderResource = ResourceBarrier.BarrierTransition(
                    cameraTexture,
                    ResourceStates.CopyDest,
                    ResourceStates.PixelShaderResource);
                _commandList.ResourceBarrier([toPixelShaderResource]);
                _cameraTextureState = ResourceStates.PixelShaderResource;

                var toRenderTarget = ResourceBarrier.BarrierTransition(
                    renderTarget,
                    ResourceStates.Present,
                    ResourceStates.RenderTarget);
                _commandList.ResourceBarrier([toRenderTarget]);

                var rtvHandle = GetRtvHandle(frameIndex);
                _commandList.SetGraphicsRootSignature(_previewRootSignature);
                _commandList.SetPipelineState(_previewPipelineState);
                _commandList.SetDescriptorHeaps([_srvHeap]);
                _commandList.SetGraphicsRootDescriptorTable(0, _srvHeap.GetGPUDescriptorHandleForHeapStart());
                _commandList.SetGraphicsRootDescriptorTable(1, GetSrvGpuHandle(BgraColorSettingsDescriptorStart + frameIndex));
                var viewport = new Viewport(0, 0, _width, _height);
                var scissor = new RawRect(0, 0, _width, _height);
                _commandList.RSSetViewports(viewport);
                _commandList.RSSetScissorRects(scissor);
                _commandList.OMSetRenderTargets(rtvHandle, null);
                _commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                _commandList.DrawInstanced(3, 1, 0, 0);

                var toPresent = ResourceBarrier.BarrierTransition(
                    renderTarget,
                    ResourceStates.RenderTarget,
                    ResourceStates.Present);
                _commandList.ResourceBarrier([toPresent]);
                ExecuteAndPresent(frameResource);
                return true;
            }
            catch
            {
                _shaderPreviewUnavailable = true;
                return false;
            }
        }

        private unsafe void RenderBgraFrameToBackBuffer(byte[] bgraBytes, int height, int stride)
        {
            var frameResource = BeginFrame(out var frameIndex);
            var renderTarget = _renderTargets[frameIndex] ?? throw new InvalidOperationException("DX12 render target is not ready.");

            var toCommon = ResourceBarrier.BarrierTransition(
                renderTarget,
                ResourceStates.Present,
                ResourceStates.Common);
            _commandList.ResourceBarrier([toCommon]);
            _commandList.Close();
            _commandQueue.ExecuteCommandList(_commandList);
            WaitForGpu();

            fixed (byte* source = bgraBytes)
            {
                renderTarget.WriteToSubresource(
                    0,
                    null,
                    (IntPtr)source,
                    (uint)stride,
                    (uint)(stride * height));
            }

            frameResource.CommandAllocator.Reset();
            _commandList.Reset(frameResource.CommandAllocator);
            var toPresent = ResourceBarrier.BarrierTransition(
                renderTarget,
                ResourceStates.Common,
                ResourceStates.Present);
            _commandList.ResourceBarrier([toPresent]);
            ExecuteAndPresent(frameResource);
        }

        private void EnsureCameraTexture(int width, int height)
        {
            if (_cameraTexture is not null
                && CameraUploadBuffersReady()
                && _cameraTextureWidth == width
                && _cameraTextureHeight == height)
            {
                return;
            }

            WaitForGpu();
            _cameraTexture?.Dispose();
            ReleaseCameraUploadBuffers();
            _cameraTexture = null;
            _cameraTextureWidth = width;
            _cameraTextureHeight = height;
            var description = new ResourceDescription(
                ResourceDimension.Texture2D,
                0,
                (ulong)width,
                (uint)height,
                1,
                1,
                Format.B8G8R8A8_UNorm,
                1,
                0,
                TextureLayout.Unknown,
                ResourceFlags.None);
            _cameraTexture = _device.CreateCommittedResource<ID3D12Resource>(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                description,
                ResourceStates.CopyDest);
            _cameraTextureState = ResourceStates.CopyDest;
            var layouts = new PlacedSubresourceFootPrint[1];
            var numRows = new uint[1];
            var rowSizesInBytes = new ulong[1];
            _device.GetCopyableFootprints(
                description,
                0,
                1,
                0,
                layouts,
                numRows,
                rowSizesInBytes,
                out var uploadBytes);
            _cameraTextureFootprint = layouts[0];
            foreach (var frameResource in _frameResources)
            {
                frameResource.CreateCameraUploadBuffer(_device, uploadBytes);
            }

            var srvDescription = new ShaderResourceViewDescription
            {
                Format = Format.B8G8R8A8_UNorm,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = D3D12DefaultShader4ComponentMappingValue,
                Texture2D = new Texture2DShaderResourceView
                {
                    MipLevels = 1
                }
            };
            _device.CreateShaderResourceView(
                _cameraTexture,
                srvDescription,
                _srvHeap.GetCPUDescriptorHandleForHeapStart());
        }

        private void EnsureNv12Textures(int width, int height)
        {
            if (_nv12YTexture is not null
                && _nv12UvTexture is not null
                && Nv12UploadBuffersReady()
                && _nv12TextureWidth == width
                && _nv12TextureHeight == height)
            {
                return;
            }

            WaitForGpu();
            _nv12YTexture?.Dispose();
            _nv12UvTexture?.Dispose();
            ReleaseNv12UploadBuffers();
            _nv12YTexture = null;
            _nv12UvTexture = null;
            _nv12TextureWidth = width;
            _nv12TextureHeight = height;

            var yDescription = new ResourceDescription(
                ResourceDimension.Texture2D,
                0,
                (ulong)width,
                (uint)height,
                1,
                1,
                Format.R8_UNorm,
                1,
                0,
                TextureLayout.Unknown,
                ResourceFlags.None);
            var uvDescription = new ResourceDescription(
                ResourceDimension.Texture2D,
                0,
                (ulong)Math.Max(1, width / 2),
                (uint)Math.Max(1, height / 2),
                1,
                1,
                Format.R8G8_UNorm,
                1,
                0,
                TextureLayout.Unknown,
                ResourceFlags.None);

            _nv12YTexture = _device.CreateCommittedResource<ID3D12Resource>(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                yDescription,
                ResourceStates.CopyDest);
            _nv12UvTexture = _device.CreateCommittedResource<ID3D12Resource>(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                uvDescription,
                ResourceStates.CopyDest);
            _nv12YTextureState = ResourceStates.CopyDest;
            _nv12UvTextureState = ResourceStates.CopyDest;

            _nv12YFootprint = GetTextureFootprint(yDescription, out var yUploadBytes);
            _nv12UvFootprint = GetTextureFootprint(uvDescription, out var uvUploadBytes);
            foreach (var frameResource in _frameResources)
            {
                frameResource.CreateNv12UploadBuffers(_device, yUploadBytes, uvUploadBytes);
            }

            var ySrvDescription = new ShaderResourceViewDescription
            {
                Format = Format.R8_UNorm,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = D3D12DefaultShader4ComponentMappingValue,
                Texture2D = new Texture2DShaderResourceView
                {
                    MipLevels = 1
                }
            };
            var uvSrvDescription = new ShaderResourceViewDescription
            {
                Format = Format.R8G8_UNorm,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = D3D12DefaultShader4ComponentMappingValue,
                Texture2D = new Texture2DShaderResourceView
                {
                    MipLevels = 1
                }
            };
            _device.CreateShaderResourceView(_nv12YTexture, ySrvDescription, GetSrvCpuHandle(1));
            _device.CreateShaderResourceView(_nv12UvTexture, uvSrvDescription, GetSrvCpuHandle(2));
        }

        private PlacedSubresourceFootPrint GetTextureFootprint(
            ResourceDescription description,
            out ulong uploadBytes)
        {
            var layouts = new PlacedSubresourceFootPrint[1];
            var numRows = new uint[1];
            var rowSizesInBytes = new ulong[1];
            _device.GetCopyableFootprints(
                description,
                0,
                1,
                0,
                layouts,
                numRows,
                rowSizesInBytes,
                out uploadBytes);
            return layouts[0];
        }

        private bool CameraUploadBuffersReady()
        {
            foreach (var frameResource in _frameResources)
            {
                if (frameResource.CameraUploadBuffer is null || frameResource.CameraUploadPointer == IntPtr.Zero)
                {
                    return false;
                }
            }

            return true;
        }

        private bool Nv12UploadBuffersReady()
        {
            foreach (var frameResource in _frameResources)
            {
                if (frameResource.Nv12YUploadBuffer is null
                    || frameResource.Nv12UvUploadBuffer is null
                    || frameResource.Nv12YUploadPointer == IntPtr.Zero
                    || frameResource.Nv12UvUploadPointer == IntPtr.Zero)
                {
                    return false;
                }
            }

            return true;
        }

        private void ReleaseCameraUploadBuffers()
        {
            foreach (var frameResource in _frameResources)
            {
                frameResource.ReleaseCameraUploadBuffer();
            }
        }

        private void ReleaseNv12UploadBuffers()
        {
            foreach (var frameResource in _frameResources)
            {
                frameResource.ReleaseNv12UploadBuffers();
            }
        }

        private unsafe void CopyBgraFrameToUploadBuffer(FrameResource frameResource, byte[] bgraBytes, int width, int height, int stride)
        {
            var mappedData = (byte*)frameResource.CameraUploadPointer;
            if (mappedData == null)
            {
                throw new InvalidOperationException("DX12 BGRA upload buffer is not mapped.");
            }

            fixed (byte* sourceStart = bgraBytes)
            {
                var sourceRowBytes = width * 4;
                var destinationStart = mappedData + (nint)_cameraTextureFootprint.Offset;
                var destinationStride = (nint)_cameraTextureFootprint.Footprint.RowPitch;
                for (var row = 0; row < height; row++)
                {
                    Buffer.MemoryCopy(
                        sourceStart + row * stride,
                        destinationStart + row * destinationStride,
                        destinationStride,
                        sourceRowBytes);
                }
            }
        }

        private unsafe void WriteBgraColorSettings(
            FrameResource frameResource,
            VideoFrameColorSettings settings,
            bool denoiseEnabled,
            double denoiseStrength,
            int width,
            int height)
        {
            var mappedData = (float*)frameResource.BgraColorSettingsPointer;
            if (mappedData == null)
            {
                throw new InvalidOperationException("DX12 BGRA color settings buffer is not mapped.");
            }

            var hasColor = settings.HasVisibleAdjustments;
            mappedData[0] = hasColor ? (float)(Math.Clamp(settings.Exposure, -30d, 30d) * 2.2d) : 0f;
            mappedData[1] = hasColor ? (float)(1d + Math.Clamp(settings.Contrast, -40d, 40d) / 100d) : 1f;
            mappedData[2] = hasColor ? (float)(1d + Math.Clamp(settings.Saturation, -40d, 40d) / 100d) : 1f;
            mappedData[3] = hasColor ? (float)(Math.Clamp(settings.Warmth, -40d, 40d) * 0.9d) : 0f;
            var strength = (float)Math.Clamp(denoiseStrength, 0.5d, 5d);
            mappedData[4] = denoiseEnabled ? Math.Clamp(0.06f + strength * 0.08f, 0.10f, 0.42f) : 0f;
            mappedData[5] = denoiseEnabled ? Math.Clamp(0.018f + strength * 0.006f, 0.024f, 0.052f) : 0f;
            mappedData[6] = 1f / Math.Max(1, width);
            mappedData[7] = 1f / Math.Max(1, height);
        }

        private unsafe void CopyNv12FrameToUploadBuffers(
            FrameResource frameResource,
            byte[] nv12Bytes,
            int width,
            int height,
            int stride)
        {
            var uvHeight = Math.Max(1, height / 2);
            var uvOffset = stride * height;
            var mappedYPointer = (byte*)frameResource.Nv12YUploadPointer;
            var mappedUvPointer = (byte*)frameResource.Nv12UvUploadPointer;
            if (mappedYPointer == null || mappedUvPointer == null)
            {
                throw new InvalidOperationException("DX12 NV12 upload buffers are not mapped.");
            }

            fixed (byte* sourceStart = nv12Bytes)
            {
                var yDestinationStart = mappedYPointer + (nint)_nv12YFootprint.Offset;
                var yDestinationStride = (nint)_nv12YFootprint.Footprint.RowPitch;
                for (var row = 0; row < height; row++)
                {
                    Buffer.MemoryCopy(
                        sourceStart + row * stride,
                        yDestinationStart + row * yDestinationStride,
                        yDestinationStride,
                        width);
                }

                var uvDestinationStart = mappedUvPointer + (nint)_nv12UvFootprint.Offset;
                var uvDestinationStride = (nint)_nv12UvFootprint.Footprint.RowPitch;
                for (var row = 0; row < uvHeight; row++)
                {
                    Buffer.MemoryCopy(
                        sourceStart + uvOffset + row * stride,
                        uvDestinationStart + row * uvDestinationStride,
                        uvDestinationStride,
                        width);
                }
            }
        }

        private void SetNv12ShaderConstants(
            int width,
            int height,
            VideoFrameColorSettings colorSettings,
            bool denoiseEnabled,
            double denoiseStrength)
        {
            var hasColor = colorSettings.HasVisibleAdjustments;
            _commandList.SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(hasColor ? (float)(Math.Clamp(colorSettings.Exposure, -30d, 30d) * 2.2d) : 0f), 0);
            _commandList.SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(hasColor ? (float)(1d + Math.Clamp(colorSettings.Contrast, -40d, 40d) / 100d) : 1f), 1);
            _commandList.SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(hasColor ? (float)(1d + Math.Clamp(colorSettings.Saturation, -40d, 40d) / 100d) : 1f), 2);
            _commandList.SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(hasColor ? (float)(Math.Clamp(colorSettings.Warmth, -40d, 40d) * 0.9d) : 0f), 3);
            var strength = (float)Math.Clamp(denoiseStrength, 0.5d, 5d);
            var amount = denoiseEnabled ? Math.Clamp(0.08f + strength * 0.11f, 0.14f, 0.58f) : 0f;
            var edgeThreshold = denoiseEnabled ? Math.Clamp(0.018f + strength * 0.006f, 0.024f, 0.052f) : 0f;
            _commandList.SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(amount), 4);
            _commandList.SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(edgeThreshold), 5);
            _commandList.SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(1f / Math.Max(1, width)), 6);
            _commandList.SetGraphicsRoot32BitConstant(1, BitConverter.SingleToUInt32Bits(1f / Math.Max(1, height)), 7);
        }

        private void TryCreatePreviewShaderPipeline()
        {
            try
            {
                var vertexShader = CompileShader("VSMain", "vs_5_0");
                var pixelShader = CompileShader("PSMain", "ps_5_0");
                var textureRanges = new[]
                {
                    new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1, 0)
                };
                var colorRanges = new[]
                {
                    new DescriptorRange(DescriptorRangeType.ConstantBufferView, 1, 0)
                };
                var parameters = new[]
                {
                    new RootParameter(new RootDescriptorTable(textureRanges), ShaderVisibility.Pixel),
                    new RootParameter(new RootDescriptorTable(colorRanges), ShaderVisibility.Pixel)
                };
                var samplers = new[]
                {
                    new StaticSamplerDescription(
                        0,
                        Filter.MinMagMipLinear,
                        TextureAddressMode.Clamp,
                        TextureAddressMode.Clamp,
                        TextureAddressMode.Clamp,
                        0,
                        0,
                        ComparisonFunction.Never,
                        StaticBorderColor.TransparentBlack,
                        0,
                        float.MaxValue,
                        ShaderVisibility.Pixel,
                        0)
                };
                var rootDescription = new RootSignatureDescription(
                    RootSignatureFlags.AllowInputAssemblerInputLayout,
                    parameters,
                    samplers);
                _previewRootSignature = _device.CreateRootSignature(in rootDescription, RootSignatureVersion.Version1);

                var pipelineDescription = new GraphicsPipelineStateDescription
                {
                    RootSignature = _previewRootSignature,
                    VertexShader = vertexShader,
                    PixelShader = pixelShader,
                    BlendState = BlendDescription.Opaque,
                    RasterizerState = RasterizerDescription.CullNone,
                    DepthStencilState = DepthStencilDescription.None,
                    SampleMask = uint.MaxValue,
                    PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                    RenderTargetFormats = [Format.B8G8R8A8_UNorm],
                    SampleDescription = new SampleDescription(1, 0)
                };
                _previewPipelineState = _device.CreateGraphicsPipelineState<ID3D12PipelineState>(pipelineDescription);
            }
            catch
            {
                _shaderPreviewUnavailable = true;
            }
        }

        private void TryCreateNv12PreviewShaderPipeline()
        {
            try
            {
                var vertexShader = CompileShader(Nv12PreviewShaderSource, "VSMain", "vs_5_0");
                var pixelShader = CompileShader(Nv12PreviewShaderSource, "PSMain", "ps_5_0");
                var ranges = new[]
                {
                    new DescriptorRange(DescriptorRangeType.ShaderResourceView, 2, 0)
                };
                var parameters = new[]
                {
                    new RootParameter(new RootDescriptorTable(ranges), ShaderVisibility.Pixel),
                    new RootParameter(new RootConstants(0, 0, 8), ShaderVisibility.Pixel)
                };
                var samplers = new[]
                {
                    new StaticSamplerDescription(
                        0,
                        Filter.MinMagMipLinear,
                        TextureAddressMode.Clamp,
                        TextureAddressMode.Clamp,
                        TextureAddressMode.Clamp,
                        0,
                        0,
                        ComparisonFunction.Never,
                        StaticBorderColor.TransparentBlack,
                        0,
                        float.MaxValue,
                        ShaderVisibility.Pixel,
                        0)
                };
                var rootDescription = new RootSignatureDescription(
                    RootSignatureFlags.AllowInputAssemblerInputLayout,
                    parameters,
                    samplers);
                _nv12PreviewRootSignature = _device.CreateRootSignature(in rootDescription, RootSignatureVersion.Version1);

                var pipelineDescription = new GraphicsPipelineStateDescription
                {
                    RootSignature = _nv12PreviewRootSignature,
                    VertexShader = vertexShader,
                    PixelShader = pixelShader,
                    BlendState = BlendDescription.Opaque,
                    RasterizerState = RasterizerDescription.CullNone,
                    DepthStencilState = DepthStencilDescription.None,
                    SampleMask = uint.MaxValue,
                    PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                    RenderTargetFormats = [Format.B8G8R8A8_UNorm],
                    SampleDescription = new SampleDescription(1, 0)
                };
                _nv12PreviewPipelineState = _device.CreateGraphicsPipelineState<ID3D12PipelineState>(pipelineDescription);
            }
            catch (Exception ex)
            {
                _nv12PreviewUnavailable = true;
                _nv12PreviewFailureReason = $"NV12 shader pipeline creation failed: {ex.Message}";
            }
        }

        private static byte[] CompileShader(string entryPoint, string profile)
        {
            return CompileShader(PreviewShaderSource, entryPoint, profile);
        }

        private static byte[] CompileShader(string shaderSource, string entryPoint, string profile)
        {
            return Compiler.Compile(
                    shaderSource,
                    entryPoint,
                    "AvatarBuilderPreview.hlsl",
                    profile,
                    ShaderFlags.OptimizationLevel3,
                    EffectFlags.None)
                .ToArray();
        }

        private const string PreviewShaderSource = """
            Texture2D<float4> CameraFrame : register(t0);
            SamplerState CameraSampler : register(s0);

            cbuffer ColorSettings : register(b0)
            {
                float ExposureOffset;
                float Contrast;
                float Saturation;
                float Warmth;
                float DenoiseAmount;
                float DenoiseEdgeThreshold;
                float TexelWidth;
                float TexelHeight;
            };

            struct VertexOutput
            {
                float4 Position : SV_POSITION;
                float2 TexCoord : TEXCOORD0;
            };

            VertexOutput VSMain(uint vertexId : SV_VertexID)
            {
                float2 positions[3] =
                {
                    float2(-1.0, -1.0),
                    float2(-1.0, 3.0),
                    float2(3.0, -1.0)
                };
                float2 texCoords[3] =
                {
                    float2(0.0, 1.0),
                    float2(0.0, -1.0),
                    float2(2.0, 1.0)
                };

                VertexOutput output;
                output.Position = float4(positions[vertexId], 0.0, 1.0);
                output.TexCoord = texCoords[vertexId];
                return output;
            }

            float3 ApplyColorPolish(float3 rgb)
            {
                float3 rgb255 = rgb * 255.0;
                rgb255.r = ((rgb255.r + ExposureOffset + Warmth - 128.0) * Contrast) + 128.0;
                rgb255.g = ((rgb255.g + ExposureOffset - 128.0) * Contrast) + 128.0;
                rgb255.b = ((rgb255.b + ExposureOffset - Warmth - 128.0) * Contrast) + 128.0;

                float luma = dot(rgb255, float3(0.2126, 0.7152, 0.0722));
                rgb255 = luma + (rgb255 - luma) * Saturation;
                return saturate(rgb255 / 255.0);
            }

            float Luma(float3 rgb)
            {
                return dot(rgb, float3(0.2126, 0.7152, 0.0722));
            }

            float EdgeAwareWeight(float sampleY, float centerY)
            {
                return saturate(1.0 - abs(sampleY - centerY) / max(DenoiseEdgeThreshold, 0.0001));
            }

            float3 ApplyBgraDenoise(float3 centerRgb, float2 texCoord)
            {
                if (DenoiseAmount <= 0.001)
                {
                    return centerRgb;
                }

                float2 xOffset = float2(TexelWidth, 0.0);
                float2 yOffset = float2(0.0, TexelHeight);
                float3 leftRgb = CameraFrame.Sample(CameraSampler, texCoord - xOffset).rgb;
                float3 rightRgb = CameraFrame.Sample(CameraSampler, texCoord + xOffset).rgb;
                float3 upRgb = CameraFrame.Sample(CameraSampler, texCoord - yOffset).rgb;
                float3 downRgb = CameraFrame.Sample(CameraSampler, texCoord + yOffset).rgb;

                float centerY = Luma(centerRgb);
                float centerWeight = 2.0;
                float leftWeight = EdgeAwareWeight(Luma(leftRgb), centerY);
                float rightWeight = EdgeAwareWeight(Luma(rightRgb), centerY);
                float upWeight = EdgeAwareWeight(Luma(upRgb), centerY);
                float downWeight = EdgeAwareWeight(Luma(downRgb), centerY);
                float totalWeight = centerWeight + leftWeight + rightWeight + upWeight + downWeight;
                float3 smoothedRgb = (
                    centerRgb * centerWeight
                    + leftRgb * leftWeight
                    + rightRgb * rightWeight
                    + upRgb * upWeight
                    + downRgb * downWeight) / max(totalWeight, 0.0001);
                return lerp(centerRgb, smoothedRgb, DenoiseAmount);
            }

            float4 PSMain(VertexOutput input) : SV_TARGET
            {
                float4 color = CameraFrame.Sample(CameraSampler, input.TexCoord);
                float3 denoised = ApplyBgraDenoise(color.rgb, input.TexCoord);
                return float4(ApplyColorPolish(denoised), 1.0);
            }
            """;

        private const string Nv12PreviewShaderSource = """
            Texture2D<float> CameraLuma : register(t0);
            Texture2D<float2> CameraChroma : register(t1);
            SamplerState CameraSampler : register(s0);

            cbuffer Nv12PreviewSettings : register(b0)
            {
                float ExposureOffset;
                float Contrast;
                float Saturation;
                float Warmth;
                float DenoiseAmount;
                float DenoiseEdgeThreshold;
                float TexelWidth;
                float TexelHeight;
            };

            struct VertexOutput
            {
                float4 Position : SV_POSITION;
                float2 TexCoord : TEXCOORD0;
            };

            VertexOutput VSMain(uint vertexId : SV_VertexID)
            {
                float2 positions[3] =
                {
                    float2(-1.0, -1.0),
                    float2(-1.0, 3.0),
                    float2(3.0, -1.0)
                };
                float2 texCoords[3] =
                {
                    float2(0.0, 1.0),
                    float2(0.0, -1.0),
                    float2(2.0, 1.0)
                };

                VertexOutput output;
                output.Position = float4(positions[vertexId], 0.0, 1.0);
                output.TexCoord = texCoords[vertexId];
                return output;
            }

            float NormalizeLuma(float rawY)
            {
                return saturate((rawY - (16.0 / 255.0)) * (255.0 / 219.0));
            }

            float EdgeAwareWeight(float sampleY, float centerY)
            {
                return saturate(1.0 - abs(sampleY - centerY) / max(DenoiseEdgeThreshold, 0.0001));
            }

            float SampleNormalizedLuma(float2 texCoord)
            {
                return NormalizeLuma(CameraLuma.Sample(CameraSampler, texCoord));
            }

            float ApplyLumaDenoise(float centerY, float2 texCoord)
            {
                if (DenoiseAmount <= 0.001)
                {
                    return centerY;
                }

                float2 xOffset = float2(TexelWidth, 0.0);
                float2 yOffset = float2(0.0, TexelHeight);
                float leftY = SampleNormalizedLuma(texCoord - xOffset);
                float rightY = SampleNormalizedLuma(texCoord + xOffset);
                float upY = SampleNormalizedLuma(texCoord - yOffset);
                float downY = SampleNormalizedLuma(texCoord + yOffset);

                float centerWeight = 2.0;
                float leftWeight = EdgeAwareWeight(leftY, centerY);
                float rightWeight = EdgeAwareWeight(rightY, centerY);
                float upWeight = EdgeAwareWeight(upY, centerY);
                float downWeight = EdgeAwareWeight(downY, centerY);
                float totalWeight = centerWeight + leftWeight + rightWeight + upWeight + downWeight;
                float smoothedY = (
                    centerY * centerWeight
                    + leftY * leftWeight
                    + rightY * rightWeight
                    + upY * upWeight
                    + downY * downWeight) / max(totalWeight, 0.0001);
                return lerp(centerY, smoothedY, DenoiseAmount);
            }

            float3 ApplyColorPolish(float3 rgb)
            {
                float3 rgb255 = rgb * 255.0;
                rgb255.r = ((rgb255.r + ExposureOffset + Warmth - 128.0) * Contrast) + 128.0;
                rgb255.g = ((rgb255.g + ExposureOffset - 128.0) * Contrast) + 128.0;
                rgb255.b = ((rgb255.b + ExposureOffset - Warmth - 128.0) * Contrast) + 128.0;

                float luma = dot(rgb255, float3(0.2126, 0.7152, 0.0722));
                rgb255 = luma + (rgb255 - luma) * Saturation;
                return saturate(rgb255 / 255.0);
            }

            float4 PSMain(VertexOutput input) : SV_TARGET
            {
                float y = NormalizeLuma(CameraLuma.Sample(CameraSampler, input.TexCoord));
                y = ApplyLumaDenoise(y, input.TexCoord);
                float2 uv = CameraChroma.Sample(CameraSampler, input.TexCoord) - float2(0.5, 0.5);
                float3 rgb = float3(
                    y + 1.5748 * uv.y,
                    y - 0.1873 * uv.x - 0.4681 * uv.y,
                    y + 1.8556 * uv.x);
                return float4(ApplyColorPolish(saturate(rgb)), 1.0);
            }
            """;

        public void Resize(int width, int height)
        {
            if (_disposed || width == _width && height == _height)
            {
                return;
            }

            WaitForGpu();
            ReleaseRenderTargets();
            _swapChain.ResizeBuffers(FrameCount, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
            _width = width;
            _height = height;
            CreateRenderTargetViews();
            RenderProofFrame(0);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            WaitForGpu();
            _disposed = true;
            ReleaseRenderTargets();
            _swapChain.Dispose();
            _rtvHeap.Dispose();
            _srvHeap.Dispose();
            _cameraTexture?.Dispose();
            _nv12YTexture?.Dispose();
            _nv12UvTexture?.Dispose();
            _previewPipelineState?.Dispose();
            _previewRootSignature?.Dispose();
            _nv12PreviewPipelineState?.Dispose();
            _nv12PreviewRootSignature?.Dispose();
            _fence.Dispose();
            _fenceEvent.Dispose();
            _commandList.Dispose();
            foreach (var frameResource in _frameResources)
            {
                frameResource.Dispose();
            }

            _factory.Dispose();
            _commandQueue.Dispose();
            _device.Dispose();
        }

        private void CreateRenderTargetViews()
        {
            var handle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
            for (var i = 0; i < FrameCount; i++)
            {
                _renderTargets[i] = _swapChain.GetBuffer<ID3D12Resource>((uint)i);
                _device.CreateRenderTargetView(_renderTargets[i], null, handle);
                handle += _rtvDescriptorSize;
            }
        }

        private CpuDescriptorHandle GetRtvHandle(int frameIndex)
        {
            return _rtvHeap.GetCPUDescriptorHandleForHeapStart() + frameIndex * _rtvDescriptorSize;
        }

        private CpuDescriptorHandle GetSrvCpuHandle(int descriptorIndex)
        {
            return _srvHeap.GetCPUDescriptorHandleForHeapStart() + descriptorIndex * _srvDescriptorSize;
        }

        private GpuDescriptorHandle GetSrvGpuHandle(int descriptorIndex)
        {
            return _srvHeap.GetGPUDescriptorHandleForHeapStart() + descriptorIndex * _srvDescriptorSize;
        }

        private void ReleaseRenderTargets()
        {
            for (var i = 0; i < _renderTargets.Length; i++)
            {
                _renderTargets[i]?.Dispose();
                _renderTargets[i] = null;
            }
        }

        private FrameResource BeginFrame(out int frameIndex)
        {
            frameIndex = (int)_swapChain.CurrentBackBufferIndex;
            var frameResource = _frameResources[frameIndex];
            WaitForFrameResource(frameResource);
            frameResource.CommandAllocator.Reset();
            _commandList.Reset(frameResource.CommandAllocator);
            return frameResource;
        }

        private void ExecuteAndPresent(FrameResource frameResource)
        {
            _commandList.Close();
            _commandQueue.ExecuteCommandList(_commandList);
            _swapChain.Present(0, PresentFlags.None);
            SignalFrameSubmitted(frameResource);
        }

        private void WaitForGpu()
        {
            if (_disposed)
            {
                return;
            }

            _fenceValue++;
            _commandQueue.Signal(_fence, _fenceValue);
            if (_fence.CompletedValue >= _fenceValue)
            {
                ClearFrameFenceValues();
                return;
            }

            _fence.SetEventOnCompletion(_fenceValue, _fenceEvent);
            _fenceEvent.WaitOne();
            ClearFrameFenceValues();
        }

        private void SignalFrameSubmitted(FrameResource frameResource)
        {
            if (_disposed)
            {
                return;
            }

            _fenceValue++;
            _commandQueue.Signal(_fence, _fenceValue);
            frameResource.FenceValue = _fenceValue;
        }

        private void WaitForFrameResource(FrameResource frameResource)
        {
            if (_disposed || frameResource.FenceValue == 0)
            {
                return;
            }

            if (_fence.CompletedValue < frameResource.FenceValue)
            {
                _fence.SetEventOnCompletion(frameResource.FenceValue, _fenceEvent);
                _fenceEvent.WaitOne();
            }

            frameResource.FenceValue = 0;
        }

        private void ClearFrameFenceValues()
        {
            foreach (var frameResource in _frameResources)
            {
                frameResource.FenceValue = 0;
            }
        }

        private sealed class FrameResource : IDisposable
        {
            public FrameResource(ID3D12CommandAllocator commandAllocator)
            {
                CommandAllocator = commandAllocator;
            }

            public ID3D12CommandAllocator CommandAllocator { get; }

            public ID3D12Resource? CameraUploadBuffer { get; private set; }

            public IntPtr CameraUploadPointer { get; private set; }

            public ID3D12Resource? BgraColorSettingsBuffer { get; private set; }

            public IntPtr BgraColorSettingsPointer { get; private set; }

            public ID3D12Resource? Nv12YUploadBuffer { get; private set; }

            public IntPtr Nv12YUploadPointer { get; private set; }

            public ID3D12Resource? Nv12UvUploadBuffer { get; private set; }

            public IntPtr Nv12UvUploadPointer { get; private set; }

            public ulong FenceValue { get; set; }

            public unsafe void CreateCameraUploadBuffer(ID3D12Device device, ulong uploadBytes)
            {
                ReleaseCameraUploadBuffer();
                CameraUploadBuffer = CreateMappedUploadBuffer(device, uploadBytes, out var mappedPointer);
                CameraUploadPointer = mappedPointer;
            }

            public unsafe void CreateBgraColorSettingsBuffer(ID3D12Device device, ulong uploadBytes)
            {
                ReleaseBgraColorSettingsBuffer();
                BgraColorSettingsBuffer = CreateMappedUploadBuffer(device, uploadBytes, out var mappedPointer);
                BgraColorSettingsPointer = mappedPointer;
            }

            public unsafe void CreateNv12UploadBuffers(ID3D12Device device, ulong yUploadBytes, ulong uvUploadBytes)
            {
                ReleaseNv12UploadBuffers();
                Nv12YUploadBuffer = CreateMappedUploadBuffer(device, yUploadBytes, out var mappedYPointer);
                Nv12YUploadPointer = mappedYPointer;
                Nv12UvUploadBuffer = CreateMappedUploadBuffer(device, uvUploadBytes, out var mappedUvPointer);
                Nv12UvUploadPointer = mappedUvPointer;
            }

            public void ReleaseCameraUploadBuffer()
            {
                if (CameraUploadBuffer is not null)
                {
                    CameraUploadBuffer.Unmap(0, null);
                    CameraUploadBuffer.Dispose();
                    CameraUploadBuffer = null;
                }

                CameraUploadPointer = IntPtr.Zero;
            }

            public void ReleaseBgraColorSettingsBuffer()
            {
                if (BgraColorSettingsBuffer is not null)
                {
                    BgraColorSettingsBuffer.Unmap(0, null);
                    BgraColorSettingsBuffer.Dispose();
                    BgraColorSettingsBuffer = null;
                }

                BgraColorSettingsPointer = IntPtr.Zero;
            }

            public void ReleaseNv12UploadBuffers()
            {
                if (Nv12YUploadBuffer is not null)
                {
                    Nv12YUploadBuffer.Unmap(0, null);
                    Nv12YUploadBuffer.Dispose();
                    Nv12YUploadBuffer = null;
                }

                if (Nv12UvUploadBuffer is not null)
                {
                    Nv12UvUploadBuffer.Unmap(0, null);
                    Nv12UvUploadBuffer.Dispose();
                    Nv12UvUploadBuffer = null;
                }

                Nv12YUploadPointer = IntPtr.Zero;
                Nv12UvUploadPointer = IntPtr.Zero;
            }

            public void Dispose()
            {
                ReleaseCameraUploadBuffer();
                ReleaseBgraColorSettingsBuffer();
                ReleaseNv12UploadBuffers();
                CommandAllocator.Dispose();
            }

            private static unsafe ID3D12Resource CreateMappedUploadBuffer(
                ID3D12Device device,
                ulong uploadBytes,
                out IntPtr mappedPointer)
            {
                var uploadBuffer = device.CreateCommittedResource<ID3D12Resource>(
                    new HeapProperties(HeapType.Upload),
                    HeapFlags.None,
                    ResourceDescription.Buffer(uploadBytes, ResourceFlags.None, 0),
                    ResourceStates.GenericRead);
                void* mapped = null;
                uploadBuffer.Map(0, null, &mapped).CheckError();
                mappedPointer = (IntPtr)mapped;
                return uploadBuffer;
            }
        }
    }
}

