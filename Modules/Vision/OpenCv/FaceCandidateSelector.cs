using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public static class FaceCandidateSelector
{
	public static FaceCandidate? SelectBest(IEnumerable<FaceCandidate> candidates, Rect? previousFace, int frameWidth, int frameHeight)
	{
		return (from candidate in candidates
			where IsPlausibleFace(candidate.Face, frameWidth, frameHeight)
			select new
			{
				Candidate = candidate,
				Score = ScoreCandidate(candidate.Face, previousFace, frameWidth, frameHeight, candidate.DetectorScore)
			} into candidate
			orderby candidate.Score descending
			select candidate).FirstOrDefault()?.Candidate;
	}

	public static double ScoreCandidate(Rect face, Rect? previousFace, int frameWidth, int frameHeight, double detectorScore)
	{
		double num = Math.Max(1.0, frameWidth * frameHeight);
		double num2 = Math.Max(1.0, face.Width * face.Height);
		double value = num2 / num;
		double num3 = Math.Clamp(detectorScore, 0.0, 1.0);
		double num4 = (double)face.Width / (double)Math.Max(1, face.Height);
		double num5 = 1.0 - Math.Clamp(Math.Abs(num4 - 0.86) / 0.7, 0.0, 1.0);
		double num6 = (double)face.X + (double)face.Width / 2.0;
		double num7 = (double)face.Y + (double)face.Height / 2.0;
		double num8 = (double)frameWidth / 2.0;
		double num9 = (double)frameHeight / 2.0;
		double num10 = Math.Sqrt(frameWidth * frameWidth + frameHeight * frameHeight);
		double num11 = 1.0 - Math.Clamp(Math.Sqrt(Math.Pow(num6 - num8, 2.0) + Math.Pow(num7 - num9, 2.0)) / Math.Max(1.0, num10 * 0.72), 0.0, 1.0);
		double num12;
		if (previousFace.HasValue)
		{
			Rect valueOrDefault = previousFace.GetValueOrDefault();
			num12 = Math.Max(1.0, valueOrDefault.Width * valueOrDefault.Height) / num;
		}
		else
		{
			num12 = 0.12;
		}
		double target = num12;
		double num13 = LogSimilarity(value, target, 6.0);
		double num14 = num3 * 0.45 + num13 * 0.25 + num5 * 0.15 + num11 * 0.05;
		if (previousFace.HasValue)
		{
			Rect valueOrDefault2 = previousFace.GetValueOrDefault();
			double num15 = OverlapOverSmaller(face, valueOrDefault2);
			double num16 = CenterProximity(face, valueOrDefault2);
			double num17 = LogSimilarity(num2, Math.Max(1.0, valueOrDefault2.Width * valueOrDefault2.Height), 4.0);
			num14 += num15 * 0.8 + num16 * 0.35 + num17 * 0.2;
			if (num15 >= 0.18)
			{
				num14 += 0.45;
			}
		}
		return num14;
	}

	public static bool IsContinuousWithPrevious(Rect face, Rect? previousFace)
	{
		if (previousFace.HasValue)
		{
			Rect valueOrDefault = previousFace.GetValueOrDefault();
			if (face.Width <= 0 || face.Height <= 0 || valueOrDefault.Width <= 0 || valueOrDefault.Height <= 0)
			{
				return false;
			}
			if (OverlapOverSmaller(face, valueOrDefault) >= 0.08)
			{
				return true;
			}
			double num = CenterProximity(face, valueOrDefault);
			double num2 = LogSimilarity(Math.Max(1.0, face.Width * face.Height), Math.Max(1.0, valueOrDefault.Width * valueOrDefault.Height), 5.0);
			if (num >= 0.48)
			{
				return num2 >= 0.2;
			}
			return false;
		}
		return true;
	}

	public static bool IsAcceptableTrackingCandidate(FaceCandidate candidate, Rect? previousFace, int frameWidth, int frameHeight, int missedFrames)
	{
		if (!previousFace.HasValue || IsContinuousWithPrevious(candidate.Face, previousFace))
		{
			return true;
		}
		return IsStrongGlobalReacquireCandidate(candidate, previousFace.Value, frameWidth, frameHeight, missedFrames);
	}

	public static bool IsStrongGlobalReacquireCandidate(FaceCandidate candidate, Rect previousFace, int frameWidth, int frameHeight, int missedFrames)
	{
		if (missedFrames < 2 || !IsPlausibleFace(candidate.Face, frameWidth, frameHeight))
		{
			return false;
		}
		double num = Math.Clamp(candidate.DetectorScore, 0.0, 1.0);
		if (num < 0.9)
		{
			return false;
		}
		Rect face = candidate.Face;
		double num2 = Math.Max(1.0, frameWidth * frameHeight);
		double num3 = Math.Max(1.0, face.Width * face.Height);
		if (num3 / num2 < 0.012)
		{
			return false;
		}
		double num4 = (double)face.Width / (double)Math.Max(1, face.Height);
		double num5 = 1.0 - Math.Clamp(Math.Abs(num4 - 0.86) / 0.7, 0.0, 1.0);
		double num6 = (double)face.X + (double)face.Width / 2.0;
		double num7 = (double)face.Y + (double)face.Height / 2.0;
		double num8 = Math.Sqrt(frameWidth * frameWidth + frameHeight * frameHeight);
		double num9 = 1.0 - Math.Clamp(Math.Sqrt(Math.Pow(num6 - (double)frameWidth / 2.0, 2.0) + Math.Pow(num7 - (double)frameHeight / 2.0, 2.0)) / Math.Max(1.0, num8 * 0.72), 0.0, 1.0);
		double num10 = LogSimilarity(num3, Math.Max(1.0, previousFace.Width * previousFace.Height), 7.0);
		if (num10 < 0.12)
		{
			return false;
		}
		double num11 = Math.Clamp((double)missedFrames / 6.0, 0.0, 1.0);
		return num * 0.54 + num5 * 0.18 + num10 * 0.16 + num9 * 0.08 + num11 * 0.04 >= 0.62;
	}

	private static bool IsPlausibleFace(Rect face, int frameWidth, int frameHeight)
	{
		if (face.Width <= 0 || face.Height <= 0)
		{
			return false;
		}
		double num = Math.Max(18.0, (double)Math.Min(frameWidth, frameHeight) / 80.0);
		if ((double)face.Width < num || (double)face.Height < num)
		{
			return false;
		}
		double num2 = (double)face.Width / (double)Math.Max(1, face.Height);
		double num3 = (double)(face.Width * face.Height) / (double)Math.Max(1, frameWidth * frameHeight);
		if (num2 > 0.45 && num2 < 1.75)
		{
			if (num3 > 0.0015)
			{
				return num3 < 0.92;
			}
			return false;
		}
		return false;
	}

	private static double CenterProximity(Rect current, Rect previous)
	{
		double num = (double)current.X + (double)current.Width / 2.0;
		double num2 = (double)current.Y + (double)current.Height / 2.0;
		double num3 = (double)previous.X + (double)previous.Width / 2.0;
		double num4 = (double)previous.Y + (double)previous.Height / 2.0;
		double num5 = Math.Sqrt(Math.Pow(num - num3, 2.0) + Math.Pow(num2 - num4, 2.0));
		double val = Math.Sqrt(current.Width * current.Width + current.Height * current.Height);
		double val2 = Math.Sqrt(previous.Width * previous.Width + previous.Height * previous.Height);
		return 1.0 - Math.Clamp(num5 / Math.Max(1.0, Math.Max(val, val2) * 1.25), 0.0, 1.0);
	}

	private static double OverlapOverSmaller(Rect first, Rect second)
	{
		int num = Math.Max(first.Left, second.Left);
		int num2 = Math.Max(first.Top, second.Top);
		int num3 = Math.Min(first.Right, second.Right);
		int num4 = Math.Min(first.Bottom, second.Bottom);
		int num5 = Math.Max(0, num3 - num);
		int num6 = Math.Max(0, num4 - num2);
		int num7 = num5 * num6;
		int num8 = Math.Min(first.Width * first.Height, second.Width * second.Height);
		if (num8 > 0)
		{
			return (double)num7 / (double)num8;
		}
		return 0.0;
	}

	private static double LogSimilarity(double value, double target, double toleranceFactor)
	{
		if (value <= 0.0 || target <= 0.0)
		{
			return 0.0;
		}
		double num = Math.Abs(Math.Log(value / target));
		return 1.0 - Math.Clamp(num / Math.Log(Math.Max(1.01, toleranceFactor)), 0.0, 1.0);
	}
}
