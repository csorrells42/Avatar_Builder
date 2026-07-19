using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Personalization;

namespace AvatarBuilder.Modules.Episodes;

public sealed class LandmarkEventAggregate
{
    private readonly HashSet<string> _backendStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _captureQualityIssues = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _captureQualityLabels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sources = new(StringComparer.OrdinalIgnoreCase);

    public int SampleCount { get; private set; }

    public double? MinimumEyeOpeningRatio { get; private set; }

    public double? MaximumEyeClosurePercent { get; private set; }

    public double? MaximumMouthOpeningRatio { get; private set; }

    public double? MaximumMouthOpeningChangePercent { get; private set; }

    public double? MaximumMouthOpeningVelocityPerSecond { get; private set; }

    public double? MaximumJawDroopRatio { get; private set; }

    public double? MaximumJawDroopChangePercent { get; private set; }

    public double? MaximumJawDroopVelocityPerSecond { get; private set; }

    public double? MaximumMediaPipeAverageEyeBlinkPercent { get; private set; }

    public double? MaximumMediaPipeJawOpenPercent { get; private set; }

    public double? MinimumMediaPipeMouthClosePercent { get; private set; }

    public double? MaximumMediaPipeBlinkChangePercent { get; private set; }

    public double? MaximumMediaPipeJawOpenChangePercent { get; private set; }

    public double? MaximumMediaPipeMouthCloseDropPercent { get; private set; }

    public double? MaximumMediaPipeMouthOpeningEvidencePercent { get; private set; }

    public double? MaximumLandmarkCueScore { get; private set; }

    public double? MaximumEyeClosingTrendPercent { get; private set; }

    public double? MaximumMouthOpeningTrendPercent { get; private set; }

    public double? MinimumEyeOpeningSlopePerSecond { get; private set; }

    public double? MaximumMouthOpeningSlopePerSecond { get; private set; }

    public double? MaximumLandmarkTrendScore { get; private set; }

    public double? MaximumEyeGlarePercent { get; private set; }

    public double? MaximumMouthGlarePercent { get; private set; }

    public double? MinimumEyeContrastPercent { get; private set; }

    public double? MinimumMouthContrastPercent { get; private set; }

    public double? MinimumEyeSharpnessPercent { get; private set; }

    public double? MinimumMouthSharpnessPercent { get; private set; }

    public double? MaximumRawEyeAsymmetryPercent { get; private set; }

    public double? MaximumEyeAsymmetryPercent { get; private set; }

    public int PossibleOneEyeArtifactSamples { get; private set; }

    public int LeftEyeReconstructedSamples { get; private set; }

    public int RightEyeReconstructedSamples { get; private set; }

    public int MouthReconstructedSamples { get; private set; }

    public int EyeArtifactSuppressedSamples { get; private set; }

    public int MediaPipeEyeOpeningCorrectedSamples { get; private set; }

    public int MediaPipeMouthOpeningCorrectedSamples { get; private set; }

    public double? MaximumAbsoluteMediaPipeEyeOpeningCorrection { get; private set; }

    public double? MaximumAbsoluteMediaPipeMouthOpeningCorrection { get; private set; }

    public double? MinimumEyeQualityPercent { get; private set; }

    public double? MinimumMouthQualityPercent { get; private set; }

    public double? MinimumOverallQualityPercent { get; private set; }

    public double AverageOverallQualityPercent => SampleCount == 0 ? 0d : _overallQualityTotal / SampleCount;

    public int FaceReliabilitySamples { get; private set; }

    public int FaceReliabilityUsableSamples { get; private set; }

    public double? MinimumFaceReliabilityPercent { get; private set; }

    public double AverageFaceReliabilityPercent => FaceReliabilitySamples == 0 ? 0d : _faceReliabilityTotal / FaceReliabilitySamples;

    public double? MinimumFaceContinuityPercent { get; private set; }

    public double AverageFaceContinuityPercent => FaceReliabilitySamples == 0 ? 0d : _faceContinuityTotal / FaceReliabilitySamples;

