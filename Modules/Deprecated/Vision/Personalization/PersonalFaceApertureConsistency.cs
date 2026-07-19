namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceApertureConsistencyReport
{
    public string SchemaVersion { get; set; } = "personal-face-aperture-consistency-v1";

    public double HealthPercent { get; set; } = 65d;

    public double EyeApertureHealthPercent { get; set; } = 65d;

    public double MouthApertureHealthPercent { get; set; } = 65d;

    public double JawDroopAgreementHealthPercent { get; set; } = 65d;

    public int EyeComparedSampleCount { get; set; }

    public int MouthComparedSampleCount { get; set; }

    public int JawComparedSampleCount { get; set; }

    public double? EyeOpeningBlinkCorrelation { get; set; }

    public double? MouthOpeningEvidenceCorrelation { get; set; }

    public double? JawDroopEvidenceCorrelation { get; set; }

    public double? EyeOpeningRange { get; set; }

    public double? MediaPipeBlinkRangePercent { get; set; }

    public double? MouthOpeningRange { get; set; }

    public double? JawDroopRange { get; set; }

    public double? MediaPipeMouthOpenEvidenceRangePercent { get; set; }

    public double EyeMediaPipeCorrectionRate { get; set; }

    public double MouthMediaPipeCorrectionRate { get; set; }

    public double EyeArtifactRate { get; set; }

    public double EyeReconstructedRate { get; set; }

    public double MouthReconstructedRate { get; set; }

    public string Status { get; set; } = "waiting for aperture corroboration samples";

    public List<string> Findings { get; set; } = [];
}

public static class PersonalFaceApertureConsistencyAnalyzer
{
    private const int MinimumComparedSamples = 12;
    private const double UsefulEyeOpeningRange = 0.045d;
    private const double UsefulMouthOpeningRange = 0.050d;
    private const double UsefulJawDroopRange = 0.040d;
    private const double UsefulBlendshapeRangePercent = 18d;

    public static PersonalFaceApertureConsistencyReport Analyze(
        IReadOnlyList<PersonalFaceMeasurementSample>? recentSamples)
    {
        var report = new PersonalFaceApertureConsistencyReport();
        var samples = (recentSamples ?? [])
            .Where(static sample => sample.CaptureQualityCanCollect)
            .ToList();
        if (samples.Count == 0)
        {
            return report;
        }

        AnalyzeEyes(report, samples);
        AnalyzeMouth(report, samples);
        AnalyzeJaw(report, samples);
        report.EyeMediaPipeCorrectionRate = Rate(samples.Count(static sample => sample.MediaPipeEyeOpeningCorrected), samples.Count);
        report.MouthMediaPipeCorrectionRate = Rate(samples.Count(static sample => sample.MediaPipeMouthOpeningCorrected), samples.Count);
        report.EyeArtifactRate = Rate(samples.Count(static sample => sample.EyeArtifactSuppressed || sample.PossibleOneEyeArtifact), samples.Count);
        report.EyeReconstructedRate = Rate(samples.Count(static sample => sample.LeftEyeReconstructed || sample.RightEyeReconstructed), samples.Count);
        report.MouthReconstructedRate = Rate(samples.Count(static sample => sample.MouthReconstructed), samples.Count);

        report.HealthPercent = Round(Average(
            report.EyeApertureHealthPercent,
            report.MouthApertureHealthPercent,
            report.JawDroopAgreementHealthPercent));
        report.Status = report.HealthPercent switch
        {
            >= 85d => "aperture corroboration strong",
            >= 70d => "aperture corroboration usable",
            >= 55d => "aperture corroboration warming",
            _ => "review aperture corroboration"
        };
        return report;
    }

