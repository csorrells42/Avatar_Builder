using System.Numerics;
using SharpGen.Runtime;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D11on12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct2D1.D2D1;
using static Vortice.Direct3D11on12.Apis;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

internal sealed class Direct2DTrackingOverlayRenderer : IDisposable
{
    private const float FaceBoxThickness = 0.75f;
    private const float FaceContourThickness = 0.5f;
    private const float FeatureContourThickness = 0.75f;
    private const float FaceMeshThickness = 0.42f;
    private const float FaceMeshFeatureThickness = 1.75f;
    private const float FaceMeshPointDiameter = 2.0f;
    private const float FaceMeshFeaturePointDiameter = 3.2f;

    private static readonly OverlayColor FaceBoxColor = new(0.96f, 0.83f, 0.37f, 0.96f);
    private static readonly OverlayColor FaceContourColor = new(0.73f, 0.84f, 0.94f, 0.96f);
    private static readonly OverlayColor EyeContourColor = new(0.48f, 0.85f, 1.00f, 0.96f);
    private static readonly OverlayColor BrowContourColor = new(0.77f, 0.97f, 0.64f, 0.96f);
    private static readonly OverlayColor LipContourColor = new(0.96f, 0.52f, 0.69f, 0.96f);
    private static readonly OverlayColor InferredContourColor = new(0.93f, 0.68f, 0.29f, 0.96f);
    private static readonly OverlayColor FaceMeshColor = FromBytes(0x2f, 0x6c, 0x8f, 0x58);
    private static readonly OverlayColor FaceMeshPointColor = FromBytes(0xdc, 0xef, 0xff, 0xb8);
    private static readonly OverlayColor FaceMeshEyeColor = FromBytes(0x8f, 0xf2, 0xc5, 0xf2);
    private static readonly OverlayColor FaceMeshBrowColor = FromBytes(0xc9, 0xf7, 0xa3, 0xf2);
    private static readonly OverlayColor FaceMeshMouthColor = FromBytes(0xff, 0x9f, 0xbd, 0xf2);
    private static readonly OverlayColor FaceMeshJawColor = FromBytes(0xff, 0xd1, 0x66, 0xf2);
    private static readonly OverlayColor FaceMeshNoseColor = FromBytes(0xd9, 0xe8, 0xff, 0xf2);
    private static readonly OverlayColor FaceMeshOutlineColor = FromBytes(0x65, 0xc8, 0xff, 0xf2);

    private readonly ID3D11Device _direct3D11Device;
    private readonly ID3D11DeviceContext _direct3D11Context;
    private readonly ID3D11On12Device _direct3D11On12Device;
    private readonly IDXGIDevice _dxgiDevice;
    private readonly ID2D1Factory1 _direct2DFactory;
    private readonly ID2D1Device _direct2DDevice;
    private readonly ID2D1DeviceContext _direct2DContext;
    private readonly ID3D11Resource?[] _wrappedBackBuffers;
    private readonly ID3D11Resource[][] _wrappedBackBufferViews;
    private readonly ID2D1Bitmap1?[] _direct2DTargets;
    private readonly Dictionary<OverlayColor, ID2D1SolidColorBrush> _brushes = [];
    private ID2D1CommandList? _overlayCommandList;
    private PreviewTrackingOverlay? _recordedOverlay;
    private int _recordedWidth;
    private int _recordedHeight;
    private bool _disabled;
    private bool _disposed;

