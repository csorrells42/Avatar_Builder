using AvatarBuilder.Modules.Vision.Reconstruction;

namespace AvatarBuilder.Modules.Storage.AvatarObservations;

public static class AvatarObservationRanker
{
    public const int MaximumRetainedObservationCount = 360;
    public const double MinimumReconstructionConfidencePercent = 55d;
    public const double MinimumCaptureQualityPercent = 45d;

    private const double PoseThresholdDegrees = 10d;
    private const double ReplacementMarginPercent = 2d;

    public static AvatarObservationRankingDecision Rank(
        AvatarObservationCapture capture,
        IReadOnlyList<AvatarObservation> retained)
    {
        var reconstruction = capture.Reconstruction;
        if (reconstruction.CanonicalIdentityVertices.Count < 1_000)
        {
            return AvatarObservationRankingDecision.Reject("3DDFA did not provide a full canonical identity scan.");
        }

        if (reconstruction.ReconstructionConfidencePercent < MinimumReconstructionConfidencePercent)
        {
            return AvatarObservationRankingDecision.Reject(
                $"3DDFA confidence {reconstruction.ReconstructionConfidencePercent:0.#}% was below {MinimumReconstructionConfidencePercent:0.#}%.");
        }

        if (capture.CaptureQuality.ScorePercent < MinimumCaptureQualityPercent)
        {
            return AvatarObservationRankingDecision.Reject(
                $"Capture quality {capture.CaptureQuality.ScorePercent:0.#}% was below {MinimumCaptureQualityPercent:0.#}%.");
        }

        var expressionEnergy = CalculateExpressionEnergy(reconstruction.ExpressionCoefficients);
        var poseBucket = CreatePoseBucket(
            reconstruction.ARotationAroundXDegrees,
            reconstruction.BRotationAroundYDegrees,
            reconstruction.CRotationAroundZDegrees,
            capture.FaceGeometry.RelativeDistanceScale);
        var bucketCount = retained.Count(item => string.Equals(item.PoseBucket, poseBucket, StringComparison.Ordinal));
        var coverageScore = Math.Clamp(100d - bucketCount * 16d, 20d, 100d);
        var identityWeight = Math.Clamp(
            reconstruction.ReconstructionConfidencePercent - Math.Min(35d, expressionEnergy * 0.30d),
            0d,
            100d);
        var identityScore = Math.Clamp(
            reconstruction.ReconstructionConfidencePercent * 0.55d
            + capture.CaptureQuality.ScorePercent * 0.25d
            + capture.CaptureQuality.StabilityScorePercent * 0.20d
            - expressionEnergy * 0.15d,
            0d,
            100d);
        var expressionNovelty = CalculateExpressionNovelty(reconstruction.ExpressionCoefficients, retained);
        var animationScore = Math.Clamp(
            reconstruction.ReconstructionConfidencePercent * 0.50d
            + capture.CaptureQuality.ScorePercent * 0.25d
            + expressionNovelty * 0.25d,
            0d,
            100d);
        var retentionScore = Math.Clamp(
            Math.Max(identityScore, animationScore * 0.92d) * 0.58d
            + coverageScore * 0.32d
            + Math.Min(reconstruction.ReconstructionConfidencePercent, capture.CaptureQuality.ScorePercent) * 0.10d,
            0d,
            100d);
        var candidate = new AvatarObservation
        {
            ObservationId = Guid.NewGuid().ToString("N"),
            RequestId = reconstruction.RequestId,
            SampleId = string.IsNullOrWhiteSpace(reconstruction.RequestId)
                ? $"3ddfa-{reconstruction.CapturedAtUtc.Ticks}"
                : $"3ddfa-{reconstruction.RequestId}",
            CapturedAtUtc = reconstruction.CapturedAtUtc == default ? DateTime.UtcNow : reconstruction.CapturedAtUtc,
            Source = reconstruction.Source,
            ReconstructionConfidencePercent = Round(reconstruction.ReconstructionConfidencePercent),
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
            IdentityWeightPercent = Round(identityWeight),
            ExpressionWeightPercent = Round(reconstruction.ReconstructionConfidencePercent),
            IdentityScorePercent = Round(identityScore),
            AnimationScorePercent = Round(animationScore),
            CoverageScorePercent = Round(coverageScore),
            RetentionScorePercent = Round(retentionScore),
            ExpressionEnergyPercent = Round(expressionEnergy),
            PoseBucket = poseBucket,
            IdentityUse = expressionEnergy >= 42d
                ? "Expression-rich observation retained primarily for animation range."
                : "Identity-friendly canonical 3DDFA observation.",
            TrustDecision = reconstruction.TrustDecision,
            DenseVertexCount = reconstruction.Vertices.Count,
            CanonicalVertexCount = reconstruction.CanonicalIdentityVertices.Count,
            ShapeCoefficients = reconstruction.ShapeCoefficients.Select(Round).ToList(),
            ExpressionCoefficients = reconstruction.ExpressionCoefficients.Select(Round).ToList(),
            Warnings = reconstruction.Warnings.ToList()
        };

        var duplicate = FindNearDuplicate(candidate, retained);
        if (duplicate is not null)
        {
            var candidateValue = Math.Max(candidate.RetentionScorePercent, candidate.AnimationScorePercent);
            var duplicateValue = Math.Max(duplicate.RetentionScorePercent, duplicate.AnimationScorePercent);
            return candidateValue >= duplicateValue + ReplacementMarginPercent
                ? AvatarObservationRankingDecision.Accept(candidate, duplicate, "A higher-quality observation replaced a near duplicate.")
                : AvatarObservationRankingDecision.Reject("The observation was a lower-value near duplicate of retained evidence.");
        }

        if (retained.Count < MaximumRetainedObservationCount)
        {
            return AvatarObservationRankingDecision.Accept(candidate, null, "The observation added useful retained evidence.");
        }

        var sameBucketLowest = retained
            .Where(item => string.Equals(item.PoseBucket, poseBucket, StringComparison.Ordinal))
            .OrderBy(static item => item.RetentionScorePercent)
            .FirstOrDefault();
        if (sameBucketLowest is not null
            && candidate.RetentionScorePercent >= sameBucketLowest.RetentionScorePercent + ReplacementMarginPercent)
        {
            return AvatarObservationRankingDecision.Accept(candidate, sameBucketLowest, "A stronger observation replaced the weakest sample in its coverage bucket.");
        }

        var bucketSizes = retained
            .GroupBy(static item => item.PoseBucket, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        var globalReplacement = retained
            .Where(item => bucketSizes.GetValueOrDefault(item.PoseBucket) > 1)
            .OrderBy(static item => item.RetentionScorePercent)
            .FirstOrDefault();
        return globalReplacement is not null
               && candidate.RetentionScorePercent >= globalReplacement.RetentionScorePercent + 4d
            ? AvatarObservationRankingDecision.Accept(candidate, globalReplacement, "A substantially stronger observation replaced weak redundant evidence.")
            : AvatarObservationRankingDecision.Reject("The retained evidence set already contains stronger and more diverse observations.");
    }

    private static AvatarObservation? FindNearDuplicate(
        AvatarObservation candidate,
        IReadOnlyList<AvatarObservation> retained)
    {
        foreach (var existing in retained.Where(item => string.Equals(item.PoseBucket, candidate.PoseBucket, StringComparison.Ordinal)))
        {
            var angleDistance = Math.Sqrt(
                Square(candidate.ARotationAroundXDegrees - existing.ARotationAroundXDegrees)
                + Square(candidate.BRotationAroundYDegrees - existing.BRotationAroundYDegrees)
                + Square(candidate.CRotationAroundZDegrees - existing.CRotationAroundZDegrees));
            if (angleDistance > 3d)
            {
                continue;
            }

            var shapeDistance = RelativeRmsPercent(candidate.ShapeCoefficients, existing.ShapeCoefficients);
            var expressionDistance = RelativeRmsPercent(candidate.ExpressionCoefficients, existing.ExpressionCoefficients);
            if (shapeDistance <= 0.75d && expressionDistance <= 2.5d)
            {
                return existing;
            }
        }

        return null;
    }

    private static double CalculateExpressionNovelty(
        IReadOnlyList<double> coefficients,
        IReadOnlyList<AvatarObservation> retained)
    {
        if (coefficients.Count == 0 || retained.Count == 0)
        {
            return 100d;
        }

        var nearest = retained
            .Select(item => RelativeRmsPercent(coefficients, item.ExpressionCoefficients))
            .DefaultIfEmpty(100d)
            .Min();
        return Math.Clamp(nearest * 4d, 0d, 100d);
    }

    private static string CreatePoseBucket(double a, double b, double c, double? z)
    {
        return $"A{AxisBucket(a)}-B{AxisBucket(b)}-C{AxisBucket(c)}-Z{DistanceBucket(z)}";
    }

    private static string AxisBucket(double value)
    {
        return value <= -PoseThresholdDegrees ? "N" : value >= PoseThresholdDegrees ? "P" : "0";
    }

    private static string DistanceBucket(double? value)
    {
        return value switch
        {
            <= 0.92d => "L",
            >= 1.08d => "H",
            _ => "0"
        };
    }

    private static double CalculateExpressionEnergy(IReadOnlyList<double> coefficients)
    {
        return coefficients.Count == 0
            ? 0d
            : Math.Clamp(coefficients.Average(static value => Math.Abs(value)) * 100d, 0d, 100d);
    }

    private static double RelativeRmsPercent(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        var count = Math.Min(left.Count, right.Count);
        if (count == 0)
        {
            return 100d;
        }

        var delta = 0d;
        var scale = 0d;
        for (var index = 0; index < count; index++)
        {
            delta += Square(left[index] - right[index]);
            scale += Square(right[index]);
        }

        return Math.Sqrt(delta / count) / Math.Max(0.000001d, Math.Sqrt(scale / count)) * 100d;
    }

    private static double Square(double value) => value * value;

    private static double Round(double value) => double.IsFinite(value) ? Math.Round(value, 6) : 0d;

    private static double? RoundNullable(double? value) => value is { } number ? Round(number) : null;
}

public sealed record AvatarObservationRankingDecision(
    bool Accepted,
    AvatarObservation? Candidate,
    AvatarObservation? Replacement,
    string Reason)
{
    public static AvatarObservationRankingDecision Accept(
        AvatarObservation candidate,
        AvatarObservation? replacement,
        string reason) => new(true, candidate, replacement, reason);

    public static AvatarObservationRankingDecision Reject(string reason) => new(false, null, null, reason);
}
