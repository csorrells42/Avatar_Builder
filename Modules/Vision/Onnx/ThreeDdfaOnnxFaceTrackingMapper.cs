using System;
using System.Collections.Generic;
using System.Windows;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Onnx;

public static class ThreeDdfaOnnxFaceTrackingMapper
{
	private const int SparseLandmarkCount = 68;

	private static readonly int[] JawIndices = new int[17]
	{
		0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
		10, 11, 12, 13, 14, 15, 16
	};

	private static readonly int[] LeftBrowIndices = new int[5] { 17, 18, 19, 20, 21 };

	private static readonly int[] RightBrowIndices = new int[5] { 22, 23, 24, 25, 26 };

	private static readonly int[] LeftEyeIndices = new int[6] { 36, 37, 38, 39, 40, 41 };

	private static readonly int[] RightEyeIndices = new int[6] { 42, 43, 44, 45, 46, 47 };

	private static readonly int[] OuterLipIndices = new int[12]
	{
		48, 49, 50, 51, 52, 53, 54, 55, 56, 57,
		58, 59
	};

	private static readonly int[] InnerLipIndices = new int[8] { 60, 61, 62, 63, 64, 65, 66, 67 };

	public static FaceLandmarkTrackingResult ToTrackingResult(ThreeDdfaOnnxSidecarResponse response, int frameWidth, int frameHeight, DateTime capturedAtUtc)
	{
		if (!response.Ok || !response.HasFace || response.FaceBox == null || frameWidth <= 0 || frameHeight <= 0)
		{
			return new FaceLandmarkTrackingResult
			{
				BackendName = "3DDFA-V2 FaceBoxes",
				BackendStatus = (string.IsNullOrWhiteSpace(response.Status) ? "3DDFA-V2 FaceBoxes searching" : response.Status)
			};
		}
		Rect faceBox = NormalizeFaceBox(response.FaceBox, frameWidth, frameHeight);
		if (faceBox.Width <= 0.0 || faceBox.Height <= 0.0)
		{
			return new FaceLandmarkTrackingResult
			{
				BackendName = "3DDFA-V2 FaceBoxes",
				BackendStatus = "3DDFA-V2 returned an invalid face box"
			};
		}
		ThreeDdfaOnnxSidecarVertex?[] points = IndexSparseLandmarks(response.SparseLandmarks);
		IReadOnlyList<Point> jawContour = SelectPoints(points, JawIndices, frameWidth, frameHeight);
		IReadOnlyList<Point> readOnlyList = SelectPoints(points, LeftEyeIndices, frameWidth, frameHeight);
		IReadOnlyList<Point> readOnlyList2 = SelectPoints(points, RightEyeIndices, frameWidth, frameHeight);
		IReadOnlyList<Point> leftBrowContour = SelectPoints(points, LeftBrowIndices, frameWidth, frameHeight);
		IReadOnlyList<Point> rightBrowContour = SelectPoints(points, RightBrowIndices, frameWidth, frameHeight);
		IReadOnlyList<Point> readOnlyList3 = SelectPoints(points, OuterLipIndices, frameWidth, frameHeight);
		IReadOnlyList<Point> innerLipContour = SelectPoints(points, InnerLipIndices, frameWidth, frameHeight);
		IReadOnlyList<Point> faceContour = CreateFaceContour(faceBox, 32);
		double num = Math.Clamp(response.ReconstructionConfidencePercent / 100.0, 0.01, 1.0);
		double eyeConfidence = ((readOnlyList.Count >= 4 && readOnlyList2.Count >= 4) ? (num * 0.92) : (num * 0.35));
		double mouthConfidence = ((readOnlyList3.Count >= 4) ? (num * 0.9) : (num * 0.35));
		FaceFeatureDetection featureDetection = new FaceFeatureDetection
		{
			HasFace = true,
			Source = "3DDFA_V2 ONNX FaceBoxes and 68-point landmarks",
			FaceBox = faceBox,
			LeftEyeBox = Bounds(readOnlyList),
			RightEyeBox = Bounds(readOnlyList2),
			MouthBox = Bounds(readOnlyList3),
			FaceContour = faceContour,
			LeftEyeContour = readOnlyList,
			RightEyeContour = readOnlyList2,
			OuterLipContour = readOnlyList3,
			InnerLipContour = innerLipContour,
			JawContour = jawContour,
			TrackingConfidence = num,
			EyeConfidence = eyeConfidence,
			MouthConfidence = mouthConfidence
		};
		FaceLandmarkFrame landmarkFrame = new FaceLandmarkFrame
		{
			HasFace = true,
			Source = "3DDFA_V2 ONNX FaceBoxes and 68-point landmarks",
			CapturedAtUtc = capturedAtUtc,
			TrackingConfidence = num,
			EyeConfidence = eyeConfidence,
			MouthConfidence = mouthConfidence,
			HeadPitchDegrees = response.Pose.ARotationAroundXDegrees,
			HeadYawDegrees = response.Pose.BRotationAroundYDegrees,
			HeadRollDegrees = response.Pose.CRotationAroundZDegrees,
			FaceContour = faceContour,
			LeftEyeContour = readOnlyList,
			RightEyeContour = readOnlyList2,
			LeftBrowContour = leftBrowContour,
			RightBrowContour = rightBrowContour,
			OuterLipContour = readOnlyList3,
			InnerLipContour = innerLipContour,
			JawContour = jawContour
		};
		return new FaceLandmarkTrackingResult
		{
			BackendName = "3DDFA-V2 FaceBoxes",
			BackendStatus = response.Status,
			FeatureDetection = featureDetection,
			LandmarkFrame = landmarkFrame,
			Diagnostics = response.Diagnostics
		};
	}

