using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Personalization;

namespace AvatarBuilder.Modules.Episodes;

public sealed class LandmarkEventTimeline
{
    private readonly List<LandmarkEventTimelineSample> _samples = [];

    public int Count => _samples.Count;

    public IReadOnlyList<LandmarkEventTimelineSample> Samples => _samples;

    public void Reset()
    {
        _samples.Clear();
    }

    public void Add(
        DateTime eventStartedAt,
        double? motionPercent,
        FaceLandmarkMetrics metrics,
        FaceLandmarkCueAnalysis? cueAnalysis,
        FaceLandmarkTrendAnalysis trendAnalysis,
        string backendStatus)
    {
        Add(eventStartedAt, motionPercent, metrics, cueAnalysis, trendAnalysis, FaceLockStabilityAnalysis.Waiting, backendStatus, null);
    }

    public void Add(
        DateTime eventStartedAt,
        double? motionPercent,
        FaceLandmarkMetrics metrics,
        FaceLandmarkCueAnalysis? cueAnalysis,
        FaceLandmarkTrendAnalysis trendAnalysis,
        FaceLockStabilityAnalysis stabilityAnalysis,
        string backendStatus)
    {
        Add(eventStartedAt, motionPercent, metrics, cueAnalysis, trendAnalysis, stabilityAnalysis, backendStatus, null);
    }