    public double? MinimumEyeReliabilityPercent { get; private set; }

    public double AverageEyeReliabilityPercent => FaceReliabilitySamples == 0 ? 0d : _eyeReliabilityTotal / FaceReliabilitySamples;

    public double? MinimumMouthReliabilityPercent { get; private set; }

    public double AverageMouthReliabilityPercent => FaceReliabilitySamples == 0 ? 0d : _mouthReliabilityTotal / FaceReliabilitySamples;

    public int CaptureQualitySamples { get; private set; }

    public int CaptureQualityCanCollectSamples { get; private set; }

    public int CaptureQualityAvatarGradeSamples { get; private set; }

    public double? MinimumCaptureQualityScore { get; private set; }

    public double? MaximumCaptureQualityScore { get; private set; }

    public double AverageCaptureQualityScore => CaptureQualitySamples == 0 ? 0d : _captureQualityScoreTotal / CaptureQualitySamples;

    public IReadOnlyList<string> CaptureQualityLabels => _captureQualityLabels.OrderBy(static label => label).ToList();

    public IReadOnlyList<string> CaptureQualityIssues => _captureQualityIssues.OrderBy(static issue => issue).ToList();

    public IReadOnlyList<string> Sources => _sources.OrderBy(static source => source).ToList();

    public IReadOnlyList<string> BackendStatuses => _backendStatuses.OrderBy(static status => status).ToList();

    private double _overallQualityTotal;
    private double _faceReliabilityTotal;
    private double _faceContinuityTotal;
    private double _eyeReliabilityTotal;
    private double _mouthReliabilityTotal;
    private double _captureQualityScoreTotal;

    public void Reset()
    {
        _backendStatuses.Clear();
        _captureQualityIssues.Clear();
        _captureQualityLabels.Clear();
        _sources.Clear();
        SampleCount = 0;
        MinimumEyeOpeningRatio = null;
        MaximumEyeClosurePercent = null;
        MaximumMouthOpeningRatio = null;
        MaximumMouthOpeningChangePercent = null;
        MaximumMouthOpeningVelocityPerSecond = null;
        MaximumJawDroopRatio = null;
        MaximumJawDroopChangePercent = null;
        MaximumJawDroopVelocityPerSecond = null;
        MaximumMediaPipeAverageEyeBlinkPercent = null;
        MaximumMediaPipeJawOpenPercent = null;
        MinimumMediaPipeMouthClosePercent = null;
        MaximumMediaPipeBlinkChangePercent = null;
        MaximumMediaPipeJawOpenChangePercent = null;
        MaximumMediaPipeMouthCloseDropPercent = null;
        MaximumMediaPipeMouthOpeningEvidencePercent = null;
        MaximumLandmarkCueScore = null;
        MaximumEyeClosingTrendPercent = null;
        MaximumMouthOpeningTrendPercent = null;
        MinimumEyeOpeningSlopePerSecond = null;
        MaximumMouthOpeningSlopePerSecond = null;
        MaximumLandmarkTrendScore = null;
        MaximumEyeGlarePercent = null;
        MaximumMouthGlarePercent = null;
        MinimumEyeContrastPercent = null;
        MinimumMouthContrastPercent = null;
        MinimumEyeSharpnessPercent = null;
        MinimumMouthSharpnessPercent = null;
        MaximumRawEyeAsymmetryPercent = null;
        MaximumEyeAsymmetryPercent = null;
        PossibleOneEyeArtifactSamples = 0;
        LeftEyeReconstructedSamples = 0;
        RightEyeReconstructedSamples = 0;
        MouthReconstructedSamples = 0;
        EyeArtifactSuppressedSamples = 0;
        MediaPipeEyeOpeningCorrectedSamples = 0;
        MediaPipeMouthOpeningCorrectedSamples = 0;
        MaximumAbsoluteMediaPipeEyeOpeningCorrection = null;
        MaximumAbsoluteMediaPipeMouthOpeningCorrection = null;
        MinimumEyeQualityPercent = null;
        MinimumMouthQualityPercent = null;
        MinimumOverallQualityPercent = null;
        _overallQualityTotal = 0d;
        FaceReliabilitySamples = 0;
        FaceReliabilityUsableSamples = 0;
        MinimumFaceReliabilityPercent = null;
        MinimumFaceContinuityPercent = null;
        MinimumEyeReliabilityPercent = null;
        MinimumMouthReliabilityPercent = null;
        _faceReliabilityTotal = 0d;
        _faceContinuityTotal = 0d;
        _eyeReliabilityTotal = 0d;
        _mouthReliabilityTotal = 0d;
        CaptureQualitySamples = 0;
        CaptureQualityCanCollectSamples = 0;
        CaptureQualityAvatarGradeSamples = 0;
        MinimumCaptureQualityScore = null;
        MaximumCaptureQualityScore = null;
        _captureQualityScoreTotal = 0d;
    }

