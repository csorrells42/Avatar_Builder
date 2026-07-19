namespace AvatarBuilder.Modules.Vision.Personalization;

public static class PersonalFaceLearningAuditGate
{
    public const int DefaultMinimumSamples = 60;
    public const double DefaultDataAuditHoldThresholdPercent = 50d;
    public const double DefaultPoseHoldThresholdPercent = 40d;
    public const double DefaultFeatureAnchoringHoldThresholdPercent = 40d;

    private static readonly string[] HighRiskFindingFragments =
    [
        "face-local feature proportions are drifting",
        "pose bucket consistency drift",
        "pose bucket axis mismatch",
        "recent identity-session high",
        "recent identity-session confidence is low",
        "C rotation changes but no B rotation changes",
        "surface geometry health is weak"
    ];

    public static PersonalFaceLearningAuditGateResult Evaluate(
        PersonalFaceCorpusReadiness readiness,
        int minimumSamples = DefaultMinimumSamples,
        double dataAuditHoldThresholdPercent = DefaultDataAuditHoldThresholdPercent,
        double poseHoldThresholdPercent = DefaultPoseHoldThresholdPercent,
        double featureAnchoringHoldThresholdPercent = DefaultFeatureAnchoringHoldThresholdPercent)
    {
        ArgumentNullException.ThrowIfNull(readiness);

        if (readiness.AcceptedBaselineSamples < Math.Max(1, minimumSamples))
        {
            return PersonalFaceLearningAuditGateResult.Allow(
                $"tracking audit warming: {readiness.AcceptedBaselineSamples}/{Math.Max(1, minimumSamples)} samples");
        }

        if (readiness.DataAuditHealthPercent <= 0d)
        {
            return PersonalFaceLearningAuditGateResult.Allow("tracking audit waiting for calculated data-audit health");
        }

        var highRiskFinding = readiness.DataAuditFindings.FirstOrDefault(IsHighRiskFinding);
        if (!string.IsNullOrWhiteSpace(highRiskFinding))
        {
            return PersonalFaceLearningAuditGateResult.Hold(
                $"tracking audit hold: {highRiskFinding}",
                readiness.DataAuditHealthPercent,
                readiness.PoseEstimationHealthPercent,
                readiness.FeatureAnchoringHealthPercent);
        }

        if (readiness.DataAuditHealthPercent < dataAuditHoldThresholdPercent)
        {
            return PersonalFaceLearningAuditGateResult.Hold(
                $"tracking audit hold: data audit health {readiness.DataAuditHealthPercent:0.#}% is below {dataAuditHoldThresholdPercent:0.#}%",
                readiness.DataAuditHealthPercent,
                readiness.PoseEstimationHealthPercent,
                readiness.FeatureAnchoringHealthPercent);
        }

        if (readiness.PoseEstimationHealthPercent is > 0d
            && readiness.PoseEstimationHealthPercent < poseHoldThresholdPercent)
        {
            return PersonalFaceLearningAuditGateResult.Hold(
                $"tracking audit hold: pose estimation health {readiness.PoseEstimationHealthPercent:0.#}% is below {poseHoldThresholdPercent:0.#}%",
                readiness.DataAuditHealthPercent,
                readiness.PoseEstimationHealthPercent,
                readiness.FeatureAnchoringHealthPercent);
        }

        if (readiness.FeatureAnchoringHealthPercent is > 0d
            && readiness.FeatureAnchoringHealthPercent < featureAnchoringHoldThresholdPercent)
        {
            return PersonalFaceLearningAuditGateResult.Hold(
                $"tracking audit hold: feature anchoring health {readiness.FeatureAnchoringHealthPercent:0.#}% is below {featureAnchoringHoldThresholdPercent:0.#}%",
                readiness.DataAuditHealthPercent,
                readiness.PoseEstimationHealthPercent,
                readiness.FeatureAnchoringHealthPercent);
        }

        return PersonalFaceLearningAuditGateResult.Allow(
            $"tracking audit allows learning: data audit {readiness.DataAuditHealthPercent:0.#}%, pose {readiness.PoseEstimationHealthPercent:0.#}%, anchoring {readiness.FeatureAnchoringHealthPercent:0.#}%");
    }

    private static bool IsHighRiskFinding(string finding)
    {
        return HighRiskFindingFragments.Any(fragment =>
            finding.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record PersonalFaceLearningAuditGateResult(
    bool HoldLearning,
    string Reason,
    double DataAuditHealthPercent,
    double PoseEstimationHealthPercent,
    double FeatureAnchoringHealthPercent)
{
    public static PersonalFaceLearningAuditGateResult Allow(string reason)
    {
        return new PersonalFaceLearningAuditGateResult(false, reason, 0d, 0d, 0d);
    }

    public static PersonalFaceLearningAuditGateResult Hold(
        string reason,
        double dataAuditHealthPercent,
        double poseEstimationHealthPercent,
        double featureAnchoringHealthPercent)
    {
        return new PersonalFaceLearningAuditGateResult(
            true,
            reason,
            dataAuditHealthPercent,
            poseEstimationHealthPercent,
            featureAnchoringHealthPercent);
    }
}
