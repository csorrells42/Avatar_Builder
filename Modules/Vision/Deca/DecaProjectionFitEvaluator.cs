using System;
using System.Collections.Generic;
using System.Linq;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Deca;

public static class DecaProjectionFitEvaluator
{
	private readonly record struct Point2(double X, double Y);

	private readonly record struct AnchorPair(string Name, Point2 Projected, Point2 Observed);

	private const double MaximumAnchorRmseFaceWidthPercent = 12.0;

	private const double MinimumJawWidthRatio = 0.82;

	private const double MaximumJawWidthRatio = 1.18;

	private const double MaximumCenterOffsetFaceWidthPercent = 8.0;

	private static readonly int[] MediaPipeEyeA = new int[16]
	{
		33, 246, 161, 160, 159, 158, 157, 173, 133, 155,
		154, 153, 145, 144, 163, 7
	};

	private static readonly int[] MediaPipeEyeB = new int[16]
	{
		362, 398, 384, 385, 386, 387, 388, 466, 263, 249,
		390, 373, 374, 380, 381, 382
	};

	private static readonly int[] MediaPipeJaw = new int[21]
	{
		234, 93, 132, 58, 172, 136, 150, 149, 176, 148,
		152, 377, 400, 378, 379, 365, 397, 288, 361, 323,
		454
	};

	public static DecaProjectionFitEvaluation Evaluate(IReadOnlyList<FaceMeshLandmarkPoint> projectedDecaLandmarks, FaceLandmarkFrame observedMediaPipeFrame, int frameWidthPixels, int frameHeightPixels)
	{
		if (projectedDecaLandmarks.Count < 68 || observedMediaPipeFrame.DenseMeshPoints.Count < 468 || frameWidthPixels <= 0 || frameHeightPixels <= 0)
		{
			return DecaProjectionFitEvaluation.NotMeasured("DECA projection fit was not measured because the same-frame 68-point DECA and 468-point MediaPipe landmarks were unavailable.");
		}
		Dictionary<int, FaceMeshLandmarkPoint> dictionary = projectedDecaLandmarks.ToDictionary((FaceMeshLandmarkPoint faceMeshLandmarkPoint) => faceMeshLandmarkPoint.Index);
		Dictionary<int, FaceMeshLandmarkPoint> dictionary2 = observedMediaPipeFrame.DenseMeshPoints.ToDictionary((FaceMeshLandmarkPoint faceMeshLandmarkPoint) => faceMeshLandmarkPoint.Index);
		if (!TryObservedPoint(dictionary2, 234, frameWidthPixels, frameHeightPixels, out var point) || !TryObservedPoint(dictionary2, 454, frameWidthPixels, frameHeightPixels, out var second) || !TryProjectedPoint(dictionary, 0, out var first) || !TryProjectedPoint(dictionary, 16, out var second2))
		{
			return DecaProjectionFitEvaluation.NotMeasured("DECA projection fit was not measured because the jaw-width reference anchors were incomplete.");
		}
		SortByX(ref point, ref second);
		SortByX(ref first, ref second2);
		double num = Distance(point, second);
		if (!double.IsFinite(num) || num < 8.0)
		{
			return DecaProjectionFitEvaluation.NotMeasured("DECA projection fit was not measured because the observed face-width reference was invalid.");
		}
		List<AnchorPair> list = new List<AnchorPair>(16);
		AddPair(list, "jaw", first, second2, point, second);
		AddJawContour(list, dictionary, dictionary2, frameWidthPixels, frameHeightPixels);
		AddSingle(list, "chin", dictionary, 8, dictionary2, 152, frameWidthPixels, frameHeightPixels);
		AddSingle(list, "nose bridge", dictionary, 27, dictionary2, 168, frameWidthPixels, frameHeightPixels);
		AddSingle(list, "nose tip", dictionary, 30, dictionary2, 1, frameWidthPixels, frameHeightPixels);
		AddIndexedPair(list, "nose", dictionary, 31, 35, dictionary2, 98, 327, frameWidthPixels, frameHeightPixels);
		AddIndexedPair(list, "mouth", dictionary, 48, 54, dictionary2, 61, 291, frameWidthPixels, frameHeightPixels);
		AddSingle(list, "upper lip", dictionary, 51, dictionary2, 0, frameWidthPixels, frameHeightPixels);
		AddSingle(list, "lower lip", dictionary, 57, dictionary2, 17, frameWidthPixels, frameHeightPixels);
		AddEyeCenters(list, dictionary, dictionary2, frameWidthPixels, frameHeightPixels);
		if (list.Count < 12)
		{
			return DecaProjectionFitEvaluation.NotMeasured($"DECA projection fit was not measured because only {list.Count} corresponding facial anchors were available.");
		}
		double num2 = 0.0;
		foreach (AnchorPair item in list)
		{
			num2 += Square(item.Projected.X - item.Observed.X) + Square(item.Projected.Y - item.Observed.Y);
		}
		double num3 = Math.Sqrt(num2 / (double)list.Count) / num * 100.0;
		double num4 = Distance(first, second2) / num;
		double num5 = Distance(Midpoint(first, second2), Midpoint(point, second)) / num * 100.0;
		double num6 = Math.Clamp(100.0 - num3 * 3.0 - Math.Abs(num4 - 1.0) * 120.0 - num5 * 1.2, 0.0, 100.0);
		bool flag = num3 <= 12.0 && num4 >= 0.82 && num4 <= 1.18 && num5 <= 8.0;
		double fitConfidencePercent = (flag ? num6 : Math.Min(num6, 54.0));
		string text = (flag ? $"Measured DECA fit passed: {num3:0.#}% anchor RMSE, {num4:0.###} jaw-width ratio, {num5:0.#}% center offset." : $"Measured DECA fit failed: {num3:0.#}% anchor RMSE, {num4:0.###} jaw-width ratio, {num5:0.#}% center offset.");
		return new DecaProjectionFitEvaluation(IsMeasured: true, flag, fitConfidencePercent, list.Count, num3, num4, num5, text, new global::_003C_003Ez__ReadOnlyArray<string>(new string[2]
		{
			text,
			flag ? "The projected DECA facial anchors agree with the same-frame MediaPipe observations." : "Rejected: projected DECA geometry did not agree with the same-frame MediaPipe facial geometry."
		}));
	}