    public void Update(
        FaceLandmarkMetrics metrics,
        FaceLandmarkCueAnalysis? cueAnalysis,
        FaceLandmarkTrendAnalysis? trendAnalysis,
        string backendStatus)
    {
        Update(metrics, cueAnalysis, trendAnalysis, null, backendStatus, null);
    }

    public void Update(
        FaceLandmarkMetrics metrics,
        FaceLandmarkCueAnalysis? cueAnalysis,
        FaceLandmarkTrendAnalysis? trendAnalysis,
        FaceLockStabilityAnalysis? stabilityAnalysis,
        string backendStatus)
    {
        Update(metrics, cueAnalysis, trendAnalysis, stabilityAnalysis, backendStatus, null);
    }

    public void Update(
        FaceLandmarkMetrics metrics,
        FaceLandmarkCueAnalysis? cueAnalysis,
        FaceLandmarkTrendAnalysis? trendAnalysis,
        FaceLockStabilityAnalysis? stabilityAnalysis,
        string backendStatus,
        AvatarCaptureQualityAssessment? captureQuality)
    {
        if (!string.IsNullOrWhiteSpace(backendStatus))
        {
            _backendStatuses.Add(backendStatus.Trim());
        }

        UpdateReliability(stabilityAnalysis);
        UpdateCaptureQuality(captureQuality);

        if (!metrics.HasFace)
        {
            return;
        }

        SampleCount++;
        if (!string.IsNullOrWhiteSpace(metrics.Source))
        {
            _sources.Add(metrics.Source.Trim());
        }

        MinimumEyeOpeningRatio = Min(MinimumEyeOpeningRatio, metrics.AverageEyeOpeningRatio);
        MaximumMouthOpeningRatio = Max(MaximumMouthOpeningRatio, metrics.MouthOpeningRatio);
        MaximumMouthOpeningVelocityPerSecond = Max(MaximumMouthOpeningVelocityPerSecond, metrics.MouthOpeningVelocityPerSecond);
        MaximumJawDroopRatio = Max(MaximumJawDroopRatio, metrics.JawDroopRatio);
        MaximumJawDroopVelocityPerSecond = Max(MaximumJawDroopVelocityPerSecond, metrics.JawDroopVelocityPerSecond);
        MaximumMediaPipeAverageEyeBlinkPercent = Max(MaximumMediaPipeAverageEyeBlinkPercent, metrics.MediaPipeAverageEyeBlinkPercent);
        MaximumMediaPipeJawOpenPercent = Max(MaximumMediaPipeJawOpenPercent, metrics.MediaPipeJawOpenPercent);
        MinimumMediaPipeMouthClosePercent = Min(MinimumMediaPipeMouthClosePercent, metrics.MediaPipeMouthClosePercent);
        MaximumAbsoluteMediaPipeEyeOpeningCorrection = Max(MaximumAbsoluteMediaPipeEyeOpeningCorrection, Abs(metrics.MediaPipeEyeOpeningCorrectionRatio));
        MaximumAbsoluteMediaPipeMouthOpeningCorrection = Max(MaximumAbsoluteMediaPipeMouthOpeningCorrection, Abs(metrics.MediaPipeMouthOpeningCorrectionRatio));
        MinimumEyeQualityPercent = Min(MinimumEyeQualityPercent, metrics.EyeMeasurementQualityPercent);
        MinimumMouthQualityPercent = Min(MinimumMouthQualityPercent, metrics.MouthMeasurementQualityPercent);
        MinimumOverallQualityPercent = Min(MinimumOverallQualityPercent, metrics.OverallMeasurementQualityPercent);
        MaximumRawEyeAsymmetryPercent = Max(MaximumRawEyeAsymmetryPercent, metrics.RawEyeAsymmetryPercent);
        MaximumEyeAsymmetryPercent = Max(MaximumEyeAsymmetryPercent, metrics.EyeAsymmetryPercent);
        if (metrics.PossibleOneEyeArtifact)
        {
            PossibleOneEyeArtifactSamples++;
        }

        if (metrics.LeftEyeReconstructed)
        {
            LeftEyeReconstructedSamples++;
        }

        if (metrics.RightEyeReconstructed)
        {
            RightEyeReconstructedSamples++;
        }

        if (metrics.MouthReconstructed)
        {
            MouthReconstructedSamples++;
        }

        if (metrics.EyeArtifactSuppressed)
        {
            EyeArtifactSuppressedSamples++;
        }

        if (metrics.MediaPipeEyeOpeningCorrected)
        {
            MediaPipeEyeOpeningCorrectedSamples++;
        }

        if (metrics.MediaPipeMouthOpeningCorrected)
        {
            MediaPipeMouthOpeningCorrectedSamples++;
        }

        _overallQualityTotal += metrics.OverallMeasurementQualityPercent;
        if (metrics.EyeImageQualityAvailable)
        {
            MaximumEyeGlarePercent = Max(MaximumEyeGlarePercent, metrics.EyeGlarePercent);
            MinimumEyeContrastPercent = Min(MinimumEyeContrastPercent, metrics.EyeContrastPercent);
            MinimumEyeSharpnessPercent = Min(MinimumEyeSharpnessPercent, metrics.EyeSharpnessPercent);
        }

        if (metrics.MouthImageQualityAvailable)
        {
            MaximumMouthGlarePercent = Max(MaximumMouthGlarePercent, metrics.MouthGlarePercent);
            MinimumMouthContrastPercent = Min(MinimumMouthContrastPercent, metrics.MouthContrastPercent);
            MinimumMouthSharpnessPercent = Min(MinimumMouthSharpnessPercent, metrics.MouthSharpnessPercent);
        }

        if (cueAnalysis is not null)
        {
            MaximumEyeClosurePercent = Max(MaximumEyeClosurePercent, cueAnalysis.EyeClosurePercent);
            MaximumMouthOpeningChangePercent = Max(MaximumMouthOpeningChangePercent, cueAnalysis.MouthOpeningChangePercent);
            MaximumJawDroopChangePercent = Max(MaximumJawDroopChangePercent, cueAnalysis.JawDroopChangePercent);
            MaximumMediaPipeBlinkChangePercent = Max(MaximumMediaPipeBlinkChangePercent, cueAnalysis.MediaPipeBlinkChangePercent);
            MaximumMediaPipeJawOpenChangePercent = Max(MaximumMediaPipeJawOpenChangePercent, cueAnalysis.MediaPipeJawOpenChangePercent);
            MaximumMediaPipeMouthCloseDropPercent = Max(MaximumMediaPipeMouthCloseDropPercent, cueAnalysis.MediaPipeMouthCloseDropPercent);
            MaximumMediaPipeMouthOpeningEvidencePercent = Max(MaximumMediaPipeMouthOpeningEvidencePercent, cueAnalysis.MediaPipeMouthOpeningEvidencePercent);
            MaximumLandmarkCueScore = Max(MaximumLandmarkCueScore, cueAnalysis.CompositeCuePercent);
        }

        if (trendAnalysis is not null)
        {
            MaximumEyeClosingTrendPercent = Max(MaximumEyeClosingTrendPercent, trendAnalysis.EyeClosingTrendPercent);
            MaximumMouthOpeningTrendPercent = Max(MaximumMouthOpeningTrendPercent, trendAnalysis.MouthOpeningTrendPercent);
            MinimumEyeOpeningSlopePerSecond = Min(MinimumEyeOpeningSlopePerSecond, trendAnalysis.EyeOpeningSlopePerSecond);
            MaximumMouthOpeningSlopePerSecond = Max(MaximumMouthOpeningSlopePerSecond, trendAnalysis.MouthOpeningSlopePerSecond);
            MaximumLandmarkTrendScore = Max(MaximumLandmarkTrendScore, trendAnalysis.TrendCuePercent);
        }
    }

