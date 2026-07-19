using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvatarBuilder;
using AvatarBuilder.Modules.Episodes;
using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.MediaPipe;
using AvatarBuilder.Modules.Vision.Onnx;
using AvatarBuilder.Modules.Vision.OpenCv;
using AvatarBuilder.Modules.Vision.Personalization;
using AvatarBuilder.Modules.Vision.Pipeline;
using AvatarBuilder.Modules.Vision.Reconstruction;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectX12;
using OpenCvSharp;
using CvPoint = OpenCvSharp.Point;
using CvRect = OpenCvSharp.Rect;
using CvSize = OpenCvSharp.Size;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

if (args.Length >= 2 && args[0].Equals("--write-synthetic-video", StringComparison.OrdinalIgnoreCase))
{
    WriteSyntheticVideo(Path.GetFullPath(args[1]), includeEyeInset: true);
    Console.WriteLine(Path.GetFullPath(args[1]));
    return;
}

if (args.Length >= 2 && args[0].Equals("--write-synthetic-no-inset-video", StringComparison.OrdinalIgnoreCase))
{
    WriteSyntheticVideo(Path.GetFullPath(args[1]), includeEyeInset: false);
    Console.WriteLine(Path.GetFullPath(args[1]));
    return;
}

if (args.Length >= 2 && args[0].Equals("--write-landmark-stress", StringComparison.OrdinalIgnoreCase))
{
    WriteSyntheticLandmarkStress(Path.GetFullPath(args[1]));
    Console.WriteLine(Path.GetFullPath(args[1]));
    return;
}

if (TryGetOption(args, "--audit-folder", out var auditFolder))
{
    RunSavedDataAudit(Path.GetFullPath(auditFolder), args.Any(static arg => arg.Equals("--write-audit-reports", StringComparison.OrdinalIgnoreCase)));
    return;
}

RunApertureSmoke();
RunFaceLockStabilitySmoke();
RunPersonalFaceModelSmoke();
RunPersonalFaceSurfaceProfileSmoke();
RunPersonalFaceApertureConsistencySmoke();
RunEyeApertureReliabilityAuditSmoke();
RunMouthVerticalAnchorAuditSmoke();
RunPoseExplainedFeatureMotionAuditSmoke();
RunPersonalFaceLearningAuditGateSmoke();
RunPersonalFaceCaptureQualitySmoke();
RunHeadPoseEstimatorSmoke();
RunStoredHeadPoseRoutingSmoke();
RunPersonalFaceMotionModelSmoke();
RunFaceReconstructionContractSmoke();
RunMeasurementFacePreviewSmoke();
RunLastGoodFeatureMeshSmoke();
RunMeasurementAvatarTrainingPackageSmoke();
RunAvatarBuilderStartupOptionsSmoke();
RunMeasurementAvatarEasyModeAdvisorSmoke();
RunMeasurementAvatarCapturePlanSmoke();
RunSyntheticLandmarkStressArtifactSmoke();
RunTexturePreviewRoutingSmoke();
Console.WriteLine("Avatar Builder vision smoke checks passed.");

const int SyntheticVideoWidth = 640;
const int SyntheticVideoHeight = 360;
const double SyntheticVideoFramesPerSecond = 12d;
const int SyntheticVideoFrameCount = 72;

static void RunSavedDataAudit(string folder, bool writeReports)
{
    if (!Directory.Exists(folder))
    {
        throw new DirectoryNotFoundException($"Audit folder does not exist: {folder}");
    }

    var model = new PersonalFaceModelStore().TryRead(folder)
        ?? throw new FileNotFoundException("No personal_face_model.json found in the audit folder.", Path.Combine(folder, "personal_face_model.json"));
    var samples = PersonalFaceMeasurementJournal
        .ReadRecentSamples(folder, PersonalFaceMeasurementJournal.DefaultRecentSampleReadLimit)
        .Where(sample => string.Equals(sample.SubjectId, model.SubjectId, StringComparison.OrdinalIgnoreCase))
        .ToList();
    var observations = samples
        .Select(PersonalFaceMotionObservation.FromMeasurementSample)
        .ToList();
    var motion = new PersonalFaceMotionModelBuilder().Build(observations);
    if (motion.ObservationCount == 0)
    {
        motion.SubjectId = model.SubjectId;
        motion.SubjectDisplayName = model.SubjectDisplayName;
        motion.SubjectCollectionMode = model.SubjectCollectionMode;
        motion.CreatedAtUtc = model.CreatedAtUtc != default ? model.CreatedAtUtc : DateTime.UtcNow;
        motion.UpdatedAtUtc = model.UpdatedAtUtc != default ? model.UpdatedAtUtc : DateTime.UtcNow;
    }

    var measurementBytes = PersonalFaceMeasurementJournal.GetMeasurementsSizeBytes(folder);
    var readiness = new PersonalFaceCorpusReadinessBuilder().Build(model, motion, samples, measurementBytes);
    var motionPath = Path.Combine(folder, "personal_face_motion_model.json");
    var readinessPath = Path.Combine(folder, PersonalFaceCorpusReadinessStore.DefaultJsonFileName);
    if (writeReports)
    {
        motionPath = new PersonalFaceMotionModelStore().Write(folder, motion);
        readinessPath = new PersonalFaceCorpusReadinessStore().Write(folder, readiness);
    }

    Console.WriteLine($"Avatar Builder data audit: {folder}");
    Console.WriteLine($"Subject: {model.SubjectDisplayName} ({model.SubjectId})");
    Console.WriteLine($"Accepted samples: {model.AcceptedSamples}");
    Console.WriteLine($"Retained journal rows: {samples.Count}");
    Console.WriteLine($"Journal coverage: {readiness.MeasurementJournalCoveragePercent:0.#}%");
    Console.WriteLine($"Overall readiness: {readiness.OverallReadinessPercent:0.#}%");
    Console.WriteLine($"Data audit health: {readiness.DataAuditHealthPercent:0.#}%");
    Console.WriteLine($"Pose estimation health: {readiness.PoseEstimationHealthPercent:0.#}%");
    Console.WriteLine($"Feature anchoring health: {readiness.FeatureAnchoringHealthPercent:0.#}%");
    Console.WriteLine($"Identity session health: {readiness.IdentitySessionHealthPercent:0.#}%");
    Console.WriteLine($"Recent identity samples/confidence/outliers: {readiness.RecentIdentityMeasurementSamples} / {FormatOptional(readiness.AverageRecentIdentityConfidencePercent)}% avg / {FormatRate(readiness.RecentIdentityOutlierFrameRate)}");
    Console.WriteLine($"Mouth vertical anchor health: {readiness.MouthVerticalAnchorHealthPercent:0.#}%");
    Console.WriteLine($"Jaw droop scale health: {readiness.JawDroopScaleHealthPercent:0.#}%");
    Console.WriteLine($"A/B/C ranges: {FormatOptional(readiness.HeadPitchRangeDegrees)} / {FormatOptional(readiness.HeadYawRangeDegrees)} / {FormatOptional(readiness.HeadRollRangeDegrees)} deg");
    Console.WriteLine($"Eye/Mouth/Jaw ranges: {FormatOptional(readiness.EyeOpeningRange)} / {FormatOptional(readiness.MouthOpeningRange)} / {FormatOptional(readiness.JawDroopRange)}");
    Console.WriteLine(writeReports ? $"Motion model: {motionPath}" : "Motion model write skipped; audit is read-only.");
    Console.WriteLine(writeReports ? $"Readiness report: {readinessPath}" : "Readiness report write skipped; audit is read-only.");
    Console.WriteLine(writeReports ? $"Readiness HTML: {PersonalFaceCorpusReadinessStore.GetHtmlPath(readinessPath)}" : "Readiness HTML write skipped; audit is read-only.");

    if (readiness.DataAuditFindings.Count == 0)
    {
        Console.WriteLine("Data audit findings: none");
    }
    else
    {
        Console.WriteLine("Data audit findings:");
        foreach (var finding in readiness.DataAuditFindings)
        {
            Console.WriteLine($"- {finding}");
        }
    }

    Environment.ExitCode = readiness.DataAuditHealthPercent >= 50d ? 0 : 4;
}

static void RunPersonalFaceLearningAuditGateSmoke()
{
    var earlyRisk = PersonalFaceLearningAuditGate.Evaluate(new PersonalFaceCorpusReadiness
    {
        AcceptedBaselineSamples = 22,
        DataAuditHealthPercent = 30d,
        PoseEstimationHealthPercent = 30d,
        FeatureAnchoringHealthPercent = 30d,
        DataAuditFindings =
        [
            "head A/B rotations are not moving even though the face moves in frame; turned-head evidence may be getting stored as 2D feature movement instead of head pose."
        ]
    });
    Require(!earlyRisk.HoldLearning, "tracking audit gate held early/warming data before enough samples existed");

    var healthy = PersonalFaceLearningAuditGate.Evaluate(new PersonalFaceCorpusReadiness
    {
        AcceptedBaselineSamples = 120,
        DataAuditHealthPercent = 86d,
        PoseEstimationHealthPercent = 82d,
        FeatureAnchoringHealthPercent = 88d
    });
    Require(!healthy.HoldLearning, $"tracking audit gate held healthy data: {healthy.Reason}");

    var consistentPoseBuckets = PersonalFacePoseBucketConsistencyAnalyzer.Analyze(PoseBucketProfiles());
    Require(consistentPoseBuckets.ComparedPoseBucketCount > 0, "pose-bucket consistency analyzer did not compare healthy turned-head buckets");
    Require(consistentPoseBuckets.SuspiciousPoseBucketCount == 0, "pose-bucket consistency analyzer flagged healthy pose buckets");

    var driftingPoseBuckets = PoseBucketProfiles();
    var negativeYawBucket = driftingPoseBuckets.First(static bucket => bucket.BucketId == PersonalFacePoseBuckets.YawNegative);
    negativeYawBucket.InterEyeDistanceToFaceWidth = Distribution(0.19d, negativeYawBucket.SampleCount, negativeYawBucket.TotalWeight, spread: 0.018d);
    negativeYawBucket.MouthWidthToFaceWidth = Distribution(0.58d, negativeYawBucket.SampleCount, negativeYawBucket.TotalWeight, spread: 0.02d);
    var driftingPoseBucketReport = PersonalFacePoseBucketConsistencyAnalyzer.Analyze(driftingPoseBuckets);
    Require(driftingPoseBucketReport.SuspiciousPoseBucketCount > 0, "pose-bucket consistency analyzer did not flag intentionally drifting identity ratios");
    Require(
        driftingPoseBucketReport.Findings.Any(static finding => finding.Contains("face features sliding", StringComparison.OrdinalIgnoreCase)),
        "pose-bucket consistency analyzer did not produce an actionable sliding-features finding");

    var axisMismatchPoseBuckets = PoseBucketProfiles();
    var positiveYawBucket = axisMismatchPoseBuckets.First(static bucket => bucket.BucketId == PersonalFacePoseBuckets.YawPositive);
    positiveYawBucket.HeadYawDegrees = Distribution(1.2d, positiveYawBucket.SampleCount, positiveYawBucket.TotalWeight, spread: 0.5d);
    var axisMismatchPoseBucketReport = PersonalFacePoseBucketConsistencyAnalyzer.Analyze(axisMismatchPoseBuckets);
    Require(axisMismatchPoseBucketReport.SuspiciousPoseBucketCount > 0, "pose-bucket consistency analyzer did not flag a B bucket with almost no measured B rotation");
    Require(
        axisMismatchPoseBucketReport.Comparisons.Any(static comparison =>
            comparison.BucketId == PersonalFacePoseBuckets.YawPositive
            && comparison.PoseAxisHealthPercent < 55d
            && comparison.Status.Equals("suspicious", StringComparison.OrdinalIgnoreCase)),
        "pose-bucket consistency analyzer did not expose the pose-axis health failure");
    Require(
        axisMismatchPoseBucketReport.Findings.Any(static finding =>
            finding.Contains("pose bucket axis mismatch", StringComparison.OrdinalIgnoreCase)
            && finding.Contains("head pose", StringComparison.OrdinalIgnoreCase)),
        "pose-bucket consistency analyzer did not explain the pose-axis mismatch");

    var translationOnlyPoseFinding = PersonalFaceLearningAuditGate.Evaluate(new PersonalFaceCorpusReadiness
    {
        AcceptedBaselineSamples = 120,
        DataAuditHealthPercent = 62d,
        PoseEstimationHealthPercent = 55d,
        FeatureAnchoringHealthPercent = 76d,
        DataAuditFindings =
        [
            "A/B rotation coverage is still early while the face position or scale changed. This is not treated as a tracking failure by itself; collect deliberate left/right and up/down head turns before trusting turned-head avatar fitting."
        ]
    });
    Require(!translationOnlyPoseFinding.HoldLearning, $"tracking audit gate held translation-only pose coverage guidance: {translationOnlyPoseFinding.Reason}");

    var highRiskFeatureFinding = PersonalFaceLearningAuditGate.Evaluate(new PersonalFaceCorpusReadiness
    {
        AcceptedBaselineSamples = 120,
        DataAuditHealthPercent = 68d,
        PoseEstimationHealthPercent = 75d,
        FeatureAnchoringHealthPercent = 55d,
        DataAuditFindings =
        [
            "face-local feature proportions are drifting more than expected (eye spacing range 0.22, mouth width range 0.2, eye-to-mouth range 0.16); review overlay/video for features sliding on the head."
        ]
    });
    Require(highRiskFeatureFinding.HoldLearning, "tracking audit gate did not hold high-risk feature anchoring finding");
    Require(highRiskFeatureFinding.Reason.Contains("drifting", StringComparison.OrdinalIgnoreCase), "tracking audit gate feature hold reason was not actionable");

    var highRiskPoseBucketFinding = PersonalFaceLearningAuditGate.Evaluate(new PersonalFaceCorpusReadiness
    {
        AcceptedBaselineSamples = 120,
        DataAuditHealthPercent = 72d,
        PoseEstimationHealthPercent = 78d,
        FeatureAnchoringHealthPercent = 82d,
        PoseBucketConsistencyHealthPercent = 42d,
        DataAuditFindings =
        [
            "pose bucket consistency drift in Negative B turn: identity-shaped ratios changed vs front-neutral"
        ]
    });
    Require(highRiskPoseBucketFinding.HoldLearning, "tracking audit gate did not hold high-risk pose-bucket consistency finding");
    Require(highRiskPoseBucketFinding.Reason.Contains("pose bucket consistency", StringComparison.OrdinalIgnoreCase), "tracking audit gate pose-bucket hold reason was not actionable");

    var highRiskPoseAxisFinding = PersonalFaceLearningAuditGate.Evaluate(new PersonalFaceCorpusReadiness
    {
        AcceptedBaselineSamples = 120,
        DataAuditHealthPercent = 72d,
        PoseEstimationHealthPercent = 78d,
        FeatureAnchoringHealthPercent = 82d,
        PoseBucketConsistencyHealthPercent = 48d,
        DataAuditFindings =
        [
            "pose bucket axis mismatch in Positive B turn: expected positive B rotation, measured B 1.2 deg; head turns may be getting stored without the expected head pose."
        ]
    });
    Require(highRiskPoseAxisFinding.HoldLearning, "tracking audit gate did not hold high-risk pose-axis mismatch finding");
    Require(highRiskPoseAxisFinding.Reason.Contains("pose bucket axis mismatch", StringComparison.OrdinalIgnoreCase), "tracking audit gate pose-axis hold reason was not actionable");

    var highRiskSurfaceGeometryFinding = PersonalFaceLearningAuditGate.Evaluate(new PersonalFaceCorpusReadiness
    {
        AcceptedBaselineSamples = 140,
        DataAuditHealthPercent = 66d,
        PoseEstimationHealthPercent = 80d,
        FeatureAnchoringHealthPercent = 82d,
        SurfaceGeometryHealthPercent = 34d,
        SurfaceGeometryReviewPatchCount = 3,
        SurfaceGeometryStatus = "3 patch(es) need review",
        DataAuditFindings =
        [
            "surface geometry health is weak (34%): 3 measured patch(es) need review; status 3 patch(es) need review."
        ]
    });
    Require(highRiskSurfaceGeometryFinding.HoldLearning, "tracking audit gate did not hold high-risk surface geometry finding");
    Require(highRiskSurfaceGeometryFinding.Reason.Contains("surface geometry", StringComparison.OrdinalIgnoreCase), "tracking audit gate surface-geometry hold reason was not actionable");

    var identitySessionRisk = BuildSyntheticIdentitySessionReadiness(outlierFrames: 14, normalFrames: 22);
    Require(identitySessionRisk.IdentitySessionHealthPercent < 60d, $"identity-session audit did not lower health enough: {identitySessionRisk.IdentitySessionHealthPercent}");
    Require(identitySessionRisk.RecentIdentityOutlierFrameRate >= 0.30d, $"identity-session audit did not retain outlier rate: {identitySessionRisk.RecentIdentityOutlierFrameRate}");
    Require(
        identitySessionRisk.DataAuditFindings.Any(static finding =>
            finding.Contains("recent identity-session high", StringComparison.OrdinalIgnoreCase)),
        "identity-session audit did not produce a high-risk finding");
    var highRiskIdentitySessionFinding = PersonalFaceLearningAuditGate.Evaluate(identitySessionRisk);
    Require(highRiskIdentitySessionFinding.HoldLearning, "tracking audit gate did not hold high-risk recent identity-session finding");
    Require(highRiskIdentitySessionFinding.Reason.Contains("recent identity-session", StringComparison.OrdinalIgnoreCase), "tracking audit gate identity-session hold reason was not actionable");

    var lowHealth = PersonalFaceLearningAuditGate.Evaluate(new PersonalFaceCorpusReadiness
    {
        AcceptedBaselineSamples = 120,
        DataAuditHealthPercent = 44d,
        PoseEstimationHealthPercent = 55d,
        FeatureAnchoringHealthPercent = 58d
    });
    Require(lowHealth.HoldLearning, "tracking audit gate did not hold low data-audit health");

    var lowPoseAxis = PersonalFaceLearningAuditGate.Evaluate(new PersonalFaceCorpusReadiness
    {
        AcceptedBaselineSamples = 120,
        DataAuditHealthPercent = 75d,
        PoseEstimationHealthPercent = 35d,
        FeatureAnchoringHealthPercent = 70d
    });
    Require(lowPoseAxis.HoldLearning, "tracking audit gate did not hold very low pose health");
}

static void RunPersonalFaceApertureConsistencySmoke()
{
    var healthySamples = Enumerable.Range(0, 24)
        .Select(index =>
        {
            var progress = index / 23d;
            return ApertureSample(
                eyeOpening: 0.31d - progress * 0.17d,
                blinkPercent: 8d + progress * 72d,
                mouthOpening: 0.04d + progress * 0.22d,
                jawDroop: 0.01d + progress * 0.13d,
                jawOpenPercent: 5d + progress * 62d,
                mouthClosePercent: 90d - progress * 66d);
        })
        .ToList();
    var healthy = PersonalFaceApertureConsistencyAnalyzer.Analyze(healthySamples);
    Require(healthy.HealthPercent >= 82d, $"aperture consistency analyzer scored healthy corroborated samples too low: {healthy.HealthPercent}");
    Require(healthy.EyeOpeningBlinkCorrelation < -0.85d, $"healthy eye aperture correlation did not oppose blink evidence: {healthy.EyeOpeningBlinkCorrelation}");
    Require(healthy.MouthOpeningEvidenceCorrelation > 0.85d, $"healthy mouth aperture correlation did not match mouth evidence: {healthy.MouthOpeningEvidenceCorrelation}");
    Require(healthy.JawDroopEvidenceCorrelation > 0.85d, $"healthy jaw droop correlation did not match jaw evidence: {healthy.JawDroopEvidenceCorrelation}");

    var badEyeSamples = Enumerable.Range(0, 24)
        .Select(index =>
        {
            var progress = index / 23d;
            return ApertureSample(
                eyeOpening: 0.12d + progress * 0.17d,
                blinkPercent: 8d + progress * 72d,
                mouthOpening: 0.04d + progress * 0.22d,
                jawDroop: 0.01d + progress * 0.13d,
                jawOpenPercent: 5d + progress * 62d,
                mouthClosePercent: 90d - progress * 66d);
        })
        .ToList();
    var badEye = PersonalFaceApertureConsistencyAnalyzer.Analyze(badEyeSamples);
    Require(badEye.EyeApertureHealthPercent < 55d, $"aperture consistency analyzer did not penalize wrong-direction eye evidence: {badEye.EyeApertureHealthPercent}");
    Require(
        badEye.Findings.Any(static finding => finding.Contains("behind-glasses eyelid", StringComparison.OrdinalIgnoreCase)),
        "aperture consistency analyzer did not explain wrong-direction eye evidence");

    var badMouthSamples = Enumerable.Range(0, 24)
        .Select(index =>
        {
            var progress = index / 23d;
            return ApertureSample(
                eyeOpening: 0.31d - progress * 0.17d,
                blinkPercent: 8d + progress * 72d,
                mouthOpening: 0.28d - progress * 0.20d,
                jawDroop: 0.13d - progress * 0.10d,
                jawOpenPercent: 5d + progress * 62d,
                mouthClosePercent: 90d - progress * 66d);
        })
        .ToList();
    var badMouth = PersonalFaceApertureConsistencyAnalyzer.Analyze(badMouthSamples);
    Require(badMouth.MouthApertureHealthPercent < 55d, $"aperture consistency analyzer did not penalize wrong-direction mouth evidence: {badMouth.MouthApertureHealthPercent}");
    Require(
        badMouth.Findings.Any(static finding => finding.Contains("under the nose", StringComparison.OrdinalIgnoreCase)),
        "aperture consistency analyzer did not explain wrong-direction mouth evidence");

    var realLikeSparseMouthCloseSamples = Enumerable.Range(0, 24)
        .Select(index =>
        {
            var progress = index / 23d;
            return ApertureSample(
                eyeOpening: 0.31d - progress * 0.17d,
                blinkPercent: 8d + progress * 72d,
                mouthOpening: 0.04d + progress * 0.18d,
                jawDroop: 0.01d + progress * 0.09d,
                jawOpenPercent: 1d + progress * 12d,
                mouthClosePercent: 0.15d + progress * 0.70d);
        })
        .ToList();
    var realLikeSparseMouthClose = PersonalFaceApertureConsistencyAnalyzer.Analyze(realLikeSparseMouthCloseSamples);
    Require(
        realLikeSparseMouthClose.MouthOpeningEvidenceCorrelation > 0.85d,
        $"aperture consistency analyzer let near-zero mouthClose dominate jaw-open evidence: {realLikeSparseMouthClose.MouthOpeningEvidenceCorrelation}");
    Require(
        realLikeSparseMouthClose.MouthApertureHealthPercent >= 70d,
        $"aperture consistency analyzer scored real-like jaw-open corroboration too low: {realLikeSparseMouthClose.MouthApertureHealthPercent}");
}

static PersonalFaceMeasurementSample ApertureSample(
    double eyeOpening,
    double blinkPercent,
    double mouthOpening,
    double jawDroop,
    double jawOpenPercent,
    double mouthClosePercent)
{
    return new PersonalFaceMeasurementSample
    {
        CapturedAtUtc = DateTime.UtcNow,
        CaptureQualityCanCollect = true,
        AverageEyeOpeningRatio = eyeOpening,
        MouthOpeningRatio = mouthOpening,
        JawDroopRatio = jawDroop,
        MediaPipeAverageEyeBlinkPercent = blinkPercent,
        MediaPipeJawOpenPercent = jawOpenPercent,
        MediaPipeMouthClosePercent = mouthClosePercent,
        EyeQualityPercent = 88d,
        MouthQualityPercent = 86d
    };
}

static void RunEyeApertureReliabilityAuditSmoke()
{
    var healthy = BuildSyntheticEyeApertureReliabilityReadiness(
        possibleOneEyeArtifactSamples: 0,
        eyeArtifactSuppressedSamples: 0,
        leftEyeReconstructedSamples: 1,
        rightEyeReconstructedSamples: 1,
        eyeAgreementAveragePercent: 92d,
        eyeAgreementMinimumPercent: 80d);
    Require(
        healthy.EyeApertureReliabilityHealthPercent >= 90d,
        $"clean eye aperture reliability audit scored too low: {healthy.EyeApertureReliabilityHealthPercent}");
    Require(
        healthy.PossibleOneEyeArtifactRate is 0d,
        $"clean eye aperture reliability audit reported one-eye artifacts: {healthy.PossibleOneEyeArtifactRate}");

    var polluted = BuildSyntheticEyeApertureReliabilityReadiness(
        possibleOneEyeArtifactSamples: 24,
        eyeArtifactSuppressedSamples: 18,
        leftEyeReconstructedSamples: 30,
        rightEyeReconstructedSamples: 30,
        eyeAgreementAveragePercent: 51d,
        eyeAgreementMinimumPercent: 24d);
    Require(
        polluted.EyeApertureReliabilityHealthPercent < 50d,
        $"polluted eye aperture reliability audit did not lower health enough: {polluted.EyeApertureReliabilityHealthPercent}");
    Require(
        polluted.PossibleOneEyeArtifactRate >= 0.25d,
        $"polluted eye aperture reliability audit did not count one-eye artifacts: {polluted.PossibleOneEyeArtifactRate}");
    Require(
        polluted.DataAuditFindings.Any(static finding =>
            finding.Contains("eye aperture reliability", StringComparison.OrdinalIgnoreCase)
            && finding.Contains("one-eye", StringComparison.OrdinalIgnoreCase)),
        "polluted eye aperture reliability audit did not explain the one-eye artifact risk");
}

static PersonalFaceCorpusReadiness BuildSyntheticEyeApertureReliabilityReadiness(
    int possibleOneEyeArtifactSamples,
    int eyeArtifactSuppressedSamples,
    int leftEyeReconstructedSamples,
    int rightEyeReconstructedSamples,
    double eyeAgreementAveragePercent,
    double eyeAgreementMinimumPercent)
{
    const int sampleCount = 80;
    var now = DateTime.UtcNow;
    var model = new PersonalFaceModel
    {
        CreatedAtUtc = now,
        UpdatedAtUtc = now.AddSeconds(sampleCount),
        ObservedSamples = sampleCount,
        AcceptedSamples = sampleCount,
        AcceptedSampleWeight = sampleCount,
        AverageFaceReliabilityPercent = 92d,
        AverageFaceContinuityPercent = 90d,
        AverageEyeReliabilityPercent = 90d,
        AverageMouthReliabilityPercent = 90d,
        PossibleOneEyeArtifactSamples = possibleOneEyeArtifactSamples,
        EyeArtifactSuppressedSamples = eyeArtifactSuppressedSamples,
        LeftEyeReconstructedSamples = leftEyeReconstructedSamples,
        RightEyeReconstructedSamples = rightEyeReconstructedSamples,
        IdentitySignatureSamples = sampleCount,
        LearningStability = new PersonalFaceLearningStability
        {
            AnchorPercent = 82d,
            AnchorStatus = "synthetic",
            MaximumNextSampleInfluencePercent = 1.6d,
            MaximumEventLikeNextSampleInfluencePercent = 0.8d
        },
        FaceCenterX = Metric(0.50d, sampleCount),
        FaceCenterY = Metric(0.50d, sampleCount),
        FaceWidth = Metric(0.40d, sampleCount),
        FaceHeight = Metric(0.62d, sampleCount),
        HeadYawDegrees = Metric(0d, sampleCount, 5d),
        HeadPitchDegrees = Metric(0d, sampleCount, 3d),
        HeadRollDegrees = Metric(0d, sampleCount, 3d),
        LeftEyeOpeningRatio = Metric(0.25d, sampleCount),
        RightEyeOpeningRatio = Metric(0.25d, sampleCount),
        AverageEyeOpeningRatio = Metric(0.25d, sampleCount),
        EyeAgreementPercent = Metric(eyeAgreementAveragePercent, sampleCount, spread: Math.Max(1d, eyeAgreementAveragePercent - eyeAgreementMinimumPercent)),
        MouthOpeningRatio = Metric(0.08d, sampleCount),
        JawDroopRatio = Metric(0.04d, sampleCount),
        FaceAspectRatio = Metric(1.45d, sampleCount),
        EyeMidlineXToFaceWidth = Metric(0.50d, sampleCount),
        MouthCenterXToFaceWidth = Metric(0.50d, sampleCount),
        EyeToMouthXOffsetToFaceWidth = Metric(0.02d, sampleCount),
        InterEyeDistanceToFaceWidth = Metric(0.40d, sampleCount),
        LeftEyeWidthToFaceWidth = Metric(0.14d, sampleCount),
        RightEyeWidthToFaceWidth = Metric(0.14d, sampleCount),
        MouthWidthToFaceWidth = Metric(0.28d, sampleCount),
        EyeMidlineYToFaceHeight = Metric(0.36d, sampleCount),
        MouthCenterYToFaceHeight = Metric(0.66d, sampleCount),
        EyeToMouthYDistanceToFaceHeight = Metric(0.30d, sampleCount),
        EyeGlarePercent = Metric(possibleOneEyeArtifactSamples > 0 ? 64d : 8d, sampleCount),
        EyeContrastPercent = Metric(possibleOneEyeArtifactSamples > 0 ? 34d : 72d, sampleCount),
        EyeSharpnessPercent = Metric(possibleOneEyeArtifactSamples > 0 ? 40d : 78d, sampleCount)
    };
    var samples = Enumerable.Range(0, sampleCount)
        .Select(index => new PersonalFaceMeasurementSample
        {
            CapturedAtUtc = now.AddSeconds(index),
            SampleWeight = 1d,
            TrackingConfidence = 0.92d,
            EyeConfidence = possibleOneEyeArtifactSamples > 0 ? 0.52d : 0.90d,
            MouthConfidence = 0.90d,
            OverallQualityPercent = possibleOneEyeArtifactSamples > 0 ? 72d : 88d,
            EyeQualityPercent = possibleOneEyeArtifactSamples > 0 ? 56d : 88d,
            MouthQualityPercent = 88d,
            CaptureQualityLabel = possibleOneEyeArtifactSamples > 0 ? "review" : "avatar-grade",
            CaptureQualityScorePercent = possibleOneEyeArtifactSamples > 0 ? 72d : 88d,
            CaptureQualityCanCollect = true,
            CaptureQualityAvatarGrade = possibleOneEyeArtifactSamples == 0,
            CaptureQualityReason = "synthetic eye aperture reliability audit",
            CaptureQualityCameraModeScorePercent = 90d,
            CaptureQualityFaceScaleScorePercent = 90d,
            CaptureQualityEyeScorePercent = possibleOneEyeArtifactSamples > 0 ? 56d : 88d,
            CaptureQualityMouthScorePercent = 88d,
            CaptureQualityStabilityScorePercent = 90d,
            CaptureQualityGlassesScorePercent = possibleOneEyeArtifactSamples > 0 ? 45d : 84d,
            CaptureQualityStorageScorePercent = 100d,
            FaceReliabilityPercent = 92d,
            FaceContinuityPercent = 90d,
            EyeReliabilityPercent = possibleOneEyeArtifactSamples > 0 ? 55d : 90d,
            MouthReliabilityPercent = 90d,
            AverageEyeOpeningRatio = 0.25d,
            MouthOpeningRatio = 0.08d,
            JawDroopRatio = 0.04d,
            MediaPipeAverageEyeBlinkPercent = 18d,
            MediaPipeJawOpenPercent = 8d,
            MediaPipeMouthClosePercent = 88d,
            FaceAspectRatio = 1.45d,
            EyeMidlineXToFaceWidth = 0.50d,
            MouthCenterXToFaceWidth = 0.50d,
            EyeToMouthXOffsetToFaceWidth = 0.02d,
            InterEyeDistanceToFaceWidth = 0.40d,
            LeftEyeWidthToFaceWidth = 0.14d,
            RightEyeWidthToFaceWidth = 0.14d,
            MouthWidthToFaceWidth = 0.28d,
            EyeMidlineYToFaceHeight = 0.36d,
            MouthCenterYToFaceHeight = 0.66d,
            EyeToMouthYDistanceToFaceHeight = 0.30d,
            IdentityMeasurementAvailable = true,
            PossibleOneEyeArtifact = index < possibleOneEyeArtifactSamples,
            EyeArtifactSuppressed = index < eyeArtifactSuppressedSamples,
            LeftEyeReconstructed = index < leftEyeReconstructedSamples,
            RightEyeReconstructed = index < rightEyeReconstructedSamples
        })
        .ToList();
    return new PersonalFaceCorpusReadinessBuilder().Build(model, new PersonalFaceMotionModel(), samples, measurementJournalBytes: 0L);
}

static void RunMouthVerticalAnchorAuditSmoke()
{
    var goodReadiness = BuildSyntheticMouthAnchorReadiness(
        mouthCenterYToFaceHeight: 0.70d,
        eyeToMouthYDistanceToFaceHeight: 0.34d,
        suspiciousEverySample: false);
    Require(
        goodReadiness.MouthVerticalAnchorHealthPercent >= 90d,
        $"clean mouth vertical anchor audit scored too low: {goodReadiness.MouthVerticalAnchorHealthPercent}");
    Require(
        goodReadiness.MouthVerticalAnchorSuspiciousSampleRate is 0d,
        $"clean mouth vertical anchor audit reported suspicious samples: {goodReadiness.MouthVerticalAnchorSuspiciousSampleRate}");

    var underNoseReadiness = BuildSyntheticMouthAnchorReadiness(
        mouthCenterYToFaceHeight: 0.50d,
        eyeToMouthYDistanceToFaceHeight: 0.14d,
        suspiciousEverySample: true);
    Require(
        underNoseReadiness.MouthVerticalAnchorHealthPercent < 60d,
        $"under-nose mouth vertical anchor audit did not lower health enough: {underNoseReadiness.MouthVerticalAnchorHealthPercent}");
    Require(
        underNoseReadiness.MouthVerticalAnchorSuspiciousSampleRate >= 0.99d,
        $"under-nose mouth vertical anchor audit did not count suspicious samples: {underNoseReadiness.MouthVerticalAnchorSuspiciousSampleRate}");
    Require(
        underNoseReadiness.DataAuditFindings.Any(static finding =>
            finding.Contains("mouth vertical anchor", StringComparison.OrdinalIgnoreCase)
            && finding.Contains("under the nose", StringComparison.OrdinalIgnoreCase)),
        "under-nose mouth vertical anchor audit did not explain the suspicious lip lock");
}

static PersonalFaceCorpusReadiness BuildSyntheticMouthAnchorReadiness(
    double mouthCenterYToFaceHeight,
    double eyeToMouthYDistanceToFaceHeight,
    bool suspiciousEverySample)
{
    const int sampleCount = 24;
    var now = DateTime.UtcNow;
    var model = new PersonalFaceModel
    {
        CreatedAtUtc = now,
        UpdatedAtUtc = now.AddSeconds(sampleCount),
        ObservedSamples = sampleCount,
        AcceptedSamples = sampleCount,
        AcceptedSampleWeight = sampleCount,
        AverageFaceReliabilityPercent = 92d,
        AverageFaceContinuityPercent = 90d,
        AverageEyeReliabilityPercent = 90d,
        AverageMouthReliabilityPercent = 90d,
        IdentitySignatureSamples = sampleCount,
        LearningStability = new PersonalFaceLearningStability
        {
            AnchorPercent = 74d,
            AnchorStatus = "synthetic",
            MaximumNextSampleInfluencePercent = 4d,
            MaximumEventLikeNextSampleInfluencePercent = 2d
        },
        FaceCenterX = Metric(0.50d, sampleCount),
        FaceCenterY = Metric(0.50d, sampleCount),
        FaceWidth = Metric(0.40d, sampleCount),
        FaceHeight = Metric(0.62d, sampleCount),
        HeadYawDegrees = Metric(0d, sampleCount),
        HeadPitchDegrees = Metric(0d, sampleCount),
        HeadRollDegrees = Metric(0d, sampleCount),
        AverageEyeOpeningRatio = Metric(0.26d, sampleCount),
        MouthOpeningRatio = Metric(0.08d, sampleCount),
        JawDroopRatio = Metric(0.18d, sampleCount),
        FaceAspectRatio = Metric(1.45d, sampleCount),
        EyeMidlineXToFaceWidth = Metric(0.50d, sampleCount),
        MouthCenterXToFaceWidth = Metric(0.50d, sampleCount),
        EyeToMouthXOffsetToFaceWidth = Metric(0.02d, sampleCount),
        InterEyeDistanceToFaceWidth = Metric(0.40d, sampleCount),
        LeftEyeWidthToFaceWidth = Metric(0.14d, sampleCount),
        RightEyeWidthToFaceWidth = Metric(0.14d, sampleCount),
        MouthWidthToFaceWidth = Metric(0.28d, sampleCount),
        EyeMidlineYToFaceHeight = Metric(0.36d, sampleCount),
        MouthCenterYToFaceHeight = Metric(mouthCenterYToFaceHeight, sampleCount),
        EyeToMouthYDistanceToFaceHeight = Metric(eyeToMouthYDistanceToFaceHeight, sampleCount)
    };
    var samples = Enumerable.Range(0, sampleCount)
        .Select(index => new PersonalFaceMeasurementSample
        {
            CapturedAtUtc = now.AddSeconds(index),
            SampleWeight = 1d,
            TrackingConfidence = 0.92d,
            EyeConfidence = 0.90d,
            MouthConfidence = 0.90d,
            OverallQualityPercent = 88d,
            EyeQualityPercent = 88d,
            MouthQualityPercent = 88d,
            CaptureQualityLabel = "avatar-grade",
            CaptureQualityScorePercent = 88d,
            CaptureQualityCanCollect = true,
            CaptureQualityAvatarGrade = true,
            CaptureQualityReason = "synthetic mouth anchor audit",
            CaptureQualityCameraModeScorePercent = 90d,
            CaptureQualityFaceScaleScorePercent = 90d,
            CaptureQualityEyeScorePercent = 88d,
            CaptureQualityMouthScorePercent = 88d,
            CaptureQualityStabilityScorePercent = 90d,
            CaptureQualityGlassesScorePercent = 82d,
            CaptureQualityStorageScorePercent = 100d,
            FaceReliabilityPercent = 92d,
            FaceContinuityPercent = 90d,
            EyeReliabilityPercent = 90d,
            MouthReliabilityPercent = 90d,
            AverageEyeOpeningRatio = 0.26d,
            MouthOpeningRatio = 0.08d,
            JawDroopRatio = 0.18d,
            MediaPipeJawOpenPercent = suspiciousEverySample ? 7d : 8d,
            MediaPipeMouthClosePercent = suspiciousEverySample ? 91d : 90d,
            FaceAspectRatio = 1.45d,
            EyeMidlineXToFaceWidth = 0.50d,
            MouthCenterXToFaceWidth = 0.50d,
            EyeToMouthXOffsetToFaceWidth = 0.02d,
            InterEyeDistanceToFaceWidth = 0.40d,
            LeftEyeWidthToFaceWidth = 0.14d,
            RightEyeWidthToFaceWidth = 0.14d,
            MouthWidthToFaceWidth = 0.28d,
            EyeMidlineYToFaceHeight = 0.36d,
            MouthCenterYToFaceHeight = mouthCenterYToFaceHeight,
            EyeToMouthYDistanceToFaceHeight = eyeToMouthYDistanceToFaceHeight,
            IdentityMeasurementAvailable = true
        })
        .ToList();
    return new PersonalFaceCorpusReadinessBuilder().Build(model, new PersonalFaceMotionModel(), samples, measurementJournalBytes: 0L);
}

static PersonalFaceCorpusReadiness BuildSyntheticIdentitySessionReadiness(int outlierFrames, int normalFrames)
{
    var sampleCount = Math.Max(1, outlierFrames + normalFrames);
    var now = DateTime.UtcNow;
    var model = new PersonalFaceModel
    {
        CreatedAtUtc = now,
        UpdatedAtUtc = now.AddSeconds(sampleCount),
        ObservedSamples = 160,
        AcceptedSamples = 160,
        AcceptedSampleWeight = 150d,
        AverageFaceReliabilityPercent = 92d,
        AverageFaceContinuityPercent = 90d,
        AverageEyeReliabilityPercent = 90d,
        AverageMouthReliabilityPercent = 90d,
        IdentitySignatureSamples = 150,
        LearningStability = new PersonalFaceLearningStability
        {
            AcceptedSampleWeight = 150d,
            MinimumTrackedDistributionWeight = 140d,
            AnchorTargetWeight = 180d,
            AnchorPercent = 83d,
            AnchorStatus = "warming",
            MaximumNextSampleInfluencePercent = 0.9d,
            MaximumEventLikeNextSampleInfluencePercent = 0.4d
        },
        FaceCenterX = Metric(0.50d, 150),
        FaceCenterY = Metric(0.50d, 150),
        FaceWidth = Metric(0.40d, 150),
        FaceHeight = Metric(0.62d, 150),
        HeadYawDegrees = Metric(0d, 150, 8d),
        HeadPitchDegrees = Metric(0d, 150, 5d),
        HeadRollDegrees = Metric(0d, 150, 4d),
        AverageEyeOpeningRatio = Metric(0.26d, 150),
        EyeAgreementPercent = Metric(91d, 150, 3d),
        MouthOpeningRatio = Metric(0.08d, 150),
        JawDroopRatio = Metric(0.12d, 150),
        FaceAspectRatio = Metric(1.45d, 150),
        EyeMidlineXToFaceWidth = Metric(0.50d, 150),
        MouthCenterXToFaceWidth = Metric(0.50d, 150),
        EyeToMouthXOffsetToFaceWidth = Metric(0.02d, 150),
        InterEyeDistanceToFaceWidth = Metric(0.40d, 150),
        LeftEyeWidthToFaceWidth = Metric(0.14d, 150),
        RightEyeWidthToFaceWidth = Metric(0.14d, 150),
        MouthWidthToFaceWidth = Metric(0.28d, 150),
        EyeMidlineYToFaceHeight = Metric(0.36d, 150),
        MouthCenterYToFaceHeight = Metric(0.66d, 150),
        EyeToMouthYDistanceToFaceHeight = Metric(0.30d, 150)
    };

    var samples = Enumerable.Range(0, sampleCount)
        .Select(index =>
        {
            var outlier = index < outlierFrames;
            return new PersonalFaceMeasurementSample
            {
                CapturedAtUtc = now.AddSeconds(index),
                SampleWeight = 1d,
                TrackingConfidence = 0.90d,
                EyeConfidence = 0.88d,
                MouthConfidence = 0.88d,
                OverallQualityPercent = 88d,
                EyeQualityPercent = 88d,
                MouthQualityPercent = 88d,
                CaptureQualityLabel = "avatar-grade",
                CaptureQualityScorePercent = 88d,
                CaptureQualityCanCollect = true,
                CaptureQualityAvatarGrade = true,
                CaptureQualityReason = "synthetic identity-session audit",
                CaptureQualityCameraModeScorePercent = 92d,
                CaptureQualityFaceScaleScorePercent = 90d,
                CaptureQualityEyeScorePercent = 88d,
                CaptureQualityMouthScorePercent = 88d,
                CaptureQualityStabilityScorePercent = 90d,
                CaptureQualityGlassesScorePercent = 84d,
                CaptureQualityStorageScorePercent = 100d,
                FaceReliabilityPercent = 92d,
                FaceContinuityPercent = 90d,
                EyeReliabilityPercent = 90d,
                MouthReliabilityPercent = 90d,
                AverageEyeOpeningRatio = 0.26d,
                MouthOpeningRatio = 0.08d,
                JawDroopRatio = 0.12d,
                FaceAspectRatio = outlier ? 1.78d : 1.45d,
                EyeMidlineXToFaceWidth = outlier ? 0.64d : 0.50d,
                MouthCenterXToFaceWidth = outlier ? 0.34d : 0.50d,
                EyeToMouthXOffsetToFaceWidth = outlier ? 0.17d : 0.02d,
                InterEyeDistanceToFaceWidth = outlier ? 0.24d : 0.40d,
                LeftEyeWidthToFaceWidth = outlier ? 0.08d : 0.14d,
                RightEyeWidthToFaceWidth = outlier ? 0.08d : 0.14d,
                MouthWidthToFaceWidth = outlier ? 0.46d : 0.28d,
                EyeMidlineYToFaceHeight = outlier ? 0.24d : 0.36d,
                MouthCenterYToFaceHeight = outlier ? 0.78d : 0.66d,
                EyeToMouthYDistanceToFaceHeight = outlier ? 0.54d : 0.30d,
                IdentityMeasurementAvailable = true,
                IdentityAutoGateReady = true,
                IdentityWarmupStrongMismatchGateReady = true,
                IdentityConfidencePercent = outlier ? 18d : 92d,
                IdentityComparedFeatureCount = 10,
                IdentityOutlierFeatureCount = outlier ? 7 : 0
            };
        })
        .ToList();

    return new PersonalFaceCorpusReadinessBuilder().Build(model, new PersonalFaceMotionModel(), samples, measurementJournalBytes: 0L);
}

static PersonalMetricDistribution Metric(double value, int sampleCount, double spread = 0.01d)
{
    return new PersonalMetricDistribution
    {
        SampleCount = sampleCount,
        TotalWeight = sampleCount,
        Average = value,
        Minimum = value - spread,
        Maximum = value + spread,
        StandardDeviation = spread / 2d,
        ExponentialMovingAverage = value,
        NormalLow = value - spread * 2d,
        NormalHigh = value + spread * 2d
    };
}

static void RunPoseExplainedFeatureMotionAuditSmoke()
{
    var strongPoseReadiness = BuildSyntheticPoseExplainedFeatureReadiness(
        yawRangeDegrees: 58d,
        pitchRangeDegrees: 20d,
        rollRangeDegrees: 16d,
        featureRange: 0.42d);
    Require(
        strongPoseReadiness.PoseExplainedFeatureMotionHealthPercent >= 80d,
        $"pose-explained feature audit penalized a large head turn too much: {strongPoseReadiness.PoseExplainedFeatureMotionHealthPercent}");
    Require(
        strongPoseReadiness.DataAuditFindings.All(static finding => !finding.Contains("pose-explained feature motion", StringComparison.OrdinalIgnoreCase)),
        "pose-explained feature audit produced a sliding-feature finding for a strongly posed synthetic turn");

    var weakPoseReadiness = BuildSyntheticPoseExplainedFeatureReadiness(
        yawRangeDegrees: 4d,
        pitchRangeDegrees: 3d,
        rollRangeDegrees: 3d,
        featureRange: 0.42d);
    Require(
        weakPoseReadiness.PoseExplainedFeatureMotionHealthPercent < 60d,
        $"pose-explained feature audit did not penalize feature drift with almost no head pose: {weakPoseReadiness.PoseExplainedFeatureMotionHealthPercent}");
    Require(
        weakPoseReadiness.DataAuditFindings.Any(static finding =>
            finding.Contains("pose-explained feature motion", StringComparison.OrdinalIgnoreCase)
            && finding.Contains("sliding on the head", StringComparison.OrdinalIgnoreCase)),
        "pose-explained feature audit did not explain the sliding-feature risk");
}

static PersonalFaceCorpusReadiness BuildSyntheticPoseExplainedFeatureReadiness(
    double yawRangeDegrees,
    double pitchRangeDegrees,
    double rollRangeDegrees,
    double featureRange)
{
    const int sampleCount = 120;
    var spread = featureRange / 2d;
    var now = DateTime.UtcNow;
    var model = new PersonalFaceModel
    {
        CreatedAtUtc = now,
        UpdatedAtUtc = now.AddSeconds(sampleCount),
        ObservedSamples = sampleCount,
        AcceptedSamples = sampleCount,
        AcceptedSampleWeight = sampleCount,
        AverageFaceReliabilityPercent = 92d,
        AverageFaceContinuityPercent = 90d,
        AverageEyeReliabilityPercent = 90d,
        AverageMouthReliabilityPercent = 90d,
        IdentitySignatureSamples = sampleCount,
        LearningStability = new PersonalFaceLearningStability
        {
            AnchorPercent = 80d,
            AnchorStatus = "synthetic",
            MaximumNextSampleInfluencePercent = 2d,
            MaximumEventLikeNextSampleInfluencePercent = 1d
        },
        FaceCenterX = Metric(0.50d, sampleCount),
        FaceCenterY = Metric(0.50d, sampleCount),
        FaceWidth = Metric(0.40d, sampleCount),
        FaceHeight = Metric(0.62d, sampleCount),
        HeadYawDegrees = Metric(0d, sampleCount, yawRangeDegrees / 2d),
        HeadPitchDegrees = Metric(0d, sampleCount, pitchRangeDegrees / 2d),
        HeadRollDegrees = Metric(0d, sampleCount, rollRangeDegrees / 2d),
        AverageEyeOpeningRatio = Metric(0.26d, sampleCount),
        MouthOpeningRatio = Metric(0.08d, sampleCount),
        JawDroopRatio = Metric(0.18d, sampleCount),
        FaceAspectRatio = Metric(1.45d, sampleCount),
        EyeMidlineXToFaceWidth = Metric(0.50d, sampleCount, spread),
        MouthCenterXToFaceWidth = Metric(0.50d, sampleCount, spread * 0.72d),
        EyeToMouthXOffsetToFaceWidth = Metric(0.03d, sampleCount, spread * 0.38d),
        InterEyeDistanceToFaceWidth = Metric(0.40d, sampleCount, spread * 0.58d),
        LeftEyeWidthToFaceWidth = Metric(0.14d, sampleCount),
        RightEyeWidthToFaceWidth = Metric(0.14d, sampleCount),
        MouthWidthToFaceWidth = Metric(0.28d, sampleCount, spread * 0.44d),
        EyeMidlineYToFaceHeight = Metric(0.36d, sampleCount),
        MouthCenterYToFaceHeight = Metric(0.70d, sampleCount),
        EyeToMouthYDistanceToFaceHeight = Metric(0.34d, sampleCount, spread * 0.24d)
    };

    return new PersonalFaceCorpusReadinessBuilder().Build(model, new PersonalFaceMotionModel(), [], measurementJournalBytes: 0L);
}

static bool TryGetOption(IReadOnlyList<string> args, string optionName, out string value)
{
    for (var index = 0; index < args.Count - 1; index++)
    {
        if (string.Equals(args[index], optionName, StringComparison.OrdinalIgnoreCase))
        {
            value = args[index + 1];
            return !string.IsNullOrWhiteSpace(value);
        }
    }

    value = "";
    return false;
}

static string FormatOptional(double? value)
{
    return value is double number ? number.ToString("0.###") : "";
}

static string FormatRate(double? value)
{
    return value is double number ? number.ToString("P0") : "";
}

static void RunApertureSmoke()
{
    using var openEye = CreateApertureImage(160, 80, centerY: 40, halfWidth: 52, halfHeight: 11);
    using var sleepyEye = CreateApertureImage(160, 80, centerY: 40, halfWidth: 52, halfHeight: 4);
    var eyeRegion = new CvRect(20, 18, 120, 44);
    var openEyeEstimate = OpenCvApertureEstimator.EstimateEye(openEye, eyeRegion);
    var sleepyEyeEstimate = OpenCvApertureEstimator.EstimateEye(sleepyEye, eyeRegion);
    Require(openEyeEstimate.HasAperture, "open eye aperture was not detected");
    Require(sleepyEyeEstimate.HasAperture, "sleepy eye aperture was not detected");
    Require(
        openEyeEstimate.ApertureBox.Height > sleepyEyeEstimate.ApertureBox.Height * 1.45d,
        $"eye aperture did not shrink enough: open={openEyeEstimate.ApertureBox.Height}, sleepy={sleepyEyeEstimate.ApertureBox.Height}");
    Require(openEyeEstimate.ContrastScore > 0d, "open eye aperture did not report contrast diagnostics");
    Require(openEyeEstimate.SharpnessScore > 0d, "open eye aperture did not report sharpness diagnostics");
    Require(openEyeEstimate.ProfileSampleCount > 12, $"open eye aperture profile sample count too low: {openEyeEstimate.ProfileSampleCount}");
    Require(openEyeEstimate.AverageOpeningRatio is > 0d, "open eye aperture did not report an averaged opening ratio");

    using var closedMouth = CreateApertureImage(180, 90, centerY: 48, halfWidth: 60, halfHeight: 4);
    using var openMouth = CreateApertureImage(180, 90, centerY: 50, halfWidth: 58, halfHeight: 16);
    var mouthRegion = new CvRect(24, 20, 132, 54);
    var closedMouthEstimate = OpenCvApertureEstimator.EstimateMouth(closedMouth, mouthRegion);
    var openMouthEstimate = OpenCvApertureEstimator.EstimateMouth(openMouth, mouthRegion);
    Require(closedMouthEstimate.HasAperture, "closed mouth aperture was not detected");
    Require(openMouthEstimate.HasAperture, "open mouth aperture was not detected");
    Require(
        openMouthEstimate.ApertureBox.Height > closedMouthEstimate.ApertureBox.Height * 1.75d,
        $"mouth aperture did not expand enough: open={openMouthEstimate.ApertureBox.Height}, closed={closedMouthEstimate.ApertureBox.Height}");
    Require(openMouthEstimate.ProfileSampleCount > 18, $"open mouth aperture profile sample count too low: {openMouthEstimate.ProfileSampleCount}");

    var shiftedMouthRegion = new CvRect(mouthRegion.X, Math.Max(0, mouthRegion.Y - 10), mouthRegion.Width, mouthRegion.Height);
    var refinedMouthRegion = ApertureRegionRefiner.RefineMouth(
        openMouth,
        new CvRect(0, 0, openMouth.Width, openMouth.Height),
        shiftedMouthRegion);
    Require(refinedMouthRegion.Estimate.HasAperture, "aperture region refiner did not recover a shifted mouth aperture");
    Require(
        DistanceFromCenterY(refinedMouthRegion.Estimate.ApertureBox, 50) <= 9d,
        $"aperture region refiner mouth center drifted too far: {refinedMouthRegion.Estimate.ApertureBox}");

    using var underNoseDecoyMouth = CreateMouthWithUnderNoseDecoy(
        out var decoyFace,
        out var underNoseSeed,
        out var underNoseY,
        out var realMouthY);
    var underNoseSeedEstimate = OpenCvApertureEstimator.EstimateMouth(underNoseDecoyMouth, underNoseSeed);
    Require(underNoseSeedEstimate.HasAperture, "under-nose mouth decoy seed did not reproduce an aperture");
    Require(
        DistanceFromCenterY(underNoseSeedEstimate.ApertureBox, underNoseY) < DistanceFromCenterY(underNoseSeedEstimate.ApertureBox, realMouthY),
        $"under-nose decoy setup did not initially prefer the false target: {underNoseSeedEstimate.ApertureBox}");
    var decoyRefinedMouth = ApertureRegionRefiner.RefineMouth(underNoseDecoyMouth, decoyFace, underNoseSeed);
    Require(decoyRefinedMouth.Estimate.HasAperture, "mouth refiner did not recover from under-nose decoy");
    Require(
        DistanceFromCenterY(decoyRefinedMouth.Estimate.ApertureBox, realMouthY) + 2d
        < DistanceFromCenterY(decoyRefinedMouth.Estimate.ApertureBox, underNoseY),
        $"mouth refiner still preferred the under-nose gap over the real mouth: {decoyRefinedMouth.Estimate.ApertureBox}, underNoseY={underNoseY}, mouthY={realMouthY}");

    using var glassesOpenEye = CreateApertureImage(200, 100, centerY: 50, halfWidth: 68, halfHeight: 13);
    using var glassesSleepyEye = CreateApertureImage(200, 100, centerY: 50, halfWidth: 68, halfHeight: 5);
    AddGlassesOcclusion(glassesOpenEye);
    AddGlassesOcclusion(glassesSleepyEye);
    var glassesRegion = new CvRect(24, 22, 152, 56);
    var glassesOpenEstimate = OpenCvApertureEstimator.EstimateEye(glassesOpenEye, glassesRegion);
    var glassesSleepyEstimate = OpenCvApertureEstimator.EstimateEye(glassesSleepyEye, glassesRegion);
    Require(glassesOpenEstimate.HasAperture, "glasses open eye aperture was not detected");
    Require(glassesSleepyEstimate.HasAperture, "glasses sleepy eye aperture was not detected");
    Require(
        glassesOpenEstimate.ApertureBox.Height > glassesSleepyEstimate.ApertureBox.Height * 1.25d,
        $"glasses eye aperture did not shrink enough: open={glassesOpenEstimate.ApertureBox.Height}, sleepy={glassesSleepyEstimate.ApertureBox.Height}");
    Require(
        glassesOpenEstimate.GlareRatio > openEyeEstimate.GlareRatio + 0.01d,
        $"glasses glare diagnostic did not increase: plain={openEyeEstimate.GlareRatio}, glasses={glassesOpenEstimate.GlareRatio}");
    Require(glassesOpenEstimate.DarkCoverageRatio > 0d, "glasses aperture did not report dark aperture coverage");

    var shiftedGlassesRegion = new CvRect(glassesRegion.X, Math.Max(0, glassesRegion.Y - 14), glassesRegion.Width, glassesRegion.Height);
    var shiftedGlassesEstimate = OpenCvApertureEstimator.EstimateEye(glassesOpenEye, shiftedGlassesRegion);
    var refinedGlassesRegion = ApertureRegionRefiner.RefineEye(
        glassesOpenEye,
        new CvRect(0, 0, glassesOpenEye.Width, glassesOpenEye.Height),
        shiftedGlassesRegion);
    Require(refinedGlassesRegion.Estimate.HasAperture, "aperture region refiner did not recover a shifted glasses eye aperture");
    Require(
        DistanceFromCenterY(refinedGlassesRegion.Estimate.ApertureBox, 50) <= DistanceFromCenterY(shiftedGlassesEstimate.ApertureBox, 50) + 1.5d,
        "aperture region refiner moved the shifted glasses eye estimate farther from the true eye center");
    Require(
        refinedGlassesRegion.Score > 0.20d,
        $"aperture region refiner score too weak for shifted glasses eye: {refinedGlassesRegion.Score}");

    using var openInsetFrame = CreateBottomRightEyeInsetFrame(sleepy: false);
    using var sleepyInsetFrame = CreateBottomRightEyeInsetFrame(sleepy: true);
    var openInsetEstimate = EyeInsetApertureAnalyzer.Analyze(openInsetFrame, EyeInsetApertureAnalyzer.BottomRightDefaultRegion);
    var sleepyInsetEstimate = EyeInsetApertureAnalyzer.Analyze(sleepyInsetFrame, EyeInsetApertureAnalyzer.BottomRightDefaultRegion);
    var autoInsetEstimate = EyeInsetApertureAnalyzer.AnalyzeBest(openInsetFrame);
    Require(openInsetEstimate.HasMeasurement, "bottom-right eye inset open aperture was not detected");
    Require(sleepyInsetEstimate.HasMeasurement, "bottom-right eye inset sleepy aperture was not detected");
    Require(autoInsetEstimate.HasMeasurement, "automatic eye inset selector did not detect the synthetic inset");
    Require(
        autoInsetEstimate.RegionLabel.Contains("bottom-right", StringComparison.OrdinalIgnoreCase),
        $"automatic eye inset selector did not choose the bottom-right synthetic inset: {autoInsetEstimate.RegionLabel}");
    using var blankInsetFrame = new Mat(360, 640, MatType.CV_8UC1, new Scalar(38));
    using var delayedInsetFrame = CreateBottomRightEyeInsetFrame(sleepy: false);
    using var delayedSleepyInsetFrame = CreateBottomRightEyeInsetFrame(sleepy: true);
    var delayedSelectedInset = EyeInsetApertureAnalyzer.SelectBestRegion([blankInsetFrame, delayedInsetFrame, delayedSleepyInsetFrame]);
    Require(delayedSelectedInset is not null, "multi-frame automatic eye inset selector did not recover after a blank first frame");
    var selectedDelayedInset = delayedSelectedInset ?? throw new InvalidOperationException("multi-frame automatic eye inset selector returned null after assertion");
    Require(
        selectedDelayedInset.Label.Contains("bottom-right", StringComparison.OrdinalIgnoreCase),
        $"multi-frame automatic eye inset selector did not choose the later bottom-right inset: {selectedDelayedInset.Label}");
    Require(
        openInsetEstimate.AverageEyeOpeningRatio is double openInsetRatio
        && sleepyInsetEstimate.AverageEyeOpeningRatio is double sleepyInsetRatio
        && openInsetRatio > sleepyInsetRatio * 1.20d,
        $"bottom-right eye inset aperture did not shrink enough: open={openInsetEstimate.AverageEyeOpeningRatio}, sleepy={sleepyInsetEstimate.AverageEyeOpeningRatio}");
    Require(openInsetEstimate.ImageQualityAvailable, "bottom-right eye inset did not report image-quality diagnostics");
    Require(openInsetEstimate.ContrastPercent > 10d, $"bottom-right eye inset contrast diagnostic too low: {openInsetEstimate.ContrastPercent}");
    Require(openInsetEstimate.SharpnessPercent > 10d, $"bottom-right eye inset sharpness diagnostic too low: {openInsetEstimate.SharpnessPercent}");
    Require(openInsetEstimate.DarkCoveragePercent > 1d, $"bottom-right eye inset dark coverage diagnostic too low: {openInsetEstimate.DarkCoveragePercent}");
    var eyeInsetCueAnalyzer = new EyeInsetCueAnalyzer();
    EyeInsetCueAnalysis eyeInsetCue = EyeInsetCueAnalysis.Waiting;
    for (var index = 0; index < 12; index++)
    {
        eyeInsetCue = eyeInsetCueAnalyzer.Analyze(openInsetEstimate);
    }

    Require(eyeInsetCue.BaselineReady, "bottom-right eye inset cue baseline did not become ready");
    var sleepyInsetCue = eyeInsetCueAnalyzer.Analyze(sleepyInsetEstimate);
    Require(
        sleepyInsetCue.EyeClosurePercent is > 18d,
        $"bottom-right eye inset cue did not detect eyelid closure: {sleepyInsetCue.EyeClosurePercent}");
    Require(
        sleepyInsetCue.CompositeCuePercent > 15d,
        $"bottom-right eye inset cue score too weak: {sleepyInsetCue.CompositeCuePercent}");

    var matchingAgreement = EyeInsetAgreementAnalyzer.Analyze(
        Enumerable.Range(0, 8).Select(index =>
        {
            var opening = 0.34d - index * 0.026d;
            return new EyeInsetAgreementSample(index, opening, opening * 1.8d + 0.02d);
        }));
    Require(matchingAgreement.PairedSamples == 8, $"eye-inset agreement paired sample count was wrong: {matchingAgreement.PairedSamples}");
    Require(matchingAgreement.OpeningCorrelation is > 0.98d, $"eye-inset agreement correlation was too weak: {matchingAgreement.OpeningCorrelation}");
    Require(matchingAgreement.DirectionAgreement == 1d, $"eye-inset agreement direction check failed: {matchingAgreement.DirectionAgreement}");
    Require(matchingAgreement.SlopeDirectionAgreement == 1d, $"eye-inset agreement slope direction failed: {matchingAgreement.SlopeDirectionAgreement}");
    Require(matchingAgreement.AgreementTrustPercent > 75d, $"eye-inset agreement trust too low for matching samples: {matchingAgreement.AgreementTrustPercent}");

    var disagreeingAgreement = EyeInsetAgreementAnalyzer.Analyze(
        Enumerable.Range(0, 8).Select(index => new EyeInsetAgreementSample(
            index,
            0.18d + index * 0.018d,
            0.46d - index * 0.030d)));
    Require(disagreeingAgreement.OpeningCorrelation is < -0.98d, $"eye-inset disagreement correlation did not go negative: {disagreeingAgreement.OpeningCorrelation}");
    Require(disagreeingAgreement.SlopeDirectionAgreement == 0d, $"eye-inset disagreement slope direction was not flagged: {disagreeingAgreement.SlopeDirectionAgreement}");
    Require(disagreeingAgreement.AgreementTrustPercent < 35d, $"eye-inset agreement trust too high for disagreeing samples: {disagreeingAgreement.AgreementTrustPercent}");

    using var yuNetDetector = new OpenCvYuNetFaceDetector();
    using var blankFaceFrame = new Mat(240, 320, MatType.CV_8UC1, Scalar.White);
    _ = yuNetDetector.Detect(blankFaceFrame);
    Require(
        yuNetDetector.Status.Contains("loaded", StringComparison.OrdinalIgnoreCase),
        $"YuNet face detector model did not load from portable dependencies: {yuNetDetector.Status}");

    using var lbfTracker = new OpenCvFacemarkLandmarkTracker();
    Require(
        lbfTracker.IsAvailable,
        $"OpenCV LBF facemark backend did not load from portable dependencies: {lbfTracker.Status}");

    var syntheticYuNetFace = new YuNetFaceDetection(
        new CvRect(80, 34, 160, 178),
        new Point2f(176, 100),
        new Point2f(124, 98),
        new Point2f(150, 132),
        new Point2f(178, 166),
        new Point2f(124, 164),
        0.92d);
    var yuNetCueBoxes = OpenCvFaceFeatureTracker.EstimateCueBoxesFromYuNet(syntheticYuNetFace, 320, 240);
    Require(yuNetCueBoxes.LeftEye.X < yuNetCueBoxes.RightEye.X, "YuNet eye cue boxes were not sorted in frame-left/frame-right order");
    Require(
        yuNetCueBoxes.LeftEye.X >= syntheticYuNetFace.FaceBox.X
        && yuNetCueBoxes.RightEye.Right <= syntheticYuNetFace.FaceBox.Right,
        "YuNet eye cue boxes drifted outside the detected face");
    Require(
        yuNetCueBoxes.Mouth.X <= syntheticYuNetFace.LeftMouthCorner.X
        && yuNetCueBoxes.Mouth.Right >= syntheticYuNetFace.RightMouthCorner.X
        && yuNetCueBoxes.Mouth.Y > yuNetCueBoxes.LeftEye.Bottom,
        "YuNet mouth cue box did not span the mouth corners below the eyes");

    var previousTrackedFace = new CvRect(216, 52, 72, 88);
    var faceCandidates = new[]
    {
        new FaceCandidate(new CvRect(28, 34, 142, 166), "large decoy", null, 0.88d),
        new FaceCandidate(new CvRect(220, 56, 70, 86), "tracked head", null, 0.68d)
    };
    var noHistoryFace = FaceCandidateSelector.SelectBest(faceCandidates, null, 320, 240);
    Require(noHistoryFace?.Source == "large decoy", $"face candidate selector did not choose the strongest candidate without history: {noHistoryFace?.Source}");
    var continuityFace = FaceCandidateSelector.SelectBest(faceCandidates, previousTrackedFace, 320, 240);
    Require(continuityFace?.Source == "tracked head", $"face candidate selector did not preserve the previously tracked head: {continuityFace?.Source}");
    var reframedFace = new FaceCandidate(new CvRect(42, 48, 96, 118), "camera-follow reframe", null, 0.94d);
    Require(
        !FaceCandidateSelector.IsAcceptableTrackingCandidate(reframedFace, previousTrackedFace, 320, 240, missedFrames: 0),
        "face candidate selector accepted a discontinuous jump before any missed frames");
    Require(
        FaceCandidateSelector.IsAcceptableTrackingCandidate(reframedFace, previousTrackedFace, 320, 240, missedFrames: 3),
        "face candidate selector did not accept a strong camera-follow reframe after missed frames");
    var tinyReframeDecoy = new FaceCandidate(new CvRect(8, 16, 22, 24), "tiny reframe decoy", null, 0.98d);
    Require(
        !FaceCandidateSelector.IsAcceptableTrackingCandidate(tinyReframeDecoy, previousTrackedFace, 320, 240, missedFrames: 5),
        "face candidate selector accepted an implausibly tiny reframe decoy");

    var physicalIdentity = @"\\?\usb#vid_2e1a&pid_4c01&mi_00#avatar-builder-camera";
    var mediaFoundationCameras = new[]
    {
        new CameraDevice(0, "Insta360 Link 2 Pro", physicalIdentity + @"#{e5323777-f976-4f5b-9b55-b94699c46e44}\global", "Media Foundation")
    };
    var directShowCameras = new[]
    {
        new CameraDevice(0, "Insta360 Link 2 Pro", physicalIdentity + @"#{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global", "DirectShow"),
        new CameraDevice(1, "OBS Virtual Camera", @"@device:sw:{category}\obs", "DirectShow")
    };
    var mergedCameras = CameraDeviceCatalog.MergeDevices(mediaFoundationCameras, directShowCameras);
    Require(mergedCameras.Count == 2, $"camera catalog did not merge the physical fallback pair: {mergedCameras.Count}");
    Require(mergedCameras[0].HasFallbackDevice, "camera catalog did not attach the DirectShow fallback camera");
    Require(mergedCameras[0].DirectShowDeviceOrSelf().Source == "DirectShow", "camera catalog did not expose the DirectShow fallback for controls");
    Require(mergedCameras[1].DisplayName.Contains("DirectShow", StringComparison.Ordinal), "software-only camera display name should keep its source");

    var recommended4KMode = CameraModeRecommendation.FindRecommendedMode(
        [
            CameraVideoMode.Auto,
            new CameraVideoMode("1280x720 @ 30 fps (MJPEG)", 1280, 720, 30d, "mjpeg"),
            new CameraVideoMode("1920x1080 @ 10 fps (MJPEG)", 1920, 1080, 10d, "mjpeg"),
            new CameraVideoMode("3840x2160 @ 24 fps (H264)", 3840, 2160, 24d, "h264"),
            new CameraVideoMode("3840x2160 @ 30 fps (MJPEG)", 3840, 2160, 30d, "mjpeg")
        ],
        maximumWidth: 3840,
        targetFramesPerSecond: 5d);
    Require(
        recommended4KMode is { Width: 3840, Height: 2160, FramesPerSecond: 24d },
        $"4K tracking fidelity did not choose the least-heavy 4K camera mode: {recommended4KMode?.Label}");

    var recommendedHdMode = CameraModeRecommendation.FindRecommendedMode(
        [
            CameraVideoMode.Auto,
            new CameraVideoMode("1920x1080 @ 10 fps (MJPEG)", 1920, 1080, 10d, "mjpeg"),
            new CameraVideoMode("3840x2160 @ 30 fps (MJPEG)", 3840, 2160, 30d, "mjpeg")
        ],
        maximumWidth: 1920,
        targetFramesPerSecond: 10d);
    Require(
        recommendedHdMode is { Width: 1920, Height: 1080, FramesPerSecond: 10d },
        $"HD tracking fidelity did not avoid over-selecting 4K: {recommendedHdMode?.Label}");

    var denseInfo = DenseFaceLandmarkModelInfo.Load();
    Require(denseInfo.ExpectedLandmarks is 0 or 478, $"dense manifest landmark count is unexpected: {denseInfo.ExpectedLandmarks}");
    Require(
        string.IsNullOrWhiteSpace(denseInfo.ModelUrl) || denseInfo.ModelUrl.Contains("face_landmarker.task", StringComparison.OrdinalIgnoreCase),
        $"dense model URL should point at a Face Landmarker task bundle: {denseInfo.ModelUrl}");
    if (!string.Equals(denseInfo.InferenceImplementationStatus, "ready", StringComparison.OrdinalIgnoreCase))
    {
        Require(!denseInfo.CanRunInference, "dense backend advertised inference before the runtime was marked ready");
    }

    using var denseTracker = new DenseFaceMeshLandmarkTracker();
    Require(!denseTracker.IsAvailable, "dense tracker advertised availability before its C# inference bridge is compiled");

    using var mediaPipeSidecarTracker = new MediaPipeFaceLandmarkerSidecarTracker();
    if (mediaPipeSidecarTracker.IsAvailable)
    {
        var sidecarResult = mediaPipeSidecarTracker.Detect(CreateBlankBitmap(256, 256), DateTime.UtcNow);
        Require(
            sidecarResult.BackendName.Contains("MediaPipe", StringComparison.OrdinalIgnoreCase),
            $"MediaPipe sidecar smoke used an unexpected backend: {sidecarResult.BackendName}");
        Require(
            sidecarResult.BackendStatus.Contains("MediaPipe", StringComparison.OrdinalIgnoreCase),
            $"MediaPipe sidecar smoke did not return a MediaPipe status: {sidecarResult.BackendStatus}");
        Require(
            !sidecarResult.BackendStatus.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            && !sidecarResult.BackendStatus.Contains("failed", StringComparison.OrdinalIgnoreCase),
            $"MediaPipe sidecar smoke returned a process failure: {sidecarResult.BackendStatus}");
    }

    using var scaledOpenEye = CreateApertureImage(320, 160, centerY: 80, halfWidth: 104, halfHeight: 22);
    using var scaledSleepyEye = CreateApertureImage(320, 160, centerY: 80, halfWidth: 104, halfHeight: 8);
    var scaledRegion = new CvRect(40, 36, 240, 88);
    var scaledOpenEstimate = OpenCvApertureEstimator.EstimateEye(scaledOpenEye, scaledRegion);
    var scaledSleepyEstimate = OpenCvApertureEstimator.EstimateEye(scaledSleepyEye, scaledRegion);
    Require(
        scaledOpenEstimate.HasAperture
        && scaledSleepyEstimate.HasAperture
        && scaledOpenEstimate.ApertureBox.Height > scaledSleepyEstimate.ApertureBox.Height * 1.45d,
        "scaled eye aperture did not track open/sleepy state");

    var initialPairReconstructor = new FaceLandmarkTemporalReconstructor();
    var initialPairedEyeReconstruction = initialPairReconstructor.Update(new FaceLandmarkFrame
    {
        HasFace = true,
        Source = "synthetic first-frame paired-eye reconstruction",
        CapturedAtUtc = DateTime.UtcNow,
        TrackingConfidence = 0.76d,
        EyeConfidence = 0.42d,
        MouthConfidence = 0.70d,
        FaceContour = CreateNormalizedOval(0.50d, 0.48d, 0.22d, 0.32d),
        LeftEyeContour = [],
        RightEyeContour = CreateNormalizedOval(0.58d, 0.39d, 0.055d, 0.055d * 0.20d),
        InnerLipContour = CreateNormalizedOval(0.50d, 0.62d, 0.080d, 0.080d * 0.08d),
        JawContour = [new(0.36d, 0.62d), new(0.43d, 0.74d), new(0.50d, 0.79d), new(0.57d, 0.74d), new(0.64d, 0.62d)]
    });
    Require(
        initialPairedEyeReconstruction.LeftEyeReconstructed
        && CalculateContourOpeningRatio(initialPairedEyeReconstruction.LeftEyeContour) is double initialLeftEye
        && CalculateContourOpeningRatio(initialPairedEyeReconstruction.RightEyeContour) is double initialRightEye
        && Math.Abs(initialLeftEye - initialRightEye) < 0.03d,
        "temporal reconstructor did not recreate a first-frame missing eye from the paired eye");

    var reconstructor = new FaceLandmarkTemporalReconstructor();
    var reconstructedOpen = reconstructor.Update(CreateSyntheticLandmarkFrame(
        DateTime.UtcNow,
        leftEyeRatio: 0.24d,
        rightEyeRatio: 0.24d,
        mouthRatio: 0.06d,
        eyeConfidence: 0.70d,
        mouthConfidence: 0.70d));
    var reconstructedMissingEye = reconstructor.Update(CreateSyntheticLandmarkFrame(
        DateTime.UtcNow.AddSeconds(1),
        leftEyeRatio: 0.09d,
        rightEyeRatio: null,
        mouthRatio: 0.08d,
        eyeConfidence: 0.34d,
        mouthConfidence: 0.70d));
    Require(
        CalculateContourOpeningRatio(reconstructedMissingEye.RightEyeContour) is double reconstructedRightEye
        && reconstructedRightEye < CalculateContourOpeningRatio(reconstructedOpen.RightEyeContour),
        "temporal reconstructor did not recreate a missing glasses-occluded eye as closing");
    Require(
        reconstructedMissingEye.Source.Contains("temporal reconstruction", StringComparison.OrdinalIgnoreCase),
        "temporal reconstructor did not mark reconstructed missing-eye frame");
    Require(
        reconstructedMissingEye.RightEyeReconstructed,
        "temporal reconstructor did not flag the missing right eye as reconstructed");

    var fallbackGuardReconstructor = new FaceLandmarkTemporalReconstructor();
    var fallbackGuardStartedAt = DateTime.UtcNow.AddSeconds(5);
    var fallbackGuardOpen = fallbackGuardReconstructor.Update(CreateSyntheticLandmarkFrame(
        fallbackGuardStartedAt,
        leftEyeRatio: 0.20d,
        rightEyeRatio: 0.20d,
        mouthRatio: 0.06d,
        eyeConfidence: 0.72d,
        mouthConfidence: 0.70d,
        source: "OpenCV aperture fallback baseline"));
    var fallbackGuardFalseOpen = fallbackGuardReconstructor.Update(CreateSyntheticLandmarkFrame(
        fallbackGuardStartedAt.AddSeconds(0.20d),
        leftEyeRatio: 0.62d,
        rightEyeRatio: 0.62d,
        mouthRatio: 0.08d,
        eyeConfidence: 0.92d,
        mouthConfidence: 0.70d,
        source: "OpenCV aperture fallback false-open glasses contour"));
    Require(
        CalculateContourOpeningRatio(fallbackGuardOpen.LeftEyeContour) is double fallbackGuardOpenEye
        && CalculateContourOpeningRatio(fallbackGuardFalseOpen.LeftEyeContour) is double fallbackGuardLimitedEye
        && fallbackGuardLimitedEye < fallbackGuardOpenEye + 0.025d,
        $"low-fidelity fallback eye opening was allowed to jump too quickly: open={CalculateContourOpeningRatio(fallbackGuardOpen.LeftEyeContour)}, falseOpen={CalculateContourOpeningRatio(fallbackGuardFalseOpen.LeftEyeContour)}");
    Require(
        fallbackGuardFalseOpen.LeftEyeReconstructed && fallbackGuardFalseOpen.RightEyeReconstructed,
        "low-fidelity fallback eye-opening guard did not expose reconstruction flags");

    var denseDirectReconstructor = new FaceLandmarkTemporalReconstructor();
    var denseDirectStartedAt = DateTime.UtcNow.AddSeconds(8);
    var denseDirectOpen = denseDirectReconstructor.Update(CreateSyntheticLandmarkFrame(
        denseDirectStartedAt,
        leftEyeRatio: 0.20d,
        rightEyeRatio: 0.20d,
        mouthRatio: 0.06d,
        eyeConfidence: 0.90d,
        mouthConfidence: 0.72d,
        source: "MediaPipe Face Landmarker sidecar direct dense eye baseline",
        eyeBlinkLeftScore: 0.08d,
        eyeBlinkRightScore: 0.08d));
    var denseDirectWider = denseDirectReconstructor.Update(CreateSyntheticLandmarkFrame(
        denseDirectStartedAt.AddSeconds(0.20d),
        leftEyeRatio: 0.42d,
        rightEyeRatio: 0.42d,
        mouthRatio: 0.06d,
        eyeConfidence: 0.90d,
        mouthConfidence: 0.72d,
        source: "MediaPipe Face Landmarker sidecar direct dense eye quick movement",
        eyeBlinkLeftScore: 0.04d,
        eyeBlinkRightScore: 0.04d));
    Require(
        CalculateContourOpeningRatio(denseDirectOpen.LeftEyeContour) is double denseDirectOpenEye
        && CalculateContourOpeningRatio(denseDirectWider.LeftEyeContour) is double denseDirectWiderEye
        && denseDirectWiderEye > denseDirectOpenEye + 0.15d,
        $"high-confidence dense direct eye opening was incorrectly rate-limited: open={CalculateContourOpeningRatio(denseDirectOpen.LeftEyeContour)}, wider={CalculateContourOpeningRatio(denseDirectWider.LeftEyeContour)}");
    Require(
        !denseDirectWider.LeftEyeReconstructed && !denseDirectWider.RightEyeReconstructed,
        "high-confidence dense direct eye movement was incorrectly labeled as reconstructed");

    var blendshapeGuidedReconstructor = new FaceLandmarkTemporalReconstructor();
    var blendshapeOpenFrame = blendshapeGuidedReconstructor.Update(CreateSyntheticLandmarkFrame(
        DateTime.UtcNow.AddSeconds(10),
        leftEyeRatio: 0.28d,
        rightEyeRatio: 0.28d,
        mouthRatio: 0.06d,
        eyeConfidence: 0.72d,
        mouthConfidence: 0.72d,
        source: "MediaPipe Face Landmarker sidecar synthetic dense mesh",
        eyeBlinkLeftScore: 0.08d,
        eyeBlinkRightScore: 0.09d,
        jawOpenScore: 0.05d,
        mouthCloseScore: 0.88d));
    var blendshapeOccludedFrame = blendshapeGuidedReconstructor.Update(CreateSyntheticLandmarkFrame(
        DateTime.UtcNow.AddSeconds(11),
        leftEyeRatio: null,
        rightEyeRatio: null,
        mouthRatio: null,
        eyeConfidence: 0.24d,
        mouthConfidence: 0.24d,
        source: "MediaPipe Face Landmarker sidecar synthetic dense mesh with glasses and lip occlusion",
        eyeBlinkLeftScore: 0.78d,
        eyeBlinkRightScore: 0.80d,
        jawOpenScore: 0.72d,
        mouthCloseScore: 0.14d));
    Require(
        CalculateContourOpeningRatio(blendshapeOpenFrame.LeftEyeContour) is double blendshapeOpenEye
        && CalculateContourOpeningRatio(blendshapeOccludedFrame.LeftEyeContour) is double blendshapeClosedEye
        && blendshapeClosedEye < blendshapeOpenEye * 0.55d,
        $"MediaPipe blink evidence did not guide missing-eye reconstruction toward closure: open={CalculateContourOpeningRatio(blendshapeOpenFrame.LeftEyeContour)}, closed={CalculateContourOpeningRatio(blendshapeOccludedFrame.LeftEyeContour)}");
    Require(
        CalculateContourOpeningRatio(blendshapeOpenFrame.InnerLipContour) is double blendshapeClosedMouth
        && CalculateContourOpeningRatio(blendshapeOccludedFrame.InnerLipContour) is double blendshapeOpenMouth
        && blendshapeOpenMouth > blendshapeClosedMouth * 1.80d,
        $"MediaPipe jaw/mouth evidence did not guide missing-lip reconstruction toward opening: closed={CalculateContourOpeningRatio(blendshapeOpenFrame.InnerLipContour)}, open={CalculateContourOpeningRatio(blendshapeOccludedFrame.InnerLipContour)}");
    Require(
        blendshapeOccludedFrame.LeftEyeReconstructed
        && blendshapeOccludedFrame.RightEyeReconstructed
        && blendshapeOccludedFrame.MouthReconstructed,
        "MediaPipe-guided missing contour reconstruction did not carry reconstruction evidence flags");

    var rapidBlendshapeReconstructor = new FaceLandmarkTemporalReconstructor();
    var rapidCapturedAt = DateTime.UtcNow.AddSeconds(20);
    var rapidOpenFrame = rapidBlendshapeReconstructor.Update(CreateSyntheticLandmarkFrame(
        rapidCapturedAt,
        leftEyeRatio: 0.28d,
        rightEyeRatio: 0.28d,
        mouthRatio: 0.06d,
        eyeConfidence: 0.72d,
        mouthConfidence: 0.72d,
        source: "MediaPipe Face Landmarker sidecar rapid baseline",
        eyeBlinkLeftScore: 0.08d,
        eyeBlinkRightScore: 0.09d,
        jawOpenScore: 0.05d,
        mouthCloseScore: 0.88d));
    var rapidOccludedFrame = rapidBlendshapeReconstructor.Update(CreateSyntheticLandmarkFrame(
        rapidCapturedAt.AddSeconds(0.20d),
        leftEyeRatio: null,
        rightEyeRatio: null,
        mouthRatio: null,
        eyeConfidence: 0.22d,
        mouthConfidence: 0.22d,
        source: "MediaPipe Face Landmarker sidecar rapid glasses occlusion",
        eyeBlinkLeftScore: 0.86d,
        eyeBlinkRightScore: 0.84d,
        jawOpenScore: 0.82d,
        mouthCloseScore: 0.10d));
    Require(
        CalculateContourOpeningRatio(rapidOpenFrame.LeftEyeContour) is double rapidOpenEye
        && CalculateContourOpeningRatio(rapidOccludedFrame.LeftEyeContour) is double rapidClosedEye
        && rapidClosedEye < rapidOpenEye * 0.55d,
        $"rapid MediaPipe blink evidence was over-smoothed during missing-eye reconstruction: open={CalculateContourOpeningRatio(rapidOpenFrame.LeftEyeContour)}, closed={CalculateContourOpeningRatio(rapidOccludedFrame.LeftEyeContour)}");
    Require(
        CalculateContourOpeningRatio(rapidOpenFrame.InnerLipContour) is double rapidClosedMouth
        && CalculateContourOpeningRatio(rapidOccludedFrame.InnerLipContour) is double rapidOpenMouth
        && rapidOpenMouth > rapidClosedMouth + 0.08d,
        $"rapid MediaPipe jaw evidence was over-smoothed during missing-lip reconstruction: closed={CalculateContourOpeningRatio(rapidOpenFrame.InnerLipContour)}, open={CalculateContourOpeningRatio(rapidOccludedFrame.InnerLipContour)}");

    var reconstructedArtifact = reconstructor.Update(CreateSyntheticLandmarkFrame(
        DateTime.UtcNow.AddSeconds(2),
        leftEyeRatio: 0.72d,
        rightEyeRatio: 0.10d,
        mouthRatio: 0.10d,
        eyeConfidence: 0.32d,
        mouthConfidence: 0.70d));
    var repairedArtifactEye = CalculateContourOpeningRatio(reconstructedArtifact.LeftEyeContour);
    Require(
        repairedArtifactEye is < 0.28d,
        $"temporal reconstructor did not suppress a likely glasses artifact eye aperture: {repairedArtifactEye}");
    Require(
        reconstructedArtifact.LeftEyeReconstructed && reconstructedArtifact.EyeArtifactSuppressed,
        "temporal reconstructor did not flag glasses artifact suppression evidence");

    var reconstructedMetric = new FaceLandmarkMetricCalculator().Update(reconstructedArtifact);
    Require(
        reconstructedMetric.LeftEyeReconstructed && reconstructedMetric.EyeArtifactSuppressed,
        "landmark metrics did not carry reconstruction and artifact suppression flags");

    var shiftedShapeArtifact = reconstructor.Update(new FaceLandmarkFrame
    {
        HasFace = true,
        Source = "synthetic shifted glasses contour artifact",
        CapturedAtUtc = DateTime.UtcNow.AddSeconds(3),
        TrackingConfidence = 0.78d,
        EyeConfidence = 0.31d,
        MouthConfidence = 0.70d,
        EyeImageQualityAvailable = true,
        EyeGlarePercent = 22d,
        EyeContrastPercent = 76d,
        EyeSharpnessPercent = 68d,
        FaceContour = CreateNormalizedOval(0.50d, 0.48d, 0.22d, 0.32d),
        LeftEyeContour = CreateNormalizedOval(0.24d, 0.30d, 0.16d, 0.016d),
        RightEyeContour = CreateNormalizedOval(0.58d, 0.39d, 0.055d, 0.055d * 0.10d),
        InnerLipContour = CreateNormalizedOval(0.50d, 0.62d, 0.080d, 0.080d * 0.10d),
        JawContour = [new(0.36d, 0.62d), new(0.43d, 0.74d), new(0.50d, 0.79d), new(0.57d, 0.74d), new(0.64d, 0.62d)]
    });
    var shiftedRepairBounds = GetContourBounds(shiftedShapeArtifact.LeftEyeContour);
    Require(
        shiftedShapeArtifact.LeftEyeReconstructed
        && shiftedShapeArtifact.EyeArtifactSuppressed,
        "temporal reconstructor did not flag shifted glasses contour artifact suppression");
    Require(
        shiftedRepairBounds is WpfRect repairedLeftBounds
        && repairedLeftBounds.Left + repairedLeftBounds.Width / 2d > 0.34d
        && repairedLeftBounds.Left + repairedLeftBounds.Width / 2d < 0.50d,
        $"temporal reconstructor remembered shifted glasses artifact geometry: {shiftedRepairBounds}");

    var metricCalculator = new FaceLandmarkMetricCalculator();
    var first = metricCalculator.Update(new FaceLandmarkFrame
    {
        HasFace = true,
        CapturedAtUtc = DateTime.UtcNow,
        Source = "smoke",
        TrackingConfidence = 0.8d,
        EyeConfidence = 0.8d,
        MouthConfidence = 0.8d,
        LeftEyeContour = Normalize(openEyeEstimate.Contour, 160, 80),
        RightEyeContour = Normalize(openEyeEstimate.Contour, 160, 80),
        InnerLipContour = Normalize(closedMouthEstimate.Contour, 180, 90),
        BlendshapeScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["eyeBlinkLeft"] = 0.08d,
            ["eyeBlinkRight"] = 0.10d,
            ["jawOpen"] = 0.05d,
            ["mouthClose"] = 0.70d
        }
    });
    var second = metricCalculator.Update(new FaceLandmarkFrame
    {
        HasFace = true,
        CapturedAtUtc = DateTime.UtcNow.AddSeconds(1),
        Source = "smoke",
        TrackingConfidence = 0.8d,
        EyeConfidence = 0.8d,
        MouthConfidence = 0.8d,
        LeftEyeContour = Normalize(sleepyEyeEstimate.Contour, 160, 80),
        RightEyeContour = Normalize(sleepyEyeEstimate.Contour, 160, 80),
        InnerLipContour = Normalize(openMouthEstimate.Contour, 180, 90),
        BlendshapeScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["eyeBlinkLeft"] = 0.62d,
            ["eyeBlinkRight"] = 0.66d,
            ["jawOpen"] = 0.48d,
            ["mouthClose"] = 0.24d
        }
    });

    Require(
        first.RawAverageEyeOpeningRatio is double firstRawEye
        && second.RawAverageEyeOpeningRatio is double secondRawEye
        && secondRawEye < firstRawEye,
        "raw landmark eye metric did not decrease for the sleepy synthetic eye");
    Require(
        first.AverageEyeOpeningRatio is double firstEye
        && second.AverageEyeOpeningRatio is double secondEye
        && secondEye < firstEye,
        "smoothed landmark eye metric did not decrease for the sleepy synthetic eye");
    Require(
        first.RawMouthOpeningRatio is double firstRawMouth
        && second.RawMouthOpeningRatio is double secondRawMouth
        && secondRawMouth > firstRawMouth,
        "raw landmark mouth metric did not increase for the opening synthetic mouth");
    Require(
        second.MouthOpeningVelocityPerSecond is > 0d,
        "landmark mouth velocity did not increase for the opening synthetic mouth");
    Require(
        second.MediaPipeAverageEyeBlinkPercent is > 60d
        && second.MediaPipeJawOpenPercent is > 45d
        && second.MediaPipeMouthClosePercent is < 30d,
        $"MediaPipe blendshape evidence was not carried into landmark metrics: blink={second.MediaPipeAverageEyeBlinkPercent}, jaw={second.MediaPipeJawOpenPercent}, mouthClose={second.MediaPipeMouthClosePercent}");
    var mouthFalseClosedCalculator = new FaceLandmarkMetricCalculator();
    var mouthClosedReference = mouthFalseClosedCalculator.Update(new FaceLandmarkFrame
    {
        HasFace = true,
        CapturedAtUtc = DateTime.UtcNow,
        Source = "MediaPipe Face Landmarker sidecar synthetic dense mesh",
        TrackingConfidence = 0.92d,
        EyeConfidence = 0.80d,
        MouthConfidence = 0.90d,
        LeftEyeContour = Normalize(openEyeEstimate.Contour, 160, 80),
        RightEyeContour = Normalize(openEyeEstimate.Contour, 160, 80),
        InnerLipContour = Normalize(closedMouthEstimate.Contour, 180, 90),
        BlendshapeScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["jawOpen"] = 0.05d,
            ["mouthClose"] = 0.82d
        }
    });
    var mouthFalseClosedOpen = mouthFalseClosedCalculator.Update(new FaceLandmarkFrame
    {
        HasFace = true,
        CapturedAtUtc = DateTime.UtcNow.AddSeconds(1),
        Source = "MediaPipe Face Landmarker sidecar synthetic dense mesh",
        TrackingConfidence = 0.92d,
        EyeConfidence = 0.80d,
        MouthConfidence = 0.90d,
        LeftEyeContour = Normalize(openEyeEstimate.Contour, 160, 80),
        RightEyeContour = Normalize(openEyeEstimate.Contour, 160, 80),
        InnerLipContour = Normalize(closedMouthEstimate.Contour, 180, 90),
        BlendshapeScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["jawOpen"] = 0.72d,
            ["mouthClose"] = 0.16d
        }
    });
    Require(
        mouthClosedReference.RawMouthOpeningRatio is double closedRawMouth
        && mouthFalseClosedOpen.RawMouthOpeningRatio is double falseClosedRawMouth
        && falseClosedRawMouth <= closedRawMouth * 1.12d,
        $"synthetic false-closed mouth contour did not preserve raw audit measurement: closed={mouthClosedReference.RawMouthOpeningRatio}, falseClosed={mouthFalseClosedOpen.RawMouthOpeningRatio}");
    Require(
        mouthClosedReference.MouthOpeningRatio is double closedWorkingMouth
        && mouthFalseClosedOpen.MouthOpeningRatio is double liftedWorkingMouth
        && liftedWorkingMouth > closedWorkingMouth + 0.04d
        && mouthFalseClosedOpen.MouthOpeningVelocityPerSecond is > 0d,
        $"MediaPipe jaw/mouth corroboration did not lift a false-closed lip contour: closed={mouthClosedReference.MouthOpeningRatio}, lifted={mouthFalseClosedOpen.MouthOpeningRatio}, velocity={mouthFalseClosedOpen.MouthOpeningVelocityPerSecond}");
    Require(
        mouthFalseClosedOpen.MediaPipeMouthOpeningCorrected
        && mouthFalseClosedOpen.MediaPipeMouthOpeningCorrectionRatio is > 0d,
        $"false-closed mouth lift did not expose positive MediaPipe correction evidence: {mouthFalseClosedOpen.MediaPipeMouthOpeningCorrectionRatio}");
    var mouthFalseOpenCalculator = new FaceLandmarkMetricCalculator();
    var mouthClosedForCap = mouthFalseOpenCalculator.Update(new FaceLandmarkFrame
    {
        HasFace = true,
        CapturedAtUtc = DateTime.UtcNow,
        Source = "MediaPipe Face Landmarker sidecar synthetic dense mesh",
        TrackingConfidence = 0.92d,
        EyeConfidence = 0.80d,
        MouthConfidence = 0.90d,
        LeftEyeContour = Normalize(openEyeEstimate.Contour, 160, 80),
        RightEyeContour = Normalize(openEyeEstimate.Contour, 160, 80),
        InnerLipContour = Normalize(closedMouthEstimate.Contour, 180, 90),
        BlendshapeScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["jawOpen"] = 0.04d,
            ["mouthClose"] = 0.84d
        }
    });
    var mouthFalseOpenClosed = mouthFalseOpenCalculator.Update(new FaceLandmarkFrame
    {
        HasFace = true,
        CapturedAtUtc = DateTime.UtcNow.AddSeconds(1),
        Source = "MediaPipe Face Landmarker sidecar synthetic dense mesh",
        TrackingConfidence = 0.92d,
        EyeConfidence = 0.80d,
        MouthConfidence = 0.90d,
        LeftEyeContour = Normalize(openEyeEstimate.Contour, 160, 80),
        RightEyeContour = Normalize(openEyeEstimate.Contour, 160, 80),
        InnerLipContour = Normalize(openMouthEstimate.Contour, 180, 90),
        BlendshapeScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["jawOpen"] = 0.06d,
            ["mouthClose"] = 0.86d
        }
    });
    Require(
        mouthFalseOpenClosed.RawMouthOpeningRatio is double rawFalseOpenMouth
        && mouthClosedForCap.RawMouthOpeningRatio is double rawClosedForCap
        && rawFalseOpenMouth > rawClosedForCap * 1.35d,
        $"synthetic false-open mouth contour did not preserve raw audit measurement: closed={mouthClosedForCap.RawMouthOpeningRatio}, falseOpen={mouthFalseOpenClosed.RawMouthOpeningRatio}");
    Require(
        mouthFalseOpenClosed.MouthOpeningRatio is double cappedClosedMouth
        && mouthFalseOpenClosed.RawMouthOpeningRatio is double falseOpenRawForCap
        && falseOpenRawForCap > 0d
        && cappedClosedMouth < falseOpenRawForCap * 0.78d,
        $"MediaPipe jaw/mouth corroboration did not cap a false-open lip contour: raw={mouthFalseOpenClosed.RawMouthOpeningRatio}, capped={mouthFalseOpenClosed.MouthOpeningRatio}");
    Require(
        mouthFalseOpenClosed.MediaPipeMouthOpeningCorrected
        && mouthFalseOpenClosed.MediaPipeMouthOpeningCorrectionRatio is < 0d,
        $"false-open mouth cap did not expose negative MediaPipe correction evidence: {mouthFalseOpenClosed.MediaPipeMouthOpeningCorrectionRatio}");
    var glassesMetricCalculator = new FaceLandmarkMetricCalculator();
    var glassesOpen = glassesMetricCalculator.Update(new FaceLandmarkFrame
    {
        HasFace = true,
        CapturedAtUtc = DateTime.UtcNow,
        Source = "MediaPipe Face Landmarker sidecar synthetic dense mesh",
        TrackingConfidence = 0.92d,
        EyeConfidence = 0.90d,
        MouthConfidence = 0.80d,
        LeftEyeContour = Normalize(openEyeEstimate.Contour, 160, 80),
        RightEyeContour = Normalize(openEyeEstimate.Contour, 160, 80),
        InnerLipContour = Normalize(closedMouthEstimate.Contour, 180, 90),
        BlendshapeScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["eyeBlinkLeft"] = 0.05d,
            ["eyeBlinkRight"] = 0.06d
        }
    });
    var glassesFalseOpenBlink = glassesMetricCalculator.Update(new FaceLandmarkFrame
    {
        HasFace = true,
        CapturedAtUtc = DateTime.UtcNow.AddSeconds(1),
        Source = "MediaPipe Face Landmarker sidecar synthetic dense mesh",
        TrackingConfidence = 0.92d,
        EyeConfidence = 0.90d,
        MouthConfidence = 0.80d,
        LeftEyeContour = Normalize(openEyeEstimate.Contour, 160, 80),
        RightEyeContour = Normalize(openEyeEstimate.Contour, 160, 80),
        InnerLipContour = Normalize(closedMouthEstimate.Contour, 180, 90),
        BlendshapeScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["eyeBlinkLeft"] = 0.92d,
            ["eyeBlinkRight"] = 0.90d
        }
    });
    Require(
        glassesOpen.RawAverageEyeOpeningRatio is double rawOpen
        && glassesFalseOpenBlink.RawAverageEyeOpeningRatio is double rawFalseOpen
        && rawFalseOpen >= rawOpen * 0.92d,
        $"synthetic glasses false-open contour did not preserve raw audit measurement: open={glassesOpen.RawAverageEyeOpeningRatio}, blink={glassesFalseOpenBlink.RawAverageEyeOpeningRatio}");
    Require(
        glassesOpen.AverageEyeOpeningRatio is double openMetric
        && glassesFalseOpenBlink.AverageEyeOpeningRatio is double blinkCappedMetric
        && blinkCappedMetric < openMetric * 0.70d,
        $"MediaPipe blink corroboration did not cap a false-open glasses contour: open={glassesOpen.AverageEyeOpeningRatio}, blink={glassesFalseOpenBlink.AverageEyeOpeningRatio}");
    Require(
        glassesFalseOpenBlink.MediaPipeEyeOpeningCorrected
        && glassesFalseOpenBlink.MediaPipeEyeOpeningCorrectionRatio is < 0d,
        $"false-open glasses blink cap did not expose negative MediaPipe eye correction evidence: {glassesFalseOpenBlink.MediaPipeEyeOpeningCorrectionRatio}");

    var lowFidelityEyeMetricCalculator = new FaceLandmarkMetricCalculator();
    var lowFidelityStartedAt = DateTime.UtcNow.AddSeconds(12);
    var lowFidelityEyeReference = lowFidelityEyeMetricCalculator.Update(CreateSyntheticLandmarkFrame(
        lowFidelityStartedAt,
        leftEyeRatio: 0.20d,
        rightEyeRatio: 0.20d,
        mouthRatio: 0.06d,
        eyeConfidence: 0.74d,
        mouthConfidence: 0.70d,
        source: "OpenCV aperture fallback baseline"));
    var lowFidelityFalseOpenEye = lowFidelityEyeMetricCalculator.Update(CreateSyntheticLandmarkFrame(
        lowFidelityStartedAt.AddSeconds(0.20d),
        leftEyeRatio: 0.62d,
        rightEyeRatio: 0.62d,
        mouthRatio: 0.07d,
        eyeConfidence: 0.84d,
        mouthConfidence: 0.70d,
        source: "OpenCV aperture fallback false-open glasses contour"));
    Require(
        lowFidelityFalseOpenEye.RawAverageEyeOpeningRatio is double lowFidelityRawFalseOpen
        && lowFidelityRawFalseOpen > 0.58d,
        $"low-fidelity false-open eye guard did not preserve raw audit measurement: raw={lowFidelityFalseOpenEye.RawAverageEyeOpeningRatio}");
    Require(
        lowFidelityEyeReference.AverageEyeOpeningRatio is double lowFidelityReference
        && lowFidelityFalseOpenEye.AverageEyeOpeningRatio is double lowFidelityGuarded
        && lowFidelityGuarded < lowFidelityReference + 0.02d,
        $"low-fidelity false-open eye guard allowed the working metric to jump: reference={lowFidelityEyeReference.AverageEyeOpeningRatio}, guarded={lowFidelityFalseOpenEye.AverageEyeOpeningRatio}");
    Require(
        lowFidelityFalseOpenEye.MediaPipeEyeOpeningCorrectionRatio is < 0d,
        $"low-fidelity false-open eye guard did not expose negative correction evidence: {lowFidelityFalseOpenEye.MediaPipeEyeOpeningCorrectionRatio}");

    Require(
        second.Status.Contains("mp blink", StringComparison.OrdinalIgnoreCase)
        && second.Status.Contains("jaw", StringComparison.OrdinalIgnoreCase),
        $"landmark status did not expose MediaPipe blink/jaw evidence for overlays: {second.Status}");

    var artifactMetricCalculator = new FaceLandmarkMetricCalculator();
    var oneEyeArtifact = artifactMetricCalculator.Update(new FaceLandmarkFrame
    {
        HasFace = true,
        CapturedAtUtc = DateTime.UtcNow,
        Source = "smoke aperture glare",
        TrackingConfidence = 0.8d,
        EyeConfidence = 0.76d,
        MouthConfidence = 0.70d,
        EyeImageQualityAvailable = true,
        EyeGlarePercent = 18d,
        EyeContrastPercent = 92d,
        EyeSharpnessPercent = 84d,
        LeftEyeContour = CreateNormalizedOval(0.42d, 0.39d, 0.055d, 0.055d * 0.72d),
        RightEyeContour = CreateNormalizedOval(0.58d, 0.39d, 0.055d, 0.055d * 0.10d),
        InnerLipContour = Normalize(closedMouthEstimate.Contour, 180, 90)
    });
    Require(oneEyeArtifact.PossibleOneEyeArtifact, "one-eye glare artifact was not flagged");
    Require(oneEyeArtifact.RawEyeAsymmetryPercent is > 55d, $"one-eye glare artifact asymmetry was too low: {oneEyeArtifact.RawEyeAsymmetryPercent}");
    Require(oneEyeArtifact.EyeMeasurementQualityPercent < 60d, $"one-eye glare artifact did not reduce eye quality: {oneEyeArtifact.EyeMeasurementQualityPercent}");

    var directDenseAsymmetry = new FaceLandmarkMetricCalculator().Update(new FaceLandmarkFrame
    {
        HasFace = true,
        CapturedAtUtc = DateTime.UtcNow,
        Source = "MediaPipe Face Landmarker sidecar clean dense one-eye closure",
        TrackingConfidence = 0.92d,
        EyeConfidence = 0.90d,
        MouthConfidence = 0.70d,
        EyeImageQualityAvailable = true,
        EyeGlarePercent = 0d,
        EyeContrastPercent = 82d,
        EyeSharpnessPercent = 76d,
        LeftEyeContour = CreateNormalizedOval(0.42d, 0.39d, 0.055d, 0.055d * 0.52d),
        RightEyeContour = CreateNormalizedOval(0.58d, 0.39d, 0.055d, 0.055d * 0.08d),
        InnerLipContour = Normalize(closedMouthEstimate.Contour, 180, 90),
        BlendshapeScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["eyeBlinkLeft"] = 0.22d,
            ["eyeBlinkRight"] = 0.78d
        }
    });
    Require(
        directDenseAsymmetry.RawEyeAsymmetryPercent is > 85d,
        $"clean dense one-eye closure did not create strong asymmetry evidence: {directDenseAsymmetry.RawEyeAsymmetryPercent}");
    Require(
        !directDenseAsymmetry.PossibleOneEyeArtifact,
        "clean high-confidence dense one-eye closure was incorrectly labeled as an artifact");

    var suppressedSideAngleArtifact = new FaceLandmarkMetricCalculator().Update(new FaceLandmarkFrame
    {
        HasFace = true,
        CapturedAtUtc = DateTime.UtcNow,
        Source = "smoke side angle suppressed eye reconstruction",
        TrackingConfidence = 0.50d,
        EyeConfidence = 0.34d,
        MouthConfidence = 0.70d,
        EyeImageQualityAvailable = true,
        EyeGlarePercent = 0d,
        EyeContrastPercent = 92d,
        EyeSharpnessPercent = 1d,
        LeftEyeReconstructed = true,
        RightEyeReconstructed = true,
        EyeArtifactSuppressed = true,
        LeftEyeContour = CreateNormalizedOval(0.42d, 0.39d, 0.055d, 0.055d * 0.52d),
        RightEyeContour = CreateNormalizedOval(0.58d, 0.39d, 0.055d, 0.055d * 0.18d),
        InnerLipContour = Normalize(closedMouthEstimate.Contour, 180, 90)
    });
    Require(
        suppressedSideAngleArtifact.PossibleOneEyeArtifact,
        "suppressed side-angle eye reconstruction was not exposed as a possible one-eye artifact");
    Require(
        suppressedSideAngleArtifact.Status.Contains("possible one-eye artifact", StringComparison.OrdinalIgnoreCase),
        $"suppressed side-angle eye artifact was not visible in status text: {suppressedSideAngleArtifact.Status}");

    var openFacemarkFrame = OpenCvFacemarkLandmarkTracker.CreateLandmarkFrameFrom68Points(
        CreateSyntheticFacemark68Points(eyeRatio: 0.30d, mouthRatio: 0.08d),
        DateTime.UtcNow,
        "synthetic LBF open");
    var sleepyFacemarkFrame = OpenCvFacemarkLandmarkTracker.CreateLandmarkFrameFrom68Points(
        CreateSyntheticFacemark68Points(eyeRatio: 0.10d, mouthRatio: 0.24d),
        DateTime.UtcNow.AddSeconds(1),
        "synthetic LBF sleepy");
    Require(openFacemarkFrame.HasFace, "synthetic LBF frame did not report a face");
    Require(openFacemarkFrame.LeftEyeContour.Count == 6 && openFacemarkFrame.RightEyeContour.Count == 6, "synthetic LBF eye contours were not mapped from 68 points");
    Require(openFacemarkFrame.OuterLipContour.Count == 12 && openFacemarkFrame.InnerLipContour.Count == 8, "synthetic LBF lip contours were not mapped from 68 points");
    Require(openFacemarkFrame.JawContour.Count == 17, "synthetic LBF jaw contour was not mapped from 68 points");

    var facemarkMetricCalculator = new FaceLandmarkMetricCalculator();
    var openFacemarkMetrics = facemarkMetricCalculator.Update(openFacemarkFrame);
    var sleepyFacemarkMetrics = facemarkMetricCalculator.Update(sleepyFacemarkFrame);
    Require(
        openFacemarkMetrics.RawAverageEyeOpeningRatio is double openFacemarkEye
        && sleepyFacemarkMetrics.RawAverageEyeOpeningRatio is double sleepyFacemarkEye
        && sleepyFacemarkEye < openFacemarkEye * 0.55d,
        $"synthetic LBF eye opening did not shrink enough: open={openFacemarkMetrics.RawAverageEyeOpeningRatio}, sleepy={sleepyFacemarkMetrics.RawAverageEyeOpeningRatio}");
    Require(
        openFacemarkMetrics.RawMouthOpeningRatio is double openFacemarkMouth
        && sleepyFacemarkMetrics.RawMouthOpeningRatio is double sleepyFacemarkMouth
        && sleepyFacemarkMouth > openFacemarkMouth * 1.75d,
        $"synthetic LBF mouth opening did not expand enough: open={openFacemarkMetrics.RawMouthOpeningRatio}, sleepy={sleepyFacemarkMetrics.RawMouthOpeningRatio}");

    var jawBaselineFrame = OpenCvFacemarkLandmarkTracker.CreateLandmarkFrameFrom68Points(
        CreateSyntheticFacemark68Points(eyeRatio: 0.22d, mouthRatio: 0.10d, jawDroopRatio: 0d),
        DateTime.UtcNow,
        "synthetic LBF jaw baseline");
    var jawDroopFrame = OpenCvFacemarkLandmarkTracker.CreateLandmarkFrameFrom68Points(
        CreateSyntheticFacemark68Points(eyeRatio: 0.22d, mouthRatio: 0.10d, jawDroopRatio: 0.10d),
        DateTime.UtcNow.AddSeconds(1),
        "synthetic LBF jaw droop");
    var jawMetricCalculator = new FaceLandmarkMetricCalculator();
    var jawBaselineMetrics = jawMetricCalculator.Update(jawBaselineFrame);
    var jawDroopMetrics = jawMetricCalculator.Update(jawDroopFrame);
    Require(
        jawBaselineMetrics.RawJawDroopRatio is double jawBaseline
        && jawDroopMetrics.RawJawDroopRatio is double jawDroop
        && jawDroop > jawBaseline * 1.18d,
        $"synthetic LBF jaw contour droop did not increase enough: baseline={jawBaselineMetrics.RawJawDroopRatio}, droop={jawDroopMetrics.RawJawDroopRatio}");
    Require(
        jawDroopMetrics.JawDroopVelocityPerSecond is > 0d,
        $"synthetic LBF jaw contour droop velocity did not increase: {jawDroopMetrics.JawDroopVelocityPerSecond}");

    var levelFacemarkFrame = OpenCvFacemarkLandmarkTracker.CreateLandmarkFrameFrom68Points(
        CreateSyntheticFacemark68Points(eyeRatio: 0.22d, mouthRatio: 0.16d, rollDegrees: 0d),
        DateTime.UtcNow,
        "synthetic LBF level");
    var rolledFacemarkFrame = OpenCvFacemarkLandmarkTracker.CreateLandmarkFrameFrom68Points(
        CreateSyntheticFacemark68Points(eyeRatio: 0.22d, mouthRatio: 0.16d, rollDegrees: 24d),
        DateTime.UtcNow,
        "synthetic LBF rolled");
    var levelMetrics = new FaceLandmarkMetricCalculator().Update(levelFacemarkFrame);
    var rolledMetrics = new FaceLandmarkMetricCalculator().Update(rolledFacemarkFrame);
    Require(
        levelMetrics.RawAverageEyeOpeningRatio is double levelEye
        && rolledMetrics.RawAverageEyeOpeningRatio is double rolledEye
        && Math.Abs(levelEye - rolledEye) < 0.018d,
        $"C-axis-aware eye aperture measurement drifted under C-axis head tilt: level={levelMetrics.RawAverageEyeOpeningRatio}, tilted={rolledMetrics.RawAverageEyeOpeningRatio}");
    Require(
        levelMetrics.RawMouthOpeningRatio is double levelMouth
        && rolledMetrics.RawMouthOpeningRatio is double rolledMouth
        && Math.Abs(levelMouth - rolledMouth) < 0.025d,
        $"C-axis-aware mouth aperture measurement drifted under C-axis head tilt: level={levelMetrics.RawMouthOpeningRatio}, tilted={rolledMetrics.RawMouthOpeningRatio}");
    Require(
        levelMetrics.RawJawDroopRatio is double levelJawDroop
        && rolledMetrics.RawJawDroopRatio is double rolledJawDroop
        && Math.Abs(levelJawDroop - rolledJawDroop) < 0.035d,
        $"C-axis-aware jaw droop measurement drifted under C-axis head tilt: level={levelMetrics.RawJawDroopRatio}, tilted={rolledMetrics.RawJawDroopRatio}");

    var cleanEyeContour = levelFacemarkFrame.LeftEyeContour;
    var noisyEyeContour = cleanEyeContour.ToList();
    noisyEyeContour[2] = new WpfPoint(noisyEyeContour[2].X, noisyEyeContour[2].Y - 0.055d);
    var cleanPairedEye = ContourOpeningEstimator.CalculateOpeningRatio(cleanEyeContour, preferPairedAverage: true);
    var noisyPairedEye = ContourOpeningEstimator.CalculateOpeningRatio(noisyEyeContour, preferPairedAverage: true);
    var noisyMaxEye = ContourOpeningEstimator.CalculateOpeningRatio(noisyEyeContour, preferPairedAverage: false);
    Require(
        cleanPairedEye is double cleanPair
        && noisyPairedEye is double noisyPair
        && noisyMaxEye is double noisyMax
        && Math.Abs(noisyPair - cleanPair) < Math.Abs(noisyMax - cleanPair),
        $"paired-average eyelid aperture was not more stable than max-height aperture: clean={cleanPairedEye}, paired={noisyPairedEye}, max={noisyMaxEye}");
    var noisyMediaPipeEyeMetrics = new FaceLandmarkMetricCalculator().Update(new FaceLandmarkFrame
    {
        HasFace = true,
        CapturedAtUtc = DateTime.UtcNow,
        Source = "MediaPipe Face Landmarker sidecar synthetic dense mesh",
        TrackingConfidence = 0.92d,
        EyeConfidence = 0.90d,
        MouthConfidence = 0.70d,
        LeftEyeContour = noisyEyeContour,
        RightEyeContour = noisyEyeContour,
        InnerLipContour = Normalize(closedMouthEstimate.Contour, 180, 90)
    });
    Require(
        noisyMediaPipeEyeMetrics.RawAverageEyeOpeningRatio is double mediaPipePair
        && cleanPairedEye is double cleanMediaPipePair
        && noisyMaxEye is double mediaPipeNoisyMax
        && Math.Abs(mediaPipePair - cleanMediaPipePair) < Math.Abs(mediaPipeNoisyMax - cleanMediaPipePair),
        $"MediaPipe dense contour did not use paired-average eyelid aperture: clean={cleanPairedEye}, mediaPipe={noisyMediaPipeEyeMetrics.RawAverageEyeOpeningRatio}, max={noisyMaxEye}");

    RunMovingFaceTrendSmoke();
    RunCompositeFusionSmoke();

    var cueAnalyzer = new FaceLandmarkCueAnalyzer();
    for (var i = 0; i < 20; i++)
    {
        cueAnalyzer.Analyze(CreateMetrics(first.AverageEyeOpeningRatio, first.MouthOpeningRatio));
    }

    var cue = cueAnalyzer.Analyze(CreateMetrics(second.AverageEyeOpeningRatio, second.MouthOpeningRatio, second.MouthOpeningVelocityPerSecond));
    Require(cue.BaselineReady, "landmark cue baseline was not ready after 20 samples");
    Require(cue.EyeClosurePercent is > 20d, $"landmark eye closure cue was too weak: {cue.EyeClosurePercent}");
    Require(cue.MouthOpeningChangePercent is > 20d, $"landmark mouth opening cue was too weak: {cue.MouthOpeningChangePercent}");
    Require(cue.CompositeCuePercent > 20d, $"landmark composite cue was too weak: {cue.CompositeCuePercent}");
    Require(cue.EyeCueEligible, "landmark eye cue was not marked eligible after high-quality baseline");
    Require(cue.EyeQualityPercent >= 60d, $"landmark eye cue quality was too low: {cue.EyeQualityPercent}");

    var jawCueAnalyzer = new FaceLandmarkCueAnalyzer();
    for (var i = 0; i < 20; i++)
    {
        jawCueAnalyzer.Analyze(CreateMetrics(
            0.30d,
            0.08d,
            jawDroop: 0.78d));
    }

    var jawCueMetric = CreateMetrics(
        0.28d,
        0.08d,
        jawDroop: 1.02d,
        jawDroopVelocity: 0.10d);
    var jawCue = jawCueAnalyzer.Analyze(jawCueMetric);
    Require(jawCue.MouthBaselineReady, "landmark jaw droop cue baseline was not ready");
    Require(jawCue.JawDroopChangePercent is > 25d, $"landmark jaw droop cue was too weak: {jawCue.JawDroopChangePercent}");
    Require(jawCue.CompositeCuePercent > 10d, $"landmark jaw droop supporting cue was too weak: {jawCue.CompositeCuePercent}");
    Require(jawCue.MouthCueEligible, "landmark jaw droop cue was not marked lower-face eligible");

    var mediaPipeCueAnalyzer = new FaceLandmarkCueAnalyzer();
    for (var i = 0; i < 20; i++)
    {
        mediaPipeCueAnalyzer.Analyze(CreateMetrics(
            0.30d,
            0.08d,
            mediaPipeBlink: 8d,
            mediaPipeJawOpen: 5d,
            mediaPipeMouthClose: 92d));
    }

    var mediaPipeCue = mediaPipeCueAnalyzer.Analyze(CreateMetrics(
        0.16d,
        0.19d,
        mouthVelocity: 0.08d,
        mediaPipeBlink: 64d,
        mediaPipeJawOpen: 48d,
        mediaPipeMouthClose: 28d));
    Require(mediaPipeCue.MediaPipeBlinkBaselineReady, "MediaPipe blink baseline did not become ready");
    Require(mediaPipeCue.MediaPipeMouthBaselineReady, "MediaPipe mouth baseline did not become ready");
    Require(mediaPipeCue.MediaPipeBlinkChangePercent is > 50d, $"MediaPipe blink change was too weak: {mediaPipeCue.MediaPipeBlinkChangePercent}");
    Require(mediaPipeCue.MediaPipeJawOpenChangePercent is > 40d, $"MediaPipe jaw-open change was too weak: {mediaPipeCue.MediaPipeJawOpenChangePercent}");
    Require(mediaPipeCue.MediaPipeMouthCloseDropPercent is > 60d, $"MediaPipe mouth-close drop was too weak: {mediaPipeCue.MediaPipeMouthCloseDropPercent}");
    Require(mediaPipeCue.MediaPipeMouthOpeningEvidencePercent is > 60d, $"MediaPipe mouth opening evidence was too weak: {mediaPipeCue.MediaPipeMouthOpeningEvidencePercent}");
    Require(
        mediaPipeCue.Status.Contains("mp blink +", StringComparison.OrdinalIgnoreCase)
        && mediaPipeCue.Status.Contains("mp mouth +", StringComparison.OrdinalIgnoreCase),
        $"landmark cue status did not include baseline-relative MediaPipe evidence: {mediaPipeCue.Status}");

    var mediaPipeOnlyEyeCueAnalyzer = new FaceLandmarkCueAnalyzer();
    for (var i = 0; i < 20; i++)
    {
        mediaPipeOnlyEyeCueAnalyzer.Analyze(CreateMetrics(
            null,
            null,
            trackingConfidence: 0.78d,
            mediaPipeBlink: 7d,
            source: "MediaPipe Face Landmarker sidecar blendshape baseline"));
    }

    var mediaPipeOnlyEyeCue = mediaPipeOnlyEyeCueAnalyzer.Analyze(CreateMetrics(
        null,
        null,
        trackingConfidence: 0.78d,
        mediaPipeBlink: 78d,
        source: "MediaPipe Face Landmarker sidecar glasses contour dropout"));
    Require(mediaPipeOnlyEyeCue.BaselineReady, "MediaPipe-only blink cue baseline was not ready after contour dropout");
    Require(mediaPipeOnlyEyeCue.EyeCueEligible, "MediaPipe-only blink cue did not stay eye-eligible after contour dropout");
    Require(mediaPipeOnlyEyeCue.EyeClosurePercent is null, $"MediaPipe-only blink cue should not pretend to be direct contour closure: {mediaPipeOnlyEyeCue.EyeClosurePercent}");
    Require(mediaPipeOnlyEyeCue.MediaPipeBlinkChangePercent is > 65d, $"MediaPipe-only blink change was too weak: {mediaPipeOnlyEyeCue.MediaPipeBlinkChangePercent}");
    Require(mediaPipeOnlyEyeCue.CompositeCuePercent > 45d, $"MediaPipe-only blink cue score was too weak: {mediaPipeOnlyEyeCue.CompositeCuePercent}");
    Require(
        mediaPipeOnlyEyeCue.Status.Contains("eye closure mp +", StringComparison.OrdinalIgnoreCase),
        $"MediaPipe-only blink status did not identify blendshape-backed eye evidence: {mediaPipeOnlyEyeCue.Status}");

    var mediaPipeOnlyMouthCueAnalyzer = new FaceLandmarkCueAnalyzer();
    for (var i = 0; i < 20; i++)
    {
        mediaPipeOnlyMouthCueAnalyzer.Analyze(CreateMetrics(
            null,
            null,
            trackingConfidence: 0.78d,
            mediaPipeJawOpen: 5d,
            mediaPipeMouthClose: 92d,
            source: "MediaPipe Face Landmarker sidecar lower-face baseline"));
    }

    var mediaPipeOnlyMouthCue = mediaPipeOnlyMouthCueAnalyzer.Analyze(CreateMetrics(
        null,
        null,
        trackingConfidence: 0.78d,
        mediaPipeJawOpen: 74d,
        mediaPipeMouthClose: 20d,
        source: "MediaPipe Face Landmarker sidecar lip contour dropout"));
    Require(mediaPipeOnlyMouthCue.BaselineReady, "MediaPipe-only mouth cue baseline was not ready after lip contour dropout");
    Require(mediaPipeOnlyMouthCue.MouthCueEligible, "MediaPipe-only mouth cue did not stay lower-face eligible after lip contour dropout");
    Require(!mediaPipeOnlyMouthCue.EyeCueEligible, "MediaPipe-only mouth cue should not imply eye cue eligibility");
    Require(mediaPipeOnlyMouthCue.MouthOpeningChangePercent is null, $"MediaPipe-only mouth cue should not pretend to be direct lip opening: {mediaPipeOnlyMouthCue.MouthOpeningChangePercent}");
    Require(mediaPipeOnlyMouthCue.MediaPipeMouthOpeningEvidencePercent is > 65d, $"MediaPipe-only mouth evidence was too weak: {mediaPipeOnlyMouthCue.MediaPipeMouthOpeningEvidencePercent}");
    Require(mediaPipeOnlyMouthCue.CompositeCuePercent is > 8d and < 25d, $"MediaPipe-only mouth cue should be supportive, not dominant: {mediaPipeOnlyMouthCue.CompositeCuePercent}");
    Require(
        mediaPipeOnlyMouthCue.Status.Contains("mouth opening mp +", StringComparison.OrdinalIgnoreCase),
        $"MediaPipe-only mouth status did not identify blendshape-backed lower-face evidence: {mediaPipeOnlyMouthCue.Status}");

    var temporalBaselineAnalyzer = new FaceLandmarkCueAnalyzer();
    FaceLandmarkCueAnalysis temporalBaseline = FaceLandmarkCueAnalysis.Waiting;
    for (var i = 0; i < 20; i++)
    {
        temporalBaseline = temporalBaselineAnalyzer.Analyze(CreateMetrics(
            0.30d,
            0.08d,
            source: "synthetic landmarks; temporal hold"));
    }

    Require(!temporalBaseline.BaselineReady, "temporal hold frames should not establish an awake landmark baseline");
    Require(temporalBaseline.BaselineSamples == 0, $"temporal hold baseline sample count should stay at 0: {temporalBaseline.BaselineSamples}");

    var qualityGateAnalyzer = new FaceLandmarkCueAnalyzer();
    for (var i = 0; i < 20; i++)
    {
        qualityGateAnalyzer.Analyze(CreateMetrics(0.30d, 0.08d));
    }

    var lowQualityCue = qualityGateAnalyzer.Analyze(CreateMetrics(
        0.04d,
        0.30d,
        mouthVelocity: 0.10d,
        eyeConfidence: 0.20d,
        mouthConfidence: 0.20d,
        eyeQuality: 20d,
        mouthQuality: 20d));
    Require(!lowQualityCue.HasUsableMeasurements, "low-quality landmark measurements should be rejected before cue scoring");
    Require(lowQualityCue.CompositeCuePercent == 0d, $"low-quality landmark cue should not score: {lowQualityCue.CompositeCuePercent}");

    var trendAnalyzer = new FaceLandmarkTrendAnalyzer();
    FaceLandmarkTrendAnalysis trend = FaceLandmarkTrendAnalysis.Waiting;
    var trendStart = DateTime.UtcNow;
    for (var i = 0; i < 12; i++)
    {
        var progress = i / 11d;
        trend = trendAnalyzer.Update(CreateMetrics(
            eyeOpening: 0.34d - progress * 0.18d,
            mouthOpening: 0.08d + progress * 0.18d,
            eyeQuality: 76d,
            mouthQuality: 74d,
            capturedAtUtc: trendStart.AddSeconds(i)));
    }

    Require(trend.HasUsableTrend, "landmark trend analyzer did not produce a usable trend");
    Require(trend.EyeClosingTrendPercent is > 35d, $"landmark eye closing trend was too weak: {trend.EyeClosingTrendPercent}");
    Require(trend.MouthOpeningTrendPercent is > 80d, $"landmark mouth opening trend was too weak: {trend.MouthOpeningTrendPercent}");
    Require(trend.EyeOpeningSlopePerSecond is < 0d, $"landmark eye slope was not negative: {trend.EyeOpeningSlopePerSecond}");
    Require(trend.MouthOpeningSlopePerSecond is > 0d, $"landmark mouth slope was not positive: {trend.MouthOpeningSlopePerSecond}");
    Require(trend.TrendCuePercent > 35d, $"landmark trend cue score was too weak: {trend.TrendCuePercent}");

    var noisyMouthTrendAnalyzer = new FaceLandmarkTrendAnalyzer();
    FaceLandmarkTrendAnalysis noisyMouthTrend = FaceLandmarkTrendAnalysis.Waiting;
    var noisyMouthValues = new[] { 0.08d, 0.24d, 0.12d, 0.14d, 0.15d, 0.17d, 0.18d, 0.20d, 0.21d, 0.22d, 0.24d, 0.25d };
    for (var i = 0; i < noisyMouthValues.Length; i++)
    {
        noisyMouthTrend = noisyMouthTrendAnalyzer.Update(CreateMetrics(
            eyeOpening: 0.28d - i * 0.002d,
            mouthOpening: noisyMouthValues[i],
            eyeQuality: 76d,
            mouthQuality: 74d,
            capturedAtUtc: trendStart.AddSeconds(i)));
    }

    Require(noisyMouthTrend.MouthOpeningSlopePerSecond is > 0.006d, $"robust landmark mouth slope did not survive a noisy early spike: {noisyMouthTrend.MouthOpeningSlopePerSecond}");
    Require(noisyMouthTrend.MouthOpeningTrendPercent is > 40d, $"robust landmark mouth trend did not keep endpoint rise: {noisyMouthTrend.MouthOpeningTrendPercent}");

    var lowQualityTrendAnalyzer = new FaceLandmarkTrendAnalyzer();
    FaceLandmarkTrendAnalysis lowQualityTrend = FaceLandmarkTrendAnalysis.Waiting;
    for (var i = 0; i < 12; i++)
    {
        var progress = i / 11d;
        lowQualityTrend = lowQualityTrendAnalyzer.Update(CreateMetrics(
            eyeOpening: 0.34d - progress * 0.18d,
            mouthOpening: 0.08d + progress * 0.18d,
            eyeConfidence: 0.22d,
            mouthConfidence: 0.22d,
            eyeQuality: 22d,
            mouthQuality: 22d,
            capturedAtUtc: trendStart.AddSeconds(i)));
    }

    Require(!lowQualityTrend.HasUsableTrend, "low-quality landmark trend should not become usable");
    Require(lowQualityTrend.TrendCuePercent == 0d, $"low-quality landmark trend should not score: {lowQualityTrend.TrendCuePercent}");

    var strongFaceLock = new FaceLockStabilityAnalysis
    {
        SampleCount = 6,
        WindowSeconds = 4.5d,
        FaceBoundsRatePercent = 100d,
        FaceContinuityPercent = 92d,
        EyeUsableRatePercent = 100d,
        MouthUsableRatePercent = 100d,
        AverageEyeQualityPercent = 84d,
        AverageMouthQualityPercent = 82d,
        AverageOverallQualityPercent = 83d,
        EyeReliabilityPercent = 91d,
        MouthReliabilityPercent = 88d,
        CompositeReliabilityPercent = 90d
    };
    var limitedFaceLock = new FaceLockStabilityAnalysis
    {
        SampleCount = 6,
        WindowSeconds = 4.5d,
        FaceBoundsRatePercent = 82d,
        FaceContinuityPercent = 64d,
        EyeUsableRatePercent = 62d,
        MouthUsableRatePercent = 68d,
        AverageEyeQualityPercent = 58d,
        AverageMouthQualityPercent = 61d,
        AverageOverallQualityPercent = 59d,
        EyeReliabilityPercent = 61d,
        MouthReliabilityPercent = 63d,
        CompositeReliabilityPercent = 52d
    };
    var weakCaptureQuality = new PersonalFaceCaptureQualityAssessment
    {
        Label = "limited",
        ScorePercent = 48d,
        PrimaryReason = "synthetic weak evidence",
        CanCollectMeasurements = false,
        StrongEnoughForAvatarLearning = false,
        FaceScaleScorePercent = 42d,
        EyeEvidenceScorePercent = 44d,
        MouthEvidenceScorePercent = 52d,
        Issues = ["small face"]
    };
    var usableCaptureQuality = new PersonalFaceCaptureQualityAssessment
    {
        Label = "usable",
        ScorePercent = 74d,
        PrimaryReason = "synthetic usable evidence",
        CanCollectMeasurements = true,
        StrongEnoughForAvatarLearning = false,
        FaceScaleScorePercent = 76d,
        EyeEvidenceScorePercent = 72d,
        MouthEvidenceScorePercent = 78d,
        Issues = ["minor glare"]
    };
    var avatarCaptureQuality = new PersonalFaceCaptureQualityAssessment
    {
        Label = "avatar-grade",
        ScorePercent = 91d,
        PrimaryReason = "synthetic strong evidence",
        CanCollectMeasurements = true,
        StrongEnoughForAvatarLearning = true,
        CameraModeScorePercent = 100d,
        FaceScaleScorePercent = 92d,
        EyeEvidenceScorePercent = 90d,
        MouthEvidenceScorePercent = 88d,
        StabilityScorePercent = 93d,
        GlassesRiskScorePercent = 86d,
        StorageScorePercent = 100d,
        FaceWidthPercent = 42d,
        FaceHeightPercent = 58d,
        Suggestions = ["hold this framing"]
    };

    var aggregate = new LandmarkEventAggregate();
    aggregate.Update(first, null, null, null, "dense missing; fallback active", weakCaptureQuality);
    aggregate.Update(second, mediaPipeCue, trend, strongFaceLock, "fallback aperture lock", avatarCaptureQuality);
    aggregate.Update(jawCueMetric, jawCue, trend, limitedFaceLock, "jaw contour cue", usableCaptureQuality);
    Require(aggregate.SampleCount == 3, $"landmark aggregate sample count was wrong: {aggregate.SampleCount}");
    var expectedMinimumEyeOpening = new[] { first.AverageEyeOpeningRatio, second.AverageEyeOpeningRatio, jawCueMetric.AverageEyeOpeningRatio }
        .Where(static value => value.HasValue)
        .Min();
    Require(
        Math.Abs(aggregate.MinimumEyeOpeningRatio.GetValueOrDefault() - expectedMinimumEyeOpening.GetValueOrDefault()) < 0.000001d,
        "landmark aggregate did not keep the minimum eye opening");
    Require(aggregate.MaximumEyeClosurePercent is > 20d, "landmark aggregate did not keep maximum eye closure");
    Require(aggregate.MaximumMouthOpeningChangePercent is > 20d, "landmark aggregate did not keep maximum mouth opening change");
    Require(aggregate.MaximumJawDroopRatio is > 1.0d, "landmark aggregate did not keep maximum jaw droop ratio");
    Require(aggregate.MaximumJawDroopChangePercent is > 25d, "landmark aggregate did not keep maximum jaw droop change");
    Require(aggregate.MaximumJawDroopVelocityPerSecond is > 0d, "landmark aggregate did not keep maximum jaw droop velocity");
    Require(aggregate.MaximumMediaPipeAverageEyeBlinkPercent is > 60d, "landmark aggregate did not keep MediaPipe blink evidence");
    Require(aggregate.MaximumMediaPipeJawOpenPercent is > 45d, "landmark aggregate did not keep MediaPipe jaw-open evidence");
    Require(aggregate.MaximumMediaPipeBlinkChangePercent is > 50d, "landmark aggregate did not keep MediaPipe blink change evidence");
    Require(aggregate.MaximumMediaPipeMouthOpeningEvidencePercent is > 60d, "landmark aggregate did not keep MediaPipe mouth change evidence");
    Require(aggregate.BackendStatuses.Count == 3, "landmark aggregate did not keep backend statuses");
    Require(aggregate.MinimumEyeQualityPercent is >= 60d, $"landmark aggregate did not keep eye quality: {aggregate.MinimumEyeQualityPercent}");
    Require(aggregate.AverageOverallQualityPercent > 60d, $"landmark aggregate average quality was too low: {aggregate.AverageOverallQualityPercent}");
    Require(aggregate.MaximumEyeClosingTrendPercent is > 35d, "landmark aggregate did not keep maximum eye trend");
    Require(aggregate.MaximumLandmarkTrendScore is > 35d, "landmark aggregate did not keep maximum trend score");
    Require(aggregate.FaceReliabilitySamples == 2, $"landmark aggregate reliability sample count was wrong: {aggregate.FaceReliabilitySamples}");
    Require(aggregate.FaceReliabilityUsableSamples == 1, $"landmark aggregate usable reliability count was wrong: {aggregate.FaceReliabilityUsableSamples}");
    Require(aggregate.MinimumFaceReliabilityPercent is < 55d, "landmark aggregate did not retain limited face reliability");
    Require(aggregate.AverageFaceReliabilityPercent > 70d, "landmark aggregate did not average face reliability");
    Require(aggregate.MinimumFaceContinuityPercent is < 70d, "landmark aggregate did not retain minimum face continuity");
    Require(aggregate.AverageEyeReliabilityPercent > 70d, "landmark aggregate did not average eye reliability");
    Require(aggregate.AverageMouthReliabilityPercent > 70d, "landmark aggregate did not average mouth reliability");
    Require(aggregate.CaptureQualitySamples == 3, $"landmark aggregate capture quality sample count was wrong: {aggregate.CaptureQualitySamples}");
    Require(aggregate.CaptureQualityCanCollectSamples == 2, $"landmark aggregate can-collect count was wrong: {aggregate.CaptureQualityCanCollectSamples}");
    Require(aggregate.CaptureQualityAvatarGradeSamples == 1, $"landmark aggregate avatar-grade count was wrong: {aggregate.CaptureQualityAvatarGradeSamples}");
    Require(aggregate.MinimumCaptureQualityScore is < 50d, "landmark aggregate did not retain minimum capture quality");
    Require(aggregate.MaximumCaptureQualityScore is > 90d, "landmark aggregate did not retain maximum capture quality");
    Require(aggregate.AverageCaptureQualityScore > 70d, "landmark aggregate did not average capture quality");
    Require(aggregate.CaptureQualityIssues.Any(issue => issue.Contains("small face", StringComparison.OrdinalIgnoreCase)), "landmark aggregate did not retain capture quality issues");

    var correctionAggregate = new LandmarkEventAggregate();
    correctionAggregate.Update(glassesFalseOpenBlink, null, null, "glasses false-open correction");
    correctionAggregate.Update(mouthFalseClosedOpen, null, null, "mouth false-closed correction");
    correctionAggregate.Update(mouthFalseOpenClosed, null, null, "mouth false-open correction");
    Require(correctionAggregate.MediaPipeEyeOpeningCorrectedSamples == 1, "landmark aggregate did not count MediaPipe eye-opening corrections");
    Require(correctionAggregate.MediaPipeMouthOpeningCorrectedSamples == 2, "landmark aggregate did not count MediaPipe mouth-opening corrections");
    Require(correctionAggregate.MaximumAbsoluteMediaPipeEyeOpeningCorrection is > 0d, "landmark aggregate did not retain maximum absolute eye correction");
    Require(correctionAggregate.MaximumAbsoluteMediaPipeMouthOpeningCorrection is > 0d, "landmark aggregate did not retain maximum absolute mouth correction");

    var correctionTimeline = new LandmarkEventTimeline();
    correctionTimeline.Add(DateTime.UtcNow, 0.2d, glassesFalseOpenBlink, FaceLandmarkCueAnalysis.Waiting, FaceLandmarkTrendAnalysis.Waiting, "glasses false-open correction");
    correctionTimeline.Add(DateTime.UtcNow, 0.2d, mouthFalseClosedOpen, FaceLandmarkCueAnalysis.Waiting, FaceLandmarkTrendAnalysis.Waiting, "mouth false-closed correction");
    Require(
        correctionTimeline.Samples[0].MediaPipeEyeOpeningCorrected
        && correctionTimeline.Samples[0].MediaPipeEyeOpeningCorrection is < 0d
        && correctionTimeline.Samples[1].MediaPipeMouthOpeningCorrected
        && correctionTimeline.Samples[1].MediaPipeMouthOpeningCorrection is > 0d,
        "landmark timeline did not retain MediaPipe correction evidence");

    var timeline = new LandmarkEventTimeline();
    var timelineStart = DateTime.UtcNow;
    timeline.Add(timelineStart, 1.2d, reconstructedMetric, cue, trend, FaceLockStabilityAnalysis.Waiting, "synthetic backend status", weakCaptureQuality);
    timeline.Add(timelineStart, 0.4d, second, mediaPipeCue, trend, strongFaceLock, "fallback aperture lock", avatarCaptureQuality);
    timeline.Add(timelineStart, 0.3d, jawCueMetric, jawCue, trend, limitedFaceLock, "jaw contour cue", usableCaptureQuality);
    Require(timeline.Count == 3, $"landmark timeline sample count was wrong: {timeline.Count}");
    Require(timeline.Samples[0].LeftEyeReconstructed && timeline.Samples[0].EyeArtifactSuppressed, "landmark timeline did not retain reconstruction evidence");
    Require(
        timeline.Samples[1].MotionPercent is double timelineMotion && Math.Abs(timelineMotion - 0.4d) < 0.000001d,
        "landmark timeline did not retain motion evidence");
    Require(
        timeline.Samples[1].MediaPipeAverageEyeBlinkPercent is > 60d
        && timeline.Samples[1].MediaPipeJawOpenPercent is > 45d,
        "landmark timeline did not retain MediaPipe blendshape evidence");
    Require(
        timeline.Samples[1].FaceReliability > 80d
        && timeline.Samples[1].FaceReliabilityStatus.Contains("strong", StringComparison.OrdinalIgnoreCase)
        && timeline.Samples[2].FaceReliability < 55d,
        "landmark timeline did not retain temporal face reliability evidence");
    Require(
        timeline.Samples[1].CueMediaPipeBlinkChangePercent is > 50d
        && timeline.Samples[1].CueMediaPipeMouthOpeningEvidencePercent is > 60d,
        "landmark timeline did not retain baseline-relative MediaPipe cue evidence");
    Require(
        timeline.Samples[2].JawDroop is > 1.0d
        && timeline.Samples[2].JawDroopChangePercent is > 25d,
        "landmark timeline did not retain jaw contour cue evidence");
    Require(
        timeline.Samples[1].CaptureQualityLabel.Equals("avatar-grade", StringComparison.OrdinalIgnoreCase)
        && timeline.Samples[1].CaptureQualityAvatarGrade == true
        && timeline.Samples[1].CaptureQualityScore is > 90d,
        "landmark timeline did not retain avatar-grade capture quality evidence");
    Require(
        timeline.Samples[0].CaptureQualityCanCollect == false
        && timeline.Samples[0].CaptureQualityIssues.Contains("small face", StringComparison.OrdinalIgnoreCase),
        "landmark timeline did not retain limited capture quality evidence");
    var timelineFolder = Path.Combine(Path.GetTempPath(), $"AvatarBuilderVisionSmoke-{Guid.NewGuid():N}");
    var timelineFiles = timeline.Write(timelineFolder);
    Require(File.Exists(timelineFiles.JsonPath), "landmark timeline JSON file was not written");
    Require(File.Exists(timelineFiles.CsvPath), "landmark timeline CSV file was not written");
    var timelineCsv = File.ReadAllText(timelineFiles.CsvPath);
    Require(
        timelineCsv.Contains("EyeArtifactSuppressed", StringComparison.Ordinal)
        && timelineCsv.Contains("FaceReliabilityStatus", StringComparison.Ordinal)
        && timelineCsv.Contains("FaceContinuity", StringComparison.Ordinal)
        && timelineCsv.Contains("MediaPipeAverageEyeBlink", StringComparison.Ordinal)
        && timelineCsv.Contains("MediaPipeEyeOpeningCorrection", StringComparison.Ordinal)
        && timelineCsv.Contains("MediaPipeMouthOpeningCorrected", StringComparison.Ordinal)
        && timelineCsv.Contains("CaptureQualityScore", StringComparison.Ordinal)
        && timelineCsv.Contains("avatar-grade", StringComparison.Ordinal)
        && timelineCsv.Contains("CueMediaPipeBlinkChange", StringComparison.Ordinal)
        && timelineCsv.Contains("JawDroopChange", StringComparison.Ordinal)
        && timelineCsv.Contains("synthetic backend status", StringComparison.Ordinal),
        "landmark timeline CSV did not include evidence headers and backend status");
    Directory.Delete(timelineFolder, recursive: true);

    var reconstructionAggregate = new LandmarkEventAggregate();
    reconstructionAggregate.Update(reconstructedMetric, null, null, "temporal reconstruction");
    Require(reconstructionAggregate.LeftEyeReconstructedSamples == 1, "landmark aggregate did not count reconstructed left-eye samples");
    Require(reconstructionAggregate.EyeArtifactSuppressedSamples == 1, "landmark aggregate did not count eye artifact suppression samples");

    using var fakeStatefulTracker = new FakeStatefulTracker();
    using var compositeTracker = new CompositeFaceLandmarkTracker([fakeStatefulTracker]);
    compositeTracker.Reset();
    Require(fakeStatefulTracker.ResetCalls == 1, "composite tracker did not reset stateful fallback trackers");
}

static void RunFaceLockStabilitySmoke()
{
    var analyzer = new FaceLockStabilityAnalyzer();
    var calculator = new FaceLandmarkMetricCalculator();
    var startedAt = DateTime.UtcNow;
    FaceLockStabilityAnalysis analysis = FaceLockStabilityAnalysis.Waiting;
    for (var index = 0; index < 6; index++)
    {
        var capturedAt = startedAt.AddSeconds(index * 0.75d);
        var frame = CreateSyntheticLandmarkFrame(
            capturedAt,
            leftEyeRatio: 0.26d,
            rightEyeRatio: 0.25d,
            mouthRatio: 0.07d,
            eyeConfidence: 0.82d,
            mouthConfidence: 0.78d,
            source: "stability smoke dense landmarks");
        var metrics = calculator.Update(frame);
        var detection = new FaceFeatureDetection
        {
            HasFace = true,
            FaceBox = new WpfRect(0.29d + index * 0.004d, 0.18d + index * 0.002d, 0.42d, 0.60d),
            TrackingConfidence = 0.86d,
            EyeConfidence = 0.82d,
            MouthConfidence = 0.78d,
            Source = "stability smoke face box"
        };

        analysis = analyzer.Update(detection, frame, metrics);
    }

    Require(analysis.SampleCount >= 6, $"face lock stability did not retain enough samples: {analysis.SampleCount}");
    Require(analysis.FaceContinuityPercent >= 80d, $"face lock continuity too low for coherent motion: {analysis.FaceContinuityPercent}");
    Require(analysis.EyeReliabilityPercent >= 75d, $"eye reliability too low for coherent landmarks: {analysis.EyeReliabilityPercent}");
    Require(analysis.MouthReliabilityPercent >= 70d, $"mouth reliability too low for coherent landmarks: {analysis.MouthReliabilityPercent}");
    Require(analysis.CompositeReliabilityPercent >= 75d, $"composite face reliability too low: {analysis.CompositeReliabilityPercent}");

    var waiting = analyzer.Update(FaceFeatureDetection.None, FaceLandmarkFrame.None, FaceLandmarkMetrics.None);
    Require(waiting.SampleCount == 0, "face lock stability did not reset after losing the face");
}

static void RunPersonalFaceModelSmoke()
{
    var builder = new PersonalFaceModelBuilder();
    var stabilityAnalyzer = new FaceLockStabilityAnalyzer();
    var calculator = new FaceLandmarkMetricCalculator();
    var cueAnalyzer = new FaceLandmarkCueAnalyzer();
    var trendAnalyzer = new FaceLandmarkTrendAnalyzer();
    var captureQualityAnalyzer = new PersonalFaceCaptureQualityAnalyzer();
    var startedAt = DateTime.UtcNow;
    PersonalFaceModelUpdate update = new(false, PersonalFaceModelRejectionKind.NoFace, "not started", 0d, builder.CurrentModel);
    var lastFrame = FaceLandmarkFrame.None;
    var lastMetrics = FaceLandmarkMetrics.None;
    var lastStability = FaceLockStabilityAnalysis.Waiting;
    var journal = new PersonalFaceMeasurementJournal(TimeSpan.Zero, 1_000_000L);
    var journalRoot = Path.Combine(Path.GetTempPath(), $"AvatarBuilderPersonalJournalSmoke-{Guid.NewGuid():N}");
    var lastJournalPath = "";
    var collectionAuditObservations = new List<PersonalFaceCollectionAuditObservation>();

    const int awakeSampleCount = 28;
    for (var index = 0; index < awakeSampleCount; index++)
    {
        var capturedAt = startedAt.AddSeconds(index * 0.5d);
        var frame = CreateSyntheticLandmarkFrame(
            capturedAt,
            leftEyeRatio: 0.28d - index * 0.001d,
            rightEyeRatio: 0.27d - index * 0.001d,
            mouthRatio: 0.07d + index * 0.0005d,
            eyeConfidence: 0.86d,
            mouthConfidence: 0.82d,
            source: "personal model awake baseline");
        var metrics = calculator.Update(frame);
        var cue = cueAnalyzer.Analyze(metrics);
        var trend = trendAnalyzer.Update(metrics);
        var stability = stabilityAnalyzer.Update(
            new FaceFeatureDetection
            {
                HasFace = true,
                FaceBox = new WpfRect(0.31d + Math.Sin(index * 0.4d) * 0.01d, 0.19d, 0.40d, 0.58d),
                TrackingConfidence = 0.88d,
                EyeConfidence = 0.86d,
                MouthConfidence = 0.82d,
                Source = "personal model smoke face box"
            },
            frame,
            metrics);
        update = builder.Update(frame, metrics, stability, cue, trend);
        var captureQuality = captureQualityAnalyzer.Analyze(new PersonalFaceCaptureQualityInput
        {
            VideoWidth = 3840,
            VideoHeight = 2160,
            FramesPerSecond = 30d,
            InputFormat = "MJPEG",
            LandmarkFrame = frame,
            Metrics = metrics,
            Stability = stability,
            PersonalModelUpdate = update
        });
        var journalPath = journal.WriteAcceptedSampleIfDue(journalRoot, update, frame, metrics, stability, captureQuality);
        if (!string.IsNullOrWhiteSpace(journalPath))
        {
            lastJournalPath = journalPath;
        }

        collectionAuditObservations.Add(PersonalFaceCollectionAuditObservation.Create(
            capturedAt,
            subjectConfirmed: true,
            pausedForEventOrCalibration: false,
            hasFace: frame.HasFace && metrics.HasFace,
            update,
            captureQuality));

        lastFrame = frame;
        lastMetrics = metrics;
        lastStability = stability;
    }

    var model = update.Model;
    Require(model.SubjectId == PersonalFaceSubject.DefaultSubjectId, $"personal model subject id missing: {model.SubjectId}");
    Require(model.UnknownSubjectPolicy == PersonalFaceSubject.UnknownSubjectPolicy, "personal model did not preserve unknown-subject reject policy");
    Require(model.ObservedSamples == awakeSampleCount, $"personal model observed sample count wrong: {model.ObservedSamples}");
    Require(model.AcceptedSamples >= 18, $"personal model accepted too few stable awake samples: {model.AcceptedSamples}");
    Require(model.AverageEyeOpeningRatio.Average is > 0.20d, $"personal model eye baseline was not learned: {model.AverageEyeOpeningRatio.Average}");
    Require(model.MouthOpeningRatio.Average is > 0.04d and < 0.12d, $"personal model mouth baseline was not plausible: {model.MouthOpeningRatio.Average}");
    Require(model.FaceCenterX.SampleCount >= 10 && model.FaceWidth.SampleCount >= 10, "personal model did not learn face position/scale distribution");
    Require(model.LearningStability.AcceptedSampleWeight > 0d, "personal model learning stability did not record accepted sample weight");
    Require(model.LearningStability.MinimumTrackedDistributionWeight > 0d, "personal model learning stability did not record weakest tracked distribution weight");
    Require(
        model.LearningStability.MinimumTrackedDistributionWeight <= model.LearningStability.AcceptedSampleWeight,
        "personal model weakest tracked distribution weight exceeded accepted sample weight");
    Require(model.LearningStability.MaximumNextSampleInfluencePercent is > 0d and < 12d, $"personal model next-sample influence is not bounded tightly enough: {model.LearningStability.MaximumNextSampleInfluencePercent}");
    Require(
        model.LearningStability.MaximumEventLikeNextSampleInfluencePercent < model.LearningStability.MaximumNextSampleInfluencePercent,
        "personal model event-like learning was not downweighted below stable learning");
    Require(model.IdentitySignatureSamples >= 18, $"personal model identity signature samples too low: {model.IdentitySignatureSamples}");
    Require(model.FaceAspectRatio.Average is > 1d, $"personal model did not learn face aspect signature: {model.FaceAspectRatio.Average}");
    Require(model.InterEyeDistanceToFaceWidth.Average is > 0.2d and < 0.6d, $"personal model did not learn eye-spacing signature: {model.InterEyeDistanceToFaceWidth.Average}");
    Require(model.LeftEyeShape.SampleCount >= 10 && model.LeftEyeShape.Points.Count == 8, "personal model did not learn aggregate left-eye shape profile");
    Require(model.RightEyeShape.SampleCount >= 10 && model.RightEyeShape.Points.Count == 8, "personal model did not learn aggregate right-eye shape profile");
    Require(model.InnerLipShape.SampleCount >= 10 && model.InnerLipShape.Points.Count == 10, "personal model did not learn aggregate inner-lip shape profile");
    Require(model.JawShape.SampleCount >= 10 && model.JawShape.Points.Count == 9, "personal model did not learn aggregate jaw shape profile");
    Require(model.PoseBuckets.Count >= PersonalFacePoseBuckets.Definitions.Count, "personal model did not expose pose buckets");
    var neutralPose = model.PoseBuckets.FirstOrDefault(static bucket => bucket.BucketId == PersonalFacePoseBuckets.FrontNeutral);
    Require(neutralPose is { SampleCount: >= 10, PrimaryNeutralReference: true }, "personal model did not learn a front-neutral pose bucket");
    var frontNeutralPose = neutralPose ?? throw new InvalidOperationException("front-neutral pose bucket missing after assertion");
    Require(frontNeutralPose.FaceAspectRatio.SampleCount >= 10, "front-neutral pose bucket did not learn identity proportions");

    var artifactBuilder = new PersonalFaceModelBuilder();
    artifactBuilder.LoadModel(model);
    var artifactFrame = CreateSyntheticLandmarkFrame(
        startedAt.AddSeconds(awakeSampleCount * 0.5d + 1d),
        leftEyeRatio: 0.62d,
        rightEyeRatio: 0.18d,
        mouthRatio: 0.08d,
        eyeConfidence: 0.82d,
        mouthConfidence: 0.82d,
        source: "personal model eye artifact smoke",
        eyeArtifactSuppressed: true);
    var artifactMetrics = new FaceLandmarkMetricCalculator().Update(artifactFrame);
    var artifactUpdate = artifactBuilder.Update(
        artifactFrame,
        artifactMetrics,
        lastStability,
        cueAnalysis: null,
        trendAnalysis: null,
        allowEventLikeMeasurements: true);
    Require(!artifactUpdate.Accepted, "personal model accepted an eye-artifact frame into avatar learning");
    Require(
        artifactUpdate.RejectionKind == PersonalFaceModelRejectionKind.TrackingArtifact,
        $"personal model rejected eye-artifact frame with the wrong reason: {artifactUpdate.RejectionKind}, {artifactUpdate.Reason}");
    Require(
        artifactUpdate.Model.TrackingArtifactRejectedSamples == model.TrackingArtifactRejectedSamples + 1,
        "personal model did not count eye-artifact rejected samples separately");
    Require(
        artifactUpdate.Model.AcceptedSamples == model.AcceptedSamples,
        "eye-artifact rejection changed accepted sample count");
    Require(
        artifactUpdate.Model.IdentitySignatureSamples == model.IdentitySignatureSamples,
        "eye-artifact rejection changed identity signature sample count");
    Require(
        artifactUpdate.Model.PoseBuckets.First(static bucket => bucket.BucketId == PersonalFacePoseBuckets.FrontNeutral).FaceAspectRatio.SampleCount
        == frontNeutralPose.FaceAspectRatio.SampleCount,
        "eye-artifact rejection updated front-neutral identity geometry");

    var geometryArtifactBuilder = new PersonalFaceModelBuilder();
    geometryArtifactBuilder.LoadModel(model);
    var geometryArtifactFrame = CreateSyntheticLandmarkFrame(
        startedAt.AddSeconds(awakeSampleCount * 0.5d + 1.5d),
        leftEyeRatio: 0.27d,
        rightEyeRatio: 0.27d,
        mouthRatio: 0.07d,
        eyeConfidence: 0.94d,
        mouthConfidence: 0.92d,
        source: "personal model silent geometry artifact smoke",
        eyeCenterOffset: 0.13d,
        leftEyeHalfWidth: 0.018d,
        rightEyeHalfWidth: 0.086d,
        mouthHalfWidth: 0.030d,
        mouthCenterX: 0.61d,
        mouthCenterY: 0.54d);
    var geometryArtifactMetrics = new FaceLandmarkMetricCalculator().Update(geometryArtifactFrame);
    var geometryArtifactUpdate = geometryArtifactBuilder.Update(
        geometryArtifactFrame,
        geometryArtifactMetrics,
        lastStability,
        cueAnalysis: null,
        trendAnalysis: null,
        allowEventLikeMeasurements: true);
    Require(!geometryArtifactUpdate.Accepted, "personal model accepted a silent feature-geometry artifact into avatar learning");
    Require(
        geometryArtifactUpdate.RejectionKind == PersonalFaceModelRejectionKind.TrackingArtifact,
        $"personal model rejected silent geometry artifact with the wrong reason: {geometryArtifactUpdate.RejectionKind}, {geometryArtifactUpdate.Reason}");
    Require(
        geometryArtifactUpdate.IdentityAnalysis is { OutlierFeatureCount: >= 4, ConfidencePercent: < 45d },
        $"silent geometry artifact did not report enough identity outliers: {geometryArtifactUpdate.IdentityAnalysis?.Status}");
    Require(
        geometryArtifactUpdate.Reason.Contains("feature geometry", StringComparison.OrdinalIgnoreCase),
        $"silent geometry artifact rejection reason did not explain the geometry problem: {geometryArtifactUpdate.Reason}");
    Require(
        geometryArtifactUpdate.Model.TrackingArtifactRejectedSamples == model.TrackingArtifactRejectedSamples + 1,
        "personal model did not count silent geometry artifact rejected samples separately");
    Require(
        geometryArtifactUpdate.Model.AcceptedSamples == model.AcceptedSamples,
        "silent geometry artifact rejection changed accepted sample count");
    Require(
        geometryArtifactUpdate.Model.PoseBuckets.First(static bucket => bucket.BucketId == PersonalFacePoseBuckets.FrontNeutral).FaceAspectRatio.SampleCount
        == frontNeutralPose.FaceAspectRatio.SampleCount,
        "silent geometry artifact rejection updated front-neutral identity geometry");

    Require(!string.IsNullOrWhiteSpace(lastJournalPath) && File.Exists(lastJournalPath), "personal measurement journal did not write accepted measurement samples");
    var journalText = File.ReadAllText(lastJournalPath);
    Require(journalText.Contains("SubjectId", StringComparison.Ordinal), "personal measurement journal did not include subject id");
    Require(journalText.Contains("AverageEyeOpeningRatio", StringComparison.Ordinal), "personal measurement journal did not include compact eye opening measurements");
    Require(journalText.Contains("FaceAspectRatio", StringComparison.Ordinal), "personal measurement journal did not include identity signature measurements");
    Require(journalText.Contains("SampleWeight", StringComparison.Ordinal), "personal measurement journal did not include sample weights");
    Require(journalText.Contains("PoseBucketIds", StringComparison.Ordinal), "personal measurement journal did not include compact pose bucket ids");
    Require(journalText.Contains("IdentityConfidencePercent", StringComparison.Ordinal), "personal measurement journal did not include compact identity confidence");
    Require(journalText.Contains("IdentityOutlierFeatureCount", StringComparison.Ordinal), "personal measurement journal did not include identity outlier summary");
    Require(journalText.Contains("CaptureQualityLabel", StringComparison.Ordinal), "personal measurement journal did not include capture quality label");
    Require(journalText.Contains("CaptureQualityScorePercent", StringComparison.Ordinal), "personal measurement journal did not include capture quality score");
    Require(!journalText.Contains("FaceContour", StringComparison.Ordinal) && !journalText.Contains("LeftEyeContour", StringComparison.Ordinal), "personal measurement journal stored raw contour data");

    var modelStore = new PersonalFaceModelStore();
    var persistedModelFolder = Path.Combine(journalRoot, "persisted_personal_model");
    var persistedModelPath = modelStore.Write(persistedModelFolder, model);
    Require(File.Exists(persistedModelPath), "personal face model store did not write a model file");
    var persistedModel = modelStore.Read(persistedModelFolder);
    Require(persistedModel.AcceptedSamples == model.AcceptedSamples, "personal face model store did not read accepted sample count");
    Require(persistedModel.AverageEyeOpeningRatio.Average == model.AverageEyeOpeningRatio.Average, "personal face model store did not preserve eye average");
    Require(persistedModel.LearningStability.MaximumNextSampleInfluencePercent == model.LearningStability.MaximumNextSampleInfluencePercent, "personal face model store did not preserve learning stability");
    Require(persistedModel.LeftEyeShape.SampleCount == model.LeftEyeShape.SampleCount, "personal face model store did not preserve aggregate eye shape profile");
    Require(persistedModel.PoseBuckets.Any(static bucket => bucket.BucketId == PersonalFacePoseBuckets.FrontNeutral && bucket.SampleCount >= 10), "personal face model store did not preserve pose buckets");
    var resumedBuilder = new PersonalFaceModelBuilder();
    resumedBuilder.LoadModel(persistedModel);
    var resumedModel = resumedBuilder.CurrentModel;
    Require(resumedModel.ObservedSamples == model.ObservedSamples, "personal face model builder did not restore observed sample count");
    Require(resumedModel.AcceptedSamples == model.AcceptedSamples, "personal face model builder did not restore accepted sample count");
    Require(
        resumedModel.AverageEyeOpeningRatio.Average is double resumedEye
        && model.AverageEyeOpeningRatio.Average is double modelEye
        && Math.Abs(resumedEye - modelEye) < 0.000001d,
        $"personal face model builder did not restore weighted eye average: saved={model.AverageEyeOpeningRatio.Average}, resumed={resumedModel.AverageEyeOpeningRatio.Average}");
    Require(
        resumedModel.MouthOpeningRatio.TotalWeight >= model.MouthOpeningRatio.TotalWeight * 0.999d,
        "personal face model builder did not restore mouth distribution weight");
    var continuedUpdate = resumedBuilder.Update(lastFrame, lastMetrics, lastStability, cueAnalysis: null, trendAnalysis: null);
    Require(continuedUpdate.Accepted, $"resumed personal face model did not accept a stable continuation sample: {continuedUpdate.RejectionKind}, {continuedUpdate.Reason}");
    Require(continuedUpdate.Model.ObservedSamples == model.ObservedSamples + 1, "resumed personal face model did not continue observed sample count");
    Require(continuedUpdate.Model.AcceptedSamples == model.AcceptedSamples + 1, "resumed personal face model did not continue accepted sample count");
    Require(
        continuedUpdate.Model.AverageEyeOpeningRatio.TotalWeight > resumedModel.AverageEyeOpeningRatio.TotalWeight,
        "resumed personal face model did not continue weighted distribution updates");
    Require(
        continuedUpdate.Model.LearningStability.MaximumNextSampleInfluencePercent < resumedModel.LearningStability.MaximumNextSampleInfluencePercent,
        "resumed personal face model did not reduce next-sample influence after accepting more evidence");
    Require(
        continuedUpdate.Model.LeftEyeShape.SampleCount > resumedModel.LeftEyeShape.SampleCount,
        "resumed personal face model did not continue aggregate contour shape learning");
    Require(
        continuedUpdate.Model.PoseBuckets.First(static bucket => bucket.BucketId == PersonalFacePoseBuckets.FrontNeutral).SampleCount
        > resumedModel.PoseBuckets.First(static bucket => bucket.BucketId == PersonalFacePoseBuckets.FrontNeutral).SampleCount,
        "resumed personal face model did not continue pose bucket learning");

    var outlierBuilder = new PersonalFaceModelBuilder();
    outlierBuilder.LoadModel(model);
    var outlierCalculator = new FaceLandmarkMetricCalculator();
    var outlierFrame = CreateSyntheticLandmarkFrame(
        startedAt.AddSeconds(awakeSampleCount * 0.5d + 2d),
        leftEyeRatio: 0.58d,
        rightEyeRatio: 0.58d,
        mouthRatio: 0.34d,
        eyeConfidence: 0.95d,
        mouthConfidence: 0.95d,
        source: "personal model deliberate outlier");
    var outlierMetrics = outlierCalculator.Update(outlierFrame);
    var outlierUpdate = outlierBuilder.Update(outlierFrame, outlierMetrics, lastStability, cueAnalysis: null, trendAnalysis: null);
    Require(outlierUpdate.Accepted, $"personal model rejected deliberate bounded-drift outlier unexpectedly: {outlierUpdate.RejectionKind}, {outlierUpdate.Reason}");
    RequireBoundedAverageMovement(
        "eye opening",
        model.AverageEyeOpeningRatio.Average,
        outlierMetrics.AverageEyeOpeningRatio,
        outlierUpdate.Model.AverageEyeOpeningRatio.Average,
        model.LearningStability.MaximumNextSampleInfluencePercent);
    RequireBoundedAverageMovement(
        "mouth opening",
        model.MouthOpeningRatio.Average,
        outlierMetrics.MouthOpeningRatio,
        outlierUpdate.Model.MouthOpeningRatio.Average,
        model.LearningStability.MaximumNextSampleInfluencePercent);

    resumedBuilder.Reset();
    var resetModel = resumedBuilder.CurrentModel;
    Require(resetModel.ObservedSamples == 0 && resetModel.AcceptedSamples == 0, "personal face model reset did not clear sample counts");
    Require(resetModel.AverageEyeOpeningRatio.SampleCount == 0, "personal face model reset did not clear eye distribution");
    Require(
        resetModel.PoseBuckets.All(static bucket => bucket.SampleCount == 0),
        "personal face model reset did not clear pose bucket samples");

    var lowCameraModeQuality = captureQualityAnalyzer.Analyze(new PersonalFaceCaptureQualityInput
    {
        VideoWidth = 640,
        VideoHeight = 360,
        FramesPerSecond = 12d,
        InputFormat = "synthetic",
        LandmarkFrame = lastFrame,
        Metrics = lastMetrics,
        Stability = lastStability,
        PersonalModelUpdate = update
    });
    var lowCameraModeUpdate = new PersonalFaceModelUpdate(
        false,
        PersonalFaceModelRejectionKind.LowQuality,
        $"capture quality gate: {lowCameraModeQuality.PrimaryReason}",
        0d,
        update.Model);
    collectionAuditObservations.Add(PersonalFaceCollectionAuditObservation.Create(
        startedAt.AddSeconds(awakeSampleCount * 0.5d),
        subjectConfirmed: true,
        pausedForEventOrCalibration: false,
        hasFace: lastFrame.HasFace && lastMetrics.HasFace,
        lowCameraModeUpdate,
        lowCameraModeQuality));
    var lowCameraModeJournalPath = journal.WriteAcceptedSampleIfDue(journalRoot, update, lastFrame, lastMetrics, lastStability, lowCameraModeQuality);
    Require(string.IsNullOrWhiteSpace(lowCameraModeJournalPath), "personal measurement journal wrote a low camera-mode sample");

    var measurementsFolder = Path.Combine(journalRoot, "measurements");
    var recentSamples = PersonalFaceMeasurementJournal.ReadRecentSamples(journalRoot, maxSamples: 100);
    Require(recentSamples.Count >= 10, $"personal measurement journal reader returned too few samples: {recentSamples.Count}");
    Require(recentSamples.All(static sample => sample.CaptureQualityCanCollect), "personal measurement journal reader returned non-collectable samples");
    Require(recentSamples.All(static sample => sample.CaptureQualityScorePercent >= 62d), "personal measurement journal reader returned below-threshold capture-quality samples");
    Require(
        recentSamples.SequenceEqual(recentSamples.OrderBy(static sample => sample.CapturedAtUtc)),
        "personal measurement journal reader did not return samples in chronological order");
    var journalMotionModel = new PersonalFaceMotionModelBuilder().Build(recentSamples.Select(PersonalFaceMotionObservation.FromMeasurementSample));
    Require(journalMotionModel.UsableObservationCount >= 10, $"journal motion model usable count too low: {journalMotionModel.UsableObservationCount}");
    Require(journalMotionModel.MotionPairCount >= 8, $"journal motion model pair count too low: {journalMotionModel.MotionPairCount}");
    var journalMotionPath = new PersonalFaceMotionModelStore().Write(journalRoot, journalMotionModel);
    Require(File.Exists(journalMotionPath), "journal-derived personal face motion model was not written");
    var journalMotionJson = File.ReadAllText(journalMotionPath);
    Require(journalMotionJson.Contains("personal-face-motion-model-v1", StringComparison.Ordinal), "journal motion model JSON did not include schema");
    Require(!journalMotionJson.Contains("FaceContour", StringComparison.Ordinal) && !journalMotionJson.Contains("data:image", StringComparison.OrdinalIgnoreCase), "journal motion model leaked raw contour/image data");
    var readiness = new PersonalFaceCorpusReadinessBuilder().Build(model, journalMotionModel, recentSamples, PersonalFaceMeasurementJournal.GetMeasurementsSizeBytes(journalRoot), 1_000_000L);
    Require(readiness.OverallReadinessPercent > 0d, "personal face corpus readiness did not produce a score");
    Require(readiness.IdentityCoveragePercent > 0d, "personal face corpus readiness did not include identity coverage");
    Require(readiness.ContourShapeCoveragePercent > 0d, "personal face corpus readiness did not include contour shape coverage");
    Require(readiness.EyeBehindGlassesTrustPercent > 0d, "personal face corpus readiness did not include eye-behind-glasses trust");
    Require(readiness.MouthJawTrustPercent > 0d, "personal face corpus readiness did not include mouth/jaw trust");
    Require(readiness.DirectFeatureMeasurementTrustPercent > 0d, "personal face corpus readiness did not include direct feature trust");
    Require(readiness.ApertureConsistencyHealthPercent > 0d, "personal face corpus readiness did not include aperture consistency health");
    Require(readiness.EyeApertureReliabilityHealthPercent > 0d, "personal face corpus readiness did not include eye aperture reliability health");
    Require(readiness.MouthVerticalAnchorHealthPercent > 0d, "personal face corpus readiness did not include mouth vertical anchor health");
    Require(readiness.LearningStabilityCoveragePercent > 0d, "personal face corpus readiness did not include learning stability coverage");
    Require(readiness.XYZABCCoveragePercent >= 0d, "personal face corpus readiness did not include XYZABC coverage");
    Require(readiness.DataAuditHealthPercent > 0d, "personal face corpus readiness did not include data audit health");
    Require(readiness.PoseEstimationHealthPercent > 0d, "personal face corpus readiness did not include pose estimation audit health");
    Require(readiness.PoseBucketConsistencyHealthPercent > 0d, "personal face corpus readiness did not include pose-bucket consistency health");
    Require(readiness.SurfaceGeometryHealthPercent >= 0d, "personal face corpus readiness did not include surface geometry health");
    Require(readiness.SurfaceGeometryPatchCount >= 0, $"personal face corpus readiness surface geometry patch count was invalid: {readiness.SurfaceGeometryPatchCount}");
    Require(!string.IsNullOrWhiteSpace(readiness.SurfaceGeometryStatus), "personal face corpus readiness did not include surface geometry status");
    Require(readiness.IdentitySessionHealthPercent > 0d, "personal face corpus readiness did not include identity-session health");
    Require(!string.IsNullOrWhiteSpace(readiness.IdentitySessionAuditStage), "personal face corpus readiness did not include identity-session audit stage");
    Require(!string.IsNullOrWhiteSpace(readiness.IdentitySessionAuditStatus), "personal face corpus readiness did not include identity-session audit status");
    Require(readiness.MaximumNextSampleInfluencePercent > 0d, "personal face corpus readiness did not include next-sample influence");
    Require(readiness.Warnings.Any(static warning => warning.Contains("weakly anchored", StringComparison.OrdinalIgnoreCase)), "early personal face corpus readiness did not warn about weak anchoring");
    Require(readiness.NextCaptureSuggestions.Any(static suggestion => suggestion.Contains("next-sample influence", StringComparison.OrdinalIgnoreCase)), "early personal face corpus readiness did not suggest reducing next-sample influence");
    Require(readiness.CaptureQualitySamples == recentSamples.Count, $"personal face corpus readiness did not review capture quality samples: {readiness.CaptureQualitySamples}/{recentSamples.Count}");
    Require(readiness.CaptureQualityCanCollectRate >= 0.99d, $"personal face corpus readiness collectable rate was too low: {readiness.CaptureQualityCanCollectRate}");
    Require(readiness.CaptureQualityCoveragePercent > 0d, "personal face corpus readiness did not score capture quality coverage");
    Require(readiness.NextCaptureSuggestions.Count > 0, "early personal face corpus readiness did not suggest next captures");
    var readinessPath = new PersonalFaceCorpusReadinessStore().Write(journalRoot, readiness);
    var readinessHtmlPath = PersonalFaceCorpusReadinessStore.GetHtmlPath(readinessPath);
    Require(File.Exists(readinessPath), "personal face corpus readiness JSON was not written");
    Require(File.Exists(readinessHtmlPath), "personal face corpus readiness HTML was not written");
    var readinessJson = File.ReadAllText(readinessPath);
    var readinessHtml = File.ReadAllText(readinessHtmlPath);
    Require(readinessJson.Contains("personal-face-corpus-readiness-v1", StringComparison.Ordinal), "personal face corpus readiness JSON did not include schema");
    Require(readinessJson.Contains("IdentityCoveragePercent", StringComparison.Ordinal), "personal face corpus readiness JSON did not include identity coverage");
    Require(readinessJson.Contains("ContourShapeCoveragePercent", StringComparison.Ordinal), "personal face corpus readiness JSON did not include contour shape coverage");
    Require(readinessJson.Contains("DirectFeatureMeasurementTrustPercent", StringComparison.Ordinal), "personal face corpus readiness JSON did not include direct feature trust");
    Require(readinessJson.Contains("ApertureConsistencyHealthPercent", StringComparison.Ordinal), "personal face corpus readiness JSON did not include aperture consistency health");
    Require(readinessJson.Contains("EyeApertureReliabilityHealthPercent", StringComparison.Ordinal), "personal face corpus readiness JSON did not include eye aperture reliability health");
    Require(readinessJson.Contains("PossibleOneEyeArtifactRate", StringComparison.Ordinal), "personal face corpus readiness JSON did not include possible one-eye artifact rate");
    Require(readinessJson.Contains("MouthVerticalAnchorHealthPercent", StringComparison.Ordinal), "personal face corpus readiness JSON did not include mouth vertical anchor health");
    Require(readinessJson.Contains("LearningStabilityCoveragePercent", StringComparison.Ordinal), "personal face corpus readiness JSON did not include learning stability coverage");
    Require(readinessJson.Contains("MinimumTrackedDistributionWeight", StringComparison.Ordinal), "personal face corpus readiness JSON did not include weakest tracked distribution weight");
    Require(readinessJson.Contains("CaptureQualityCoveragePercent", StringComparison.Ordinal), "personal face corpus readiness JSON did not include capture quality coverage");
    Require(readinessJson.Contains("XYZABCCoveragePercent", StringComparison.Ordinal), "personal face corpus readiness JSON did not include XYZABC coverage");
    Require(readinessJson.Contains("DataAuditHealthPercent", StringComparison.Ordinal), "personal face corpus readiness JSON did not include data audit health");
    Require(readinessJson.Contains("PoseBucketConsistencyHealthPercent", StringComparison.Ordinal), "personal face corpus readiness JSON did not include pose-bucket consistency health");
    Require(readinessJson.Contains("PoseAxisHealthPercent", StringComparison.Ordinal), "personal face corpus readiness JSON did not include pose-axis health");
    Require(readinessJson.Contains("SurfaceGeometryHealthPercent", StringComparison.Ordinal), "personal face corpus readiness JSON did not include surface geometry health");
    Require(readinessJson.Contains("SurfaceGeometryStatus", StringComparison.Ordinal), "personal face corpus readiness JSON did not include surface geometry status");
    Require(readinessJson.Contains("IdentitySessionHealthPercent", StringComparison.Ordinal), "personal face corpus readiness JSON did not include identity-session health");
    Require(readinessJson.Contains("IdentitySessionAuditStage", StringComparison.Ordinal), "personal face corpus readiness JSON did not include identity-session audit stage");
    Require(readinessJson.Contains("IdentitySessionAuditStatus", StringComparison.Ordinal), "personal face corpus readiness JSON did not include identity-session audit status");
    Require(readinessHtml.Contains("Coverage Scores", StringComparison.Ordinal), "personal face corpus readiness HTML did not include score section");
    Require(readinessHtml.Contains("Identity", StringComparison.Ordinal), "personal face corpus readiness HTML did not include identity score");
    Require(readinessHtml.Contains("Contour Shape", StringComparison.Ordinal), "personal face corpus readiness HTML did not include contour shape score");
    Require(readinessHtml.Contains("Direct Feature Trust", StringComparison.Ordinal), "personal face corpus readiness HTML did not include direct feature trust score");
    Require(readinessHtml.Contains("Aperture Consistency", StringComparison.Ordinal), "personal face corpus readiness HTML did not include aperture consistency score");
    Require(readinessHtml.Contains("Eye Aperture Reliability", StringComparison.Ordinal), "personal face corpus readiness HTML did not include eye aperture reliability score");
    Require(readinessHtml.Contains("Mouth Anchor", StringComparison.Ordinal), "personal face corpus readiness HTML did not include mouth anchor score");
    Require(readinessHtml.Contains("Learning Stability", StringComparison.Ordinal), "personal face corpus readiness HTML did not include learning stability score");
    Require(readinessHtml.Contains("Weakest tracked weight", StringComparison.Ordinal), "personal face corpus readiness HTML did not include weakest tracked distribution weight");
    Require(readinessHtml.Contains("Capture Quality", StringComparison.Ordinal), "personal face corpus readiness HTML did not include capture quality score");
    Require(readinessHtml.Contains("XYZABC", StringComparison.Ordinal), "personal face corpus readiness HTML did not include XYZABC score");
    Require(readinessHtml.Contains("Data Audit", StringComparison.Ordinal), "personal face corpus readiness HTML did not include data audit score");
    Require(readinessHtml.Contains("Pose Bucket Consistency", StringComparison.Ordinal), "personal face corpus readiness HTML did not include pose-bucket consistency score");
    Require(readinessHtml.Contains("Surface Geometry", StringComparison.Ordinal), "personal face corpus readiness HTML did not include surface geometry score");
    Require(readinessHtml.Contains("Identity Session", StringComparison.Ordinal), "personal face corpus readiness HTML did not include identity-session score");
    Require(readinessHtml.Contains("Identity session status", StringComparison.Ordinal), "personal face corpus readiness HTML did not include identity-session audit status");
    Require(!readinessJson.Contains("FaceContour", StringComparison.Ordinal) && !readinessJson.Contains("data:image", StringComparison.OrdinalIgnoreCase), "personal face corpus readiness leaked raw contour/image data");
    Require(!readinessHtml.Contains("data:image", StringComparison.OrdinalIgnoreCase), "personal face corpus readiness HTML embedded raw image data");

    var collectionAudit = new PersonalFaceCollectionAuditBuilder().Build(model, collectionAuditObservations);
    Require(collectionAudit.TotalFramesReviewed == awakeSampleCount + 1, $"personal collection audit frame count wrong: {collectionAudit.TotalFramesReviewed}");
    Require(collectionAudit.PersonalModelAcceptedFrames >= 18, $"personal collection audit accepted too few frames: {collectionAudit.PersonalModelAcceptedFrames}");
    Require(collectionAudit.LowQualityGateFrames >= 1, "personal collection audit did not count low-quality gate frames");
    Require(collectionAudit.IdentityMeasuredFrames >= 18, $"personal collection audit identity measured frames too low: {collectionAudit.IdentityMeasuredFrames}");
    Require(collectionAudit.AverageIdentityConfidencePercent.HasValue, "personal collection audit did not summarize identity confidence");
    Require(collectionAudit.CaptureQualityCollectableRate < 1d, "personal collection audit did not reflect rejected low camera-mode sample");
    Require(collectionAudit.TopPersonalModelRejectionReasons.Count > 0, "personal collection audit did not summarize rejection reasons");
    var trackingHoldObservations = collectionAuditObservations
        .Concat([
            new PersonalFaceCollectionAuditObservation
            {
                ReviewedAtUtc = startedAt.AddSeconds(awakeSampleCount * 0.5d + 1d),
                SubjectConfirmed = true,
                HasFace = true,
                PersonalModelAccepted = false,
                PersonalModelRejectionKind = PersonalFaceModelRejectionKind.TrackingAuditHold.ToString(),
                PersonalModelUpdateReason = "tracking audit hold: head A/B rotations are not moving",
                CaptureQualityLabel = "avatar-grade",
                CaptureQualityScorePercent = 88d,
                CaptureQualityCanCollect = true,
                CaptureQualityAvatarGrade = true,
                CaptureQualityReason = "tracking audit hold",
                CaptureQualityCameraModeScorePercent = 92d,
                CaptureQualityFaceScaleScorePercent = 90d,
                CaptureQualityEyeScorePercent = 88d,
                CaptureQualityMouthScorePercent = 86d,
                CaptureQualityStabilityScorePercent = 90d,
                CaptureQualityGlassesScorePercent = 82d,
                CaptureQualityStorageScorePercent = 100d
            },
            new PersonalFaceCollectionAuditObservation
            {
                ReviewedAtUtc = startedAt.AddSeconds(awakeSampleCount * 0.5d + 1.5d),
                SubjectConfirmed = true,
                HasFace = true,
                PersonalModelAccepted = false,
                PersonalModelRejectionKind = PersonalFaceModelRejectionKind.TrackingArtifact.ToString(),
                PersonalModelUpdateReason = "eye artifact suppression active; not learning personal face shape from this frame",
                CaptureQualityLabel = "limited",
                CaptureQualityScorePercent = 66d,
                CaptureQualityCanCollect = false,
                CaptureQualityAvatarGrade = false,
                CaptureQualityReason = "tracking artifact gate",
                CaptureQualityCameraModeScorePercent = 92d,
                CaptureQualityFaceScaleScorePercent = 90d,
                CaptureQualityEyeScorePercent = 42d,
                CaptureQualityMouthScorePercent = 86d,
                CaptureQualityStabilityScorePercent = 90d,
                CaptureQualityGlassesScorePercent = 36d,
                CaptureQualityStorageScorePercent = 100d
            }
        ])
        .ToList();
    var trackingHoldAudit = new PersonalFaceCollectionAuditBuilder().Build(model, trackingHoldObservations);
    Require(trackingHoldAudit.TrackingAuditHoldFrames == 1, "personal collection audit did not count tracking audit holds");
    Require(trackingHoldAudit.TrackingArtifactGateFrames == 1, "personal collection audit did not count tracking artifact gates");
    Require(
        trackingHoldAudit.NextActions.Any(static action => action.Contains("tracking audit", StringComparison.OrdinalIgnoreCase)),
        "personal collection audit did not suggest next action for tracking audit holds");
    Require(
        trackingHoldAudit.NextActions.Any(static action => action.Contains("Tracking artifacts", StringComparison.OrdinalIgnoreCase)),
        "personal collection audit did not suggest next action for tracking artifacts");
    var collectionAuditPath = new PersonalFaceCollectionAuditStore().Write(journalRoot, collectionAudit);
    var collectionAuditHtmlPath = PersonalFaceCollectionAuditStore.GetHtmlPath(collectionAuditPath);
    Require(File.Exists(collectionAuditPath), "personal collection audit JSON was not written");
    Require(File.Exists(collectionAuditHtmlPath), "personal collection audit HTML was not written");
    var collectionAuditJson = File.ReadAllText(collectionAuditPath);
    var collectionAuditHtml = File.ReadAllText(collectionAuditHtmlPath);
    Require(collectionAuditJson.Contains("personal-face-collection-audit-v1", StringComparison.Ordinal), "personal collection audit JSON did not include schema");
    Require(collectionAuditJson.Contains("CaptureQualityCollectableRate", StringComparison.Ordinal), "personal collection audit JSON did not include collectable rate");
    Require(collectionAuditJson.Contains("TopCaptureQualityIssues", StringComparison.Ordinal), "personal collection audit JSON did not include top capture issues");
    Require(collectionAuditJson.Contains("AverageIdentityConfidencePercent", StringComparison.Ordinal), "personal collection audit JSON did not include identity confidence");
    Require(collectionAuditJson.Contains("TrackingAuditHoldFrames", StringComparison.Ordinal), "personal collection audit JSON did not include tracking audit hold frames");
    Require(collectionAuditJson.Contains("TrackingArtifactGateFrames", StringComparison.Ordinal), "personal collection audit JSON did not include tracking artifact gate frames");
    Require(collectionAuditHtml.Contains("Collection Gates", StringComparison.Ordinal), "personal collection audit HTML did not include gate section");
    Require(collectionAuditHtml.Contains("Identity confidence", StringComparison.Ordinal), "personal collection audit HTML did not include identity confidence");
    Require(collectionAuditHtml.Contains("Tracking audit hold", StringComparison.Ordinal), "personal collection audit HTML did not include tracking audit hold count");
    Require(collectionAuditHtml.Contains("Tracking artifact gate", StringComparison.Ordinal), "personal collection audit HTML did not include tracking artifact count");
    Require(!collectionAuditJson.Contains("FaceContour", StringComparison.Ordinal) && !collectionAuditJson.Contains("data:image", StringComparison.OrdinalIgnoreCase), "personal collection audit leaked raw contour/image data");
    Require(!collectionAuditHtml.Contains("data:image", StringComparison.OrdinalIgnoreCase), "personal collection audit HTML embedded raw image data");

    for (var fileIndex = 0; fileIndex < 3; fileIndex++)
    {
        var fillerPath = Path.Combine(measurementsFolder, $"old-{fileIndex}.jsonl");
        File.WriteAllText(fillerPath, new string('x', 700_000), Encoding.UTF8);
        File.SetLastWriteTimeUtc(fillerPath, startedAt.AddDays(-10 - fileIndex));
    }

    var budgetReport = PersonalFaceMeasurementJournal.EnforceBudget(measurementsFolder, 1_000_000L);
    Require(
        Directory.EnumerateFiles(measurementsFolder, "old-*.jsonl").Count() <= 1,
        "personal measurement journal budget pruning did not delete oldest measurement files");
    Require(budgetReport.BytesAfter <= budgetReport.BudgetBytes, $"personal measurement journal budget report stayed over budget: {budgetReport.BytesAfter}/{budgetReport.BudgetBytes}");
    Directory.Delete(journalRoot, recursive: true);

    var oversizedJournalRoot = Path.Combine(Path.GetTempPath(), $"AvatarBuilderOversizedPersonalJournalSmoke-{Guid.NewGuid():N}");
    var oversizedMeasurementFolder = Path.Combine(oversizedJournalRoot, "measurements");
    Directory.CreateDirectory(oversizedMeasurementFolder);
    var oversizedPath = Path.Combine(oversizedMeasurementFolder, "oversized.jsonl");
    File.WriteAllText(oversizedPath, new string('x', 1_200_000), Encoding.UTF8);
    var oversizedBudgetReport = PersonalFaceMeasurementJournal.EnforceBudgetForModelFolder(oversizedJournalRoot, 1_000_000L);
    Require(oversizedBudgetReport.DeletedFileCount == 1, $"single oversized journal file was not pruned: {oversizedBudgetReport.DeletedFileCount}");
    Require(oversizedBudgetReport.BytesAfter <= oversizedBudgetReport.BudgetBytes, $"single oversized journal file left storage over budget: {oversizedBudgetReport.BytesAfter}/{oversizedBudgetReport.BudgetBytes}");
    Require(!File.Exists(oversizedPath), "single oversized journal file still exists after budget enforcement");
    Directory.Delete(oversizedJournalRoot, recursive: true);

    var sleepyFrame = CreateSyntheticLandmarkFrame(
        startedAt.AddSeconds(awakeSampleCount * 0.5d + 0.5d),
        leftEyeRatio: 0.08d,
        rightEyeRatio: 0.07d,
        mouthRatio: 0.34d,
        eyeConfidence: 0.86d,
        mouthConfidence: 0.82d,
        source: "personal model event-like frame");
    var sleepyMetrics = calculator.Update(sleepyFrame);
    var sleepyCue = cueAnalyzer.Analyze(sleepyMetrics);
    var sleepyTrend = trendAnalyzer.Update(sleepyMetrics);
    var sleepyStability = stabilityAnalyzer.Update(
        new FaceFeatureDetection
        {
            HasFace = true,
            FaceBox = new WpfRect(0.32d, 0.20d, 0.40d, 0.58d),
            TrackingConfidence = 0.88d,
            EyeConfidence = 0.86d,
            MouthConfidence = 0.82d,
            Source = "personal model smoke face box"
        },
        sleepyFrame,
        sleepyMetrics);
    var rejected = builder.Update(sleepyFrame, sleepyMetrics, sleepyStability, sleepyCue, sleepyTrend);
    Require(!rejected.Accepted && rejected.RejectionKind == PersonalFaceModelRejectionKind.EventLike, $"personal model did not reject event-like frame: {rejected.RejectionKind}, {rejected.Reason}");
    Require(rejected.Model.EventLikeRejectedSamples >= 1, "personal model did not count event-like rejected samples");
    var avatarRangeUpdate = builder.Update(
        sleepyFrame,
        sleepyMetrics,
        sleepyStability,
        sleepyCue,
        sleepyTrend,
        allowEventLikeMeasurements: true);
    Require(avatarRangeUpdate.Accepted, $"avatar learning did not accept event-like face range/motion data: {avatarRangeUpdate.RejectionKind}, {avatarRangeUpdate.Reason}");
    Require(avatarRangeUpdate.SampleWeight is > 0d and < 1d, $"avatar event-like range sample did not use reduced weight: {avatarRangeUpdate.SampleWeight}");
    Require(
        avatarRangeUpdate.Reason.Contains("event-like face range/motion", StringComparison.OrdinalIgnoreCase),
        $"avatar event-like range sample did not explain why it was accepted: {avatarRangeUpdate.Reason}");

    var warmupIdentityBuilder = new PersonalFaceModelBuilder("chris", "Chris", PersonalFaceSubject.ManualConfirmationMode);
    var warmupIdentityCalculator = new FaceLandmarkMetricCalculator();
    var warmupIdentityStability = new FaceLockStabilityAnalysis
    {
        SampleCount = 12,
        WindowSeconds = 4d,
        FaceBoundsRatePercent = 100d,
        FaceContinuityPercent = 92d,
        EyeUsableRatePercent = 100d,
        MouthUsableRatePercent = 100d,
        AverageEyeQualityPercent = 88d,
        AverageMouthQualityPercent = 88d,
        AverageOverallQualityPercent = 88d,
        EyeReliabilityPercent = 90d,
        MouthReliabilityPercent = 88d,
        CompositeReliabilityPercent = 91d
    };
    PersonalFaceModelUpdate warmupIdentityUpdate = new(false, PersonalFaceModelRejectionKind.NoFace, "not started", 0d, warmupIdentityBuilder.CurrentModel);
    for (var index = 0; index < 90; index++)
    {
        var frame = CreateSyntheticLandmarkFrame(
            startedAt.AddSeconds(40 + index * 0.25d),
            leftEyeRatio: 0.26d,
            rightEyeRatio: 0.26d,
            mouthRatio: 0.08d,
            eyeConfidence: 0.90d,
            mouthConfidence: 0.88d,
            source: "identity warmup owner baseline");
        var metrics = warmupIdentityCalculator.Update(frame);
        warmupIdentityUpdate = warmupIdentityBuilder.Update(frame, metrics, warmupIdentityStability, cueAnalysis: null, trendAnalysis: null);
    }

    Require(warmupIdentityUpdate.Model.IdentitySignatureSamples >= 80, $"warmup identity signature did not reach protective gate sample count: {warmupIdentityUpdate.Model.IdentitySignatureSamples}");
    var warmupMismatchFrame = CreateSyntheticLandmarkFrame(
        startedAt.AddSeconds(70),
        leftEyeRatio: 0.26d,
        rightEyeRatio: 0.26d,
        mouthRatio: 0.08d,
        eyeConfidence: 0.91d,
        mouthConfidence: 0.89d,
        source: "identity warmup mismatch",
        faceHalfWidth: 0.16d,
        faceHalfHeight: 0.36d,
        eyeCenterOffset: 0.105d,
        eyeHalfWidth: 0.035d,
        mouthHalfWidth: 0.125d,
        eyeCenterY: 0.35d,
        mouthCenterY: 0.68d);
    var warmupMismatchMetrics = warmupIdentityCalculator.Update(warmupMismatchFrame);
    var warmupMismatchUpdate = warmupIdentityBuilder.Update(warmupMismatchFrame, warmupMismatchMetrics, warmupIdentityStability, cueAnalysis: null, trendAnalysis: null);
    var warmupMismatchScores = warmupMismatchUpdate.IdentityAnalysis is null
        ? "no identity analysis"
        : string.Join("; ", warmupMismatchUpdate.IdentityAnalysis.FeatureScores.Select(score => $"{score.Name}={score.ConfidencePercent:0.#}% outlier={score.IsOutlier}"));
    Require(
        !warmupMismatchUpdate.Accepted && warmupMismatchUpdate.RejectionKind == PersonalFaceModelRejectionKind.SubjectMismatch,
        $"warmup identity protection did not reject an extreme synthetic non-subject: {warmupMismatchUpdate.RejectionKind}, {warmupMismatchUpdate.Reason}; analysis {warmupMismatchUpdate.IdentityAnalysis?.Status}; scores {warmupMismatchScores}");
    Require(
        warmupMismatchUpdate.IdentityAnalysis is { AutoGateReady: false, WarmupStrongMismatchGateReady: true, ConfidencePercent: < 12d },
        $"warmup identity protection did not report a strong mismatch: {warmupMismatchUpdate.IdentityAnalysis?.Status}");

    var identityBuilder = new PersonalFaceModelBuilder("chris", "Chris", PersonalFaceSubject.ManualConfirmationMode);
    var identityStability = new FaceLockStabilityAnalyzer();
    var identityCalculator = new FaceLandmarkMetricCalculator();
    PersonalFaceModelUpdate identityUpdate = new(false, PersonalFaceModelRejectionKind.NoFace, "not started", 0d, identityBuilder.CurrentModel);
    for (var index = 0; index < 260; index++)
    {
        var frame = CreateSyntheticLandmarkFrame(
            startedAt.AddSeconds(index * 0.25d),
            leftEyeRatio: 0.26d,
            rightEyeRatio: 0.26d,
            mouthRatio: 0.08d,
            eyeConfidence: 0.90d,
            mouthConfidence: 0.88d,
            source: "identity owner baseline");
        var metrics = identityCalculator.Update(frame);
        var stability = identityStability.Update(
            new FaceFeatureDetection
            {
                HasFace = true,
                FaceBox = new WpfRect(0.31d, 0.18d, 0.40d, 0.58d),
                TrackingConfidence = 0.92d,
                EyeConfidence = 0.90d,
                MouthConfidence = 0.88d,
                Source = "identity owner face box"
            },
            frame,
            metrics);
        identityUpdate = identityBuilder.Update(frame, metrics, stability, cueAnalysis: null, trendAnalysis: null);
    }

    Require(identityUpdate.Model.IdentitySignatureSamples >= 240, $"identity signature did not reach gate-ready sample count: {identityUpdate.Model.IdentitySignatureSamples}");
    PersonalFaceModelUpdate nonSubjectUpdate = new(false, PersonalFaceModelRejectionKind.NoFace, "not started", 0d, identityBuilder.CurrentModel);
    for (var attempt = 0; attempt < 4; attempt++)
    {
        var nonSubjectFrame = CreateSyntheticLandmarkFrame(
            startedAt.AddSeconds(80 + attempt * 0.25d),
            leftEyeRatio: 0.26d,
            rightEyeRatio: 0.26d,
            mouthRatio: 0.08d,
            eyeConfidence: 0.91d,
            mouthConfidence: 0.89d,
            source: "identity mismatch",
            faceHalfWidth: 0.16d,
            faceHalfHeight: 0.36d,
            eyeCenterOffset: 0.105d,
            eyeHalfWidth: 0.035d,
            mouthHalfWidth: 0.125d,
            eyeCenterY: 0.35d,
            mouthCenterY: 0.68d);
        var nonSubjectMetrics = identityCalculator.Update(nonSubjectFrame);
        var nonSubjectStability = identityStability.Update(
            new FaceFeatureDetection
            {
                HasFace = true,
                FaceBox = new WpfRect(0.34d, 0.12d, 0.32d, 0.72d),
                TrackingConfidence = 0.92d,
                EyeConfidence = 0.91d,
                MouthConfidence = 0.89d,
                Source = "identity mismatch face box"
            },
            nonSubjectFrame,
            nonSubjectMetrics);
        nonSubjectUpdate = identityBuilder.Update(nonSubjectFrame, nonSubjectMetrics, nonSubjectStability, cueAnalysis: null, trendAnalysis: null);
    }

    Require(!nonSubjectUpdate.Accepted && nonSubjectUpdate.RejectionKind == PersonalFaceModelRejectionKind.SubjectMismatch, $"identity gate did not reject a synthetic non-subject: {nonSubjectUpdate.RejectionKind}, {nonSubjectUpdate.Reason}");
    Require(nonSubjectUpdate.IdentityAnalysis is { AutoGateReady: true, ConfidencePercent: < 28d }, $"identity gate did not report low confidence: {nonSubjectUpdate.IdentityAnalysis?.ConfidencePercent}");
    Require(nonSubjectUpdate.Model.SubjectMismatchRejectedSamples >= 1, "personal model did not count subject mismatch rejection");
}

static void RunPersonalFaceSurfaceProfileSmoke()
{
    var builder = new PersonalFaceModelBuilder();
    var stabilityAnalyzer = new FaceLockStabilityAnalyzer();
    var calculator = new FaceLandmarkMetricCalculator();
    var startedAt = DateTime.UtcNow;
    PersonalFaceModelUpdate update = new(false, PersonalFaceModelRejectionKind.NoFace, "not started", 0d, builder.CurrentModel);

    for (var index = 0; index < 24; index++)
    {
        var frame = CreateSyntheticDenseMeshFrame(
            scale: 0.94d + index % 5 * 0.025d,
            matrixYawDegrees: -10d + index % 6 * 4d,
            matrixPitchDegrees: -4d + index % 4 * 2d,
            matrixRollDegrees: -3d + index % 3 * 3d,
            capturedAtUtc: startedAt.AddSeconds(index * 0.4d));
        var metrics = calculator.Update(frame);
        var stability = stabilityAnalyzer.Update(
            new FaceFeatureDetection
            {
                HasFace = true,
                FaceBox = new WpfRect(0.28d, 0.18d, 0.44d, 0.64d),
                TrackingConfidence = frame.TrackingConfidence,
                EyeConfidence = frame.EyeConfidence,
                MouthConfidence = frame.MouthConfidence,
                Source = "surface profile smoke dense face box"
            },
            frame,
            metrics);
        update = builder.Update(frame, metrics, stability, cueAnalysis: null, trendAnalysis: null);
    }

    var model = update.Model;
    Require(model.LeftBrowShape.SampleCount >= 12 && model.LeftBrowShape.Points.Count == 10, "personal model did not learn aggregate left-brow 3D surface profile");
    Require(model.RightBrowShape.SampleCount >= 12 && model.RightBrowShape.Points.Count == 10, "personal model did not learn aggregate right-brow 3D surface profile");
    Require(model.NoseBridgeShape.SampleCount >= 12 && model.NoseBridgeShape.Points.Count == 10, "personal model did not learn aggregate nose-bridge 3D surface profile");
    Require(model.NoseBaseShape.SampleCount >= 12 && model.NoseBaseShape.Points.Count == 5, "personal model did not learn aggregate nose-base 3D surface profile");
    Require(model.LeftCheekSurface.SampleCount >= 12 && model.LeftCheekSurface.Points.Count == 6, "personal model did not learn aggregate left-cheek 3D surface profile");
    Require(model.RightCheekSurface.SampleCount >= 12 && model.RightCheekSurface.Points.Count == 6, "personal model did not learn aggregate right-cheek 3D surface profile");
    Require(model.ForeheadSurface.SampleCount >= 12 && model.ForeheadSurface.Points.Count == 9, "personal model did not learn aggregate forehead 3D surface profile");
    Require(model.NoseBridgeShape.Points.Any(static point => point.Z.SampleCount > 0 && point.Z.Average.HasValue), "nose bridge surface profile did not retain weighted Z depth evidence");
    Require(model.LeftCheekSurface.Points.Any(static point => point.Z.SampleCount > 0 && point.Z.Average.HasValue), "cheek surface profile did not retain weighted Z depth evidence");

    var motionModel = new PersonalFaceMotionModel
    {
        SubjectId = model.SubjectId,
        SubjectDisplayName = model.SubjectDisplayName,
        CreatedAtUtc = model.CreatedAtUtc,
        UpdatedAtUtc = model.UpdatedAtUtc
    };
    var readiness = new PersonalFaceCorpusReadinessBuilder().Build(model, motionModel, [], measurementJournalBytes: 0L);
    Require(readiness.SurfaceShapeCoveragePercent > 0d, "learning-data health did not score dense surface shape coverage");
    Require(readiness.XYZABCCoveragePercent > 0d, "learning-data health did not score XYZABC pose/distance coverage for moving dense samples");
    Require(readiness.NoseBridgeShapeSamples == model.NoseBridgeShape.SampleCount, "learning-data health did not report nose bridge surface samples");

    var gate = FaceReconstructionSubjectGate.FromPersonalModel(model, manualSubjectConfirmed: true, identityConfidencePercent: 96d);
    var preview = new MeasurementFacePreviewBuilder().Build(model, gate);
    Require(preview.ContourShapeProfiles.ContainsKey("nose_bridge_shape"), "measurement face preview omitted nose bridge surface profile");
    Require(preview.ContourShapeProfiles.ContainsKey("forehead_surface"), "measurement face preview omitted forehead surface profile");
    Require(preview.Points.Any(static point => point.Role == "nose" && point.Provenance.Contains("personal", StringComparison.OrdinalIgnoreCase)), "measurement face preview did not render measured nose surface points");
    Require(preview.Points.Any(static point => point.Role == "cheek"), "measurement face preview did not render measured cheek surface points");

    var package = new MeasurementAvatarTrainingPackageBuilder().Build(model, motionModel, readiness, gate, measurementJournalBytes: 0L);
    Require(package.Readiness.SurfaceShapeCoveragePercent > 0d, "avatar package omitted surface shape readiness score");
    Require(package.ContourShapeProfiles.ContainsKey("nose_bridge_shape"), "avatar package omitted nose bridge surface profile");
    Require(package.ContourShapeProfiles.ContainsKey("left_cheek_surface"), "avatar package omitted cheek surface profile");
    Require(package.IntegrationNotes.Any(static note => note.Contains("surface shape profiles", StringComparison.OrdinalIgnoreCase)), "avatar package omitted surface-profile integration guidance");
}

static void RequireBoundedAverageMovement(
    string label,
    double? beforeAverage,
    double? newObservation,
    double? afterAverage,
    double maximumInfluencePercent)
{
    Require(beforeAverage.HasValue, $"{label} drift check missing before average");
    Require(newObservation.HasValue, $"{label} drift check missing new observation");
    Require(afterAverage.HasValue, $"{label} drift check missing after average");
    var denominator = Math.Abs(newObservation!.Value - beforeAverage!.Value);
    if (denominator <= 0.000001d)
    {
        return;
    }

    var actualInfluencePercent = Math.Abs(afterAverage!.Value - beforeAverage.Value) / denominator * 100d;
    Require(
        actualInfluencePercent <= maximumInfluencePercent + 0.20d,
        $"{label} average moved {actualInfluencePercent:0.###}% toward one outlier, above reported {maximumInfluencePercent:0.###}% max influence");
}

static void RunPersonalFaceCaptureQualitySmoke()
{
    var analyzer = new PersonalFaceCaptureQualityAnalyzer();
    var calculator = new FaceLandmarkMetricCalculator();
    var timestamp = DateTime.UtcNow;
    var strongFrame = CreateSyntheticLandmarkFrame(
        timestamp,
        leftEyeRatio: 0.30d,
        rightEyeRatio: 0.29d,
        mouthRatio: 0.08d,
        eyeConfidence: 0.92d,
        mouthConfidence: 0.90d,
        source: "capture quality strong");
    var strongMetrics = calculator.Update(strongFrame);
    var strongStability = new FaceLockStabilityAnalysis
    {
        SampleCount = 12,
        WindowSeconds = 4d,
        FaceBoundsRatePercent = 100d,
        FaceContinuityPercent = 92d,
        EyeUsableRatePercent = 100d,
        MouthUsableRatePercent = 100d,
        AverageEyeQualityPercent = strongMetrics.EyeMeasurementQualityPercent,
        AverageMouthQualityPercent = strongMetrics.MouthMeasurementQualityPercent,
        AverageOverallQualityPercent = strongMetrics.OverallMeasurementQualityPercent,
        EyeReliabilityPercent = 90d,
        MouthReliabilityPercent = 88d,
        CompositeReliabilityPercent = 91d
    };
    var acceptedUpdate = new PersonalFaceModelUpdate(
        true,
        PersonalFaceModelRejectionKind.None,
        "accepted",
        1d,
        new PersonalFaceModel
        {
            SubjectId = "chris",
            SubjectDisplayName = "Chris",
            ObservedSamples = 120,
            AcceptedSamples = 90
        });
    var strong = analyzer.Analyze(new PersonalFaceCaptureQualityInput
    {
        VideoWidth = 3840,
        VideoHeight = 2160,
        FramesPerSecond = 30d,
        InputFormat = "MJPEG",
        LandmarkFrame = strongFrame,
        Metrics = strongMetrics,
        Stability = strongStability,
        PersonalModelUpdate = acceptedUpdate
    });
    Require(strong.CanCollectMeasurements, $"strong capture quality did not allow measurement collection: {strong.StatusLine}");
    Require(strong.StrongEnoughForAvatarLearning, $"strong capture quality was not avatar-grade: {strong.StatusLine}");
    Require(strong.FaceWidthPercent is > 35d and < 55d, $"strong capture face scale was not measured: {strong.FaceWidthPercent}");
    Require(strong.Issues.Count == 0, $"strong capture quality unexpectedly reported issues: {string.Join("; ", strong.Issues)}");

    var lowCameraMode = analyzer.Analyze(new PersonalFaceCaptureQualityInput
    {
        VideoWidth = 640,
        VideoHeight = 360,
        FramesPerSecond = 12d,
        InputFormat = "synthetic",
        LandmarkFrame = strongFrame,
        Metrics = strongMetrics,
        Stability = strongStability,
        PersonalModelUpdate = acceptedUpdate
    });
    Require(!lowCameraMode.CanCollectMeasurements, $"low camera mode allowed long-term collection: {lowCameraMode.StatusLine}");
    Require(!lowCameraMode.StrongEnoughForAvatarLearning, "low camera mode was avatar-grade");
    Require(lowCameraMode.Issues.Any(issue => issue.Contains("low resolution", StringComparison.OrdinalIgnoreCase)), $"low camera mode did not report resolution issue: {string.Join("; ", lowCameraMode.Issues)}");
    Require(lowCameraMode.Issues.Any(issue => issue.Contains("frame rate", StringComparison.OrdinalIgnoreCase)), $"low camera mode did not report frame-rate issue: {string.Join("; ", lowCameraMode.Issues)}");

    var weakFrame = CreateSyntheticLandmarkFrame(
        timestamp.AddSeconds(1),
        leftEyeRatio: 0.20d,
        rightEyeRatio: 0.12d,
        mouthRatio: 0.05d,
        eyeConfidence: 0.42d,
        mouthConfidence: 0.38d,
        source: "capture quality weak",
        faceHalfWidth: 0.045d,
        faceHalfHeight: 0.070d,
        eyeCenterOffset: 0.020d,
        eyeHalfWidth: 0.012d,
        mouthHalfWidth: 0.022d);
    var weakMetrics = calculator.Update(weakFrame);
    var weak = analyzer.Analyze(new PersonalFaceCaptureQualityInput
    {
        VideoWidth = 640,
        VideoHeight = 480,
        FramesPerSecond = 8d,
        InputFormat = "unknown",
        LandmarkFrame = weakFrame,
        Metrics = weakMetrics,
        Stability = FaceLockStabilityAnalysis.Waiting,
        PersonalModelUpdate = new PersonalFaceModelUpdate(
            false,
            PersonalFaceModelRejectionKind.LowQuality,
            "synthetic low-quality capture",
            0d,
            new PersonalFaceModel())
    });
    Require(!weak.CanCollectMeasurements, $"weak capture quality allowed collection: {weak.StatusLine}");
    Require(!weak.StrongEnoughForAvatarLearning, "weak capture quality was avatar-grade");
    Require(weak.Issues.Any(issue => issue.Contains("low resolution", StringComparison.OrdinalIgnoreCase)), $"weak capture did not flag low resolution: {string.Join("; ", weak.Issues)}");
    Require(weak.Issues.Any(issue => issue.Contains("small", StringComparison.OrdinalIgnoreCase)), $"weak capture did not flag small face: {string.Join("; ", weak.Issues)}");
    Require(weak.Suggestions.Count > 0, "weak capture did not suggest a correction");
}

static void RunHeadPoseEstimatorSmoke()
{
    var estimator = new HeadPoseEstimator();
    var calibration = new HeadPoseCalibration
    {
        CameraHorizontalFovDegrees = 71.4d,
        ReferenceInterEyeFrameWidth = 0.16d,
        ReferenceSampleCount = 48,
        ReferenceSource = "learned synthetic face scale"
    };
    var nearFrame = CreateSyntheticDenseMeshFrame(scale: 1.18d, matrixYawDegrees: 14d);
    var farFrame = CreateSyntheticDenseMeshFrame(scale: 0.72d, matrixYawDegrees: 14d);
    var flatNeutralFrame = CreateSyntheticDenseMeshFrame();
    var pitchFrame = CreateSyntheticDenseMeshFrame(matrixPitchDegrees: -6d);
    var rollFrame = CreateSyntheticDenseMeshFrame(matrixRollDegrees: 3d);
    var denseYawFrame = CreateSyntheticDenseYawEvidenceFrame();
    var near = estimator.Estimate(new HeadPoseEstimatorInput
    {
        Frame = nearFrame,
        FrameWidthPixels = 3840,
        FrameHeightPixels = 2160,
        Calibration = calibration
    });
    var far = estimator.Estimate(new HeadPoseEstimatorInput
    {
        Frame = farFrame,
        FrameWidthPixels = 3840,
        FrameHeightPixels = 2160,
        Calibration = calibration
    });
    var flatNeutral = estimator.Estimate(new HeadPoseEstimatorInput
    {
        Frame = flatNeutralFrame,
        FrameWidthPixels = 3840,
        FrameHeightPixels = 2160,
        Calibration = calibration
    });
    var pitch = estimator.Estimate(new HeadPoseEstimatorInput
    {
        Frame = pitchFrame,
        FrameWidthPixels = 3840,
        FrameHeightPixels = 2160,
        Calibration = calibration
    });
    var roll = estimator.Estimate(new HeadPoseEstimatorInput
    {
        Frame = rollFrame,
        FrameWidthPixels = 3840,
        FrameHeightPixels = 2160,
        Calibration = calibration
    });
    var denseYaw = estimator.Estimate(new HeadPoseEstimatorInput
    {
        Frame = denseYawFrame,
        FrameWidthPixels = 3840,
        FrameHeightPixels = 2160,
        Calibration = calibration
    });

    Require(near.HasFace && far.HasFace, "head pose estimator did not accept synthetic dense faces");
    Require(near.FaceFillWidthPercent > far.FaceFillWidthPercent, "head pose face fill did not increase for the closer/larger synthetic face");
    Require(near.ApparentDistanceUnits < far.ApparentDistanceUnits, "head pose apparent Z units did not decrease as face fill increased");
    Require(near.ZRelativeToReference is < 1d, $"head pose relative Z did not show closer-than-reference face: {near.ZRelativeToReference}");
    Require(far.ZRelativeToReference is > 1d, $"head pose relative Z did not show farther-than-reference face: {far.ZRelativeToReference}");
    Require(near.ZConfidencePercent >= 70d, $"head pose Z confidence was too low: {near.ZConfidencePercent}");
    Require(near.ZUsesCameraFov, "head pose Z did not report camera-FOV evidence");
    Require(near.ZUsesLearnedReference, "head pose Z did not report learned-reference evidence");
    Require(near.ZEstimateKind.Contains("camera-fov", StringComparison.OrdinalIgnoreCase), $"head pose Z estimate kind was unexpected: {near.ZEstimateKind}");
    Require(near.ZQualityLabel.Contains("Z", StringComparison.Ordinal), $"head pose Z quality label was missing: {near.ZQualityLabel}");
    Require(near.ReferenceScaleSource.Contains("synthetic", StringComparison.OrdinalIgnoreCase), $"head pose relative Z did not retain reference source: {near.ReferenceScaleSource}");
    Require(near.XHorizontalPercent is > 45d and < 55d, $"head pose X center was implausible: {near.XHorizontalPercent}");
    Require(near.YVerticalPercent is > 45d and < 55d, $"head pose Y center was implausible: {near.YVerticalPercent}");
    Require(Math.Abs(near.BRotationAroundYDegrees - 14d) < 0.75d, $"head pose B/Y rotation was wrong: {near.BRotationAroundYDegrees}");
    Require(Math.Abs(flatNeutral.BRotationAroundYDegrees) < 1d && Math.Abs(flatNeutral.ARotationAroundXDegrees) < 1d, $"head pose dense fallback biased a flat neutral matrix: A={flatNeutral.ARotationAroundXDegrees}, B={flatNeutral.BRotationAroundYDegrees}");
    Require(Math.Abs(pitch.ARotationAroundXDegrees - -6d) < 0.75d, $"head pose A/X rotation was wrong: {pitch.ARotationAroundXDegrees}");
    Require(Math.Abs(roll.CRotationAroundZDegrees - 3d) < 0.75d, $"head pose C/Z rotation was wrong: {roll.CRotationAroundZDegrees}");
    Require(denseYaw.BRotationAroundYDegrees >= 10d, $"head pose dense fallback did not recover B/Y rotation from mesh geometry: {denseYaw.BRotationAroundYDegrees}");
    Require(denseYaw.RotationSource.Contains("dense mesh geometry", StringComparison.OrdinalIgnoreCase), $"head pose dense fallback did not report its source: {denseYaw.RotationSource}");
    Require(near.DistanceSource.Contains("71.4", StringComparison.Ordinal), $"head pose did not use Link 2 Pro horizontal FOV: {near.DistanceSource}");
    Require(near.ScaleCaveat.Contains("Zoom", StringComparison.OrdinalIgnoreCase), "head pose did not include zoom caveat");
    Require(near.StatusLine.Contains("Z ref", StringComparison.Ordinal), "head pose status did not report reference-scaled Z");
    Require(near.StatusLine.Contains("A around X", StringComparison.Ordinal), "head pose status did not use XYZABC rotation labels");
    Require(near.HasSolvePnPPose, $"head pose did not produce OpenCV solvePnP second-opinion pose: {near.PoseSolverStatus}");
    Require(near.SolvePnPReprojectionErrorPixels is >= 0d, $"head pose solvePnP did not expose reprojection error: {near.SolvePnPReprojectionErrorPixels}");
    Require(near.PoseSolverStatus.Contains("solvePnP", StringComparison.Ordinal) && near.PoseSolverStatus.Contains("A/B/C", StringComparison.Ordinal), $"head pose solvePnP status was not actionable: {near.PoseSolverStatus}");
    Require(near.StatusLine.Contains("solvePnP", StringComparison.Ordinal), "head pose status line omitted solvePnP comparison");
}

static void RunStoredHeadPoseRoutingSmoke()
{
    var capturedAt = DateTime.UtcNow;
    var frame = CreateSyntheticLandmarkFrame(
        capturedAt,
        leftEyeRatio: 0.27d,
        rightEyeRatio: 0.26d,
        mouthRatio: 0.08d,
        eyeConfidence: 0.90d,
        mouthConfidence: 0.88d,
        source: "stored head pose routing smoke",
        headYawDegrees: 0d,
        headPitchDegrees: 0d,
        headRollDegrees: 0d);
    var metrics = new FaceLandmarkMetricCalculator().Update(frame);
    var stability = new FaceLockStabilityAnalysis
    {
        SampleCount = 6,
        WindowSeconds = 3d,
        FaceBoundsRatePercent = 100d,
        FaceContinuityPercent = 94d,
        EyeUsableRatePercent = 100d,
        MouthUsableRatePercent = 100d,
        AverageEyeQualityPercent = metrics.EyeMeasurementQualityPercent,
        AverageMouthQualityPercent = metrics.MouthMeasurementQualityPercent,
        AverageOverallQualityPercent = metrics.OverallMeasurementQualityPercent,
        EyeReliabilityPercent = 93d,
        MouthReliabilityPercent = 91d,
        CompositeReliabilityPercent = 94d
    };
    var pose = new HeadPoseEstimate
    {
        HasFace = true,
        CapturedAtUtc = capturedAt,
        YawDegrees = 16d,
        PitchDegrees = -9d,
        RollDegrees = 4d,
        ApparentDistanceUnits = 4.8d,
        RelativeDistanceScale = 1.05d,
        ZConfidencePercent = 88d,
        ZEstimateKind = "pose-routing-smoke",
        RotationSource = "pose routing smoke"
    };
    var builder = new PersonalFaceModelBuilder();
    var update = builder.Update(frame, metrics, stability, cueAnalysis: null, trendAnalysis: null, pose);
    Require(update.Accepted, $"personal model rejected stored-pose routing smoke: {update.Reason}");
    Require(MatchesAverage(update.Model.HeadYawDegrees, 16d), $"personal model stored stale B/Y rotation: {update.Model.HeadYawDegrees.Average}");
    Require(MatchesAverage(update.Model.HeadPitchDegrees, -9d), $"personal model stored stale A/X rotation: {update.Model.HeadPitchDegrees.Average}");
    Require(MatchesAverage(update.Model.HeadRollDegrees, 4d), $"personal model stored stale C/Z rotation: {update.Model.HeadRollDegrees.Average}");
    Require(
        update.Model.PoseBuckets.Any(static bucket =>
            bucket.BucketId == PersonalFacePoseBuckets.YawPositive
            && bucket.SampleCount == 1
            && MatchesAverage(bucket.HeadYawDegrees, 16d)),
        "personal model did not route stored B/Y rotation into the positive-B pose bucket");
    Require(
        update.Model.PoseBuckets.Any(static bucket =>
            bucket.BucketId == PersonalFacePoseBuckets.PitchNegative
            && bucket.SampleCount == 1
            && MatchesAverage(bucket.HeadPitchDegrees, -9d)),
        "personal model did not route stored A/X rotation into the negative-A pose bucket");

    var captureQuality = new PersonalFaceCaptureQualityAssessment
    {
        Label = "avatar-grade",
        ScorePercent = 92d,
        CanCollectMeasurements = true,
        StrongEnoughForAvatarLearning = true,
        PrimaryReason = "stored head pose routing smoke"
    };
    var sample = PersonalFaceMeasurementSample.Create(update, frame, metrics, stability, captureQuality, pose);
    Require(Math.Abs(sample.HeadYawDegrees - 16d) < 0.001d, $"measurement sample stored stale B/Y rotation: {sample.HeadYawDegrees}");
    Require(sample.PoseBucketIds.Contains(PersonalFacePoseBuckets.YawPositive), "measurement sample did not retain positive-B bucket id");

    var observation = PersonalFaceMotionObservation.Create(
        update.Model.SubjectId,
        update.Model.SubjectDisplayName,
        update.Model.SubjectCollectionMode,
        capturedAt,
        update.SampleWeight,
        metrics,
        stability,
        acceptedForPersonalModel: true,
        source: "stored head pose routing smoke",
        pose);
    Require(Math.Abs(observation.HeadYawDegrees - 16d) < 0.001d, $"motion observation stored stale B/Y rotation: {observation.HeadYawDegrees}");

    var journalRoot = Path.Combine(Path.GetTempPath(), $"AvatarBuilderStoredPoseSmoke-{Guid.NewGuid():N}");
    try
    {
        var journal = new PersonalFaceMeasurementJournal(TimeSpan.Zero, 1_000_000L);
        var journalPath = journal.WriteAcceptedSampleIfDue(journalRoot, update, frame, metrics, stability, captureQuality, pose);
        Require(File.Exists(journalPath), "measurement journal did not write stored-pose sample");
        var journalSample = PersonalFaceMeasurementJournal.ReadRecentSamples(journalRoot, 10).Single();
        Require(Math.Abs(journalSample.HeadYawDegrees - 16d) < 0.001d, $"measurement journal stored stale B/Y rotation: {journalSample.HeadYawDegrees}");
    }
    finally
    {
        if (Directory.Exists(journalRoot))
        {
            Directory.Delete(journalRoot, recursive: true);
        }
    }
}

static bool MatchesAverage(PersonalMetricDistribution distribution, double expected, double tolerance = 0.001d)
{
    return distribution.Average is double average && Math.Abs(average - expected) <= tolerance;
}

static void RunPersonalFaceMotionModelSmoke()
{
    var startedAt = DateTime.UtcNow;
    var observations = Enumerable.Range(0, 48)
        .Select(index =>
        {
            var progress = index / 47d;
            return new PersonalFaceMotionObservation
            {
                SubjectId = "chris",
                SubjectDisplayName = "Chris",
                SubjectCollectionMode = PersonalFaceSubject.ManualConfirmationMode,
                CapturedAtUtc = startedAt.AddMilliseconds(index * 250d),
                AcceptedForPersonalModel = index < 24,
                Source = "synthetic motion smoke",
                SampleWeight = 1d,
                OverallQualityPercent = 88d,
                FaceReliabilityPercent = 90d,
                FaceContinuityPercent = 92d,
                EyeReliabilityPercent = 89d,
                MouthReliabilityPercent = 87d,
                HeadYawDegrees = -8d + progress * 16d,
                HeadPitchDegrees = 2d + progress * 4d,
                HeadRollDegrees = -1d + progress * 2d,
                AverageEyeOpeningRatio = 0.26d - progress * 0.16d,
                MouthOpeningRatio = 0.05d + progress * 0.22d,
                JawDroopRatio = 0.08d + progress * 0.18d,
                MediaPipeAverageEyeBlinkPercent = progress * 78d,
                MediaPipeJawOpenPercent = progress * 72d,
                MediaPipeMouthClosePercent = 82d - progress * 55d,
                EyeArtifactSuppressed = index is 17 or 31,
                AnyEyeReconstructed = index is 18 or 32,
                MouthReconstructed = index == 33
            };
        })
        .ToList();

    var model = new PersonalFaceMotionModelBuilder().Build(observations);
    Require(model.SubjectId == "chris", "personal face motion model lost subject id");
    Require(model.UsableObservationCount == observations.Count, $"personal face motion model usable count was wrong: {model.UsableObservationCount}");
    Require(model.MotionPairCount >= 40, $"personal face motion model pair count was too low: {model.MotionPairCount}");
    Require(model.EyeClosingVelocityPerSecond.Average is > 0d, "personal face motion model did not measure eye-closing velocity");
    Require(model.MouthOpeningVelocityPerSecond.Average is > 0d, "personal face motion model did not measure mouth-opening velocity");
    Require(model.JawDroopVelocityPerSecond.Average is > 0d, "personal face motion model did not measure jaw-droop velocity");
    Require(model.EyeClosingWithMouthOpeningRate > 0.9d, $"eye/mouth coupling rate was too low: {model.EyeClosingWithMouthOpeningRate}");
    Require(model.EyeClosingWithJawDroopRate > 0.9d, $"eye/jaw coupling rate was too low: {model.EyeClosingWithJawDroopRate}");

    var root = Path.Combine(Path.GetTempPath(), $"AvatarBuilderMotionModelSmoke-{Guid.NewGuid():N}");
    var path = new PersonalFaceMotionModelStore().Write(root, model);
    Require(File.Exists(path), "personal face motion model JSON was not written");
    var json = File.ReadAllText(path);
    Require(json.Contains("personal-face-motion-model-v1", StringComparison.Ordinal), "personal face motion model JSON did not include schema");
    Require(!json.Contains("FaceContour", StringComparison.Ordinal) && !json.Contains("data:image", StringComparison.OrdinalIgnoreCase), "personal face motion model leaked raw contour/image data");
    Directory.Delete(root, recursive: true);
}

static void RunFaceReconstructionContractSmoke()
{
    var model = new PersonalFaceModel
    {
        SubjectId = "chris",
        SubjectDisplayName = "Chris",
        SubjectCollectionMode = PersonalFaceSubject.ManualConfirmationMode,
        UnknownSubjectPolicy = PersonalFaceSubject.UnknownSubjectPolicy,
        AcceptedSamples = 80,
        ObservedSamples = 120
    };
    var gate = FaceReconstructionSubjectGate.FromPersonalModel(
        model,
        manualSubjectConfirmed: true,
        identityConfidencePercent: 96d);
    Require(gate.GateDecision == "accepted", $"face reconstruction subject gate did not accept confirmed subject: {gate.GateDecision}");
    Require(gate.UnknownSubjectPolicy == PersonalFaceSubject.UnknownSubjectPolicy, "face reconstruction gate lost reject-unknown policy");

    var blockedGate = FaceReconstructionSubjectGate.FromPersonalModel(
        model,
        manualSubjectConfirmed: false,
        identityConfidencePercent: 96d);
    Require(blockedGate.GateDecision == "paused", "face reconstruction gate accepted an unconfirmed subject");

    var jobRoot = Path.Combine(Path.GetTempPath(), $"AvatarBuilderReconstructionJobSmoke-{Guid.NewGuid():N}");
    var store = new FaceReconstructionJobStore();
    var sourceFrames = new[]
    {
        new FaceReconstructionSourceFrame
        {
            SourceKind = FaceReconstructionSourceKinds.ExplicitTrainingImage,
            SourcePath = Path.Combine(jobRoot, "training", "frame_0001.png"),
            Width = 3840,
            Height = 2160,
            FaceReliabilityPercent = 94d,
            OverallQualityPercent = 88d,
            GlassesVisible = true,
            Notes = "explicit avatar-training still"
        }
    };

    var threeDdfaJob = ThreeDdfaOnnxReconstructionSpec.CreateWorkItem(
        gate,
        personalFaceModelPath: Path.Combine(jobRoot, "personal_face_model.json"),
        measurementJournalFolder: Path.Combine(jobRoot, "measurements"),
        outputFolder: Path.Combine(jobRoot, "three-ddfa"),
        sourceFrames: sourceFrames);
    Require(threeDdfaJob.BackendId == FaceReconstructionBackendIds.ThreeDdfaV2OnnxReconstruction, "3DDFA work item used the wrong backend id");
    Require(threeDdfaJob.RequestedOutputs.Contains("dense_3d_face_vertices", StringComparer.OrdinalIgnoreCase), "3DDFA work item did not request dense vertices");
    Require(threeDdfaJob.RequestedOutputs.Contains("head_pose_xyzabc", StringComparer.OrdinalIgnoreCase), "3DDFA work item did not request XYZABC pose");

    var threeDdfaPath = store.Write(jobRoot, threeDdfaJob, ThreeDdfaOnnxReconstructionSpec.WorkItemFileName);
    Require(File.Exists(threeDdfaPath), "3DDFA reconstruction job manifest was not written");
    var threeDdfaJson = File.ReadAllText(threeDdfaPath);
    Require(threeDdfaJson.Contains(FaceReconstructionBackendIds.ThreeDdfaV2OnnxReconstruction, StringComparison.Ordinal), "3DDFA job did not declare the ONNX backend");
    Require(threeDdfaJson.Contains("dense_3d_face_vertices", StringComparison.Ordinal), "3DDFA job did not serialize dense vertex output");
    Require(threeDdfaJson.Contains("\"SubjectId\": \"chris\"", StringComparison.Ordinal), "3DDFA job did not preserve subject id");
    Require(threeDdfaJson.Contains("\"StoresRawContinuousVideo\": false", StringComparison.Ordinal), "3DDFA job did not keep raw continuous video disabled");
    Require(!threeDdfaJson.Contains("FaceContour", StringComparison.Ordinal), "3DDFA job leaked raw contour data");

    var roundTrip = store.Read(threeDdfaPath);
    Require(roundTrip.SubjectGate.SubjectId == "chris", "3DDFA job round-trip lost subject id");
    Require(roundTrip.SourceFrames.Count == 1, $"3DDFA job round-trip lost source frames: {roundTrip.SourceFrames.Count}");

    var modelInfo = ThreeDdfaOnnxModelInfo.Load();
    Require(modelInfo.ManifestPath.EndsWith("three_ddfa_onnx_manifest.json", StringComparison.OrdinalIgnoreCase), "3DDFA model info used the wrong manifest name");
    Require(modelInfo.Backend.Contains("3DDFA", StringComparison.OrdinalIgnoreCase), "3DDFA model info did not identify the backend");
    Require(modelInfo.Status.Contains("3DDFA", StringComparison.OrdinalIgnoreCase), "3DDFA model status did not explain the lane state");
    Directory.Delete(jobRoot, recursive: true);
}

static void RunMeasurementFacePreviewSmoke()
{
    var model = new PersonalFaceModel
    {
        SubjectId = "chris",
        SubjectDisplayName = "Chris",
        SubjectCollectionMode = PersonalFaceSubject.ManualConfirmationMode,
        UnknownSubjectPolicy = PersonalFaceSubject.UnknownSubjectPolicy,
        ObservedSamples = 140,
        AcceptedSamples = 96,
        RejectedSamples = 44,
        AcceptedSampleWeight = 82d,
        AverageFaceReliabilityPercent = 90d,
        AverageFaceContinuityPercent = 86d,
        AverageEyeReliabilityPercent = 88d,
        AverageMouthReliabilityPercent = 84d,
        FaceCenterX = Distribution(0.51d),
        FaceCenterY = Distribution(0.47d),
        FaceWidth = Distribution(0.40d),
        FaceHeight = Distribution(0.58d),
        HeadYawDegrees = Distribution(2.4d),
        HeadPitchDegrees = Distribution(-1.2d),
        HeadRollDegrees = Distribution(0.8d),
        LeftEyeOpeningRatio = Distribution(0.27d),
        RightEyeOpeningRatio = Distribution(0.25d),
        AverageEyeOpeningRatio = Distribution(0.26d),
        EyeAgreementPercent = Distribution(92d),
        MouthOpeningRatio = Distribution(0.08d),
        JawDroopRatio = Distribution(0.025d),
        MediaPipeAverageEyeBlinkPercent = Distribution(18d),
        MediaPipeJawOpenPercent = Distribution(7d),
        MediaPipeMouthClosePercent = Distribution(80d),
        EyeGlarePercent = Distribution(11d),
        EyeContrastPercent = Distribution(68d),
        EyeSharpnessPercent = Distribution(74d),
        LeftEyeShape = ShapeProfile("left_eye_shape", "Left eye contour shape", 8, closed: true),
        RightEyeShape = ShapeProfile("right_eye_shape", "Right eye contour shape", 8, closed: true),
        OuterLipShape = ShapeProfile("outer_lip_shape", "Outer lip contour shape", 12, closed: true),
        InnerLipShape = ShapeProfile("inner_lip_shape", "Inner lip contour shape", 10, closed: true),
        JawShape = ShapeProfile("jaw_shape", "Jaw contour shape", 9, closed: false),
        LeftBrowShape = SurfaceProfile("left_brow_shape", "Left brow 3D shape", 10, depth: 0.035d),
        RightBrowShape = SurfaceProfile("right_brow_shape", "Right brow 3D shape", 10, depth: 0.035d),
        NoseBridgeShape = SurfaceProfile("nose_bridge_shape", "Nose bridge 3D shape", 10, depth: 0.09d),
        NoseBaseShape = SurfaceProfile("nose_base_shape", "Nose base 3D shape", 5, depth: 0.065d),
        LeftCheekSurface = SurfaceProfile("left_cheek_surface", "Left cheek 3D surface", 6, depth: 0.025d),
        RightCheekSurface = SurfaceProfile("right_cheek_surface", "Right cheek 3D surface", 6, depth: 0.025d),
        ForeheadSurface = SurfaceProfile("forehead_surface", "Forehead 3D surface", 9, depth: 0.02d),
        PoseBuckets = PoseBucketProfiles()
    };
    var acceptedGate = FaceReconstructionSubjectGate.FromPersonalModel(
        model,
        manualSubjectConfirmed: true,
        identityConfidencePercent: 95d);
    var builder = new MeasurementFacePreviewBuilder();
    var preview = builder.Build(model, acceptedGate);
    Require(preview.CanRender, "measurement face preview did not render with an accepted subject gate");
    Require(preview.BackendId == FaceReconstructionBackendIds.MeasurementOnlyPreview, "measurement face preview used the wrong backend id");
    Require(preview.ConfidencePercent > 45d, $"measurement face preview confidence too low: {preview.ConfidencePercent}");
    Require(preview.MeasurementContributionPercent > 90d, $"measurement face preview measured contribution too low: {preview.MeasurementContributionPercent}");
    Require(preview.TemplatePriorContributionPercent < 10d, $"measurement face preview retained too much template prior: {preview.TemplatePriorContributionPercent}");
    Require(preview.Points.Count >= 70, $"measurement face preview geometry was too sparse: {preview.Points.Count}");
    Require(preview.Points.Any(static point => point.Provenance.Contains("personal", StringComparison.OrdinalIgnoreCase)), "measurement face preview points omitted provenance");
    Require(preview.ContourShapeProfiles.ContainsKey("left_eye_shape"), "measurement face preview omitted aggregate contour shape profiles");
    Require(preview.ContourShapeProfiles.ContainsKey("nose_bridge_shape"), "measurement face preview omitted aggregate nose bridge surface profile");
    Require(preview.Points.Any(static point => point.Role == "nose" && point.Provenance.Contains("personal", StringComparison.OrdinalIgnoreCase)), "measurement face preview did not render measured nose surface geometry");
    Require(preview.Points.Any(static point => point.Role == "cheek" && point.Provenance.Contains("personal", StringComparison.OrdinalIgnoreCase)), "measurement face preview did not render measured cheek surface geometry");
    Require(preview.PoseBuckets.Count >= PersonalFacePoseBuckets.Definitions.Count, "measurement face preview omitted pose bucket summaries");
    Require(preview.PoseBuckets.Any(static bucket => bucket.BucketId == PersonalFacePoseBuckets.FrontNeutral && bucket.SampleCount > 0), "measurement face preview omitted front-neutral pose bucket summary");
    Require(preview.PoseBuckets.Any(static bucket => bucket.BucketId == PersonalFacePoseBuckets.YawNegative && Math.Abs(bucket.HeadYawDegrees) > 5d), "measurement face preview omitted turned-head pose bucket summary");
    Require(preview.PoseBucketConsistency.ComparedPoseBucketCount > 0, "measurement face preview omitted pose-bucket consistency comparisons");
    Require(preview.PoseBucketConsistency.HealthPercent > 0d, "measurement face preview omitted pose-bucket consistency health");
    Require(preview.SurfaceEvidence.Count >= 6, "measurement face preview omitted surface-confidence evidence");
    Require(preview.SurfaceEvidence.Any(static surface => surface.RegionId == "nose_projection" && surface.DepthEvidencePercent > surface.FrontEvidencePercent), "measurement face preview did not treat nose projection as depth-driven evidence");
    Require(preview.SurfaceEvidence.Any(static surface => surface.RegionId == "cheeks" && surface.SupportingPoseBuckets.Contains(PersonalFacePoseBuckets.YawNegative)), "measurement face preview did not tie cheek evidence to B pose buckets");
    Require(preview.SurfaceEvidence.Any(static surface => surface.RegionId == "brows_forehead" && surface.NextCaptureHint.Contains("up/down", StringComparison.OrdinalIgnoreCase)), "measurement face preview did not expose forehead A-axis capture guidance");
    Require(PreviewMetric(preview, "LeftEyeRenderDepthScale") > 0d && PreviewMetric(preview, "RightEyeRenderDepthScale") > 0d, "measurement face preview did not enable learned eye depth rendering");
    Require(PreviewMetric(preview, "OuterLipRenderDepthScale") > 0d && PreviewMetric(preview, "InnerLipRenderDepthScale") > 0d, "measurement face preview did not enable learned lip depth rendering");
    Require(PreviewMetric(preview, "JawRenderDepthScale") > 0d, "measurement face preview did not enable learned jaw depth rendering");
    Require(PreviewMetric(preview, "RenderSurfacePatchCount") >= 8d, "measurement face preview omitted measured surface patches");
    Require(PreviewMetric(preview, "RenderSurfaceTriangleCount") >= 40d, "measurement face preview omitted measured surface patch triangles");
    Require(PreviewMetric(preview, "RenderSurfacePatchTotalArea") > 0.001d, "measurement face preview omitted measured surface patch area");
    Require(PreviewMetric(preview, "RenderSurfacePatchAverageNormalConsistencyPercent") > 40d, "measurement face preview surface patch normals were not coherent enough");
    Require(RoleZRange(preview, "eye") > 0.005d, "measurement face preview rendered learned eye contours too flat");
    Require(RoleZRange(preview, "mouth") > 0.005d, "measurement face preview rendered learned lip contours too flat");
    Require(RoleZRange(preview, "jaw") > 0.005d, "measurement face preview rendered learned jaw contour too flat");
    ValidateMeasurementFacePreviewSurfacePatches(
        preview.Points,
        preview.SurfacePatches,
        "measurement face preview",
        requireMeasuredPatches: true);
    Require(preview.Polylines.Any(static line => line.Role == "eye"), "measurement face preview did not include eye geometry");
    Require(preview.Polylines.Any(static line => line.Role == "mouth-opening"), "measurement face preview did not include mouth-opening geometry");
    Require(preview.Polylines.Any(static line => line.Role == "jaw-droop"), "measurement face preview did not include jaw-droop geometry");
    ValidateMeasurementFacePreviewGeometry(preview, "measurement face preview");

    var previewRoot = Path.Combine(Path.GetTempPath(), $"AvatarBuilderMeasurementPreviewSmoke-{Guid.NewGuid():N}");
    var files = new MeasurementFacePreviewStore().Write(previewRoot, preview);
    Require(File.Exists(files.JsonPath), "measurement face preview JSON was not written");
    Require(File.Exists(files.HtmlPath), "measurement face preview HTML was not written");
    var json = File.ReadAllText(files.JsonPath);
    var html = File.ReadAllText(files.HtmlPath);
    Require(json.Contains("\"CanRender\": true", StringComparison.Ordinal), "measurement face preview JSON did not mark the preview renderable");
    Require(json.Contains("\"PoseBuckets\"", StringComparison.Ordinal), "measurement face preview JSON did not include pose bucket summaries");
    Require(json.Contains("\"PoseBucketConsistency\"", StringComparison.Ordinal), "measurement face preview JSON did not include pose-bucket consistency report");
    Require(json.Contains("\"SurfaceEvidence\"", StringComparison.Ordinal), "measurement face preview JSON did not include surface-confidence evidence");
    Require(json.Contains("\"SurfacePatches\"", StringComparison.Ordinal), "measurement face preview JSON did not include measured surface patches");
    Require(json.Contains("\"Triangles\"", StringComparison.Ordinal), "measurement face preview JSON did not include measured surface patch triangles");
    Require(json.Contains("\"NormalConsistencyPercent\"", StringComparison.Ordinal), "measurement face preview JSON did not include surface patch normal consistency");
    Require(json.Contains("\"GeometryHealthPercent\"", StringComparison.Ordinal), "measurement face preview JSON did not include surface patch geometry health");
    Require(json.Contains("\"RegionId\": \"nose_projection\"", StringComparison.Ordinal), "measurement face preview JSON did not include nose projection evidence");
    Require(json.Contains("\"nose_bridge_shape\"", StringComparison.Ordinal), "measurement face preview JSON did not include nose bridge surface profile");
    Require(json.Contains("\"PoseAxisHealthPercent\"", StringComparison.Ordinal), "measurement face preview JSON did not include pose-axis health");
    Require(html.Contains("<svg", StringComparison.OrdinalIgnoreCase), "measurement face preview HTML did not include an SVG view");
    Require(html.Contains("id=\"face3d\"", StringComparison.Ordinal), "measurement face preview HTML did not include the interactive 3D canvas");
    Require(html.Contains("id=\"face-preview-scene\"", StringComparison.Ordinal), "measurement face preview HTML did not include embedded 3D scene data");
    Require(html.Contains("Pose Buckets", StringComparison.Ordinal), "measurement face preview HTML did not include pose bucket table");
    Require(html.Contains("Pose buckets", StringComparison.Ordinal), "measurement face preview HTML did not include pose bucket overlay summary");
    Require(html.Contains("Pose Consistency", StringComparison.Ordinal), "measurement face preview HTML did not include pose consistency section");
    Require(html.Contains("Surface Confidence", StringComparison.Ordinal), "measurement face preview HTML did not include surface confidence section");
    Require(html.Contains("Surface Patch Geometry", StringComparison.Ordinal), "measurement face preview HTML did not include surface patch geometry section");
    Require(html.Contains("Health", StringComparison.Ordinal), "measurement face preview HTML did not expose surface patch health");
    Require(html.Contains("surface-patch", StringComparison.Ordinal), "measurement face preview HTML did not render measured surface patches");
    Require(html.Contains("surface-triangle", StringComparison.Ordinal), "measurement face preview HTML did not render measured surface patch triangles");
    Require(html.Contains("Normal consistency", StringComparison.Ordinal), "measurement face preview HTML did not expose surface normal consistency");
    Require(html.Contains("Nose projection", StringComparison.Ordinal), "measurement face preview HTML did not include nose projection confidence");
    Require(html.Contains("Depth", StringComparison.Ordinal), "measurement face preview HTML did not expose depth evidence");
    Require(html.Contains("Axis reason", StringComparison.Ordinal), "measurement face preview HTML did not include pose-axis audit detail");
    Require(html.Contains("cursor: grab", StringComparison.Ordinal), "measurement face preview HTML did not expose a draggable canvas cursor");
    Require(html.Contains("addEventListener('wheel'", StringComparison.Ordinal), "measurement face preview HTML did not wire mouse-wheel zoom");
    Require(html.Contains("addEventListener('dblclick'", StringComparison.Ordinal), "measurement face preview HTML did not wire double-click reset");
    Require(html.Contains("Measured", StringComparison.Ordinal) && html.Contains("scaffold", StringComparison.Ordinal), "measurement face preview HTML did not expose measured/scaffold contribution");
    Require(!json.Contains("FaceContour", StringComparison.Ordinal) && !json.Contains("LeftEyeContour", StringComparison.Ordinal), "measurement face preview leaked raw contour data");
    Require(!html.Contains("data:image", StringComparison.OrdinalIgnoreCase), "measurement face preview HTML embedded raw image data");
    ValidateMeasurementFacePreviewHtmlScene(html, preview, "measurement face preview HTML scene");

    var seedModel = new PersonalFaceModel
    {
        SubjectId = "chris",
        SubjectDisplayName = "Chris",
        SubjectCollectionMode = PersonalFaceSubject.ManualConfirmationMode,
        UnknownSubjectPolicy = PersonalFaceSubject.UnknownSubjectPolicy
    };
    var seedGate = FaceReconstructionSubjectGate.FromPersonalModel(
        seedModel,
        manualSubjectConfirmed: true,
        reason: "seed preview subject confirmed");
    var seedPreview = builder.Build(seedModel, seedGate);
    Require(seedPreview.CanRender, "measurement face preview did not render canonical seed scaffold for a confirmed subject");
    Require(seedPreview.AcceptedSamples == 0, "seed preview changed accepted measurement count");
    Require(seedPreview.TemplatePriorUsed, "seed preview did not mark template prior use");
    Require(seedPreview.TemplatePriorContributionPercent == 100d, $"seed preview template contribution was not 100%: {seedPreview.TemplatePriorContributionPercent}");
    Require(seedPreview.MeasurementContributionPercent == 0d, $"seed preview measured contribution was not 0%: {seedPreview.MeasurementContributionPercent}");
    Require(seedPreview.SurfaceEvidence.Any(static surface => surface.Status == "mostly scaffold"), "seed preview did not mark missing surface evidence as scaffold");
    Require(seedPreview.Points.Count >= 60, $"seed preview geometry was too sparse: {seedPreview.Points.Count}");
    Require(seedPreview.Points.All(static point => point.Provenance.Contains("template", StringComparison.OrdinalIgnoreCase)), "seed preview emitted points without template provenance");
    ValidateMeasurementFacePreviewGeometry(seedPreview, "seed measurement face preview");
    var seedFiles = new MeasurementFacePreviewStore().Write(Path.Combine(previewRoot, "seed"), seedPreview);
    var seedHtml = File.ReadAllText(seedFiles.HtmlPath);
    ValidateMeasurementFacePreviewHtmlScene(seedHtml, seedPreview, "seed measurement face preview HTML scene");

    var poseBiasedModel = new PersonalFaceModel
    {
        SubjectId = "chris",
        SubjectDisplayName = "Chris",
        SubjectCollectionMode = PersonalFaceSubject.ManualConfirmationMode,
        UnknownSubjectPolicy = PersonalFaceSubject.UnknownSubjectPolicy,
        ObservedSamples = 11,
        AcceptedSamples = 9,
        RejectedSamples = 2,
        AcceptedSampleWeight = 8d,
        AverageFaceReliabilityPercent = 95d,
        AverageFaceContinuityPercent = 92d,
        AverageEyeReliabilityPercent = 93d,
        AverageMouthReliabilityPercent = 92d,
        FaceWidth = Distribution(0.22d, sampleCount: 9, totalWeight: 8d, spread: 0.01d),
        FaceHeight = Distribution(0.43d, sampleCount: 9, totalWeight: 8d, spread: 0.015d),
        LeftEyeOpeningRatio = Distribution(0.34d, sampleCount: 9, totalWeight: 8d, spread: 0.03d),
        RightEyeOpeningRatio = Distribution(0.34d, sampleCount: 9, totalWeight: 8d, spread: 0.03d),
        AverageEyeOpeningRatio = Distribution(0.34d, sampleCount: 9, totalWeight: 8d, spread: 0.03d),
        MouthOpeningRatio = Distribution(0.14d, sampleCount: 9, totalWeight: 8d, spread: 0.04d),
        JawDroopRatio = Distribution(0.08d, sampleCount: 9, totalWeight: 8d, spread: 0.03d),
        InterEyeDistanceToFaceWidth = Distribution(0.41d, sampleCount: 9, totalWeight: 8d, spread: 0.02d),
        LeftEyeWidthToFaceWidth = Distribution(0.18d, sampleCount: 9, totalWeight: 8d, spread: 0.02d),
        RightEyeWidthToFaceWidth = Distribution(0.18d, sampleCount: 9, totalWeight: 8d, spread: 0.02d),
        MouthWidthToFaceWidth = Distribution(0.38d, sampleCount: 9, totalWeight: 8d, spread: 0.02d),
        EyeMidlineYToFaceHeight = Distribution(0.31d, sampleCount: 9, totalWeight: 8d, spread: 0.02d),
        MouthCenterYToFaceHeight = Distribution(0.68d, sampleCount: 9, totalWeight: 8d, spread: 0.02d),
        LeftEyeShape = ShiftShapeProfile(ShapeProfile("left_eye_shape", "Left eye contour shape", 8, closed: true, sampleCount: 9, totalWeight: 8d), xShift: -0.22d),
        RightEyeShape = ShiftShapeProfile(ShapeProfile("right_eye_shape", "Right eye contour shape", 8, closed: true, sampleCount: 9, totalWeight: 8d), xShift: -0.22d),
        OuterLipShape = ShiftShapeProfile(ShapeProfile("outer_lip_shape", "Outer lip contour shape", 12, closed: true, sampleCount: 9, totalWeight: 8d), xShift: -0.22d),
        InnerLipShape = ShiftShapeProfile(ShapeProfile("inner_lip_shape", "Inner lip contour shape", 10, closed: true, sampleCount: 9, totalWeight: 8d), xShift: -0.22d),
        JawShape = ShiftShapeProfile(ShapeProfile("jaw_shape", "Jaw contour shape", 9, closed: false, sampleCount: 9, totalWeight: 8d), xShift: -0.22d),
        PoseBuckets = PoseBucketProfiles(sampleCount: 9, totalWeight: 8d)
    };
    var poseBiasedPreview = builder.Build(poseBiasedModel, FaceReconstructionSubjectGate.FromPersonalModel(
        poseBiasedModel,
        manualSubjectConfirmed: true,
        identityConfidencePercent: 95d));
    ValidateMeasurementFacePreviewGeometry(poseBiasedPreview, "pose-biased measurement face preview");
    var leftEyeCenterX = AveragePreviewPointX(poseBiasedPreview, "left_eye_shape_");
    var rightEyeCenterX = AveragePreviewPointX(poseBiasedPreview, "right_eye_shape_");
    var mouthCenterX = AveragePreviewPointX(poseBiasedPreview, "mouth_outer_shape_");
    var jawCenterX = AveragePreviewPointX(poseBiasedPreview, "jaw_shape_");
    Require(leftEyeCenterX < -0.025d, $"pose-biased left eye was not kept on the left side: {leftEyeCenterX}");
    Require(rightEyeCenterX > 0.025d, $"pose-biased right eye was not kept on the right side: {rightEyeCenterX}");
    Require(Math.Abs((leftEyeCenterX + rightEyeCenterX) / 2d) < 0.02d, $"pose-biased eye pair was not recentered around the neutral face: left={leftEyeCenterX}, right={rightEyeCenterX}");
    Require(Math.Abs(mouthCenterX) < 0.02d, $"pose-biased mouth contour slid sideways instead of using neutral placement: {mouthCenterX}");
    Require(Math.Abs(jawCenterX) < 0.025d, $"pose-biased jaw contour slid sideways instead of using neutral placement: {jawCenterX}");
    Require(
        poseBiasedPreview.Points.Any(static point => point.Provenance.Contains("pose-normalized", StringComparison.OrdinalIgnoreCase)),
        "pose-biased preview did not mark contour geometry as pose-normalized");

    var blockedGate = FaceReconstructionSubjectGate.FromPersonalModel(
        model,
        manualSubjectConfirmed: false,
        identityConfidencePercent: 95d);
    var blockedPreview = builder.Build(model, blockedGate);
    Require(!blockedPreview.CanRender, "measurement face preview rendered with an unconfirmed subject gate");
    Require(blockedPreview.Points.Count == 0 && blockedPreview.Polylines.Count == 0, "blocked measurement face preview still emitted geometry");
    var blockedFiles = new MeasurementFacePreviewStore().Write(Path.Combine(previewRoot, "blocked"), blockedPreview);
    var blockedHtml = File.ReadAllText(blockedFiles.HtmlPath);
    Require(blockedHtml.Contains("Confirm Chris", StringComparison.Ordinal), "blocked measurement face preview did not explain subject confirmation");
    Require(blockedHtml.Contains("Subject gate", StringComparison.Ordinal), "blocked measurement face preview did not expose the subject-gate reason");

    var blockedSeedGate = FaceReconstructionSubjectGate.FromPersonalModel(
        seedModel,
        manualSubjectConfirmed: false,
        reason: "seed preview subject unconfirmed");
    var blockedSeedPreview = builder.Build(seedModel, blockedSeedGate);
    var blockedSeedFiles = new MeasurementFacePreviewStore().Write(Path.Combine(previewRoot, "blocked-seed"), blockedSeedPreview);
    var blockedSeedHtml = File.ReadAllText(blockedSeedFiles.HtmlPath);
    Require(blockedSeedHtml.Contains("Confirm Chris", StringComparison.Ordinal), "blocked seed preview did not explain subject confirmation");
    Require(blockedSeedHtml.Contains("Start Avatar Learning", StringComparison.Ordinal), "blocked seed preview did not explain how to start measurements");
    Directory.Delete(previewRoot, recursive: true);
}

static void ValidateMeasurementFacePreviewHtmlScene(
    string html,
    MeasurementFacePreviewModel expectedPreview,
    string label)
{
    var sceneJson = ExtractApplicationJsonScript(html, "face-preview-scene");
    var scene = JsonSerializer.Deserialize<MeasurementFacePreviewSceneContract>(sceneJson)
        ?? throw new InvalidOperationException($"{label} could not deserialize embedded scene JSON");
    Require(scene.Points.Count == expectedPreview.Points.Count, $"{label} point count drifted from the preview model");
    Require(scene.Polylines.Count == expectedPreview.Polylines.Count, $"{label} polyline count drifted from the preview model");
    Require(scene.SurfacePatches.Count == expectedPreview.SurfacePatches.Count, $"{label} surface patch count drifted from the preview model");
    Require(scene.PoseBuckets.Count == expectedPreview.PoseBuckets.Count, $"{label} pose bucket count drifted from the preview model");
    Require(scene.SurfaceEvidence.Count == expectedPreview.SurfaceEvidence.Count, $"{label} surface evidence count drifted from the preview model");
    Require(
        scene.PoseBuckets.Count == 0 || scene.PoseBuckets.Any(static bucket => bucket.PrimaryNeutralReference),
        $"{label} embedded scene omitted the neutral pose reference bucket");
    Require(
        scene.SurfaceEvidence.Count == 0 || scene.SurfaceEvidence.Any(static surface => surface.RegionId == "nose_projection"),
        $"{label} embedded scene omitted nose projection surface evidence");
    Require(
        Math.Abs(scene.MeasurementContributionPercent - expectedPreview.MeasurementContributionPercent) < 0.001d,
        $"{label} measurement contribution drifted from the preview model");
    Require(
        Math.Abs(scene.TemplatePriorContributionPercent - expectedPreview.TemplatePriorContributionPercent) < 0.001d,
        $"{label} template contribution drifted from the preview model");
    Require(
        string.Equals(scene.GeometryProvenance, expectedPreview.GeometryProvenance, StringComparison.Ordinal),
        $"{label} geometry provenance drifted from the preview model");
    ValidateMeasurementFacePreviewSurfacePatches(
        scene.Points,
        scene.SurfacePatches,
        label,
        requireMeasuredPatches: expectedPreview.SurfacePatches.Count > 0);
    ValidateMeasurementFacePreviewScene(
        scene.Points,
        scene.Polylines,
        scene.MeasurementContributionPercent,
        scene.TemplatePriorContributionPercent,
        scene.GeometryProvenance,
        label);
}

static void RunLastGoodFeatureMeshSmoke()
{
    var frame = CreateSyntheticDenseMeshFrame();
    var metrics = new FaceLandmarkMetricCalculator().Update(frame);
    var stability = new FaceLockStabilityAnalyzer().Update(
        new FaceFeatureDetection
        {
            HasFace = true,
            Source = frame.Source,
            FaceBox = GetContourBounds(frame.FaceContour) ?? new WpfRect(0.30d, 0.22d, 0.40d, 0.56d),
            TrackingConfidence = frame.TrackingConfidence,
            EyeConfidence = frame.EyeConfidence,
            MouthConfidence = frame.MouthConfidence,
            FaceContour = frame.FaceContour,
            LeftEyeContour = frame.LeftEyeContour,
            RightEyeContour = frame.RightEyeContour,
            OuterLipContour = frame.OuterLipContour,
            InnerLipContour = frame.InnerLipContour,
            JawContour = frame.JawContour
        },
        frame,
        metrics);
    var captureQuality = new PersonalFaceCaptureQualityAssessment
    {
        Label = "avatar-grade",
        ScorePercent = 88d,
        CanCollectMeasurements = true,
        StrongEnoughForAvatarLearning = true,
        PrimaryReason = "synthetic dense mesh smoke"
    };

    Require(
        LastGoodFeatureMeshSampleFactory.TryCreate(frame, metrics, stability, captureQuality, out var sample, out var reason),
        $"last good feature mesh factory rejected synthetic dense mesh: {reason}");
    var suppliedPose = new HeadPoseEstimate
    {
        HasFace = true,
        YawDegrees = 16d,
        PitchDegrees = -6d,
        RollDegrees = 4d,
        XHorizontalPercent = 52d,
        YVerticalPercent = 47d,
        ApparentDistanceUnits = 3.25d,
        RelativeDistanceScale = 1.12d,
        InterEyeFrameWidthPercent = 18.5d,
        ZConfidencePercent = 88d,
        ZEstimateKind = "smoke-learned-reference",
        ZQualityLabel = "strong learned-reference apparent Z",
        RotationSource = "smoke supplied pose",
        HasSolvePnPPose = true,
        SolvePnPYawDegrees = 15.2d,
        SolvePnPPitchDegrees = -5.8d,
        SolvePnPRollDegrees = 3.7d,
        SolvePnPReprojectionErrorPixels = 2.4d,
        PoseSolverDisagreementDegrees = 0.8d,
        PoseSolverStatus = "solvePnP agrees A/B/C -5.8/15.2/3.7; reproj 2.4 px; disagreement 0.8 deg",
        DistanceSource = "smoke apparent Z",
        ReferenceScaleSource = "smoke learned reference"
    };
    Require(
        LastGoodFeatureMeshSampleFactory.TryCreate(frame, metrics, stability, captureQuality, out var poseSample, out var poseReason, headPose: suppliedPose),
        $"last good feature mesh factory rejected synthetic dense mesh with supplied pose: {poseReason}");
    Require(Math.Abs(poseSample.HeadYawDegrees - 16d) < 0.001d, $"last good feature mesh stored stale B/Y rotation: {poseSample.HeadYawDegrees}");
    Require(Math.Abs(poseSample.HeadPitchDegrees + 6d) < 0.001d, $"last good feature mesh stored stale A/X rotation: {poseSample.HeadPitchDegrees}");
    Require(Math.Abs(poseSample.HeadRollDegrees - 4d) < 0.001d, $"last good feature mesh stored stale C/Z rotation: {poseSample.HeadRollDegrees}");
    Require(Math.Abs(poseSample.XHorizontalPercent - 52d) < 0.001d, $"last good feature mesh omitted X pose center: {poseSample.XHorizontalPercent}");
    Require(Math.Abs(poseSample.YVerticalPercent - 47d) < 0.001d, $"last good feature mesh omitted Y pose center: {poseSample.YVerticalPercent}");
    Require(Math.Abs(poseSample.ApparentDistanceUnits.GetValueOrDefault() - 3.25d) < 0.001d, $"last good feature mesh omitted apparent Z: {poseSample.ApparentDistanceUnits}");
    Require(Math.Abs(poseSample.RelativeDistanceScale.GetValueOrDefault() - 1.12d) < 0.001d, $"last good feature mesh omitted learned Z scale: {poseSample.RelativeDistanceScale}");
    Require(Math.Abs(poseSample.InterEyeFrameWidthPercent.GetValueOrDefault() - 18.5d) < 0.001d, $"last good feature mesh omitted eye-span pose evidence: {poseSample.InterEyeFrameWidthPercent}");
    Require(poseSample.RotationSource == "smoke supplied pose", "last good feature mesh omitted pose rotation provenance");
    Require(poseSample.DistanceSource == "smoke apparent Z", "last good feature mesh omitted pose distance provenance");
    Require(poseSample.ReferenceScaleSource == "smoke learned reference", "last good feature mesh omitted pose reference provenance");
    Require(poseSample.ZEstimateKind == "smoke-learned-reference", "last good feature mesh omitted Z estimate kind");
    Require(poseSample.HasSolvePnPPose, "last good feature mesh omitted solvePnP pose availability");
    Require(Math.Abs(poseSample.SolvePnPYawDegrees.GetValueOrDefault() - 15.2d) < 0.001d, $"last good feature mesh omitted solvePnP B/Y rotation: {poseSample.SolvePnPYawDegrees}");
    Require(Math.Abs(poseSample.SolvePnPPitchDegrees.GetValueOrDefault() + 5.8d) < 0.001d, $"last good feature mesh omitted solvePnP A/X rotation: {poseSample.SolvePnPPitchDegrees}");
    Require(Math.Abs(poseSample.SolvePnPRollDegrees.GetValueOrDefault() - 3.7d) < 0.001d, $"last good feature mesh omitted solvePnP C/Z rotation: {poseSample.SolvePnPRollDegrees}");
    Require(Math.Abs(poseSample.SolvePnPReprojectionErrorPixels.GetValueOrDefault() - 2.4d) < 0.001d, $"last good feature mesh omitted solvePnP reprojection error: {poseSample.SolvePnPReprojectionErrorPixels}");
    Require(poseSample.PoseSolverStatus.Contains("solvePnP", StringComparison.Ordinal), "last good feature mesh omitted solvePnP status text");
    Require(metrics.IsBrowMeasurementUsable, $"synthetic dense mesh did not produce usable brow metrics: q {metrics.BrowMeasurementQualityPercent:0.#}");
    Require(metrics.AverageBrowHeightRatio is > 0.05d and < 0.30d, $"synthetic dense mesh brow height was implausible: {metrics.AverageBrowHeightRatio}");
    Require(metrics.BrowAsymmetryPercent is < 40d, $"synthetic dense mesh brow asymmetry was implausible: {metrics.BrowAsymmetryPercent}");
    Require(sample.Points.Count == 468, $"last good feature mesh did not preserve all dense points: {sample.Points.Count}");
    Require(sample.BrowQualityPercent > 50d, $"last good feature mesh omitted useful brow quality: {sample.BrowQualityPercent:0.#}");
    Require(sample.AverageBrowHeightRatio is > 0.05d and < 0.30d, $"last good feature mesh omitted useful brow height: {sample.AverageBrowHeightRatio}");
    Require(sample.FeatureGroups.Any(static group => group.Role == "nose"), "last good feature mesh omitted nose feature group");
    Require(sample.FeatureGroups.Any(static group => group.Role == "brow"), "last good feature mesh omitted brow feature group");
    Require(sample.FeatureGroups.Any(static group => group.Role == "cheek"), "last good feature mesh omitted cheek feature group");
    Require(sample.FeatureGroups.Any(static group => group.Role == "forehead"), "last good feature mesh omitted forehead feature group");
    Require(sample.WireframeEdges.Count(static edge => edge.Role == "surface") >= 400, "last good feature mesh emitted too few MediaPipe tessellation edges");
    Require(sample.WireframeEdges.Any(static edge => edge.Source == "mediapipe-face-tessellation"), "last good feature mesh omitted MediaPipe tessellation edge provenance");
    Require(
        sample.WireframeEdges
            .Where(static edge => edge.Role == "surface")
            .All(static edge => edge.LengthPercent <= 18d),
        "last good feature mesh emitted a long unsafe surface wireframe edge");
    Require(sample.WireframeEdges.Any(static edge => edge.Role == "eye"), "last good feature mesh omitted eye wireframe edges");
    Require(sample.WireframeEdges.Any(static edge => edge.Role == "mouth"), "last good feature mesh omitted mouth wireframe edges");
    Require(sample.WireframeEdges.Any(static edge => edge.Role == "forehead"), "last good feature mesh omitted forehead wireframe edges");
    var stableHeadLock = LastGoodFeatureMeshStabilityAnalyzer.Analyze([sample, sample, sample]);
    Require(stableHeadLock.HeadLockedSampleCount == 3, "last good feature stability analyzer did not head-lock repeated samples");
    Require(stableHeadLock.HealthPercent >= 82d, $"last good feature stability analyzer treated repeated samples as unstable: {stableHeadLock.HealthPercent:0.#}%");
    Require(stableHeadLock.YawHealthPercent <= 0d, "last good feature stability analyzer should wait for B range before scoring head turns");
    var shiftedMouth = ShiftLastGoodFeatureGroup(sample, "outer_lip", 0.18d, 0d, 0d);
    var driftingHeadLock = LastGoodFeatureMeshStabilityAnalyzer.Analyze([sample, sample, shiftedMouth]);
    Require(driftingHeadLock.HealthPercent < stableHeadLock.HealthPercent, "last good feature stability analyzer did not reduce health for a shifted mouth");
    Require(
        driftingHeadLock.Features.Any(static feature =>
            feature.FeatureId == "outer_lip"
            && feature.MaximumDriftPercent >= 8d
            && feature.Status is "review" or "sliding"),
        "last good feature stability analyzer did not flag the intentionally shifted lip anchor");
    Require(
        driftingHeadLock.Findings.Any(static finding => finding.Contains("drifted", StringComparison.OrdinalIgnoreCase)),
        "last good feature stability analyzer did not emit an actionable drift finding");
    var yawLeft = RotateLastGoodFeatureMeshSample(sample, -18d);
    var yawRight = RotateLastGoodFeatureMeshSample(sample, 18d);
    var stableYawLock = LastGoodFeatureMeshStabilityAnalyzer.Analyze([yawLeft, sample, yawRight]);
    Require(stableYawLock.YawRangeDegrees >= 30d, $"last good feature stability analyzer did not retain B range: {stableYawLock.YawRangeDegrees:0.#}");
    Require(stableYawLock.YawLeftSampleCount > 0 && stableYawLock.YawRightSampleCount > 0, "last good feature stability analyzer did not count left/right B samples");
    Require(stableYawLock.YawHealthPercent >= 82d, $"last good feature stability analyzer treated rotated head samples as B-axis sliding: {stableYawLock.YawHealthPercent:0.#}%");
    Require(
        stableYawLock.YawFindings.Any(static finding => finding.Contains("stayed attached", StringComparison.OrdinalIgnoreCase)),
        "last good feature stability analyzer did not report stable B-axis attachment");
    var driftingYawMouth = ShiftLastGoodFeatureGroup(yawRight, "outer_lip", 0.18d, 0d, 0d);
    var driftingYawLock = LastGoodFeatureMeshStabilityAnalyzer.Analyze([yawLeft, sample, driftingYawMouth]);
    Require(driftingYawLock.YawHealthPercent < stableYawLock.YawHealthPercent, "last good feature stability analyzer did not reduce B health for mouth sliding during a turn");
    Require(
        driftingYawLock.YawFindings.Any(static finding => finding.Contains("while B range", StringComparison.OrdinalIgnoreCase)),
        "last good feature stability analyzer did not emit B-specific sliding finding");
    var offAxisPitchMouthDrift = ShiftLastGoodFeatureGroup(
        TransformLastGoodFeatureMeshSample(sample, pitchDegrees: 12d),
        "outer_lip",
        0.18d,
        0d,
        0d);
    var yawWithOffAxisDrift = LastGoodFeatureMeshStabilityAnalyzer.Analyze([yawLeft, sample, yawRight, offAxisPitchMouthDrift]);
    Require(
        yawWithOffAxisDrift.HealthPercent < stableYawLock.HealthPercent,
        "last good feature stability analyzer did not keep overall drift sensitive to an off-axis feature shift");
    Require(
        yawWithOffAxisDrift.YawHealthPercent >= 82d,
        $"B-axis health was contaminated by an A-axis-only drift sample: {yawWithOffAxisDrift.YawHealthPercent:0.#}%");
    Require(
        yawWithOffAxisDrift.YawComparedFeatureCount > 0,
        "B-axis health did not expose its axis-specific compared feature count");
    var aNegative = TransformLastGoodFeatureMeshSample(sample, pitchDegrees: -12d);
    var aPositive = TransformLastGoodFeatureMeshSample(sample, pitchDegrees: 12d);
    var stableALock = LastGoodFeatureMeshStabilityAnalyzer.Analyze([aNegative, sample, aPositive]);
    Require(stableALock.ARangeDegrees >= 20d, $"last good feature stability analyzer did not retain A range: {stableALock.ARangeDegrees:0.#}");
    Require(stableALock.ANegativeSampleCount > 0 && stableALock.APositiveSampleCount > 0, "last good feature stability analyzer did not count negative/positive A samples");
    Require(stableALock.AHealthPercent >= 82d, $"last good feature stability analyzer treated A-axis tilt as sliding: {stableALock.AHealthPercent:0.#}%");
    Require(stableALock.AComparedFeatureCount > 0, "A-axis health did not expose its axis-specific compared feature count");
    Require(
        stableALock.AFindings.Any(static finding => finding.Contains("A-axis", StringComparison.OrdinalIgnoreCase) && finding.Contains("stayed attached", StringComparison.OrdinalIgnoreCase)),
        "last good feature stability analyzer did not report stable A-axis attachment");
    var driftingAMouth = ShiftLastGoodFeatureGroup(aPositive, "outer_lip", 0.18d, 0d, 0d);
    var driftingALock = LastGoodFeatureMeshStabilityAnalyzer.Analyze([aNegative, sample, driftingAMouth]);
    Require(driftingALock.AHealthPercent < stableALock.AHealthPercent, "last good feature stability analyzer did not reduce A health for mouth sliding during A tilt");
    Require(
        driftingALock.AFindings.Any(static finding => finding.Contains("while A range", StringComparison.OrdinalIgnoreCase)),
        "last good feature stability analyzer did not emit A-specific sliding finding");

    var cNegative = TransformLastGoodFeatureMeshSample(sample, rollDegrees: -12d);
    var cPositive = TransformLastGoodFeatureMeshSample(sample, rollDegrees: 12d);
    var stableCLock = LastGoodFeatureMeshStabilityAnalyzer.Analyze([cNegative, sample, cPositive]);
    Require(stableCLock.CRangeDegrees >= 20d, $"last good feature stability analyzer did not retain C range: {stableCLock.CRangeDegrees:0.#}");
    Require(stableCLock.CNegativeSampleCount > 0 && stableCLock.CPositiveSampleCount > 0, "last good feature stability analyzer did not count negative/positive C samples");
    Require(stableCLock.CHealthPercent >= 82d, $"last good feature stability analyzer treated C-axis tilt as sliding: {stableCLock.CHealthPercent:0.#}%");
    Require(stableCLock.CComparedFeatureCount > 0, "C-axis health did not expose its axis-specific compared feature count");

    var zFar = TransformLastGoodFeatureMeshSample(sample, scale: 0.84d);
    var zClose = TransformLastGoodFeatureMeshSample(sample, scale: 1.16d);
    var stableZLock = LastGoodFeatureMeshStabilityAnalyzer.Analyze([zFar, sample, zClose]);
    Require(stableZLock.ZFaceScaleRangePercent >= 25d, $"last good feature stability analyzer did not retain Z face-scale range: {stableZLock.ZFaceScaleRangePercent:0.#}%");
    Require(stableZLock.ZCloseSampleCount > 0 && stableZLock.ZFarSampleCount > 0, "last good feature stability analyzer did not count close/far Z samples");
    Require(stableZLock.ZHealthPercent >= 82d, $"last good feature stability analyzer treated Z distance change as sliding: {stableZLock.ZHealthPercent:0.#}%");
    Require(stableZLock.ZComparedFeatureCount > 0, "Z health did not expose its axis-specific compared feature count");
    Require(
        stableZLock.ZFindings.Any(static finding => finding.Contains("closer/farther", StringComparison.OrdinalIgnoreCase)),
        "last good feature stability analyzer did not report stable Z distance attachment");

    var root = Path.Combine(Path.GetTempPath(), $"avatar-builder-last-good-features-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var files = new LastGoodFeatureMeshStore().Write(
            root,
            new LastGoodFeatureMeshReport
            {
                SubjectId = "chris",
                SubjectDisplayName = "Chris",
                Samples = [poseSample]
            });
        Require(File.Exists(files.JsonPath), "last good feature mesh JSON was not written");
        Require(File.Exists(files.HtmlPath), "last good feature mesh HTML was not written");
        var json = File.ReadAllText(files.JsonPath);
        var html = File.ReadAllText(files.HtmlPath);
        Require(json.Contains("\"schemaVersion\": \"last-good-feature-mesh-v1\"", StringComparison.Ordinal), "last good feature mesh JSON used the wrong schema");
        Require(json.Contains("\"points\"", StringComparison.Ordinal), "last good feature mesh JSON omitted dense points");
        Require(json.Contains("\"wireframeEdges\"", StringComparison.Ordinal), "last good feature mesh JSON omitted wireframe edges");
        Require(json.Contains("\"featureGroups\"", StringComparison.Ordinal), "last good feature mesh JSON omitted feature groups");
        Require(json.Contains("\"mediapipe-face-tessellation\"", StringComparison.Ordinal), "last good feature mesh JSON omitted MediaPipe tessellation edge provenance");
        Require(json.Contains("\"hasSolvePnPPose\": true", StringComparison.Ordinal), "last good feature mesh JSON omitted solvePnP availability");
        Require(json.Contains("\"solvePnPReprojectionErrorPixels\"", StringComparison.Ordinal), "last good feature mesh JSON omitted solvePnP reprojection error");
        Require(json.Contains("\"headLockedStability\"", StringComparison.Ordinal), "last good feature mesh JSON omitted head-locked stability report");
        Require(json.Contains("\"yawHealthPercent\"", StringComparison.Ordinal), "last good feature mesh JSON omitted B head-turn stability report");
        Require(json.Contains("\"yawComparedFeatureCount\"", StringComparison.Ordinal), "last good feature mesh JSON omitted B axis-specific compared feature count");
        Require(json.Contains("\"aHealthPercent\"", StringComparison.Ordinal), "last good feature mesh JSON omitted A tilt stability report");
        Require(json.Contains("\"aComparedFeatureCount\"", StringComparison.Ordinal), "last good feature mesh JSON omitted A axis-specific compared feature count");
        Require(json.Contains("\"cHealthPercent\"", StringComparison.Ordinal), "last good feature mesh JSON omitted C tilt stability report");
        Require(json.Contains("\"cComparedFeatureCount\"", StringComparison.Ordinal), "last good feature mesh JSON omitted C axis-specific compared feature count");
        Require(json.Contains("\"zHealthPercent\"", StringComparison.Ordinal), "last good feature mesh JSON omitted Z distance stability report");
        Require(json.Contains("\"zComparedFeatureCount\"", StringComparison.Ordinal), "last good feature mesh JSON omitted Z axis-specific compared feature count");
        Require(html.Contains("Last 10 Good Features", StringComparison.Ordinal), "last good feature mesh HTML omitted title");
        Require(html.Contains("Head-Locked Stability", StringComparison.Ordinal), "last good feature mesh HTML omitted head-locked stability panel");
        Require(html.Contains("B Head-Turn Findings", StringComparison.Ordinal), "last good feature mesh HTML omitted B head-turn findings");
        Require(html.Contains("B head-turn lock", StringComparison.Ordinal), "last good feature mesh HTML omitted B head-turn lock status");
        Require(html.Contains("A Tilt Findings", StringComparison.Ordinal), "last good feature mesh HTML omitted A tilt findings");
        Require(html.Contains("C Tilt Findings", StringComparison.Ordinal), "last good feature mesh HTML omitted C tilt findings");
        Require(html.Contains("Z Distance Findings", StringComparison.Ordinal), "last good feature mesh HTML omitted Z distance findings");
        Require(html.Contains("meshReport", StringComparison.Ordinal), "last good feature mesh HTML omitted embedded scene data");
        Require(html.Contains("Wireframe", StringComparison.Ordinal), "last good feature mesh HTML omitted wireframe control");
        Require(html.Contains("Ghost Last 10", StringComparison.Ordinal), "last good feature mesh HTML omitted ghost comparison control");
        Require(html.Contains("Head Lock", StringComparison.Ordinal), "last good feature mesh HTML omitted head-locked comparison control");
        Require(html.Contains("Frame Axes", StringComparison.Ordinal), "last good feature mesh HTML omitted head frame axis control");
        Require(html.Contains("head-locked view", StringComparison.Ordinal), "last good feature mesh HTML omitted head-locked coordinate mode");
        Require(html.Contains("Head-local anchors", StringComparison.Ordinal), "last good feature mesh HTML omitted head-local anchor details");
        Require(html.Contains("Pose comparison", StringComparison.Ordinal), "last good feature mesh HTML omitted pose comparison details");
        Require(html.Contains("OpenCV solvePnP", StringComparison.Ordinal), "last good feature mesh HTML omitted solvePnP comparison label");
        Require(html.Contains("Selected Pose Model", StringComparison.Ordinal), "last good feature mesh HTML omitted selected pose model canvas");
        Require(html.Contains("OpenCV solvePnP Pose Model", StringComparison.Ordinal), "last good feature mesh HTML omitted solvePnP pose model canvas");
        Require(html.Contains("solvePnpPoseModel", StringComparison.Ordinal), "last good feature mesh HTML omitted solvePnP visual canvas id");
        Require(html.Contains("review-only", StringComparison.Ordinal), "last good feature mesh HTML omitted solvePnP review-only warning path");
        Require(html.Contains("axis delta A/B/C", StringComparison.Ordinal), "last good feature mesh HTML omitted solvePnP axis delta details");
        Require(html.Contains("Brow tracking", StringComparison.Ordinal), "last good feature mesh HTML omitted brow tracking details");
        Require(html.Contains("compared features", StringComparison.OrdinalIgnoreCase), "last good feature mesh HTML omitted axis-specific compared feature counts");
        Require(html.Contains("auto-refreshes every 10 seconds", StringComparison.OrdinalIgnoreCase), "last good feature mesh HTML did not use the throttled refresh copy");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static LastGoodFeatureMeshSample ShiftLastGoodFeatureGroup(
    LastGoodFeatureMeshSample sample,
    string featureGroupId,
    double xOffset,
    double yOffset,
    double zOffset)
{
    var shiftedIndices = sample.FeatureGroups
        .First(group => string.Equals(group.Id, featureGroupId, StringComparison.OrdinalIgnoreCase))
        .LandmarkIndices
        .ToHashSet();
    return new LastGoodFeatureMeshSample
    {
        SampleId = $"{sample.SampleId}-shift-{featureGroupId}",
        CapturedAtUtc = sample.CapturedAtUtc.AddMilliseconds(33),
        Source = sample.Source,
        DenseMeshTopology = sample.DenseMeshTopology,
        PointCount = sample.PointCount,
        TrackingConfidencePercent = sample.TrackingConfidencePercent,
        EyeConfidencePercent = sample.EyeConfidencePercent,
        MouthConfidencePercent = sample.MouthConfidencePercent,
        OverallQualityPercent = sample.OverallQualityPercent,
        EyeQualityPercent = sample.EyeQualityPercent,
        MouthQualityPercent = sample.MouthQualityPercent,
        BrowQualityPercent = sample.BrowQualityPercent,
        FaceReliabilityPercent = sample.FaceReliabilityPercent,
        FaceContinuityPercent = sample.FaceContinuityPercent,
        EyeReliabilityPercent = sample.EyeReliabilityPercent,
        MouthReliabilityPercent = sample.MouthReliabilityPercent,
        HeadYawDegrees = sample.HeadYawDegrees,
        HeadPitchDegrees = sample.HeadPitchDegrees,
        HeadRollDegrees = sample.HeadRollDegrees,
        XHorizontalPercent = sample.XHorizontalPercent,
        YVerticalPercent = sample.YVerticalPercent,
        DistanceInches = sample.DistanceInches,
        ApparentDistanceUnits = sample.ApparentDistanceUnits,
        RelativeDistanceScale = sample.RelativeDistanceScale,
        InterEyeFrameWidthPercent = sample.InterEyeFrameWidthPercent,
        ZConfidencePercent = sample.ZConfidencePercent,
        DistanceCalibrated = sample.DistanceCalibrated,
        ZUsesCameraFov = sample.ZUsesCameraFov,
        ZUsesLearnedReference = sample.ZUsesLearnedReference,
        ZEstimateKind = sample.ZEstimateKind,
        ZQualityLabel = sample.ZQualityLabel,
        RotationSource = sample.RotationSource,
        DistanceSource = sample.DistanceSource,
        ReferenceScaleSource = sample.ReferenceScaleSource,
        LeftBrowHeightRatio = sample.LeftBrowHeightRatio,
        RightBrowHeightRatio = sample.RightBrowHeightRatio,
        AverageBrowHeightRatio = sample.AverageBrowHeightRatio,
        BrowAsymmetryPercent = sample.BrowAsymmetryPercent,
        PossibleOneEyeArtifact = sample.PossibleOneEyeArtifact,
        LeftEyeReconstructed = sample.LeftEyeReconstructed,
        RightEyeReconstructed = sample.RightEyeReconstructed,
        MouthReconstructed = sample.MouthReconstructed,
        EyeArtifactSuppressed = sample.EyeArtifactSuppressed,
        CaptureQualityLabel = sample.CaptureQualityLabel,
        CaptureQualityScorePercent = sample.CaptureQualityScorePercent,
        GoodFeatureReason = sample.GoodFeatureReason,
        FacialTransformationMatrix = sample.FacialTransformationMatrix.ToList(),
        Points = sample.Points
            .Select(point => new FaceMeshLandmarkPoint
            {
                Index = point.Index,
                X = shiftedIndices.Contains(point.Index) ? point.X + xOffset : point.X,
                Y = shiftedIndices.Contains(point.Index) ? point.Y + yOffset : point.Y,
                Z = shiftedIndices.Contains(point.Index) ? point.Z + zOffset : point.Z
            })
            .ToList(),
        FeatureGroups = sample.FeatureGroups
            .Select(group => new LastGoodFeatureMeshFeatureGroup
            {
                Id = group.Id,
                Label = group.Label,
                Role = group.Role,
                Closed = group.Closed,
                ConfidencePercent = group.ConfidencePercent,
                LandmarkIndices = group.LandmarkIndices.ToList()
            })
            .ToList(),
        WireframeEdges = sample.WireframeEdges
            .Select(edge => new LastGoodFeatureMeshWireframeEdge
            {
                FromIndex = edge.FromIndex,
                ToIndex = edge.ToIndex,
                Role = edge.Role,
                Source = edge.Source,
                LengthPercent = edge.LengthPercent,
                ConfidencePercent = edge.ConfidencePercent
            })
            .ToList()
    };
}

static LastGoodFeatureMeshSample RotateLastGoodFeatureMeshSample(
    LastGoodFeatureMeshSample sample,
    double yawDegrees)
{
    return TransformLastGoodFeatureMeshSample(sample, yawDegrees: yawDegrees);
}

static LastGoodFeatureMeshSample TransformLastGoodFeatureMeshSample(
    LastGoodFeatureMeshSample sample,
    double yawDegrees = 0d,
    double pitchDegrees = 0d,
    double rollDegrees = 0d,
    double scale = 1d)
{
    var origin = new SyntheticMeshPoint(
        sample.Points.Average(static point => point.X),
        sample.Points.Average(static point => point.Y),
        sample.Points.Average(static point => point.Z));
    var yawRadians = yawDegrees * Math.PI / 180d;
    var pitchRadians = pitchDegrees * Math.PI / 180d;
    var rollRadians = rollDegrees * Math.PI / 180d;
    var yawCos = Math.Cos(yawRadians);
    var yawSin = Math.Sin(yawRadians);
    var pitchCos = Math.Cos(pitchRadians);
    var pitchSin = Math.Sin(pitchRadians);
    var rollCos = Math.Cos(rollRadians);
    var rollSin = Math.Sin(rollRadians);
    var rotatedPoints = sample.Points
        .Select(point =>
        {
            var dx = (point.X - origin.X) * scale;
            var dy = (point.Y - origin.Y) * scale;
            var dz = (point.Z - origin.Z) * scale;

            var pitchY = dy * pitchCos - dz * pitchSin;
            var pitchZ = dy * pitchSin + dz * pitchCos;
            dy = pitchY;
            dz = pitchZ;

            var yawX = dx * yawCos + dz * yawSin;
            var yawZ = -dx * yawSin + dz * yawCos;
            dx = yawX;
            dz = yawZ;

            var rollX = dx * rollCos - dy * rollSin;
            var rollY = dx * rollSin + dy * rollCos;
            dx = rollX;
            dy = rollY;

            return new FaceMeshLandmarkPoint
            {
                Index = point.Index,
                X = origin.X + dx,
                Y = origin.Y + dy,
                Z = origin.Z + dz
            };
        })
        .ToList();

    return new LastGoodFeatureMeshSample
    {
        SampleId = $"{sample.SampleId}-transform-y{yawDegrees:0.#}-p{pitchDegrees:0.#}-r{rollDegrees:0.#}-s{scale:0.##}",
        CapturedAtUtc = sample.CapturedAtUtc.AddMilliseconds(Math.Abs(yawDegrees) + Math.Abs(pitchDegrees) + Math.Abs(rollDegrees) + Math.Abs(scale - 1d) * 100d),
        Source = sample.Source,
        DenseMeshTopology = sample.DenseMeshTopology,
        PointCount = sample.PointCount,
        TrackingConfidencePercent = sample.TrackingConfidencePercent,
        EyeConfidencePercent = sample.EyeConfidencePercent,
        MouthConfidencePercent = sample.MouthConfidencePercent,
        OverallQualityPercent = sample.OverallQualityPercent,
        EyeQualityPercent = sample.EyeQualityPercent,
        MouthQualityPercent = sample.MouthQualityPercent,
        BrowQualityPercent = sample.BrowQualityPercent,
        FaceReliabilityPercent = sample.FaceReliabilityPercent,
        FaceContinuityPercent = sample.FaceContinuityPercent,
        EyeReliabilityPercent = sample.EyeReliabilityPercent,
        MouthReliabilityPercent = sample.MouthReliabilityPercent,
        HeadYawDegrees = yawDegrees,
        HeadPitchDegrees = pitchDegrees,
        HeadRollDegrees = rollDegrees,
        XHorizontalPercent = sample.XHorizontalPercent,
        YVerticalPercent = sample.YVerticalPercent,
        DistanceInches = sample.DistanceInches,
        ApparentDistanceUnits = sample.ApparentDistanceUnits,
        RelativeDistanceScale = sample.RelativeDistanceScale,
        InterEyeFrameWidthPercent = sample.InterEyeFrameWidthPercent,
        ZConfidencePercent = sample.ZConfidencePercent,
        DistanceCalibrated = sample.DistanceCalibrated,
        ZUsesCameraFov = sample.ZUsesCameraFov,
        ZUsesLearnedReference = sample.ZUsesLearnedReference,
        ZEstimateKind = sample.ZEstimateKind,
        ZQualityLabel = sample.ZQualityLabel,
        RotationSource = sample.RotationSource,
        DistanceSource = sample.DistanceSource,
        ReferenceScaleSource = sample.ReferenceScaleSource,
        LeftBrowHeightRatio = sample.LeftBrowHeightRatio,
        RightBrowHeightRatio = sample.RightBrowHeightRatio,
        AverageBrowHeightRatio = sample.AverageBrowHeightRatio,
        BrowAsymmetryPercent = sample.BrowAsymmetryPercent,
        PossibleOneEyeArtifact = sample.PossibleOneEyeArtifact,
        LeftEyeReconstructed = sample.LeftEyeReconstructed,
        RightEyeReconstructed = sample.RightEyeReconstructed,
        MouthReconstructed = sample.MouthReconstructed,
        EyeArtifactSuppressed = sample.EyeArtifactSuppressed,
        CaptureQualityLabel = sample.CaptureQualityLabel,
        CaptureQualityScorePercent = sample.CaptureQualityScorePercent,
        GoodFeatureReason = sample.GoodFeatureReason,
        FacialTransformationMatrix = sample.FacialTransformationMatrix.ToList(),
        Points = rotatedPoints,
        WireframeEdges = sample.WireframeEdges
            .Select(edge => new LastGoodFeatureMeshWireframeEdge
            {
                FromIndex = edge.FromIndex,
                ToIndex = edge.ToIndex,
                Role = edge.Role,
                Source = edge.Source,
                LengthPercent = edge.LengthPercent,
                ConfidencePercent = edge.ConfidencePercent
            })
            .ToList(),
        FeatureGroups = sample.FeatureGroups
            .Select(group => new LastGoodFeatureMeshFeatureGroup
            {
                Id = group.Id,
                Label = group.Label,
                Role = group.Role,
                Closed = group.Closed,
                ConfidencePercent = group.ConfidencePercent,
                LandmarkIndices = group.LandmarkIndices.ToList()
            })
            .ToList()
    };
}

static void ValidateMeasurementFacePreviewGeometry(MeasurementFacePreviewModel preview, string label)
{
    Require(preview.CanRender, $"{label} must be renderable before geometry validation");
    ValidateMeasurementFacePreviewScene(
        preview.Points,
        preview.Polylines,
        preview.MeasurementContributionPercent,
        preview.TemplatePriorContributionPercent,
        preview.GeometryProvenance,
        label);
}

static double PreviewMetric(MeasurementFacePreviewModel preview, string key)
{
    return preview.Metrics.TryGetValue(key, out var value) && value is double number
        ? number
        : 0d;
}

static double RoleZRange(MeasurementFacePreviewModel preview, string role)
{
    var values = preview.Points
        .Where(point => string.Equals(point.Role, role, StringComparison.OrdinalIgnoreCase))
        .Select(static point => point.Z)
        .ToList();
    return values.Count == 0 ? 0d : values.Max() - values.Min();
}

static void ValidateMeasurementFacePreviewSurfacePatches(
    IReadOnlyList<MeasurementFacePreviewPoint> points,
    IReadOnlyList<MeasurementFacePreviewSurfacePatch> patches,
    string label,
    bool requireMeasuredPatches)
{
    if (requireMeasuredPatches)
    {
        Require(patches.Count >= 8, $"{label} has too few measured surface patches: {patches.Count}");
        var requiredRoles = new[] { "eye", "mouth", "mouth-opening", "jaw", "nose", "brow", "forehead", "cheek" };
        var roles = patches.Select(static patch => patch.Role).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var role in requiredRoles)
        {
            Require(roles.Contains(role), $"{label} omitted measured {role} surface patch");
        }
    }

    var pointMap = points.ToDictionary(static point => point.Id, StringComparer.OrdinalIgnoreCase);
    var patchIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var patch in patches)
    {
        Require(!string.IsNullOrWhiteSpace(patch.Id), $"{label} emitted a surface patch without an id");
        Require(patchIds.Add(patch.Id), $"{label} emitted duplicate surface patch id '{patch.Id}'");
        Require(!string.IsNullOrWhiteSpace(patch.Role), $"{label} surface patch '{patch.Id}' omitted a role");
        Require(!string.IsNullOrWhiteSpace(patch.Provenance), $"{label} surface patch '{patch.Id}' omitted provenance");
        Require(patch.ConfidencePercent is >= 0d and <= 100d, $"{label} surface patch '{patch.Id}' has invalid confidence: {patch.ConfidencePercent}");
        Require(patch.FillOpacity is >= 0d and <= 1d, $"{label} surface patch '{patch.Id}' has invalid fill opacity: {patch.FillOpacity}");
        Require(!string.IsNullOrWhiteSpace(patch.CenterPointId), $"{label} surface patch '{patch.Id}' omitted its center point id");
        Require(patch.PointIds.Count >= 3, $"{label} surface patch '{patch.Id}' has too few point references");
        Require(patch.TriangleCount == patch.Triangles.Count, $"{label} surface patch '{patch.Id}' triangle count drifted from triangle cells");
        Require(patch.Triangles.Count >= patch.PointIds.Count, $"{label} surface patch '{patch.Id}' has too few measured triangles: {patch.Triangles.Count}");
        Require(patch.SurfaceArea > 0d, $"{label} surface patch '{patch.Id}' omitted surface area");
        Require(patch.AverageTriangleArea > 0d, $"{label} surface patch '{patch.Id}' omitted average triangle area");
        Require(patch.DepthRelief >= 0d, $"{label} surface patch '{patch.Id}' emitted invalid depth relief");
        Require(double.IsFinite(patch.AverageNormalX) && double.IsFinite(patch.AverageNormalY) && double.IsFinite(patch.AverageNormalZ), $"{label} surface patch '{patch.Id}' emitted non-finite normal");
        var normalMagnitude = Math.Sqrt(
            patch.AverageNormalX * patch.AverageNormalX
            + patch.AverageNormalY * patch.AverageNormalY
            + patch.AverageNormalZ * patch.AverageNormalZ);
        Require(normalMagnitude is > 0.90d and < 1.10d, $"{label} surface patch '{patch.Id}' normal was not unit length: {normalMagnitude}");
        Require(patch.AverageNormalZ >= 0d, $"{label} surface patch '{patch.Id}' average normal does not face the camera");
        Require(patch.NormalConsistencyPercent is >= 0d and <= 100d, $"{label} surface patch '{patch.Id}' has invalid normal consistency: {patch.NormalConsistencyPercent}");
        Require(patch.GeometryHealthPercent is >= 0d and <= 100d, $"{label} surface patch '{patch.Id}' has invalid geometry health: {patch.GeometryHealthPercent}");
        Require(!string.IsNullOrWhiteSpace(patch.GeometryStatus), $"{label} surface patch '{patch.Id}' omitted geometry status");
        Require(!string.IsNullOrWhiteSpace(patch.GeometryFinding), $"{label} surface patch '{patch.Id}' omitted geometry finding");
        if (requireMeasuredPatches)
        {
            Require(patch.NormalConsistencyPercent > 40d, $"{label} measured surface patch '{patch.Id}' normal consistency is too weak: {patch.NormalConsistencyPercent}");
        }

        var patchPointIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var patchPoints = new List<MeasurementFacePreviewPoint>(patch.PointIds.Count);
        foreach (var pointId in patch.PointIds)
        {
            Require(patchPointIds.Add(pointId), $"{label} surface patch '{patch.Id}' repeats point '{pointId}'");
            if (!pointMap.TryGetValue(pointId, out var point))
            {
                throw new InvalidOperationException($"{label} surface patch '{patch.Id}' references missing point '{pointId}'");
            }

            patchPoints.Add(point);
            if (requireMeasuredPatches)
            {
                Require(
                    point.Provenance.Contains("personal", StringComparison.OrdinalIgnoreCase),
                    $"{label} measured surface patch '{patch.Id}' referenced non-personal point '{pointId}'");
            }
        }

        var projectedPatch = patchPoints
            .Select(point => ProjectMeasurementFacePreviewPoint(point, 960d, 560d, -0.34d, -0.08d, 1d))
            .ToList();
        var span = CalculateProjectedSpan(projectedPatch);
        Require(span > 1.0d, $"{label} surface patch '{patch.Id}' collapses to a degenerate 3D projection");

        var referencedBoundaryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var triangle in patch.Triangles)
        {
            Require(triangle.PointIds.Count == 3, $"{label} surface patch '{patch.Id}' emitted a non-triangle cell");
            var trianglePointIds = triangle.PointIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            Require(trianglePointIds.Count == 3, $"{label} surface patch '{patch.Id}' emitted a triangle with repeated points");
            Require(trianglePointIds.Contains(patch.CenterPointId), $"{label} surface patch '{patch.Id}' emitted a triangle without the center point");
            var trianglePoints = new List<MeasurementFacePreviewPoint>(3);
            foreach (var pointId in triangle.PointIds)
            {
                if (!pointMap.TryGetValue(pointId, out var point))
                {
                    throw new InvalidOperationException($"{label} surface patch '{patch.Id}' triangle references missing point '{pointId}'");
                }

                trianglePoints.Add(point);
                if (patchPointIds.Contains(pointId))
                {
                    referencedBoundaryIds.Add(pointId);
                }

                if (requireMeasuredPatches)
                {
                    Require(
                        point.Provenance.Contains("personal", StringComparison.OrdinalIgnoreCase),
                        $"{label} measured surface patch '{patch.Id}' triangle referenced non-personal point '{pointId}'");
                }
            }

            Require(
                trianglePointIds.Count(pointId => patchPointIds.Contains(pointId)) >= 2,
                $"{label} surface patch '{patch.Id}' triangle does not attach to two measured boundary points");
            var projectedTriangle = trianglePoints
                .Select(point => ProjectMeasurementFacePreviewPoint(point, 960d, 560d, -0.34d, -0.08d, 1d))
                .ToList();
            var area = CalculateProjectedTriangleArea(projectedTriangle);
            Require(area > 0.05d, $"{label} surface patch '{patch.Id}' emitted a degenerate projected triangle");
        }

        foreach (var pointId in patchPointIds)
        {
            Require(referencedBoundaryIds.Contains(pointId), $"{label} surface patch '{patch.Id}' boundary point '{pointId}' was not used by a triangle");
        }
    }
}

static void ValidateMeasurementFacePreviewScene(
    IReadOnlyList<MeasurementFacePreviewPoint> points,
    IReadOnlyList<MeasurementFacePreviewPolyline> polylines,
    double measurementContributionPercent,
    double templatePriorContributionPercent,
    string geometryProvenance,
    string label)
{
    Require(points.Count >= 60, $"{label} 3D scene is too sparse: {points.Count} points");
    Require(polylines.Count >= 7, $"{label} 3D scene has too few connected feature paths: {polylines.Count}");
    Require(
        Math.Abs(measurementContributionPercent + templatePriorContributionPercent - 100d) < 0.001d,
        $"{label} measured/template contributions do not sum to 100%");
    Require(!string.IsNullOrWhiteSpace(geometryProvenance), $"{label} omitted geometry provenance");

    var pointIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var point in points)
    {
        Require(!string.IsNullOrWhiteSpace(point.Id), $"{label} emitted a point without an id");
        Require(pointIds.Add(point.Id), $"{label} emitted duplicate point id '{point.Id}'");
        Require(!string.IsNullOrWhiteSpace(point.Role), $"{label} point '{point.Id}' omitted a role");
        Require(!string.IsNullOrWhiteSpace(point.Provenance), $"{label} point '{point.Id}' omitted provenance");
        Require(double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z), $"{label} point '{point.Id}' has non-finite coordinates");
        Require(Math.Abs(point.X) <= 1.05d, $"{label} point '{point.Id}' X is outside normalized preview bounds: {point.X}");
        Require(Math.Abs(point.Y) <= 1.05d, $"{label} point '{point.Id}' Y is outside normalized preview bounds: {point.Y}");
        Require(Math.Abs(point.Z) <= 0.75d, $"{label} point '{point.Id}' Z is outside normalized preview bounds: {point.Z}");
        Require(point.ConfidencePercent is >= 0d and <= 100d, $"{label} point '{point.Id}' has invalid confidence: {point.ConfidencePercent}");
        if (templatePriorContributionPercent >= 99.999d)
        {
            Require(
                point.Provenance.Contains("template", StringComparison.OrdinalIgnoreCase),
                $"{label} template-only point '{point.Id}' was not tagged as template provenance");
        }
    }

    var roles = polylines.Select(static line => line.Role).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Require(roles.Contains("face"), $"{label} omitted the face outline path");
    Require(roles.Contains("eye"), $"{label} omitted eye paths");
    Require(roles.Contains("mouth"), $"{label} omitted outer lip paths");
    Require(roles.Contains("mouth-opening"), $"{label} omitted mouth-opening paths");
    Require(roles.Contains("jaw"), $"{label} omitted jaw paths");
    Require(roles.Contains("jaw-droop"), $"{label} omitted jaw-droop paths");
    Require(roles.Contains("nose"), $"{label} omitted nose pose paths");

    var pointMap = points.ToDictionary(static point => point.Id, StringComparer.OrdinalIgnoreCase);
    foreach (var line in polylines)
    {
        Require(!string.IsNullOrWhiteSpace(line.Id), $"{label} emitted a polyline without an id");
        Require(!string.IsNullOrWhiteSpace(line.Role), $"{label} polyline '{line.Id}' omitted a role");
        Require(!string.IsNullOrWhiteSpace(line.Provenance), $"{label} polyline '{line.Id}' omitted provenance");
        Require(line.ConfidencePercent is >= 0d and <= 100d, $"{label} polyline '{line.Id}' has invalid confidence: {line.ConfidencePercent}");
        Require(line.PointIds.Count >= 2, $"{label} polyline '{line.Id}' has too few point references");
        var linePoints = line.PointIds
            .Select(pointId =>
            {
                Require(pointMap.ContainsKey(pointId), $"{label} polyline '{line.Id}' references missing point '{pointId}'");
                return pointMap[pointId];
            })
            .ToList();
        var projectedLine = linePoints.Select(point => ProjectMeasurementFacePreviewPoint(point, 960d, 560d, -0.34d, -0.08d, 1d)).ToList();
        var span = CalculateProjectedSpan(projectedLine);
        Require(span > 1.0d, $"{label} polyline '{line.Id}' collapses to a degenerate 3D projection");
    }

    var projected = points.Select(point => ProjectMeasurementFacePreviewPoint(point, 960d, 560d, -0.34d, -0.08d, 1d)).ToList();
    var spanX = projected.Max(static point => point.X) - projected.Min(static point => point.X);
    var spanY = projected.Max(static point => point.Y) - projected.Min(static point => point.Y);
    Require(spanX > 120d, $"{label} projected 3D scene is too narrow: {spanX}");
    Require(spanY > 160d, $"{label} projected 3D scene is too short: {spanY}");
    var insideCanvas = projected.Count(static point => point.X is >= -40d and <= 1000d && point.Y is >= -40d and <= 600d);
    Require(insideCanvas >= projected.Count * 0.94d, $"{label} projected too many points outside the 3D canvas");

    var depthCuePoint = points.OrderByDescending(static point => Math.Abs(point.Z)).First();
    var leftYaw = ProjectMeasurementFacePreviewPoint(depthCuePoint, 960d, 560d, -0.34d, -0.08d, 1d);
    var rightYaw = ProjectMeasurementFacePreviewPoint(depthCuePoint, 960d, 560d, 0.34d, -0.08d, 1d);
    Require(
        Math.Abs(leftYaw.X - rightYaw.X) > 8d,
        $"{label} did not respond visibly to B rotation around depth point '{depthCuePoint.Id}'");

    var edgePoint = points.OrderByDescending(static point => Math.Abs(point.X) + Math.Abs(point.Y)).First();
    var baseZoom = ProjectMeasurementFacePreviewPoint(edgePoint, 960d, 560d, -0.34d, -0.08d, 1d);
    var wheelZoom = ProjectMeasurementFacePreviewPoint(edgePoint, 960d, 560d, -0.34d, -0.08d, 1.5d);
    Require(
        DistanceFromCanvasCenter(wheelZoom, 960d, 560d) > DistanceFromCanvasCenter(baseZoom, 960d, 560d) * 1.20d,
        $"{label} did not respond visibly to zoom around edge point '{edgePoint.Id}'");
}

static string ExtractApplicationJsonScript(string html, string id)
{
    var startMarker = $"<script type=\"application/json\" id=\"{id}\">";
    var start = html.IndexOf(startMarker, StringComparison.Ordinal);
    Require(start >= 0, $"HTML did not include JSON script '{id}'");
    start += startMarker.Length;
    var end = html.IndexOf("</script>", start, StringComparison.OrdinalIgnoreCase);
    Require(end > start, $"HTML JSON script '{id}' was not closed");
    return html[start..end].Trim();
}

static MeasurementPreviewProjectedPoint ProjectMeasurementFacePreviewPoint(
    MeasurementFacePreviewPoint point,
    double width,
    double height,
    double yaw,
    double pitch,
    double zoomFactor)
{
    var cosY = Math.Cos(yaw);
    var sinY = Math.Sin(yaw);
    var cosP = Math.Cos(pitch);
    var sinP = Math.Sin(pitch);
    var x1 = point.X * cosY + point.Z * sinY;
    var z1 = -point.X * sinY + point.Z * cosY;
    var y1 = point.Y * cosP - z1 * sinP;
    var z2 = point.Y * sinP + z1 * cosP;
    var depth = 1.45d + z2;
    var zoom = Math.Min(width, height) * 0.82d * zoomFactor / Math.Max(0.35d, depth);
    return new MeasurementPreviewProjectedPoint(
        width * 0.5d + x1 * zoom,
        height * 0.52d + y1 * zoom,
        z2);
}

static double CalculateProjectedSpan(IReadOnlyList<MeasurementPreviewProjectedPoint> points)
{
    if (points.Count == 0)
    {
        return 0d;
    }

    var spanX = points.Max(static point => point.X) - points.Min(static point => point.X);
    var spanY = points.Max(static point => point.Y) - points.Min(static point => point.Y);
    return Math.Sqrt(spanX * spanX + spanY * spanY);
}

static double CalculateProjectedTriangleArea(IReadOnlyList<MeasurementPreviewProjectedPoint> points)
{
    if (points.Count != 3)
    {
        return 0d;
    }

    return Math.Abs(
        points[0].X * (points[1].Y - points[2].Y)
        + points[1].X * (points[2].Y - points[0].Y)
        + points[2].X * (points[0].Y - points[1].Y)) / 2d;
}

static double DistanceFromCanvasCenter(MeasurementPreviewProjectedPoint point, double width, double height)
{
    var dx = point.X - width * 0.5d;
    var dy = point.Y - height * 0.52d;
    return Math.Sqrt(dx * dx + dy * dy);
}

static void RunMeasurementAvatarTrainingPackageSmoke()
{
    var model = new PersonalFaceModel
    {
        SubjectId = "chris",
        SubjectDisplayName = "Chris",
        SubjectCollectionMode = PersonalFaceSubject.ManualConfirmationMode,
        UnknownSubjectPolicy = PersonalFaceSubject.UnknownSubjectPolicy,
        IdentityGatePolicy = PersonalFaceSubject.IdentityGatePolicy,
        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-30),
        UpdatedAtUtc = DateTime.UtcNow,
        ObservedSamples = 220,
        AcceptedSamples = 160,
        RejectedSamples = 60,
        AcceptedSampleWeight = 144d,
        AverageFaceReliabilityPercent = 91d,
        AverageFaceContinuityPercent = 88d,
        AverageEyeReliabilityPercent = 87d,
        AverageMouthReliabilityPercent = 85d,
        FaceCenterX = Distribution(0.51d),
        FaceCenterY = Distribution(0.47d),
        FaceWidth = Distribution(0.40d, spread: 0.08d),
        FaceHeight = Distribution(0.58d, spread: 0.09d),
        HeadYawDegrees = Distribution(2.4d, spread: 16d),
        HeadPitchDegrees = Distribution(-1.2d, spread: 9d),
        HeadRollDegrees = Distribution(0.8d, spread: 7d),
        LeftEyeOpeningRatio = Distribution(0.27d, spread: 0.055d),
        RightEyeOpeningRatio = Distribution(0.25d, spread: 0.052d),
        AverageEyeOpeningRatio = Distribution(0.26d, spread: 0.06d),
        EyeAgreementPercent = Distribution(92d, spread: 4d),
        MouthOpeningRatio = Distribution(0.08d, spread: 0.12d),
        JawDroopRatio = Distribution(0.025d, spread: 0.08d),
        MediaPipeAverageEyeBlinkPercent = Distribution(18d, spread: 26d),
        MediaPipeJawOpenPercent = Distribution(7d, spread: 24d),
        MediaPipeMouthClosePercent = Distribution(80d, spread: 20d),
        EyeGlarePercent = Distribution(11d, spread: 5d),
        EyeContrastPercent = Distribution(68d, spread: 7d),
        EyeSharpnessPercent = Distribution(74d, spread: 6d),
        IdentitySignatureSamples = 160,
        FaceAspectRatio = Distribution(1.45d, 160, 144d, 0.025d),
        InterEyeDistanceToFaceWidth = Distribution(0.38d, 160, 144d, 0.018d),
        LeftEyeWidthToFaceWidth = Distribution(0.18d, 160, 144d, 0.012d),
        RightEyeWidthToFaceWidth = Distribution(0.18d, 160, 144d, 0.012d),
        MouthWidthToFaceWidth = Distribution(0.34d, 160, 144d, 0.02d),
        EyeMidlineYToFaceHeight = Distribution(0.32d, 160, 144d, 0.018d),
        MouthCenterYToFaceHeight = Distribution(0.66d, 160, 144d, 0.02d),
        EyeToMouthYDistanceToFaceHeight = Distribution(0.34d, 160, 144d, 0.018d),
        LeftEyeShape = ShapeProfile("left_eye_shape", "Left eye contour shape", 8, closed: true, sampleCount: 160, totalWeight: 144d),
        RightEyeShape = ShapeProfile("right_eye_shape", "Right eye contour shape", 8, closed: true, sampleCount: 160, totalWeight: 144d),
        OuterLipShape = ShapeProfile("outer_lip_shape", "Outer lip contour shape", 12, closed: true, sampleCount: 150, totalWeight: 132d),
        InnerLipShape = ShapeProfile("inner_lip_shape", "Inner lip contour shape", 10, closed: true, sampleCount: 150, totalWeight: 132d),
        JawShape = ShapeProfile("jaw_shape", "Jaw contour shape", 9, closed: false, sampleCount: 150, totalWeight: 132d),
        LeftBrowShape = SurfaceProfile("left_brow_shape", "Left brow 3D shape", 10, sampleCount: 150, totalWeight: 132d, depth: 0.035d),
        RightBrowShape = SurfaceProfile("right_brow_shape", "Right brow 3D shape", 10, sampleCount: 150, totalWeight: 132d, depth: 0.035d),
        NoseBridgeShape = SurfaceProfile("nose_bridge_shape", "Nose bridge 3D shape", 10, sampleCount: 150, totalWeight: 132d, depth: 0.09d),
        NoseBaseShape = SurfaceProfile("nose_base_shape", "Nose base 3D shape", 5, sampleCount: 150, totalWeight: 132d, depth: 0.065d),
        LeftCheekSurface = SurfaceProfile("left_cheek_surface", "Left cheek 3D surface", 6, sampleCount: 150, totalWeight: 132d, depth: 0.025d),
        RightCheekSurface = SurfaceProfile("right_cheek_surface", "Right cheek 3D surface", 6, sampleCount: 150, totalWeight: 132d, depth: 0.025d),
        ForeheadSurface = SurfaceProfile("forehead_surface", "Forehead 3D surface", 9, sampleCount: 150, totalWeight: 132d, depth: 0.02d),
        PoseBuckets = PoseBucketProfiles(sampleCount: 72, totalWeight: 64d)
    };

    var start = DateTime.UtcNow.AddMinutes(-4);
    var observations = Enumerable.Range(0, 90)
        .Select(index =>
        {
            var progress = index / 89d;
            return new PersonalFaceMotionObservation
            {
                SubjectId = model.SubjectId,
                SubjectDisplayName = model.SubjectDisplayName,
                SubjectCollectionMode = model.SubjectCollectionMode,
                CapturedAtUtc = start.AddSeconds(index * 2),
                AcceptedForPersonalModel = true,
                Source = "avatar package smoke",
                SampleWeight = 1d,
                OverallQualityPercent = 86d,
                FaceReliabilityPercent = 90d,
                FaceContinuityPercent = 88d,
                EyeReliabilityPercent = 86d,
                MouthReliabilityPercent = 84d,
                HeadYawDegrees = -12d + progress * 24d,
                HeadPitchDegrees = -5d + progress * 10d,
                HeadRollDegrees = -4d + progress * 8d,
                AverageEyeOpeningRatio = 0.29d - progress * 0.14d,
                MouthOpeningRatio = 0.05d + progress * 0.18d,
                JawDroopRatio = 0.01d + progress * 0.11d,
                MediaPipeAverageEyeBlinkPercent = 8d + progress * 54d,
                MediaPipeJawOpenPercent = 5d + progress * 48d,
                MediaPipeMouthClosePercent = 88d - progress * 42d
            };
        })
        .ToList();
    var motionModel = new PersonalFaceMotionModelBuilder().Build(observations);
    var readiness = new PersonalFaceCorpusReadinessBuilder().Build(
        model,
        motionModel,
        [],
        measurementJournalBytes: 12_345L,
        measurementBudgetBytes: 1_000_000L);
    var acceptedGate = FaceReconstructionSubjectGate.FromPersonalModel(
        model,
        manualSubjectConfirmed: true,
        identityConfidencePercent: 95d,
        reason: "avatar package smoke subject confirmed");
    var packageAudit = new PersonalFaceCollectionAuditBuilder().Build(
        model,
        observations.Select((observation, index) => new PersonalFaceCollectionAuditObservation
        {
            ReviewedAtUtc = observation.CapturedAtUtc,
            SubjectConfirmed = true,
            PausedForEventOrCalibration = false,
            HasFace = true,
            PersonalModelAccepted = true,
            PersonalModelRejectionKind = "",
            PersonalModelUpdateReason = "avatar package smoke accepted",
            CaptureQualityLabel = "avatar-grade",
            CaptureQualityScorePercent = 88d,
            CaptureQualityCanCollect = true,
            CaptureQualityAvatarGrade = true,
            CaptureQualityReason = "strong synthetic avatar package audit frame",
            CaptureQualityCameraModeScorePercent = 92d,
            CaptureQualityFaceScaleScorePercent = 90d,
            CaptureQualityEyeScorePercent = 86d,
            CaptureQualityMouthScorePercent = 84d,
            CaptureQualityStabilityScorePercent = 87d,
            CaptureQualityGlassesScorePercent = 82d,
            CaptureQualityStorageScorePercent = 100d,
            CaptureQualityFaceWidthPercent = 40d,
            CaptureQualityFaceHeightPercent = 58d,
            IdentityMeasurementAvailable = true,
            IdentityAutoGateReady = true,
            IdentityWarmupStrongMismatchGateReady = true,
            IdentityConfidencePercent = 94d - index % 5,
            IdentityComparedFeatureCount = 8,
            IdentityOutlierFeatureCount = index % 37 == 0 ? 1 : 0,
            IdentityStatus = "accepted"
        }).ToList());

    var package = new MeasurementAvatarTrainingPackageBuilder().Build(
        model,
        motionModel,
        readiness,
        acceptedGate,
        measurementJournalBytes: 12_345L,
        measurementBudgetBytes: 1_000_000L,
        collectionAudit: packageAudit);
    Require(package.CanUseForAvatarTraining, "measurement avatar package did not allow subject-gated training use");
    Require(package.NeutralFaceProfile.ContainsKey("AverageEyeOpeningRatio"), "measurement avatar package omitted average eye opening");
    Require(package.MotionProfile.ContainsKey("EyeClosingVelocityPerSecond"), "measurement avatar package omitted eye closing velocity");
    Require(package.MotionProfile.ContainsKey("MouthOpeningWithJawDroopRate"), "measurement avatar package omitted mouth/jaw coupling");
    Require(package.IdentityProfile.ContainsKey("InterEyeDistanceToFaceWidth"), "measurement avatar package omitted identity eye spacing");
    Require(package.PoseCoverageProfile.Any(static bucket => bucket.BucketId == PersonalFacePoseBuckets.FrontNeutral && bucket.SampleCount > 0), "measurement avatar package omitted front-neutral pose bucket");
    Require(package.Readiness.PoseBucketCoveragePercent > 0d, "measurement avatar package omitted pose bucket readiness");
    Require(package.ContourShapeProfiles.ContainsKey("left_eye_shape") && package.ContourShapeProfiles.ContainsKey("inner_lip_shape"), "measurement avatar package omitted aggregate contour shape profiles");
    Require(package.ContourShapeProfiles.ContainsKey("nose_bridge_shape") && package.ContourShapeProfiles.ContainsKey("left_cheek_surface"), "measurement avatar package omitted aggregate surface shape profiles");
    Require(package.Readiness.ContourShapeCoveragePercent > 0d, "measurement avatar package omitted contour shape readiness");
    Require(package.Readiness.SurfaceShapeCoveragePercent > 0d, "measurement avatar package omitted surface shape readiness");
    Require(package.Readiness.SurfaceGeometryHealthPercent > 0d, "measurement avatar package omitted surface geometry readiness");
    Require(package.Readiness.XYZABCCoveragePercent > 0d, "measurement avatar package omitted XYZABC readiness");
    Require(package.Readiness.DirectFeatureMeasurementTrustPercent > 0d, "measurement avatar package omitted direct feature trust readiness");
    Require(package.Readiness.ApertureConsistencyHealthPercent > 0d, "measurement avatar package omitted aperture consistency readiness");
    Require(package.Readiness.EyeApertureReliabilityHealthPercent > 0d, "measurement avatar package omitted eye aperture reliability readiness");
    Require(package.Readiness.DataAuditHealthPercent > 0d, "measurement avatar package omitted data audit readiness");
    Require(package.Readiness.PoseEstimationHealthPercent > 0d, "measurement avatar package omitted pose estimation audit readiness");
    Require(package.Readiness.FeatureAnchoringHealthPercent > 0d, "measurement avatar package omitted feature anchoring audit readiness");
    Require(package.Readiness.PoseExplainedFeatureMotionHealthPercent > 0d, "measurement avatar package omitted pose-explained feature motion readiness");
    Require(package.Readiness.PoseBucketConsistencyHealthPercent > 0d, "measurement avatar package omitted pose-bucket consistency readiness");
    Require(package.Readiness.IdentitySessionHealthPercent > 0d, "measurement avatar package omitted identity-session readiness");
    Require(!string.IsNullOrWhiteSpace(package.Readiness.IdentitySessionAuditStage), "measurement avatar package omitted identity-session audit stage");
    Require(!string.IsNullOrWhiteSpace(package.Readiness.IdentitySessionAuditStatus), "measurement avatar package omitted identity-session audit status");
    Require(package.PoseBucketConsistency.ComparedPoseBucketCount > 0, "measurement avatar package omitted pose-bucket consistency comparisons");
    Require(package.QualityProfile.ContainsKey("EyeGlarePercent"), "measurement avatar package omitted eye glare quality context");
    Require(package.QualityProfile.ContainsKey("ApertureConsistencyHealthPercent"), "measurement avatar package omitted aperture consistency quality context");
    Require(package.QualityProfile.ContainsKey("EyeApertureReliabilityHealthPercent"), "measurement avatar package omitted eye aperture reliability quality context");
    Require(package.QualityProfile.ContainsKey("PossibleOneEyeArtifactRate"), "measurement avatar package omitted possible one-eye artifact quality context");
    Require(package.QualityProfile.ContainsKey("DataAuditHealthPercent"), "measurement avatar package omitted data audit quality context");
    Require(package.QualityProfile.ContainsKey("PoseExplainedFeatureMotionHealthPercent"), "measurement avatar package omitted pose-explained feature motion quality context");
    Require(package.QualityProfile.ContainsKey("PoseBucketConsistencyHealthPercent"), "measurement avatar package omitted pose-bucket consistency quality context");
    Require(package.QualityProfile.ContainsKey("SurfaceGeometryHealthPercent"), "measurement avatar package omitted surface geometry quality context");
    Require(package.QualityProfile.ContainsKey("IdentitySessionHealthPercent"), "measurement avatar package omitted identity-session quality context");
    Require(package.QualityProfile.ContainsKey("MinimumTrackedDistributionWeight"), "measurement avatar package omitted weakest tracked distribution weight quality context");
    Require(package.NeutralFaceProfile.ContainsKey("ARotationAroundXDegrees"), "measurement avatar package omitted A/X neutral rotation context");
    Require(package.NeutralFaceProfile.ContainsKey("BRotationAroundYDegrees"), "measurement avatar package omitted B/Y neutral rotation context");
    Require(package.NeutralFaceProfile.ContainsKey("CRotationAroundZDegrees"), "measurement avatar package omitted C/Z neutral rotation context");
    Require(package.MotionProfile.ContainsKey("ARotationAroundXVelocityDegreesPerSecond"), "measurement avatar package omitted A/X rotation velocity context");
    Require(package.MotionProfile.ContainsKey("BRotationAroundYVelocityDegreesPerSecond"), "measurement avatar package omitted B/Y rotation velocity context");
    Require(package.MotionProfile.ContainsKey("CRotationAroundZVelocityDegreesPerSecond"), "measurement avatar package omitted C/Z rotation velocity context");
    Require(package.QualityProfile.ContainsKey("BRotationAroundYRangeDegrees"), "measurement avatar package omitted B/Y range audit context");
    Require(package.QualityProfile.ContainsKey("InterEyeDistanceToFaceWidthRange"), "measurement avatar package omitted face-local feature drift context");
    Require(package.QualityProfile.ContainsKey("AverageIdentityConfidencePercent"), "measurement avatar package omitted collection identity confidence context");
    Require(package.QualityProfile.ContainsKey("IdentityOutlierFrames"), "measurement avatar package omitted identity outlier frame context");
    Require(package.QualityProfile.ContainsKey("TrackingAuditHoldFrames"), "measurement avatar package omitted tracking audit hold context");
    Require(package.SourceArtifacts.All(static artifact => !artifact.ContainsRawPixels && !artifact.ContainsRawContinuousVideo), "measurement avatar package marked source artifacts as raw media");
    Require(package.SourceArtifacts.Any(static artifact => artifact.Kind == "low-trust-template-prior"), "measurement avatar package omitted low-trust template prior artifact");
    Require(package.SourceArtifacts.Any(static artifact => artifact.Kind == "data-audit"), "measurement avatar package omitted data audit artifact");
    Require(package.MeasurementContributionPercent > 90d, $"measurement avatar package measured contribution too low: {package.MeasurementContributionPercent}");
    Require(package.TemplatePriorContributionPercent < 10d, $"measurement avatar package retained too much template prior: {package.TemplatePriorContributionPercent}");
    Require(package.SafetyBoundary.Contains("digital representation", StringComparison.OrdinalIgnoreCase), "measurement avatar package omitted digital-representation safety boundary");

    var blockedGate = FaceReconstructionSubjectGate.FromPersonalModel(
        model,
        manualSubjectConfirmed: false,
        identityConfidencePercent: 95d);
    var blockedPackage = new MeasurementAvatarTrainingPackageBuilder().Build(
        model,
        motionModel,
        readiness,
        blockedGate,
        measurementJournalBytes: 12_345L,
        measurementBudgetBytes: 1_000_000L);
    Require(!blockedPackage.CanUseForAvatarTraining, "measurement avatar package allowed training use with an unconfirmed subject gate");

    var seedPackageModel = new PersonalFaceModel
    {
        SubjectId = "chris",
        SubjectDisplayName = "Chris",
        SubjectCollectionMode = PersonalFaceSubject.ManualConfirmationMode,
        UnknownSubjectPolicy = PersonalFaceSubject.UnknownSubjectPolicy,
        IdentityGatePolicy = PersonalFaceSubject.IdentityGatePolicy
    };
    var seedPackageGate = FaceReconstructionSubjectGate.FromPersonalModel(
        seedPackageModel,
        manualSubjectConfirmed: true,
        reason: "seed package subject confirmed");
    var seedPackage = new MeasurementAvatarTrainingPackageBuilder().Build(
        seedPackageModel,
        new PersonalFaceMotionModel(),
        new PersonalFaceCorpusReadiness(),
        seedPackageGate,
        measurementJournalBytes: 0L,
        measurementBudgetBytes: 1_000_000L);
    Require(!seedPackage.CanUseForAvatarTraining, "template-prior-only package allowed avatar training use");
    Require(seedPackage.TemplatePriorContributionPercent == 100d, $"template-prior-only package contribution was not 100%: {seedPackage.TemplatePriorContributionPercent}");
    Require(seedPackage.MeasurementContributionPercent == 0d, $"template-prior-only package measured contribution was not 0%: {seedPackage.MeasurementContributionPercent}");

    var packageRoot = Path.Combine(Path.GetTempPath(), $"AvatarBuilderAvatarPackageSmoke-{Guid.NewGuid():N}");
    var files = new MeasurementAvatarTrainingPackageStore().Write(packageRoot, package);
    Require(File.Exists(files.JsonPath), "measurement avatar package JSON was not written");
    Require(File.Exists(files.HtmlPath), "measurement avatar package HTML was not written");
    var json = File.ReadAllText(files.JsonPath);
    var html = File.ReadAllText(files.HtmlPath);
    Require(json.Contains("\"SchemaVersion\": \"measurement-avatar-training-package-v1\"", StringComparison.Ordinal), "measurement avatar package JSON used the wrong schema");
    Require(json.Contains("\"ContourShapeProfiles\"", StringComparison.Ordinal), "measurement avatar package JSON did not include aggregate contour shape profiles");
    Require(json.Contains("\"PoseCoverageProfile\"", StringComparison.Ordinal), "measurement avatar package JSON did not include pose coverage profile");
    Require(json.Contains("\"PoseBucketCoveragePercent\"", StringComparison.Ordinal), "measurement avatar package JSON did not include pose bucket readiness");
    Require(json.Contains("\"ContourShapeCoveragePercent\"", StringComparison.Ordinal), "measurement avatar package JSON did not include contour shape readiness");
    Require(json.Contains("\"SurfaceShapeCoveragePercent\"", StringComparison.Ordinal), "measurement avatar package JSON did not include surface shape readiness");
    Require(json.Contains("\"SurfaceGeometryHealthPercent\"", StringComparison.Ordinal), "measurement avatar package JSON did not include surface geometry readiness");
    Require(json.Contains("\"XYZABCCoveragePercent\"", StringComparison.Ordinal), "measurement avatar package JSON did not include XYZABC readiness");
    Require(json.Contains("\"nose_bridge_shape\"", StringComparison.Ordinal), "measurement avatar package JSON did not include nose bridge surface profile");
    Require(json.Contains("\"DirectFeatureMeasurementTrustPercent\"", StringComparison.Ordinal), "measurement avatar package JSON did not include direct feature trust");
    Require(json.Contains("\"ApertureConsistencyHealthPercent\"", StringComparison.Ordinal), "measurement avatar package JSON did not include aperture consistency health");
    Require(json.Contains("\"ApertureConsistency\"", StringComparison.Ordinal), "measurement avatar package JSON did not include aperture consistency report");
    Require(json.Contains("\"EyeApertureReliabilityHealthPercent\"", StringComparison.Ordinal), "measurement avatar package JSON did not include eye aperture reliability health");
    Require(json.Contains("\"PossibleOneEyeArtifactRate\"", StringComparison.Ordinal), "measurement avatar package JSON did not include possible one-eye artifact rate");
    Require(json.Contains("\"DataAuditHealthPercent\"", StringComparison.Ordinal), "measurement avatar package JSON did not include data audit health");
    Require(json.Contains("\"PoseEstimationHealthPercent\"", StringComparison.Ordinal), "measurement avatar package JSON did not include pose estimation health");
    Require(json.Contains("\"FeatureAnchoringHealthPercent\"", StringComparison.Ordinal), "measurement avatar package JSON did not include feature anchoring health");
    Require(json.Contains("\"PoseExplainedFeatureMotionHealthPercent\"", StringComparison.Ordinal), "measurement avatar package JSON did not include pose-explained feature motion health");
    Require(json.Contains("\"PoseBucketConsistencyHealthPercent\"", StringComparison.Ordinal), "measurement avatar package JSON did not include pose-bucket consistency health");
    Require(json.Contains("\"PoseBucketConsistency\"", StringComparison.Ordinal), "measurement avatar package JSON did not include pose-bucket consistency report");
    Require(json.Contains("\"PoseAxisHealthPercent\"", StringComparison.Ordinal), "measurement avatar package JSON did not include pose-axis health");
    Require(json.Contains("\"IdentitySessionHealthPercent\"", StringComparison.Ordinal), "measurement avatar package JSON did not include identity-session health");
    Require(json.Contains("\"IdentitySessionAuditStage\"", StringComparison.Ordinal), "measurement avatar package JSON did not include identity-session audit stage");
    Require(json.Contains("\"IdentitySessionAuditStatus\"", StringComparison.Ordinal), "measurement avatar package JSON did not include identity-session audit status");
    Require(json.Contains("\"AverageIdentityConfidencePercent\"", StringComparison.Ordinal), "measurement avatar package JSON did not include collection identity confidence");
    Require(json.Contains("\"IdentityOutlierFrames\"", StringComparison.Ordinal), "measurement avatar package JSON did not include identity outlier frames");
    Require(json.Contains("\"TrackingAuditHoldFrames\"", StringComparison.Ordinal), "measurement avatar package JSON did not include tracking audit hold frames");
    Require(json.Contains("\"MinimumTrackedDistributionWeight\"", StringComparison.Ordinal), "measurement avatar package JSON did not include weakest tracked distribution weight");
    Require(json.Contains("\"TemplatePriorContributionPercent\"", StringComparison.Ordinal), "measurement avatar package JSON did not include template-prior contribution");
    Require(html.Contains("Contour Shape Profiles", StringComparison.Ordinal), "measurement avatar package HTML did not include contour shape profile summary");
    Require(html.Contains("XYZABC", StringComparison.Ordinal), "measurement avatar package HTML did not include XYZABC score");
    Require(html.Contains("Pose Coverage Profile", StringComparison.Ordinal), "measurement avatar package HTML did not include pose coverage profile");
    Require(html.Contains("template prior contribution", StringComparison.OrdinalIgnoreCase), "measurement avatar package HTML did not include template-prior contribution");
    Require(html.Contains("Contour Shape", StringComparison.Ordinal), "measurement avatar package HTML did not include contour shape readiness score");
    Require(html.Contains("Surface Geometry", StringComparison.Ordinal), "measurement avatar package HTML did not include surface geometry readiness score");
    Require(html.Contains("Direct Feature Trust", StringComparison.Ordinal), "measurement avatar package HTML did not include direct feature trust score");
    Require(html.Contains("Aperture Consistency", StringComparison.Ordinal), "measurement avatar package HTML did not include aperture consistency score");
    Require(html.Contains("Data Audit", StringComparison.Ordinal), "measurement avatar package HTML did not include data audit readiness score");
    Require(html.Contains("Pose Estimation", StringComparison.Ordinal), "measurement avatar package HTML did not include pose estimation audit score");
    Require(html.Contains("Feature Anchoring", StringComparison.Ordinal), "measurement avatar package HTML did not include feature anchoring audit score");
    Require(html.Contains("Pose Bucket Consistency", StringComparison.Ordinal), "measurement avatar package HTML did not include pose-bucket consistency audit score");
    Require(html.Contains("Axis reason", StringComparison.Ordinal), "measurement avatar package HTML did not include pose-axis audit detail");
    Require(html.Contains("Identity Session", StringComparison.Ordinal), "measurement avatar package HTML did not include identity-session readiness score");
    Require(html.Contains("Identity session status", StringComparison.Ordinal), "measurement avatar package HTML did not include identity-session audit status");
    Require(html.Contains("Weakest tracked weight", StringComparison.Ordinal), "measurement avatar package HTML did not include weakest tracked distribution weight");
    Require(html.Contains("Identity confidence", StringComparison.Ordinal), "measurement avatar package HTML did not include identity-confidence quality metric");
    Require(json.Contains("\"ContainsRawPixels\": false", StringComparison.Ordinal), "measurement avatar package JSON did not mark artifacts as pixel-free");
    Require(!json.Contains("FaceContour", StringComparison.Ordinal) && !json.Contains("LeftEyeContour", StringComparison.Ordinal), "measurement avatar package leaked raw contour names");
    Require(!html.Contains("data:image", StringComparison.OrdinalIgnoreCase), "measurement avatar package HTML embedded image data");
    Directory.Delete(packageRoot, recursive: true);
}

static void RunAvatarBuilderStartupOptionsSmoke()
{
    var easy = AvatarBuilderStartupOptions.Parse(["--easy-avatar", "--output-folder", @"D:\Avatar Builder Output"]);
    Require(easy.EasyAvatarMode, "startup options did not enable easy avatar mode");
    Require(easy.OpenAvatarSystem, "easy avatar mode did not imply opening the Avatar System");
    Require(easy.StartAvatarLearning, "easy avatar mode did not imply avatar learning request");
    Require(easy.OutputFolder == @"D:\Avatar Builder Output", $"startup options did not parse separated output folder: {easy.OutputFolder}");

    var makeAvatar = AvatarBuilderStartupOptions.Parse(["/make-avatar", "/output=E:\\Avatar Data"]);
    Require(makeAvatar.EasyAvatarMode && makeAvatar.OpenAvatarSystem && makeAvatar.StartAvatarLearning, "make-avatar alias did not match easy avatar behavior");
    Require(makeAvatar.OutputFolder == @"E:\Avatar Data", $"startup options did not parse inline output folder: {makeAvatar.OutputFolder}");

    var explicitFalse = AvatarBuilderStartupOptions.Parse(["--easy-avatar=false", "--open-avatar-system"]);
    Require(!explicitFalse.EasyAvatarMode, "explicit false easy avatar option was not honored");
    Require(explicitFalse.OpenAvatarSystem, "open avatar system option was not honored after explicit false easy avatar");
    Require(!explicitFalse.StartAvatarLearning, "explicit false easy avatar unexpectedly requested learning");
}

static void RunMeasurementAvatarCapturePlanSmoke()
{
    var model = new PersonalFaceModel
    {
        SubjectId = "chris",
        SubjectDisplayName = "Chris",
        SubjectCollectionMode = PersonalFaceSubject.ManualConfirmationMode,
        UnknownSubjectPolicy = PersonalFaceSubject.UnknownSubjectPolicy,
        CreatedAtUtc = DateTime.UtcNow.AddMinutes(-20),
        UpdatedAtUtc = DateTime.UtcNow,
        ObservedSamples = 64,
        AcceptedSamples = 42,
        AcceptedSampleWeight = 36d,
        AverageFaceReliabilityPercent = 72d,
        AverageFaceContinuityPercent = 70d,
        AverageEyeReliabilityPercent = 68d,
        AverageMouthReliabilityPercent = 69d,
        FaceWidth = Distribution(0.36d, 42, 36d, 0.02d),
        FaceHeight = Distribution(0.54d, 42, 36d, 0.02d),
        HeadYawDegrees = Distribution(1d, 42, 36d, 3d),
        HeadPitchDegrees = Distribution(0d, 42, 36d, 2d),
        HeadRollDegrees = Distribution(0d, 42, 36d, 2d),
        AverageEyeOpeningRatio = Distribution(0.25d, 42, 36d, 0.02d),
        MouthOpeningRatio = Distribution(0.06d, 42, 36d, 0.02d),
        JawDroopRatio = Distribution(0.01d, 42, 36d, 0.015d),
        IdentitySignatureSamples = 42,
        FaceAspectRatio = Distribution(1.48d, 42, 36d, 0.02d),
        InterEyeDistanceToFaceWidth = Distribution(0.38d, 42, 36d, 0.015d),
        MouthWidthToFaceWidth = Distribution(0.33d, 42, 36d, 0.018d),
        EyeMidlineYToFaceHeight = Distribution(0.32d, 42, 36d, 0.012d),
        MouthCenterYToFaceHeight = Distribution(0.66d, 42, 36d, 0.012d)
    };
    var motionModel = new PersonalFaceMotionModel
    {
        SubjectId = model.SubjectId,
        SubjectDisplayName = model.SubjectDisplayName,
        SubjectCollectionMode = model.SubjectCollectionMode,
        ObservationCount = 24,
        UsableObservationCount = 18,
        MotionPairCount = 12,
        AverageObservationQualityPercent = 70d
    };
    var readiness = new PersonalFaceCorpusReadiness
    {
        SubjectId = model.SubjectId,
        SubjectDisplayName = model.SubjectDisplayName,
        SubjectCollectionMode = model.SubjectCollectionMode,
        UnknownSubjectPolicy = model.UnknownSubjectPolicy,
        AcceptedBaselineSamples = model.AcceptedSamples,
        MotionUsableObservations = motionModel.UsableObservationCount,
        MotionPairs = motionModel.MotionPairCount,
        IdentitySignatureSamples = model.IdentitySignatureSamples,
        MeasurementJournalBytes = 12_000L,
        MeasurementBudgetBytes = 1_000_000L,
        MeasurementBudgetUsedPercent = 1.2d,
        OverallReadinessPercent = 34d,
        BaselineCoveragePercent = 18d,
        MotionCoveragePercent = 8d,
        PoseCoveragePercent = 16d,
        DistanceCoveragePercent = 20d,
        ExpressionCoveragePercent = 22d,
        IdentityCoveragePercent = 19d,
        LeftEyeShapeSamples = 42,
        RightEyeShapeSamples = 42,
        OuterLipShapeSamples = 36,
        InnerLipShapeSamples = 36,
        JawShapeSamples = 36,
        ContourShapeCoveragePercent = 10d,
        SurfaceGeometryHealthPercent = 24d,
        SurfaceGeometryPatchCount = 8,
        SurfaceGeometryReviewPatchCount = 3,
        SurfaceGeometryStatus = "3 patch(es) need review",
        EyeBehindGlassesTrustPercent = 12d,
        MouthJawTrustPercent = 18d,
        DirectFeatureMeasurementTrustPercent = 15d,
        QualityCoveragePercent = 54d,
        StorageHealthPercent = 100d
    };
    var acceptedGate = FaceReconstructionSubjectGate.FromPersonalModel(
        model,
        manualSubjectConfirmed: true,
        identityConfidencePercent: 95d,
        reason: "capture plan smoke subject confirmed");
    var plan = new MeasurementAvatarCapturePlanBuilder().Build(
        model,
        motionModel,
        readiness,
        acceptedGate,
        measurementJournalBytes: 12_000L,
        measurementBudgetBytes: 1_000_000L);
    Require(plan.CanCollectMeasurements, "measurement avatar capture plan did not allow accepted subject-gated collection");
    Require(plan.Items.Count >= 6, $"measurement avatar capture plan did not generate enough coverage tasks: {plan.Items.Count}");
    Require(plan.Items.Any(static item => item.Id == "natural-motion"), "measurement avatar capture plan omitted natural motion task");
    Require(plan.Items.Any(static item => item.Id == "expression-ladder"), "measurement avatar capture plan omitted expression ladder task");
    Require(plan.Items.Any(static item => item.Id == "identity-lock"), "measurement avatar capture plan omitted identity task");
    Require(plan.Items.Any(static item => item.Id == "contour-shape-pass"), "measurement avatar capture plan omitted contour shape task");
    Require(plan.Items.Any(static item => item.Id == "surface-geometry-review-pass"), "measurement avatar capture plan omitted surface geometry review task");
    Require(plan.Items.Any(static item => item.Id == "eye-glasses-trust"), "measurement avatar capture plan omitted eye trust task");
    Require(plan.Items.Any(static item => item.Id == "mouth-jaw-trust"), "measurement avatar capture plan omitted mouth/jaw trust task");
    Require(plan.Items.Any(static item => item.Id == "aperture-corroboration"), "measurement avatar capture plan omitted aperture corroboration task");
    var poseSweep = plan.Items.FirstOrDefault(static item => item.Id == "pose-sweep");
    Require(poseSweep is not null, "measurement avatar capture plan omitted pose sweep task");
    var poseSweepItem = poseSweep ?? throw new InvalidOperationException("measurement avatar capture plan omitted pose sweep task");
    Require(
        poseSweepItem.Instructions.Contains("three-quarter", StringComparison.OrdinalIgnoreCase)
        && poseSweepItem.Instructions.Contains("near-side", StringComparison.OrdinalIgnoreCase),
        "measurement avatar capture plan did not ask for three-quarter and side pose evidence");
    Require(
        poseSweepItem.WhyItMatters.Contains("nose projection", StringComparison.OrdinalIgnoreCase)
        && poseSweepItem.WhyItMatters.Contains("cheek", StringComparison.OrdinalIgnoreCase)
        && poseSweepItem.WhyItMatters.Contains("forehead depth", StringComparison.OrdinalIgnoreCase),
        "measurement avatar capture plan did not explain side-pose depth value");
    Require(plan.Items.All(static item => item.NoRawMediaNeeded), "measurement avatar capture plan requested raw media for passive learning");
    Require(plan.EstimatedMeasurementBytes > 0L && plan.EstimatedMeasurementBytes < 10_000_000L, $"capture plan estimate looked wrong: {plan.EstimatedMeasurementBytes}");

    var blockedGate = FaceReconstructionSubjectGate.FromPersonalModel(
        model,
        manualSubjectConfirmed: false,
        identityConfidencePercent: 95d);
    var blockedPlan = new MeasurementAvatarCapturePlanBuilder().Build(
        model,
        motionModel,
        readiness,
        blockedGate,
        measurementJournalBytes: 12_000L,
        measurementBudgetBytes: 1_000_000L);
    Require(!blockedPlan.CanCollectMeasurements, "measurement avatar capture plan allowed collection with an unconfirmed subject gate");
    Require(blockedPlan.Items.Count == 1 && blockedPlan.Items[0].Id == "confirm-subject", "blocked capture plan did not focus only on subject confirmation");

    var planRoot = Path.Combine(Path.GetTempPath(), $"AvatarBuilderAvatarCapturePlanSmoke-{Guid.NewGuid():N}");
    var files = new MeasurementAvatarCapturePlanStore().Write(planRoot, plan);
    Require(File.Exists(files.JsonPath), "measurement avatar capture plan JSON was not written");
    Require(File.Exists(files.HtmlPath), "measurement avatar capture plan HTML was not written");
    var json = File.ReadAllText(files.JsonPath);
    var html = File.ReadAllText(files.HtmlPath);
    Require(json.Contains("\"SchemaVersion\": \"measurement-avatar-capture-plan-v1\"", StringComparison.Ordinal), "measurement avatar capture plan JSON used the wrong schema");
    Require(json.Contains("\"NoRawMediaNeeded\": true", StringComparison.Ordinal), "measurement avatar capture plan JSON did not mark tasks as measurement-only");
    Require(!json.Contains("FaceContour", StringComparison.Ordinal) && !json.Contains("LeftEyeContour", StringComparison.Ordinal), "measurement avatar capture plan leaked raw contour names");
    Require(!html.Contains("data:image", StringComparison.OrdinalIgnoreCase), "measurement avatar capture plan HTML embedded image data");
    Directory.Delete(planRoot, recursive: true);
}

static void RunMeasurementAvatarEasyModeAdvisorSmoke()
{
    var strongQuality = new PersonalFaceCaptureQualityAssessment
    {
        Label = "strong",
        ScorePercent = 92d,
        CanCollectMeasurements = true,
        StrongEnoughForAvatarLearning = true,
        PrimaryReason = "capture quality is strong"
    };

    var unconfirmed = MeasurementAvatarEasyModeAdvisor.Create(new MeasurementAvatarEasyModeInput
    {
        CaptureQuality = strongQuality
    });
    Require(!unconfirmed.CanStartLearning, "easy mode allowed learning without subject confirmation");
    Require(unconfirmed.Title.Contains("Confirm", StringComparison.OrdinalIgnoreCase), "easy mode did not ask for subject confirmation first");

    var ready = MeasurementAvatarEasyModeAdvisor.Create(new MeasurementAvatarEasyModeInput
    {
        SubjectConfirmed = true,
        CameraActive = true,
        FaceLocked = true,
        AvatarLearningRequested = false,
        CaptureQuality = strongQuality
    });
    Require(ready.CanStartLearning, "easy mode was not start-ready with subject, camera, face lock, and quality");
    Require(ready.ActionText.Contains("Start", StringComparison.OrdinalIgnoreCase), "easy mode did not expose a start action before learning is requested");
    Require(ready.Detail.Contains("3DDFA", StringComparison.OrdinalIgnoreCase), "easy mode did not point to 3DDFA capture");

    var running = MeasurementAvatarEasyModeAdvisor.Create(new MeasurementAvatarEasyModeInput
    {
        SubjectConfirmed = true,
        CameraActive = true,
        FaceLocked = true,
        AvatarLearningRequested = true,
        CaptureQuality = strongQuality
    });
    Require(running.Severity == MeasurementAvatarEasyModeSeverity.Good, "easy mode did not mark active guided capture as good");
    Require(running.Title.Contains("3D capture", StringComparison.OrdinalIgnoreCase), "easy mode did not mark 3D capture as running");
    Require(running.Detail.Contains("3DDFA", StringComparison.OrdinalIgnoreCase), "easy mode omitted 3DDFA reconstruction guidance");

    var poorQuality = MeasurementAvatarEasyModeAdvisor.Create(new MeasurementAvatarEasyModeInput
    {
        SubjectConfirmed = true,
        CameraActive = true,
        FaceLocked = true,
        AvatarLearningRequested = true,
        CaptureQuality = new PersonalFaceCaptureQualityAssessment
        {
            Label = "weak",
            ScorePercent = 34d,
            CanCollectMeasurements = false,
            PrimaryReason = "eye evidence is weak",
            Suggestions = ["reduce glasses glare"]
        }
    });
    Require(poorQuality.Title.Contains("Fix capture", StringComparison.OrdinalIgnoreCase), "easy mode did not stop for weak capture quality");
    Require(poorQuality.Detail.Contains("reduce glasses glare", StringComparison.OrdinalIgnoreCase), "easy mode did not surface capture-quality correction");
}

static void RunTexturePreviewRoutingSmoke()
{
    const int width = 4;
    const int height = 2;
    const int stride = 4;
    var nv12 = new byte[stride * height + stride * ((height + 1) / 2)];
    Require(
        TextureNativePreviewPolicy.CanUseNv12UploadFallback("nv12", width, height, nv12, stride),
        "NV12 byte upload should remain available only as a fallback after texture preview paths fail");
    Require(
        !TextureNativePreviewPolicy.CanUseNv12UploadFallback("bgra32", width, height, nv12, stride),
        "BGRA texture preview should not be routed through the NV12 upload fallback");
    Require(
        !TextureNativePreviewPolicy.CanUseNv12UploadFallback("nv12", width, height, nv12[..^1], stride),
        "short NV12 preview buffers should not be treated as reliable upload fallbacks");
}

static void RunMovingFaceTrendSmoke()
{
    var reconstructor = new FaceLandmarkTemporalReconstructor();
    var calculator = new FaceLandmarkMetricCalculator();
    var metrics = new List<FaceLandmarkMetrics>();
    var start = DateTime.UtcNow;
    var centers = new[]
    {
        new WpfPoint(0.42d, 0.48d),
        new WpfPoint(0.56d, 0.45d),
        new WpfPoint(0.48d, 0.54d),
        new WpfPoint(0.62d, 0.50d),
        new WpfPoint(0.38d, 0.52d),
        new WpfPoint(0.52d, 0.47d)
    };
    var scales = new[] { 0.84d, 1.15d, 0.96d, 1.28d, 0.74d, 1.06d };
    var rolls = new[] { -4d, 3d, -7d, 5d, -2d, 4d };

    for (var index = 0; index < centers.Length; index++)
    {
        var closingProgress = index / (double)(centers.Length - 1);
        var eyeRatio = 0.30d - closingProgress * 0.21d;
        var mouthRatio = 0.07d + closingProgress * 0.22d;
        var frame = OpenCvFacemarkLandmarkTracker.CreateLandmarkFrameFrom68Points(
            CreateSyntheticFacemark68Points(eyeRatio, mouthRatio, centers[index], scales[index], rolls[index]),
            start.AddSeconds(index),
            $"moving LBF smoke {index}");

        if (index == 3)
        {
            frame = new FaceLandmarkFrame
            {
                HasFace = frame.HasFace,
                Source = frame.Source + " with right-eye glasses occlusion",
                CapturedAtUtc = frame.CapturedAtUtc,
                TrackingConfidence = frame.TrackingConfidence,
                EyeConfidence = 0.36d,
                MouthConfidence = frame.MouthConfidence,
                HeadRollDegrees = frame.HeadRollDegrees,
                FaceContour = frame.FaceContour,
                LeftEyeContour = frame.LeftEyeContour,
                RightEyeContour = [],
                OuterLipContour = frame.OuterLipContour,
                InnerLipContour = frame.InnerLipContour,
                JawContour = frame.JawContour
            };
        }

        var reconstructed = reconstructor.Update(frame);
        metrics.Add(calculator.Update(reconstructed));
    }

    var first = metrics.First();
    var last = metrics.Last();
    Require(
        first.AverageEyeOpeningRatio is double firstEye
        && last.AverageEyeOpeningRatio is double lastEye
        && lastEye < firstEye * 0.55d,
        $"moving/scale synthetic eye trend was not strong enough: first={first.AverageEyeOpeningRatio}, last={last.AverageEyeOpeningRatio}");
    Require(
        first.MouthOpeningRatio is double firstMouth
        && last.MouthOpeningRatio is double lastMouth
        && lastMouth > firstMouth * 1.80d,
        $"moving/scale synthetic mouth trend was not strong enough: first={first.MouthOpeningRatio}, last={last.MouthOpeningRatio}");
    Require(
        metrics.All(metric => metric.TrackingConfidence >= 0.70d),
        "moving/scale synthetic face tracking confidence fell below usable range");
    Require(
        metrics[3].AverageEyeOpeningRatio is not null,
        "moving/scale synthetic glasses occlusion frame did not reconstruct a usable average eye opening");
    Require(
        metrics[3].RightEyeReconstructed,
        "moving/scale synthetic glasses occlusion frame did not carry right-eye reconstruction evidence");

    var geometryReconstructor = new FaceLandmarkTemporalReconstructor();
    var baselineFrame = OpenCvFacemarkLandmarkTracker.CreateLandmarkFrameFrom68Points(
        CreateSyntheticFacemark68Points(
            eyeRatio: 0.27d,
            mouthRatio: 0.09d,
            center: new WpfPoint(0.37d, 0.44d),
            scale: 0.74d,
            rollDegrees: -3d),
        start.AddSeconds(10),
        "face-relative reconstruction baseline");
    var movedReferenceFrame = OpenCvFacemarkLandmarkTracker.CreateLandmarkFrameFrom68Points(
        CreateSyntheticFacemark68Points(
            eyeRatio: 0.18d,
            mouthRatio: 0.18d,
            center: new WpfPoint(0.64d, 0.55d),
            scale: 1.28d,
            rollDegrees: 4d),
        start.AddSeconds(11),
        "face-relative reconstruction moved reference");
    _ = geometryReconstructor.Update(baselineFrame);
    var occludedMovedFrame = new FaceLandmarkFrame
    {
        HasFace = true,
        Source = movedReferenceFrame.Source + " with glasses and lip occlusion",
        CapturedAtUtc = movedReferenceFrame.CapturedAtUtc,
        TrackingConfidence = movedReferenceFrame.TrackingConfidence,
        EyeConfidence = 0.28d,
        MouthConfidence = 0.24d,
        FaceContour = movedReferenceFrame.FaceContour,
        LeftEyeContour = [],
        RightEyeContour = [],
        OuterLipContour = [],
        InnerLipContour = [],
        JawContour = movedReferenceFrame.JawContour
    };
    var faceRelativeReconstruction = geometryReconstructor.Update(occludedMovedFrame);
    var expectedLeftEyeBounds = GetContourBounds(movedReferenceFrame.LeftEyeContour);
    var expectedRightEyeBounds = GetContourBounds(movedReferenceFrame.RightEyeContour);
    var expectedMouthBounds = GetContourBounds(movedReferenceFrame.InnerLipContour);
    var reconstructedLeftEyeBounds = GetContourBounds(faceRelativeReconstruction.LeftEyeContour);
    var reconstructedRightEyeBounds = GetContourBounds(faceRelativeReconstruction.RightEyeContour);
    var reconstructedMouthBounds = GetContourBounds(faceRelativeReconstruction.InnerLipContour);
    Require(
        faceRelativeReconstruction.LeftEyeReconstructed
        && faceRelativeReconstruction.RightEyeReconstructed
        && faceRelativeReconstruction.MouthReconstructed,
        "face-relative temporal reconstruction did not mark occluded eyes and mouth as reconstructed");
    Require(
        expectedLeftEyeBounds is WpfRect expectedLeft
        && reconstructedLeftEyeBounds is WpfRect reconstructedLeft
        && RectCenterDistance(expectedLeft, reconstructedLeft) < 0.045d
        && Math.Abs(expectedLeft.Width - reconstructedLeft.Width) < 0.045d,
        $"face-relative left-eye reconstruction did not follow moved/scaled face geometry: expected={expectedLeftEyeBounds}, reconstructed={reconstructedLeftEyeBounds}");
    Require(
        expectedRightEyeBounds is WpfRect expectedRight
        && reconstructedRightEyeBounds is WpfRect reconstructedRight
        && RectCenterDistance(expectedRight, reconstructedRight) < 0.045d
        && Math.Abs(expectedRight.Width - reconstructedRight.Width) < 0.045d,
        $"face-relative right-eye reconstruction did not follow moved/scaled face geometry: expected={expectedRightEyeBounds}, reconstructed={reconstructedRightEyeBounds}");
    Require(
        expectedMouthBounds is WpfRect expectedMouth
        && reconstructedMouthBounds is WpfRect reconstructedMouth
        && RectCenterDistance(expectedMouth, reconstructedMouth) < 0.055d
        && Math.Abs(expectedMouth.Width - reconstructedMouth.Width) < 0.055d,
        $"face-relative mouth reconstruction did not follow moved/scaled face geometry: expected={expectedMouthBounds}, reconstructed={reconstructedMouthBounds}");
}

static void RunCompositeFusionSmoke()
{
    var capturedAt = DateTime.UtcNow;
    var lbfFrame = OpenCvFacemarkLandmarkTracker.CreateLandmarkFrameFrom68Points(
        CreateSyntheticFacemark68Points(eyeRatio: 0.30d, mouthRatio: 0.08d),
        capturedAt,
        "synthetic LBF structure");
    var apertureFrame = CreateSyntheticLandmarkFrame(
        capturedAt,
        leftEyeRatio: 0.08d,
        rightEyeRatio: 0.09d,
        mouthRatio: 0.30d,
        eyeConfidence: 0.42d,
        mouthConfidence: 0.40d);

    using var composite = new CompositeFaceLandmarkTracker(
    [
        new FakeResultTracker("OpenCV LBF facemark backend", "LBF 68-point landmark lock", lbfFrame),
        new FakeResultTracker("OpenCV aperture fallback", "fallback aperture lock", apertureFrame)
    ]);

    var result = composite.Detect(CreateTinyBitmap(), capturedAt);
    var eyeRatio = CalculateContourOpeningRatio(result.LandmarkFrame.LeftEyeContour);
    var mouthRatio = CalculateContourOpeningRatio(result.LandmarkFrame.InnerLipContour);
    Require(result.BackendName.Contains("fusion", StringComparison.OrdinalIgnoreCase), $"composite tracker did not report fusion: {result.BackendName}");
    Require(result.LandmarkFrame.Source.Contains("fused", StringComparison.OrdinalIgnoreCase), "composite tracker did not mark the fused landmark source");
    Require(eyeRatio is < 0.14d, $"composite tracker did not use direct aperture eye opening: {eyeRatio}");
    Require(mouthRatio is > 0.22d, $"composite tracker did not use direct aperture mouth opening: {mouthRatio}");
    Require(result.LandmarkFrame.JawContour.Count == lbfFrame.JawContour.Count, "composite tracker did not preserve structural jaw landmarks");

    var mediaPipeFrame = OpenCvFacemarkLandmarkTracker.CreateLandmarkFrameFrom68Points(
        CreateSyntheticFacemark68Points(eyeRatio: 0.30d, mouthRatio: 0.08d),
        capturedAt,
        "MediaPipe Face Landmarker sidecar synthetic dense mesh");
    using var densePreferredComposite = new CompositeFaceLandmarkTracker(
    [
        new FakeResultTracker("MediaPipe Face Landmarker sidecar", "MediaPipe dense landmark lock", mediaPipeFrame),
        new FakeResultTracker("OpenCV aperture fallback", "fallback aperture lock", apertureFrame)
    ]);

    var densePreferredResult = densePreferredComposite.Detect(CreateTinyBitmap(), capturedAt);
    var densePreferredEyeRatio = CalculateContourOpeningRatio(densePreferredResult.LandmarkFrame.LeftEyeContour);
    var densePreferredMouthRatio = CalculateContourOpeningRatio(densePreferredResult.LandmarkFrame.InnerLipContour);
    Require(
        densePreferredResult.BackendName.Contains("MediaPipe", StringComparison.OrdinalIgnoreCase),
        $"composite tracker downgraded strong MediaPipe primary to another backend: {densePreferredResult.BackendName}");
    Require(
        densePreferredEyeRatio is > 0.20d,
        $"composite tracker let aperture fallback overwrite strong MediaPipe eyelid geometry: {densePreferredEyeRatio}");
    Require(
        densePreferredMouthRatio is < 0.18d,
        $"composite tracker let aperture fallback overwrite strong MediaPipe mouth geometry: {densePreferredMouthRatio}");

    var weakMediaPipeFrame = WithLandmarkConfidence(
        mediaPipeFrame,
        "MediaPipe Face Landmarker sidecar synthetic weak-eye mesh",
        trackingConfidence: 0.72d,
        eyeConfidence: 0.34d,
        mouthConfidence: 0.34d);
    using var denseRepairComposite = new CompositeFaceLandmarkTracker(
    [
        new FakeResultTracker("MediaPipe Face Landmarker sidecar", "MediaPipe dense landmark lock with weak apertures", weakMediaPipeFrame),
        new FakeResultTracker("OpenCV aperture fallback", "fallback aperture lock", apertureFrame)
    ]);

    var denseRepairResult = denseRepairComposite.Detect(CreateTinyBitmap(), capturedAt);
    var denseRepairEyeRatio = CalculateContourOpeningRatio(denseRepairResult.LandmarkFrame.LeftEyeContour);
    var denseRepairMouthRatio = CalculateContourOpeningRatio(denseRepairResult.LandmarkFrame.InnerLipContour);
    Require(
        denseRepairResult.BackendName.Contains("fusion", StringComparison.OrdinalIgnoreCase),
        $"composite tracker did not fuse aperture fallback when MediaPipe primary was weak: {denseRepairResult.BackendName}");
    Require(
        denseRepairEyeRatio is < 0.14d,
        $"composite tracker did not allow aperture repair for weak MediaPipe eye geometry: {denseRepairEyeRatio}");
    Require(
        denseRepairMouthRatio is > 0.22d,
        $"composite tracker did not allow aperture repair for weak MediaPipe mouth geometry: {denseRepairMouthRatio}");

    var cropFrame = OpenCvFacemarkLandmarkTracker.CreateLandmarkFrameFrom68Points(
        CreateSyntheticFacemark68Points(eyeRatio: 0.26d, mouthRatio: 0.11d),
        capturedAt,
        "MediaPipe Face Landmarker sidecar crop-normalized dense mesh");
    using var cropRefiner = new FakeCropRefiner(cropFrame);
    using var cropRefinedComposite = new CompositeFaceLandmarkTracker(
    [
        cropRefiner,
        new FakeResultTracker("OpenCV LBF facemark backend", "LBF 68-point landmark lock", lbfFrame)
    ]);

    var cropRefinedResult = cropRefinedComposite.Detect(CreateTinyBitmap(), capturedAt);
    var cropRefinedBounds = GetContourBounds(cropRefinedResult.LandmarkFrame.FaceContour);
    var lbfBounds = GetContourBounds(lbfFrame.FaceContour);
    Require(cropRefiner.CropCalls == 1, $"composite tracker did not request one MediaPipe crop refinement: {cropRefiner.CropCalls}");
    Require(
        cropRefinedResult.BackendName.Contains("MediaPipe", StringComparison.OrdinalIgnoreCase),
        $"composite tracker did not promote crop-refined MediaPipe result: {cropRefinedResult.BackendName}");
    Require(
        cropRefinedResult.BackendStatus.Contains("crop", StringComparison.OrdinalIgnoreCase),
        $"crop-refined MediaPipe result did not retain crop audit status: {cropRefinedResult.BackendStatus}");
    Require(
        cropRefinedBounds is WpfRect mappedFace
        && lbfBounds is WpfRect hintedFace
        && mappedFace.Left >= hintedFace.Left - 0.001d
        && mappedFace.Right <= hintedFace.Right + 0.001d
        && mappedFace.Top >= hintedFace.Top - 0.001d
        && mappedFace.Bottom <= hintedFace.Bottom + 0.001d,
        $"crop-refined MediaPipe landmarks were not mapped back inside the full-frame face hint: mapped={cropRefinedBounds}, hint={lbfBounds}");

    using var recoveryCropRefiner = new FakeCropRefiner(cropFrame);
    using var recoveringComposite = new CompositeFaceLandmarkTracker(
    [
        recoveryCropRefiner,
        new FakeSequenceTracker(
        [
            new FaceLandmarkTrackingResult
            {
                BackendName = "OpenCV aperture fallback",
                BackendStatus = "fallback aperture lock",
                LandmarkFrame = lbfFrame,
                FeatureDetection = new FaceFeatureDetection
                {
                    HasFace = true,
                    Source = lbfFrame.Source,
                    FaceBox = lbfBounds ?? new WpfRect(0.30d, 0.22d, 0.40d, 0.56d),
                    TrackingConfidence = lbfFrame.TrackingConfidence,
                    EyeConfidence = lbfFrame.EyeConfidence,
                    MouthConfidence = lbfFrame.MouthConfidence,
                    FaceContour = lbfFrame.FaceContour,
                    LeftEyeContour = lbfFrame.LeftEyeContour,
                    RightEyeContour = lbfFrame.RightEyeContour,
                    OuterLipContour = lbfFrame.OuterLipContour,
                    InnerLipContour = lbfFrame.InnerLipContour,
                    JawContour = lbfFrame.JawContour
                }
            },
            new FaceLandmarkTrackingResult
            {
                BackendName = "OpenCV aperture fallback",
                BackendStatus = "fallback aperture searching"
            }
        ])
    ]);

    _ = recoveringComposite.Detect(CreateTinyBitmap(), capturedAt);
    var recoveredResult = recoveringComposite.Detect(CreateTinyBitmap(), capturedAt.AddMilliseconds(350));
    Require(
        recoveryCropRefiner.CropCalls == 2,
        $"temporal recovery did not request a second MediaPipe crop from the previous face hint: {recoveryCropRefiner.CropCalls}");
    Require(
        recoveredResult.HasFace && recoveredResult.BackendName.Contains("MediaPipe", StringComparison.OrdinalIgnoreCase),
        $"temporal recovery did not return the crop-refined MediaPipe face after a detector miss: {recoveredResult.BackendName} / {recoveredResult.BackendStatus}");
    Require(
        recoveredResult.BackendStatus.Contains("temporal recovery", StringComparison.OrdinalIgnoreCase),
        $"temporal recovery status did not explain the previous-face hint path: {recoveredResult.BackendStatus}");
}

static FaceLandmarkFrame WithLandmarkConfidence(
    FaceLandmarkFrame frame,
    string source,
    double trackingConfidence,
    double eyeConfidence,
    double mouthConfidence)
{
    return new FaceLandmarkFrame
    {
        HasFace = frame.HasFace,
        Source = source,
        CapturedAtUtc = frame.CapturedAtUtc,
        TrackingConfidence = trackingConfidence,
        EyeConfidence = eyeConfidence,
        MouthConfidence = mouthConfidence,
        EyeImageQualityAvailable = frame.EyeImageQualityAvailable,
        MouthImageQualityAvailable = frame.MouthImageQualityAvailable,
        EyeGlarePercent = frame.EyeGlarePercent,
        MouthGlarePercent = frame.MouthGlarePercent,
        EyeContrastPercent = frame.EyeContrastPercent,
        MouthContrastPercent = frame.MouthContrastPercent,
        EyeSharpnessPercent = frame.EyeSharpnessPercent,
        MouthSharpnessPercent = frame.MouthSharpnessPercent,
        EyeDarkCoveragePercent = frame.EyeDarkCoveragePercent,
        MouthDarkCoveragePercent = frame.MouthDarkCoveragePercent,
        LeftEyeReconstructed = frame.LeftEyeReconstructed,
        RightEyeReconstructed = frame.RightEyeReconstructed,
        MouthReconstructed = frame.MouthReconstructed,
        EyeArtifactSuppressed = frame.EyeArtifactSuppressed,
        HeadYawDegrees = frame.HeadYawDegrees,
        HeadPitchDegrees = frame.HeadPitchDegrees,
        HeadRollDegrees = frame.HeadRollDegrees,
        BlendshapeScores = frame.BlendshapeScores,
        FaceContour = frame.FaceContour,
        LeftEyeContour = frame.LeftEyeContour,
        RightEyeContour = frame.RightEyeContour,
        LeftBrowContour = frame.LeftBrowContour,
        RightBrowContour = frame.RightBrowContour,
        OuterLipContour = frame.OuterLipContour,
        InnerLipContour = frame.InnerLipContour,
        JawContour = frame.JawContour
    };
}

static void WriteSyntheticVideo(string outputPath, bool includeEyeInset)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory);
    var fourcc = VideoWriter.FourCC('M', 'J', 'P', 'G');
    using var writer = new VideoWriter(
        outputPath,
        fourcc,
        SyntheticVideoFramesPerSecond,
        new CvSize(SyntheticVideoWidth, SyntheticVideoHeight));
    if (!writer.IsOpened())
    {
        throw new InvalidOperationException($"Could not open synthetic video writer: {outputPath}");
    }

    for (var index = 0; index < SyntheticVideoFrameCount; index++)
    {
        using var frame = CreateSyntheticVideoFrame(index, includeEyeInset);
        writer.Write(frame);
    }
}

static void WriteSyntheticLandmarkStress(string outputPath)
{
    var outputFolder = Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory;
    Directory.CreateDirectory(outputFolder);

    var start = DateTime.UtcNow;
    var reconstructor = new FaceLandmarkTemporalReconstructor();
    var calculator = new FaceLandmarkMetricCalculator();
    var cueAnalyzer = new FaceLandmarkCueAnalyzer();
    var trendAnalyzer = new FaceLandmarkTrendAnalyzer();
    var stabilityAnalyzer = new FaceLockStabilityAnalyzer();
    var headPoseEstimator = new HeadPoseEstimator();
    var personalModelBuilder = new PersonalFaceModelBuilder();
    var captureQualityAnalyzer = new PersonalFaceCaptureQualityAnalyzer();
    var aggregate = new LandmarkEventAggregate();
    var timeline = new LandmarkEventTimeline();
    var records = new List<SyntheticLandmarkStressRecord>();
    const int frameCount = 72;

    for (var index = 0; index < frameCount; index++)
    {
        var progress = index / (double)(frameCount - 1);
        var sleepiness = SmoothStep(Math.Clamp((progress - 0.12d) / 0.88d, 0d, 1d));
        var eyeRatio = 0.31d - sleepiness * 0.22d;
        var mouthRatio = 0.07d + sleepiness * 0.23d;
        var jawDroopRatio = sleepiness * 0.11d;
        var reframeStress = SmoothStep(Math.Clamp((progress - 0.30d) / 0.70d, 0d, 1d));
        var center = new WpfPoint(
            Math.Clamp(0.49d + Math.Sin(progress * Math.PI * 2.4d) * 0.12d + Math.Sin(progress * Math.PI * 3.5d) * 0.31d * reframeStress, 0.18d, 0.82d),
            Math.Clamp(0.49d + Math.Cos(progress * Math.PI * 1.7d) * 0.055d + Math.Cos(progress * Math.PI * 2.8d) * 0.22d * reframeStress, 0.27d, 0.73d));
        var scale = 0.78d + Math.Sin(progress * Math.PI) * 0.44d - 0.10d * reframeStress;
        if (index is >= 24 and <= 31)
        {
            center = new WpfPoint(0.16d, 0.42d);
            scale = Math.Min(scale, 0.92d);
        }
        else if (index is >= 32 and <= 43)
        {
            center = new WpfPoint(0.84d, 0.52d);
            scale = Math.Min(scale, 0.92d);
        }
        else if (index is >= 52 and <= 61)
        {
            center = new WpfPoint(0.50d, 0.24d);
            scale = Math.Min(scale, 0.88d);
        }
        else if (index >= 62)
        {
            center = new WpfPoint(0.50d, 0.73d);
            scale = Math.Min(scale, 0.82d);
        }

        var yaw = Math.Sin(progress * Math.PI * 2.2d) * 18d;
        var pitch = Math.Cos(progress * Math.PI * 1.8d) * 11d;
        var roll = Math.Sin(progress * Math.PI * 2.1d) * 13d;
        var capturedAt = start.AddSeconds(index / SyntheticVideoFramesPerSecond);
        var frame = OpenCvFacemarkLandmarkTracker.CreateLandmarkFrameFrom68Points(
            CreateSyntheticFacemark68Points(eyeRatio, mouthRatio, center, scale, roll, jawDroopRatio, yaw, pitch),
            capturedAt,
            $"synthetic landmark stress {index}");
        frame = AddSyntheticLandmarkStressEvidence(frame, progress, sleepiness, index);

        var reconstructed = reconstructor.Update(frame);
        var metrics = calculator.Update(reconstructed);
        var cue = cueAnalyzer.Analyze(metrics);
        var trend = trendAnalyzer.Update(metrics);
        var motion = 0.4d + Math.Abs(Math.Sin(progress * Math.PI * 4d)) * 2.8d;
        var faceBounds = GetContourBounds(reconstructed.FaceContour);
        var nearFrameEdge = faceBounds is WpfRect faceRect
            && (faceRect.Left <= 0.08d || faceRect.Top <= 0.12d || faceRect.Right >= 0.92d || faceRect.Bottom >= 0.88d);
        var stabilityDetection = faceBounds is WpfRect lockFaceBox
            ? new FaceFeatureDetection
            {
                HasFace = true,
                FaceBox = lockFaceBox,
                TrackingConfidence = metrics.TrackingConfidence,
                EyeConfidence = metrics.EyeConfidence,
                MouthConfidence = metrics.MouthConfidence,
                Source = metrics.Source
            }
            : FaceFeatureDetection.None;
        var stability = stabilityAnalyzer.Update(stabilityDetection, reconstructed, metrics);
        var headPose = headPoseEstimator.Estimate(new HeadPoseEstimatorInput
        {
            Frame = reconstructed,
            FrameWidthPixels = SyntheticVideoWidth,
            FrameHeightPixels = SyntheticVideoHeight,
            Calibration = new HeadPoseCalibration
            {
                CameraHorizontalFovDegrees = 71.4d
            }
        });
        var personalModelUpdate = personalModelBuilder.Update(reconstructed, metrics, stability, cue, trend, headPose);
        var captureQuality = captureQualityAnalyzer.Analyze(new PersonalFaceCaptureQualityInput
        {
            VideoWidth = SyntheticVideoWidth,
            VideoHeight = SyntheticVideoHeight,
            FramesPerSecond = SyntheticVideoFramesPerSecond,
            InputFormat = "synthetic facemark",
            LandmarkFrame = reconstructed,
            Metrics = metrics,
            Stability = stability,
            PersonalModelUpdate = personalModelUpdate
        });
        aggregate.Update(metrics, cue, trend, stability, "synthetic landmark stress", captureQuality);
        timeline.Add(start, motion, metrics, cue, trend, stability, "synthetic landmark stress", captureQuality);
        records.Add(new SyntheticLandmarkStressRecord(
            index,
            progress,
            (capturedAt - start).TotalSeconds,
            metrics.AverageEyeOpeningRatio,
            metrics.MouthOpeningRatio,
            metrics.JawDroopRatio,
            cue.EyeClosurePercent,
            cue.MouthOpeningChangePercent,
            cue.JawDroopChangePercent,
            cue.CompositeCuePercent,
            trend.EyeOpeningSlopePerSecond,
            trend.MouthOpeningSlopePerSecond,
            metrics.LeftEyeReconstructed,
            metrics.RightEyeReconstructed,
            metrics.MouthReconstructed,
            metrics.EyeArtifactSuppressed,
            cue.BaselineReady,
            cue.EyeCueEligible,
            cue.MouthCueEligible,
            metrics.OverallMeasurementQualityPercent,
            faceBounds?.Left,
            faceBounds?.Top,
            faceBounds?.Right,
            faceBounds?.Bottom,
            nearFrameEdge,
            captureQuality.Label,
            captureQuality.ScorePercent,
            captureQuality.CanCollectMeasurements,
            captureQuality.StrongEnoughForAvatarLearning,
            captureQuality.CameraModeScorePercent,
            captureQuality.FaceScaleScorePercent,
            captureQuality.EyeEvidenceScorePercent,
            captureQuality.MouthEvidenceScorePercent,
            captureQuality.StabilityScorePercent,
            captureQuality.GlassesRiskScorePercent,
            string.Join("; ", captureQuality.Issues),
            personalModelUpdate.Accepted,
            personalModelUpdate.RejectionKind.ToString(),
            personalModelUpdate.Reason));
    }

    var timelineFolder = Path.Combine(outputFolder, "landmark_stress_timeline");
    if (Directory.Exists(timelineFolder))
    {
        Directory.Delete(timelineFolder, recursive: true);
    }

    var timelineFiles = timeline.Write(timelineFolder);
    var personalModelFolder = Path.Combine(outputFolder, "personal_model");
    if (Directory.Exists(personalModelFolder))
    {
        Directory.Delete(personalModelFolder, recursive: true);
    }

    var personalModelPath = new PersonalFaceModelStore().Write(personalModelFolder, personalModelBuilder.CurrentModel);
    var personalModel = personalModelBuilder.CurrentModel;
    var previewGate = FaceReconstructionSubjectGate.FromPersonalModel(
        personalModel,
        manualSubjectConfirmed: true,
        reason: "synthetic landmark stress subject is explicit");
    var preview = new MeasurementFacePreviewBuilder().Build(personalModel, previewGate);
    var previewFiles = new MeasurementFacePreviewStore().Write(personalModelFolder, preview);
    var motionModel = new PersonalFaceMotionModelBuilder().Build(records.Select(record => new PersonalFaceMotionObservation
    {
        SubjectId = personalModel.SubjectId,
        SubjectDisplayName = personalModel.SubjectDisplayName,
        SubjectCollectionMode = personalModel.SubjectCollectionMode,
        CapturedAtUtc = DateTime.UnixEpoch.AddMilliseconds(Math.Max(0d, record.TimestampSeconds) * 1000d),
        AcceptedForPersonalModel = record.PersonalModelAccepted,
        Source = "synthetic landmark stress",
        SampleWeight = Math.Clamp(record.OverallQuality / 100d, 0.05d, 1.25d),
        OverallQualityPercent = record.OverallQuality,
        FaceReliabilityPercent = 85d,
        FaceContinuityPercent = 85d,
        EyeReliabilityPercent = 82d,
        MouthReliabilityPercent = 82d,
        AverageEyeOpeningRatio = record.EyeOpening,
        MouthOpeningRatio = record.MouthOpening,
        JawDroopRatio = record.JawDroop,
        EyeArtifactSuppressed = record.EyeArtifactSuppressed,
        AnyEyeReconstructed = record.LeftEyeReconstructed || record.RightEyeReconstructed,
        MouthReconstructed = record.MouthReconstructed
    }));
    var motionModelPath = new PersonalFaceMotionModelStore().Write(personalModelFolder, motionModel);
    var corpusSamples = records
        .Select(record => new PersonalFaceMeasurementSample
        {
            SubjectId = personalModel.SubjectId,
            SubjectDisplayName = personalModel.SubjectDisplayName,
            SubjectCollectionMode = personalModel.SubjectCollectionMode,
            CapturedAtUtc = DateTime.UnixEpoch.AddMilliseconds(Math.Max(0d, record.TimestampSeconds) * 1000d),
            SampleWeight = Math.Clamp(record.OverallQuality / 100d, 0.05d, 1.25d),
            OverallQualityPercent = record.OverallQuality,
            CaptureQualityLabel = record.CaptureQualityLabel,
            CaptureQualityScorePercent = record.CaptureQualityScore,
            CaptureQualityCanCollect = record.CaptureQualityCanCollect,
            CaptureQualityAvatarGrade = record.CaptureQualityAvatarGrade,
            CaptureQualityCameraModeScorePercent = record.CaptureQualityCameraModeScore,
            CaptureQualityFaceScaleScorePercent = record.CaptureQualityFaceScaleScore,
            CaptureQualityEyeScorePercent = record.CaptureQualityEyeScore,
            CaptureQualityMouthScorePercent = record.CaptureQualityMouthScore,
            CaptureQualityStabilityScorePercent = record.CaptureQualityStabilityScore,
            CaptureQualityGlassesScorePercent = record.CaptureQualityGlassesScore,
            CaptureQualityIssues = record.CaptureQualityIssues
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            FaceReliabilityPercent = 85d,
            FaceContinuityPercent = 85d,
            EyeReliabilityPercent = 82d,
            MouthReliabilityPercent = 82d,
            FaceWidth = record.FaceRight - record.FaceLeft,
            FaceHeight = record.FaceBottom - record.FaceTop,
            AverageEyeOpeningRatio = record.EyeOpening,
            MouthOpeningRatio = record.MouthOpening,
            JawDroopRatio = record.JawDroop,
            EyeArtifactSuppressed = record.EyeArtifactSuppressed,
            LeftEyeReconstructed = record.LeftEyeReconstructed,
            RightEyeReconstructed = record.RightEyeReconstructed,
            MouthReconstructed = record.MouthReconstructed
        })
        .ToList();
    var corpusReadiness = new PersonalFaceCorpusReadinessBuilder().Build(personalModel, motionModel, corpusSamples, measurementJournalBytes: 0L);
    var corpusReadinessPath = new PersonalFaceCorpusReadinessStore().Write(personalModelFolder, corpusReadiness);
    var corpusReadinessHtmlPath = PersonalFaceCorpusReadinessStore.GetHtmlPath(corpusReadinessPath);
    var avatarPackage = new MeasurementAvatarTrainingPackageBuilder().Build(
        personalModel,
        motionModel,
        corpusReadiness,
        previewGate,
        measurementJournalBytes: 0L);
    var avatarPackageFiles = new MeasurementAvatarTrainingPackageStore().Write(personalModelFolder, avatarPackage);
    Require(File.Exists(avatarPackageFiles.JsonPath), "synthetic stress did not write avatar package JSON");
    Require(File.Exists(avatarPackageFiles.HtmlPath), "synthetic stress did not write avatar package HTML");
    Require(avatarPackage.NeutralFaceProfile.ContainsKey("AverageEyeOpeningRatio"), "synthetic stress avatar package omitted eyelid profile");
    Require(avatarPackage.MotionProfile.ContainsKey("JawDroopVelocityPerSecond"), "synthetic stress avatar package omitted jaw motion profile");
    Require(avatarPackage.LearningStability.MaximumNextSampleInfluencePercent > 0d, "synthetic stress avatar package omitted learning stability");
    Require(avatarPackage.Readiness.LearningStabilityCoveragePercent > 0d, "synthetic stress avatar package omitted learning-stability readiness score");
    Require(avatarPackage.QualityProfile.ContainsKey("MaximumNextSampleInfluencePercent"), "synthetic stress avatar package omitted next-sample influence metric");
    Require(avatarPackage.IntegrationNotes.Any(static note => note.Contains("Learning stability", StringComparison.OrdinalIgnoreCase)), "synthetic stress avatar package omitted learning-stability integration note");
    var avatarCapturePlan = new MeasurementAvatarCapturePlanBuilder().Build(
        personalModel,
        motionModel,
        corpusReadiness,
        previewGate,
        measurementJournalBytes: 0L);
    var avatarCapturePlanFiles = new MeasurementAvatarCapturePlanStore().Write(personalModelFolder, avatarCapturePlan);
    Require(File.Exists(avatarCapturePlanFiles.JsonPath), "synthetic stress did not write avatar capture plan JSON");
    Require(File.Exists(avatarCapturePlanFiles.HtmlPath), "synthetic stress did not write avatar capture plan HTML");
    Require(avatarCapturePlan.Items.Count > 0, "synthetic stress avatar capture plan did not include any next capture items");
    var collectionAudit = new PersonalFaceCollectionAuditBuilder().Build(
        personalModel,
        records.Select(record => new PersonalFaceCollectionAuditObservation
        {
            ReviewedAtUtc = DateTime.UnixEpoch.AddMilliseconds(Math.Max(0d, record.TimestampSeconds) * 1000d),
            SubjectConfirmed = true,
            PausedForEventOrCalibration = record.PersonalModelRejectionKind.Equals(
                PersonalFaceModelRejectionKind.EventLike.ToString(),
                StringComparison.OrdinalIgnoreCase),
            HasFace = record.FaceLeft.HasValue && record.FaceTop.HasValue && record.FaceRight.HasValue && record.FaceBottom.HasValue,
            PersonalModelAccepted = record.PersonalModelAccepted,
            PersonalModelRejectionKind = record.PersonalModelRejectionKind,
            PersonalModelUpdateReason = record.PersonalModelUpdateReason,
            CaptureQualityLabel = record.CaptureQualityLabel,
            CaptureQualityScorePercent = record.CaptureQualityScore,
            CaptureQualityCanCollect = record.CaptureQualityCanCollect,
            CaptureQualityAvatarGrade = record.CaptureQualityAvatarGrade,
            CaptureQualityReason = record.CaptureQualityIssues,
            CaptureQualityCameraModeScorePercent = record.CaptureQualityCameraModeScore,
            CaptureQualityFaceScaleScorePercent = record.CaptureQualityFaceScaleScore,
            CaptureQualityEyeScorePercent = record.CaptureQualityEyeScore,
            CaptureQualityMouthScorePercent = record.CaptureQualityMouthScore,
            CaptureQualityStabilityScorePercent = record.CaptureQualityStabilityScore,
            CaptureQualityGlassesScorePercent = record.CaptureQualityGlassesScore,
            CaptureQualityStorageScorePercent = 100d,
            CaptureQualityFaceWidthPercent = record.FaceRight.HasValue && record.FaceLeft.HasValue
                ? Math.Max(0d, record.FaceRight.Value - record.FaceLeft.Value) * 100d
                : null,
            CaptureQualityFaceHeightPercent = record.FaceBottom.HasValue && record.FaceTop.HasValue
                ? Math.Max(0d, record.FaceBottom.Value - record.FaceTop.Value) * 100d
                : null,
            CaptureQualityIssues = record.CaptureQualityIssues
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList()
        }).ToList());
    var collectionAuditPath = new PersonalFaceCollectionAuditStore().Write(personalModelFolder, collectionAudit);
    var collectionAuditHtmlPath = PersonalFaceCollectionAuditStore.GetHtmlPath(collectionAuditPath);
    Require(File.Exists(collectionAuditPath), "synthetic stress did not write personal collection audit JSON");
    Require(File.Exists(collectionAuditHtmlPath), "synthetic stress did not write personal collection audit HTML");
    Require(collectionAudit.TotalFramesReviewed == records.Count, "synthetic stress collection audit reviewed wrong frame count");
    Require(collectionAudit.TopCaptureQualityIssues.Count > 0, "synthetic stress collection audit did not retain capture issues");
    Require(collectionAudit.NextActions.Count > 0, "synthetic stress collection audit did not suggest next actions");
    var avatarDashboard = new MeasurementAvatarSystemDashboard
    {
        SubjectId = personalModel.SubjectId,
        SubjectDisplayName = personalModel.SubjectDisplayName,
        SubjectConfirmed = true,
        AvatarCaptureRequested = true,
        AvatarCaptureActive = true,
        AvatarCaptureStatus = "Capturing 3D avatar data",
        AvatarCaptureCorrection = "synthetic stress verifier",
        CurrentCaptureQuality = PersonalFaceCaptureQualityAssessment.Waiting,
        CurrentHeadPose = new HeadPoseEstimate
        {
            HasFace = true,
            XHorizontalPercent = 50.2d,
            YVerticalPercent = 48.7d,
            ApparentDistanceUnits = 3.82d,
            FaceFillWidthPercent = 42.5d,
            FaceFillHeightPercent = 61.0d,
            RelativeDistanceScale = 0.94d,
            InterEyeFrameWidthPercent = 16.4d,
            YawDegrees = 8.5d,
            PitchDegrees = -2.1d,
            RollDegrees = 1.2d,
            ConfidencePercent = 92d,
            RotationSource = "facial transform matrix",
            DistanceSource = "apparent face units from eye span and 71.4 deg horizontal FOV",
            ReferenceScaleSource = "learned synthetic face scale",
            ScaleCaveat = "Zoom changes effective FOV, so this is apparent camera-space distance until zoom/FOV is calibrated."
        },
        LastGoodFeatureStability = new LastGoodFeatureMeshStabilityReport
        {
            SampleCount = 4,
            HeadLockedSampleCount = 4,
            ComparedFeatureCount = 6,
            HealthPercent = 91d,
            WorstFeatureDriftPercent = 2.4d,
            Status = "head-locked features are stable",
            YawLeftSampleCount = 2,
            YawRightSampleCount = 2,
            YawRangeDegrees = 36d,
            YawHealthPercent = 93d,
            YawWorstFeatureDriftPercent = 2.1d,
            YawStatus = "B head-turn lock is stable",
            ANegativeSampleCount = 1,
            APositiveSampleCount = 1,
            ARangeDegrees = 18d,
            AHealthPercent = 91d,
            AWorstFeatureDriftPercent = 2.3d,
            AStatus = "A-axis tilt lock is stable",
            CNegativeSampleCount = 1,
            CPositiveSampleCount = 1,
            CRangeDegrees = 16d,
            CHealthPercent = 90d,
            CWorstFeatureDriftPercent = 2.4d,
            CStatus = "C-axis tilt lock is stable",
            ZCloseSampleCount = 1,
            ZFarSampleCount = 1,
            ZFaceScaleRangePercent = 22d,
            ZHealthPercent = 89d,
            ZWorstFeatureDriftPercent = 2.6d,
            ZStatus = "Z distance lock is stable",
            Findings = ["Head-locked feature centers stayed within the current drift tolerance across the retained samples."],
            YawFindings = ["During recent left/right head turns, feature centers stayed attached in head-locked coordinates."],
            AFindings = ["During recent A-axis tilts, feature centers stayed attached in head-locked coordinates."],
            CFindings = ["During recent C-axis tilts, feature centers stayed attached in head-locked coordinates."],
            ZFindings = ["During recent closer/farther samples, feature centers stayed attached after head-scale normalization."]
        },
        ReconstructionLane = new FaceReconstructionLaneStatus
        {
            FastTrackingAvailable = true,
            FastTrackingHasDenseFace = true,
            FastTrackingStatus = "MediaPipe/OpenCV tracking locked",
            AvatarReconstructionManifestPresent = true,
            AvatarReconstructionModelPresent = true,
            AvatarReconstructionCanRunInference = true,
            AvatarReconstructionStatus = "3DDFA_V2 ONNX lock 94%",
            AvatarReconstructionRuntime = "synthetic smoke",
            AvatarReconstructionModelDirectory = "synthetic",
            TrustLevel = "avatar-reconstruction",
            TrustDecision = "3DDFA_V2 ONNX synthetic lane is available",
            LearningImpact = "Does not block narcolepsy tracking.",
            Warnings = ["Synthetic dashboard lane warning"]
        },
        LastGoodFeatureSampleCount = 4,
        LastGoodFeatureStatus = "Retained 4 rolling feature samples for review.",
        LastGoodFeaturesHtmlPath = previewFiles.HtmlPath
    };
    var avatarDashboardJsonPath = new MeasurementAvatarSystemDashboardStore().Write(personalModelFolder, avatarDashboard);
    var avatarDashboardHtmlPath = MeasurementAvatarSystemDashboardStore.GetHtmlPath(avatarDashboardJsonPath);
    Require(File.Exists(avatarDashboardJsonPath), "synthetic stress did not write avatar system JSON");
    Require(File.Exists(avatarDashboardHtmlPath), "synthetic stress did not write avatar system HTML");
    var dashboardHtml = File.ReadAllText(avatarDashboardHtmlPath);
    Require(dashboardHtml.Contains("Active Backend Boundary", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted backend boundary");
    Require(dashboardHtml.Contains("Currently capturing", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted capture state");
    Require(dashboardHtml.Contains("3DDFA_V2 ONNX lock", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted 3DDFA lane status");
    Require(dashboardHtml.Contains("B head-turn lock", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted B head-turn lock status");
    Require(dashboardHtml.Contains("Z source", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted Z source metric");
    Require(dashboardHtml.Contains("A tilt lock", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted A tilt lock status");
    Require(dashboardHtml.Contains("C tilt lock", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted C tilt lock status");
    Require(dashboardHtml.Contains("Z distance lock", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted Z distance lock status");
    Require(dashboardHtml.Contains("A/B/C", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted XYZABC pose metric");
    Require(dashboardHtml.Contains("X is horizontal", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted XYZABC definition");
    Require(dashboardHtml.Contains("A rotate around X", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted A rotation metric");
    Require(dashboardHtml.Contains("B rotate around Y", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted B rotation metric");
    Require(dashboardHtml.Contains("C rotate around Z", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted C rotation metric");
    Require(dashboardHtml.Contains("Tracking holds", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted tracking hold count");
    Require(dashboardHtml.Contains("Pose audit", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted pose audit metric");
    Require(dashboardHtml.Contains("Feature anchoring", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted feature anchoring metric");
    Require(dashboardHtml.Contains("Identity session", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted identity-session metric");
    Require(dashboardHtml.Contains("identity signature warming", StringComparison.OrdinalIgnoreCase)
        || dashboardHtml.Contains("mature identity-session", StringComparison.OrdinalIgnoreCase)
        || dashboardHtml.Contains("comparable identity", StringComparison.OrdinalIgnoreCase),
        "synthetic stress avatar system dashboard omitted identity-session audit status");
    Require(dashboardHtml.Contains("Pose consistency", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted pose consistency metric");
    Require(dashboardHtml.Contains("Aperture consistency", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted aperture consistency metric");
    Require(dashboardHtml.Contains("Pose estimation", StringComparison.OrdinalIgnoreCase), "synthetic stress avatar system dashboard omitted pose estimation score");
    var firstWindow = records.Take(12).ToList();
    var lastWindow = records.Skip(Math.Max(0, records.Count - 12)).ToList();
    var edgeWindow = records.Where(static record => record.NearFrameEdge).ToList();
    var summary = new
    {
        FrameCount = records.Count,
        FirstAverageEyeOpening = AverageOptional(firstWindow.Select(static record => record.EyeOpening)),
        LastAverageEyeOpening = AverageOptional(lastWindow.Select(static record => record.EyeOpening)),
        FirstAverageMouthOpening = AverageOptional(firstWindow.Select(static record => record.MouthOpening)),
        LastAverageMouthOpening = AverageOptional(lastWindow.Select(static record => record.MouthOpening)),
        FirstAverageJawDroop = AverageOptional(firstWindow.Select(static record => record.JawDroop)),
        LastAverageJawDroop = AverageOptional(lastWindow.Select(static record => record.JawDroop)),
        EyeOpeningSlopePerSecond = EyeInsetAgreementAnalyzer.SlopePerSecond(records.Select(static record => (record.TimestampSeconds, record.EyeOpening))),
        MouthOpeningSlopePerSecond = EyeInsetAgreementAnalyzer.SlopePerSecond(records.Select(static record => (record.TimestampSeconds, record.MouthOpening))),
        JawDroopSlopePerSecond = EyeInsetAgreementAnalyzer.SlopePerSecond(records.Select(static record => (record.TimestampSeconds, record.JawDroop))),
        MaximumEyeClosureCue = MaxOptional(records.Select(static record => record.EyeClosureCue)),
        MaximumMouthOpeningCue = MaxOptional(records.Select(static record => record.MouthOpeningCue)),
        MaximumJawDroopCue = MaxOptional(records.Select(static record => record.JawDroopCue)),
        MaximumCompositeCue = MaxOptional(records.Select(static record => record.CompositeCue)),
        MaximumEyeClosingTrendSlope = MinOptional(records.Select(static record => record.EyeTrendSlope)),
        MaximumMouthOpeningTrendSlope = MaxOptional(records.Select(static record => record.MouthTrendSlope)),
        BaselineReadyRate = CountRate(records.Count(static record => record.BaselineReady), records.Count),
        EyeCueEligibleRate = CountRate(records.Count(static record => record.EyeCueEligible), records.Count),
        MouthCueEligibleRate = CountRate(records.Count(static record => record.MouthCueEligible), records.Count),
        ReconstructedEyeFrameCount = records.Count(static record => record.LeftEyeReconstructed || record.RightEyeReconstructed),
        MouthReconstructedFrameCount = records.Count(static record => record.MouthReconstructed),
        EyeArtifactSuppressedFrameCount = records.Count(static record => record.EyeArtifactSuppressed),
        EdgeStressFrameCount = edgeWindow.Count,
        EdgeStressEyeMeasurementRate = CountRate(edgeWindow.Count(static record => record.EyeOpening.HasValue), edgeWindow.Count),
        EdgeStressMouthMeasurementRate = CountRate(edgeWindow.Count(static record => record.MouthOpening.HasValue), edgeWindow.Count),
        MinimumFaceLeft = MinOptional(records.Select(static record => record.FaceLeft)),
        MinimumFaceTop = MinOptional(records.Select(static record => record.FaceTop)),
        MaximumFaceRight = MaxOptional(records.Select(static record => record.FaceRight)),
        MaximumFaceBottom = MaxOptional(records.Select(static record => record.FaceBottom)),
        AverageOverallQuality = AverageOptional(records.Select(static record => (double?)record.OverallQuality)),
        AverageCaptureQuality = AverageOptional(records.Select(static record => (double?)record.CaptureQualityScore)),
        MinimumCaptureQuality = MinOptional(records.Select(static record => (double?)record.CaptureQualityScore)),
        CaptureQualityCanCollectRate = CountRate(records.Count(static record => record.CaptureQualityCanCollect), records.Count),
        CaptureQualityAvatarGradeRate = CountRate(records.Count(static record => record.CaptureQualityAvatarGrade), records.Count),
        CaptureQualityLabels = records.Select(static record => record.CaptureQualityLabel).Where(static label => !string.IsNullOrWhiteSpace(label)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static label => label).ToList(),
        CaptureQualityIssueLabels = records.SelectMany(static record => record.CaptureQualityIssues.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static issue => issue).ToList(),
        AggregateSamples = aggregate.SampleCount,
        AggregateMinimumEyeOpening = aggregate.MinimumEyeOpeningRatio,
        AggregateMaximumMouthOpening = aggregate.MaximumMouthOpeningRatio,
        AggregateMaximumMouthOpeningChange = aggregate.MaximumMouthOpeningChangePercent,
        AggregateMaximumJawDroop = aggregate.MaximumJawDroopRatio,
        AggregateMaximumJawDroopChange = aggregate.MaximumJawDroopChangePercent,
        AggregateMaximumCueScore = aggregate.MaximumLandmarkCueScore,
        AggregateFaceReliabilitySamples = aggregate.FaceReliabilitySamples,
        AggregateFaceReliabilityUsableSamples = aggregate.FaceReliabilityUsableSamples,
        AggregateMinimumFaceReliability = aggregate.MinimumFaceReliabilityPercent,
        AggregateAverageFaceReliability = aggregate.AverageFaceReliabilityPercent,
        AggregateMinimumFaceContinuity = aggregate.MinimumFaceContinuityPercent,
        AggregateAverageFaceContinuity = aggregate.AverageFaceContinuityPercent,
        AggregateAverageEyeReliability = aggregate.AverageEyeReliabilityPercent,
        AggregateAverageMouthReliability = aggregate.AverageMouthReliabilityPercent,
        AggregateCaptureQualitySamples = aggregate.CaptureQualitySamples,
        AggregateCaptureQualityCanCollectSamples = aggregate.CaptureQualityCanCollectSamples,
        AggregateCaptureQualityAvatarGradeSamples = aggregate.CaptureQualityAvatarGradeSamples,
        AggregateMinimumCaptureQuality = aggregate.MinimumCaptureQualityScore,
        AggregateAverageCaptureQuality = aggregate.AverageCaptureQualityScore,
        TimelineSampleCount = timeline.Count,
        TimelineJsonPath = timelineFiles.JsonPath,
        TimelineCsvPath = timelineFiles.CsvPath,
        PersonalModelPath = personalModelPath,
        PersonalFaceMotionModelPath = motionModelPath,
        PersonalFaceMotionModelStatus = motionModel.Status,
        PersonalFaceMotionModelUsableObservations = motionModel.UsableObservationCount,
        PersonalFaceMotionModelMotionPairs = motionModel.MotionPairCount,
        PersonalFaceMotionModelEyeClosingVelocity = motionModel.EyeClosingVelocityPerSecond.Average,
        PersonalFaceMotionModelMouthOpeningVelocity = motionModel.MouthOpeningVelocityPerSecond.Average,
        PersonalFaceMotionModelJawDroopVelocity = motionModel.JawDroopVelocityPerSecond.Average,
        PersonalFaceCorpusReadinessPath = corpusReadinessPath,
        PersonalFaceCorpusReadinessHtmlPath = corpusReadinessHtmlPath,
        PersonalFaceCorpusReadinessStatus = corpusReadiness.Status,
        PersonalFaceCorpusReadinessPercent = corpusReadiness.OverallReadinessPercent,
        PersonalFaceCorpusContourShapeCoveragePercent = corpusReadiness.ContourShapeCoveragePercent,
        PersonalFaceCorpusContourDepthProfileHealthPercent = corpusReadiness.ContourDepthProfileHealthPercent,
        PersonalFaceCorpusSurfaceShapeCoveragePercent = corpusReadiness.SurfaceShapeCoveragePercent,
        PersonalFaceCorpusSurfaceDepthProfileHealthPercent = corpusReadiness.SurfaceDepthProfileHealthPercent,
        PersonalFaceCorpusZDistanceCoveragePercent = corpusReadiness.ZDistanceCoveragePercent,
        PersonalFaceCorpusZDistanceEvidenceHealthPercent = corpusReadiness.ZDistanceEvidenceHealthPercent,
        PersonalFaceCorpusZEstimateSamples = corpusReadiness.ZEstimateSamples,
        PersonalFaceCorpusAverageZConfidencePercent = corpusReadiness.AverageZConfidencePercent,
        PersonalFaceCorpusZApparentOnlyRate = corpusReadiness.ZApparentOnlyRate,
        PersonalFaceCorpusARotationAroundXCoveragePercent = corpusReadiness.ARotationAroundXCoveragePercent,
        PersonalFaceCorpusBRotationAroundYCoveragePercent = corpusReadiness.BRotationAroundYCoveragePercent,
        PersonalFaceCorpusCRotationAroundZCoveragePercent = corpusReadiness.CRotationAroundZCoveragePercent,
        PersonalFaceCorpusXYZABCCoveragePercent = corpusReadiness.XYZABCCoveragePercent,
        PersonalFaceCorpusLeftBrowShapeSamples = corpusReadiness.LeftBrowShapeSamples,
        PersonalFaceCorpusRightBrowShapeSamples = corpusReadiness.RightBrowShapeSamples,
        PersonalFaceCorpusNoseBridgeShapeSamples = corpusReadiness.NoseBridgeShapeSamples,
        PersonalFaceCorpusNoseBaseShapeSamples = corpusReadiness.NoseBaseShapeSamples,
        PersonalFaceCorpusLeftCheekSurfaceSamples = corpusReadiness.LeftCheekSurfaceSamples,
        PersonalFaceCorpusRightCheekSurfaceSamples = corpusReadiness.RightCheekSurfaceSamples,
        PersonalFaceCorpusForeheadSurfaceSamples = corpusReadiness.ForeheadSurfaceSamples,
        PersonalFaceCorpusDataAuditHealthPercent = corpusReadiness.DataAuditHealthPercent,
        PersonalFaceCorpusPoseEstimationHealthPercent = corpusReadiness.PoseEstimationHealthPercent,
        PersonalFaceCorpusFeatureAnchoringHealthPercent = corpusReadiness.FeatureAnchoringHealthPercent,
        PersonalFaceCorpusIdentitySessionHealthPercent = corpusReadiness.IdentitySessionHealthPercent,
        PersonalFaceCorpusIdentitySessionAuditStage = corpusReadiness.IdentitySessionAuditStage,
        PersonalFaceCorpusIdentitySessionAuditStatus = corpusReadiness.IdentitySessionAuditStatus,
        PersonalFaceCorpusRecentIdentityMeasurementSamples = corpusReadiness.RecentIdentityMeasurementSamples,
        PersonalFaceCorpusAverageRecentIdentityConfidencePercent = corpusReadiness.AverageRecentIdentityConfidencePercent,
        PersonalFaceCorpusRecentIdentityOutlierFrameRate = corpusReadiness.RecentIdentityOutlierFrameRate,
        PersonalFaceCorpusPoseExplainedFeatureMotionHealthPercent = corpusReadiness.PoseExplainedFeatureMotionHealthPercent,
        PersonalFaceCorpusPoseExplainedFeatureObservedRange = corpusReadiness.PoseExplainedFeatureObservedRange,
        PersonalFaceCorpusPoseExplainedFeatureExpectedRange = corpusReadiness.PoseExplainedFeatureExpectedRange,
        PersonalFaceCorpusEyeApertureReliabilityHealthPercent = corpusReadiness.EyeApertureReliabilityHealthPercent,
        PersonalFaceCorpusPossibleOneEyeArtifactRate = corpusReadiness.PossibleOneEyeArtifactRate,
        PersonalFaceCorpusEyeAgreementAveragePercent = corpusReadiness.EyeAgreementAveragePercent,
        PersonalFaceCorpusEyeAgreementMinimumPercent = corpusReadiness.EyeAgreementMinimumPercent,
        PersonalFaceCorpusMouthVerticalAnchorHealthPercent = corpusReadiness.MouthVerticalAnchorHealthPercent,
        PersonalFaceCorpusMouthVerticalAnchorSamplesReviewed = corpusReadiness.MouthVerticalAnchorSamplesReviewed,
        PersonalFaceCorpusMouthVerticalAnchorSuspiciousSampleRate = corpusReadiness.MouthVerticalAnchorSuspiciousSampleRate,
        PersonalFaceCorpusJawDroopScaleHealthPercent = corpusReadiness.JawDroopScaleHealthPercent,
        PersonalFaceCorpusMeasurementJournalCoveragePercent = corpusReadiness.MeasurementJournalCoveragePercent,
        PersonalFaceCorpusDataAuditFindings = corpusReadiness.DataAuditFindings,
        PersonalFaceCorpusReadinessWarnings = corpusReadiness.Warnings,
        PersonalFaceCorpusReadinessNextCaptureSuggestions = corpusReadiness.NextCaptureSuggestions,
        MeasurementFacePreviewJsonPath = previewFiles.JsonPath,
        MeasurementFacePreviewHtmlPath = previewFiles.HtmlPath,
        MeasurementFacePreviewRenderable = preview.CanRender,
        MeasurementFacePreviewConfidencePercent = preview.ConfidencePercent,
        MeasurementAvatarTrainingPackageJsonPath = avatarPackageFiles.JsonPath,
        MeasurementAvatarTrainingPackageHtmlPath = avatarPackageFiles.HtmlPath,
        MeasurementAvatarTrainingPackageCanUse = avatarPackage.CanUseForAvatarTraining,
        MeasurementAvatarTrainingPackageDecision = avatarPackage.TrainingDecision,
        MeasurementAvatarCapturePlanJsonPath = avatarCapturePlanFiles.JsonPath,
        MeasurementAvatarCapturePlanHtmlPath = avatarCapturePlanFiles.HtmlPath,
        MeasurementAvatarCapturePlanDecision = avatarCapturePlan.CollectionDecision,
        MeasurementAvatarCapturePlanItemCount = avatarCapturePlan.Items.Count,
        MeasurementAvatarCapturePlanEstimatedBytes = avatarCapturePlan.EstimatedMeasurementBytes,
        MeasurementAvatarSystemJsonPath = avatarDashboardJsonPath,
        MeasurementAvatarSystemHtmlPath = avatarDashboardHtmlPath,
        PersonalFaceCollectionAuditJsonPath = collectionAuditPath,
        PersonalFaceCollectionAuditHtmlPath = collectionAuditHtmlPath,
        PersonalFaceCollectionAuditStatus = collectionAudit.Status,
        PersonalFaceCollectionAuditFramesReviewed = collectionAudit.TotalFramesReviewed,
        PersonalFaceCollectionAuditFaceDetectionRate = collectionAudit.FaceDetectionRate,
        PersonalFaceCollectionAuditCollectableRate = collectionAudit.CaptureQualityCollectableRate,
        PersonalFaceCollectionAuditAvatarGradeRate = collectionAudit.CaptureQualityAvatarGradeRate,
        PersonalFaceCollectionAuditTopRejections = collectionAudit.TopPersonalModelRejectionReasons,
        PersonalFaceCollectionAuditTopCaptureIssues = collectionAudit.TopCaptureQualityIssues,
        PersonalFaceCollectionAuditNextActions = collectionAudit.NextActions,
        PersonalModelStatus = personalModel.Status,
        PersonalModelObservedSamples = personalModel.ObservedSamples,
        PersonalModelAcceptedSamples = personalModel.AcceptedSamples,
        PersonalModelRejectedSamples = personalModel.RejectedSamples,
        PersonalModelEventLikeRejectedSamples = personalModel.EventLikeRejectedSamples,
        PersonalModelLowQualityRejectedSamples = personalModel.LowQualityRejectedSamples,
        PersonalModelTrackingArtifactRejectedSamples = personalModel.TrackingArtifactRejectedSamples,
        PersonalModelNoFaceRejectedSamples = personalModel.NoFaceRejectedSamples,
        PersonalModelAcceptedSampleWeight = personalModel.AcceptedSampleWeight,
        PersonalModelLearningAnchorPercent = personalModel.LearningStability.AnchorPercent,
        PersonalModelLearningAnchorStatus = personalModel.LearningStability.AnchorStatus,
        PersonalModelMaxNextSampleInfluencePercent = personalModel.LearningStability.MaximumNextSampleInfluencePercent,
        PersonalModelMaxEventLikeNextSampleInfluencePercent = personalModel.LearningStability.MaximumEventLikeNextSampleInfluencePercent,
        PersonalModelAcceptedRate = personalModel.AcceptedRate,
        PersonalModelAverageEyeOpening = personalModel.AverageEyeOpeningRatio.Average,
        PersonalModelEyeOpeningWeight = personalModel.AverageEyeOpeningRatio.TotalWeight,
        PersonalModelAverageMouthOpening = personalModel.MouthOpeningRatio.Average,
        PersonalModelMouthOpeningWeight = personalModel.MouthOpeningRatio.TotalWeight,
        PersonalModelAverageJawDroop = personalModel.JawDroopRatio.Average,
        PersonalModelFaceCenterXRange = RangeOptional(personalModel.FaceCenterX.Minimum, personalModel.FaceCenterX.Maximum),
        PersonalModelFaceCenterYRange = RangeOptional(personalModel.FaceCenterY.Minimum, personalModel.FaceCenterY.Maximum),
        PersonalModelFaceWidthRange = RangeOptional(personalModel.FaceWidth.Minimum, personalModel.FaceWidth.Maximum),
        PersonalModelZEstimateSamples = personalModel.ZEstimateSamples,
        PersonalModelZApparentDistanceRange = RangeOptional(personalModel.ZApparentDistanceUnits.Minimum, personalModel.ZApparentDistanceUnits.Maximum),
        PersonalModelAverageZConfidencePercent = personalModel.ZConfidencePercent.Average,
        PersonalModelSurfaceShapeCoveragePercent = corpusReadiness.SurfaceShapeCoveragePercent,
        PersonalModelSurfaceDepthProfileHealthPercent = corpusReadiness.SurfaceDepthProfileHealthPercent,
        PersonalModelContourDepthProfileHealthPercent = corpusReadiness.ContourDepthProfileHealthPercent,
        PersonalModelZDistanceCoveragePercent = corpusReadiness.ZDistanceCoveragePercent,
        PersonalModelARotationAroundXCoveragePercent = corpusReadiness.ARotationAroundXCoveragePercent,
        PersonalModelBRotationAroundYCoveragePercent = corpusReadiness.BRotationAroundYCoveragePercent,
        PersonalModelCRotationAroundZCoveragePercent = corpusReadiness.CRotationAroundZCoveragePercent,
        PersonalModelXYZABCCoveragePercent = corpusReadiness.XYZABCCoveragePercent,
        Records = records
    };

    File.WriteAllText(
        outputPath,
        JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }),
        Encoding.UTF8);
}

static void RunSyntheticLandmarkStressArtifactSmoke()
{
    var outputPath = Path.Combine(Path.GetTempPath(), $"AvatarBuilderSyntheticStress-{Guid.NewGuid():N}", "stress_summary.json");
    WriteSyntheticLandmarkStress(outputPath);
    Require(File.Exists(outputPath), "synthetic landmark stress smoke did not write summary JSON");

    using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
    var root = document.RootElement;
    Require(
        root.TryGetProperty("PersonalModelMaxNextSampleInfluencePercent", out var influence)
        && influence.GetDouble() > 0d,
        "synthetic landmark stress summary omitted personal model max next-sample influence");
    Require(
        root.TryGetProperty("MeasurementAvatarSystemHtmlPath", out var dashboardPath)
        && File.Exists(dashboardPath.GetString()),
        "synthetic landmark stress summary omitted avatar system dashboard path");
}

static FaceLandmarkFrame AddSyntheticLandmarkStressEvidence(FaceLandmarkFrame frame, double progress, double sleepiness, int index)
{
    var source = frame.Source;
    var leftEyeContour = frame.LeftEyeContour;
    var rightEyeContour = frame.RightEyeContour;
    var innerLipContour = frame.InnerLipContour;
    var eyeConfidence = frame.EyeConfidence;
    var mouthConfidence = frame.MouthConfidence;
    var eyeGlare = 4d + sleepiness * 18d;
    var eyeContrast = 72d - sleepiness * 18d;
    var eyeSharpness = 82d - sleepiness * 16d;

    if (index is >= 18 and <= 23)
    {
        source += "; right-eye glasses occlusion";
        rightEyeContour = [];
        eyeConfidence = 0.36d;
        eyeGlare += 10d;
    }
    else if (index is >= 36 and <= 41)
    {
        source += "; shifted glasses artifact";
        leftEyeContour = CreateNormalizedOval(0.18d, 0.28d, 0.17d, 0.014d);
        eyeConfidence = 0.30d;
        eyeGlare += 18d;
    }

    if (index is >= 48 and <= 50)
    {
        source += "; mouth shadow gap";
        innerLipContour = [];
        mouthConfidence = 0.32d;
    }

    return new FaceLandmarkFrame
    {
        HasFace = frame.HasFace,
        Source = source,
        CapturedAtUtc = frame.CapturedAtUtc,
        TrackingConfidence = frame.TrackingConfidence,
        EyeConfidence = eyeConfidence,
        MouthConfidence = mouthConfidence,
        EyeImageQualityAvailable = true,
        MouthImageQualityAvailable = true,
        EyeGlarePercent = eyeGlare,
        MouthGlarePercent = 3d + sleepiness * 5d,
        EyeContrastPercent = eyeContrast,
        MouthContrastPercent = 64d - sleepiness * 9d,
        EyeSharpnessPercent = eyeSharpness,
        MouthSharpnessPercent = 72d - sleepiness * 10d,
        EyeDarkCoveragePercent = 15d + sleepiness * 7d,
        MouthDarkCoveragePercent = 8d + sleepiness * 22d,
        HeadYawDegrees = frame.HeadYawDegrees,
        HeadPitchDegrees = frame.HeadPitchDegrees,
        HeadRollDegrees = frame.HeadRollDegrees,
        FaceContour = frame.FaceContour,
        LeftEyeContour = leftEyeContour,
        RightEyeContour = rightEyeContour,
        OuterLipContour = frame.OuterLipContour,
        InnerLipContour = innerLipContour,
        JawContour = frame.JawContour,
        BlendshapeScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["eyeBlinkLeft"] = Math.Clamp(0.08d + sleepiness * 0.68d, 0d, 1d),
            ["eyeBlinkRight"] = Math.Clamp(0.09d + sleepiness * 0.66d, 0d, 1d),
            ["jawOpen"] = Math.Clamp(0.04d + sleepiness * 0.48d, 0d, 1d),
            ["mouthClose"] = Math.Clamp(0.88d - sleepiness * 0.58d, 0d, 1d)
        }
    };
}

static Mat CreateSyntheticVideoFrame(int index, bool includeEyeInset)
{
    var progress = index / (double)(SyntheticVideoFrameCount - 1);
    var frame = new Mat(SyntheticVideoHeight, SyntheticVideoWidth, MatType.CV_8UC3, new Scalar(31, 35, 39));
    DrawSyntheticRoom(frame);
    DrawSyntheticDecoyFaces(frame, progress);
    DrawSyntheticMovingFace(frame, progress);
    if (includeEyeInset)
    {
        DrawSyntheticEyeInset(frame, progress);
    }

    DrawSyntheticFrameLabel(frame, progress);
    return frame;
}

static void DrawSyntheticRoom(Mat frame)
{
    Cv2.Rectangle(frame, new CvRect(0, 0, SyntheticVideoWidth, SyntheticVideoHeight), new Scalar(38, 42, 47), -1);
    Cv2.Rectangle(frame, new CvRect(0, 248, SyntheticVideoWidth, 112), new Scalar(28, 31, 35), -1);
    Cv2.Line(frame, new CvPoint(0, 248), new CvPoint(SyntheticVideoWidth, 248), new Scalar(70, 78, 86), 2);
    Cv2.Rectangle(frame, new CvRect(34, 72, 120, 112), new Scalar(48, 54, 61), -1);
    Cv2.Rectangle(frame, new CvRect(486, 56, 110, 160), new Scalar(42, 48, 55), -1);
}

static void DrawSyntheticDecoyFaces(Mat frame, double progress)
{
    if (progress < 0.24d || progress > 0.78d)
    {
        return;
    }

    DrawSyntheticDecoyFace(frame, new CvPoint(96, 136), new CvSize(34, 45), glare: progress > 0.46d);
    DrawSyntheticDecoyFace(frame, new CvPoint(548, 138), new CvSize(32, 44), glare: progress > 0.62d);
}

static void DrawSyntheticDecoyFace(Mat frame, CvPoint center, CvSize size, bool glare)
{
    Cv2.Ellipse(frame, center, size, 0, 0, 360, new Scalar(77, 92, 103), -1);
    Cv2.Ellipse(frame, new CvPoint(center.X, center.Y - size.Height / 5), new CvSize(size.Width - 7, size.Height - 9), 0, 0, 360, new Scalar(111, 128, 139), -1);

    var leftEye = new CvRect(center.X - size.Width / 2 - 2, center.Y - size.Height / 5 - 6, 18, 12);
    var rightEye = new CvRect(center.X + size.Width / 2 - 16, center.Y - size.Height / 5 - 6, 18, 12);
    Cv2.Ellipse(frame, new CvPoint(leftEye.X + leftEye.Width / 2, leftEye.Y + leftEye.Height / 2), new CvSize(5, 2), 0, 0, 360, new Scalar(36, 42, 47), -1);
    Cv2.Ellipse(frame, new CvPoint(rightEye.X + rightEye.Width / 2, rightEye.Y + rightEye.Height / 2), new CvSize(5, 2), 0, 0, 360, new Scalar(36, 42, 47), -1);
    Cv2.Line(frame, new CvPoint(leftEye.X, leftEye.Y + 5), new CvPoint(rightEye.Right, rightEye.Y + 5), new Scalar(54, 63, 72), 1);

    var mouthCenter = new CvPoint(center.X, center.Y + size.Height / 4);
    Cv2.Ellipse(frame, mouthCenter, new CvSize(size.Width / 4, 3), 0, 0, 360, new Scalar(43, 47, 51), -1);
    if (glare)
    {
        Cv2.Line(frame, new CvPoint(center.X - size.Width / 2, center.Y - size.Height / 4), new CvPoint(center.X + size.Width / 2, center.Y + size.Height / 8), new Scalar(156, 170, 181), 1);
    }
}

static void DrawSyntheticMovingFace(Mat frame, double progress)
{
    var centerX = (int)Math.Round(292 + Math.Sin(progress * Math.PI * 2.2d) * 48d);
    var centerY = (int)Math.Round(150 + Math.Cos(progress * Math.PI * 1.4d) * 16d);
    var scale = 0.82d + Math.Sin(progress * Math.PI) * 0.22d;
    var faceWidth = (int)Math.Round(116 * scale);
    var faceHeight = (int)Math.Round(150 * scale);
    var eyeOpen = 9d - progress * 6.2d;
    var mouthOpen = 3d + progress * 24d;

    Cv2.Ellipse(frame, new CvPoint(centerX, centerY), new CvSize(faceWidth / 2, faceHeight / 2), 0, 0, 360, new Scalar(121, 156, 181), -1);
    Cv2.Ellipse(frame, new CvPoint(centerX, centerY - faceHeight / 18), new CvSize(faceWidth / 2 - 6, faceHeight / 2 - 5), 0, 0, 360, new Scalar(181, 205, 218), -1);
    Cv2.Ellipse(frame, new CvPoint(centerX, centerY - faceHeight / 2 - 12), new CvSize(faceWidth / 2 + 12, 24), 0, 0, 360, new Scalar(90, 116, 145), -1);

    var leftEye = new CvRect(centerX - faceWidth / 3 - 20, centerY - faceHeight / 7, 40, 24);
    var rightEye = new CvRect(centerX + faceWidth / 3 - 20, centerY - faceHeight / 7, 40, 24);
    DrawSyntheticEye(frame, leftEye, eyeOpen, withGlasses: true);
    DrawSyntheticEye(frame, rightEye, eyeOpen * 0.96d, withGlasses: true);

    Cv2.Line(frame, new CvPoint(leftEye.Right, leftEye.Y + 10), new CvPoint(rightEye.X, rightEye.Y + 10), new Scalar(38, 45, 54), 3);
    Cv2.Rectangle(frame, leftEye, new Scalar(40, 47, 56), 2);
    Cv2.Rectangle(frame, rightEye, new Scalar(40, 47, 56), 2);

    var mouthCenter = new CvPoint(centerX, centerY + faceHeight / 4);
    Cv2.Ellipse(frame, mouthCenter, new CvSize((int)(36 * scale), Math.Max(2, (int)Math.Round(mouthOpen))), 0, 0, 360, new Scalar(31, 34, 39), -1);
    Cv2.Ellipse(frame, mouthCenter, new CvSize((int)(42 * scale), Math.Max(4, (int)Math.Round(mouthOpen + 3))), 0, 0, 360, new Scalar(99, 126, 146), 2);
}

static void DrawSyntheticEye(Mat frame, CvRect eyeBox, double opening, bool withGlasses)
{
    var center = new CvPoint(eyeBox.X + eyeBox.Width / 2, eyeBox.Y + eyeBox.Height / 2);
    Cv2.Ellipse(frame, center, new CvSize(16, Math.Max(2, (int)Math.Round(opening))), 0, 0, 360, new Scalar(18, 21, 24), -1);
    if (withGlasses)
    {
        Cv2.Line(frame, new CvPoint(eyeBox.X + 3, eyeBox.Y + 4), new CvPoint(eyeBox.Right - 3, eyeBox.Bottom - 7), new Scalar(225, 234, 238), 2);
    }
}

static void DrawSyntheticEyeInset(Mat frame, double progress)
{
    var inset = new CvRect(
        (int)Math.Round(SyntheticVideoWidth * 0.62d),
        (int)Math.Round(SyntheticVideoHeight * 0.56d),
        (int)Math.Round(SyntheticVideoWidth * 0.36d),
        (int)Math.Round(SyntheticVideoHeight * 0.40d));
    Cv2.Rectangle(frame, inset, new Scalar(244, 244, 244), -1);
    Cv2.Rectangle(frame, inset, new Scalar(104, 118, 132), 2);

    var eyeTop = inset.Y + (int)Math.Round(inset.Height * 0.10d);
    var eyeHeight = (int)Math.Round(inset.Height * 0.78d);
    var horizontalPadding = (int)Math.Round(inset.Width * 0.035d);
    var centerGap = (int)Math.Round(inset.Width * 0.025d);
    var halfRegionWidth = (inset.Width - horizontalPadding * 2 - centerGap) / 2;
    var left = new CvRect(inset.X + horizontalPadding, eyeTop, halfRegionWidth, eyeHeight);
    var right = new CvRect(inset.X + horizontalPadding + halfRegionWidth + centerGap, eyeTop, halfRegionWidth, eyeHeight);
    var opening = 13d - progress * 9.6d;
    DrawSyntheticInsetEye(frame, left, opening);
    DrawSyntheticInsetEye(frame, right, opening * 0.94d);

    var bridgeX = inset.X + inset.Width / 2;
    Cv2.Line(frame, new CvPoint(bridgeX, eyeTop + 6), new CvPoint(bridgeX, eyeTop + eyeHeight - 6), new Scalar(75, 75, 75), 3);
    Cv2.Line(frame, new CvPoint(left.X + 12, left.Y + 8), new CvPoint(right.Right - 12, right.Y + 8), new Scalar(82, 82, 82), 3);
}

static void DrawSyntheticInsetEye(Mat frame, CvRect eyeBox, double opening)
{
    var center = new CvPoint(eyeBox.X + eyeBox.Width / 2, eyeBox.Y + eyeBox.Height / 2);
    var halfWidth = Math.Max(12, (int)Math.Round(eyeBox.Width * 0.35d));
    var halfHeight = Math.Max(3, (int)Math.Round(opening));
    Cv2.Ellipse(frame, center, new CvSize(halfWidth, halfHeight), 0, 0, 360, new Scalar(8, 8, 8), -1);
    Cv2.Rectangle(frame, eyeBox, new Scalar(88, 88, 88), 2);
    Cv2.Line(frame, new CvPoint(eyeBox.X + 4, eyeBox.Y + eyeBox.Height - 10), new CvPoint(eyeBox.Right - 4, eyeBox.Y + 12), new Scalar(230, 230, 230), 4);
}

static void DrawSyntheticFrameLabel(Mat frame, double progress)
{
    var label = $"synthetic sleepiness cue {progress:P0}";
    Cv2.PutText(frame, label, new CvPoint(16, 28), HersheyFonts.HersheySimplex, 0.62, new Scalar(210, 224, 235), 1, LineTypes.AntiAlias);
}

static FaceLandmarkMetrics CreateMetrics(
    double? eyeOpening,
    double? mouthOpening,
    double? mouthVelocity = null,
    double? jawDroop = null,
    double? jawDroopVelocity = null,
    double trackingConfidence = 0.8d,
    double eyeConfidence = 0.8d,
    double mouthConfidence = 0.8d,
    double? eyeQuality = null,
    double? mouthQuality = null,
    double? mediaPipeBlink = null,
    double? mediaPipeJawOpen = null,
    double? mediaPipeMouthClose = null,
    string source = "smoke",
    DateTime? capturedAtUtc = null)
{
    return new FaceLandmarkMetrics
    {
        HasFace = true,
        Source = source,
        ConfidenceLabel = "strong",
        CapturedAtUtc = capturedAtUtc ?? DateTime.UtcNow,
        TrackingConfidence = trackingConfidence,
        EyeConfidence = eyeConfidence,
        MouthConfidence = mouthConfidence,
        EyeMeasurementQualityPercent = eyeQuality ?? eyeConfidence * 100d,
        MouthMeasurementQualityPercent = mouthQuality ?? mouthConfidence * 100d,
        LeftEyeOpeningRatio = eyeOpening,
        RightEyeOpeningRatio = eyeOpening,
        AverageEyeOpeningRatio = eyeOpening,
        MouthOpeningRatio = mouthOpening,
        MouthOpeningVelocityPerSecond = mouthVelocity,
        RawJawDroopRatio = jawDroop,
        JawDroopRatio = jawDroop,
        JawDroopVelocityPerSecond = jawDroopVelocity,
        MediaPipeAverageEyeBlinkPercent = mediaPipeBlink,
        MediaPipeJawOpenPercent = mediaPipeJawOpen,
        MediaPipeMouthClosePercent = mediaPipeMouthClose
    };
}

static FaceLandmarkFrame CreateSyntheticLandmarkFrame(
    DateTime capturedAtUtc,
    double? leftEyeRatio,
    double? rightEyeRatio,
    double? mouthRatio,
    double eyeConfidence,
    double mouthConfidence,
    string source = "synthetic landmarks",
    double? eyeBlinkLeftScore = null,
    double? eyeBlinkRightScore = null,
    double? jawOpenScore = null,
    double? mouthCloseScore = null,
    double faceHalfWidth = 0.22d,
    double faceHalfHeight = 0.32d,
    double eyeCenterOffset = 0.08d,
    double eyeHalfWidth = 0.055d,
    double mouthHalfWidth = 0.080d,
    double eyeCenterY = 0.39d,
    double mouthCenterY = 0.62d,
    double headYawDegrees = 0d,
    double headPitchDegrees = 0d,
    double headRollDegrees = 0d,
    bool leftEyeReconstructed = false,
    bool rightEyeReconstructed = false,
    bool mouthReconstructed = false,
    bool eyeArtifactSuppressed = false,
    double? leftEyeHalfWidth = null,
    double? rightEyeHalfWidth = null,
    double mouthCenterX = 0.50d)
{
    var leftEyeWidth = leftEyeHalfWidth ?? eyeHalfWidth;
    var rightEyeWidth = rightEyeHalfWidth ?? eyeHalfWidth;
    return new FaceLandmarkFrame
    {
        HasFace = true,
        Source = source,
        CapturedAtUtc = capturedAtUtc,
        TrackingConfidence = 0.80d,
        EyeConfidence = eyeConfidence,
        MouthConfidence = mouthConfidence,
        LeftEyeReconstructed = leftEyeReconstructed,
        RightEyeReconstructed = rightEyeReconstructed,
        MouthReconstructed = mouthReconstructed,
        EyeArtifactSuppressed = eyeArtifactSuppressed,
        HeadYawDegrees = headYawDegrees,
        HeadPitchDegrees = headPitchDegrees,
        HeadRollDegrees = headRollDegrees,
        FaceContour = CreateNormalizedOval(0.50d, 0.48d, faceHalfWidth, faceHalfHeight),
        LeftEyeContour = leftEyeRatio is double left ? CreateNormalizedOval(0.50d - eyeCenterOffset, eyeCenterY, leftEyeWidth, leftEyeWidth * left) : [],
        RightEyeContour = rightEyeRatio is double right ? CreateNormalizedOval(0.50d + eyeCenterOffset, eyeCenterY, rightEyeWidth, rightEyeWidth * right) : [],
        LeftBrowContour = CreateSyntheticBrow(0.50d - eyeCenterOffset, eyeCenterY - eyeHalfWidth * 0.90d, leftEyeWidth),
        RightBrowContour = CreateSyntheticBrow(0.50d + eyeCenterOffset, eyeCenterY - eyeHalfWidth * 0.90d, rightEyeWidth),
        InnerLipContour = mouthRatio is double mouth ? CreateNormalizedOval(mouthCenterX, mouthCenterY, mouthHalfWidth, mouthHalfWidth * mouth) : [],
        JawContour = [new(0.50d - faceHalfWidth * 0.64d, mouthCenterY), new(0.50d - faceHalfWidth * 0.32d, 0.74d), new(0.50d, 0.79d), new(0.50d + faceHalfWidth * 0.32d, 0.74d), new(0.50d + faceHalfWidth * 0.64d, mouthCenterY)],
        BlendshapeScores = CreateBlendshapeScores(eyeBlinkLeftScore, eyeBlinkRightScore, jawOpenScore, mouthCloseScore)
    };
}

static FaceLandmarkFrame CreateSyntheticDenseMeshFrame(
    double scale = 1d,
    double matrixYawDegrees = 0d,
    double matrixPitchDegrees = 0d,
    double matrixRollDegrees = 0d,
    DateTime? capturedAtUtc = null)
{
    int[] faceOval =
    [
        10, 338, 297, 332, 284, 251, 389, 356, 454, 323, 361, 288,
        397, 365, 379, 378, 400, 377, 152, 148, 176, 149, 150, 136,
        172, 58, 132, 93, 234, 127, 162, 21, 54, 103, 67, 109
    ];
    int[] eyeA =
    [
        33, 246, 161, 160, 159, 158, 157, 173, 133, 155, 154, 153, 145, 144, 163, 7
    ];
    int[] eyeB =
    [
        362, 398, 384, 385, 386, 387, 388, 466, 263, 249, 390, 373, 374, 380, 381, 382
    ];
    int[] browA =
    [
        70, 63, 105, 66, 107, 55, 65, 52, 53, 46
    ];
    int[] browB =
    [
        336, 296, 334, 293, 300, 285, 295, 282, 283, 276
    ];
    int[] outerLip =
    [
        61, 185, 40, 39, 37, 0, 267, 269, 270, 409,
        291, 375, 321, 405, 314, 17, 84, 181, 91, 146
    ];
    int[] innerLip =
    [
        78, 191, 80, 81, 82, 13, 312, 311, 310, 415,
        308, 324, 318, 402, 317, 14, 87, 178, 88, 95
    ];
    int[] jaw =
    [
        234, 93, 132, 58, 172, 136, 150, 149, 176, 148, 152,
        377, 400, 378, 379, 365, 397, 288, 361, 323, 454
    ];

    var points = new SyntheticMeshPoint[468];
    for (var index = 0; index < points.Length; index++)
    {
        var angle = index / (double)points.Length * Math.PI * 2d;
        points[index] = new SyntheticMeshPoint(
            0.50d + Math.Cos(angle) * 0.15d,
            0.50d + Math.Sin(angle) * 0.23d,
            Math.Sin(angle) * 0.025d);
    }

    SetOval(points, faceOval, 0.50d, 0.50d, 0.22d * scale, 0.32d * scale, zScale: 0.030d * scale);
    SetOval(points, eyeA, 0.50d - 0.08d * scale, 0.50d - 0.10d * scale, 0.055d * scale, 0.014d * scale, zScale: 0.040d * scale);
    SetOval(points, eyeB, 0.50d + 0.08d * scale, 0.50d - 0.10d * scale, 0.055d * scale, 0.014d * scale, zScale: 0.040d * scale);
    SetOval(points, outerLip, 0.50d, 0.50d + 0.125d * scale, 0.095d * scale, 0.030d * scale, zScale: 0.055d * scale);
    SetOval(points, innerLip, 0.50d, 0.50d + 0.125d * scale, 0.065d * scale, 0.012d * scale, zScale: 0.060d * scale);
    SetPolyline(points, jaw, ScaleAnchors([new(0.31d, 0.57d), new(0.36d, 0.72d), new(0.50d, 0.81d), new(0.64d, 0.72d), new(0.69d, 0.57d)], scale), z: 0.015d * scale);
    SetPolyline(points, [168, 6, 197, 195, 5, 4, 1, 19, 94, 2], ScaleAnchors([new(0.50d, 0.42d), new(0.50d, 0.55d), new(0.50d, 0.59d)], scale), z: 0.080d * scale);
    SetPolyline(points, [98, 97, 2, 326, 327], ScaleAnchors([new(0.43d, 0.60d), new(0.50d, 0.62d), new(0.57d, 0.60d)], scale), z: 0.060d * scale);
    SetPolyline(points, browA, ScaleAnchors([new(0.35d, 0.33d), new(0.45d, 0.32d)], scale), z: 0.035d * scale);
    SetPolyline(points, browB, ScaleAnchors([new(0.55d, 0.32d), new(0.65d, 0.33d)], scale), z: 0.035d * scale);
    SetPolyline(points, [234, 93, 132, 58, 172, 136], ScaleAnchors([new(0.31d, 0.46d), new(0.36d, 0.57d)], scale), z: 0.010d * scale);
    SetPolyline(points, [454, 323, 361, 288, 397, 365], ScaleAnchors([new(0.69d, 0.46d), new(0.64d, 0.57d)], scale), z: 0.010d * scale);

    var densePoints = points
        .Select((point, index) => new FaceMeshLandmarkPoint
        {
            Index = index,
            X = point.X,
            Y = point.Y,
            Z = point.Z
        })
        .ToList();

    return new FaceLandmarkFrame
    {
        HasFace = true,
        Source = "synthetic MediaPipe dense mesh",
        CapturedAtUtc = capturedAtUtc ?? DateTime.UtcNow,
        TrackingConfidence = 0.94d,
        EyeConfidence = 0.92d,
        MouthConfidence = 0.91d,
        HeadYawDegrees = matrixYawDegrees,
        HeadPitchDegrees = matrixPitchDegrees,
        HeadRollDegrees = matrixRollDegrees,
        DenseMeshTopology = "MediaPipeFaceMesh468",
        DenseMeshPoints = densePoints,
        FacialTransformationMatrix = CreateSyntheticFacialTransformMatrix(matrixYawDegrees, matrixPitchDegrees, matrixRollDegrees),
        FaceContour = SelectMeshPoints(densePoints, faceOval),
        LeftEyeContour = SelectMeshPoints(densePoints, eyeA),
        RightEyeContour = SelectMeshPoints(densePoints, eyeB),
        LeftBrowContour = SelectMeshPoints(densePoints, browA),
        RightBrowContour = SelectMeshPoints(densePoints, browB),
        OuterLipContour = SelectMeshPoints(densePoints, outerLip),
        InnerLipContour = SelectMeshPoints(densePoints, innerLip),
        JawContour = SelectMeshPoints(densePoints, jaw),
        BlendshapeScores = CreateBlendshapeScores(0.10d, 0.11d, 0.08d, 0.86d)
    };
}

static FaceLandmarkFrame CreateSyntheticDenseYawEvidenceFrame()
{
    var baseFrame = CreateSyntheticDenseMeshFrame(matrixYawDegrees: 0d, matrixPitchDegrees: 0d, matrixRollDegrees: 0d);
    var densePoints = baseFrame.DenseMeshPoints
        .Select(static point =>
        {
            var x = point.Index == 1 ? point.X + 0.075d : point.X;
            var z = point.Index switch
            {
                234 => point.Z - 0.050d,
                454 => point.Z + 0.050d,
                _ => point.Z
            };
            return new FaceMeshLandmarkPoint
            {
                Index = point.Index,
                X = x,
                Y = point.Y,
                Z = z
            };
        })
        .ToList();

    return new FaceLandmarkFrame
    {
        HasFace = true,
        Source = "synthetic MediaPipe dense mesh with flat transform and yaw geometry",
        CapturedAtUtc = baseFrame.CapturedAtUtc,
        TrackingConfidence = baseFrame.TrackingConfidence,
        EyeConfidence = baseFrame.EyeConfidence,
        MouthConfidence = baseFrame.MouthConfidence,
        HeadYawDegrees = 0d,
        HeadPitchDegrees = 0d,
        HeadRollDegrees = 0d,
        DenseMeshTopology = baseFrame.DenseMeshTopology,
        DenseMeshPoints = densePoints,
        FacialTransformationMatrix = CreateSyntheticFacialTransformMatrix(0d, 0d, 0d),
        FaceContour = baseFrame.FaceContour,
        LeftEyeContour = baseFrame.LeftEyeContour,
        RightEyeContour = baseFrame.RightEyeContour,
        LeftBrowContour = baseFrame.LeftBrowContour,
        RightBrowContour = baseFrame.RightBrowContour,
        OuterLipContour = baseFrame.OuterLipContour,
        InnerLipContour = baseFrame.InnerLipContour,
        JawContour = baseFrame.JawContour,
        BlendshapeScores = baseFrame.BlendshapeScores
    };
}

static IReadOnlyList<WpfPoint> SelectMeshPoints(IReadOnlyList<FaceMeshLandmarkPoint> points, IReadOnlyList<int> indices)
{
    var lookup = points.ToDictionary(static point => point.Index);
    return indices
        .Where(lookup.ContainsKey)
        .Select(index => new WpfPoint(lookup[index].X, lookup[index].Y))
        .ToList();
}

static IReadOnlyList<WpfPoint> ScaleAnchors(IReadOnlyList<WpfPoint> anchors, double scale)
{
    return anchors
        .Select(point => new WpfPoint(
            0.50d + (point.X - 0.50d) * scale,
            0.50d + (point.Y - 0.50d) * scale))
        .ToList();
}

static IReadOnlyList<double> CreateSyntheticFacialTransformMatrix(
    double yawDegrees,
    double pitchDegrees,
    double rollDegrees)
{
    var yaw = yawDegrees * Math.PI / 180d;
    var pitch = pitchDegrees * Math.PI / 180d;
    var roll = rollDegrees * Math.PI / 180d;

    var sinYaw = Math.Sin(yaw);
    var cosYaw = Math.Cos(yaw);
    var sinPitch = Math.Sin(pitch);
    var cosPitch = Math.Cos(pitch);
    var sinRoll = Math.Sin(roll);
    var cosRoll = Math.Cos(roll);

    var yawMatrix = new[,]
    {
        { cosYaw, 0d, sinYaw },
        { 0d, 1d, 0d },
        { -sinYaw, 0d, cosYaw }
    };
    var pitchMatrix = new[,]
    {
        { 1d, 0d, 0d },
        { 0d, cosPitch, -sinPitch },
        { 0d, sinPitch, cosPitch }
    };
    var rollMatrix = new[,]
    {
        { cosRoll, -sinRoll, 0d },
        { sinRoll, cosRoll, 0d },
        { 0d, 0d, 1d }
    };

    var rotation = MultiplyMatrix(MultiplyMatrix(rollMatrix, pitchMatrix), yawMatrix);
    return
    [
        rotation[0, 0], rotation[0, 1], rotation[0, 2], 0d,
        rotation[1, 0], rotation[1, 1], rotation[1, 2], 0d,
        rotation[2, 0], rotation[2, 1], rotation[2, 2], 0d,
        0d, 0d, 0d, 1d
    ];
}

static double[,] MultiplyMatrix(double[,] left, double[,] right)
{
    var result = new double[3, 3];
    for (var row = 0; row < 3; row++)
    {
        for (var column = 0; column < 3; column++)
        {
            result[row, column] =
                left[row, 0] * right[0, column]
                + left[row, 1] * right[1, column]
                + left[row, 2] * right[2, column];
        }
    }

    return result;
}

static void SetOval(SyntheticMeshPoint[] points, IReadOnlyList<int> indices, double centerX, double centerY, double halfWidth, double halfHeight, double zScale)
{
    for (var offset = 0; offset < indices.Count; offset++)
    {
        var angle = offset / (double)indices.Count * Math.PI * 2d;
        points[indices[offset]] = new SyntheticMeshPoint(
            centerX + Math.Cos(angle) * halfWidth,
            centerY + Math.Sin(angle) * halfHeight,
            Math.Cos(angle) * zScale);
    }
}

static void SetPolyline(SyntheticMeshPoint[] points, IReadOnlyList<int> indices, IReadOnlyList<WpfPoint> anchors, double z)
{
    for (var offset = 0; offset < indices.Count; offset++)
    {
        var t = indices.Count == 1 ? 0d : offset / (double)(indices.Count - 1);
        var scaled = t * (anchors.Count - 1);
        var anchorIndex = Math.Min(anchors.Count - 2, Math.Max(0, (int)Math.Floor(scaled)));
        var local = scaled - anchorIndex;
        var first = anchors[anchorIndex];
        var second = anchors[anchorIndex + 1];
        points[indices[offset]] = new SyntheticMeshPoint(
            first.X + (second.X - first.X) * local,
            first.Y + (second.Y - first.Y) * local,
            z);
    }
}

static IReadOnlyDictionary<string, double> CreateBlendshapeScores(
    double? eyeBlinkLeftScore,
    double? eyeBlinkRightScore,
    double? jawOpenScore,
    double? mouthCloseScore)
{
    var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    if (eyeBlinkLeftScore is double left)
    {
        scores["eyeBlinkLeft"] = Math.Clamp(left, 0d, 1d);
    }

    if (eyeBlinkRightScore is double right)
    {
        scores["eyeBlinkRight"] = Math.Clamp(right, 0d, 1d);
    }

    if (jawOpenScore is double jaw)
    {
        scores["jawOpen"] = Math.Clamp(jaw, 0d, 1d);
    }

    if (mouthCloseScore is double mouthClose)
    {
        scores["mouthClose"] = Math.Clamp(mouthClose, 0d, 1d);
    }

    return scores;
}

static IReadOnlyList<WpfPoint> CreateSyntheticFacemark68Points(
    double eyeRatio,
    double mouthRatio,
    WpfPoint? center = null,
    double scale = 1d,
    double rollDegrees = 0d,
    double jawDroopRatio = 0d,
    double yawDegrees = 0d,
    double pitchDegrees = 0d)
{
    var points = Enumerable.Repeat(new WpfPoint(0.50d, 0.50d), 68).ToArray();
    for (var i = 0; i < 17; i++)
    {
        var t = i / 16d;
        var x = 0.30d + t * 0.40d;
        var lowerFaceWeight = Math.Sin(t * Math.PI);
        var y = 0.50d + lowerFaceWeight * (0.23d + jawDroopRatio);
        points[i] = new WpfPoint(x, y);
    }

    FillLine(points, 17, 5, new WpfPoint(0.34d, 0.35d), new WpfPoint(0.46d, 0.33d));
    FillLine(points, 22, 5, new WpfPoint(0.54d, 0.33d), new WpfPoint(0.66d, 0.35d));
    FillLine(points, 27, 4, new WpfPoint(0.50d, 0.38d), new WpfPoint(0.50d, 0.54d));
    FillLine(points, 31, 5, new WpfPoint(0.43d, 0.55d), new WpfPoint(0.57d, 0.55d));
    FillClosedOval(points, 36, 6, 0.40d, 0.42d, 0.050d, 0.050d * eyeRatio);
    FillClosedOval(points, 42, 6, 0.60d, 0.42d, 0.050d, 0.050d * eyeRatio);
    FillClosedOval(points, 48, 12, 0.50d, 0.63d, 0.110d, 0.050d + 0.050d * mouthRatio);
    FillClosedOval(points, 60, 8, 0.50d, 0.63d, 0.075d, 0.075d * mouthRatio);
    ApplySyntheticHeadPose(points, yawDegrees, pitchDegrees);
    return TransformFacePoints(points, center ?? new WpfPoint(0.50d, 0.50d), scale, rollDegrees);
}

static void ApplySyntheticHeadPose(WpfPoint[] points, double yawDegrees, double pitchDegrees)
{
    var yawOffset = Math.Clamp(yawDegrees, -28d, 28d) / 34d * 0.20d;
    var pitchOffset = Math.Clamp(pitchDegrees, -24d, 24d) / 50d * 0.21d;

    for (var index = 27; index <= 35; index++)
    {
        var noseWeight = index <= 30
            ? (index - 27) / 3d
            : Math.Max(0.35d, 1d - (index - 30) / 5d);
        points[index] = OffsetPoint(points[index], yawOffset * noseWeight, pitchOffset * noseWeight);
    }

    for (var index = 31; index <= 35; index++)
    {
        points[index] = OffsetPoint(points[index], yawOffset * 0.18d, pitchOffset * 0.18d);
    }
}

static WpfPoint OffsetPoint(WpfPoint point, double x, double y)
{
    return new WpfPoint(
        Math.Clamp(point.X + x, 0d, 1d),
        Math.Clamp(point.Y + y, 0d, 1d));
}

static void FillLine(WpfPoint[] points, int start, int count, WpfPoint first, WpfPoint last)
{
    for (var i = 0; i < count; i++)
    {
        var t = count == 1 ? 0d : i / (double)(count - 1);
        points[start + i] = new WpfPoint(
            first.X + (last.X - first.X) * t,
            first.Y + (last.Y - first.Y) * t);
    }
}

static void FillClosedOval(WpfPoint[] points, int start, int count, double centerX, double centerY, double halfWidth, double halfHeight)
{
    for (var i = 0; i < count; i++)
    {
        var angle = i / (double)count * Math.PI * 2d;
        points[start + i] = new WpfPoint(
            centerX + Math.Cos(angle) * halfWidth,
            centerY + Math.Sin(angle) * halfHeight);
    }
}

static IReadOnlyList<WpfPoint> TransformFacePoints(IReadOnlyList<WpfPoint> points, WpfPoint center, double scale, double rollDegrees)
{
    var radians = rollDegrees * Math.PI / 180d;
    var cos = Math.Cos(radians);
    var sin = Math.Sin(radians);
    return points
        .Select(point =>
        {
            var x = (point.X - 0.50d) * scale;
            var y = (point.Y - 0.50d) * scale;
            var rotatedX = x * cos - y * sin;
            var rotatedY = x * sin + y * cos;
            return new WpfPoint(
                Math.Clamp(center.X + rotatedX, 0d, 1d),
                Math.Clamp(center.Y + rotatedY, 0d, 1d));
        })
        .ToList();
}

static IReadOnlyList<WpfPoint> CreateNormalizedOval(double centerX, double centerY, double halfWidth, double halfHeight)
{
    return
    [
        new(centerX - halfWidth, centerY),
        new(centerX - halfWidth * 0.72d, centerY - halfHeight * 0.70d),
        new(centerX, centerY - halfHeight),
        new(centerX + halfWidth * 0.72d, centerY - halfHeight * 0.70d),
        new(centerX + halfWidth, centerY),
        new(centerX + halfWidth * 0.72d, centerY + halfHeight * 0.70d),
        new(centerX, centerY + halfHeight),
        new(centerX - halfWidth * 0.72d, centerY + halfHeight * 0.70d)
    ];
}

static IReadOnlyList<WpfPoint> CreateSyntheticBrow(double centerX, double centerY, double halfWidth)
{
    return
    [
        new(centerX - halfWidth * 0.95d, centerY + halfWidth * 0.08d),
        new(centerX - halfWidth * 0.45d, centerY - halfWidth * 0.03d),
        new(centerX, centerY - halfWidth * 0.08d),
        new(centerX + halfWidth * 0.45d, centerY - halfWidth * 0.03d),
        new(centerX + halfWidth * 0.95d, centerY + halfWidth * 0.08d)
    ];
}

static double? CalculateContourOpeningRatio(IReadOnlyList<WpfPoint> contour)
{
    return ContourOpeningEstimator.CalculateOpeningRatio(contour);
}

static double SmoothStep(double value)
{
    var x = Math.Clamp(value, 0d, 1d);
    return x * x * (3d - 2d * x);
}

static double? AverageOptional(IEnumerable<double?> values)
{
    var numbers = values
        .Where(static value => value.HasValue)
        .Select(static value => value!.Value)
        .ToList();
    return numbers.Count == 0 ? null : numbers.Average();
}

static double? MaxOptional(IEnumerable<double?> values)
{
    var numbers = values
        .Where(static value => value.HasValue)
        .Select(static value => value!.Value)
        .ToList();
    return numbers.Count == 0 ? null : numbers.Max();
}

static double? MinOptional(IEnumerable<double?> values)
{
    var numbers = values
        .Where(static value => value.HasValue)
        .Select(static value => value!.Value)
        .ToList();
    return numbers.Count == 0 ? null : numbers.Min();
}

static double? RangeOptional(double? minimum, double? maximum)
{
    return minimum is double min && maximum is double max
        ? max - min
        : null;
}

static PersonalMetricDistribution Distribution(double average, int sampleCount = 96, double totalWeight = 82d, double spread = 0.015d)
{
    return new PersonalMetricDistribution
    {
        SampleCount = sampleCount,
        TotalWeight = totalWeight,
        Average = average,
        Minimum = average - spread,
        Maximum = average + spread,
        StandardDeviation = spread / 2d,
        ExponentialMovingAverage = average,
        NormalLow = average - spread * 2d,
        NormalHigh = average + spread * 2d
    };
}

static List<PersonalFacePoseBucketProfile> PoseBucketProfiles(int sampleCount = 64, double totalWeight = 56d)
{
    return PersonalFacePoseBuckets.Definitions
        .Select(definition =>
        {
            var (yaw, pitch, roll) = definition.BucketId switch
            {
                PersonalFacePoseBuckets.YawNegative => (-18d, 1d, 0d),
                PersonalFacePoseBuckets.YawPositive => (18d, 1d, 0d),
                PersonalFacePoseBuckets.PitchNegative => (1d, -11d, 0d),
                PersonalFacePoseBuckets.PitchPositive => (1d, 11d, 0d),
                PersonalFacePoseBuckets.RollNegative => (0d, 0d, -11d),
                PersonalFacePoseBuckets.RollPositive => (0d, 0d, 11d),
                _ => (0d, 0d, 0d)
            };

            return new PersonalFacePoseBucketProfile
            {
                BucketId = definition.BucketId,
                Label = definition.Label,
                Description = definition.Description,
                CaptureInstruction = definition.CaptureInstruction,
                PrimaryNeutralReference = definition.PrimaryNeutralReference,
                RequiredForAvatarCoverage = definition.RequiredForAvatarCoverage,
                SampleCount = sampleCount,
                TotalWeight = totalWeight,
                HeadYawDegrees = Distribution(yaw, sampleCount, totalWeight, spread: 2d),
                HeadPitchDegrees = Distribution(pitch, sampleCount, totalWeight, spread: 1.5d),
                HeadRollDegrees = Distribution(roll, sampleCount, totalWeight, spread: 1.5d),
                FaceAspectRatio = Distribution(1.45d, sampleCount, totalWeight, spread: 0.02d),
                InterEyeDistanceToFaceWidth = Distribution(0.38d, sampleCount, totalWeight, spread: 0.018d),
                MouthWidthToFaceWidth = Distribution(0.34d, sampleCount, totalWeight, spread: 0.02d),
                EyeMidlineYToFaceHeight = Distribution(0.32d, sampleCount, totalWeight, spread: 0.018d),
                MouthCenterYToFaceHeight = Distribution(0.66d, sampleCount, totalWeight, spread: 0.02d),
                AverageEyeOpeningRatio = Distribution(0.26d, sampleCount, totalWeight, spread: 0.04d),
                MouthOpeningRatio = Distribution(0.08d, sampleCount, totalWeight, spread: 0.05d),
                JawDroopRatio = Distribution(0.025d, sampleCount, totalWeight, spread: 0.025d),
                AverageFaceReliabilityPercent = 91d,
                AverageEyeReliabilityPercent = 87d,
                AverageMouthReliabilityPercent = 85d
            };
        })
        .ToList();
}

static PersonalFaceContourShapeProfile ShapeProfile(
    string featureId,
    string label,
    int pointCount,
    bool closed,
    int sampleCount = 96,
    double totalWeight = 82d)
{
    var points = new List<PersonalFaceContourShapePointProfile>(pointCount);
    for (var index = 0; index < pointCount; index++)
    {
        var t = pointCount <= 1 ? 0d : index / (double)(closed ? pointCount : pointCount - 1);
        var angle = t * Math.PI * 2d;
        var centerX = featureId.Contains("left", StringComparison.OrdinalIgnoreCase)
            ? 0.32d
            : featureId.Contains("right", StringComparison.OrdinalIgnoreCase) ? 0.68d : 0.50d;
        var centerY = featureId.Contains("eye", StringComparison.OrdinalIgnoreCase)
            ? 0.34d
            : featureId.Contains("jaw", StringComparison.OrdinalIgnoreCase) ? 0.72d : 0.64d;
        var halfWidth = featureId.Contains("lip", StringComparison.OrdinalIgnoreCase)
            ? 0.16d
            : featureId.Contains("jaw", StringComparison.OrdinalIgnoreCase) ? 0.28d : 0.08d;
        var halfHeight = featureId.Contains("jaw", StringComparison.OrdinalIgnoreCase)
            ? 0.10d
            : featureId.Contains("lip", StringComparison.OrdinalIgnoreCase) ? 0.035d : 0.018d;
        var x = closed
            ? centerX + Math.Cos(angle) * halfWidth
            : centerX - halfWidth + halfWidth * 2d * t;
        var y = closed
            ? centerY + Math.Sin(angle) * halfHeight
            : centerY + Math.Sin(t * Math.PI) * halfHeight;
        var depth = featureId.Contains("jaw", StringComparison.OrdinalIgnoreCase)
            ? 0.075d
            : featureId.Contains("lip", StringComparison.OrdinalIgnoreCase) ? 0.055d : 0.025d;
        var depthSwing = featureId.Contains("jaw", StringComparison.OrdinalIgnoreCase)
            ? 0.85d
            : featureId.Contains("lip", StringComparison.OrdinalIgnoreCase) ? 0.45d : 0.35d;
        var z = closed
            ? depth + Math.Sin(angle) * depth * depthSwing
            : depth + Math.Sin(t * Math.PI) * depth * depthSwing;
        points.Add(new PersonalFaceContourShapePointProfile
        {
            Index = index,
            X = Distribution(x, sampleCount, totalWeight, spread: 0.004d),
            Y = Distribution(y, sampleCount, totalWeight, spread: 0.004d),
            Z = Distribution(z, sampleCount, totalWeight, spread: 0.003d)
        });
    }

    var zValues = points
        .Select(static point => point.Z.Average)
        .OfType<double>()
        .ToList();
    return new PersonalFaceContourShapeProfile
    {
        FeatureId = featureId,
        Label = label,
        Closed = closed,
        PointCount = pointCount,
        SampleCount = sampleCount,
        TotalWeight = totalWeight,
        DepthPointCount = pointCount,
        PointCoveragePercent = 100d,
        DepthPointCoveragePercent = 100d,
        DepthEvidencePercent = 88d,
        DepthStabilityPercent = 82d,
        DepthRange = zValues.Count > 0 ? zValues.Max() - zValues.Min() : null,
        AverageDepthStandardDeviation = 0.003d,
        Points = points
    };
}

static PersonalFaceContourShapeProfile SurfaceProfile(
    string featureId,
    string label,
    int pointCount,
    int sampleCount = 96,
    double totalWeight = 82d,
    double depth = 0.03d)
{
    var profile = ShapeProfile(featureId, label, pointCount, closed: false, sampleCount: sampleCount, totalWeight: totalWeight);
    for (var index = 0; index < profile.Points.Count; index++)
    {
        var t = profile.Points.Count <= 1 ? 0d : index / (double)(profile.Points.Count - 1);
        profile.Points[index].Z = Distribution(depth + Math.Sin(t * Math.PI) * depth * 0.18d, sampleCount, totalWeight, spread: 0.003d);
    }

    var zValues = profile.Points
        .Select(static point => point.Z.Average)
        .OfType<double>()
        .ToList();
    profile.DepthPointCount = profile.PointCount;
    profile.PointCoveragePercent = 100d;
    profile.DepthPointCoveragePercent = 100d;
    profile.DepthEvidencePercent = 92d;
    profile.DepthStabilityPercent = 86d;
    profile.DepthRange = zValues.Count > 0 ? zValues.Max() - zValues.Min() : null;
    profile.AverageDepthStandardDeviation = 0.003d;

    return profile;
}

static PersonalFaceContourShapeProfile ShiftShapeProfile(
    PersonalFaceContourShapeProfile profile,
    double xShift = 0d,
    double yShift = 0d)
{
    foreach (var point in profile.Points)
    {
        ShiftDistribution(point.X, xShift);
        ShiftDistribution(point.Y, yShift);
    }

    return profile;
}

static void ShiftDistribution(PersonalMetricDistribution distribution, double delta)
{
    if (delta == 0d)
    {
        return;
    }

    distribution.Average += delta;
    distribution.Minimum += delta;
    distribution.Maximum += delta;
    distribution.ExponentialMovingAverage += delta;
    distribution.NormalLow += delta;
    distribution.NormalHigh += delta;
}

static double AveragePreviewPointX(MeasurementFacePreviewModel preview, string idPrefix)
{
    var points = preview.Points
        .Where(point => point.Id.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase))
        .ToList();
    Require(points.Count > 0, $"measurement face preview did not include points starting with '{idPrefix}'");
    return points.Average(static point => point.X);
}

static double CountRate(int count, int total)
{
    return total <= 0 ? 0d : count / (double)total;
}

static WpfRect? GetContourBounds(IReadOnlyList<WpfPoint> contour)
{
    if (contour.Count == 0)
    {
        return null;
    }

    var left = contour.Min(static point => point.X);
    var right = contour.Max(static point => point.X);
    var top = contour.Min(static point => point.Y);
    var bottom = contour.Max(static point => point.Y);
    return right > left && bottom > top
        ? new WpfRect(left, top, right - left, bottom - top)
        : null;
}

static double RectCenterDistance(WpfRect first, WpfRect second)
{
    var dx = first.Left + first.Width / 2d - (second.Left + second.Width / 2d);
    var dy = first.Top + first.Height / 2d - (second.Top + second.Height / 2d);
    return Math.Sqrt(dx * dx + dy * dy);
}

static double DistanceFromCenterY(CvRect rect, double targetY)
{
    if (rect.Width <= 0 || rect.Height <= 0)
    {
        return double.PositiveInfinity;
    }

    return Math.Abs(rect.Y + rect.Height / 2d - targetY);
}

static Mat CreateApertureImage(int width, int height, int centerY, int halfWidth, int halfHeight)
{
    var image = new Mat(height, width, MatType.CV_8UC1, new Scalar(210));
    Cv2.Ellipse(
        image,
        new CvPoint(width / 2, centerY),
        new CvSize(halfWidth, halfHeight),
        0,
        0,
        360,
        Scalar.Black,
        -1);
    return image;
}

static Mat CreateMouthWithUnderNoseDecoy(
    out CvRect face,
    out CvRect underNoseSeed,
    out double underNoseY,
    out double realMouthY)
{
    var image = new Mat(180, 220, MatType.CV_8UC1, new Scalar(206));
    face = new CvRect(30, 18, 160, 142);
    underNoseY = face.Y + face.Height * 0.52d;
    realMouthY = face.Y + face.Height * 0.70d;

    Cv2.Ellipse(image, new CvPoint(face.X + face.Width / 2, face.Y + face.Height / 2), new CvSize(face.Width / 2, face.Height / 2), 0, 0, 360, new Scalar(222), -1);
    Cv2.Ellipse(image, new CvPoint(face.X + face.Width / 2, face.Y + (int)Math.Round(face.Height * 0.40d)), new CvSize(11, 18), 0, 0, 360, new Scalar(172), -1);
    Cv2.Ellipse(image, new CvPoint(face.X + face.Width / 2, (int)Math.Round(underNoseY)), new CvSize(46, 5), 0, 0, 360, new Scalar(26), -1);
    Cv2.Ellipse(image, new CvPoint(face.X + face.Width / 2, (int)Math.Round(realMouthY)), new CvSize(52, 15), 0, 0, 360, new Scalar(10), -1);

    underNoseSeed = new CvRect(
        face.X + (int)Math.Round(face.Width * 0.25d),
        (int)Math.Round(underNoseY - face.Height * 0.12d),
        (int)Math.Round(face.Width * 0.50d),
        (int)Math.Round(face.Height * 0.22d));
    return image;
}

static void AddGlassesOcclusion(Mat image)
{
    var centerY = image.Rows / 2;
    Cv2.Line(image, new CvPoint(12, centerY - 20), new CvPoint(image.Cols - 12, centerY - 20), new Scalar(70), 3);
    Cv2.Line(image, new CvPoint(12, centerY + 20), new CvPoint(image.Cols - 12, centerY + 20), new Scalar(70), 3);
    Cv2.Line(image, new CvPoint(image.Cols / 2 - 6, centerY - 28), new CvPoint(image.Cols / 2 + 6, centerY + 28), new Scalar(70), 4);
    Cv2.Line(image, new CvPoint(image.Cols / 2 - 44, centerY - 16), new CvPoint(image.Cols / 2 + 30, centerY + 14), new Scalar(250), 5);
}

static Mat CreateBottomRightEyeInsetFrame(bool sleepy)
{
    var frame = new Mat(360, 640, MatType.CV_8UC1, new Scalar(238));
    var inset = EyeInsetApertureAnalyzer.BottomRightDefaultRegion.ToPixelRect(frame.Width, frame.Height);
    Cv2.Rectangle(frame, inset, new Scalar(252), -1);
    Cv2.Rectangle(frame, inset, new Scalar(120), 2);

    var eyeTop = inset.Y + Math.Max(0, (int)Math.Round(inset.Height * 0.10d));
    var eyeHeight = Math.Max(8, (int)Math.Round(inset.Height * 0.78d));
    var horizontalPadding = Math.Max(2, (int)Math.Round(inset.Width * 0.035d));
    var centerGap = Math.Max(2, (int)Math.Round(inset.Width * 0.025d));
    var halfRegionWidth = Math.Max(10, (inset.Width - horizontalPadding * 2 - centerGap) / 2);
    var leftEyeBox = new CvRect(inset.X + horizontalPadding, eyeTop, halfRegionWidth, eyeHeight);
    var rightEyeBox = new CvRect(inset.X + horizontalPadding + halfRegionWidth + centerGap, eyeTop, halfRegionWidth, eyeHeight);
    DrawInsetEye(frame, leftEyeBox, sleepy);
    DrawInsetEye(frame, rightEyeBox, sleepy);

    var bridgeX = inset.X + inset.Width / 2;
    Cv2.Line(frame, new CvPoint(bridgeX, eyeTop + 6), new CvPoint(bridgeX, eyeTop + eyeHeight - 6), new Scalar(75), 3);
    Cv2.Line(frame, new CvPoint(leftEyeBox.X + 12, leftEyeBox.Y + 8), new CvPoint(rightEyeBox.Right - 12, rightEyeBox.Y + 8), new Scalar(82), 3);
    return frame;
}

static void DrawInsetEye(Mat frame, CvRect eyeBox, bool sleepy)
{
    var center = new CvPoint(eyeBox.X + eyeBox.Width / 2, eyeBox.Y + eyeBox.Height / 2);
    var halfHeight = sleepy ? Math.Max(3, eyeBox.Height / 18) : Math.Max(6, eyeBox.Height / 8);
    var halfWidth = Math.Max(12, (int)Math.Round(eyeBox.Width * 0.35d));
    Cv2.Ellipse(
        frame,
        center,
        new CvSize(halfWidth, halfHeight),
        0,
        0,
        360,
        Scalar.Black,
        -1);
    Cv2.Rectangle(frame, eyeBox, new Scalar(88), 2);
    Cv2.Line(frame, new CvPoint(eyeBox.X + 4, eyeBox.Y + eyeBox.Height - 10), new CvPoint(eyeBox.Right - 4, eyeBox.Y + 12), new Scalar(230), 4);
}

static IReadOnlyList<WpfPoint> Normalize(IReadOnlyList<WpfPoint> points, int width, int height)
{
    return points
        .Select(point => new WpfPoint(point.X / width, point.Y / height))
        .ToList();
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static BitmapSource CreateTinyBitmap()
{
    return CreateBlankBitmap(320, 240);
}

static BitmapSource CreateBlankBitmap(int width, int height)
{
    var pixels = new byte[width * height * 4];
    for (var i = 0; i < pixels.Length; i += 4)
    {
        pixels[i] = 255;
        pixels[i + 1] = 255;
        pixels[i + 2] = 255;
        pixels[i + 3] = 255;
    }

    var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    bitmap.Freeze();
    return bitmap;
}

internal sealed class FakeStatefulTracker : IStatefulFaceLandmarkTracker
{
    public int ResetCalls { get; private set; }

    public string Name => "fake stateful tracker";

    public bool IsAvailable => true;

    public int MaxDetectionDimension { get; set; } = 960;

    public FaceLandmarkTrackingResult Detect(BitmapSource bitmap, DateTime capturedAtUtc)
    {
        return FaceLandmarkTrackingResult.None;
    }

    public void Reset()
    {
        ResetCalls++;
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeCropRefiner : IFaceLandmarkTracker, IFaceLandmarkCropRefiner
{
    private readonly FaceLandmarkFrame _cropFrame;

    public FakeCropRefiner(FaceLandmarkFrame cropFrame)
    {
        _cropFrame = cropFrame;
    }

    public int CropCalls { get; private set; }

    public string Name => "MediaPipe Face Landmarker sidecar";

    public bool IsAvailable => true;

    public int MaxDetectionDimension { get; set; } = 960;

    public FaceLandmarkTrackingResult Detect(BitmapSource bitmap, DateTime capturedAtUtc)
    {
        return new FaceLandmarkTrackingResult
        {
            BackendName = Name,
            BackendStatus = "MediaPipe sidecar searching"
        };
    }

    public FaceLandmarkTrackingResult DetectFaceCrop(
        BitmapSource bitmap,
        WpfRect normalizedFaceHint,
        DateTime capturedAtUtc)
    {
        CropCalls++;
        var cropResult = new FaceLandmarkTrackingResult
        {
            BackendName = Name,
            BackendStatus = "MediaPipe dense landmark lock from crop",
            LandmarkFrame = _cropFrame,
            FeatureDetection = new FaceFeatureDetection
            {
                HasFace = _cropFrame.HasFace,
                Source = _cropFrame.Source,
                FaceBox = Bounds(_cropFrame.FaceContour) ?? new WpfRect(0.25d, 0.18d, 0.50d, 0.64d),
                TrackingConfidence = _cropFrame.TrackingConfidence,
                EyeConfidence = _cropFrame.EyeConfidence,
                MouthConfidence = _cropFrame.MouthConfidence,
                FaceContour = _cropFrame.FaceContour,
                LeftEyeContour = _cropFrame.LeftEyeContour,
                RightEyeContour = _cropFrame.RightEyeContour,
                OuterLipContour = _cropFrame.OuterLipContour,
                InnerLipContour = _cropFrame.InnerLipContour,
                JawContour = _cropFrame.JawContour
            }
        };

        return FaceLandmarkCropMapper.MapToFrame(cropResult, normalizedFaceHint, "fake crop refinement");
    }

    public void Dispose()
    {
    }

    private static WpfRect? Bounds(IReadOnlyList<WpfPoint> points)
    {
        if (points.Count == 0)
        {
            return null;
        }

        var minX = points.Min(static point => point.X);
        var maxX = points.Max(static point => point.X);
        var minY = points.Min(static point => point.Y);
        var maxY = points.Max(static point => point.Y);
        return maxX <= minX || maxY <= minY
            ? null
            : new WpfRect(minX, minY, maxX - minX, maxY - minY);
    }
}

internal sealed class FakeResultTracker : IFaceLandmarkTracker
{
    private readonly FaceLandmarkTrackingResult _result;

    public FakeResultTracker(string name, string status, FaceLandmarkFrame frame)
    {
        Name = name;
        _result = new FaceLandmarkTrackingResult
        {
            BackendName = name,
            BackendStatus = status,
            LandmarkFrame = frame,
            FeatureDetection = new FaceFeatureDetection
            {
                HasFace = frame.HasFace,
                Source = frame.Source,
                FaceBox = Bounds(frame.FaceContour) ?? new WpfRect(0.30d, 0.22d, 0.40d, 0.56d),
                TrackingConfidence = frame.TrackingConfidence,
                EyeConfidence = frame.EyeConfidence,
                MouthConfidence = frame.MouthConfidence,
                FaceContour = frame.FaceContour,
                LeftEyeContour = frame.LeftEyeContour,
                RightEyeContour = frame.RightEyeContour,
                OuterLipContour = frame.OuterLipContour,
                InnerLipContour = frame.InnerLipContour,
                JawContour = frame.JawContour
            }
        };
    }

    public string Name { get; }

    public bool IsAvailable => true;

    public int MaxDetectionDimension { get; set; } = 960;

    public FaceLandmarkTrackingResult Detect(BitmapSource bitmap, DateTime capturedAtUtc)
    {
        return _result;
    }

    public void Dispose()
    {
    }

    private static WpfRect? Bounds(IReadOnlyList<WpfPoint> points)
    {
        if (points.Count == 0)
        {
            return null;
        }

        var minX = points.Min(static point => point.X);
        var maxX = points.Max(static point => point.X);
        var minY = points.Min(static point => point.Y);
        var maxY = points.Max(static point => point.Y);
        return maxX <= minX || maxY <= minY
            ? null
            : new WpfRect(minX, minY, maxX - minX, maxY - minY);
    }
}

internal sealed class FakeSequenceTracker : IFaceLandmarkTracker
{
    private readonly IReadOnlyList<FaceLandmarkTrackingResult> _results;
    private int _index;

    public FakeSequenceTracker(IReadOnlyList<FaceLandmarkTrackingResult> results)
    {
        _results = results;
    }

    public string Name => "fake sequence tracker";

    public bool IsAvailable => true;

    public int MaxDetectionDimension { get; set; } = 960;

    public FaceLandmarkTrackingResult Detect(BitmapSource bitmap, DateTime capturedAtUtc)
    {
        if (_results.Count == 0)
        {
            return FaceLandmarkTrackingResult.None;
        }

        var result = _results[Math.Min(_index, _results.Count - 1)];
        _index++;
        return result;
    }

    public void Dispose()
    {
    }
}

internal sealed record SyntheticLandmarkStressRecord(
    int FrameIndex,
    double Progress,
    double TimestampSeconds,
    double? EyeOpening,
    double? MouthOpening,
    double? JawDroop,
    double? EyeClosureCue,
    double? MouthOpeningCue,
    double? JawDroopCue,
    double? CompositeCue,
    double? EyeTrendSlope,
    double? MouthTrendSlope,
    bool LeftEyeReconstructed,
    bool RightEyeReconstructed,
    bool MouthReconstructed,
    bool EyeArtifactSuppressed,
    bool BaselineReady,
    bool EyeCueEligible,
    bool MouthCueEligible,
    double OverallQuality,
    double? FaceLeft,
    double? FaceTop,
    double? FaceRight,
    double? FaceBottom,
    bool NearFrameEdge,
    string CaptureQualityLabel,
    double CaptureQualityScore,
    bool CaptureQualityCanCollect,
    bool CaptureQualityAvatarGrade,
    double CaptureQualityCameraModeScore,
    double CaptureQualityFaceScaleScore,
    double CaptureQualityEyeScore,
    double CaptureQualityMouthScore,
    double CaptureQualityStabilityScore,
    double CaptureQualityGlassesScore,
    string CaptureQualityIssues,
    bool PersonalModelAccepted,
    string PersonalModelRejectionKind,
    string PersonalModelUpdateReason);

internal sealed class MeasurementFacePreviewSceneContract
{
    public List<MeasurementFacePreviewPoint> Points { get; set; } = [];

    public List<MeasurementFacePreviewPolyline> Polylines { get; set; } = [];

    public List<MeasurementFacePreviewSurfacePatch> SurfacePatches { get; set; } = [];

    public List<MeasurementFacePreviewPoseBucket> PoseBuckets { get; set; } = [];

    public List<MeasurementFacePreviewSurfaceEvidence> SurfaceEvidence { get; set; } = [];

    public double MeasurementContributionPercent { get; set; }

    public double TemplatePriorContributionPercent { get; set; }

    public string GeometryProvenance { get; set; } = "";
}

internal sealed record MeasurementPreviewProjectedPoint(double X, double Y, double Z);

internal readonly record struct SyntheticMeshPoint(double X, double Y, double Z);
