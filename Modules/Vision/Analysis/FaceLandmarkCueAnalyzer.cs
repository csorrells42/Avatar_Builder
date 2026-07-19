using AvatarBuilder.Modules.Vision.Common;
namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceLandmarkCueAnalyzer
{
    private const int BaselineTargetSamples = 20;
    private double _eyeBaseline;
    private double _mouthBaseline;
    private double _jawDroopBaseline;
    private double _mediaPipeBlinkBaseline;
    private double _mediaPipeJawOpenBaseline;
    private double _mediaPipeMouthCloseBaseline;
    private int _eyeBaselineSamples;
    private int _mouthBaselineSamples;
    private int _jawDroopBaselineSamples;
    private int _mediaPipeBlinkBaselineSamples;
    private int _mediaPipeJawOpenBaselineSamples;
    private int _mediaPipeMouthCloseBaselineSamples;

    public void Reset()
    {
        _eyeBaseline = 0d;
        _mouthBaseline = 0d;
        _jawDroopBaseline = 0d;
        _mediaPipeBlinkBaseline = 0d;
        _mediaPipeJawOpenBaseline = 0d;
        _mediaPipeMouthCloseBaseline = 0d;
        _eyeBaselineSamples = 0;
        _mouthBaselineSamples = 0;
        _jawDroopBaselineSamples = 0;
        _mediaPipeBlinkBaselineSamples = 0;
        _mediaPipeJawOpenBaselineSamples = 0;
        _mediaPipeMouthCloseBaselineSamples = 0;
    }

    public FaceLandmarkCueBaselineSnapshot ExportBaseline()
    {
        return new FaceLandmarkCueBaselineSnapshot
        {
            EyeBaseline = _eyeBaseline,
            MouthBaseline = _mouthBaseline,
            JawDroopBaseline = _jawDroopBaseline,
            MediaPipeBlinkBaseline = _mediaPipeBlinkBaseline,
            MediaPipeJawOpenBaseline = _mediaPipeJawOpenBaseline,
            MediaPipeMouthCloseBaseline = _mediaPipeMouthCloseBaseline,
            EyeBaselineSamples = _eyeBaselineSamples,
            MouthBaselineSamples = _mouthBaselineSamples,
            JawDroopBaselineSamples = _jawDroopBaselineSamples,
            MediaPipeBlinkBaselineSamples = _mediaPipeBlinkBaselineSamples,
            MediaPipeJawOpenBaselineSamples = _mediaPipeJawOpenBaselineSamples,
            MediaPipeMouthCloseBaselineSamples = _mediaPipeMouthCloseBaselineSamples
        };
    }

    public bool TryImportBaseline(FaceLandmarkCueBaselineSnapshot? baseline)
    {
        if (baseline is null || GetMaximumBaselineSamples(baseline) <= 0)
        {
            return false;
        }

        _eyeBaseline = baseline.EyeBaseline;
        _mouthBaseline = baseline.MouthBaseline;
        _jawDroopBaseline = baseline.JawDroopBaseline;
        _mediaPipeBlinkBaseline = baseline.MediaPipeBlinkBaseline;
        _mediaPipeJawOpenBaseline = baseline.MediaPipeJawOpenBaseline;
        _mediaPipeMouthCloseBaseline = baseline.MediaPipeMouthCloseBaseline;
        _eyeBaselineSamples = Math.Clamp(baseline.EyeBaselineSamples, 0, BaselineTargetSamples);
        _mouthBaselineSamples = Math.Clamp(baseline.MouthBaselineSamples, 0, BaselineTargetSamples);
        _jawDroopBaselineSamples = Math.Clamp(baseline.JawDroopBaselineSamples, 0, BaselineTargetSamples);
        _mediaPipeBlinkBaselineSamples = Math.Clamp(baseline.MediaPipeBlinkBaselineSamples, 0, BaselineTargetSamples);
        _mediaPipeJawOpenBaselineSamples = Math.Clamp(baseline.MediaPipeJawOpenBaselineSamples, 0, BaselineTargetSamples);
        _mediaPipeMouthCloseBaselineSamples = Math.Clamp(baseline.MediaPipeMouthCloseBaselineSamples, 0, BaselineTargetSamples);
        return true;
    }

    public FaceLandmarkCueAnalysis Analyze(FaceLandmarkMetrics metrics)
    {
        if (!metrics.HasFace)
        {
            return FaceLandmarkCueAnalysis.Waiting;
        }

        var eyeOpening = metrics.AverageEyeOpeningRatio;
        var mouthOpening = metrics.MouthOpeningRatio;
        var jawDroop = metrics.JawDroopRatio;
        var eye = eyeOpening.GetValueOrDefault();
        var mouth = mouthOpening.GetValueOrDefault();
        var jaw = jawDroop.GetValueOrDefault();
        var hasEye = metrics.IsEyeMeasurementUsable;
        var hasMouth = metrics.IsMouthMeasurementUsable;
        var hasJawDroop = metrics.IsJawDroopMeasurementUsable;
        var quality = CalculateQualityPercent(metrics);
        var eyeQuality = metrics.EyeMeasurementQualityPercent;
        var mouthQuality = metrics.MouthMeasurementQualityPercent;
        var mediaPipeBlink = metrics.MediaPipeAverageEyeBlinkPercent;
        var mediaPipeJawOpen = metrics.MediaPipeJawOpenPercent;
        var mediaPipeMouthClose = metrics.MediaPipeMouthClosePercent;
        var hasMediaPipeBlink = mediaPipeBlink.HasValue;
        var hasMediaPipeJawOpen = mediaPipeJawOpen.HasValue;
        var hasMediaPipeMouthClose = mediaPipeMouthClose.HasValue;
        var hasMediaPipeEvidence = hasMediaPipeBlink || hasMediaPipeJawOpen || hasMediaPipeMouthClose;

        var mediaPipeBlinkBaselineSampleEligible = hasMediaPipeBlink && IsMediaPipeBaselineSampleEligible(metrics, isEye: true);
        var mediaPipeJawOpenBaselineSampleEligible = hasMediaPipeJawOpen && IsMediaPipeBaselineSampleEligible(metrics, isEye: false);
        var mediaPipeMouthCloseBaselineSampleEligible = hasMediaPipeMouthClose && IsMediaPipeBaselineSampleEligible(metrics, isEye: false);
        if (mediaPipeBlinkBaselineSampleEligible && _mediaPipeBlinkBaselineSamples < BaselineTargetSamples)
        {
            _mediaPipeBlinkBaselineSamples++;
            _mediaPipeBlinkBaseline = Lerp(_mediaPipeBlinkBaseline, mediaPipeBlink.GetValueOrDefault(), 1d / _mediaPipeBlinkBaselineSamples);
        }

        if (mediaPipeJawOpenBaselineSampleEligible && _mediaPipeJawOpenBaselineSamples < BaselineTargetSamples)
        {
            _mediaPipeJawOpenBaselineSamples++;
            _mediaPipeJawOpenBaseline = Lerp(_mediaPipeJawOpenBaseline, mediaPipeJawOpen.GetValueOrDefault(), 1d / _mediaPipeJawOpenBaselineSamples);
        }

        if (mediaPipeMouthCloseBaselineSampleEligible && _mediaPipeMouthCloseBaselineSamples < BaselineTargetSamples)
        {
            _mediaPipeMouthCloseBaselineSamples++;
            _mediaPipeMouthCloseBaseline = Lerp(_mediaPipeMouthCloseBaseline, mediaPipeMouthClose.GetValueOrDefault(), 1d / _mediaPipeMouthCloseBaselineSamples);
        }

        var mediaPipeBlinkBaselineReady = _mediaPipeBlinkBaselineSamples >= BaselineTargetSamples;
        var mediaPipeJawOpenBaselineReady = _mediaPipeJawOpenBaselineSamples >= BaselineTargetSamples;
        var mediaPipeMouthCloseBaselineReady = _mediaPipeMouthCloseBaselineSamples >= BaselineTargetSamples;
        var mediaPipeMouthBaselineReady = mediaPipeJawOpenBaselineReady || mediaPipeMouthCloseBaselineReady;
        var mediaPipeBlinkChange = CalculateRiseFromBaseline(mediaPipeBlink, mediaPipeBlinkBaselineReady, _mediaPipeBlinkBaseline);
        var mediaPipeJawOpenChange = CalculateRiseFromBaseline(mediaPipeJawOpen, mediaPipeJawOpenBaselineReady, _mediaPipeJawOpenBaseline);
        var mediaPipeMouthCloseDrop = CalculateDropFromBaseline(mediaPipeMouthClose, mediaPipeMouthCloseBaselineReady, _mediaPipeMouthCloseBaseline);
        var mediaPipeMouthEvidence = MaxOptional(mediaPipeJawOpenChange, mediaPipeMouthCloseDrop);

        FaceLandmarkCueAnalysis CreateAnalysis(
            bool hasUsableMeasurements,
            bool baselineReady,
            int baselineSamples,
            bool eyeCueEligible = false,
            bool mouthCueEligible = false,
            double? eyeClosure = null,
            double? mouthChange = null,
            double? jawDroopChange = null,
            double composite = 0d)
        {
            return new FaceLandmarkCueAnalysis
            {
                HasUsableMeasurements = hasUsableMeasurements,
                BaselineReady = baselineReady,
                BaselineSamples = baselineSamples,
                QualityPercent = quality,
                EyeQualityPercent = eyeQuality,
                MouthQualityPercent = mouthQuality,
                EyeCueEligible = eyeCueEligible,
                MouthCueEligible = mouthCueEligible,
                EyeBaselineReady = _eyeBaselineSamples >= BaselineTargetSamples,
                MouthBaselineReady = _mouthBaselineSamples >= BaselineTargetSamples || _jawDroopBaselineSamples >= BaselineTargetSamples,
                MediaPipeBlinkBaselineReady = mediaPipeBlinkBaselineReady,
                MediaPipeMouthBaselineReady = mediaPipeMouthBaselineReady,
                EyeOpeningRatio = eyeOpening,
                EyeBaselineRatio = eyeCueEligible || (hasEye && _eyeBaselineSamples >= BaselineTargetSamples) ? _eyeBaseline : null,
                EyeClosurePercent = eyeClosure,
                MouthOpeningRatio = mouthOpening,
                MouthBaselineRatio = hasMouth && _mouthBaselineSamples >= BaselineTargetSamples ? _mouthBaseline : null,
                MouthOpeningChangePercent = mouthChange,
                MouthOpeningVelocityPerSecond = metrics.MouthOpeningVelocityPerSecond,
                JawDroopRatio = jawDroop,
                JawDroopBaselineRatio = hasJawDroop && _jawDroopBaselineSamples >= BaselineTargetSamples ? _jawDroopBaseline : null,
                JawDroopChangePercent = jawDroopChange,
                JawDroopVelocityPerSecond = metrics.JawDroopVelocityPerSecond,
                MediaPipeBlinkBaselinePercent = mediaPipeBlinkBaselineReady ? _mediaPipeBlinkBaseline : null,
                MediaPipeJawOpenBaselinePercent = mediaPipeJawOpenBaselineReady ? _mediaPipeJawOpenBaseline : null,
                MediaPipeMouthCloseBaselinePercent = mediaPipeMouthCloseBaselineReady ? _mediaPipeMouthCloseBaseline : null,
                MediaPipeBlinkChangePercent = mediaPipeBlinkChange,
                MediaPipeJawOpenChangePercent = mediaPipeJawOpenChange,
                MediaPipeMouthCloseDropPercent = mediaPipeMouthCloseDrop,
                MediaPipeMouthOpeningEvidencePercent = mediaPipeMouthEvidence,
                CompositeCuePercent = composite
            };
        }

        var mediaPipeEyeCueEligible = hasMediaPipeBlink && mediaPipeBlinkBaselineReady;
        var mediaPipeMouthCueEligible = (hasMediaPipeJawOpen || hasMediaPipeMouthClose) && mediaPipeMouthBaselineReady;
        if (!hasEye && !hasMouth && !hasJawDroop)
        {
            var mediaPipeOnlyBaselineReady = mediaPipeEyeCueEligible || mediaPipeMouthCueEligible;
            if (mediaPipeOnlyBaselineReady)
            {
                var mediaPipeOnlyComposite = CalculateCompositeCue(
                    null,
                    null,
                    0d,
                    mediaPipeBlinkChange,
                    mediaPipeMouthEvidence,
                    quality,
                    mediaPipeEyeCueEligible,
                    mediaPipeMouthCueEligible);
                return CreateAnalysis(
                    hasUsableMeasurements: true,
                    baselineReady: true,
                    baselineSamples: Math.Max(_mediaPipeBlinkBaselineSamples, Math.Max(_mediaPipeJawOpenBaselineSamples, _mediaPipeMouthCloseBaselineSamples)),
                    eyeCueEligible: mediaPipeEyeCueEligible,
                    mouthCueEligible: mediaPipeMouthCueEligible,
                    composite: mediaPipeOnlyComposite);
            }

            return CreateAnalysis(
                hasMediaPipeEvidence,
                baselineReady: false,
                baselineSamples: hasMediaPipeEvidence
                    ? Math.Max(_mediaPipeBlinkBaselineSamples, Math.Max(_mediaPipeJawOpenBaselineSamples, _mediaPipeMouthCloseBaselineSamples))
                    : Math.Min(_eyeBaselineSamples, _mouthBaselineSamples));
        }

        var eyeBaselineSampleEligible = hasEye && IsBaselineSampleEligible(metrics, isEye: true);
        var mouthBaselineSampleEligible = hasMouth && IsBaselineSampleEligible(metrics, isEye: false);
        var jawDroopBaselineSampleEligible = hasJawDroop && IsBaselineSampleEligible(metrics, isEye: false);
        if (eyeBaselineSampleEligible && _eyeBaselineSamples < BaselineTargetSamples)
        {
            _eyeBaselineSamples++;
            _eyeBaseline = Lerp(_eyeBaseline, eye, 1d / _eyeBaselineSamples);
        }

        if (mouthBaselineSampleEligible && _mouthBaselineSamples < BaselineTargetSamples)
        {
            _mouthBaselineSamples++;
            _mouthBaseline = Lerp(_mouthBaseline, mouth, 1d / _mouthBaselineSamples);
        }

        if (jawDroopBaselineSampleEligible && _jawDroopBaselineSamples < BaselineTargetSamples)
        {
            _jawDroopBaselineSamples++;
            _jawDroopBaseline = Lerp(_jawDroopBaseline, jaw, 1d / _jawDroopBaselineSamples);
        }

        var eyeBaselineReady = _eyeBaselineSamples >= BaselineTargetSamples;
        var mouthBaselineReady = _mouthBaselineSamples >= BaselineTargetSamples;
        var jawDroopBaselineReady = _jawDroopBaselineSamples >= BaselineTargetSamples;
        var lowerFaceBaselineReady = mouthBaselineReady || jawDroopBaselineReady;
        var baselineReady = hasEye
            ? eyeBaselineReady || mediaPipeEyeCueEligible
            : mediaPipeEyeCueEligible || ((hasMouth || hasJawDroop) && lowerFaceBaselineReady);
        if (!baselineReady)
        {
            return CreateAnalysis(
                hasUsableMeasurements: true,
                baselineReady: false,
                baselineSamples: hasEye ? _eyeBaselineSamples : Math.Max(_mouthBaselineSamples, _jawDroopBaselineSamples),
                eyeCueEligible: hasEye,
                mouthCueEligible: hasMouth || hasJawDroop);
        }

        var eyeCueEligible = (hasEye && eyeBaselineReady) || mediaPipeEyeCueEligible;
        var mouthCueEligible = (hasMouth && mouthBaselineReady) || (hasJawDroop && jawDroopBaselineReady) || mediaPipeMouthCueEligible;
        var eyeClosure = eyeCueEligible && _eyeBaseline > 0.0001d
            ? Math.Clamp((_eyeBaseline - eye) / _eyeBaseline * 100d, 0d, 100d)
            : (double?)null;
        var mouthChange = mouthCueEligible && _mouthBaseline > 0.0001d
            ? Math.Clamp((mouth - _mouthBaseline) / Math.Max(_mouthBaseline, 0.015d) * 100d, 0d, 300d)
            : (double?)null;
        var jawDroopChange = mouthCueEligible && _jawDroopBaseline > 0.0001d
            ? Math.Clamp((jaw - _jawDroopBaseline) / Math.Max(_jawDroopBaseline, 0.02d) * 100d, 0d, 300d)
            : (double?)null;
        var mouthVelocityCue = mouthCueEligible && metrics.MouthOpeningVelocityPerSecond is double velocity
            ? Math.Clamp(velocity * 350d, 0d, 60d)
            : 0d;
        var jawVelocityCue = mouthCueEligible && metrics.JawDroopVelocityPerSecond is double jawVelocity
            ? Math.Clamp(jawVelocity * 280d, 0d, 60d)
            : 0d;
        var composite = CalculateCompositeCue(
            eyeClosure,
            MaxOptional(mouthChange, jawDroopChange),
            Math.Max(mouthVelocityCue, jawVelocityCue),
            mediaPipeBlinkChange,
            mediaPipeMouthEvidence,
            quality,
            eyeCueEligible,
            mouthCueEligible);

        if (composite < 15d)
        {
            if (eyeBaselineSampleEligible)
            {
                _eyeBaseline = Lerp(_eyeBaseline, eye, 0.012d);
            }

            if (mouthBaselineSampleEligible)
            {
                _mouthBaseline = Lerp(_mouthBaseline, mouth, 0.012d);
            }

            if (jawDroopBaselineSampleEligible)
            {
                _jawDroopBaseline = Lerp(_jawDroopBaseline, jaw, 0.012d);
            }

            if (mediaPipeBlinkBaselineReady && mediaPipeBlinkBaselineSampleEligible)
            {
                _mediaPipeBlinkBaseline = Lerp(_mediaPipeBlinkBaseline, mediaPipeBlink.GetValueOrDefault(), 0.012d);
            }

            if (mediaPipeJawOpenBaselineReady && mediaPipeJawOpenBaselineSampleEligible)
            {
                _mediaPipeJawOpenBaseline = Lerp(_mediaPipeJawOpenBaseline, mediaPipeJawOpen.GetValueOrDefault(), 0.012d);
            }

            if (mediaPipeMouthCloseBaselineReady && mediaPipeMouthCloseBaselineSampleEligible)
            {
                _mediaPipeMouthCloseBaseline = Lerp(_mediaPipeMouthCloseBaseline, mediaPipeMouthClose.GetValueOrDefault(), 0.012d);
            }
        }

        return CreateAnalysis(
            hasUsableMeasurements: true,
            baselineReady: true,
            baselineSamples: hasEye ? _eyeBaselineSamples : Math.Max(_mouthBaselineSamples, _jawDroopBaselineSamples),
            eyeCueEligible: eyeCueEligible,
            mouthCueEligible: mouthCueEligible,
            eyeClosure: eyeClosure,
            mouthChange: mouthChange,
            jawDroopChange: jawDroopChange,
            composite: composite);
    }

    private static double CalculateCompositeCue(
        double? eyeClosure,
        double? mouthChange,
        double mouthVelocityCue,
        double? mediaPipeBlinkChange,
        double? mediaPipeMouthEvidence,
        double quality,
        bool eyeCueEligible,
        bool mouthCueEligible)
    {
        var contourEye = eyeClosure.GetValueOrDefault();
        var mediaPipeEye = mediaPipeBlinkChange is double blinkEvidence
            ? Math.Clamp(blinkEvidence * 1.05d, 0d, 100d)
            : 0d;
        var eye = Math.Max(contourEye, mediaPipeEye);
        var contourMouth = Math.Min(100d, mouthChange.GetValueOrDefault());
        var mediaPipeMouth = mediaPipeMouthEvidence is double mouthEvidence
            ? Math.Clamp(mouthEvidence * 0.95d, 0d, 100d)
            : 0d;
        var mouth = Math.Max(contourMouth, mediaPipeMouth);
        var raw = eye * 0.76d + mouth * 0.18d + mouthVelocityCue * 0.06d;
        if (eyeCueEligible && mediaPipeBlinkChange is double blinkChange && contourEye >= mediaPipeEye)
        {
            raw += Math.Min(8d, blinkChange * 0.12d);
        }

        if (mouthCueEligible && mediaPipeMouthEvidence is double mouthEvidenceBonus && contourMouth >= mediaPipeMouth)
        {
            raw += Math.Min(6d, mouthEvidenceBonus * 0.08d);
        }

        if (!eyeCueEligible)
        {
            raw *= mouthCueEligible && mediaPipeMouth > 0d ? 0.72d : 0.35d;
        }

        var qualityMultiplier = quality < 35d ? 0.55d : quality < 50d ? 0.78d : 1d;
        return Math.Clamp(raw * qualityMultiplier, 0d, 100d);
    }

    private static double CalculateQualityPercent(FaceLandmarkMetrics metrics)
    {
        var quality = metrics.OverallMeasurementQualityPercent;
        if (quality <= 0d && metrics.HasMediaPipeBlendshapeEvidence)
        {
            quality = Math.Clamp(metrics.TrackingConfidence * 100d, 35d, 72d);
        }

        return Math.Clamp(quality, 0d, 100d);
    }

    private static bool IsBaselineSampleEligible(FaceLandmarkMetrics metrics, bool isEye)
    {
        var quality = isEye
            ? metrics.EyeMeasurementQualityPercent
            : metrics.MouthMeasurementQualityPercent;
        if (quality < (isEye ? 60d : 55d))
        {
            return false;
        }

        var source = metrics.Source;
        return !source.Contains("temporal hold", StringComparison.OrdinalIgnoreCase)
            && !source.Contains("temporal face hold", StringComparison.OrdinalIgnoreCase)
            && !source.Contains("landmark hold", StringComparison.OrdinalIgnoreCase)
            && !source.Contains("temporal reconstruction", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMediaPipeBaselineSampleEligible(FaceLandmarkMetrics metrics, bool isEye)
    {
        if (metrics.TrackingConfidence < 0.35d)
        {
            return false;
        }

        var quality = isEye
            ? metrics.EyeMeasurementQualityPercent
            : metrics.MouthMeasurementQualityPercent;
        if (quality > 0d && quality < (isEye ? 35d : 30d))
        {
            return false;
        }

        return IsStableSource(metrics.Source);
    }

    private static double? CalculateRiseFromBaseline(double? current, bool baselineReady, double baseline)
    {
        return current is double value && baselineReady
            ? Math.Clamp(value - baseline, 0d, 100d)
            : null;
    }

    private static double? CalculateDropFromBaseline(double? current, bool baselineReady, double baseline)
    {
        return current is double value && baselineReady
            ? Math.Clamp(baseline - value, 0d, 100d)
            : null;
    }

    private static double? MaxOptional(double? left, double? right)
    {
        return (left, right) switch
        {
            (double leftValue, double rightValue) => Math.Max(leftValue, rightValue),
            (double leftValue, null) => leftValue,
            (null, double rightValue) => rightValue,
            _ => null
        };
    }

    private static bool IsStableSource(string source)
    {
        return !source.Contains("temporal hold", StringComparison.OrdinalIgnoreCase)
            && !source.Contains("temporal face hold", StringComparison.OrdinalIgnoreCase)
            && !source.Contains("landmark hold", StringComparison.OrdinalIgnoreCase)
            && !source.Contains("temporal reconstruction", StringComparison.OrdinalIgnoreCase);
    }

    private static double Lerp(double from, double to, double amount)
    {
        return from + (to - from) * amount;
    }

    private static int GetMaximumBaselineSamples(FaceLandmarkCueBaselineSnapshot baseline)
    {
        return Math.Max(
            Math.Max(baseline.EyeBaselineSamples, baseline.MouthBaselineSamples),
            Math.Max(
                baseline.JawDroopBaselineSamples,
                Math.Max(
                    baseline.MediaPipeBlinkBaselineSamples,
                    Math.Max(baseline.MediaPipeJawOpenBaselineSamples, baseline.MediaPipeMouthCloseBaselineSamples))));
    }
}
