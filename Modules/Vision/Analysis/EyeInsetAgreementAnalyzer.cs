namespace AvatarBuilder.Modules.Vision.Analysis;

public static class EyeInsetAgreementAnalyzer
{
    public static EyeInsetAgreementAnalysis Analyze(IEnumerable<EyeInsetAgreementSample> samples)
    {
        var sampleList = samples.ToList();
        var paired = sampleList
            .Where(static sample => sample.FullFrameEyeOpening.HasValue && sample.EyeInsetOpening.HasValue)
            .Select(static sample => (
                sample.TimestampSeconds,
                FullFrame: sample.FullFrameEyeOpening!.Value,
                EyeInset: sample.EyeInsetOpening!.Value))
            .OrderBy(static sample => sample.TimestampSeconds)
            .ToList();
        var fullFrameSlope = SlopePerSecond(sampleList.Select(static sample => (sample.TimestampSeconds, sample.FullFrameEyeOpening)));
        var eyeInsetSlope = SlopePerSecond(sampleList.Select(static sample => (sample.TimestampSeconds, sample.EyeInsetOpening)));
        var pairedRate = Rate(paired.Count, sampleList.Count);
        var openingCorrelation = PearsonCorrelation(paired);
        var normalizedError = NormalizedMeanAbsoluteError(paired);
        var directionAgreement = DirectionAgreement(paired);
        var slopeDirectionAgreement = SlopeDirectionAgreement(fullFrameSlope, eyeInsetSlope);
        return new EyeInsetAgreementAnalysis
        {
            PairedSamples = paired.Count,
            PairedRate = pairedRate,
            OpeningCorrelation = openingCorrelation,
            NormalizedMeanAbsoluteError = normalizedError,
            DirectionAgreement = directionAgreement,
            SlopeDirectionAgreement = slopeDirectionAgreement,
            AgreementTrustPercent = ScoreAgreementTrust(
                paired.Count,
                pairedRate,
                openingCorrelation,
                normalizedError,
                directionAgreement,
                slopeDirectionAgreement)
        };
    }

