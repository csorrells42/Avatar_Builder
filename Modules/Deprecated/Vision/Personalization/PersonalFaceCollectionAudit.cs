namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceCollectionAudit
{
    public string SchemaVersion { get; set; } = "personal-face-collection-audit-v1";

    public string SubjectId { get; set; } = PersonalFaceSubject.DefaultSubjectId;

    public string SubjectDisplayName { get; set; } = PersonalFaceSubject.DefaultSubjectDisplayName;

    public string SubjectCollectionMode { get; set; } = PersonalFaceSubject.ManualConfirmationMode;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public int TotalFramesReviewed { get; set; }

    public int FramesWithFace { get; set; }

    public int SubjectConfirmedFrames { get; set; }

    public int SubjectGateOffFrames { get; set; }

    public int EventLikeGateFrames { get; set; }

    public int NoFaceGateFrames { get; set; }

    public int LowQualityGateFrames { get; set; }

    public int TrackingArtifactGateFrames { get; set; }

    public int SubjectMismatchGateFrames { get; set; }

    public int TrackingAuditHoldFrames { get; set; }

    public int IdentityMeasuredFrames { get; set; }

    public int IdentityAutoGateReadyFrames { get; set; }

    public int IdentityWarmupStrongMismatchGateReadyFrames { get; set; }

    public int IdentityOutlierFrames { get; set; }

    public int PersonalModelAcceptedFrames { get; set; }

    public int PersonalModelRejectedFrames { get; set; }

    public int CaptureQualityCanCollectFrames { get; set; }

    public int CaptureQualityAvatarGradeFrames { get; set; }

    public double FaceDetectionRate { get; set; }

    public double SubjectConfirmedRate { get; set; }

    public double PersonalModelAcceptedRate { get; set; }

    public double CaptureQualityCollectableRate { get; set; }

    public double CaptureQualityAvatarGradeRate { get; set; }

    public double? AverageCaptureQualityScorePercent { get; set; }

    public double? MinimumCaptureQualityScorePercent { get; set; }

    public double? AverageCaptureQualityCameraModeScorePercent { get; set; }

    public double? AverageCaptureQualityFaceScaleScorePercent { get; set; }

    public double? AverageCaptureQualityEyeScorePercent { get; set; }

    public double? AverageCaptureQualityMouthScorePercent { get; set; }

    public double? AverageCaptureQualityStabilityScorePercent { get; set; }

    public double? AverageCaptureQualityGlassesScorePercent { get; set; }

    public double? AverageCaptureQualityStorageScorePercent { get; set; }

    public double? AverageIdentityConfidencePercent { get; set; }

    public double? MinimumIdentityConfidencePercent { get; set; }

    public int MaximumIdentityOutlierFeatureCount { get; set; }

    public double? MinimumFaceWidthPercent { get; set; }

    public double? MaximumFaceWidthPercent { get; set; }

    public double? MinimumFaceHeightPercent { get; set; }

    public double? MaximumFaceHeightPercent { get; set; }

    public List<string> CaptureQualityLabels { get; set; } = [];

    public List<string> TopPersonalModelRejectionReasons { get; set; } = [];

    public List<string> TopCaptureQualityIssues { get; set; } = [];

    public List<string> TopCaptureQualitySuggestions { get; set; } = [];

    public List<string> NextActions { get; set; } = [];

    public string StoragePolicy { get; set; } =
        "Measurement-only collection audit. No raw frames, images, video clips, full landmark meshes, or face contours are stored here.";

    public string Status
    {
        get
        {
            if (TotalFramesReviewed <= 0)
            {
                return "collection audit waiting for reviewed frames";
            }

            var collection = PersonalModelAcceptedRate switch
            {
                >= 0.70d => "strong",
                >= 0.40d => "useful",
                >= 0.15d => "warming",
                _ => "limited"
            };
            return $"collection {collection}; accepted {PersonalModelAcceptedFrames}/{TotalFramesReviewed}, collectable {CaptureQualityCollectableRate:P0}";
        }
    }
}
