using System;
using System.Collections.Generic;
using System.Windows;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceLockStabilityAnalyzer
{
	private sealed record Sample(DateTime CapturedAtUtc, Rect? FaceBounds, bool EyeUsable, bool MouthUsable, double EyeQualityPercent, double MouthQualityPercent, double OverallMeasurementQualityPercent);

	private static readonly TimeSpan StabilityWindow = TimeSpan.FromSeconds(12L);

	private readonly Queue<Sample> _samples = new Queue<Sample>();

	public void Reset()
	{
		_samples.Clear();
	}

	public FaceLockStabilityAnalysis Update(FaceFeatureDetection featureDetection, FaceLandmarkFrame frame, FaceLandmarkMetrics metrics)
	{
		if (!metrics.HasFace && !frame.HasFace && !featureDetection.HasFace)
		{
			Reset();
			return FaceLockStabilityAnalysis.Waiting;
		}
		DateTime capturedAtUtc = (metrics.HasFace ? metrics.CapturedAtUtc : (frame.HasFace ? frame.CapturedAtUtc : DateTime.UtcNow));
		_samples.Enqueue(new Sample(capturedAtUtc, GetFaceBounds(featureDetection, frame), metrics.IsEyeMeasurementUsable, metrics.IsMouthMeasurementUsable, metrics.EyeMeasurementQualityPercent, metrics.MouthMeasurementQualityPercent, metrics.OverallMeasurementQualityPercent));
		Trim(capturedAtUtc);
		return CreateAnalysis();
	}

	private void Trim(DateTime capturedAtUtc)
	{
		while (_samples.Count > 0 && capturedAtUtc - _samples.Peek().CapturedAtUtc > StabilityWindow)
		{
			_samples.Dequeue();
		}
	}

	private FaceLockStabilityAnalysis CreateAnalysis()
	{
		if (_samples.Count == 0)
		{
			return FaceLockStabilityAnalysis.Waiting;
		}
		int count = _samples.Count;
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		double num4 = 0.0;
		double num5 = 0.0;
		double num6 = 0.0;
		double num7 = 0.0;
		Rect? rect = null;
		DateTime dateTime = DateTime.MaxValue;
		DateTime dateTime2 = DateTime.MinValue;
		foreach (Sample sample in _samples)
		{
			dateTime = ((dateTime == DateTime.MaxValue) ? sample.CapturedAtUtc : dateTime);
			dateTime2 = sample.CapturedAtUtc;
			num2 += (sample.EyeUsable ? 1 : 0);
			num3 += (sample.MouthUsable ? 1 : 0);
			num4 += sample.EyeQualityPercent;
			num5 += sample.MouthQualityPercent;
			num6 += sample.OverallMeasurementQualityPercent;
			Rect? faceBounds = sample.FaceBounds;
			if (faceBounds.HasValue)
			{
				Rect valueOrDefault = faceBounds.GetValueOrDefault();
				if (rect.HasValue)
				{
					Rect valueOrDefault2 = rect.GetValueOrDefault();
					num7 += CalculatePairContinuity(valueOrDefault2, valueOrDefault);
				}
				rect = valueOrDefault;
				num++;
			}
		}
		double num8 = Rate(num, count);
		double num9 = num switch
		{
			0 => 0.0, 
			1 => 50.0, 
			_ => Math.Clamp(num7 / (double)(num - 1) * 100.0, 0.0, 100.0), 
		};
		double num10 = Rate(num2, count);
		double num11 = Rate(num3, count);
		double num12 = num4 / (double)count;
		double num13 = num5 / (double)count;
		double num14 = num6 / (double)count;
		double num15 = CalculateFeatureReliability(num8, num9, num10, num12);
		double num16 = CalculateFeatureReliability(num8, num9, num11, num13);
		double compositeReliabilityPercent = Math.Clamp(num8 * 0.18 + num9 * 0.26 + num15 * 0.34 + num16 * 0.12 + num14 * 0.1, 0.0, 100.0);
		return new FaceLockStabilityAnalysis
		{
			SampleCount = count,
			WindowSeconds = (dateTime2 - dateTime).TotalSeconds,
			FaceBoundsRatePercent = num8,
			FaceContinuityPercent = num9,
			EyeUsableRatePercent = num10,
			MouthUsableRatePercent = num11,
			AverageEyeQualityPercent = Math.Clamp(num12, 0.0, 100.0),
			AverageMouthQualityPercent = Math.Clamp(num13, 0.0, 100.0),
			AverageOverallQualityPercent = Math.Clamp(num14, 0.0, 100.0),
			EyeReliabilityPercent = num15,
			MouthReliabilityPercent = num16,
			CompositeReliabilityPercent = compositeReliabilityPercent
		};
	}

	private static double CalculateFeatureReliability(double faceBoundsRate, double continuityPercent, double usableRate, double qualityPercent)
	{
		return Math.Clamp(faceBoundsRate * 0.2 + continuityPercent * 0.24 + usableRate * 0.34 + qualityPercent * 0.22, 0.0, 100.0);
	}

	private static double CalculatePairContinuity(Rect previous, Rect current)
	{
		Point first = Center(previous);
		Point second = Center(current);
		double num = Distance(first, second);
		double num2 = Diagonal(previous);
		double num3 = Diagonal(current);
		double num4 = Math.Max(0.001, (num2 + num3) / 2.0);
		double num5 = 1.0 - Math.Clamp(num / (num4 * 1.2), 0.0, 1.0);
		double num6 = LogSimilarity(Math.Max(1E-06, current.Width * current.Height), Math.Max(1E-06, previous.Width * previous.Height), 3.5);
		return num5 * 0.72 + num6 * 0.28;
	}

	private static Rect? GetFaceBounds(FaceFeatureDetection featureDetection, FaceLandmarkFrame frame)
	{
		if (featureDetection.HasFace && featureDetection.FaceBox.Width > 0.0 && featureDetection.FaceBox.Height > 0.0)
		{
			return featureDetection.FaceBox;
		}
		return Bounds(frame.FaceContour);
	}

	private static Rect? Bounds(IReadOnlyList<Point> points)
	{
		if (points.Count == 0)
		{
			return null;
		}
		Point point = points[0];
		double num = point.X;
		double num2 = point.X;
		double num3 = point.Y;
		double num4 = point.Y;
		for (int i = 1; i < points.Count; i++)
		{
			Point point2 = points[i];
			num = Math.Min(num, point2.X);
			num2 = Math.Max(num2, point2.X);
			num3 = Math.Min(num3, point2.Y);
			num4 = Math.Max(num4, point2.Y);
		}
		if (!(num2 <= num) && !(num4 <= num3))
		{
			return new Rect(num, num3, num2 - num, num4 - num3);
		}
		return null;
	}

	private static Point Center(Rect rect)
	{
		return new Point(rect.Left + rect.Width / 2.0, rect.Top + rect.Height / 2.0);
	}

	private static double Distance(Point first, Point second)
	{
		double num = first.X - second.X;
		double num2 = first.Y - second.Y;
		return Math.Sqrt(num * num + num2 * num2);
	}

	private static double Diagonal(Rect rect)
	{
		return Math.Sqrt(rect.Width * rect.Width + rect.Height * rect.Height);
	}

	private static double LogSimilarity(double value, double target, double toleranceFactor)
	{
		double num = Math.Abs(Math.Log(value / target));
		return 1.0 - Math.Clamp(num / Math.Log(Math.Max(1.01, toleranceFactor)), 0.0, 1.0);
	}

	private static double Rate(int count, int total)
	{
		if (total > 0)
		{
			return (double)count / (double)total * 100.0;
		}
		return 0.0;
	}
}