    public void Add(
        DateTime eventStartedAt,
        double? motionPercent,
        FaceLandmarkMetrics metrics,
        FaceLandmarkCueAnalysis? cueAnalysis,
        FaceLandmarkTrendAnalysis trendAnalysis,
        FaceLockStabilityAnalysis stabilityAnalysis,
        string backendStatus,
        AvatarCaptureQualityAssessment? captureQuality)
    {
        var capturedAtUtc = metrics.CapturedAtUtc == default
            ? DateTime.UtcNow
            : metrics.CapturedAtUtc.ToUniversalTime();
        var elapsedSeconds = Math.Max(0d, (capturedAtUtc - eventStartedAt.ToUniversalTime()).TotalSeconds);
        _samples.Add(new LandmarkEventTimelineSample
        {
            CapturedAtUtc = capturedAtUtc,
            ElapsedSeconds = elapsedSeconds,
            MotionPercent = motionPercent,
            BackendStatus = backendStatus,
            Source = metrics.Source,
            ConfidenceLabel = metrics.ConfidenceLabel,
            TrackingConfidence = metrics.TrackingConfidence,
            EyeConfidence = metrics.EyeConfidence,
            MouthConfidence = metrics.MouthConfidence,
            EyeQualityPercent = metrics.EyeMeasurementQualityPercent,
            MouthQualityPercent = metrics.MouthMeasurementQualityPercent,
            OverallQualityPercent = metrics.OverallMeasurementQualityPercent,
            CaptureQualityLabel = captureQuality?.Label ?? "",
            CaptureQualityScore = captureQuality?.ScorePercent,
            CaptureQualityCanCollect = captureQuality?.CanCollectMeasurements,
            CaptureQualityAvatarGrade = captureQuality?.StrongEnoughForAvatarLearning,
            CaptureQualityReason = captureQuality?.PrimaryReason ?? "",
            CaptureQualityCameraModeScore = captureQuality?.CameraModeScorePercent,
            CaptureQualityFaceScaleScore = captureQuality?.FaceScaleScorePercent,
            CaptureQualityEyeScore = captureQuality?.EyeEvidenceScorePercent,
            CaptureQualityMouthScore = captureQuality?.MouthEvidenceScorePercent,
            CaptureQualityStabilityScore = captureQuality?.StabilityScorePercent,
            CaptureQualityGlassesScore = captureQuality?.GlassesRiskScorePercent,
            CaptureQualityStorageScore = captureQuality?.StorageScorePercent,
            CaptureQualityFaceWidthPercent = captureQuality?.FaceWidthPercent,
            CaptureQualityFaceHeightPercent = captureQuality?.FaceHeightPercent,
            CaptureQualityIssues = Join(captureQuality?.Issues),
            CaptureQualitySuggestions = Join(captureQuality?.Suggestions),
            FaceReliabilityStatus = stabilityAnalysis.Status,
            FaceReliabilitySamples = stabilityAnalysis.SampleCount,
            FaceReliability = stabilityAnalysis.CompositeReliabilityPercent,
            FaceContinuity = stabilityAnalysis.FaceContinuityPercent,
            EyeReliability = stabilityAnalysis.EyeReliabilityPercent,
            MouthReliability = stabilityAnalysis.MouthReliabilityPercent,
            FaceBoundsRate = stabilityAnalysis.FaceBoundsRatePercent,
            EyeUsableRate = stabilityAnalysis.EyeUsableRatePercent,
            MouthUsableRate = stabilityAnalysis.MouthUsableRatePercent,
            EyeUsable = metrics.IsEyeMeasurementUsable,
            MouthUsable = metrics.IsMouthMeasurementUsable,
            RawLeftEyeOpening = metrics.RawLeftEyeOpeningRatio,
            RawRightEyeOpening = metrics.RawRightEyeOpeningRatio,
            RawAverageEyeOpening = metrics.RawAverageEyeOpeningRatio,
            RawMouthOpening = metrics.RawMouthOpeningRatio,
            RawJawDroop = metrics.RawJawDroopRatio,
            LeftEyeOpening = metrics.LeftEyeOpeningRatio,
            RightEyeOpening = metrics.RightEyeOpeningRatio,
            AverageEyeOpening = metrics.AverageEyeOpeningRatio,
            MouthOpening = metrics.MouthOpeningRatio,
            MouthOpeningVelocity = metrics.MouthOpeningVelocityPerSecond,
            JawDroop = metrics.JawDroopRatio,
            JawDroopVelocity = metrics.JawDroopVelocityPerSecond,
            MediaPipeLeftEyeBlinkPercent = metrics.MediaPipeLeftEyeBlinkPercent,
            MediaPipeRightEyeBlinkPercent = metrics.MediaPipeRightEyeBlinkPercent,
            MediaPipeAverageEyeBlinkPercent = metrics.MediaPipeAverageEyeBlinkPercent,
            MediaPipeJawOpenPercent = metrics.MediaPipeJawOpenPercent,
            MediaPipeMouthClosePercent = metrics.MediaPipeMouthClosePercent,
            MediaPipeEyeOpeningCorrection = metrics.MediaPipeEyeOpeningCorrectionRatio,
            MediaPipeMouthOpeningCorrection = metrics.MediaPipeMouthOpeningCorrectionRatio,
            MediaPipeEyeOpeningCorrected = metrics.MediaPipeEyeOpeningCorrected,
            MediaPipeMouthOpeningCorrected = metrics.MediaPipeMouthOpeningCorrected,
            RawEyeAsymmetryPercent = metrics.RawEyeAsymmetryPercent,
            EyeAsymmetryPercent = metrics.EyeAsymmetryPercent,
            EyeAgreementPercent = metrics.EyeAgreementPercent,
            PossibleOneEyeArtifact = metrics.PossibleOneEyeArtifact,
            LeftEyeReconstructed = metrics.LeftEyeReconstructed,
            RightEyeReconstructed = metrics.RightEyeReconstructed,
            MouthReconstructed = metrics.MouthReconstructed,
            EyeArtifactSuppressed = metrics.EyeArtifactSuppressed,
            EyeImageQualityAvailable = metrics.EyeImageQualityAvailable,
            MouthImageQualityAvailable = metrics.MouthImageQualityAvailable,
            EyeGlarePercent = metrics.EyeGlarePercent,
            MouthGlarePercent = metrics.MouthGlarePercent,
            EyeContrastPercent = metrics.EyeContrastPercent,
            MouthContrastPercent = metrics.MouthContrastPercent,
            EyeSharpnessPercent = metrics.EyeSharpnessPercent,
            MouthSharpnessPercent = metrics.MouthSharpnessPercent,
            EyeDarkCoveragePercent = metrics.EyeDarkCoveragePercent,
            MouthDarkCoveragePercent = metrics.MouthDarkCoveragePercent,
            CueStatus = cueAnalysis?.Status ?? "",
            CueScore = cueAnalysis?.CompositeCuePercent,
            EyeCueEligible = cueAnalysis?.EyeCueEligible,
            MouthCueEligible = cueAnalysis?.MouthCueEligible,
            EyeClosurePercent = cueAnalysis?.EyeClosurePercent,
            MouthOpeningChangePercent = cueAnalysis?.MouthOpeningChangePercent,
            JawDroopChangePercent = cueAnalysis?.JawDroopChangePercent,
            CueMediaPipeBlinkBaselineReady = cueAnalysis?.MediaPipeBlinkBaselineReady,
            CueMediaPipeMouthBaselineReady = cueAnalysis?.MediaPipeMouthBaselineReady,
            CueMediaPipeBlinkBaselinePercent = cueAnalysis?.MediaPipeBlinkBaselinePercent,
            CueMediaPipeJawOpenBaselinePercent = cueAnalysis?.MediaPipeJawOpenBaselinePercent,
            CueMediaPipeMouthCloseBaselinePercent = cueAnalysis?.MediaPipeMouthCloseBaselinePercent,
            CueMediaPipeBlinkChangePercent = cueAnalysis?.MediaPipeBlinkChangePercent,
            CueMediaPipeJawOpenChangePercent = cueAnalysis?.MediaPipeJawOpenChangePercent,
            CueMediaPipeMouthCloseDropPercent = cueAnalysis?.MediaPipeMouthCloseDropPercent,
            CueMediaPipeMouthOpeningEvidencePercent = cueAnalysis?.MediaPipeMouthOpeningEvidencePercent,
            TrendStatus = trendAnalysis.Status,
            TrendScore = trendAnalysis.TrendCuePercent,
            TrendWindowSeconds = trendAnalysis.WindowSeconds,
            EyeClosingTrendPercent = trendAnalysis.EyeClosingTrendPercent,
            MouthOpeningTrendPercent = trendAnalysis.MouthOpeningTrendPercent,
            EyeOpeningSlopePerSecond = trendAnalysis.EyeOpeningSlopePerSecond,
            MouthOpeningSlopePerSecond = trendAnalysis.MouthOpeningSlopePerSecond
        });
    }

