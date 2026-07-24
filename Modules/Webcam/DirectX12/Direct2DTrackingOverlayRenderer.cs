using System;
using System.Collections.Generic;
using System.Numerics;
using SharpGen.Runtime;
using Vortice.DCommon;
using Vortice.DXGI;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D11on12;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

internal sealed class Direct2DTrackingOverlayRenderer : IDisposable
{
	private readonly record struct OverlayColor(float Red, float Green, float Blue, float Alpha);

	private const float FaceBoxThickness = 0.75f;

	private const float FaceContourThickness = 0.5f;

	private const float FeatureContourThickness = 0.75f;

	private const float FaceMeshThickness = 0.42f;

	private const float FaceMeshFeatureThickness = 1.75f;

	private const float FaceMeshPointDiameter = 2f;

	private const float FaceMeshFeaturePointDiameter = 3.2f;

	private const float TranslatedMeshThickness = 0.52f;

	private const float FusedMeshThickness = 0.68f;

	private const float CalibrationMeshThickness = 1.1f;

	private const float DiagnosticPointDiameter = 1.6f;

	private static readonly OverlayColor FaceBoxColor = new OverlayColor(0.96f, 0.83f, 0.37f, 0.96f);

	private static readonly OverlayColor RememberedPersonColor =
		FromBytes(84, 232, 151, 242);

	private static readonly OverlayColor LearningPersonColor =
		FromBytes(85, 184, byte.MaxValue, 226);

	private static readonly OverlayColor FaceContourColor = new OverlayColor(0.73f, 0.84f, 0.94f, 0.96f);

	private static readonly OverlayColor EyeContourColor = new OverlayColor(0.48f, 0.85f, 1f, 0.96f);

	private static readonly OverlayColor BrowContourColor = new OverlayColor(0.77f, 0.97f, 0.64f, 0.96f);

	private static readonly OverlayColor LipContourColor = new OverlayColor(0.96f, 0.52f, 0.69f, 0.96f);

	private static readonly OverlayColor InferredContourColor = new OverlayColor(0.93f, 0.68f, 0.29f, 0.96f);

	private static readonly OverlayColor FaceMeshColor = FromBytes(47, 108, 143, 88);

	private static readonly OverlayColor FaceMeshPointColor = FromBytes(220, 239, byte.MaxValue, 184);

	private static readonly OverlayColor FaceMeshEyeColor = FromBytes(143, 242, 197, 242);

	private static readonly OverlayColor FaceMeshBrowColor = FromBytes(201, 247, 163, 242);

	private static readonly OverlayColor FaceMeshMouthColor = FromBytes(byte.MaxValue, 159, 189, 242);

	private static readonly OverlayColor FaceMeshJawColor = FromBytes(byte.MaxValue, 209, 102, 242);

	private static readonly OverlayColor FaceMeshNoseColor = FromBytes(217, 232, byte.MaxValue, 242);

	private static readonly OverlayColor FaceMeshOutlineColor = FromBytes(101, 200, byte.MaxValue, 242);

	private static readonly OverlayColor TranslatedMeshColor = FromBytes(byte.MaxValue, 184, 77, 156);

	private static readonly OverlayColor TranslatedPointColor = FromBytes(byte.MaxValue, 210, 134, 200);

	private static readonly OverlayColor FusedMeshColor = FromBytes(101, 240, 154, 176);

	private static readonly OverlayColor FusedPointColor = FromBytes(157, byte.MaxValue, 187, 224);

	private static readonly OverlayColor CalibrationMeshColor = FromBytes(114, byte.MaxValue, 180, 240);

	private static readonly OverlayColor CalibrationPointColor = FromBytes(212, byte.MaxValue, 230, byte.MaxValue);

	private readonly ID3D11Device _direct3D11Device;

	private readonly ID3D11DeviceContext _direct3D11Context;

	private readonly ID3D11On12Device _direct3D11On12Device;

	private readonly IDXGIDevice _dxgiDevice;

	private readonly ID2D1Factory1 _direct2DFactory;