    private static void AnalyzeEyes(
        PersonalFaceApertureConsistencyReport report,
        IReadOnlyList<PersonalFaceMeasurementSample> samples)
    {
        var pairs = samples
            .Where(static sample => sample.AverageEyeOpeningRatio.HasValue && sample.MediaPipeAverageEyeBlinkPercent.HasValue)
            .Select(static sample => new AperturePair(
                sample.AverageEyeOpeningRatio!.Value,
                sample.MediaPipeAverageEyeBlinkPercent!.Value,
                sample.EyeQualityPercent))
            .ToList();
        report.EyeComparedSampleCount = pairs.Count;
        report.EyeOpeningRange = Range(pairs.Select(static pair => pair.Aperture));
        report.MediaPipeBlinkRangePercent = Range(pairs.Select(static pair => pair.Corroboration));
        if (pairs.Count < MinimumComparedSamples)
        {
            report.EyeApertureHealthPercent = 58d;
            report.Findings.Add($"eye aperture corroboration waiting: {pairs.Count}/{MinimumComparedSamples} recent collectable samples have both eyelid opening and MediaPipe blink.");
            return;
        }

        var correlation = Correlation(pairs.Select(static pair => pair.Aperture), pairs.Select(static pair => pair.Corroboration));
        report.EyeOpeningBlinkCorrelation = Round(correlation);
        report.EyeApertureHealthPercent = ScoreAgreement(
            expectedNegativeCorrelation: true,
            correlation,
            report.EyeOpeningRange,
            UsefulEyeOpeningRange,
            report.MediaPipeBlinkRangePercent,
            UsefulBlendshapeRangePercent,
            pairs.Average(static pair => pair.QualityPercent));

        if (correlation > 0.20d && report.MediaPipeBlinkRangePercent >= UsefulBlendshapeRangePercent)
        {
            report.Findings.Add($"eye aperture and MediaPipe blink are moving in the same direction (correlation {correlation:0.##}); behind-glasses eyelid aperture may be unreliable.");
        }
        else if (report.EyeApertureHealthPercent < 55d)
        {
            report.Findings.Add($"eye aperture corroboration is weak ({report.EyeApertureHealthPercent:0.#}%); collect a glare-controlled slow-blink pass before trusting eyelid closure for avatar fitting.");
        }
    }

    private static void AnalyzeMouth(
        PersonalFaceApertureConsistencyReport report,
        IReadOnlyList<PersonalFaceMeasurementSample> samples)
    {
        var pairs = samples
            .Select(static sample => new
            {
                sample.MouthOpeningRatio,
                Evidence = MouthOpenEvidence(sample),
                sample.MouthQualityPercent
            })
            .Where(static sample => sample.MouthOpeningRatio.HasValue && sample.Evidence.HasValue)
            .Select(static sample => new AperturePair(sample.MouthOpeningRatio!.Value, sample.Evidence!.Value, sample.MouthQualityPercent))
            .ToList();
        report.MouthComparedSampleCount = pairs.Count;
        report.MouthOpeningRange = Range(pairs.Select(static pair => pair.Aperture));
        report.MediaPipeMouthOpenEvidenceRangePercent = Range(pairs.Select(static pair => pair.Corroboration));
        if (pairs.Count < MinimumComparedSamples)
        {
            report.MouthApertureHealthPercent = 58d;
            report.Findings.Add($"mouth aperture corroboration waiting: {pairs.Count}/{MinimumComparedSamples} recent collectable samples have both lip opening and dense mouth evidence.");
            return;
        }

        var correlation = Correlation(pairs.Select(static pair => pair.Aperture), pairs.Select(static pair => pair.Corroboration));
        report.MouthOpeningEvidenceCorrelation = Round(correlation);
        report.MouthApertureHealthPercent = ScoreAgreement(
            expectedNegativeCorrelation: false,
            correlation,
            report.MouthOpeningRange,
            UsefulMouthOpeningRange,
            report.MediaPipeMouthOpenEvidenceRangePercent,
            UsefulBlendshapeRangePercent,
            pairs.Average(static pair => pair.QualityPercent));

        if (correlation < -0.20d && report.MediaPipeMouthOpenEvidenceRangePercent >= UsefulBlendshapeRangePercent)
        {
            report.Findings.Add($"mouth aperture and dense mouth evidence disagree (correlation {correlation:0.##}); verify the tracker is locked on the lips and not the area under the nose.");
        }
        else if (report.MouthApertureHealthPercent < 55d)
        {
            report.Findings.Add($"mouth aperture corroboration is weak ({report.MouthApertureHealthPercent:0.#}%); collect a short closed-mouth, speech, and slight-jaw-drop pass with the lower face visible.");
        }
    }

