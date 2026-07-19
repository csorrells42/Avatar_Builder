namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceCollectionAuditObservation
{
    public DateTime ReviewedAtUtc { get; set; }

    public bool SubjectConfirmed { get; set; }

    public bool PausedForEventOrCalibration { get; set; }

    public bool HasFace { get; set; }

    public bool PersonalModelAccepted { get; set; }

    public string PersonalModelRejectionKind { get; set; } = "";

    public string PersonalModelUpdateReason { get; set; } = "";

    public string CaptureQualityLabel { get; set; } = "";

    public double CaptureQualityScorePercent { get; set; }

    public bool CaptureQualityCanCollect { get; set; }

    public bool CaptureQualityAvatarGrade { get; set; }

    public string CaptureQualityReason { get; set; } = "";

    public double CaptureQualityCameraModeScorePercent { get; set; }

    public double CaptureQualityFaceScaleScorePercent { get; set; }

    public double CaptureQualityEyeScorePercent { get; set; }

    public double CaptureQualityMouthScorePercent { get; set; }

    public double CaptureQualityStabilityScorePercent { get; set; }

    public double CaptureQualityGlassesScorePercent { get; set; }

    public double CaptureQualityStorageScorePercent { get; set; }

    public double? CaptureQualityFaceWidthPercent { get; set; }

    public double? CaptureQualityFaceHeightPercent { get; set; }

    public List<string> CaptureQualityIssues { get; set; } = [];

    public List<string> CaptureQualitySuggestions { get; set; } = [];

    public bool IdentityMeasurementAvailable { get; set; }

    public bool IdentityAutoGateReady { get; set; }

    public bool IdentityWarmupStrongMismatchGateReady { get; set; }

    public double IdentityConfidencePercent { get; set; }

    public int IdentityComparedFeatureCount { get; set; }

    public int IdentityOutlierFeatureCount { get; set; }

    public string IdentityStatus { get; set; } = "";

    public static PersonalFaceCollectionAuditObservation Create(
        DateTime reviewedAtUtc,
        bool subjectConfirmed,
        bool pausedForEventOrCalibration,
        bool hasFace,
        PersonalFaceModelUpdate modelUpdate,
        PersonalFaceCaptureQualityAssessment captureQuality)
    {
        ArgumentNullException.ThrowIfNull(modelUpdate);
        ArgumentNullException.ThrowIfNull(captureQuality);

        return new PersonalFaceCollectionAuditObservation
        {
            ReviewedAtUtc = reviewedAtUtc == default ? DateTime.UtcNow : reviewedAtUtc,
            SubjectConfirmed = subjectConfirmed,
            PausedForEventOrCalibration = pausedForEventOrCalibration,
            HasFace = hasFace,
            PersonalModelAccepted = modelUpdate.Accepted,
            PersonalModelRejectionKind = modelUpdate.RejectionKind.ToString(),
            PersonalModelUpdateReason = modelUpdate.Reason,
            CaptureQualityLabel = captureQuality.Label,
            CaptureQualityScorePercent = captureQuality.ScorePercent,
            CaptureQualityCanCollect = captureQuality.CanCollectMeasurements,
            CaptureQualityAvatarGrade = captureQuality.StrongEnoughForAvatarLearning,
            CaptureQualityReason = captureQuality.PrimaryReason,
            CaptureQualityCameraModeScorePercent = captureQuality.CameraModeScorePercent,
            CaptureQualityFaceScaleScorePercent = captureQuality.FaceScaleScorePercent,
            CaptureQualityEyeScorePercent = captureQuality.EyeEvidenceScorePercent,
            CaptureQualityMouthScorePercent = captureQuality.MouthEvidenceScorePercent,
            CaptureQualityStabilityScorePercent = captureQuality.StabilityScorePercent,
            CaptureQualityGlassesScorePercent = captureQuality.GlassesRiskScorePercent,
            CaptureQualityStorageScorePercent = captureQuality.StorageScorePercent,
            CaptureQualityFaceWidthPercent = captureQuality.FaceWidthPercent,
            CaptureQualityFaceHeightPercent = captureQuality.FaceHeightPercent,
            CaptureQualityIssues = captureQuality.Issues
                .Where(static issue => !string.IsNullOrWhiteSpace(issue))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList(),
            CaptureQualitySuggestions = captureQuality.Suggestions
                .Where(static suggestion => !string.IsNullOrWhiteSpace(suggestion))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList(),
            IdentityMeasurementAvailable = modelUpdate.IdentityAnalysis?.HasMeasurement ?? false,
            IdentityAutoGateReady = modelUpdate.IdentityAnalysis?.AutoGateReady ?? false,
            IdentityWarmupStrongMismatchGateReady = modelUpdate.IdentityAnalysis?.WarmupStrongMismatchGateReady ?? false,
            IdentityConfidencePercent = modelUpdate.IdentityAnalysis?.ConfidencePercent ?? 0d,
            IdentityComparedFeatureCount = modelUpdate.IdentityAnalysis?.ComparedFeatureCount ?? 0,
            IdentityOutlierFeatureCount = modelUpdate.IdentityAnalysis?.OutlierFeatureCount ?? 0,
            IdentityStatus = modelUpdate.IdentityAnalysis?.Status ?? PersonalFaceIdentityAnalysis.NotReady.Status
        };
    }
}