	private static void AddEyeCenters(ICollection<AnchorPair> anchors, IReadOnlyDictionary<int, FaceMeshLandmarkPoint> deca, IReadOnlyDictionary<int, FaceMeshLandmarkPoint> mediaPipe, int width, int height)
	{
		if (TryProjectedMean(deca, 36, 41, out var mean) && TryProjectedMean(deca, 42, 47, out var mean2) && TryObservedMean(mediaPipe, MediaPipeEyeA, width, height, out var mean3) && TryObservedMean(mediaPipe, MediaPipeEyeB, width, height, out var mean4))
		{
			AddPair(anchors, "eye", mean, mean2, mean3, mean4);
		}
	}

	private static void AddJawContour(ICollection<AnchorPair> anchors, IReadOnlyDictionary<int, FaceMeshLandmarkPoint> deca, IReadOnlyDictionary<int, FaceMeshLandmarkPoint> mediaPipe, int width, int height)
	{
		List<Point2> list = new List<Point2>(17);
		for (int i = 0; i <= 16; i++)
		{
			if (!TryProjectedPoint(deca, i, out var point))
			{
				return;
			}
			list.Add(point);
		}
		List<Point2> list2 = new List<Point2>(MediaPipeJaw.Length);
		int[] mediaPipeJaw = MediaPipeJaw;
		foreach (int index in mediaPipeJaw)
		{
			if (!TryObservedPoint(mediaPipe, index, width, height, out var point2))
			{
				return;
			}
			list2.Add(point2);
		}
		List<Point2> list3 = ResamplePolyline(list2, list.Count);
		if (list3.Count == list.Count)
		{
			double num = Distance(list[0], list3[0]) + Distance(list[list.Count - 1], list3[list3.Count - 1]);
			if (Distance(list[0], list3[list3.Count - 1]) + Distance(list[list.Count - 1], list3[0]) < num)
			{
				list3.Reverse();
			}
			for (int k = 0; k < list.Count; k++)
			{
				anchors.Add(new AnchorPair($"jaw contour {k}", list[k], list3[k]));
			}
		}
	}

	private static List<Point2> ResamplePolyline(IReadOnlyList<Point2> points, int sampleCount)
	{
		if (points.Count < 2 || sampleCount < 2)
		{
			return new List<Point2>();
		}
		double[] array = new double[points.Count];
		for (int i = 1; i < points.Count; i++)
		{
			array[i] = array[i - 1] + Distance(points[i - 1], points[i]);
		}
		double num = array[^1];
		if (!double.IsFinite(num) || num <= 0.0)
		{
			return new List<Point2>();
		}
		List<Point2> list = new List<Point2>(sampleCount);
		int j = 1;
		for (int k = 0; k < sampleCount; k++)
		{
			double num2;
			for (num2 = num * (double)k / ((double)sampleCount - 1.0); j < array.Length - 1 && array[j] < num2; j++)
			{
			}
			double num3 = array[j - 1];
			double num4 = array[j] - num3;
			double num5 = ((num4 <= 0.0) ? 0.0 : ((num2 - num3) / num4));
			Point2 point = points[j - 1];
			Point2 point2 = points[j];
			list.Add(new Point2(point.X + (point2.X - point.X) * num5, point.Y + (point2.Y - point.Y) * num5));
		}
		return list;
	}

