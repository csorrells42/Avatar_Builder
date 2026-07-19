namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFacePoseBucketConsistencyReport
{
    public string SchemaVersion { get; set; } = "personal-face-pose-bucket-consistency-v1";

    public double HealthPercent { get; set; } = 65d;

    public int ComparedPoseBucketCount { get; set; }

    public int SuspiciousPoseBucketCount { get; set; }

    public double MinimumPoseAxisHealthPercent { get; set; } = 100d;

    public string Status { get; set; } = "waiting for comparable pose buckets";

    public List<PersonalFacePoseBucketConsistencyComparison> Comparisons { get; set; } = [];

    public List<string> Findings { get; set; } = [];
}

public sealed class PersonalFacePoseBucketConsistencyComparison
{
    public string BucketId { get; set; } = "";

    public string Label { get; set; } = "";

    public int SampleCount { get; set; }

    public double HeadYawDegrees { get; set; }

    public double HeadPitchDegrees { get; set; }

    public double HeadRollDegrees { get; set; }

    public double? FaceAspectRatioDelta { get; set; }

    public double? EyeMidlineXToFaceWidthDelta { get; set; }

    public double? MouthCenterXToFaceWidthDelta { get; set; }

    public double? EyeToMouthXOffsetToFaceWidthDelta { get; set; }

    public double? InterEyeDistanceToFaceWidthDelta { get; set; }

    public double? MouthWidthToFaceWidthDelta { get; set; }

    public double? EyeMidlineYToFaceHeightDelta { get; set; }

    public double? MouthCenterYToFaceHeightDelta { get; set; }

    public double DriftScorePercent { get; set; }

    public double PoseAxisHealthPercent { get; set; } = 100d;

    public string PoseAxisReason { get; set; } = "";

    public string Status { get; set; } = "waiting";

    public string Reason { get; set; } = "";
}

public static class PersonalFacePoseBucketConsistencyAnalyzer
{
    private const int MinimumNeutralSamples = 20;
    private const int MinimumComparedSamples = 12;
    private const double FaceAspectRatioReviewDelta = 0.22d;
    private const double EyeHorizontalReviewDelta = 0.14d;
    private const double MouthHorizontalReviewDelta = 0.14d;
    private const double EyeMouthHorizontalReviewDelta = 0.10d;
    private const double InterEyeDistanceReviewDelta = 0.10d;
    private const double MouthWidthReviewDelta = 0.12d;
    private const double EyeMidlineReviewDelta = 0.10d;
    private const double MouthCenterReviewDelta = 0.10d;
    private const double ReviewDriftScorePercent = 85d;
    private const double SuspiciousDriftScorePercent = 115d;
    private const double ReviewPoseAxisHealthPercent = 80d;
    private const double SuspiciousPoseAxisHealthPercent = 55d;
    private const double StrongYawAxisDegrees = 12d;
    private const double StrongPitchAxisDegrees = 8d;
    private const double StrongRollAxisDegrees = 8d;

