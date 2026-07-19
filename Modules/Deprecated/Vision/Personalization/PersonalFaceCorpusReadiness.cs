namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceCorpusReadiness
{
    public string SchemaVersion { get; set; } = "personal-face-corpus-readiness-v1";

    public string SubjectId { get; set; } = PersonalFaceSubject.DefaultSubjectId;

    public string SubjectDisplayName { get; set; } = PersonalFaceSubject.DefaultSubjectDisplayName;

    public string SubjectCollectionMode { get; set; } = PersonalFaceSubject.ManualConfirmationMode;

    public string UnknownSubjectPolicy { get; set; } = PersonalFaceSubject.UnknownSubjectPolicy;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public int AcceptedBaselineSamples { get; set; }

    public int RecentMeasurementSamplesReviewed { get; set; }

    public int MotionUsableObservations { get; set; }

    public int MotionPairs { get; set; }

    public int IdentitySignatureSamples { get; set; }

    public int LeftEyeShapeSamples { get; set; }

    public int RightEyeShapeSamples { get; set; }

    public int OuterLipShapeSamples { get; set; }

    public int InnerLipShapeSamples { get; set; }

    public int JawShapeSamples { get; set; }

    public int LeftBrowShapeSamples { get; set; }

    public int RightBrowShapeSamples { get; set; }

    public int NoseBridgeShapeSamples { get; set; }

    public int NoseBaseShapeSamples { get; set; }

    public int LeftCheekSurfaceSamples { get; set; }

    public int RightCheekSurfaceSamples { get; set; }

    public int ForeheadSurfaceSamples { get; set; }

    public long MeasurementJournalBytes { get; set; }

    public long MeasurementBudgetBytes { get; set; } = PersonalFaceMeasurementJournal.DefaultBudgetBytes;

    public double MeasurementBudgetUsedPercent { get; set; }

    public double OverallReadinessPercent { get; set; }

    public double BaselineCoveragePercent { get; set; }

    public double LearningStabilityCoveragePercent { get; set; }

    public double MotionCoveragePercent { get; set; }

    public double PoseCoveragePercent { get; set; }

    public double PoseBucketCoveragePercent { get; set; }

    public int PoseBucketCoveredCount { get; set; }

    public int PoseBucketRequiredCount { get; set; }

    public List<PersonalFacePoseBucketProfile> PoseBuckets { get; set; } = [];

    public double DistanceCoveragePercent { get; set; }

    public double ZDistanceCoveragePercent { get; set; }

    public double ZDistanceEvidenceHealthPercent { get; set; }

    public int ZEstimateSamples { get; set; }

    public int ZCalibratedSamples { get; set; }

    public int ZCameraFovEstimatedSamples { get; set; }

    public int ZLearnedReferenceSamples { get; set; }

    public int ZApparentOnlySamples { get; set; }

    public double? ZApparentDistanceRange { get; set; }

    public double? ZRelativeToReferenceRange { get; set; }

    public double? AverageZConfidencePercent { get; set; }

    public double? MinimumZConfidencePercent { get; set; }

    public double? ZCalibratedRate { get; set; }

    public double? ZCameraFovEstimatedRate { get; set; }

    public double? ZLearnedReferenceRate { get; set; }

    public double? ZApparentOnlyRate { get; set; }

    public double ARotationAroundXCoveragePercent { get; set; }

    public double BRotationAroundYCoveragePercent { get; set; }

    public double CRotationAroundZCoveragePercent { get; set; }

    public double XYZABCCoveragePercent { get; set; }

    public double ExpressionCoveragePercent { get; set; }

    public double IdentityCoveragePercent { get; set; }

    public double IdentitySessionHealthPercent { get; set; }

    public string IdentitySessionAuditStage { get; set; } = "waiting";

    public string IdentitySessionAuditStatus { get; set; } = "waiting for comparable identity measurements";

    public int RecentIdentityMeasurementSamples { get; set; }

    public double? AverageRecentIdentityConfidencePercent { get; set; }

    public double? MinimumRecentIdentityConfidencePercent { get; set; }

    public double? RecentIdentityOutlierFrameRate { get; set; }

    public double ContourShapeCoveragePercent { get; set; }

    public double ContourDepthProfileHealthPercent { get; set; }

    public double SurfaceShapeCoveragePercent { get; set; }

    public double SurfaceDepthProfileHealthPercent { get; set; }

    public double SurfaceGeometryHealthPercent { get; set; }

    public double? SurfaceGeometryMinimumPatchHealthPercent { get; set; }

    public int SurfaceGeometryPatchCount { get; set; }

    public int SurfaceGeometryReviewPatchCount { get; set; }

    public string SurfaceGeometryStatus { get; set; } = "surface geometry waiting for measured patches";

    public double EyeBehindGlassesTrustPercent { get; set; }

    public double MouthJawTrustPercent { get; set; }

    public double DirectFeatureMeasurementTrustPercent { get; set; }

    public double ApertureConsistencyHealthPercent { get; set; }

    public PersonalFaceApertureConsistencyReport ApertureConsistency { get; set; } = new();

    public double EyeApertureReliabilityHealthPercent { get; set; }

    public double? EyeAgreementAveragePercent { get; set; }

    public double? EyeAgreementMinimumPercent { get; set; }

    public double QualityCoveragePercent { get; set; }

    public double CaptureQualityCoveragePercent { get; set; }

    public double StorageHealthPercent { get; set; }

    public double DataAuditHealthPercent { get; set; }

    public double PoseEstimationHealthPercent { get; set; }

    public double FeatureAnchoringHealthPercent { get; set; }

    public double PoseExplainedFeatureMotionHealthPercent { get; set; }

    public double? PoseExplainedFeatureObservedRange { get; set; }

    public double? PoseExplainedFeatureExpectedRange { get; set; }

    public double MouthVerticalAnchorHealthPercent { get; set; }

    public int MouthVerticalAnchorSamplesReviewed { get; set; }

    public double? MouthVerticalAnchorSuspiciousSampleRate { get; set; }

    public double PoseBucketConsistencyHealthPercent { get; set; }

    public PersonalFacePoseBucketConsistencyReport PoseBucketConsistency { get; set; } = new();

    public double JawDroopScaleHealthPercent { get; set; }

    public double MeasurementJournalCoveragePercent { get; set; }

    public double? FaceWidthRange { get; set; }

    public double? FaceHeightRange { get; set; }

    public double? HeadYawRangeDegrees { get; set; }

    public double? HeadPitchRangeDegrees { get; set; }

    public double? HeadRollRangeDegrees { get; set; }

    public double? EyeOpeningRange { get; set; }

    public double? MouthOpeningRange { get; set; }

    public double? JawDroopRange { get; set; }

    public double? MediaPipeBlinkRangePercent { get; set; }

    public double? MediaPipeJawOpenRangePercent { get; set; }

    public double? FaceAspectRatioRange { get; set; }

    public double? EyeMidlineXToFaceWidthRange { get; set; }

    public double? MouthCenterXToFaceWidthRange { get; set; }

    public double? EyeToMouthXOffsetToFaceWidthRange { get; set; }

    public double? InterEyeDistanceToFaceWidthRange { get; set; }

    public double? MouthWidthToFaceWidthRange { get; set; }

    public double? EyeMidlineYToFaceHeightRange { get; set; }

    public double? MouthCenterYToFaceHeightRange { get; set; }

    public double? EyeToMouthYDistanceToFaceHeightRange { get; set; }

    public double? EyeArtifactSuppressedRate { get; set; }

    public double? PossibleOneEyeArtifactRate { get; set; }

    public double? EyeReconstructedRate { get; set; }

    public double? MouthReconstructedRate { get; set; }

    public double LearningAnchorPercent { get; set; }

    public string LearningAnchorStatus { get; set; } = "waiting";

    public double MinimumTrackedDistributionWeight { get; set; }

    public double MaximumNextSampleInfluencePercent { get; set; }

    public double MaximumEventLikeNextSampleInfluencePercent { get; set; }

    public int CaptureQualitySamples { get; set; }

    public int CaptureQualityCanCollectSamples { get; set; }

    public int CaptureQualityAvatarGradeSamples { get; set; }

    public double CaptureQualityCanCollectRate { get; set; }

    public double CaptureQualityAvatarGradeRate { get; set; }

    public double? AverageCaptureQualityScorePercent { get; set; }

    public double? MinimumCaptureQualityScorePercent { get; set; }

    public double? AverageCaptureQualityCameraModeScorePercent { get; set; }

    public double? AverageCaptureQualityFaceScaleScorePercent { get; set; }

    public double? AverageCaptureQualityEyeScorePercent { get; set; }

    public double? AverageCaptureQualityMouthScorePercent { get; set; }

    public double? AverageCaptureQualityStabilityScorePercent { get; set; }

    public double? AverageCaptureQualityGlassesScorePercent { get; set; }

    public List<string> CaptureQualityIssueLabels { get; set; } = [];

    public List<string> DataAuditFindings { get; set; } = [];

    public List<string> Strengths { get; set; } = [];

    public List<string> Warnings { get; set; } = [];

    public List<string> NextCaptureSuggestions { get; set; } = [];

    public string StoragePolicy { get; set; } =
        "Measurement-only readiness report. No raw frames, images, video, or full landmark meshes are stored here.";

    public string Status
    {
        get
        {
            if (AcceptedBaselineSamples <= 0 && MotionUsableObservations <= 0)
            {
                return "learning data waiting for subject-confirmed measurements";
            }

            var strength = OverallReadinessPercent switch
            {
                >= 85d => "strong",
                >= 70d => "useful",
                >= 45d => "warming",
                _ => "starting"
            };
            return $"learning data {strength}; {OverallReadinessPercent:0.#}% ready";
        }
    }
}