    private static void AnalyzeJaw(
        PersonalFaceApertureConsistencyReport report,
        IReadOnlyList<PersonalFaceMeasurementSample> samples)
    {
        var pairs = samples
            .Where(static sample => sample.JawDroopRatio.HasValue && sample.MediaPipeJawOpenPercent.HasValue)
            .Select(static sample => new AperturePair(
                sample.JawDroopRatio!.Value,
                sample.MediaPipeJawOpenPercent!.Value,
                sample.MouthQualityPercent))
            .ToList();
        report.JawComparedSampleCount = pairs.Count;
        report.JawDroopRange = Range(pairs.Select(static pair => pair.Aperture));
        if (pairs.Count < MinimumComparedSamples)
        {
            report.JawDroopAgreementHealthPercent = 58d;
            report.Findings.Add($"jaw droop corroboration waiting: {pairs.Count}/{MinimumComparedSamples} recent collectable samples have both jaw droop and MediaPipe jaw-open evidence.");
            return;
        }

        var jawEvidenceRange = Range(pairs.Select(static pair => pair.Corroboration));
        var correlation = Correlation(pairs.Select(static pair => pair.Aperture), pairs.Select(static pair => pair.Corroboration));
        report.JawDroopEvidenceCorrelation = Round(correlation);
        report.JawDroopAgreementHealthPercent = ScoreAgreement(
            expectedNegativeCorrelation: false,
            correlation,
            report.JawDroopRange,
            UsefulJawDroopRange,
            jawEvidenceRange,
            UsefulBlendshapeRangePercent,
            pairs.Average(static pair => pair.QualityPercent));

        if (correlation < -0.20d && jawEvidenceRange >= UsefulBlendshapeRangePercent)
        {
            report.Findings.Add($"jaw droop and MediaPipe jaw-open disagree (correlation {correlation:0.##}); verify jaw contour scale before using it as motion evidence.");
        }
    }

    private static double ScoreAgreement(
        bool expectedNegativeCorrelation,
        double correlation,
        double? apertureRange,
        double usefulApertureRange,
        double? corroborationRange,
        double usefulCorroborationRange,
        double averageQualityPercent)
    {
        var expected = expectedNegativeCorrelation ? -correlation : correlation;
        var directionScore = Math.Clamp((expected + 1d) / 2d * 100d, 0d, 100d);
        var apertureRangeScore = apertureRange is double aperture
            ? Math.Clamp(aperture / usefulApertureRange * 100d, 0d, 100d)
            : 0d;
        var corroborationRangeScore = corroborationRange is double corroboration
            ? Math.Clamp(corroboration / usefulCorroborationRange * 100d, 0d, 100d)
            : 0d;
        var rangeScore = Math.Max(apertureRangeScore, corroborationRangeScore);
        return Round(Math.Clamp(
            directionScore * 0.56d
            + rangeScore * 0.18d
            + Math.Clamp(averageQualityPercent, 0d, 100d) * 0.26d,
            0d,
            100d));
    }

    private static double? MouthOpenEvidence(PersonalFaceMeasurementSample sample)
    {
        if (sample.MediaPipeJawOpenPercent is double jawOpen)
        {
            return Math.Clamp(jawOpen, 0d, 100d);
        }

        if (sample.MediaPipeMouthClosePercent is double mouthClose && mouthClose >= 35d)
        {
            return Math.Clamp(100d - mouthClose, 0d, 100d);
        }

        return null;
    }

    private static double Correlation(IEnumerable<double> first, IEnumerable<double> second)
    {
        var pairs = first.Zip(second, static (x, y) => (X: x, Y: y)).ToList();
        if (pairs.Count < 2)
        {
            return 0d;
        }

        var averageX = pairs.Average(static pair => pair.X);
        var averageY = pairs.Average(static pair => pair.Y);
        var numerator = pairs.Sum(pair => (pair.X - averageX) * (pair.Y - averageY));
        var sumX = pairs.Sum(pair => Math.Pow(pair.X - averageX, 2d));
        var sumY = pairs.Sum(pair => Math.Pow(pair.Y - averageY, 2d));
        var denominator = Math.Sqrt(sumX * sumY);
        return denominator <= 0.0000001d
            ? 0d
            : Math.Clamp(numerator / denominator, -1d, 1d);
    }

    private static double? Range(IEnumerable<double> values)
    {
        var numbers = values.ToList();
        return numbers.Count == 0 ? null : Round(numbers.Max() - numbers.Min());
    }

    private static double Average(params double[] values)
    {
        return values.Length == 0 ? 0d : values.Average();
    }

    private static double Rate(int count, int total)
    {
        return total <= 0 ? 0d : Round(count / (double)total);
    }

    private static double Round(double value)
    {
        return double.IsNaN(value) || double.IsInfinity(value)
            ? 0d
            : Math.Round(value, 6, MidpointRounding.AwayFromZero);
    }

    private sealed record AperturePair(double Aperture, double Corroboration, double QualityPercent);
}
