using System;
using System.Collections.Generic;
using System.Linq;
using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public static class AvatarObservationRanker
{
	public const int MaximumRetainedObservationCount = 360;

	public const double MinimumReconstructionConfidencePercent = 55.0;

	public const double MinimumCaptureQualityPercent = 45.0;

	private const double PoseThresholdDegrees = 10.0;

	private const double ReplacementMarginPercent = 2.0;

	public static AvatarObservationRankingDecision Rank(AvatarObservationCapture capture, IReadOnlyList<AvatarObservation> retained)
	{
		AvatarReconstructionSnapshot reconstruction = capture.Reconstruction;
		bool flag = FaceReconstructionBackendIds.IsDecaFlame(reconstruction.BackendId);
		bool flag2 = string.Equals(reconstruction.BackendId, "deca-flame-standard-model-checkpoint-v1", StringComparison.Ordinal);
		if (reconstruction.CanonicalIdentityVertices.Count < 1000)
		{
			return AvatarObservationRankingDecision.Reject(reconstruction.Source + " did not provide a full canonical identity scan.");
		}
		if (!flag && reconstruction.ReconstructionConfidencePercent < 55.0)
		{
			return AvatarObservationRankingDecision.Reject($"{reconstruction.Source} measured projection fit {reconstruction.ReconstructionConfidencePercent:0.#}% was below {55.0:0.#}%.");
		}
		if (!flag && capture.CaptureQuality.ScorePercent < 45.0)
		{
			return AvatarObservationRankingDecision.Reject($"Capture quality {capture.CaptureQuality.ScorePercent:0.#}% was below {45.0:0.#}%.");
		}
		double num = CalculateExpressionEnergy(reconstruction.ExpressionCoefficients);
		string poseBucket = CreatePoseBucket(reconstruction.ARotationAroundXDegrees, reconstruction.BRotationAroundYDegrees, reconstruction.CRotationAroundZDegrees, capture.FaceGeometry.RelativeDistanceScale);
		int num2 = retained.Count((AvatarObservation item) => string.Equals(item.PoseBucket, poseBucket, StringComparison.Ordinal));
		double num3 = Math.Clamp(100.0 - (double)num2 * 16.0, 20.0, 100.0);
		double value = (flag ? 100.0 : Math.Clamp(reconstruction.ReconstructionConfidencePercent - Math.Min(35.0, num * 0.3), 0.0, 100.0));
		double num4 = Math.Clamp(reconstruction.ReconstructionConfidencePercent * 0.55 + capture.CaptureQuality.ScorePercent * 0.25 + capture.CaptureQuality.StabilityScorePercent * 0.2 - num * 0.15, 0.0, 100.0);
		double num5 = CalculateExpressionNovelty(reconstruction.ExpressionCoefficients, retained);
		double num6 = Math.Clamp(reconstruction.ReconstructionConfidencePercent * 0.5 + capture.CaptureQuality.ScorePercent * 0.25 + num5 * 0.25, 0.0, 100.0);
		double value2 = Math.Clamp(Math.Max(num4, num6 * 0.92) * 0.58 + num3 * 0.32 + Math.Min(reconstruction.ReconstructionConfidencePercent, capture.CaptureQuality.ScorePercent) * 0.1, 0.0, 100.0);
		double num7 = NormalizeCoefficientDelta(reconstruction.CurrentModelCoefficientDeltaRms);
		if (flag)
		{
			value2 = Math.Clamp(100.0 / (1.0 + num7 * 8.0) * 0.8 + capture.CaptureQuality.StabilityScorePercent * 0.15 + num3 * 0.05, 0.0, 100.0);
		}
		AvatarObservation avatarObservation = new AvatarObservation
		{
			ObservationId = Guid.NewGuid().ToString("N"),
			RequestId = reconstruction.RequestId,
			SampleId = (string.IsNullOrWhiteSpace(reconstruction.RequestId) ? $"avatar-{reconstruction.CapturedAtUtc.Ticks}" : ("avatar-" + reconstruction.RequestId)),
			CapturedAtUtc = ((reconstruction.CapturedAtUtc == default(DateTime)) ? DateTime.UtcNow : reconstruction.CapturedAtUtc),
			BackendId = reconstruction.BackendId,
			Source = reconstruction.Source,
			ReconstructionConfidencePercent = Round(reconstruction.ReconstructionConfidencePercent),
			ModelSequenceNumber = reconstruction.CurrentModelSequenceNumber,
			ModelCoefficientDeltaRms = Round(num7),
			SampleQualityPercent = Round(capture.CaptureQuality.ScorePercent),
			EyeQualityPercent = Round(capture.CaptureQuality.EyeEvidenceScorePercent),
			MouthQualityPercent = Round(capture.CaptureQuality.MouthEvidenceScorePercent),
			BrowQualityPercent = Round(capture.CaptureQuality.StabilityScorePercent),
			StabilityQualityPercent = Round(capture.CaptureQuality.StabilityScorePercent),
			ARotationAroundXDegrees = Round(reconstruction.ARotationAroundXDegrees),
			BRotationAroundYDegrees = Round(reconstruction.BRotationAroundYDegrees),
			CRotationAroundZDegrees = Round(reconstruction.CRotationAroundZDegrees),
			XHorizontalPercent = Round(capture.FaceGeometry.XHorizontalPercent),
			YVerticalPercent = Round(capture.FaceGeometry.YVerticalPercent),
			RelativeDistanceScale = RoundNullable(capture.FaceGeometry.RelativeDistanceScale),
			ApparentDistanceUnits = RoundNullable(capture.FaceGeometry.ApparentDistanceUnits),
			FaceWidthPercent = RoundNullable(capture.CaptureQuality.FaceWidthPercent),
			FaceHeightPercent = RoundNullable(capture.CaptureQuality.FaceHeightPercent),
			IdentityWeightPercent = Round(value),
			ExpressionWeightPercent = (flag ? 100.0 : Round(reconstruction.ReconstructionConfidencePercent)),
			IdentityScorePercent = Round(num4),
			AnimationScorePercent = Round(num6),
			CoverageScorePercent = Round(num3),
			RetentionScorePercent = Round(value2),
			ExpressionEnergyPercent = Round(num),
			PoseBucket = poseBucket,
			IdentityUse = ((!flag2) ? (flag ? "Lowest-delta recurrent identity anchor from a bounded five-result convergence window." : ((num >= 42.0) ? "Expression-rich observation retained primarily for animation range." : ("Identity-friendly canonical " + reconstruction.Source + " observation."))) : ((reconstruction.PinnedStillPassCount == 0) ? "Human-accepted Standard Model checkpoint with an exact paired source frame." : "Sequential Standard Model checkpoint produced by holding one still fixed until convergence.")),
			TrustDecision = reconstruction.TrustDecision,
			DenseVertexCount = reconstruction.Vertices.Count,
			CanonicalVertexCount = reconstruction.CanonicalIdentityVertices.Count,
			ShapeCoefficients = reconstruction.ShapeCoefficients.Select(Round).ToList(),
			ExpressionCoefficients = reconstruction.ExpressionCoefficients.Select(Round).ToList(),
			Warnings = reconstruction.Warnings.ToList()
		};
		if (flag2)
		{
			if (reconstruction.PinnedStillPassCount == 0)
			{
				return AvatarObservationRankingDecision.Accept(avatarObservation, null, $"Human accepted this Standard Model checkpoint at coefficient delta RMS {num7:0.000000}.");
			}
			return AvatarObservationRankingDecision.Accept(avatarObservation, null, reconstruction.PinnedStillConverged ? $"Standard Model checkpoint converged after {reconstruction.PinnedStillPassCount} recurrent pass(es) at coefficient delta RMS {num7:0.000000}." : $"Standard Model checkpoint reached its bounded {reconstruction.PinnedStillPassCount}-pass limit at coefficient delta RMS {num7:0.000000}.");
		}
		if (flag)
		{
			if (retained.Count < 360)
			{
				return AvatarObservationRankingDecision.Accept(avatarObservation, null, $"Lowest-delta recurrent result retained at coefficient delta RMS {num7:0.000000}.");
			}
			AvatarObservation avatarObservation2 = (from item in retained
				where string.Equals(item.PoseBucket, poseBucket, StringComparison.Ordinal)
				orderby NormalizeCoefficientDelta(item.ModelCoefficientDeltaRms) descending
				select item).FirstOrDefault() ?? retained.OrderByDescending((AvatarObservation item) => NormalizeCoefficientDelta(item.ModelCoefficientDeltaRms)).First();
			double num8 = NormalizeCoefficientDelta(avatarObservation2.ModelCoefficientDeltaRms);
			if (!(num7 + 1E-09 < num8))
			{
				return AvatarObservationRankingDecision.Reject($"The retained convergence anchor was already steadier ({num8:0.000000} versus {num7:0.000000}).");
			}
			return AvatarObservationRankingDecision.Accept(avatarObservation, avatarObservation2, $"A lower-delta recurrent anchor ({num7:0.000000}) replaced a noisier one ({num8:0.000000}).");
		}
		AvatarObservation avatarObservation3 = FindNearDuplicate(avatarObservation, retained);
		if ((object)avatarObservation3 != null)
		{
			double num9 = Math.Max(avatarObservation.RetentionScorePercent, avatarObservation.AnimationScorePercent);
			double num10 = Math.Max(avatarObservation3.RetentionScorePercent, avatarObservation3.AnimationScorePercent);
			if (!(num9 >= num10 + 2.0))
			{
				return AvatarObservationRankingDecision.Reject("The observation was a lower-value near duplicate of retained evidence.");
			}
			return AvatarObservationRankingDecision.Accept(avatarObservation, avatarObservation3, "A higher-quality observation replaced a near duplicate.");
		}
		if (retained.Count < 360)
		{
			return AvatarObservationRankingDecision.Accept(avatarObservation, null, "The observation added useful retained evidence.");
		}
		AvatarObservation avatarObservation4 = (from item in retained
			where string.Equals(item.PoseBucket, poseBucket, StringComparison.Ordinal)
			orderby item.RetentionScorePercent
			select item).FirstOrDefault();
		if ((object)avatarObservation4 != null && avatarObservation.RetentionScorePercent >= avatarObservation4.RetentionScorePercent + 2.0)
		{
			return AvatarObservationRankingDecision.Accept(avatarObservation, avatarObservation4, "A stronger observation replaced the weakest sample in its coverage bucket.");
		}
		Dictionary<string, int> bucketSizes = retained.GroupBy<AvatarObservation, string>((AvatarObservation item) => item.PoseBucket, StringComparer.Ordinal).ToDictionary<IGrouping<string, AvatarObservation>, string, int>((IGrouping<string, AvatarObservation> group) => group.Key, (IGrouping<string, AvatarObservation> group) => group.Count(), StringComparer.Ordinal);
		AvatarObservation avatarObservation5 = (from item in retained
			where bucketSizes.GetValueOrDefault(item.PoseBucket) > 1
			orderby item.RetentionScorePercent
			select item).FirstOrDefault();
		if ((object)avatarObservation5 == null || !(avatarObservation.RetentionScorePercent >= avatarObservation5.RetentionScorePercent + 4.0))
		{
			return AvatarObservationRankingDecision.Reject("The retained evidence set already contains stronger and more diverse observations.");
		}
		return AvatarObservationRankingDecision.Accept(avatarObservation, avatarObservation5, "A substantially stronger observation replaced weak redundant evidence.");
	}

	private static AvatarObservation? FindNearDuplicate(AvatarObservation candidate, IReadOnlyList<AvatarObservation> retained)
	{
		foreach (AvatarObservation item in retained.Where((AvatarObservation item) => string.Equals(item.PoseBucket, candidate.PoseBucket, StringComparison.Ordinal)))
		{
			if (!(Math.Sqrt(Square(candidate.ARotationAroundXDegrees - item.ARotationAroundXDegrees) + Square(candidate.BRotationAroundYDegrees - item.BRotationAroundYDegrees) + Square(candidate.CRotationAroundZDegrees - item.CRotationAroundZDegrees)) > 3.0))
			{
				double num = RelativeRmsPercent(candidate.ShapeCoefficients, item.ShapeCoefficients);
				double num2 = RelativeRmsPercent(candidate.ExpressionCoefficients, item.ExpressionCoefficients);
				if (num <= 0.75 && num2 <= 2.5)
				{
					return item;
				}
			}
		}
		return null;
	}

	private static double CalculateExpressionNovelty(IReadOnlyList<double> coefficients, IReadOnlyList<AvatarObservation> retained)
	{
		if (coefficients.Count == 0 || retained.Count == 0)
		{
			return 100.0;
		}
		return Math.Clamp(retained.Select((AvatarObservation item) => RelativeRmsPercent(coefficients, item.ExpressionCoefficients)).DefaultIfEmpty(100.0).Min() * 4.0, 0.0, 100.0);
	}

	private static string CreatePoseBucket(double a, double b, double c, double? z)
	{
		return $"A{AxisBucket(a)}-B{AxisBucket(b)}-C{AxisBucket(c)}-Z{DistanceBucket(z)}";
	}

	private static string AxisBucket(double value)
	{
		if (!(value <= -10.0))
		{
			if (!(value >= 10.0))
			{
				return "0";
			}
			return "P";
		}
		return "N";
	}

	private static string DistanceBucket(double? value)
	{
		if (value.HasValue)
		{
			double valueOrDefault = value.GetValueOrDefault();
			if (valueOrDefault <= 0.92)
			{
				return "L";
			}
			if (valueOrDefault >= 1.08)
			{
				return "H";
			}
		}
		return "0";
	}

	private static double CalculateExpressionEnergy(IReadOnlyList<double> coefficients)
	{
		if (coefficients.Count != 0)
		{
			return Math.Clamp(coefficients.Average((double value) => Math.Abs(value)) * 100.0, 0.0, 100.0);
		}
		return 0.0;
	}

	private static double RelativeRmsPercent(IReadOnlyList<double> left, IReadOnlyList<double> right)
	{
		int num = Math.Min(left.Count, right.Count);
		if (num == 0)
		{
			return 100.0;
		}
		double num2 = 0.0;
		double num3 = 0.0;
		for (int i = 0; i < num; i++)
		{
			num2 += Square(left[i] - right[i]);
			num3 += Square(right[i]);
		}
		return Math.Sqrt(num2 / (double)num) / Math.Max(1E-06, Math.Sqrt(num3 / (double)num)) * 100.0;
	}

	private static double Square(double value)
	{
		return value * value;
	}

	private static double NormalizeCoefficientDelta(double value)
	{
		if (!double.IsFinite(value) || !(value >= 0.0))
		{
			return 1000000.0;
		}
		return value;
	}

	private static double Round(double value)
	{
		if (!double.IsFinite(value))
		{
			return 0.0;
		}
		return Math.Round(value, 6);
	}

	private static double? RoundNullable(double? value)
	{
		if (value.HasValue)
		{
			double valueOrDefault = value.GetValueOrDefault();
			return Round(valueOrDefault);
		}
		return null;
	}
}