    public static PersonalFacePoseBucketConsistencyReport Analyze(
        IReadOnlyList<PersonalFacePoseBucketProfile>? buckets)
    {
        var report = new PersonalFacePoseBucketConsistencyReport();
        var normalized = (buckets ?? [])
            .Where(static bucket => !string.IsNullOrWhiteSpace(bucket.BucketId))
            .ToList();
        var neutral = normalized.FirstOrDefault(static bucket =>
            bucket.PrimaryNeutralReference
            && string.Equals(bucket.BucketId, PersonalFacePoseBuckets.FrontNeutral, StringComparison.OrdinalIgnoreCase));

        if (neutral is null || neutral.SampleCount < MinimumNeutralSamples || !neutral.HasIdentityProfile)
        {
            report.Status = $"waiting for front-neutral identity bucket ({neutral?.SampleCount ?? 0}/{MinimumNeutralSamples} samples)";
            return report;
        }

        foreach (var bucket in normalized
                     .Where(static bucket => !bucket.PrimaryNeutralReference && bucket.RequiredForAvatarCoverage)
                     .OrderBy(static bucket => bucket.BucketId, StringComparer.OrdinalIgnoreCase))
        {
            if (bucket.SampleCount < MinimumComparedSamples || !bucket.HasIdentityProfile)
            {
                continue;
            }

            report.Comparisons.Add(Compare(neutral, bucket));
        }

        report.ComparedPoseBucketCount = report.Comparisons.Count;
        if (report.ComparedPoseBucketCount == 0)
        {
            report.Status = $"waiting for turned-head pose buckets ({MinimumComparedSamples}+ samples each)";
            return report;
        }

        report.SuspiciousPoseBucketCount = report.Comparisons.Count(static comparison =>
            comparison.DriftScorePercent >= SuspiciousDriftScorePercent
            || comparison.PoseAxisHealthPercent < SuspiciousPoseAxisHealthPercent);
        report.MinimumPoseAxisHealthPercent = Round(report.Comparisons.Min(static comparison => comparison.PoseAxisHealthPercent));
        var worstDrift = report.Comparisons.Max(static comparison => comparison.DriftScorePercent);
        var averageDrift = report.Comparisons.Average(static comparison => comparison.DriftScorePercent);
        var worstPoseAxisDeficit = report.Comparisons.Max(static comparison => 100d - comparison.PoseAxisHealthPercent);
        report.HealthPercent = Round(Math.Clamp(100d - Math.Max(0d, worstDrift - 45d) * 0.62d - averageDrift * 0.10d, 20d, 100d));
        report.HealthPercent = Round(Math.Clamp(report.HealthPercent - worstPoseAxisDeficit * 0.42d, 20d, 100d));
        var hasSuspiciousAxis = report.Comparisons.Any(static comparison =>
            comparison.PoseAxisHealthPercent < SuspiciousPoseAxisHealthPercent);
        var hasReviewAxis = report.Comparisons.Any(static comparison =>
            comparison.PoseAxisHealthPercent < ReviewPoseAxisHealthPercent);
        report.Status = hasSuspiciousAxis
            ? "review pose bucket axis alignment"
            : report.SuspiciousPoseBucketCount > 0
                ? "review pose bucket identity drift"
                : worstDrift >= ReviewDriftScorePercent || hasReviewAxis
                ? "warming; some pose buckets are near drift or axis limits"
                : "consistent pose bucket identity ratios";

        foreach (var comparison in report.Comparisons
                     .Where(static comparison => comparison.DriftScorePercent >= SuspiciousDriftScorePercent)
                     .OrderByDescending(static comparison => comparison.DriftScorePercent)
                     .Take(4))
        {
            report.Findings.Add(
                $"pose bucket consistency drift in {comparison.Label}: identity-shaped ratios changed vs front-neutral ({comparison.Reason}); verify the overlay for face features sliding on the head.");
        }

        foreach (var comparison in report.Comparisons
                     .Where(static comparison => comparison.PoseAxisHealthPercent < SuspiciousPoseAxisHealthPercent)
                     .OrderBy(static comparison => comparison.PoseAxisHealthPercent)
                     .Take(4))
        {
            report.Findings.Add(
                $"pose bucket axis mismatch in {comparison.Label}: {comparison.PoseAxisReason}; head turns may be getting stored without the expected head pose, so review the overlay before learning from this bucket.");
        }

        return report;
    }

    private static PersonalFacePoseBucketConsistencyComparison Compare(
        PersonalFacePoseBucketProfile neutral,
        PersonalFacePoseBucketProfile bucket)
    {
        var faceAspectDelta = AbsDelta(neutral.FaceAspectRatio, bucket.FaceAspectRatio);
        var eyeXDelta = AbsDelta(neutral.EyeMidlineXToFaceWidth, bucket.EyeMidlineXToFaceWidth);
        var mouthXDelta = AbsDelta(neutral.MouthCenterXToFaceWidth, bucket.MouthCenterXToFaceWidth);
        var eyeMouthXDelta = AbsDelta(neutral.EyeToMouthXOffsetToFaceWidth, bucket.EyeToMouthXOffsetToFaceWidth);
        var interEyeDelta = AbsDelta(neutral.InterEyeDistanceToFaceWidth, bucket.InterEyeDistanceToFaceWidth);
        var mouthWidthDelta = AbsDelta(neutral.MouthWidthToFaceWidth, bucket.MouthWidthToFaceWidth);
        var eyeMidlineDelta = AbsDelta(neutral.EyeMidlineYToFaceHeight, bucket.EyeMidlineYToFaceHeight);
        var mouthCenterDelta = AbsDelta(neutral.MouthCenterYToFaceHeight, bucket.MouthCenterYToFaceHeight);
        var driftParts = new List<(string Label, double? Delta, double ReviewDelta)>
        {
            ("face aspect", faceAspectDelta, FaceAspectRatioReviewDelta),
            ("eye horizontal anchor", eyeXDelta, EyeHorizontalReviewDelta),
            ("mouth horizontal anchor", mouthXDelta, MouthHorizontalReviewDelta),
            ("eye-mouth horizontal offset", eyeMouthXDelta, EyeMouthHorizontalReviewDelta),
            ("eye spacing", interEyeDelta, InterEyeDistanceReviewDelta),
            ("mouth width", mouthWidthDelta, MouthWidthReviewDelta),
            ("eye height", eyeMidlineDelta, EyeMidlineReviewDelta),
            ("mouth height", mouthCenterDelta, MouthCenterReviewDelta)
        };
        var ranked = driftParts
            .Where(static part => part.Delta.HasValue)
            .Select(static part => new
            {
                part.Label,
                Delta = part.Delta!.Value,
                DriftPercent = Math.Abs(part.Delta!.Value) / part.ReviewDelta * 100d
            })
            .OrderByDescending(static part => part.DriftPercent)
            .ToList();
        var driftScore = ranked.Count == 0 ? 0d : Round(ranked[0].DriftPercent);
        var axis = AssessPoseAxis(bucket.BucketId, Value(bucket.HeadYawDegrees), Value(bucket.HeadPitchDegrees), Value(bucket.HeadRollDegrees));
        var status = driftScore >= SuspiciousDriftScorePercent || axis.HealthPercent < SuspiciousPoseAxisHealthPercent
            ? "suspicious"
            : driftScore >= ReviewDriftScorePercent || axis.HealthPercent < ReviewPoseAxisHealthPercent
                ? "review"
                : "consistent";
        var reason = ranked.Count == 0
            ? "no comparable identity ratios"
            : string.Join(", ", ranked.Take(3).Select(static part => $"{part.Label} delta {part.Delta:0.###}"));

        return new PersonalFacePoseBucketConsistencyComparison
        {
            BucketId = bucket.BucketId,
            Label = bucket.Label,
            SampleCount = bucket.SampleCount,
            HeadYawDegrees = Round(Value(bucket.HeadYawDegrees)),
            HeadPitchDegrees = Round(Value(bucket.HeadPitchDegrees)),
            HeadRollDegrees = Round(Value(bucket.HeadRollDegrees)),
            FaceAspectRatioDelta = Round(faceAspectDelta),
            EyeMidlineXToFaceWidthDelta = Round(eyeXDelta),
            MouthCenterXToFaceWidthDelta = Round(mouthXDelta),
            EyeToMouthXOffsetToFaceWidthDelta = Round(eyeMouthXDelta),
            InterEyeDistanceToFaceWidthDelta = Round(interEyeDelta),
            MouthWidthToFaceWidthDelta = Round(mouthWidthDelta),
            EyeMidlineYToFaceHeightDelta = Round(eyeMidlineDelta),
            MouthCenterYToFaceHeightDelta = Round(mouthCenterDelta),
            DriftScorePercent = driftScore,
            PoseAxisHealthPercent = Round(axis.HealthPercent),
            PoseAxisReason = axis.Reason,
            Status = status,
            Reason = reason
        };
    }