	private static void AddSingle(ICollection<AnchorPair> anchors, string name, IReadOnlyDictionary<int, FaceMeshLandmarkPoint> deca, int decaIndex, IReadOnlyDictionary<int, FaceMeshLandmarkPoint> mediaPipe, int mediaPipeIndex, int width, int height)
	{
		if (TryProjectedPoint(deca, decaIndex, out var point) && TryObservedPoint(mediaPipe, mediaPipeIndex, width, height, out var point2))
		{
			anchors.Add(new AnchorPair(name, point, point2));
		}
	}

	private static void AddIndexedPair(ICollection<AnchorPair> anchors, string name, IReadOnlyDictionary<int, FaceMeshLandmarkPoint> deca, int decaA, int decaB, IReadOnlyDictionary<int, FaceMeshLandmarkPoint> mediaPipe, int mediaPipeA, int mediaPipeB, int width, int height)
	{
		if (TryProjectedPoint(deca, decaA, out var point) && TryProjectedPoint(deca, decaB, out var point2) && TryObservedPoint(mediaPipe, mediaPipeA, width, height, out var point3) && TryObservedPoint(mediaPipe, mediaPipeB, width, height, out var point4))
		{
			AddPair(anchors, name, point, point2, point3, point4);
		}
	}

	private static void AddPair(ICollection<AnchorPair> anchors, string name, Point2 projectedA, Point2 projectedB, Point2 observedA, Point2 observedB)
	{
		SortByX(ref projectedA, ref projectedB);
		SortByX(ref observedA, ref observedB);
		anchors.Add(new AnchorPair(name + " left", projectedA, observedA));
		anchors.Add(new AnchorPair(name + " right", projectedB, observedB));
	}

	private static bool TryProjectedPoint(IReadOnlyDictionary<int, FaceMeshLandmarkPoint> points, int index, out Point2 point)
	{
		if (points.TryGetValue(index, out FaceMeshLandmarkPoint value) && double.IsFinite(value.X) && double.IsFinite(value.Y))
		{
			point = new Point2(value.X, value.Y);
			return true;
		}
		point = default(Point2);
		return false;
	}

	private static bool TryObservedPoint(IReadOnlyDictionary<int, FaceMeshLandmarkPoint> points, int index, int width, int height, out Point2 point)
	{
		if (points.TryGetValue(index, out FaceMeshLandmarkPoint value) && double.IsFinite(value.X) && double.IsFinite(value.Y))
		{
			point = new Point2(value.X * (double)width, value.Y * (double)height);
			return true;
		}
		point = default(Point2);
		return false;
	}

	private static bool TryProjectedMean(IReadOnlyDictionary<int, FaceMeshLandmarkPoint> points, int firstIndex, int lastIndex, out Point2 mean)
	{
		double num = 0.0;
		double num2 = 0.0;
		int num3 = 0;
		for (int i = firstIndex; i <= lastIndex; i++)
		{
			if (TryProjectedPoint(points, i, out var point))
			{
				num += point.X;
				num2 += point.Y;
				num3++;
			}
		}
		mean = ((num3 == 0) ? default(Point2) : new Point2(num / (double)num3, num2 / (double)num3));
		return num3 > 0;
	}

	private static bool TryObservedMean(IReadOnlyDictionary<int, FaceMeshLandmarkPoint> points, IReadOnlyList<int> indices, int width, int height, out Point2 mean)
	{
		double num = 0.0;
		double num2 = 0.0;
		int num3 = 0;
		foreach (int index in indices)
		{
			if (TryObservedPoint(points, index, width, height, out var point))
			{
				num += point.X;
				num2 += point.Y;
				num3++;
			}
		}
		mean = ((num3 == 0) ? default(Point2) : new Point2(num / (double)num3, num2 / (double)num3));
		return num3 > 0;
	}

	private static void SortByX(ref Point2 first, ref Point2 second)
	{
		if (first.X > second.X)
		{
			Point2 point = second;
			Point2 point2 = first;
			first = point;
			second = point2;
		}
	}

	private static Point2 Midpoint(Point2 first, Point2 second)
	{
		return new Point2((first.X + second.X) * 0.5, (first.Y + second.Y) * 0.5);
	}

	private static double Distance(Point2 first, Point2 second)
	{
		return Math.Sqrt(Square(first.X - second.X) + Square(first.Y - second.Y));
	}

	private static double Square(double value)
	{
		return value * value;
	}
}
