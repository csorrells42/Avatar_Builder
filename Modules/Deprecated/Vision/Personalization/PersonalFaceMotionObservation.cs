using AvatarBuilder.Modules.Vision.Analysis;

namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceMotionObservation
{
    public string SchemaVersion { get; set; } = "personal-face-motion-observation-v1";

    public string SubjectId { get; set; } = PersonalFaceSubject.DefaultSubjectId;

    public string SubjectDisplayName { get; set; } = PersonalFaceSubject.DefaultSubjectDisplayName;

    public string SubjectCollectionMode { get; set; } = PersonalFaceSubject.ManualConfirmationMode;

    public DateTime CapturedAtUtc { get; set; }

    public bool AcceptedForPersonalModel { get; set; }

    public string Source { get; set; } = "";

    public double SampleWeight { get; set; } = 1d;

    public double OverallQualityPercent { get; set; }

    public double FaceReliabilityPercent { get; set; }

    public double FaceContinuityPercent { get; set; }

    public double EyeReliabilityPercent { get; set; }

    public double MouthReliabilityPercent { get; set; }

    public double HeadYawDegrees { get; set; }

    public double HeadPitchDegrees { get; set; }

    public double HeadRollDegrees { get; set; }

    public double? ZApparentDistanceUnits { get; set; }

    public double? ZRelativeToReference { get; set; }

    public double? ZConfidencePercent { get; set; }

    public string ZEstimateKind { get; set; } = "";

    public double? AverageEyeOpeningRatio { get; set; }

    public double? MouthOpeningRatio { get; set; }

    public double? JawDroopRatio { get; set; }

    public double? AverageBrowHeightRatio { get; set; }

    public double? MediaPipeAverageEyeBlinkPercent { get; set; }

    public double? MediaPipeJawOpenPercent { get; set; }

    public double? MediaPipeMouthClosePercent { get; set; }

    public bool EyeArtifactSuppressed { get; set; }

    public bool AnyEyeReconstructed { get; set; }

    public bool MouthReconstructed { get; set; }

    public static PersonalFaceMotionObservation Create(
        string subjectId,
        string subjectDisplayName,
        string subjectCollectionMode,
        DateTime capturedAtUtc,
        double sampleWeight,
        FaceLandmarkMetrics metrics,
        FaceLockStabilityAnalysis stability,
        bool acceptedForPersonalModel,
        string source,
        HeadPoseEstimate? headPose = null)
    {
        var storedHeadPose = CreateStoredHeadPose(metrics, headPose);
        return new PersonalFaceMotionObservation
        {
            SubjectId = string.IsNullOrWhiteSpace(subjectId) ? PersonalFaceSubject.DefaultSubjectId : subjectId,
            SubjectDisplayName = string.IsNullOrWhiteSpace(subjectDisplayName) ? PersonalFaceSubject.DefaultSubjectDisplayName : subjectDisplayName,
            SubjectCollectionMode = string.IsNullOrWhiteSpace(subjectCollectionMode) ? PersonalFaceSubject.ManualConfirmationMode : subjectCollectionMode,
            CapturedAtUtc = capturedAtUtc == default ? DateTime.UtcNow : capturedAtUtc.ToUniversalTime(),
            AcceptedForPersonalModel = acceptedForPersonalModel,
            Source = string.IsNullOrWhiteSpace(source) ? metrics.Source : source,
            SampleWeight = Math.Clamp(sampleWeight, 0.05d, 2.00d),
            OverallQualityPercent = metrics.OverallMeasurementQualityPercent,
            FaceReliabilityPercent = stability.CompositeReliabilityPercent,
            FaceContinuityPercent = stability.FaceContinuityPercent,
            EyeReliabilityPercent = stability.EyeReliabilityPercent,
            MouthReliabilityPercent = stability.MouthReliabilityPercent,
            HeadYawDegrees = storedHeadPose.YawDegrees,
            HeadPitchDegrees = storedHeadPose.PitchDegrees,
            HeadRollDegrees = storedHeadPose.RollDegrees,
            ZApparentDistanceUnits = headPose?.ApparentDistanceUnits,
            ZRelativeToReference = headPose?.ZRelativeToReference,
            ZConfidencePercent = headPose?.ZConfidencePercent,
            ZEstimateKind = headPose?.ZEstimateKind ?? "",
            AverageEyeOpeningRatio = metrics.AverageEyeOpeningRatio,
            MouthOpeningRatio = metrics.MouthOpeningRatio,
            JawDroopRatio = metrics.JawDroopRatio,
            AverageBrowHeightRatio = metrics.AverageBrowHeightRatio,
            MediaPipeAverageEyeBlinkPercent = metrics.MediaPipeAverageEyeBlinkPercent,
            MediaPipeJawOpenPercent = metrics.MediaPipeJawOpenPercent,
            MediaPipeMouthClosePercent = metrics.MediaPipeMouthClosePercent,
            EyeArtifactSuppressed = metrics.EyeArtifactSuppressed,
            AnyEyeReconstructed = metrics.AnyEyeReconstructed,
            MouthReconstructed = metrics.MouthReconstructed
        };
    }

    public static PersonalFaceMotionObservation FromMeasurementSample(PersonalFaceMeasurementSample sample)
    {
        return new PersonalFaceMotionObservation
        {
            SubjectId = sample.SubjectId,
            SubjectDisplayName = sample.SubjectDisplayName,
            SubjectCollectionMode = sample.SubjectCollectionMode,
            CapturedAtUtc = sample.CapturedAtUtc,
            AcceptedForPersonalModel = true,
            Source = "personal measurement journal",
            SampleWeight = sample.SampleWeight,
            OverallQualityPercent = sample.OverallQualityPercent,
            FaceReliabilityPercent = sample.FaceReliabilityPercent,
            FaceContinuityPercent = sample.FaceContinuityPercent,
            EyeReliabilityPercent = sample.EyeReliabilityPercent,
            MouthReliabilityPercent = sample.MouthReliabilityPercent,
            HeadYawDegrees = sample.HeadYawDegrees,
            HeadPitchDegrees = sample.HeadPitchDegrees,
            HeadRollDegrees = sample.HeadRollDegrees,
            ZApparentDistanceUnits = sample.ZApparentDistanceUnits,
            ZRelativeToReference = sample.ZRelativeToReference,
            ZConfidencePercent = sample.ZConfidencePercent,
            ZEstimateKind = sample.ZEstimateKind,
            AverageEyeOpeningRatio = sample.AverageEyeOpeningRatio,
            MouthOpeningRatio = sample.MouthOpeningRatio,
            JawDroopRatio = sample.JawDroopRatio,
            AverageBrowHeightRatio = sample.AverageBrowHeightRatio,
            MediaPipeAverageEyeBlinkPercent = sample.MediaPipeAverageEyeBlinkPercent,
            MediaPipeJawOpenPercent = sample.MediaPipeJawOpenPercent,
            MediaPipeMouthClosePercent = sample.MediaPipeMouthClosePercent,
            EyeArtifactSuppressed = sample.EyeArtifactSuppressed,
            AnyEyeReconstructed = sample.LeftEyeReconstructed || sample.RightEyeReconstructed,
            MouthReconstructed = sample.MouthReconstructed
        };
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
