namespace AvatarBuilder.Modules.Vision.Personalization;

public static class PersonalFaceIdentityAnalyzer
{
    public const int MinimumSamplesForReport = 20;
    public const int MinimumSamplesForWarmupStrongMismatchGate = 80;
    public const int MinimumSamplesForAutomaticGate = 240;

    private const int MinimumComparableFeatures = 5;
    private const int MinimumWarmupStrongMismatchComparableFeatures = 7;
    private const double MinimumWarmupStrongMismatchConfidencePercent = 24d;
    private const int MinimumWarmupStrongMismatchOutlierFeatures = 6;
    private const double MinimumAutoGateConfidencePercent = 28d;
    private const int MinimumAutoGateOutlierFeatures = 4;

    public static PersonalFaceIdentityAnalysis Analyze(
        PersonalFaceModel model,
        PersonalFaceIdentityMeasurement measurement)
    {
        if (!measurement.HasMeasurement)
        {
            return new PersonalFaceIdentityAnalysis
            {
                Accepted = true,
                Reason = "identity measurement unavailable"
            };
        }

        var analysis = new PersonalFaceIdentityAnalysis
        {
            HasMeasurement = true,
            WarmupStrongMismatchGateReady = model.AcceptedSamples >= MinimumSamplesForWarmupStrongMismatchGate
                && model.IdentitySignatureSamples >= MinimumSamplesForWarmupStrongMismatchGate,
            AutoGateReady = model.AcceptedSamples >= MinimumSamplesForAutomaticGate
                && model.IdentitySignatureSamples >= MinimumSamplesForReport
        };

        AddFeature(analysis, "Face aspect", measurement.FaceAspectRatio, model.FaceAspectRatio, 0.16d);
        AddFeature(analysis, "Eye horizontal position", measurement.EyeMidlineXToFaceWidth, model.EyeMidlineXToFaceWidth, 0.075d);
        AddFeature(analysis, "Mouth horizontal position", measurement.MouthCenterXToFaceWidth, model.MouthCenterXToFaceWidth, 0.085d);
        AddFeature(analysis, "Eye-to-mouth horizontal offset", measurement.EyeToMouthXOffsetToFaceWidth, model.EyeToMouthXOffsetToFaceWidth, 0.055d);
        AddFeature(analysis, "Eye spacing / face width", measurement.InterEyeDistanceToFaceWidth, model.InterEyeDistanceToFaceWidth, 0.055d);
        AddFeature(analysis, "Left eye width / face width", measurement.LeftEyeWidthToFaceWidth, model.LeftEyeWidthToFaceWidth, 0.035d);
        AddFeature(analysis, "Right eye width / face width", measurement.RightEyeWidthToFaceWidth, model.RightEyeWidthToFaceWidth, 0.035d);
        AddFeature(analysis, "Mouth width / face width", measurement.MouthWidthToFaceWidth, model.MouthWidthToFaceWidth, 0.055d);
        AddFeature(analysis, "Eye vertical position", measurement.EyeMidlineYToFaceHeight, model.EyeMidlineYToFaceHeight, 0.060d);
        AddFeature(analysis, "Mouth vertical position", measurement.MouthCenterYToFaceHeight, model.MouthCenterYToFaceHeight, 0.070d);
        AddFeature(analysis, "Eye-to-mouth vertical span", measurement.EyeToMouthYDistanceToFaceHeight, model.EyeToMouthYDistanceToFaceHeight, 0.070d);

        analysis.ComparedFeatureCount = analysis.FeatureScores.Count;
        analysis.OutlierFeatureCount = analysis.FeatureScores.Count(static score => score.IsOutlier);
        analysis.ConfidencePercent = CalculateConfidencePercent(analysis.FeatureScores);

        if (model.AcceptedSamples < MinimumSamplesForReport || analysis.ComparedFeatureCount < MinimumComparableFeatures)
        {
            analysis.AutoGateReady = false;
            analysis.Accepted = true;
            analysis.Reason = "identity signature warming";
            return analysis;
        }

        if (analysis.AutoGateReady
            && analysis.ConfidencePercent < MinimumAutoGateConfidencePercent
            && analysis.OutlierFeatureCount >= MinimumAutoGateOutlierFeatures)
        {
            analysis.Accepted = false;
            analysis.Reason = $"likely non-subject geometry; identity confidence {analysis.ConfidencePercent:0}% with {analysis.OutlierFeatureCount} outlier feature(s)";
            return analysis;
        }

        if (!analysis.AutoGateReady
            && analysis.WarmupStrongMismatchGateReady
            && analysis.ComparedFeatureCount >= MinimumWarmupStrongMismatchComparableFeatures
            && analysis.ConfidencePercent < MinimumWarmupStrongMismatchConfidencePercent
            && analysis.OutlierFeatureCount >= MinimumWarmupStrongMismatchOutlierFeatures)
        {
            analysis.Accepted = false;
            analysis.Reason = $"strong non-subject geometry during identity warmup; identity confidence {analysis.ConfidencePercent:0}% with {analysis.OutlierFeatureCount} outlier feature(s)";
            return analysis;
        }

        analysis.Accepted = true;
        analysis.Reason = analysis.AutoGateReady
            ? $"identity confidence {analysis.ConfidencePercent:0}%"
            : "identity signature report-only until strong baseline";
        return analysis;
    }

    private static void AddFeature(
        PersonalFaceIdentityAnalysis analysis,
        string name,
        double? value,
        PersonalMetricDistribution distribution,
        double fallbackTolerance)
    {
        if (value is not double current || distribution.Average is not double average || distribution.SampleCount < 8)
        {
            return;
        }

        var standardDeviation = distribution.StandardDeviation.GetValueOrDefault();
        var tolerance = Math.Max(fallbackTolerance, standardDeviation * 3d);
        var distance = Math.Abs(current - average);
        var confidence = Math.Clamp(100d - distance / tolerance * 100d, 0d, 100d);
        var normalLow = distribution.NormalLow;
        var normalHigh = distribution.NormalHigh;
        var outsideNormal = normalLow is double low && current < low - fallbackTolerance * 0.35d
            || normalHigh is double high && current > high + fallbackTolerance * 0.35d;
        var isOutlier = confidence < 24d && outsideNormal;
        analysis.FeatureScores.Add(new PersonalFaceIdentityFeatureScore
        {
            Name = name,
            Value = current,
            BaselineAverage = average,
            NormalLow = normalLow,
            NormalHigh = normalHigh,
            ConfidencePercent = Math.Round(confidence, 6, MidpointRounding.AwayFromZero),
            IsOutlier = isOutlier
        });
    }

    private static double CalculateConfidencePercent(IReadOnlyCollection<PersonalFaceIdentityFeatureScore> scores)
    {
        if (scores.Count == 0)
        {
            return 0d;
        }

        var average = scores.Average(static score => score.ConfidencePercent);
        var outlierRate = scores.Count(static score => score.IsOutlier) / (double)scores.Count;
        var outlierLimited = Math.Clamp(100d - outlierRate * 150d, 0d, 100d);
        return Math.Round(Math.Min(average, outlierLimited), 6, MidpointRounding.AwayFromZero);
    }
}
