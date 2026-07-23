using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public static class ApertureRegionRefiner
{
	public static ApertureRegionRefinement RefineEye(Mat gray, Rect face, Rect seed, bool? leftSide = null)
	{
		return Refine(gray, face, seed, isEye: true, leftSide, (Mat image, Rect box) => OpenCvApertureEstimator.EstimateEye(image, box));
	}

	public static ApertureRegionRefinement RefineMouth(Mat gray, Rect face, Rect seed)
	{
		return Refine(gray, face, seed, isEye: false, null, (Mat image, Rect box) => OpenCvApertureEstimator.EstimateMouth(image, box));
	}

	private static ApertureRegionRefinement Refine(Mat gray, Rect face, Rect seed, bool isEye, bool? leftSide, Func<Mat, Rect, ApertureEstimate> estimator)
	{
		Rect rect = new Rect(0, 0, gray.Width, gray.Height);
		Rect rect2 = ClampRect(seed, rect);
		if (rect2.Width <= 0 || rect2.Height <= 0)
		{
			return new ApertureRegionRefinement(default(Rect), ApertureEstimate.None, 0.0);
		}
		Rect box = rect2;
		ApertureEstimate estimate = estimator(gray, rect2);
		double num = ScoreEstimate(estimate, rect2, rect2, face, isEye, leftSide);
		foreach (Rect item in CreateCandidates(rect2, face, rect, isEye))
		{
			if (IsPlausibleCandidate(item, face, isEye, leftSide))
			{
				ApertureEstimate apertureEstimate = estimator(gray, item);
				double num2 = ScoreEstimate(apertureEstimate, item, rect2, face, isEye, leftSide);
				double num3 = (isEye ? 0.03 : 0.02);
				if (num2 > num + num3)
				{
					num = num2;
					box = item;
					estimate = apertureEstimate;
				}
			}
		}
		return new ApertureRegionRefinement(box, estimate, num);
	}

	private static IEnumerable<Rect> CreateCandidates(Rect seed, Rect face, Rect frameBounds, bool isEye)
	{
		yield return seed;
		double num = Math.Max(2.0, (double)face.Width * (isEye ? 0.055 : 0.045));
		double num2 = Math.Max(2.0, (double)face.Height * (isEye ? 0.075 : 0.065));
		(double, double)[] array = ((!isEye) ? new(double, double)[7]
		{
			(0.0 - num, 0.0),
			(num, 0.0),
			(0.0, 0.0 - num2),
			(0.0, num2),
			(0.0, num2 * 1.75),
			(0.0 - num, num2),
			(num, num2)
		} : new(double, double)[8]
		{
			(0.0 - num, 0.0),
			(num, 0.0),
			(0.0, 0.0 - num2),
			(0.0, num2),
			(0.0 - num, 0.0 - num2),
			(num, 0.0 - num2),
			(0.0 - num, num2),
			(num, num2)
		});
		(double X, double Y)[] array2 = array;
		for (int i = 0; i < array2.Length; i++)
		{
			(double, double) tuple = array2[i];
			yield return ClampRect(Transform(seed, tuple.Item1, tuple.Item2, 1.0, 1.0), frameBounds);
		}
		if (!isEye)
		{
			foreach (Rect item in CreateAnatomicalMouthCandidates(seed, face, frameBounds))
			{
				yield return item;
			}
		}
		(double, double)[] array3 = ((!isEye) ? new(double, double)[3]
		{
			(0.92, 0.9),
			(1.12, 1.12),
			(1.18, 0.95)
		} : new(double, double)[3]
		{
			(0.92, 0.86),
			(1.1, 1.08),
			(1.18, 0.92)
		});
		array2 = array3;
		for (int i = 0; i < array2.Length; i++)
		{
			(double, double) tuple2 = array2[i];
			yield return ClampRect(Transform(seed, 0.0, 0.0, tuple2.Item1, tuple2.Item2), frameBounds);
		}
	}

	private static IEnumerable<Rect> CreateAnatomicalMouthCandidates(Rect seed, Rect face, Rect frameBounds)
	{
		double num = (double)seed.X + (double)seed.Width / 2.0;
		double num2 = (double)face.X + (double)face.Width / 2.0;
		double num3 = num * 0.45 + num2 * 0.55;
		double num4 = (double)face.Y + (double)face.Height * 0.69;
		int num5 = Math.Max(seed.Width, (int)Math.Round((double)face.Width * 0.48));
		int num6 = Math.Max(seed.Height, (int)Math.Round((double)face.Height * 0.2));
		Rect expected = new Rect((int)Math.Round(num3 - (double)num5 / 2.0), (int)Math.Round(num4 - (double)num6 / 2.0), Math.Max(1, num5), Math.Max(1, num6));
		yield return ClampRect(expected, frameBounds);
		yield return ClampRect(Transform(expected, 0.0, (double)face.Height * 0.045, 1.1, 1.05), frameBounds);
		yield return ClampRect(Transform(expected, 0.0, (double)(-face.Height) * 0.035, 0.94, 0.92), frameBounds);
	}

	private static double ScoreEstimate(ApertureEstimate estimate, Rect candidate, Rect seed, Rect face, bool isEye, bool? leftSide)
	{
		double num = ScoreExpectedPosition(candidate, face, isEye, leftSide);
		double num2 = num;
		double num3 = ScoreCenterProximity(candidate, seed);
		if (!estimate.HasAperture)
		{
			return Math.Clamp((estimate.ContrastScore + estimate.SharpnessScore) / 2.0, 0.0, 1.0) * 0.04 + num2 * 0.06 + num3 * 0.04;
		}
		if (!isEye)
		{
			double num4 = ScoreExpectedAperturePosition(estimate, face);
			num2 = num * 0.3 + num4 * 0.7;
		}
		double num5 = ScoreOpeningPlausibility(estimate.AverageOpeningRatio ?? ((double)estimate.ApertureBox.Height / (double)Math.Max(1, estimate.ApertureBox.Width)), isEye);
		double num6 = Math.Clamp(estimate.ProfileCoverageRatio / (isEye ? 0.36 : 0.42), 0.0, 1.0);
		double num7 = Math.Clamp((double)estimate.ProfileSampleCount / Math.Max(8.0, (double)candidate.Width * 0.22), 0.0, 1.0);
		double num8 = Math.Clamp(estimate.DarkCoverageRatio / (isEye ? 0.3 : 0.36), 0.0, 1.0);
		double num9 = Math.Clamp(estimate.ContrastScore * 0.45 + estimate.SharpnessScore * 0.35 + (1.0 - Math.Clamp(estimate.GlareRatio / 0.2, 0.0, 1.0)) * 0.2, 0.0, 1.0);
		double num10 = estimate.Confidence * 0.42 + num6 * 0.16 + num7 * 0.12 + num2 * 0.1 + num9 * 0.08 + num5 * 0.06 + num3 * 0.04 + num8 * 0.02;
		if (!isEye)
		{
			return num10 * ScoreMouthApertureBand(estimate, face);
		}
		return num10;
	}

	private static bool IsPlausibleCandidate(Rect candidate, Rect face, bool isEye, bool? leftSide)
	{
		if (candidate.Width < 6 || candidate.Height < 4)
		{
			return false;
		}
		double num = (double)candidate.X + (double)candidate.Width / 2.0;
		double num2 = (double)candidate.Y + (double)candidate.Height / 2.0;
		int x = face.X;
		int y = face.Y;
		int right = face.Right;
		double num3 = (num - (double)x) / Math.Max(1.0, face.Width);
		double num4 = (num2 - (double)y) / Math.Max(1.0, face.Height);
		if (num < (double)x - (double)face.Width * 0.12 || num > (double)right + (double)face.Width * 0.12 || num2 < (double)y - (double)face.Height * 0.08 || num2 > (double)face.Bottom + (double)face.Height * 0.08)
		{
			return false;
		}
		if (isEye)
		{
			if ((num4 < 0.18 || num4 > 0.58) ? true : false)
			{
				return false;
			}
			if (leftSide == true && num3 > 0.58)
			{
				return false;
			}
			if (leftSide == false && num3 < 0.42)
			{
				return false;
			}
			if ((double)candidate.Width <= (double)face.Width * 0.46)
			{
				return (double)candidate.Height <= (double)face.Height * 0.3;
			}
			return false;
		}
		if (num4 > 0.46 && num4 < 0.86 && (double)candidate.Width <= (double)face.Width * 0.76)
		{
			return (double)candidate.Height <= (double)face.Height * 0.34;
		}
		return false;
	}

	private static double ScoreExpectedPosition(Rect candidate, Rect face, bool isEye, bool? leftSide)
	{
		double num = (double)candidate.X + (double)candidate.Width / 2.0;
		double num2 = (double)candidate.Y + (double)candidate.Height / 2.0;
		double num3 = (isEye ? ((double)face.X + (double)face.Width * ((leftSide == false) ? 0.67 : ((leftSide == true) ? 0.33 : 0.5))) : ((double)face.X + (double)face.Width * 0.5));
		double num4 = (isEye ? ((double)face.Y + (double)face.Height * 0.38) : ((double)face.Y + (double)face.Height * 0.68));
		double num5 = Math.Max(1.0, (double)face.Width * ((isEye && leftSide.HasValue) ? 0.22 : 0.34));
		double num6 = Math.Max(1.0, (double)face.Height * (isEye ? 0.16 : 0.18));
		double num7 = 1.0 - Math.Clamp(Math.Abs(num - num3) / num5, 0.0, 1.0);
		double num8 = 1.0 - Math.Clamp(Math.Abs(num2 - num4) / num6, 0.0, 1.0);
		return num7 * 0.45 + num8 * 0.55;
	}

	private static double ScoreExpectedAperturePosition(ApertureEstimate estimate, Rect face)
	{
		if (!estimate.HasAperture || estimate.ApertureBox.Width <= 0 || estimate.ApertureBox.Height <= 0)
		{
			return 0.0;
		}
		double num = (double)estimate.ApertureBox.X + (double)estimate.ApertureBox.Width / 2.0;
		double num2 = (double)estimate.ApertureBox.Y + (double)estimate.ApertureBox.Height / 2.0;
		double num3 = (double)face.X + (double)face.Width * 0.5;
		double num4 = (double)face.Y + (double)face.Height * 0.69;
		double num5 = 1.0 - Math.Clamp(Math.Abs(num - num3) / Math.Max(1.0, (double)face.Width * 0.38), 0.0, 1.0);
		double num6 = 1.0 - Math.Clamp(Math.Abs(num2 - num4) / Math.Max(1.0, (double)face.Height * 0.17), 0.0, 1.0);
		return num5 * 0.3 + num6 * 0.7;
	}

	private static double ScoreMouthApertureBand(ApertureEstimate estimate, Rect face)
	{
		if (!estimate.HasAperture || estimate.ApertureBox.Width <= 0 || estimate.ApertureBox.Height <= 0)
		{
			return 1.0;
		}
		double num = ((double)estimate.ApertureBox.Y + (double)estimate.ApertureBox.Height / 2.0 - (double)face.Y) / Math.Max(1.0, face.Height);
		if (num < 0.5)
		{
			return 0.38;
		}
		if (num < 0.58)
		{
			return 0.38 + (num - 0.5) / 0.08 * 0.34;
		}
		if (num <= 0.82)
		{
			return 1.0;
		}
		if (num <= 0.9)
		{
			return 1.0 - (num - 0.82) / 0.08 * 0.18;
		}
		return 0.72;
	}

	private static double ScoreOpeningPlausibility(double openingRatio, bool isEye)
	{
		if (openingRatio <= 0.0)
		{
			return 0.0;
		}
		if (isEye)
		{
			if (openingRatio <= 0.4)
			{
				return 1.0;
			}
			if (openingRatio <= 0.62)
			{
				return 1.0 - Math.Clamp((openingRatio - 0.4) / 0.22, 0.0, 1.0) * 0.72;
			}
			return 0.28 - Math.Clamp((openingRatio - 0.62) / 0.38, 0.0, 1.0) * 0.24;
		}
		if (openingRatio <= 0.92)
		{
			return 1.0;
		}
		return 1.0 - Math.Clamp((openingRatio - 0.92) / 0.92, 0.0, 1.0);
	}

	private static double ScoreCenterProximity(Rect first, Rect second)
	{
		double num = (double)first.X + (double)first.Width / 2.0;
		double num2 = (double)first.Y + (double)first.Height / 2.0;
		double num3 = (double)second.X + (double)second.Width / 2.0;
		double num4 = (double)second.Y + (double)second.Height / 2.0;
		double num5 = Math.Sqrt(Math.Pow(num - num3, 2.0) + Math.Pow(num2 - num4, 2.0));
		double num6 = Math.Sqrt(second.Width * second.Width + second.Height * second.Height);
		return 1.0 - Math.Clamp(num5 / Math.Max(1.0, num6 * 0.65), 0.0, 1.0);
	}

	private static Rect Transform(Rect rect, double offsetX, double offsetY, double scaleX, double scaleY)
	{
		double num = (double)rect.X + (double)rect.Width / 2.0 + offsetX;
		double num2 = (double)rect.Y + (double)rect.Height / 2.0 + offsetY;
		double num3 = Math.Max(1.0, (double)rect.Width * scaleX);
		double num4 = Math.Max(1.0, (double)rect.Height * scaleY);
		return new Rect((int)Math.Round(num - num3 / 2.0), (int)Math.Round(num2 - num4 / 2.0), Math.Max(1, (int)Math.Round(num3)), Math.Max(1, (int)Math.Round(num4)));
	}

	private static Rect ClampRect(Rect rect, Rect bounds)
	{
		if (bounds.Width <= 0 || bounds.Height <= 0)
		{
			return default(Rect);
		}
		int num = Math.Clamp(rect.X, bounds.X, Math.Max(bounds.X, bounds.Right - 1));
		int num2 = Math.Clamp(rect.Y, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - 1));
		int num3 = Math.Clamp(rect.Right, num + 1, bounds.Right);
		int num4 = Math.Clamp(rect.Bottom, num2 + 1, bounds.Bottom);
		return new Rect(num, num2, num3 - num, num4 - num2);
	}
}