    private Direct2DTrackingOverlayRenderer(
        ID3D12Device direct3D12Device,
        ID3D12CommandQueue commandQueue,
        int frameCount)
    {
        var commandQueues = new IUnknown[] { commandQueue };
        D3D11On12CreateDevice(
                direct3D12Device,
                DeviceCreationFlags.BgraSupport,
                [Vortice.Direct3D.FeatureLevel.Level_11_0],
                commandQueues,
                0,
                out var direct3D11Device,
                out var direct3D11Context,
                out _)
            .CheckError();

        _direct3D11Device = direct3D11Device
            ?? throw new InvalidOperationException("D3D11On12 did not return a device.");
        _direct3D11Context = direct3D11Context
            ?? throw new InvalidOperationException("D3D11On12 did not return an immediate context.");
        _direct3D11On12Device = _direct3D11Device.QueryInterface<ID3D11On12Device>();
        _dxgiDevice = _direct3D11Device.QueryInterface<IDXGIDevice>();
        _direct2DFactory = D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded, DebugLevel.None);
        _direct2DDevice = _direct2DFactory.CreateDevice(_dxgiDevice);
        _direct2DContext = _direct2DDevice.CreateDeviceContext(DeviceContextOptions.None);
        _direct2DContext.AntialiasMode = AntialiasMode.PerPrimitive;
        _direct2DContext.PrimitiveBlend = PrimitiveBlend.SourceOver;
        _direct2DContext.UnitMode = UnitMode.Pixels;