    public static double? SlopePerSecond(IEnumerable<(double TimestampSeconds, double? Value)> samples)
    {
        var valid = samples
            .Where(static sample => sample.Value.HasValue)
            .Select(static sample => (sample.TimestampSeconds, Value: sample.Value!.Value))
            .ToList();
        if (valid.Count < 2)
        {
            return null;
        }

        var slopes = new List<double>();
        for (var first = 0; first < valid.Count; first++)
        {
            for (var second = first + 1; second < valid.Count; second++)
            {
                var elapsed = valid[second].TimestampSeconds - valid[first].TimestampSeconds;
                if (elapsed > 0.000001d)
                {
                    slopes.Add((valid[second].Value - valid[first].Value) / elapsed);
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

    private static double? PearsonCorrelation(IReadOnlyList<(double TimestampSeconds, double FullFrame, double EyeInset)> samples)
    {
        if (samples.Count < 3)
        {
            return null;
        }

        var fullAverage = samples.Average(static sample => sample.FullFrame);
        var insetAverage = samples.Average(static sample => sample.EyeInset);
        var numerator = 0d;
        var fullVariance = 0d;
        var insetVariance = 0d;
        foreach (var sample in samples)
        {
            var fullDelta = sample.FullFrame - fullAverage;
            var insetDelta = sample.EyeInset - insetAverage;
            numerator += fullDelta * insetDelta;
            fullVariance += fullDelta * fullDelta;
            insetVariance += insetDelta * insetDelta;
        }

        var denominator = Math.Sqrt(fullVariance * insetVariance);
        return denominator <= 0.0000001d ? null : Math.Clamp(numerator / denominator, -1d, 1d);
    }

    private static double? NormalizedMeanAbsoluteError(IReadOnlyList<(double TimestampSeconds, double FullFrame, double EyeInset)> samples)
    {
        if (samples.Count < 2)
        {
            return null;
        }

        var fullMinimum = samples.Min(static sample => sample.FullFrame);
        var fullRange = samples.Max(static sample => sample.FullFrame) - fullMinimum;
        var insetMinimum = samples.Min(static sample => sample.EyeInset);
        var insetRange = samples.Max(static sample => sample.EyeInset) - insetMinimum;
        if (fullRange <= 0.000001d || insetRange <= 0.000001d)
        {
            return null;
        }

        return samples.Average(sample =>
        {
            var normalizedFull = (sample.FullFrame - fullMinimum) / fullRange;
            var normalizedInset = (sample.EyeInset - insetMinimum) / insetRange;
            return Math.Abs(normalizedFull - normalizedInset);
        });
    }

    private static double? DirectionAgreement(IReadOnlyList<(double TimestampSeconds, double FullFrame, double EyeInset)> samples)
    {
        if (samples.Count < 3)
        {
            return null;
        }

        var agreeing = 0;
        var compared = 0;
        for (var index = 1; index < samples.Count; index++)
        {
            var fullDelta = samples[index].FullFrame - samples[index - 1].FullFrame;
            var insetDelta = samples[index].EyeInset - samples[index - 1].EyeInset;
            if (Math.Abs(fullDelta) < 0.00001d && Math.Abs(insetDelta) < 0.00001d)
            {
                continue;
            }

            compared++;
            if (Math.Sign(fullDelta) == Math.Sign(insetDelta))
            {
                agreeing++;
            }
        }

        return compared == 0 ? null : agreeing / (double)compared;
    }

    private static double? SlopeDirectionAgreement(double? fullFrameSlope, double? insetSlope)
    {
        if (fullFrameSlope is not double full || insetSlope is not double inset)
        {
            return null;
        }

        const double epsilon = 0.0005d;
        if (Math.Abs(full) < epsilon || Math.Abs(inset) < epsilon)
        {
            return null;
        }

        return Math.Sign(full) == Math.Sign(inset) ? 1d : 0d;
    }

    private static double ScoreAgreementTrust(
        int pairedSamples,
        double pairedRate,
        double? openingCorrelation,
        double? normalizedMeanAbsoluteError,
        double? directionAgreement,
        double? slopeDirectionAgreement)
    {
        if (pairedSamples <= 0 || pairedRate <= 0d)
        {
            return 0d;
        }

        var pairedScore = Math.Clamp(pairedRate / 0.70d * 100d, 0d, 100d);
        var correlationScore = openingCorrelation is double correlation
            ? Math.Clamp((correlation - 0.20d) / 0.80d * 100d, 0d, 100d)
            : pairedSamples >= 6 ? 45d : 20d;
        var errorScore = normalizedMeanAbsoluteError is double error
            ? Math.Clamp((0.55d - error) / 0.55d * 100d, 0d, 100d)
            : pairedSamples >= 6 ? 45d : 20d;
        var directionScore = directionAgreement is double direction
            ? Math.Clamp(direction * 100d, 0d, 100d)
            : pairedSamples >= 6 ? 45d : 20d;
        var slopeScore = slopeDirectionAgreement is double slope
            ? Math.Clamp(slope * 100d, 0d, 100d)
            : 50d;

        return Round(
            pairedScore * 0.24d
            + correlationScore * 0.22d
            + errorScore * 0.22d
            + directionScore * 0.20d
            + slopeScore * 0.12d);
    }

    private static double Rate(int count, int total)
    {
        return total <= 0 ? 0d : count / (double)total;
    }

    private static double Round(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value)
            ? 0d
            : Math.Round(value, 6, MidpointRounding.AwayFromZero);
    }
}

public sealed record EyeInsetAgreementSample(
    double TimestampSeconds,
    double? FullFrameEyeOpening,
    double? EyeInsetOpening);

public sealed class EyeInsetAgreementAnalysis
{
    public int PairedSamples { get; init; }

    public double PairedRate { get; init; }

    public double? OpeningCorrelation { get; init; }

    public double? NormalizedMeanAbsoluteError { get; init; }

    public double? DirectionAgreement { get; init; }

    public double? SlopeDirectionAgreement { get; init; }

    public double AgreementTrustPercent { get; init; }
}