    private static PoseAxisAssessment AssessPoseAxis(
        string bucketId,
        double headYawDegrees,
        double headPitchDegrees,
        double headRollDegrees)
    {
        return bucketId switch
        {
            PersonalFacePoseBuckets.YawNegative => AssessSignedAxis(
                -headYawDegrees,
                StrongYawAxisDegrees,
                $"expected negative B rotation, measured B {headYawDegrees:0.#} deg"),
            PersonalFacePoseBuckets.YawPositive => AssessSignedAxis(
                headYawDegrees,
                StrongYawAxisDegrees,
                $"expected positive B rotation, measured B {headYawDegrees:0.#} deg"),
            PersonalFacePoseBuckets.PitchNegative => AssessSignedAxis(
                -headPitchDegrees,
                StrongPitchAxisDegrees,
                $"expected negative A rotation, measured A {headPitchDegrees:0.#} deg"),
            PersonalFacePoseBuckets.PitchPositive => AssessSignedAxis(
                headPitchDegrees,
                StrongPitchAxisDegrees,
                $"expected positive A rotation, measured A {headPitchDegrees:0.#} deg"),
            PersonalFacePoseBuckets.RollNegative => AssessSignedAxis(
                -headRollDegrees,
                StrongRollAxisDegrees,
                $"expected negative C rotation, measured C {headRollDegrees:0.#} deg"),
            PersonalFacePoseBuckets.RollPositive => AssessSignedAxis(
                headRollDegrees,
                StrongRollAxisDegrees,
                $"expected positive C rotation, measured C {headRollDegrees:0.#} deg"),
            _ => new PoseAxisAssessment(100d, "neutral or optional bucket")
        };
    }

    private static PoseAxisAssessment AssessSignedAxis(
        double signedAxisDegrees,
        double strongAxisDegrees,
        string reason)
    {
        var health = signedAxisDegrees <= 0d
            ? 18d
            : 18d + Math.Clamp(signedAxisDegrees / strongAxisDegrees, 0d, 1d) * 82d;
        return new PoseAxisAssessment(health, reason);
    }

    private static double? AbsDelta(PersonalMetricDistribution neutral, PersonalMetricDistribution bucket)
    {
        var neutralValue = ValueOrNull(neutral);
        var bucketValue = ValueOrNull(bucket);
        return neutralValue.HasValue && bucketValue.HasValue
            ? Math.Abs(bucketValue.Value - neutralValue.Value)
            : null;
    }

    private static double Value(PersonalMetricDistribution distribution)
    {
        return ValueOrNull(distribution) ?? 0d;
    }

    private static double? ValueOrNull(PersonalMetricDistribution distribution)
    {
        var value = distribution.ExponentialMovingAverage ?? distribution.Average;
        return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value)
            ? value.Value
            : null;
    }

    private static double? Round(double? value)
    {
        return value.HasValue ? Round(value.Value) : null;
    }

    private static double Round(double value)
    {
        return Math.Round(value, 6, MidpointRounding.AwayFromZero);
    }

    private sealed record PoseAxisAssessment(double HealthPercent, string Reason);
}