    public (string JsonPath, string CsvPath) Write(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || _samples.Count == 0)
        {
            return ("", "");
        }

        Directory.CreateDirectory(folder);
        var jsonPath = Path.Combine(folder, "landmark_timeline.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(_samples, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

        var csvPath = Path.Combine(folder, "landmark_timeline.csv");
        var builder = new StringBuilder();
        builder.AppendLine("CapturedAtUtc,ElapsedSeconds,MotionPercent,BackendStatus,Source,ConfidenceLabel,TrackingConfidence,EyeConfidence,MouthConfidence,EyeQuality,MouthQuality,OverallQuality,CaptureQualityLabel,CaptureQualityScore,CaptureQualityCanCollect,CaptureQualityAvatarGrade,CaptureQualityReason,CaptureQualityCameraModeScore,CaptureQualityFaceScaleScore,CaptureQualityEyeScore,CaptureQualityMouthScore,CaptureQualityStabilityScore,CaptureQualityGlassesScore,CaptureQualityStorageScore,CaptureQualityFaceWidth,CaptureQualityFaceHeight,CaptureQualityIssues,CaptureQualitySuggestions,FaceReliabilityStatus,FaceReliabilitySamples,FaceReliability,FaceContinuity,EyeReliability,MouthReliability,FaceBoundsRate,EyeUsableRate,MouthUsableRate,EyeUsable,MouthUsable,RawLeftEyeOpening,RawRightEyeOpening,RawAverageEyeOpening,RawMouthOpening,RawJawDroop,LeftEyeOpening,RightEyeOpening,AverageEyeOpening,MouthOpening,MouthOpeningVelocity,JawDroop,JawDroopVelocity,MediaPipeLeftEyeBlink,MediaPipeRightEyeBlink,MediaPipeAverageEyeBlink,MediaPipeJawOpen,MediaPipeMouthClose,MediaPipeEyeOpeningCorrection,MediaPipeMouthOpeningCorrection,MediaPipeEyeOpeningCorrected,MediaPipeMouthOpeningCorrected,RawEyeAsymmetry,EyeAsymmetry,EyeAgreement,PossibleOneEyeArtifact,LeftEyeReconstructed,RightEyeReconstructed,MouthReconstructed,EyeArtifactSuppressed,EyeImageQualityAvailable,MouthImageQualityAvailable,EyeGlare,MouthGlare,EyeContrast,MouthContrast,EyeSharpness,MouthSharpness,EyeDarkCoverage,MouthDarkCoverage,CueStatus,CueScore,EyeCueEligible,MouthCueEligible,EyeClosure,MouthOpeningChange,JawDroopChange,CueMediaPipeBlinkBaselineReady,CueMediaPipeMouthBaselineReady,CueMediaPipeBlinkBaseline,CueMediaPipeJawOpenBaseline,CueMediaPipeMouthCloseBaseline,CueMediaPipeBlinkChange,CueMediaPipeJawOpenChange,CueMediaPipeMouthCloseDrop,CueMediaPipeMouthOpeningEvidence,TrendStatus,TrendScore,TrendWindowSeconds,EyeClosingTrend,MouthOpeningTrend,EyeOpeningSlope,MouthOpeningSlope");
        foreach (var sample in _samples)
        {
            builder.AppendLine(string.Join(",", [
                Csv(sample.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                Csv(Format(sample.ElapsedSeconds)),
                Csv(Format(sample.MotionPercent)),
                Csv(sample.BackendStatus),
                Csv(sample.Source),
                Csv(sample.ConfidenceLabel),
                Csv(Format(sample.TrackingConfidence)),
                Csv(Format(sample.EyeConfidence)),
                Csv(Format(sample.MouthConfidence)),
                Csv(Format(sample.EyeQualityPercent)),
                Csv(Format(sample.MouthQualityPercent)),
                Csv(Format(sample.OverallQualityPercent)),
                Csv(sample.CaptureQualityLabel),
                Csv(Format(sample.CaptureQualityScore)),
                Csv(Format(sample.CaptureQualityCanCollect)),
                Csv(Format(sample.CaptureQualityAvatarGrade)),
                Csv(sample.CaptureQualityReason),
                Csv(Format(sample.CaptureQualityCameraModeScore)),
                Csv(Format(sample.CaptureQualityFaceScaleScore)),
                Csv(Format(sample.CaptureQualityEyeScore)),
                Csv(Format(sample.CaptureQualityMouthScore)),
                Csv(Format(sample.CaptureQualityStabilityScore)),
                Csv(Format(sample.CaptureQualityGlassesScore)),
                Csv(Format(sample.CaptureQualityStorageScore)),
                Csv(Format(sample.CaptureQualityFaceWidthPercent)),
                Csv(Format(sample.CaptureQualityFaceHeightPercent)),
                Csv(sample.CaptureQualityIssues),
                Csv(sample.CaptureQualitySuggestions),
                Csv(sample.FaceReliabilityStatus),
                Csv(sample.FaceReliabilitySamples.ToString(CultureInfo.InvariantCulture)),
                Csv(Format(sample.FaceReliability)),
                Csv(Format(sample.FaceContinuity)),
                Csv(Format(sample.EyeReliability)),
                Csv(Format(sample.MouthReliability)),
                Csv(Format(sample.FaceBoundsRate)),
                Csv(Format(sample.EyeUsableRate)),
                Csv(Format(sample.MouthUsableRate)),
                Csv(sample.EyeUsable.ToString(CultureInfo.InvariantCulture)),
                Csv(sample.MouthUsable.ToString(CultureInfo.InvariantCulture)),
                Csv(Format(sample.RawLeftEyeOpening)),
                Csv(Format(sample.RawRightEyeOpening)),
                Csv(Format(sample.RawAverageEyeOpening)),
                Csv(Format(sample.RawMouthOpening)),
                Csv(Format(sample.RawJawDroop)),
                Csv(Format(sample.LeftEyeOpening)),
                Csv(Format(sample.RightEyeOpening)),
                Csv(Format(sample.AverageEyeOpening)),
                Csv(Format(sample.MouthOpening)),
                Csv(Format(sample.MouthOpeningVelocity)),
                Csv(Format(sample.JawDroop)),
                Csv(Format(sample.JawDroopVelocity)),
                Csv(Format(sample.MediaPipeLeftEyeBlinkPercent)),
                Csv(Format(sample.MediaPipeRightEyeBlinkPercent)),
                Csv(Format(sample.MediaPipeAverageEyeBlinkPercent)),
                Csv(Format(sample.MediaPipeJawOpenPercent)),
                Csv(Format(sample.MediaPipeMouthClosePercent)),
                Csv(Format(sample.MediaPipeEyeOpeningCorrection)),
                Csv(Format(sample.MediaPipeMouthOpeningCorrection)),
                Csv(sample.MediaPipeEyeOpeningCorrected.ToString(CultureInfo.InvariantCulture)),
                Csv(sample.MediaPipeMouthOpeningCorrected.ToString(CultureInfo.InvariantCulture)),
                Csv(Format(sample.RawEyeAsymmetryPercent)),
                Csv(Format(sample.EyeAsymmetryPercent)),
                Csv(Format(sample.EyeAgreementPercent)),
                Csv(sample.PossibleOneEyeArtifact.ToString(CultureInfo.InvariantCulture)),
                Csv(sample.LeftEyeReconstructed.ToString(CultureInfo.InvariantCulture)),
                Csv(sample.RightEyeReconstructed.ToString(CultureInfo.InvariantCulture)),
                Csv(sample.MouthReconstructed.ToString(CultureInfo.InvariantCulture)),
                Csv(sample.EyeArtifactSuppressed.ToString(CultureInfo.InvariantCulture)),
                Csv(sample.EyeImageQualityAvailable.ToString(CultureInfo.InvariantCulture)),
                Csv(sample.MouthImageQualityAvailable.ToString(CultureInfo.InvariantCulture)),
                Csv(Format(sample.EyeGlarePercent)),
                Csv(Format(sample.MouthGlarePercent)),
                Csv(Format(sample.EyeContrastPercent)),
                Csv(Format(sample.MouthContrastPercent)),
                Csv(Format(sample.EyeSharpnessPercent)),
                Csv(Format(sample.MouthSharpnessPercent)),
                Csv(Format(sample.EyeDarkCoveragePercent)),
                Csv(Format(sample.MouthDarkCoveragePercent)),
                Csv(sample.CueStatus),
                Csv(Format(sample.CueScore)),
                Csv(Format(sample.EyeCueEligible)),
                Csv(Format(sample.MouthCueEligible)),
                Csv(Format(sample.EyeClosurePercent)),
                Csv(Format(sample.MouthOpeningChangePercent)),
                Csv(Format(sample.JawDroopChangePercent)),
                Csv(Format(sample.CueMediaPipeBlinkBaselineReady)),
                Csv(Format(sample.CueMediaPipeMouthBaselineReady)),
                Csv(Format(sample.CueMediaPipeBlinkBaselinePercent)),
                Csv(Format(sample.CueMediaPipeJawOpenBaselinePercent)),
                Csv(Format(sample.CueMediaPipeMouthCloseBaselinePercent)),
                Csv(Format(sample.CueMediaPipeBlinkChangePercent)),
                Csv(Format(sample.CueMediaPipeJawOpenChangePercent)),
                Csv(Format(sample.CueMediaPipeMouthCloseDropPercent)),
                Csv(Format(sample.CueMediaPipeMouthOpeningEvidencePercent)),
                Csv(sample.TrendStatus),
                Csv(Format(sample.TrendScore)),
                Csv(Format(sample.TrendWindowSeconds)),
                Csv(Format(sample.EyeClosingTrendPercent)),
                Csv(Format(sample.MouthOpeningTrendPercent)),
                Csv(Format(sample.EyeOpeningSlopePerSecond)),
                Csv(Format(sample.MouthOpeningSlopePerSecond))
            ]));
        }

        File.WriteAllText(csvPath, builder.ToString(), Encoding.UTF8);
        return (jsonPath, csvPath);
    }

    private static string Csv(string value)
    {
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string Format(double? value)
    {
        return value?.ToString("0.######", CultureInfo.InvariantCulture) ?? "";
    }

    private static string Format(bool? value)
    {
        return value?.ToString() ?? "";
    }

    private static string Join(IReadOnlyList<string>? values)
    {
        return values is null
            ? ""
            : string.Join("; ", values.Where(static value => !string.IsNullOrWhiteSpace(value)));
    }
}

public sealed class LandmarkEventTimelineSample
{
    public DateTime CapturedAtUtc { get; init; }

    public double ElapsedSeconds { get; init; }

    public double? MotionPercent { get; init; }

    public string BackendStatus { get; init; } = "";

    public string Source { get; init; } = "";

    public string ConfidenceLabel { get; init; } = "";

    public double TrackingConfidence { get; init; }

    public double EyeConfidence { get; init; }

    public double MouthConfidence { get; init; }

    public double EyeQualityPercent { get; init; }

    public double MouthQualityPercent { get; init; }

    public double OverallQualityPercent { get; init; }

    public string CaptureQualityLabel { get; init; } = "";

    public double? CaptureQualityScore { get; init; }

    public bool? CaptureQualityCanCollect { get; init; }

    public bool? CaptureQualityAvatarGrade { get; init; }

    public string CaptureQualityReason { get; init; } = "";

    public double? CaptureQualityCameraModeScore { get; init; }

    public double? CaptureQualityFaceScaleScore { get; init; }

    public double? CaptureQualityEyeScore { get; init; }

    public double? CaptureQualityMouthScore { get; init; }

    public double? CaptureQualityStabilityScore { get; init; }

    public double? CaptureQualityGlassesScore { get; init; }

    public double? CaptureQualityStorageScore { get; init; }

    public double? CaptureQualityFaceWidthPercent { get; init; }

    public double? CaptureQualityFaceHeightPercent { get; init; }

    public string CaptureQualityIssues { get; init; } = "";

    public string CaptureQualitySuggestions { get; init; } = "";

    public string FaceReliabilityStatus { get; init; } = "";

    public int FaceReliabilitySamples { get; init; }

    public double FaceReliability { get; init; }

    public double FaceContinuity { get; init; }

    public double EyeReliability { get; init; }

    public double MouthReliability { get; init; }

    public double FaceBoundsRate { get; init; }

    public double EyeUsableRate { get; init; }

    public double MouthUsableRate { get; init; }

    public bool EyeUsable { get; init; }

    public bool MouthUsable { get; init; }

    public double? RawLeftEyeOpening { get; init; }

    public double? RawRightEyeOpening { get; init; }

    public double? RawAverageEyeOpening { get; init; }

    public double? RawMouthOpening { get; init; }

    public double? RawJawDroop { get; init; }

    public double? LeftEyeOpening { get; init; }

    public double? RightEyeOpening { get; init; }

    public double? AverageEyeOpening { get; init; }

    public double? MouthOpening { get; init; }

    public double? MouthOpeningVelocity { get; init; }

    public double? JawDroop { get; init; }

    public double? JawDroopVelocity { get; init; }

    public double? MediaPipeLeftEyeBlinkPercent { get; init; }

    public double? MediaPipeRightEyeBlinkPercent { get; init; }

    public double? MediaPipeAverageEyeBlinkPercent { get; init; }

    public double? MediaPipeJawOpenPercent { get; init; }

    public double? MediaPipeMouthClosePercent { get; init; }

    public double? MediaPipeEyeOpeningCorrection { get; init; }

    public double? MediaPipeMouthOpeningCorrection { get; init; }

    public bool MediaPipeEyeOpeningCorrected { get; init; }

    public bool MediaPipeMouthOpeningCorrected { get; init; }

    public double? RawEyeAsymmetryPercent { get; init; }

    public double? EyeAsymmetryPercent { get; init; }

    public double EyeAgreementPercent { get; init; }

    public bool PossibleOneEyeArtifact { get; init; }

    public bool LeftEyeReconstructed { get; init; }

    public bool RightEyeReconstructed { get; init; }

    public bool MouthReconstructed { get; init; }

    public bool EyeArtifactSuppressed { get; init; }

    public bool EyeImageQualityAvailable { get; init; }

    public bool MouthImageQualityAvailable { get; init; }

    public double EyeGlarePercent { get; init; }

    public double MouthGlarePercent { get; init; }

    public double EyeContrastPercent { get; init; }

    public double MouthContrastPercent { get; init; }

    public double EyeSharpnessPercent { get; init; }

    public double MouthSharpnessPercent { get; init; }

    public double EyeDarkCoveragePercent { get; init; }

    public double MouthDarkCoveragePercent { get; init; }

    public string CueStatus { get; init; } = "";

    public double? CueScore { get; init; }

    public bool? EyeCueEligible { get; init; }

    public bool? MouthCueEligible { get; init; }

    public double? EyeClosurePercent { get; init; }

    public double? MouthOpeningChangePercent { get; init; }

    public double? JawDroopChangePercent { get; init; }

    public bool? CueMediaPipeBlinkBaselineReady { get; init; }

    public bool? CueMediaPipeMouthBaselineReady { get; init; }

    public double? CueMediaPipeBlinkBaselinePercent { get; init; }

    public double? CueMediaPipeJawOpenBaselinePercent { get; init; }

    public double? CueMediaPipeMouthCloseBaselinePercent { get; init; }

    public double? CueMediaPipeBlinkChangePercent { get; init; }

    public double? CueMediaPipeJawOpenChangePercent { get; init; }

    public double? CueMediaPipeMouthCloseDropPercent { get; init; }

    public double? CueMediaPipeMouthOpeningEvidencePercent { get; init; }

    public string TrendStatus { get; init; } = "";

    public double TrendScore { get; init; }

    public double TrendWindowSeconds { get; init; }

    public double? EyeClosingTrendPercent { get; init; }

    public double? MouthOpeningTrendPercent { get; init; }

    public double? EyeOpeningSlopePerSecond { get; init; }

    public double? MouthOpeningSlopePerSecond { get; init; }
}