	private static Rect NormalizeFaceBox(ThreeDdfaOnnxSidecarFaceBox faceBox, int frameWidth, int frameHeight)
	{
		double value = (faceBox.Normalized ? faceBox.Left : (faceBox.Left / (double)frameWidth));
		double value2 = (faceBox.Normalized ? faceBox.Top : (faceBox.Top / (double)frameHeight));
		double value3 = (faceBox.Normalized ? faceBox.Right : (faceBox.Right / (double)frameWidth));
		double value4 = (faceBox.Normalized ? faceBox.Bottom : (faceBox.Bottom / (double)frameHeight));
		value = Math.Clamp(value, 0.0, 1.0);
		value2 = Math.Clamp(value2, 0.0, 1.0);
		value3 = Math.Clamp(value3, value, 1.0);
		value4 = Math.Clamp(value4, value2, 1.0);
		return new Rect(value, value2, value3 - value, value4 - value2);
	}

	private static ThreeDdfaOnnxSidecarVertex?[] IndexSparseLandmarks(IReadOnlyList<ThreeDdfaOnnxSidecarVertex> points)
	{
		ThreeDdfaOnnxSidecarVertex[] array = new ThreeDdfaOnnxSidecarVertex[68];
		foreach (ThreeDdfaOnnxSidecarVertex point in points)
		{
			if ((uint)point.Index < (uint)array.Length)
			{
				array[point.Index] = point;
			}
		}
		return array;
	}

	private static IReadOnlyList<Point> SelectPoints(IReadOnlyList<ThreeDdfaOnnxSidecarVertex?> points, IReadOnlyList<int> indices, int frameWidth, int frameHeight)
	{
		List<Point> list = new List<Point>(indices.Count);
		foreach (int index in indices)
		{
			if ((uint)index < (uint)points.Count)
			{
			ThreeDdfaOnnxSidecarVertex? threeDdfaOnnxSidecarVertex = points[index];
				if (threeDdfaOnnxSidecarVertex != null)
				{
					list.Add(new Point(Math.Clamp(threeDdfaOnnxSidecarVertex.X / (double)frameWidth, 0.0, 1.0), Math.Clamp(threeDdfaOnnxSidecarVertex.Y / (double)frameHeight, 0.0, 1.0)));
				}
			}
		}
		return list;
	}

	private static Rect? Bounds(IReadOnlyList<Point> points)
	{
		if (points.Count < 2)
		{
			return null;
		}
		Point point = points[0];
		double num = point.X;
		double num2 = point.Y;
		double num3 = point.X;
		double num4 = point.Y;
		for (int i = 1; i < points.Count; i++)
		{
			Point point2 = points[i];
			num = Math.Min(num, point2.X);
			num2 = Math.Min(num2, point2.Y);
			num3 = Math.Max(num3, point2.X);
			num4 = Math.Max(num4, point2.Y);
		}
		if (!(num3 <= num) && !(num4 <= num2))
		{
			return new Rect(num, num2, num3 - num, num4 - num2);
		}
		return null;
	}

	private static IReadOnlyList<Point> CreateFaceContour(Rect faceBox, int pointCount)
	{
		List<Point> list = new List<Point>(pointCount);
		double num = faceBox.Left + faceBox.Width / 2.0;
		double num2 = faceBox.Top + faceBox.Height / 2.0;
		for (int i = 0; i < pointCount; i++)
		{
			double num3 = Math.PI * 2.0 * (double)i / (double)pointCount;
			list.Add(new Point(num + Math.Cos(num3) * faceBox.Width / 2.0, num2 + Math.Sin(num3) * faceBox.Height / 2.0));
		}
		return list;
	}
}