        var safeFrameCount = Math.Max(1, frameCount);
        _wrappedBackBuffers = new ID3D11Resource?[safeFrameCount];
        _wrappedBackBufferViews = new ID3D11Resource[safeFrameCount][];
        _direct2DTargets = new ID2D1Bitmap1?[safeFrameCount];
        for (var index = 0; index < safeFrameCount; index++)
        {
            _wrappedBackBufferViews[index] = new ID3D11Resource[1];
        }
    }

    public static Direct2DTrackingOverlayRenderer? TryCreate(
        ID3D12Device direct3D12Device,
        ID3D12CommandQueue commandQueue,
        int frameCount)
    {
        try
        {
            return new Direct2DTrackingOverlayRenderer(direct3D12Device, commandQueue, frameCount);
        }
        catch
        {
            return null;
        }
    }

    public void AttachBackBuffers(IReadOnlyList<ID3D12Resource?> backBuffers)
    {
        ThrowIfDisposed();
        ReleaseBackBuffers();
        _disabled = false;

        var count = Math.Min(backBuffers.Count, _wrappedBackBuffers.Length);
        try
        {
            var resourceFlags = new Vortice.Direct3D11on12.ResourceFlags
            {
                BindFlags = BindFlags.RenderTarget
            };
            var bitmapProperties = new BitmapProperties1(
                new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                96f,
                96f,
                BitmapOptions.Target | BitmapOptions.CannotDraw);

            for (var index = 0; index < count; index++)
            {
                var backBuffer = backBuffers[index]
                    ?? throw new InvalidOperationException($"DX12 back buffer {index} is unavailable.");
                var wrapped = _direct3D11On12Device.CreateWrappedResource<ID3D11Resource>(
                    backBuffer,
                    resourceFlags,
                    ResourceStates.RenderTarget,
                    ResourceStates.Present);
                _wrappedBackBuffers[index] = wrapped;
                _wrappedBackBufferViews[index][0] = wrapped;

                using var surface = wrapped.QueryInterface<IDXGISurface>();
                _direct2DTargets[index] = _direct2DContext.CreateBitmapFromDxgiSurface(
                    surface,
                    bitmapProperties);
            }
        }
        catch
        {
            ReleaseBackBuffers();
            throw;
        }
    }

    public void ReleaseBackBuffers()
    {
        if (_disposed)
        {
            return;
        }

        _direct2DContext.Target = null;
        ClearOverlayCommandList();
        for (var index = 0; index < _direct2DTargets.Length; index++)
        {
            _direct2DTargets[index]?.Dispose();
            _direct2DTargets[index] = null;
            _wrappedBackBuffers[index]?.Dispose();
            _wrappedBackBuffers[index] = null;
            _wrappedBackBufferViews[index][0] = null!;
        }

        _direct3D11Context.Flush();
    }

    public bool PrepareDraw(
        int frameIndex,
        PreviewTrackingOverlay overlay,
        int width,
        int height)
    {
        if (_disposed
            || _disabled
            || !overlay.HasContent
            || width <= 1
            || height <= 1
            || (uint)frameIndex >= (uint)_wrappedBackBuffers.Length
            || _wrappedBackBuffers[frameIndex] is null
            || _direct2DTargets[frameIndex] is null)
        {
            return false;
        }

        try
        {
            EnsureOverlayCommandList(overlay, width, height);
            return _overlayCommandList is not null;
        }
        catch
        {
            _disabled = true;
            ClearOverlayCommandList();
            return false;
        }
    }

    public void Draw(int frameIndex, int width, int height, PreviewTrackingOverlay overlay)
    {
        var commandList = _overlayCommandList;
        if (_disposed
            || commandList is null
            || !ReferenceEquals(_recordedOverlay, overlay)
            || _recordedWidth != width
            || _recordedHeight != height
            || (uint)frameIndex >= (uint)_wrappedBackBuffers.Length
            || _wrappedBackBuffers[frameIndex] is null
            || _direct2DTargets[frameIndex] is null)
        {
            return;
        }

        var resources = _wrappedBackBufferViews[frameIndex];
        var target = _direct2DTargets[frameIndex]!;
        var acquired = false;
        var drawing = false;
        try
        {
            _direct3D11On12Device.AcquireWrappedResources(resources);
            acquired = true;
            _direct2DContext.Target = target;
            _direct2DContext.BeginDraw();
            drawing = true;
            _direct2DContext.DrawImage(
                commandList,
                null,
                null,
                Vortice.Direct2D1.InterpolationMode.Linear,
                CompositeMode.SourceOver);

            var result = _direct2DContext.EndDraw();
            drawing = false;
            result.CheckError();
        }
        catch when (acquired)
        {
            _disabled = true;
            // Releasing the wrapped resource below still performs the required
            // RenderTarget-to-Present transition. A transient D2D draw failure
            // must not take the camera presentation path down with it.
        }
        finally
        {
            if (drawing)
            {
                _direct2DContext.EndDraw();
            }

            _direct2DContext.Target = null;
            if (acquired)
            {
                _direct3D11On12Device.ReleaseWrappedResources(resources);
                _direct3D11Context.Flush();
            }
        }
    }

    public void ResetOverlayCache()
    {
        if (!_disposed && !_disabled)
        {
            ClearOverlayCommandList();
        }
    }

    private void EnsureOverlayCommandList(PreviewTrackingOverlay overlay, int width, int height)
    {
        if (_overlayCommandList is not null
            && ReferenceEquals(_recordedOverlay, overlay)
            && _recordedWidth == width
            && _recordedHeight == height)
        {
            return;
        }

        ID2D1CommandList? nextCommandList = null;
        var drawing = false;
        try
        {
            nextCommandList = _direct2DContext.CreateCommandList();
            _direct2DContext.Target = nextCommandList;
            _direct2DContext.BeginDraw();
            drawing = true;

            DrawMesh(overlay.FaceMesh, width, height);
            DrawRectangle(overlay.FaceBox, FaceBoxColor, FaceBoxThickness, width, height);
            DrawPolyline(overlay.FaceContour, FaceContourColor, FaceContourThickness, width, height);
            DrawPolyline(overlay.JawContour, FaceContourColor, FeatureContourThickness, width, height);
            DrawPolyline(overlay.LeftEyeContour, EyeContourColor, FeatureContourThickness, width, height);
            DrawPolyline(overlay.RightEyeContour, EyeContourColor, FeatureContourThickness, width, height);
            DrawPolyline(overlay.LeftBrowContour, BrowContourColor, FeatureContourThickness, width, height);
            DrawPolyline(overlay.RightBrowContour, BrowContourColor, FeatureContourThickness, width, height);
            DrawPolyline(overlay.OuterLipContour, LipContourColor, FeatureContourThickness, width, height);
            DrawPolyline(overlay.InnerLipContour, LipContourColor, FaceContourThickness, width, height);

            var drawResult = _direct2DContext.EndDraw();
            drawing = false;
            drawResult.CheckError();
            nextCommandList.Close().CheckError();

            _overlayCommandList?.Dispose();
            _overlayCommandList = nextCommandList;
            nextCommandList = null;
            _recordedOverlay = overlay;
            _recordedWidth = width;
            _recordedHeight = height;
        }
        finally
        {
            if (drawing)
            {
                _direct2DContext.EndDraw();
            }

            _direct2DContext.Target = null;
            nextCommandList?.Dispose();
        }
    }

    private void ClearOverlayCommandList()
    {
        _overlayCommandList?.Dispose();
        _overlayCommandList = null;
        _recordedOverlay = null;
        _recordedWidth = 0;
        _recordedHeight = 0;
    }

    private void DrawMesh(PreviewOverlayMesh? mesh, int width, int height)
    {
        if (mesh is null || mesh.Points.Count < 2)
        {
            return;
        }

        var meshBrush = BrushFor(FaceMeshColor);
        foreach (var edge in mesh.Edges)
        {
            DrawIndexedLine(mesh.Points, edge.FromIndex, edge.ToIndex, meshBrush, FaceMeshThickness, width, height);
        }

        foreach (var path in mesh.FeaturePaths)
        {
            DrawIndexedPath(mesh.Points, path, width, height);
        }

        var pointBrush = BrushFor(FaceMeshPointColor);
        for (var index = 0; index < mesh.Points.Count; index++)
        {
            if (!TryProject(mesh.Points[index], width, height, out var center))
            {
                continue;
            }

            var isFeaturePoint = index < mesh.FeaturePointMask.Count && mesh.FeaturePointMask[index];
            var diameter = isFeaturePoint ? FaceMeshFeaturePointDiameter : FaceMeshPointDiameter;
            var radius = diameter * 0.5f;
            _direct2DContext.FillEllipse(new Ellipse(center, radius, radius), pointBrush);
        }
    }

    private void DrawIndexedPath(
        IReadOnlyList<PreviewOverlayPoint> points,
        PreviewOverlayIndexedPath path,
        int width,
        int height)
    {
        if (path.PointIndices.Count < 2)
        {
            return;
        }

        var brush = BrushFor(ColorForMeshRole(path.Role));
        for (var index = 1; index < path.PointIndices.Count; index++)
        {
            DrawIndexedLine(
                points,
                path.PointIndices[index - 1],
                path.PointIndices[index],
                brush,
                FaceMeshFeatureThickness,
                width,
                height);
        }

        if (path.Closed && path.PointIndices.Count > 2)
        {
            DrawIndexedLine(
                points,
                path.PointIndices[^1],
                path.PointIndices[0],
                brush,
                FaceMeshFeatureThickness,
                width,
                height);
        }
    }

    private void DrawIndexedLine(
        IReadOnlyList<PreviewOverlayPoint> points,
        int fromIndex,
        int toIndex,
        ID2D1Brush brush,
        float thickness,
        int width,
        int height)
    {
        if ((uint)fromIndex >= (uint)points.Count
            || (uint)toIndex >= (uint)points.Count
            || !TryProject(points[fromIndex], width, height, out var from)
            || !TryProject(points[toIndex], width, height, out var to))
        {
            return;
        }

        _direct2DContext.DrawLine(from, to, brush, thickness, null!);
    }

    private void DrawRectangle(
        PreviewOverlayRect? rectangle,
        OverlayColor color,
        float thickness,
        int width,
        int height)
    {
        if (rectangle is null)
        {
            return;
        }

        var rect = rectangle.Value.Clamp();
        var topLeft = new Vector2((float)(rect.Left * width), (float)(rect.Top * height));
        var topRight = new Vector2((float)(rect.Right * width), topLeft.Y);
        var bottomLeft = new Vector2(topLeft.X, (float)(rect.Bottom * height));
        var bottomRight = new Vector2(topRight.X, bottomLeft.Y);
        var brush = BrushFor(color);
        _direct2DContext.DrawLine(topLeft, topRight, brush, thickness, null!);
        _direct2DContext.DrawLine(topRight, bottomRight, brush, thickness, null!);
        _direct2DContext.DrawLine(bottomRight, bottomLeft, brush, thickness, null!);
        _direct2DContext.DrawLine(bottomLeft, topLeft, brush, thickness, null!);
    }

    private void DrawPolyline(
        PreviewOverlayPolyline? polyline,
        OverlayColor normalColor,
        float thickness,
        int width,
        int height)
    {
        if (polyline is null || polyline.Points.Count < 2)
        {
            return;
        }

        var brush = BrushFor(polyline.Inferred ? InferredContourColor : normalColor);
        for (var index = 1; index < polyline.Points.Count; index++)
        {
            DrawLine(polyline.Points[index - 1], polyline.Points[index], brush, thickness, width, height);
        }

        if (polyline.Closed && polyline.Points.Count > 2)
        {
            DrawLine(polyline.Points[^1], polyline.Points[0], brush, thickness, width, height);
        }
    }

    private void DrawLine(
        PreviewOverlayPoint fromPoint,
        PreviewOverlayPoint toPoint,
        ID2D1Brush brush,
        float thickness,
        int width,
        int height)
    {
        if (!TryProject(fromPoint, width, height, out var from)
            || !TryProject(toPoint, width, height, out var to))
        {
            return;
        }

        _direct2DContext.DrawLine(from, to, brush, thickness, null!);
    }

    private ID2D1SolidColorBrush BrushFor(OverlayColor color)
    {
        if (_brushes.TryGetValue(color, out var brush))
        {
            return brush;
        }

        brush = _direct2DContext.CreateSolidColorBrush(
            new Color4(color.Red, color.Green, color.Blue, color.Alpha),
            null);
        _brushes.Add(color, brush);
        return brush;
    }

    private static OverlayColor ColorForMeshRole(PreviewOverlayMeshFeatureRole role)
    {
        return role switch
        {
            PreviewOverlayMeshFeatureRole.Eye => FaceMeshEyeColor,
            PreviewOverlayMeshFeatureRole.Brow => FaceMeshBrowColor,
            PreviewOverlayMeshFeatureRole.Mouth => FaceMeshMouthColor,
            PreviewOverlayMeshFeatureRole.Jaw => FaceMeshJawColor,
            PreviewOverlayMeshFeatureRole.Nose => FaceMeshNoseColor,
            PreviewOverlayMeshFeatureRole.Face => FaceMeshOutlineColor,
            _ => FaceMeshPointColor
        };
    }

    private static bool TryProject(
        PreviewOverlayPoint point,
        int width,
        int height,
        out Vector2 projected)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            projected = default;
            return false;
        }

        projected = new Vector2(
            (float)(Math.Clamp(point.X, 0d, 1d) * width),
            (float)(Math.Clamp(point.Y, 0d, 1d) * height));
        return true;
    }

    private static OverlayColor FromBytes(byte red, byte green, byte blue, byte alpha)
    {
        const float scale = 1f / byte.MaxValue;
        return new OverlayColor(red * scale, green * scale, blue * scale, alpha * scale);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ReleaseBackBuffers();
        _disposed = true;
        ClearOverlayCommandList();
        foreach (var brush in _brushes.Values)
        {
            brush.Dispose();
        }

        _brushes.Clear();
        _direct2DContext.Dispose();
        _direct2DDevice.Dispose();
        _direct2DFactory.Dispose();
        _dxgiDevice.Dispose();
        _direct3D11On12Device.Dispose();
        _direct3D11Context.Dispose();
        _direct3D11Device.Dispose();
    }

    private readonly record struct OverlayColor(float Red, float Green, float Blue, float Alpha);
}
