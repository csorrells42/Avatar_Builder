using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using AvatarBuilder.Modules.Vision.Analysis;

namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class AvatarCaptureQualityAnalyzer
{
	private sealed record FaceScaleScore(double ScorePercent, double? FaceWidthPercent, double? FaceHeightPercent);

	private const double MinimumCollectScorePercent = 62.0;

	private const double MinimumCollectCameraModeScorePercent = 60.0;

	private const double MinimumAvatarScorePercent = 80.0;

	private const double MinimumAvatarCameraModeScorePercent = 84.0;

	private const double MinimumAvatarEyeScorePercent = 72.0;

	private const double MinimumAvatarFaceScaleScorePercent = 70.0;

	private const double MinimumAvatarStabilityScorePercent = 70.0;

	public AvatarCaptureQualityAssessment Analyze(AvatarCaptureQualityInput input)
	{
		ArgumentNullException.ThrowIfNull(input, "input");
		List<string> list = new List<string>();
		List<string> list2 = new List<string>();
		if (!input.Metrics.HasFace || !input.LandmarkFrame.HasFace)
		{
			list.Add("no face landmark lock");
			list2.Add("Keep the face visible and let the tracker lock before collecting measurements.");
			return BuildAssessment("no-face", 0.0, canCollect: false, avatarReady: false, "no face landmark lock", 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, CalculateStorageScore(input, list, list2), null, null, list, list2);
		}
		double num = CalculateCameraModeScore(input, list, list2);
		FaceScaleScore faceScaleScore = CalculateFaceScaleScore(input, list, list2);
		double num2 = CalculateEyeEvidenceScore(input, list, list2);
		double num3 = CalculateMouthEvidenceScore(input, list, list2);
		double num4 = CalculateStabilityScore(input, list, list2);
		double num5 = CalculateGlassesRiskScore(input, list, list2);
		double num6 = CalculateStorageScore(input, list, list2);
		double num7 = Round(num * 0.14 + faceScaleScore.ScorePercent * 0.18 + num2 * 0.26 + num3 * 0.14 + num4 * 0.16 + num5 * 0.06 + num6 * 0.06);
		bool userLoggedIn = input.UserLoggedIn;
		bool flag = input.AvatarCaptureRequested && input.CaptureGateAccepted;
		if (!userLoggedIn)
		{
			list.Add("no avatar user is logged in");
			list2.Add("Use File > Login to identify the person in front of the webcam before collecting avatar data.");
		}
		else if (!input.AvatarCaptureRequested)
		{
			list.Add("avatar capture is stopped");
			list2.Add("Click Start Avatar Capture when the selected user is ready to collect 3DDFA samples.");
		}
		else if (!flag)
		{
			list.Add("avatar capture is waiting");
			if (!string.IsNullOrWhiteSpace(input.CaptureGateReason))
			{
				list2.Add(input.CaptureGateReason);
			}
		}
		bool flag2 = userLoggedIn && flag && num7 >= 62.0 && num >= 60.0 && num6 >= 35.0;
		bool flag3 = flag2 && num7 >= 80.0 && num >= 84.0 && num2 >= 72.0 && faceScaleScore.ScorePercent >= 70.0 && num4 >= 70.0;
		double num8 = num7;
		string text;
		if (!(num8 >= 80.0))
		{
			if (num8 >= 72.0)
			{
				goto IL_02a7;
			}
			text = ((num8 >= 62.0) ? "usable" : ((!(num8 >= 38.0)) ? "low" : "limited"));
		}
		else
		{
			if (!flag3)
			{
				goto IL_02a7;
			}
			text = "avatar-grade";
		}
		goto IL_02c9;
		IL_02a7:
		text = "strong";
		goto IL_02c9;
		IL_02c9:
		string label = text;
		string primaryReason = BuildPrimaryReason(flag2, flag3, num7, list);
		return BuildAssessment(label, num7, flag2, flag3, primaryReason, num, faceScaleScore.ScorePercent, num2, num3, num4, num5, num6, faceScaleScore.FaceWidthPercent, faceScaleScore.FaceHeightPercent, list, list2);
	}

	private static double CalculateCameraModeScore(AvatarCaptureQualityInput input, ICollection<string> issues, ICollection<string> suggestions)
	{
		int valueOrDefault;
		int valueOrDefault2;
		double num;
		if (!input.IsAutoCameraMode)
		{
			int? videoWidth = input.VideoWidth;
			if (videoWidth.HasValue)
			{
				valueOrDefault = videoWidth.GetValueOrDefault();
				videoWidth = input.VideoHeight;
				if (videoWidth.HasValue)
				{
					valueOrDefault2 = videoWidth.GetValueOrDefault();
					if (valueOrDefault >= 1920)
					{
						if (valueOrDefault < 3840)
						{
							if (valueOrDefault < 2560)
							{
								goto IL_0090;
							}
						}
						else if (valueOrDefault2 >= 2160)
						{
							num = 100.0;
							goto IL_00e1;
						}
						if (valueOrDefault2 < 1440)
						{
							goto IL_0090;
						}
						num = 84.0;
						goto IL_00e1;
					}
					if (valueOrDefault >= 1280)
					{
						goto IL_0098;
					}
					goto IL_00d6;
				}
			}
		}
		issues.Add("camera mode is auto or unknown");
		suggestions.Add("Use the explicit 3840x2160 30 fps mode when practical.");
		return 58.0;
		IL_0090:
		if (valueOrDefault2 < 1080)
		{
			goto IL_0098;
		}
		num = 68.0;
		goto IL_00e1;
		IL_00d6:
		num = 25.0;
		goto IL_00e1;
		IL_00e1:
		double num2 = num;
		if (valueOrDefault < 1920 || valueOrDefault2 < 1080)
		{
			issues.Add($"camera mode is low resolution: {valueOrDefault}x{valueOrDefault2}");
			suggestions.Add("Switch to 4K or at least 1080p before collecting long-term face measurements.");
		}
		double num3 = input.FramesPerSecond ?? 0.0;
		num = ((num3 >= 29.0) ? 100.0 : ((num3 >= 20.0) ? 82.0 : ((num3 >= 10.0) ? 60.0 : ((!(num3 > 0.0)) ? 70.0 : 38.0))));
		double num4 = num;
		if (num3 > 0.0 && num3 < 20.0)
		{
			issues.Add($"camera frame rate is low: {num3:0.#} fps");
			suggestions.Add("Use 30 fps when possible so blink and jaw motion timing stay useful.");
		}
		string text = input.InputFormat ?? "";
		double num5 = ((text.Contains("mjpg", StringComparison.OrdinalIgnoreCase) || text.Contains("mjpeg", StringComparison.OrdinalIgnoreCase) || text.Contains("nv12", StringComparison.OrdinalIgnoreCase) || text.Contains("yuy2", StringComparison.OrdinalIgnoreCase)) ? 100.0 : (string.IsNullOrWhiteSpace(text) ? 84.0 : 74.0));
		return Round(num2 * 0.66 + num4 * 0.24 + num5 * 0.1);
		IL_0098:
		if (valueOrDefault2 < 720)
		{
			goto IL_00d6;
		}
		num = 45.0;
		goto IL_00e1;
	}

	private static FaceScaleScore CalculateFaceScaleScore(AvatarCaptureQualityInput input, ICollection<string> issues, ICollection<string> suggestions)
	{
		Rect? bounds = GetBounds(input.LandmarkFrame.FaceContour);
		if (!bounds.HasValue)
		{
			issues.Add("face contour unavailable");
			suggestions.Add("Wait for dense face landmarks before collecting avatar measurements.");
			return new FaceScaleScore(0.0, null, null);
		}
		double num = bounds.Value.Width * 100.0;
		double num2 = bounds.Value.Height * 100.0;
		double value = ScoreIdealRange(num, 18.0, 55.0, 9.0, 72.0) * 0.55 + ScoreIdealRange(num2, 24.0, 70.0, 13.0, 86.0) * 0.45;
		if (num < 14.0 || num2 < 18.0)
		{
			issues.Add($"face is small in frame: {num:0}% wide, {num2:0}% tall");
			suggestions.Add("Move closer, improve lighting, or let the camera track tighter for eyelid detail.");
		}
		else if (num > 70.0 || num2 > 86.0)
		{
			issues.Add($"face is very close/cropped: {num:0}% wide, {num2:0}% tall");
			suggestions.Add("Move back slightly so the full face, eyes, lips, and jaw remain visible.");
		}
		return new FaceScaleScore(Round(value), Round(num), Round(num2));
	}

	private static double CalculateEyeEvidenceScore(AvatarCaptureQualityInput input, ICollection<string> issues, ICollection<string> suggestions)
	{
		FaceLandmarkMetrics metrics = input.Metrics;
		double num = (input.LandmarkFrame.HasEyeContours ? 100.0 : 30.0);
		double num2 = Math.Clamp(metrics.EyeConfidence * 100.0, 0.0, 100.0);
		double num3 = Math.Clamp(metrics.EyeMeasurementQualityPercent, 0.0, 100.0);
		double num4 = (metrics.EyeImageQualityAvailable ? Math.Clamp((100.0 - metrics.EyeGlarePercent) * 0.36 + metrics.EyeContrastPercent * 0.34 + metrics.EyeSharpnessPercent * 0.3, 0.0, 100.0) : 64.0);
		double num5 = (metrics.AnyEyeReconstructed ? 12.0 : 0.0);
		double num6 = (metrics.EyeArtifactSuppressed ? 10.0 : 0.0);
		double value = Round(num * 0.2 + num2 * 0.22 + num3 * 0.36 + num4 * 0.22 - num5 - num6);
		if (!metrics.IsEyeMeasurementUsable)
		{
			issues.Add("eye measurement is weak");
			suggestions.Add("Reduce glasses glare, sharpen focus, and keep both eyelids visible.");
		}
		if (metrics.EyeImageQualityAvailable && metrics.EyeGlarePercent > 32.0)
		{
			issues.Add($"eye glare is high: {metrics.EyeGlarePercent:0}%");
			suggestions.Add("Shift monitor brightness, room light, camera angle, or glasses angle to reduce reflections.");
		}
		return Math.Clamp(value, 0.0, 100.0);
	}

	private static double CalculateMouthEvidenceScore(AvatarCaptureQualityInput input, ICollection<string> issues, ICollection<string> suggestions)
	{
		FaceLandmarkMetrics metrics = input.Metrics;
		double num = (input.LandmarkFrame.HasMouthContours ? 100.0 : 35.0);
		double num2 = Math.Clamp(metrics.MouthConfidence * 100.0, 0.0, 100.0);
		double num3 = Math.Clamp(metrics.MouthMeasurementQualityPercent, 0.0, 100.0);
		double num4 = (metrics.MouthImageQualityAvailable ? Math.Clamp((100.0 - metrics.MouthGlarePercent) * 0.22 + metrics.MouthContrastPercent * 0.4 + metrics.MouthSharpnessPercent * 0.38, 0.0, 100.0) : 64.0);
		double num5 = (metrics.MouthReconstructed ? 10.0 : 0.0);
		double value = Round(num * 0.18 + num2 * 0.2 + num3 * 0.38 + num4 * 0.24 - num5);
		if (!metrics.IsMouthMeasurementUsable || !metrics.IsJawDroopMeasurementUsable)
		{
			issues.Add("mouth/jaw measurement is weak");
			suggestions.Add("Keep the lower face visible and use enough light for lip contrast.");
		}
		return Math.Clamp(value, 0.0, 100.0);
	}

	private static double CalculateStabilityScore(AvatarCaptureQualityInput input, ICollection<string> issues, ICollection<string> suggestions)
	{
		FaceLockStabilityAnalysis stability = input.Stability;
		if (stability.SampleCount < 3)
		{
			issues.Add("face lock is still warming");
			suggestions.Add("Hold a stable pose briefly before collecting long-term measurements.");
			return 38.0;
		}
		double num = Round(stability.CompositeReliabilityPercent * 0.42 + stability.FaceContinuityPercent * 0.24 + stability.EyeReliabilityPercent * 0.22 + stability.MouthReliabilityPercent * 0.12);
		if (num < 68.0)
		{
			issues.Add($"face lock stability is limited: {num:0}%");
			suggestions.Add("Improve light, reduce motion blur, or wait for the tracker to settle.");
		}
		return Math.Clamp(num, 0.0, 100.0);
	}

	private static double CalculateGlassesRiskScore(AvatarCaptureQualityInput input, ICollection<string> issues, ICollection<string> suggestions)
	{
		FaceLandmarkMetrics metrics = input.Metrics;
		if (!metrics.EyeImageQualityAvailable)
		{
			if (!metrics.EyeArtifactSuppressed && !metrics.PossibleOneEyeArtifact)
			{
				return 70.0;
			}
			return 48.0;
		}
		double num = 100.0 - Math.Clamp(metrics.EyeGlarePercent * 0.95, 0.0, 55.0) - Math.Clamp((100.0 - metrics.EyeContrastPercent) * 0.18, 0.0, 18.0) - Math.Clamp((100.0 - metrics.EyeSharpnessPercent) * 0.16, 0.0, 16.0) - (metrics.PossibleOneEyeArtifact ? 12.0 : 0.0) - (metrics.EyeArtifactSuppressed ? 10.0 : 0.0);
		if (num < 58.0)
		{
			issues.Add("glasses/eye artifact risk is high");
			suggestions.Add("Collect a quick lighting/glare check before relying on these measurements for avatar capture.");
		}
		return Round(Math.Clamp(num, 0.0, 100.0));
	}

	private static double CalculateStorageScore(AvatarCaptureQualityInput input, ICollection<string> issues, ICollection<string> suggestions)
	{
		return 100.0;
	}

	private static AvatarCaptureQualityAssessment BuildAssessment(string label, double scorePercent, bool canCollect, bool avatarReady, string primaryReason, double cameraModeScore, double faceScaleScore, double eyeScore, double mouthScore, double stabilityScore, double glassesScore, double storageScore, double? faceWidthPercent, double? faceHeightPercent, IReadOnlyList<string> issues, IReadOnlyList<string> suggestions)
	{
		string statusLine = BuildStatusLine(label, scorePercent, avatarReady, primaryReason, faceWidthPercent, eyeScore, mouthScore, glassesScore);
		return new AvatarCaptureQualityAssessment
		{
			Label = label,
			ScorePercent = Round(scorePercent),
			CanCollectMeasurements = canCollect,
			StrongEnoughForAvatarLearning = avatarReady,
			PrimaryReason = primaryReason,
			StatusLine = statusLine,
			CameraModeScorePercent = Round(cameraModeScore),
			FaceScaleScorePercent = Round(faceScaleScore),
			EyeEvidenceScorePercent = Round(eyeScore),
			MouthEvidenceScorePercent = Round(mouthScore),
			StabilityScorePercent = Round(stabilityScore),
			GlassesRiskScorePercent = Round(glassesScore),
			StorageScorePercent = Round(storageScore),
			FaceWidthPercent = faceWidthPercent,
			FaceHeightPercent = faceHeightPercent,
			Issues = issues.Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList(),
			Suggestions = suggestions.Where((string suggestion) => !string.IsNullOrWhiteSpace(suggestion)).Distinct<string>(StringComparer.OrdinalIgnoreCase).Take(4)
				.ToList()
		};
	}

	private static string BuildPrimaryReason(bool canCollect, bool avatarReady, double score, IReadOnlyList<string> issues)
	{
		if (avatarReady)
		{
			return "strong enough for long-term avatar measurements";
		}
		if (canCollect)
		{
			return "usable for personal measurements; not avatar-grade yet";
		}
		if (issues.Count <= 0)
		{
			return $"quality score {score:0}% below collection threshold";
		}
		return issues[0];
	}

	private static string BuildStatusLine(string label, double score, bool avatarReady, string reason, double? faceWidthPercent, double eyeScore, double mouthScore, double glassesScore)
	{
		object obj;
		if (faceWidthPercent.HasValue)
		{
			double valueOrDefault = faceWidthPercent.GetValueOrDefault();
			obj = $"face {valueOrDefault:0}% frame";
		}
		else
		{
			obj = "face --";
		}
		string value = (string)obj;
		string value2 = (avatarReady ? "avatar-grade" : reason);
		IFormatProvider invariantCulture = CultureInfo.InvariantCulture;
		DefaultInterpolatedStringHandler handler = new DefaultInterpolatedStringHandler(56, 7, invariantCulture);
		handler.AppendLiteral("Capture quality: ");
		handler.AppendFormatted(label);
		handler.AppendLiteral(" ");
		handler.AppendFormatted(score, "0");
		handler.AppendLiteral("% | ");
		handler.AppendFormatted(value);
		handler.AppendLiteral(" | eyes ");
		handler.AppendFormatted(eyeScore, "0");
		handler.AppendLiteral("% | mouth ");
		handler.AppendFormatted(mouthScore, "0");
		handler.AppendLiteral("% | glasses ");
		handler.AppendFormatted(glassesScore, "0");
		handler.AppendLiteral("% | ");
		handler.AppendFormatted(value2);
		return string.Create(invariantCulture, ref handler);
	}

	private static Rect? GetBounds(IReadOnlyList<Point> points)
	{
		if (points.Count == 0)
		{
			return null;
		}
		double num = points.Min((Point point) => point.X);
		double num2 = points.Min((Point point) => point.Y);
		double num3 = points.Max((Point point) => point.X);
		double num4 = points.Max((Point point) => point.Y);
		if (!(num3 > num) || !(num4 > num2))
		{
			return null;
		}
		return new Rect(num, num2, num3 - num, num4 - num2);
	}

	private static double ScoreIdealRange(double value, double idealMin, double idealMax, double weakMin, double weakMax)
	{
		if (value >= idealMin && value <= idealMax)
		{
			return 100.0;
		}
		if (value < idealMin)
		{
			if (!(value <= weakMin))
			{
				return 42.0 + (value - weakMin) / (idealMin - weakMin) * 58.0;
			}
			return Math.Clamp(value / weakMin * 42.0, 0.0, 42.0);
		}
		if (!(value >= weakMax))
		{
			return 100.0 - (value - idealMax) / (weakMax - idealMax) * 55.0;
		}
		return 45.0;
	}

	private static double Round(double value)
	{
		if (!double.IsNaN(value) && !double.IsInfinity(value))
		{
			return Math.Round(value, 6, MidpointRounding.AwayFromZero);
		}
		return 0.0;
	}
}
