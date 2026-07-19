namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class MeasurementAvatarReadinessScores
{
    public double OverallReadinessPercent { get; set; }

    public double BaselineCoveragePercent { get; set; }

    public double LearningStabilityCoveragePercent { get; set; }

    public double MotionCoveragePercent { get; set; }

    public double PoseCoveragePercent { get; set; }

    public double PoseBucketCoveragePercent { get; set; }

    public double DistanceCoveragePercent { get; set; }

    public double ZDistanceCoveragePercent { get; set; }

    public double ZDistanceEvidenceHealthPercent { get; set; }

    public double ARotationAroundXCoveragePercent { get; set; }

    public double BRotationAroundYCoveragePercent { get; set; }

    public double CRotationAroundZCoveragePercent { get; set; }

    public double XYZABCCoveragePercent { get; set; }

    public double ExpressionCoveragePercent { get; set; }

    public double IdentityCoveragePercent { get; set; }

    public double IdentitySessionHealthPercent { get; set; }

    public string IdentitySessionAuditStage { get; set; } = "waiting";

    public string IdentitySessionAuditStatus { get; set; } = "waiting for comparable identity measurements";

    public double ContourShapeCoveragePercent { get; set; }

    public double ContourDepthProfileHealthPercent { get; set; }

    public double SurfaceShapeCoveragePercent { get; set; }

    public double SurfaceDepthProfileHealthPercent { get; set; }

    public double SurfaceGeometryHealthPercent { get; set; }

    public double EyeBehindGlassesTrustPercent { get; set; }

    public double MouthJawTrustPercent { get; set; }

    public double DirectFeatureMeasurementTrustPercent { get; set; }

    public double ApertureConsistencyHealthPercent { get; set; }

    public double EyeApertureReliabilityHealthPercent { get; set; }

    public double QualityCoveragePercent { get; set; }

    public double CaptureQualityCoveragePercent { get; set; }

    public double StorageHealthPercent { get; set; }

    public double DataAuditHealthPercent { get; set; }

    public double PoseEstimationHealthPercent { get; set; }

    public double FeatureAnchoringHealthPercent { get; set; }

    public double PoseExplainedFeatureMotionHealthPercent { get; set; }

    public double MouthVerticalAnchorHealthPercent { get; set; }

    public double PoseBucketConsistencyHealthPercent { get; set; }

    public double JawDroopScaleHealthPercent { get; set; }

    public double MeasurementJournalCoveragePercent { get; set; }
}
