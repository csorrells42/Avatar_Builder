using AvatarBuilder.Modules.Vision.Common;
namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceLandmarkTrendAnalyzer
{
    private const int MinimumSamples = 6;
    private const double MinimumWindowSeconds = 4d;
    private static readonly TimeSpan TrendWindow = TimeSpan.FromSeconds(30);
    private readonly Queue<Sample> _samples = new();

    public void Reset()
    {
        _samples.Clear();
    }

    public FaceLandmarkTrendAnalysis Update(FaceLandmarkMetrics metrics)
    {
        if (!metrics.HasFace)
        {
            Reset();
            return FaceLandmarkTrendAnalysis.Waiting;
        }

        var hasEye = metrics.IsEyeMeasurementUsable;
        var hasMouth = metrics.IsMouthMeasurementUsable;
        if (hasEye || hasMouth)
        {
            _samples.Enqueue(new Sample(
                metrics.CapturedAtUtc,
                hasEye ? metrics.AverageEyeOpeningRatio : null,
                hasMouth ? metrics.MouthOpeningRatio : null,
                metrics.EyeMeasurementQualityPercent,
                metrics.MouthMeasurementQualityPercent,
                metrics.OverallMeasurementQualityPercent));
        }

        Trim(metrics.CapturedAtUtc);
        if (_samples.Count < MinimumSamples)
        {
            return CreateWarmingAnalysis();
        }

        var elapsed = (_samples.Last().CapturedAtUtc - _samples.First().CapturedAtUtc).TotalSeconds;
        if (elapsed < MinimumWindowSeconds)
        {
            return CreateWarmingAnalysis();
        }

        var eyeSamples = _samples
            .Where(static sample => sample.EyeOpeningRatio.HasValue)
            .Select(static sample => (sample.CapturedAtUtc, Value: sample.EyeOpeningRatio!.Value, sample.EyeQualityPercent))
            .ToList();
        var mouthSamples = _samples
            .Where(static sample => sample.MouthOpeningRatio.HasValue)
            .Select(static sample => (sample.CapturedAtUtc, Value: sample.MouthOpeningRatio!.Value, sample.MouthQualityPercent))
            .ToList();

        var eyeTrend = CalculateTrend(eyeSamples, closingTrend: true);
        var mouthTrend = CalculateTrend(mouthSamples, closingTrend: false);
        var averageQuality = _samples.Average(static sample => sample.OverallQualityPercent);
        var eyeEligible = eyeTrend.IsUsable && eyeTrend.QualityPercent >= 45d;
        var mouthEligible = mouthTrend.IsUsable && mouthTrend.QualityPercent >= 40d;
        var cue = CalculateTrendCue(eyeEligible ? eyeTrend.ChangePercent : null, mouthEligible ? mouthTrend.ChangePercent : null, averageQuality);

        return new FaceLandmarkTrendAnalysis
        {
            HasUsableTrend = eyeEligible || mouthEligible,
            SampleCount = _samples.Count,
            WindowSeconds = elapsed,
            QualityPercent = Math.Clamp(averageQuality, 0d, 100d),
            EyeTrendEligible = eyeEligible,
            MouthTrendEligible = mouthEligible,
            EyeOpeningStartRatio = eyeTrend.StartValue,
            EyeOpeningEndRatio = eyeTrend.EndValue,
            EyeOpeningSlopePerSecond = eyeTrend.SlopePerSecond,
            EyeClosingTrendPercent = eyeEligible ? eyeTrend.ChangePercent : null,
            MouthOpeningStartRatio = mouthTrend.StartValue,
            MouthOpeningEndRatio = mouthTrend.EndValue,
            MouthOpeningSlopePerSecond = mouthTrend.SlopePerSecond,
            MouthOpeningTrendPercent = mouthEligible ? mouthTrend.ChangePercent : null,
            TrendCuePercent = cue
        };
    }

    private void Trim(DateTime capturedAtUtc)
    {
        while (_samples.Count > 0 && capturedAtUtc - _samples.Peek().CapturedAtUtc > TrendWindow)
        {
            _samples.Dequeue();
        }
    }

    private FaceLandmarkTrendAnalysis CreateWarmingAnalysis()
    {
        if (_samples.Count == 0)
        {
            return FaceLandmarkTrendAnalysis.Waiting;
        }

        return new FaceLandmarkTrendAnalysis
        {
            SampleCount = _samples.Count,
            WindowSeconds = (_samples.Last().CapturedAtUtc - _samples.First().CapturedAtUtc).TotalSeconds,
            QualityPercent = Math.Clamp(_samples.Average(static sample => sample.OverallQualityPercent), 0d, 100d)
        };
    }

    private static TrendResult CalculateTrend(
        IReadOnlyList<(DateTime CapturedAtUtc, double Value, double QualityPercent)> samples,
        bool closingTrend)
    {
        if (samples.Count < MinimumSamples)
        {
            return TrendResult.Empty;
        }

        var firstTime = samples[0].CapturedAtUtc;
        var timed = samples
            .Select(sample => ((sample.CapturedAtUtc - firstTime).TotalSeconds, sample.Value, sample.QualityPercent))
            .ToList();
        var windowSeconds = timed[^1].Item1 - timed[0].Item1;
        if (windowSeconds < MinimumWindowSeconds)
        {
            return TrendResult.Empty;
        }

        var slope = SlopePerSecond(timed.Select(static sample => (sample.Item1, sample.Value)));
        if (slope is not double slopeValue)
        {
            return TrendResult.Empty;
        }

        var start = timed.Take(Math.Min(3, timed.Count)).Average(static sample => sample.Value);
        var end = timed.TakeLast(Math.Min(3, timed.Count)).Average(static sample => sample.Value);
        var rawChange = closingTrend
            ? start - end
            : end - start;
        var changePercent = Math.Clamp(rawChange / Math.Max(start, 0.015d) * 100d, 0d, 100d);
        if (closingTrend && slopeValue >= -0.001d)
        {
            changePercent = 0d;
        }
        else if (!closingTrend && slopeValue <= 0.001d)
        {
            changePercent = 0d;
        }

        return new TrendResult(
            IsUsable: true,
            StartValue: start,
            EndValue: end,
            SlopePerSecond: slopeValue,
            ChangePercent: changePercent,
            QualityPercent: timed.Average(static sample => sample.QualityPercent));
    }

    private static double CalculateTrendCue(double? eyeClosingTrendPercent, double? mouthOpeningTrendPercent, double quality)
    {
        var eye = eyeClosingTrendPercent.GetValueOrDefault();
        var mouth = mouthOpeningTrendPercent.GetValueOrDefault();
        var raw = eye * 0.82d + mouth * 0.18d;
        var multiplier = quality < 35d ? 0.55d : quality < 50d ? 0.78d : 1d;
        return Math.Clamp(raw * multiplier, 0d, 100d);
    }

    private static double? SlopePerSecond(IEnumerable<(double Seconds, double Value)> samples)
    {
        var values = samples.ToList();
        if (values.Count < 2)
        {
            return null;
        }

        var slopes = new List<double>();
        for (var first = 0; first < values.Count; first++)
        {
            for (var second = first + 1; second < values.Count; second++)
            {
                var elapsed = values[second].Seconds - values[first].Seconds;
                if (elapsed > 0.000001d)
                {
                    slopes.Add((values[second].Value - values[first].Value) / elapsed);
                }
            }
        }

        if (slopes.Count == 0)
        {
            return null;
        }

        slopes.Sort();
        var middle = slopes.Count / 2;
        return slopes.Count % 2 == 1
            ? slopes[middle]
            : (slopes[middle - 1] + slopes[middle]) / 2d;
    }

    private sealed record Sample(
        DateTime CapturedAtUtc,
        double? EyeOpeningRatio,
        double? MouthOpeningRatio,
        double EyeQualityPercent,
        double MouthQualityPercent,
        double OverallQualityPercent);

    private sealed record TrendResult(
        bool IsUsable,
        double? StartValue,
        double? EndValue,
        double? SlopePerSecond,
        double? ChangePercent,
        double QualityPercent)
    {
        public static TrendResult Empty { get; } = new(false, null, null, null, null, 0d);
    }
}