    private void UpdateCaptureQuality(AvatarCaptureQualityAssessment? captureQuality)
    {
        if (captureQuality is null)
        {
            return;
        }

        CaptureQualitySamples++;
        if (captureQuality.CanCollectMeasurements)
        {
            CaptureQualityCanCollectSamples++;
        }

        if (captureQuality.StrongEnoughForAvatarLearning)
        {
            CaptureQualityAvatarGradeSamples++;
        }

        MinimumCaptureQualityScore = Min(MinimumCaptureQualityScore, captureQuality.ScorePercent);
        MaximumCaptureQualityScore = Max(MaximumCaptureQualityScore, captureQuality.ScorePercent);
        _captureQualityScoreTotal += captureQuality.ScorePercent;
        if (!string.IsNullOrWhiteSpace(captureQuality.Label))
        {
            _captureQualityLabels.Add(captureQuality.Label.Trim());
        }

        foreach (var issue in captureQuality.Issues)
        {
            if (!string.IsNullOrWhiteSpace(issue))
            {
                _captureQualityIssues.Add(issue.Trim());
            }
        }
    }

    private void UpdateReliability(FaceLockStabilityAnalysis? stabilityAnalysis)
    {
        if (stabilityAnalysis is null || stabilityAnalysis.SampleCount <= 0)
        {
            return;
        }

        FaceReliabilitySamples++;
        if (stabilityAnalysis.HasUsableLock)
        {
            FaceReliabilityUsableSamples++;
        }

        MinimumFaceReliabilityPercent = Min(MinimumFaceReliabilityPercent, stabilityAnalysis.CompositeReliabilityPercent);
        MinimumFaceContinuityPercent = Min(MinimumFaceContinuityPercent, stabilityAnalysis.FaceContinuityPercent);
        MinimumEyeReliabilityPercent = Min(MinimumEyeReliabilityPercent, stabilityAnalysis.EyeReliabilityPercent);
        MinimumMouthReliabilityPercent = Min(MinimumMouthReliabilityPercent, stabilityAnalysis.MouthReliabilityPercent);
        _faceReliabilityTotal += stabilityAnalysis.CompositeReliabilityPercent;
        _faceContinuityTotal += stabilityAnalysis.FaceContinuityPercent;
        _eyeReliabilityTotal += stabilityAnalysis.EyeReliabilityPercent;
        _mouthReliabilityTotal += stabilityAnalysis.MouthReliabilityPercent;
    }

    private static double? Min(double? current, double? candidate)
    {
        if (candidate is not double value)
        {
            return current;
        }

        return current is double existing ? Math.Min(existing, value) : value;
    }

    private static double? Max(double? current, double? candidate)
    {
        if (candidate is not double value)
        {
            return current;
        }

        return current is double existing ? Math.Max(existing, value) : value;
    }

    private static double? Abs(double? value)
    {
        return value is double number ? Math.Abs(number) : null;
    }
}