	private readonly ID2D1Device _direct2DDevice;

	private readonly ID2D1DeviceContext _direct2DContext;

	private readonly ID3D11Resource?[] _wrappedBackBuffers;

	private readonly ID3D11Resource?[] _wrappedBackBufferViews;

	private readonly ID2D1Bitmap1?[] _direct2DTargets;

	private readonly Dictionary<OverlayColor, ID2D1SolidColorBrush> _brushes = new Dictionary<OverlayColor, ID2D1SolidColorBrush>();

	private ID2D1CommandList? _overlayCommandList;

	private PreviewTrackingOverlay? _recordedOverlay;

	private int _recordedWidth;

	private int _recordedHeight;

	private bool _disabled;

	private bool _disposed;

	private Direct2DTrackingOverlayRenderer(ID3D12Device direct3D12Device, ID3D12CommandQueue commandQueue, int frameCount)
	{
		IUnknown[] commandQueues = new IUnknown[1] { commandQueue };
		Apis.D3D11On12CreateDevice(direct3D12Device, DeviceCreationFlags.BgraSupport, new Vortice.Direct3D.FeatureLevel[1] { Vortice.Direct3D.FeatureLevel.Level_11_0 }, commandQueues, 0u, out ID3D11Device device, out ID3D11DeviceContext immediateContext, out Vortice.Direct3D.FeatureLevel _).CheckError();
		_direct3D11Device = device ?? throw new InvalidOperationException("D3D11On12 did not return a device.");
		_direct3D11Context = immediateContext ?? throw new InvalidOperationException("D3D11On12 did not return an immediate context.");
		_direct3D11On12Device = _direct3D11Device.QueryInterface<ID3D11On12Device>();
		_dxgiDevice = _direct3D11Device.QueryInterface<IDXGIDevice>();
		_direct2DFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);
		_direct2DDevice = _direct2DFactory.CreateDevice(_dxgiDevice);
		_direct2DContext = _direct2DDevice.CreateDeviceContext(DeviceContextOptions.None);
		_direct2DContext.AntialiasMode = AntialiasMode.PerPrimitive;
		_direct2DContext.PrimitiveBlend = PrimitiveBlend.SourceOver;
		_direct2DContext.UnitMode = UnitMode.Pixels;
		int num = Math.Max(1, frameCount);
		_wrappedBackBuffers = new ID3D11Resource[num];
		_wrappedBackBufferViews = new ID3D11Resource?[num];
		_direct2DTargets = new ID2D1Bitmap1[num];
		for (int i = 0; i < num; i++)
		{
		}
	}

	public static Direct2DTrackingOverlayRenderer? TryCreate(ID3D12Device direct3D12Device, ID3D12CommandQueue commandQueue, int frameCount)
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
		int num = Math.Min(backBuffers.Count, _wrappedBackBuffers.Length);
		try
		{
			Vortice.Direct3D11on12.ResourceFlags flags = new Vortice.Direct3D11on12.ResourceFlags
			{
				BindFlags = BindFlags.RenderTarget
			};
			BitmapProperties1 value = new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw);
			for (int i = 0; i < num; i++)
			{
				ID3D12Resource d3d12Resource = backBuffers[i] ?? throw new InvalidOperationException($"DX12 back buffer {i} is unavailable.");
				ID3D11Resource iD3D11Resource = _direct3D11On12Device.CreateWrappedResource<ID3D11Resource>(d3d12Resource, flags, ResourceStates.RenderTarget, ResourceStates.Common);
				_wrappedBackBuffers[i] = iD3D11Resource;
				_wrappedBackBufferViews[i] = iD3D11Resource;
				using IDXGISurface surface = iD3D11Resource.QueryInterface<IDXGISurface>();
				_direct2DTargets[i] = _direct2DContext.CreateBitmapFromDxgiSurface(surface, value);
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
		if (!_disposed)
		{
			_direct2DContext.Target = null;
			ClearOverlayCommandList();
			for (int i = 0; i < _direct2DTargets.Length; i++)
			{
				_direct2DTargets[i]?.Dispose();
				_direct2DTargets[i] = null;
				_wrappedBackBuffers[i]?.Dispose();
				_wrappedBackBuffers[i] = null;
				_wrappedBackBufferViews[i] = null;
			}
			_direct3D11Context.Flush();
		}
	}

	public bool PrepareDraw(int frameIndex, PreviewTrackingOverlay overlay, int width, int height)
	{
		if (_disposed || _disabled || !overlay.HasContent || width <= 1 || height <= 1 || (uint)frameIndex >= (uint)_wrappedBackBuffers.Length || _wrappedBackBuffers[frameIndex] is null || _direct2DTargets[frameIndex] is null)
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
		ID2D1CommandList? overlayCommandList = _overlayCommandList;
		if (_disposed || overlayCommandList is null || !ReferenceEquals(_recordedOverlay, overlay) || _recordedWidth != width || _recordedHeight != height || (uint)frameIndex >= (uint)_wrappedBackBuffers.Length || _wrappedBackBuffers[frameIndex] is null || _wrappedBackBufferViews[frameIndex] is null || _direct2DTargets[frameIndex] is null)
		{
			return;
		}
		ID3D11Resource? wrappedBackBufferView = _wrappedBackBufferViews[frameIndex];
		if (wrappedBackBufferView is null)
		{
			return;
		}
		ID3D11Resource[] resources = [wrappedBackBufferView];
		ID2D1Bitmap1? target = _direct2DTargets[frameIndex];
		if (target is null)
		{
			return;
		}
		bool flag = false;
		bool flag2 = false;
		try
		{
			_direct3D11On12Device.AcquireWrappedResources(resources);
			flag = true;
			_direct2DContext.Target = target;
			_direct2DContext.BeginDraw();
			flag2 = true;
			_direct2DContext.DrawImage(overlayCommandList, null, null, Vortice.Direct2D1.InterpolationMode.Linear, CompositeMode.SourceOver);
			Result result = _direct2DContext.EndDraw();
			flag2 = false;
			result.CheckError();
		}
		catch when (flag)
		{
			_disabled = true;
		}
		finally
		{
			if (flag2)
			{
				_direct2DContext.EndDraw();
			}
			_direct2DContext.Target = null;
			if (flag)
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
		if (_overlayCommandList is not null && ReferenceEquals(_recordedOverlay, overlay) && _recordedWidth == width && _recordedHeight == height)
		{
			return;
		}
		ID2D1CommandList? iD2D1CommandList = null;
		bool flag = false;
		try
		{
			iD2D1CommandList = _direct2DContext.CreateCommandList();
			_direct2DContext.Target = iD2D1CommandList;
			_direct2DContext.BeginDraw();
			flag = true;
			DrawMesh(overlay.FaceMesh, width, height);
			foreach (PreviewOverlayDiagnosticMesh diagnosticMesh in overlay.DiagnosticMeshes)
			{
				DrawDiagnosticMesh(diagnosticMesh, width, height);
			}
			DrawRectangle(overlay.FaceBox, FaceBoxColor, 0.75f, width, height);
			foreach (PreviewTrackedPerson person in overlay.TrackedPeople)
			{
				DrawRectangle(
					person.Bounds,
					person.IsRemembered
						? RememberedPersonColor
						: LearningPersonColor,
					person.IsRemembered ? 1.6f : 1.1f,
					width,
					height);
			}
			DrawPolyline(overlay.FaceContour, FaceContourColor, 0.5f, width, height);
			DrawPolyline(overlay.JawContour, FaceContourColor, 0.75f, width, height);
			DrawPolyline(overlay.LeftEyeContour, EyeContourColor, 0.75f, width, height);
			DrawPolyline(overlay.RightEyeContour, EyeContourColor, 0.75f, width, height);
			DrawPolyline(overlay.LeftBrowContour, BrowContourColor, 0.75f, width, height);
			DrawPolyline(overlay.RightBrowContour, BrowContourColor, 0.75f, width, height);
			DrawPolyline(overlay.OuterLipContour, LipContourColor, 0.75f, width, height);
			DrawPolyline(overlay.InnerLipContour, LipContourColor, 0.5f, width, height);
			Result result = _direct2DContext.EndDraw();
			flag = false;
			result.CheckError();
			iD2D1CommandList.Close().CheckError();
			_overlayCommandList?.Dispose();
			_overlayCommandList = iD2D1CommandList;
			iD2D1CommandList = null;
			_recordedOverlay = overlay;
			_recordedWidth = width;
			_recordedHeight = height;
		}
		finally
		{
			if (flag)
			{
				_direct2DContext.EndDraw();
			}
			_direct2DContext.Target = null;
			iD2D1CommandList?.Dispose();
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
		ID2D1SolidColorBrush brush = BrushFor(FaceMeshColor);
		foreach (PreviewOverlayEdge edge in mesh.Edges)
		{
			DrawIndexedLine(mesh.Points, edge.FromIndex, edge.ToIndex, brush, 0.42f, width, height);
		}
		foreach (PreviewOverlayIndexedPath featurePath in mesh.FeaturePaths)
		{
			DrawIndexedPath(mesh.Points, featurePath, width, height);
		}
		if (!mesh.DrawPoints)
		{
			return;
		}
		ID2D1SolidColorBrush brush2 = BrushFor(FaceMeshPointColor);
		for (int i = 0; i < mesh.Points.Count; i++)
		{
			if (TryProject(mesh.Points[i], width, height, out var projected))
			{
				float num = ((i < mesh.FeaturePointMask.Count && mesh.FeaturePointMask[i]) ? 3.2f : 2f) * 0.5f;
				_direct2DContext.FillEllipse(new Ellipse(in projected, num, num), brush2);
			}
		}
	}

	private void DrawDiagnosticMesh(PreviewOverlayDiagnosticMesh mesh, int width, int height)
	{
		if (mesh.Points.Count < 2)
		{
			return;
		}
		(OverlayColor lineColor, OverlayColor pointColor, float lineWidth) = mesh.Role switch
		{
			PreviewOverlayDiagnosticMeshRole.TranslatedPartner => (TranslatedMeshColor, TranslatedPointColor, 0.52f), 
			PreviewOverlayDiagnosticMeshRole.CalibrationBoard => (CalibrationMeshColor, CalibrationPointColor, 1.1f), 
			_ => (FusedMeshColor, FusedPointColor, 0.68f), 
		};
		ID2D1SolidColorBrush brush = BrushFor(lineColor);
		ID2D1SolidColorBrush brush2 = BrushFor(pointColor);
		foreach (PreviewOverlayEdge edge in mesh.Edges)
		{
			DrawIndexedLine(mesh.Points, edge.FromIndex, edge.ToIndex, brush, lineWidth, width, height);
		}
		if (!mesh.DrawPoints)
		{
			return;
		}
		float num = 0.8f;
		foreach (PreviewOverlayPoint point in mesh.Points)
		{
			if (TryProject(point, width, height, out var projected))
			{
				_direct2DContext.FillEllipse(new Ellipse(in projected, num, num), brush2);
			}
		}
	}

	private void DrawIndexedPath(IReadOnlyList<PreviewOverlayPoint> points, PreviewOverlayIndexedPath path, int width, int height)
	{
		if (path.PointIndices.Count >= 2)
		{
			ID2D1SolidColorBrush brush = BrushFor(ColorForMeshRole(path.Role));
			for (int i = 1; i < path.PointIndices.Count; i++)
			{
				DrawIndexedLine(points, path.PointIndices[i - 1], path.PointIndices[i], brush, 1.75f, width, height);
			}
			if (path.Closed && path.PointIndices.Count > 2)
			{
				IReadOnlyList<int> pointIndices = path.PointIndices;
				DrawIndexedLine(points, pointIndices[pointIndices.Count - 1], path.PointIndices[0], brush, 1.75f, width, height);
			}
		}
	}

	private void DrawIndexedLine(IReadOnlyList<PreviewOverlayPoint> points, int fromIndex, int toIndex, ID2D1Brush brush, float thickness, int width, int height)
	{
		if ((uint)fromIndex < (uint)points.Count && (uint)toIndex < (uint)points.Count && TryProject(points[fromIndex], width, height, out var projected) && TryProject(points[toIndex], width, height, out var projected2))
		{
			_direct2DContext.DrawLine(projected, projected2, brush, thickness, null);
		}
	}

	private void DrawRectangle(PreviewOverlayRect? rectangle, OverlayColor color, float thickness, int width, int height)
	{
		if (rectangle.HasValue)
		{
			PreviewOverlayRect previewOverlayRect = rectangle.Value.Clamp();
			Vector2 vector = new Vector2((float)(previewOverlayRect.Left * (double)width), (float)(previewOverlayRect.Top * (double)height));
			Vector2 vector2 = new Vector2((float)(previewOverlayRect.Right * (double)width), vector.Y);
			Vector2 vector3 = new Vector2(vector.X, (float)(previewOverlayRect.Bottom * (double)height));
			Vector2 vector4 = new Vector2(vector2.X, vector3.Y);
			ID2D1SolidColorBrush brush = BrushFor(color);
			_direct2DContext.DrawLine(vector, vector2, brush, thickness, null);
			_direct2DContext.DrawLine(vector2, vector4, brush, thickness, null);
			_direct2DContext.DrawLine(vector4, vector3, brush, thickness, null);
			_direct2DContext.DrawLine(vector3, vector, brush, thickness, null);
		}
	}

	private void DrawPolyline(PreviewOverlayPolyline? polyline, OverlayColor normalColor, float thickness, int width, int height)
	{
		if (polyline is not null && polyline.Points.Count >= 2)
		{
			ID2D1SolidColorBrush brush = BrushFor(polyline.Inferred ? InferredContourColor : normalColor);
			for (int i = 1; i < polyline.Points.Count; i++)
			{
				DrawLine(polyline.Points[i - 1], polyline.Points[i], brush, thickness, width, height);
			}
			if (polyline.Closed && polyline.Points.Count > 2)
			{
				IReadOnlyList<PreviewOverlayPoint> points = polyline.Points;
				DrawLine(points[points.Count - 1], polyline.Points[0], brush, thickness, width, height);
			}
		}
	}

	private void DrawLine(PreviewOverlayPoint fromPoint, PreviewOverlayPoint toPoint, ID2D1Brush brush, float thickness, int width, int height)
	{
		if (TryProject(fromPoint, width, height, out var projected) && TryProject(toPoint, width, height, out var projected2))
		{
			_direct2DContext.DrawLine(projected, projected2, brush, thickness, null);
		}
	}

	private ID2D1SolidColorBrush BrushFor(OverlayColor color)
	{
		if (_brushes.TryGetValue(color, out ID2D1SolidColorBrush? value))
		{
			return value;
		}
		value = _direct2DContext.CreateSolidColorBrush(new Color4(color.Red, color.Green, color.Blue, color.Alpha));
		_brushes.Add(color, value);
		return value;
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
			_ => FaceMeshPointColor, 
		};
	}

	private static bool TryProject(PreviewOverlayPoint point, int width, int height, out Vector2 projected)
	{
		if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
		{
			projected = default(Vector2);
			return false;
		}
		projected = new Vector2((float)(Math.Clamp(point.X, 0.0, 1.0) * (double)width), (float)(Math.Clamp(point.Y, 0.0, 1.0) * (double)height));
		return true;
	}

	private static OverlayColor FromBytes(byte red, byte green, byte blue, byte alpha)
	{
		return new OverlayColor((float)(int)red * 0.003921569f, (float)(int)green * 0.003921569f, (float)(int)blue * 0.003921569f, (float)(int)alpha * 0.003921569f);
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
		foreach (ID2D1SolidColorBrush value in _brushes.Values)
		{
			value.Dispose();
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
}
