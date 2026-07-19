using AvatarBuilder.Modules.Vision.OpenCv;

namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class EyeInsetCueAnalyzer
{
    private const int BaselineTargetSamples = 12;
    private double _baselineOpeningRatio;
    private int _baselineSamples;

    public void Reset()
    {
        _baselineOpeningRatio = 0d;
        _baselineSamples = 0;
    }

    public EyeInsetCueAnalysis Analyze(EyeInsetApertureAnalysis? analysis)
    {
        if (analysis is null || !analysis.HasMeasurement || analysis.AverageEyeOpeningRatio is not double opening)
        {
            return EyeInsetCueAnalysis.Waiting;
        }

        var quality = CalculateQualityPercent(analysis);
        var sampleEligible = quality >= 45d;
        if (sampleEligible && _baselineSamples < BaselineTargetSamples)
        {
            _baselineSamples++;
            _baselineOpeningRatio = _baselineSamples == 1
                ? opening
                : Math.Max(_baselineOpeningRatio, opening);
        }

        var baselineReady = _baselineSamples >= BaselineTargetSamples && _baselineOpeningRatio > 0.0001d;
        var closure = baselineReady
            ? Math.Clamp((_baselineOpeningRatio - opening) / _baselineOpeningRatio * 100d, 0d, 100d)
            : (double?)null;
        var composite = CalculateCompositeCue(closure, quality, sampleEligible);

        if (baselineReady && sampleEligible && composite < 12d && opening > _baselineOpeningRatio)
        {
            _baselineOpeningRatio = _baselineOpeningRatio * 0.98d + opening * 0.02d;
        }

        return new EyeInsetCueAnalysis
        {
            HasMeasurement = true,
            BaselineReady = baselineReady,
            BaselineSamples = _baselineSamples,
            CueEligible = baselineReady && sampleEligible,
            QualityPercent = quality,
            OpeningRatio = opening,
            BaselineOpeningRatio = baselineReady ? _baselineOpeningRatio : null,
            EyeClosurePercent = closure,
            CompositeCuePercent = composite
        };
    }

    private static double CalculateQualityPercent(EyeInsetApertureAnalysis analysis)
    {
        if (!analysis.HasMeasurement)
        {
            return 0d;
        }

        var confidence = Math.Clamp(analysis.MeasurementConfidence, 0d, 1d) * 70d;
        var contrast = Math.Clamp(analysis.ContrastPercent, 0d, 100d) * 0.14d;
        var sharpness = Math.Clamp(analysis.SharpnessPercent, 0d, 100d) * 0.10d;
        var darkCoverage = Math.Clamp(analysis.DarkCoveragePercent, 0d, 100d) * 0.10d;
        var glarePenalty = Math.Clamp(analysis.GlarePercent - 18d, 0d, 60d) * 0.08d;
        var dualEyeBonus = analysis.LeftEyeOpeningRatio.HasValue && analysis.RightEyeOpeningRatio.HasValue ? 6d : 0d;
        return Math.Clamp(confidence + contrast + sharpness + darkCoverage + dualEyeBonus - glarePenalty, 0d, 100d);
    }

    private static double CalculateCompositeCue(double? closure, double quality, bool sampleEligible)
    {
        if (!sampleEligible || closure is not double closurePercent)
        {
            return 0d;
        }

        var qualityMultiplier = quality < 50d ? 0.72d : quality < 65d ? 0.88d : 1d;
        return Math.Clamp(closurePercent * 1.10d * qualityMultiplier, 0d, 100d);
    }
}
