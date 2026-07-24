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

public partial class MainWindow
{
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
				IReadOnlyList<PreviewOverlayEdge> edges =
					MediaPipePreviewOverlayFactory.MeshEdges;
				for (int i = 0; i < edges.Count; i++)
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
			PreviewOverlayIndexedPath[] featurePaths =
				MediaPipePreviewOverlayFactory.CreateFeaturePaths(
					denseMeshPoints);
			DrawMediaPipeFeatureGeometry(canvas, featurePaths, projectedPoints, validPoints, count, PreviewOverlayMeshFeatureRole.Eye);
			DrawMediaPipeFeatureGeometry(canvas, featurePaths, projectedPoints, validPoints, count, PreviewOverlayMeshFeatureRole.Brow);
			DrawMediaPipeFeatureGeometry(canvas, featurePaths, projectedPoints, validPoints, count, PreviewOverlayMeshFeatureRole.Mouth);
			DrawMediaPipeFeatureGeometry(canvas, featurePaths, projectedPoints, validPoints, count, PreviewOverlayMeshFeatureRole.Jaw);
			DrawMediaPipeFeatureGeometry(canvas, featurePaths, projectedPoints, validPoints, count, PreviewOverlayMeshFeatureRole.Nose);
			DrawMediaPipeFeatureGeometry(canvas, featurePaths, projectedPoints, validPoints, count, PreviewOverlayMeshFeatureRole.Face);
			StreamGeometry pointGeometry = new StreamGeometry();
			using (StreamGeometryContext context = pointGeometry.Open())
			{
				IReadOnlyList<bool> featurePointMask =
					MediaPipePreviewOverlayFactory.MeshFeaturePointMask;
				for (int i = 0; i < count; i++)
				{
					if (!validPoints[i])
					{
						continue;
					}
					double radius =
						i < featurePointMask.Count && featurePointMask[i]
							? 1.6
							: 1.0;
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
				FaceMesh = MediaPipePreviewOverlayFactory.CreateMesh(
					landmarkFrame.DenseMeshPoints)
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
		IReadOnlyList<int> eyeA =
			MediaPipePreviewOverlayFactory.EyeAIndices;
		IReadOnlyList<int> eyeB =
			MediaPipePreviewOverlayFactory.EyeBIndices;
		IReadOnlyList<Point> readOnlyList =
			CreateDenseMeshContour(frame.DenseMeshPoints, eyeA);
		IReadOnlyList<Point> readOnlyList2 =
			CreateDenseMeshContour(frame.DenseMeshPoints, eyeB);
		if (readOnlyList.Count != eyeA.Count
			|| readOnlyList2.Count != eyeB.Count)
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
