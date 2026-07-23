using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using OpenCvSharp;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public static class OpenCvApertureEstimator
{
	public static ApertureEstimate EstimateEye(Mat gray, OpenCvSharp.Rect eyeBox)
	{
		return EstimateDarkAperture(gray, eyeBox, 0.14, 0.18, 0.075, 0.06, 0.18, 0.04, 0.72);
	}

	public static ApertureEstimate EstimateMouth(Mat gray, OpenCvSharp.Rect mouthBox)
	{
		return EstimateDarkAperture(gray, mouthBox, 0.1, 0.2, 0.09, 0.08, 0.18, 0.08, 0.8);
	}

	public static ApertureEstimate FromBox(OpenCvSharp.Rect box, double heightFraction, double confidence)
	{
		double num = (double)box.X + (double)box.Width / 2.0;
		double num2 = (double)box.Y + (double)box.Height / 2.0;
		double num3 = (double)box.Width * 0.48;
		double num4 = (double)box.Height * heightFraction / 2.0;
		return new ApertureEstimate(HasAperture: true, new OpenCvSharp.Rect((int)Math.Round(num - num3), (int)Math.Round(num2 - num4), Math.Max(1, (int)Math.Round(num3 * 2.0)), Math.Max(1, (int)Math.Round(num4 * 2.0))), CreateOvalContour(num, num2, num3, num4), confidence);
	}

	private static ApertureEstimate EstimateDarkAperture(Mat gray, OpenCvSharp.Rect featureBox, double innerXFraction, double innerYFraction, double minimumRowCoverage, double minimumColumnCoverage, double verticalPaddingFraction, double horizontalPaddingFraction, double maximumOpeningFraction)
	{
		if (gray.Empty() || gray.Width <= 0 || gray.Height <= 0 || gray.Channels() != 1)
		{
			return ApertureEstimate.None;
		}
		OpenCvSharp.Rect roi = ClampRect(featureBox, gray.Width, gray.Height);
		if (roi.Width < 8 || roi.Height < 6)
		{
			return ApertureEstimate.None;
		}
		using Mat mat = new Mat(gray, roi);
		using Mat mat2 = mat.Clone();
		if (mat2.Empty() || mat2.Width < 8 || mat2.Height < 6)
		{
			return ApertureEstimate.None;
		}
		using Mat mat3 = new Mat();
		using Mat mat4 = new Mat();
		ApertureImageQuality apertureImageQuality = AnalyzeImageQuality(mat2);
		Cv2.EqualizeHist(mat2, mat3);
		using Mat mat5 = SmoothGray3x3(mat3);
		Cv2.Threshold(mat5, mat4, 0.0, 255.0, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
		using Mat mat6 = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(3, 3));
		Cv2.MorphologyEx(mat4, mat4, MorphTypes.Open, mat6);
		Cv2.MorphologyEx(mat4, mat4, MorphTypes.Close, mat6);
		RemoveLikelyGlassesFrameArtifacts(mat4);
		int num = Math.Clamp((int)Math.Round((double)roi.Width * innerXFraction), 0, Math.Max(0, roi.Width - 1));
		int num2 = Math.Clamp((int)Math.Round((double)roi.Width * (1.0 - innerXFraction)), num + 1, roi.Width);
		int num3 = Math.Clamp((int)Math.Round((double)roi.Height * innerYFraction), 0, Math.Max(0, roi.Height - 1));
		int num4 = Math.Clamp((int)Math.Round((double)roi.Height * (1.0 - innerYFraction)), num3 + 1, roi.Height);
		if (TryEstimateCenterWeightedAperture(mat4, roi, num, num2, num3, num4, verticalPaddingFraction, horizontalPaddingFraction, apertureImageQuality, out ApertureEstimate estimate))
		{
			return estimate;
		}
		if (TryEstimateCentralComponent(mat4, roi, num, num2, num3, num4, verticalPaddingFraction, horizontalPaddingFraction, maximumOpeningFraction, apertureImageQuality, out ApertureEstimate estimate2))
		{
			return estimate2;
		}
		(int, int)? tuple = FindProjectionSpan(mat4, num, num2, num3, num4, scanRows: true, minimumRowCoverage);
		(int, int)? tuple2 = FindProjectionSpan(mat4, num, num2, num3, num4, scanRows: false, minimumColumnCoverage);
		if (!tuple.HasValue || !tuple2.HasValue)
		{
			return ApertureEstimate.FromDiagnostics(apertureImageQuality);
		}
		int item = tuple2.Value.Item1;
		int item2 = tuple2.Value.Item2;
		int num5 = tuple.Value.Item1;
		int num6 = tuple.Value.Item2;
		Math.Max(1, item2 - item + 1);
		int num7 = Math.Max(1, num6 - num5 + 1);
		if ((double)num7 / (double)Math.Max(1, roi.Height) > maximumOpeningFraction)
		{
			double num8 = (double)num5 + (double)num7 / 2.0;
			num7 = Math.Max(1, (int)Math.Round((double)roi.Height * maximumOpeningFraction));
			num5 = Math.Clamp((int)Math.Round(num8 - (double)num7 / 2.0), 0, Math.Max(0, roi.Height - num7));
			num6 = num5 + num7 - 1;
		}
		int num9 = CountNonZero(mat4, num, num2, num3, num4);
		int num10 = Math.Max(1, (num2 - num) * (num4 - num3));
		double num11 = (double)num9 / (double)num10;
		double num12 = Math.Clamp(num11 / 0.18, 0.0, 1.0);
		double num13 = Math.Clamp((double)num7 / (double)Math.Max(1, roi.Height) / 0.34, 0.0, 1.0);
		double confidence = AdjustConfidenceForImageQuality(Math.Clamp(num12 * 0.58 + num13 * 0.42, 0.05, 0.88), apertureImageQuality);
		return CreateProfileAwareEstimate(mat4, roi, item, item2, num5, num6, verticalPaddingFraction, horizontalPaddingFraction, confidence, apertureImageQuality.GlareRatio, apertureImageQuality.ContrastScore, apertureImageQuality.SharpnessScore, num11);
	}

	private static bool TryEstimateCenterWeightedAperture(Mat mask, OpenCvSharp.Rect roi, int innerLeft, int innerRight, int innerTop, int innerBottom, double verticalPaddingFraction, double horizontalPaddingFraction, ApertureImageQuality imageQuality, out ApertureEstimate estimate)
	{
		estimate = ApertureEstimate.None;
		double a = (double)roi.Width / 2.0;
		double num = (double)roi.Height / 2.0;
		int num2 = Math.Max(innerTop, (int)Math.Round(num - (double)roi.Height * 0.22));
		int num3 = Math.Min(innerBottom, (int)Math.Round(num + (double)roi.Height * 0.22));
		if (num3 <= num2)
		{
			return false;
		}
		int[] array = new int[innerBottom - innerTop];
		int num4 = -1;
		int num5 = 0;
		for (int i = innerTop; i < innerBottom; i++)
		{
			int num6 = (array[i - innerTop] = CountNonZero(mask, innerLeft, innerRight, i, i + 1));
			if (i >= num2 && i < num3 && num6 > num5)
			{
				num4 = i;
				num5 = num6;
			}
		}
		if (num4 < 0 || (double)num5 < Math.Max(3.0, (double)(innerRight - innerLeft) * 0.08))
		{
			return false;
		}
		int num7 = Math.Max(1, (int)Math.Round((double)num5 * 0.34));
		int num8 = num4;
		while (num8 > innerTop && array[num8 - 1 - innerTop] >= num7)
		{
			num8--;
		}
		int j;
		for (j = num4; j < innerBottom - 1 && array[j + 1 - innerTop] >= num7; j++)
		{
		}
		int num9 = Math.Max(1, j - num8 + 1);
		if ((double)num9 > (double)roi.Height * 0.55)
		{
			return false;
		}
		int[] array2 = new int[innerRight - innerLeft];
		int num10 = Math.Max(1, (int)Math.Round((double)num9 * 0.16));
		for (int k = innerLeft; k < innerRight; k++)
		{
			array2[k - innerLeft] = CountNonZero(mask, k, k + 1, num8, j + 1);
		}
		int num11 = Math.Clamp((int)Math.Round(a), innerLeft, innerRight - 1);
		int num12 = num11;
		while (num12 > innerLeft && array2[num12 - 1 - innerLeft] >= num10)
		{
			num12--;
		}
		int l;
		for (l = num11; l < innerRight - 1 && array2[l + 1 - innerLeft] >= num10; l++)
		{
		}
		if ((double)(l - num12) < Math.Max(4.0, (double)roi.Width * 0.12))
		{
			return false;
		}
		int num13 = Math.Max(1, l - num12 + 1);
		double num14 = Math.Max(1.0, (double)num13 * horizontalPaddingFraction);
		_ = (double)num13 / 2.0;
		Math.Max(2.0, (double)num13 / 2.0 + num14);
		double confidence = AdjustConfidenceForImageQuality(Math.Clamp(Math.Min(1.0, (double)num5 / Math.Max(1.0, innerRight - innerLeft) * 2.0) * 0.55 + Math.Min(1.0, (double)num9 / Math.Max(1.0, (double)roi.Height * 0.34)) * 0.45, 0.08, 0.9), imageQuality);
		double darkCoverage = (double)CountNonZero(mask, num12, l + 1, num8, j + 1) / (double)Math.Max(1, num13 * num9);
		estimate = CreateProfileAwareEstimate(mask, roi, num12, l, num8, j, verticalPaddingFraction, horizontalPaddingFraction, confidence, imageQuality.GlareRatio, imageQuality.ContrastScore, imageQuality.SharpnessScore, darkCoverage);
		return true;
	}

	private static void RemoveLikelyGlassesFrameArtifacts(Mat mask)
	{
		int width = mask.Width;
		int height = mask.Height;
		double num = (double)height / 2.0;
		for (int i = 0; i < height; i++)
		{
			int num2 = CountNonZero(mask, 0, width, i, i + 1);
			if (Math.Abs((double)i - num) > (double)height * 0.18 && (double)num2 >= (double)width * 0.45)
			{
				for (int j = 0; j < width; j++)
				{
					mask.Set(i, j, (byte)0);
				}
			}
		}
		for (int k = 0; k < width; k++)
		{
			if ((double)CountNonZero(mask, k, k + 1, 0, height) >= (double)height * 0.68)
			{
				for (int l = 0; l < height; l++)
				{
					mask.Set(l, k, (byte)0);
				}
			}
		}
	}

	private static bool TryEstimateCentralComponent(Mat mask, OpenCvSharp.Rect roi, int innerLeft, int innerRight, int innerTop, int innerBottom, double verticalPaddingFraction, double horizontalPaddingFraction, double maximumOpeningFraction, ApertureImageQuality imageQuality, out ApertureEstimate estimate)
	{
		estimate = ApertureEstimate.None;
		Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out HierarchyIndex[] _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
		double num = 0.0;
		OpenCvSharp.Rect rect = default(OpenCvSharp.Rect);
		double num2 = (double)roi.Width / 2.0;
		double num3 = (double)roi.Height / 2.0;
		OpenCvSharp.Point[][] array = contours;
		foreach (OpenCvSharp.Point[] array2 in array)
		{
			OpenCvSharp.Rect rect2 = Cv2.BoundingRect(array2);
			if ((double)rect2.Width < Math.Max(4.0, (double)roi.Width * 0.12) || rect2.Height < 1 || (double)rect2.X + (double)rect2.Width / 2.0 < (double)innerLeft || (double)rect2.X + (double)rect2.Width / 2.0 > (double)innerRight || (double)rect2.Y + (double)rect2.Height / 2.0 < (double)innerTop || (double)rect2.Y + (double)rect2.Height / 2.0 > (double)innerBottom)
			{
				continue;
			}
			bool num4 = (double)rect2.Width > (double)roi.Width * 0.72 && (double)rect2.Height < Math.Max(3.0, (double)roi.Height * 0.09);
			bool flag = (double)rect2.Width < (double)roi.Width * 0.1 && (double)rect2.Height > (double)roi.Height * 0.35;
			if (!(num4 || flag) && !((double)rect2.Height / (double)Math.Max(1, roi.Height) > maximumOpeningFraction))
			{
				double num5 = (double)rect2.X + (double)rect2.Width / 2.0;
				double num6 = (double)rect2.Y + (double)rect2.Height / 2.0;
				double val = Math.Clamp(1.0 - Math.Abs(num5 - num2) / Math.Max(1.0, (double)roi.Width * 0.5), 0.0, 1.0) * Math.Clamp(1.0 - Math.Abs(num6 - num3) / Math.Max(1.0, (double)roi.Height * 0.5), 0.0, 1.0);
				double num7 = Math.Max(1.0, Cv2.ContourArea(array2)) * Math.Max(0.08, val);
				if (num7 > num)
				{
					num = num7;
					rect = rect2;
				}
			}
		}
		if (num <= 0.0)
		{
			return false;
		}
		double num8 = Math.Clamp(num / Math.Max(1.0, (double)(roi.Width * roi.Height) * 0.12), 0.0, 1.0);
		double num9 = Math.Clamp((double)rect.Height / (double)Math.Max(1, roi.Height) / 0.34, 0.0, 1.0);
		double confidence = AdjustConfidenceForImageQuality(Math.Clamp(num8 * 0.5 + num9 * 0.5, 0.08, 0.9), imageQuality);
		double darkCoverage = (double)CountNonZero(mask, rect.X, rect.Right, rect.Y, rect.Bottom) / (double)Math.Max(1, rect.Width * rect.Height);
		estimate = CreateProfileAwareEstimate(mask, roi, rect.Left, rect.Right - 1, rect.Top, rect.Bottom - 1, verticalPaddingFraction, horizontalPaddingFraction, confidence, imageQuality.GlareRatio, imageQuality.ContrastScore, imageQuality.SharpnessScore, darkCoverage);
		return true;
	}

	private static ApertureEstimate CreateProfileAwareEstimate(Mat mask, OpenCvSharp.Rect roi, int left, int right, int top, int bottom, double verticalPaddingFraction, double horizontalPaddingFraction, double confidence, double glareRatio, double contrastScore, double sharpnessScore, double darkCoverage)
	{
		int num = Math.Max(1, right - left + 1);
		int num2 = Math.Max(1, bottom - top + 1);
		ApertureColumnProfile apertureColumnProfile = MeasureColumnProfile(mask, left, right + 1, top, bottom + 1);
		double num3 = (((double)apertureColumnProfile.SampleCount >= Math.Max(3.0, (double)num * 0.18)) ? BlendProfileHeight(num2, apertureColumnProfile.MedianHeight) : ((double)num2));
		double num4 = (double)(roi.X + left) + (double)num / 2.0;
		double num5 = (double)roi.Y + ((apertureColumnProfile.SampleCount > 0) ? apertureColumnProfile.CenterY : ((double)top + (double)num2 / 2.0));
		double num6 = Math.Max(1.0, num3 * verticalPaddingFraction);
		double num7 = Math.Max(1.0, (double)num * horizontalPaddingFraction);
		double num8 = Math.Max(2.0, (double)num / 2.0 + num7);
		double num9 = Math.Max(1.0, num3 / 2.0 + num6);
		double num10 = Math.Max(1.0, num8 * 2.0);
		double num11 = Math.Max(1.0, num9 * 2.0);
		double value = Math.Clamp(num11 / num10, 0.0, 2.0);
		return new ApertureEstimate(HasAperture: true, new OpenCvSharp.Rect(Math.Max(0, (int)Math.Round(num4 - num8)), Math.Max(0, (int)Math.Round(num5 - num9)), Math.Max(1, (int)Math.Round(num10)), Math.Max(1, (int)Math.Round(num11))), CreateOvalContour(num4, num5, num8, num9), confidence, glareRatio, contrastScore, sharpnessScore, darkCoverage, value, apertureColumnProfile.SampleCount, apertureColumnProfile.CoverageRatio);
	}

	private static double BlendProfileHeight(int boundingHeight, double profileMedianHeight)
	{
		if (profileMedianHeight <= 0.0)
		{
			return boundingHeight;
		}
		return Math.Clamp((double)boundingHeight * 0.35 + profileMedianHeight * 0.65, Math.Max(1.0, profileMedianHeight * 0.8), boundingHeight);
	}

	private static ApertureColumnProfile MeasureColumnProfile(Mat mask, int left, int right, int top, int bottom)
	{
		left = Math.Clamp(left, 0, Math.Max(0, mask.Width - 1));
		right = Math.Clamp(right, left + 1, mask.Width);
		top = Math.Clamp(top, 0, Math.Max(0, mask.Height - 1));
		bottom = Math.Clamp(bottom, top + 1, mask.Height);
		List<int> list = new List<int>();
		List<double> list2 = new List<double>();
		int num = Math.Max(2, (int)Math.Round((double)(bottom - top) * 0.92));
		for (int i = left; i < right; i++)
		{
			int num2 = -1;
			int num3 = -1;
			int num4 = 0;
			int num5 = -1;
			int num6 = 0;
			int num7 = -1;
			int num8 = 0;
			for (int j = top; j < bottom; j++)
			{
				if (mask.At<byte>(j, i) == 0)
				{
					if (num6 > num8)
					{
						num7 = num5;
						num8 = num6;
					}
					num5 = -1;
					num6 = 0;
				}
				else
				{
					num2 = ((num2 < 0) ? j : num2);
					num3 = j;
					num4++;
					num5 = ((num5 < 0) ? j : num5);
					num6++;
				}
			}
			if (num6 > num8)
			{
				num7 = num5;
				num8 = num6;
			}
			if (num2 < 0)
			{
				continue;
			}
			int num9 = num3 - num2 + 1;
			if (num9 > num && (double)num4 < (double)num9 * 0.28)
			{
				continue;
			}
			int num10;
			int num11;
			if (num8 > 0)
			{
				if (!((double)num8 <= (double)num9 * 0.78))
				{
					num10 = (((double)num4 <= (double)num9 * 0.72) ? 1 : 0);
					if (num10 == 0)
					{
						goto IL_015f;
					}
				}
				else
				{
					num10 = 1;
				}
				num11 = num8;
				goto IL_0165;
			}
			num10 = 0;
			goto IL_015f;
			IL_0165:
			int item = num11;
			double item2 = ((num10 != 0 && num7 >= 0) ? ((double)num7 + (double)num8 / 2.0) : ((double)(num2 + num3) / 2.0));
			list.Add(item);
			list2.Add(item2);
			continue;
			IL_015f:
			num11 = num9;
			goto IL_0165;
		}
		if (list.Count == 0)
		{
			return ApertureColumnProfile.None;
		}
		list.Sort();
		int num12 = list[list.Count / 2];
		int num13 = ((list.Count >= 8) ? Math.Max(1, list.Count / 10) : 0);
		return new ApertureColumnProfile(list.Skip(num13).Take(Math.Max(1, list.Count - num13 * 2)).ToList()
			.Average(), CenterY: list2.Average(), MedianHeight: num12, SampleCount: list.Count, CoverageRatio: (double)list.Count / (double)Math.Max(1, right - left));
	}

	private static (int Start, int End)? FindProjectionSpan(Mat mask, int innerLeft, int innerRight, int innerTop, int innerBottom, bool scanRows, double minimumCoverage)
	{
		int num = (scanRows ? innerBottom : innerRight);
		int num2 = (scanRows ? (innerRight - innerLeft) : (innerBottom - innerTop));
		int num3 = Math.Max(1, (int)Math.Round((double)num2 * minimumCoverage));
		int num4 = -1;
		int num5 = -1;
		int num6 = -1;
		int num7 = -1;
		for (int i = (scanRows ? innerTop : innerLeft); i < num; i++)
		{
			if ((scanRows ? CountNonZero(mask, innerLeft, innerRight, i, i + 1) : CountNonZero(mask, i, i + 1, innerTop, innerBottom)) >= num3)
			{
				num6 = ((num6 < 0) ? i : num6);
				num7 = i;
				continue;
			}
			if (num6 >= 0 && num7 - num6 > num5 - num4)
			{
				num4 = num6;
				num5 = num7;
			}
			num6 = -1;
			num7 = -1;
		}
		if (num6 >= 0 && num7 - num6 > num5 - num4)
		{
			num4 = num6;
			num5 = num7;
		}
		if (num4 >= 0)
		{
			return (num4, num5);
		}
		return null;
	}

	private static int CountNonZero(Mat mask, int left, int right, int top, int bottom)
	{
		int num = 0;
		for (int i = top; i < bottom; i++)
		{
			for (int j = left; j < right; j++)
			{
				if (mask.At<byte>(i, j) != 0)
				{
					num++;
				}
			}
		}
		return num;
	}

	private static ApertureImageQuality AnalyzeImageQuality(Mat grayRoi)
	{
		if (grayRoi.Empty() || grayRoi.Width <= 0 || grayRoi.Height <= 0)
		{
			return ApertureImageQuality.None;
		}
		Cv2.MeanStdDev(grayRoi, out var mean, out var stddev);
		using Mat mat = new Mat();
		Cv2.Laplacian(grayRoi, mat, 6);
		Cv2.MeanStdDev(mat, out mean, out var stddev2);
		int num = Math.Max(1, grayRoi.Width * grayRoi.Height);
		double glareRatio = (double)CountPixelsAtLeast(grayRoi, 238) / (double)num;
		double contrastScore = Math.Clamp(stddev.Val0 / 55.0, 0.0, 1.0);
		double sharpnessScore = Math.Clamp(stddev2.Val0 * stddev2.Val0 / 4500.0, 0.0, 1.0);
		return new ApertureImageQuality(glareRatio, contrastScore, sharpnessScore);
	}

	private static double AdjustConfidenceForImageQuality(double confidence, ApertureImageQuality imageQuality)
	{
		double num = Math.Clamp((imageQuality.GlareRatio - 0.035) / 0.18, 0.0, 0.55);
		double num2 = (imageQuality.ContrastScore - 0.5) * 0.16;
		double num3 = (imageQuality.SharpnessScore - 0.45) * 0.14;
		double num4 = Math.Clamp(1.0 + num2 + num3 - num, 0.46, 1.08);
		return Math.Clamp(confidence * num4, 0.03, 0.92);
	}

	private static int CountPixelsAtLeast(Mat gray, byte threshold)
	{
		int num = 0;
		int height = gray.Height;
		int width = gray.Width;
		for (int i = 0; i < height; i++)
		{
			for (int j = 0; j < width; j++)
			{
				if (gray.At<byte>(i, j) >= threshold)
				{
					num++;
				}
			}
		}
		return num;
	}

	private static Mat SmoothGray3x3(Mat gray)
	{
		int rows = gray.Rows;
		int cols = gray.Cols;
		Mat mat = new Mat(rows, cols, MatType.CV_8UC1);
		for (int i = 0; i < rows; i++)
		{
			for (int j = 0; j < cols; j++)
			{
				int num = 0;
				int num2 = 0;
				for (int k = Math.Max(0, i - 1); k <= Math.Min(rows - 1, i + 1); k++)
				{
					for (int l = Math.Max(0, j - 1); l <= Math.Min(cols - 1, j + 1); l++)
					{
						num += gray.At<byte>(k, l);
						num2++;
					}
				}
				mat.Set(i, j, (byte)Math.Clamp((int)Math.Round((double)num / (double)Math.Max(1, num2)), 0, 255));
			}
		}
		return mat;
	}

	private static IReadOnlyList<System.Windows.Point> CreateOvalContour(double centerX, double centerY, double halfWidth, double halfHeight)
	{
		return new global::_003C_003Ez__ReadOnlyArray<System.Windows.Point>(new System.Windows.Point[8]
		{
			new System.Windows.Point(centerX - halfWidth, centerY),
			new System.Windows.Point(centerX - halfWidth * 0.72, centerY - halfHeight * 0.7),
			new System.Windows.Point(centerX, centerY - halfHeight),
			new System.Windows.Point(centerX + halfWidth * 0.72, centerY - halfHeight * 0.7),
			new System.Windows.Point(centerX + halfWidth, centerY),
			new System.Windows.Point(centerX + halfWidth * 0.72, centerY + halfHeight * 0.7),
			new System.Windows.Point(centerX, centerY + halfHeight),
			new System.Windows.Point(centerX - halfWidth * 0.72, centerY + halfHeight * 0.7)
		});
	}

	private static OpenCvSharp.Rect ClampRect(OpenCvSharp.Rect rect, int width, int height)
	{
		int num = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
		int num2 = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
		int num3 = Math.Clamp(rect.Right, num + 1, width);
		int num4 = Math.Clamp(rect.Bottom, num2 + 1, height);
		return new OpenCvSharp.Rect(num, num2, num3 - num, num4 - num2);
	}
}
