using System.Windows;
using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceMeasurementSample
{
    public string SchemaVersion { get; set; } = "personal-face-measurement-v1";

    public string SubjectId { get; set; } = PersonalFaceSubject.DefaultSubjectId;

    public string SubjectDisplayName { get; set; } = PersonalFaceSubject.DefaultSubjectDisplayName;

    public string SubjectCollectionMode { get; set; } = PersonalFaceSubject.ManualConfirmationMode;

    public DateTime CapturedAtUtc { get; set; }

    public double SampleWeight { get; set; }

    public double TrackingConfidence { get; set; }

    public double EyeConfidence { get; set; }

    public double MouthConfidence { get; set; }

    public double OverallQualityPercent { get; set; }

    public double EyeQualityPercent { get; set; }

    public double MouthQualityPercent { get; set; }

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

    public double FaceReliabilityPercent { get; set; }

    public double FaceContinuityPercent { get; set; }

    public double EyeReliabilityPercent { get; set; }

    public double MouthReliabilityPercent { get; set; }

    public double? FaceCenterX { get; set; }

    public double? FaceCenterY { get; set; }

    public double? FaceWidth { get; set; }

    public double? FaceHeight { get; set; }

    public double? ZApparentDistanceUnits { get; set; }

    public double? ZRelativeToReference { get; set; }

    public double? ZConfidencePercent { get; set; }

    public double? DistanceInches { get; set; }

    public bool DistanceCalibrated { get; set; }

    public bool ZUsesCameraFov { get; set; }

    public bool ZUsesLearnedReference { get; set; }

    public string ZEstimateKind { get; set; } = "";

    public string ZQualityLabel { get; set; } = "";

    public string ZDistanceSource { get; set; } = "";

    public double HeadYawDegrees { get; set; }

    public double HeadPitchDegrees { get; set; }

    public double HeadRollDegrees { get; set; }

    public List<string> PoseBucketIds { get; set; } = [];

    public double? LeftEyeOpeningRatio { get; set; }

    public double? RightEyeOpeningRatio { get; set; }

    public double? AverageEyeOpeningRatio { get; set; }

    public double? MouthOpeningRatio { get; set; }

    public double? JawDroopRatio { get; set; }

    public double? LeftBrowHeightRatio { get; set; }

    public double? RightBrowHeightRatio { get; set; }

    public double? AverageBrowHeightRatio { get; set; }

    public double? BrowAsymmetryPercent { get; set; }

    public double? MediaPipeAverageEyeBlinkPercent { get; set; }

    public double? MediaPipeJawOpenPercent { get; set; }

    public double? MediaPipeMouthClosePercent { get; set; }

    public double? FaceAspectRatio { get; set; }

    public double? EyeMidlineXToFaceWidth { get; set; }

    public double? MouthCenterXToFaceWidth { get; set; }

    public double? EyeToMouthXOffsetToFaceWidth { get; set; }

    public double? InterEyeDistanceToFaceWidth { get; set; }

    public double? LeftEyeWidthToFaceWidth { get; set; }

    public double? RightEyeWidthToFaceWidth { get; set; }

    public double? MouthWidthToFaceWidth { get; set; }

    public double? EyeMidlineYToFaceHeight { get; set; }

    public double? MouthCenterYToFaceHeight { get; set; }

    public double? EyeToMouthYDistanceToFaceHeight { get; set; }

    public bool IdentityMeasurementAvailable { get; set; }

    public bool IdentityAutoGateReady { get; set; }

    public bool IdentityWarmupStrongMismatchGateReady { get; set; }

    public double IdentityConfidencePercent { get; set; }

    public int IdentityComparedFeatureCount { get; set; }

    public int IdentityOutlierFeatureCount { get; set; }

    public bool PossibleOneEyeArtifact { get; set; }

    public bool EyeArtifactSuppressed { get; set; }

    public bool LeftEyeReconstructed { get; set; }

    public bool RightEyeReconstructed { get; set; }

    public bool MouthReconstructed { get; set; }

    public bool MediaPipeEyeOpeningCorrected { get; set; }

    public bool MediaPipeMouthOpeningCorrected { get; set; }

    public static PersonalFaceMeasurementSample Create(
        PersonalFaceModelUpdate update,
        FaceLandmarkFrame frame,
        FaceLandmarkMetrics metrics,
        FaceLockStabilityAnalysis stability,
        PersonalFaceCaptureQualityAssessment captureQuality,
        HeadPoseEstimate? headPose = null)
    {
        ArgumentNullException.ThrowIfNull(captureQuality);

        var bounds = GetFaceBounds(frame);
        var identity = PersonalFaceIdentityMeasurement.FromFrame(frame);
        var storedHeadPose = CreateStoredHeadPose(metrics, headPose);
        return new PersonalFaceMeasurementSample
        {
            SubjectId = update.Model.SubjectId,
            SubjectDisplayName = update.Model.SubjectDisplayName,
            SubjectCollectionMode = update.Model.SubjectCollectionMode,
            CapturedAtUtc = metrics.CapturedAtUtc != default
                ? metrics.CapturedAtUtc
                : frame.CapturedAtUtc != default ? frame.CapturedAtUtc : DateTime.UtcNow,
            SampleWeight = update.SampleWeight,
            TrackingConfidence = metrics.TrackingConfidence,
            EyeConfidence = metrics.EyeConfidence,
            MouthConfidence = metrics.MouthConfidence,
            OverallQualityPercent = metrics.OverallMeasurementQualityPercent,
            EyeQualityPercent = metrics.EyeMeasurementQualityPercent,
            MouthQualityPercent = metrics.MouthMeasurementQualityPercent,
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
                .Take(6)
                .ToList(),
            CaptureQualitySuggestions = captureQuality.Suggestions
                .Where(static suggestion => !string.IsNullOrWhiteSpace(suggestion))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList(),
            FaceReliabilityPercent = stability.CompositeReliabilityPercent,
            FaceContinuityPercent = stability.FaceContinuityPercent,
            EyeReliabilityPercent = stability.EyeReliabilityPercent,
            MouthReliabilityPercent = stability.MouthReliabilityPercent,
            FaceCenterX = bounds is Rect rect ? rect.Left + rect.Width / 2d : null,
            FaceCenterY = bounds is Rect rectY ? rectY.Top + rectY.Height / 2d : null,
            FaceWidth = bounds?.Width,
            FaceHeight = bounds?.Height,
            ZApparentDistanceUnits = headPose?.ApparentDistanceUnits,
            ZRelativeToReference = headPose?.ZRelativeToReference,
            ZConfidencePercent = headPose?.ZConfidencePercent,
            DistanceInches = headPose?.DistanceInches,
            DistanceCalibrated = headPose?.DistanceCalibrated == true,
            ZUsesCameraFov = headPose?.ZUsesCameraFov == true,
            ZUsesLearnedReference = headPose?.ZUsesLearnedReference == true,
            ZEstimateKind = headPose?.ZEstimateKind ?? "",
            ZQualityLabel = headPose?.ZQualityLabel ?? "",
            ZDistanceSource = headPose?.DistanceSource ?? "",
            HeadYawDegrees = storedHeadPose.YawDegrees,
            HeadPitchDegrees = storedHeadPose.PitchDegrees,
            HeadRollDegrees = storedHeadPose.RollDegrees,
            PoseBucketIds = PersonalFacePoseBuckets
                .Classify(storedHeadPose.YawDegrees, storedHeadPose.PitchDegrees, storedHeadPose.RollDegrees)
                .Select(static bucket => bucket.BucketId)
                .ToList(),
            LeftEyeOpeningRatio = metrics.LeftEyeOpeningRatio,
            RightEyeOpeningRatio = metrics.RightEyeOpeningRatio,
            AverageEyeOpeningRatio = metrics.AverageEyeOpeningRatio,
            MouthOpeningRatio = metrics.MouthOpeningRatio,
            JawDroopRatio = metrics.JawDroopRatio,
            LeftBrowHeightRatio = metrics.LeftBrowHeightRatio,
            RightBrowHeightRatio = metrics.RightBrowHeightRatio,
            AverageBrowHeightRatio = metrics.AverageBrowHeightRatio,
            BrowAsymmetryPercent = metrics.BrowAsymmetryPercent,
            MediaPipeAverageEyeBlinkPercent = metrics.MediaPipeAverageEyeBlinkPercent,
            MediaPipeJawOpenPercent = metrics.MediaPipeJawOpenPercent,
            MediaPipeMouthClosePercent = metrics.MediaPipeMouthClosePercent,
            FaceAspectRatio = identity.FaceAspectRatio,
            EyeMidlineXToFaceWidth = identity.EyeMidlineXToFaceWidth,
            MouthCenterXToFaceWidth = identity.MouthCenterXToFaceWidth,
            EyeToMouthXOffsetToFaceWidth = identity.EyeToMouthXOffsetToFaceWidth,
            InterEyeDistanceToFaceWidth = identity.InterEyeDistanceToFaceWidth,
            LeftEyeWidthToFaceWidth = identity.LeftEyeWidthToFaceWidth,
            RightEyeWidthToFaceWidth = identity.RightEyeWidthToFaceWidth,
            MouthWidthToFaceWidth = identity.MouthWidthToFaceWidth,
            EyeMidlineYToFaceHeight = identity.EyeMidlineYToFaceHeight,
            MouthCenterYToFaceHeight = identity.MouthCenterYToFaceHeight,
            EyeToMouthYDistanceToFaceHeight = identity.EyeToMouthYDistanceToFaceHeight,
            IdentityMeasurementAvailable = update.IdentityAnalysis?.HasMeasurement ?? false,
            IdentityAutoGateReady = update.IdentityAnalysis?.AutoGateReady ?? false,
            IdentityWarmupStrongMismatchGateReady = update.IdentityAnalysis?.WarmupStrongMismatchGateReady ?? false,
            IdentityConfidencePercent = update.IdentityAnalysis?.ConfidencePercent ?? 0d,
            IdentityComparedFeatureCount = update.IdentityAnalysis?.ComparedFeatureCount ?? 0,
            IdentityOutlierFeatureCount = update.IdentityAnalysis?.OutlierFeatureCount ?? 0,
            PossibleOneEyeArtifact = metrics.PossibleOneEyeArtifact,
            EyeArtifactSuppressed = metrics.EyeArtifactSuppressed,
            LeftEyeReconstructed = metrics.LeftEyeReconstructed,
            RightEyeReconstructed = metrics.RightEyeReconstructed,
            MouthReconstructed = metrics.MouthReconstructed,
            MediaPipeEyeOpeningCorrected = metrics.MediaPipeEyeOpeningCorrected,
            MediaPipeMouthOpeningCorrected = metrics.MediaPipeMouthOpeningCorrected
        };
    }

    private static Rect? GetFaceBounds(FaceLandmarkFrame frame)
    {
        if (frame.FaceContour.Count == 0)
        {
            return null;
        }

        var left = frame.FaceContour.Min(static point => point.X);
        var top = frame.FaceContour.Min(static point => point.Y);
        var right = frame.FaceContour.Max(static point => point.X);
        var bottom = frame.FaceContour.Max(static point => point.Y);
        return right > left && bottom > top
            ? new Rect(left, top, right - left, bottom - top)
            : null;
    }

    private static StoredHeadPose CreateStoredHeadPose(FaceLandmarkMetrics metrics, HeadPoseEstimate? headPose)
    {
        if (headPose is { HasFace: true } pose)
        {
            return new StoredHeadPose(
                CleanPoseDegrees(pose.BRotationAroundYDegrees, metrics.HeadYawDegrees),
                CleanPoseDegrees(pose.ARotationAroundXDegrees, metrics.HeadPitchDegrees),
                CleanPoseDegrees(pose.CRotationAroundZDegrees, metrics.HeadRollDegrees));
        }

        return new StoredHeadPose(
            CleanPoseDegrees(metrics.HeadYawDegrees, 0d),
            CleanPoseDegrees(metrics.HeadPitchDegrees, 0d),
            CleanPoseDegrees(metrics.HeadRollDegrees, 0d));
    }

    private static double CleanPoseDegrees(double value, double fallback)
    {
        return double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;
    }

    private readonly record struct StoredHeadPose(double YawDegrees, double PitchDegrees, double RollDegrees);
}
