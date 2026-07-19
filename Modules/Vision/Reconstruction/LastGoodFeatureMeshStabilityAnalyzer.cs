using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public static class LastGoodFeatureMeshStabilityAnalyzer
{
    private const int MinimumUsefulSamples = 3;
    private const double StableDriftPercent = 4.5d;
    private const double ReviewDriftPercent = 8.0d;
    private const double HoldDriftPercent = 13.0d;
    private const double MinimumUsefulYawRangeDegrees = 14d;
    private const double UsefulSingleSideYawDegrees = 7d;
    private const double MinimumUsefulARangeDegrees = 8d;
    private const double UsefulSingleSideADegrees = 4d;
    private const double MinimumUsefulCRangeDegrees = 8d;
    private const double UsefulSingleSideCDegrees = 4d;
    private const double MinimumUsefulZScaleRangePercent = 8d;
    private const double UsefulSingleSideZScalePercent = 3d;

    private static readonly int[] EyeA =
    [
        33, 246, 161, 160, 159, 158, 157, 173, 133, 155, 154, 153, 145, 144, 163, 7
    ];

    private static readonly int[] EyeB =
    [
        362, 398, 384, 385, 386, 387, 388, 466, 263, 249, 390, 373, 374, 380, 381, 382
    ];

    private static readonly int[] JawCenter =
    [
        152, 148, 176, 149, 150, 377, 400, 378, 379
    ];

    public static LastGoodFeatureMeshStabilityReport Analyze(IReadOnlyList<LastGoodFeatureMeshSample> samples)
    {
        var report = new LastGoodFeatureMeshStabilityReport
        {
            SampleCount = samples.Count
        };

        if (samples.Count == 0)
        {
            report.HealthPercent = 0d;
            report.Status = "waiting for dense mesh samples";
            report.YawStatus = "waiting for dense mesh samples";
            report.AStatus = "waiting for dense mesh samples";
            report.CStatus = "waiting for dense mesh samples";
            report.ZStatus = "waiting for dense mesh samples";
            report.Findings.Add("No good dense mesh samples have been retained yet.");
            report.YawFindings.Add("No good dense mesh samples have been retained yet.");
            report.AFindings.Add("No good dense mesh samples have been retained yet.");
            report.CFindings.Add("No good dense mesh samples have been retained yet.");
            report.ZFindings.Add("No good dense mesh samples have been retained yet.");
            return report;
        }

        var normalizedSamples = samples
            .Select(TryNormalizeSample)
            .Where(static sample => sample is not null)
            .Select(static sample => sample!)
            .ToList();
        report.HeadLockedSampleCount = normalizedSamples.Count;

        if (normalizedSamples.Count < MinimumUsefulSamples)
        {
            report.HealthPercent = normalizedSamples.Count == 0 ? 0d : 58d;
            report.Status = $"warming up head-locked stability ({normalizedSamples.Count}/{MinimumUsefulSamples} samples)";
            report.Findings.Add($"Need at least {MinimumUsefulSamples} head-lockable samples before feature sliding can be scored.");
            report.Features = BuildFeatureRows(normalizedSamples);
            report.ComparedFeatureCount = report.Features.Count;
            report.WorstFeatureDriftPercent = report.Features.Count == 0 ? 0d : Round(report.Features.Max(static feature => feature.MaximumDriftPercent));
            PopulateYawStability(report, normalizedSamples);
            PopulateAStability(report, normalizedSamples);
            PopulateCStability(report, normalizedSamples);
            PopulateZStability(report, normalizedSamples);
            return report;
        }

        report.Features = BuildFeatureRows(normalizedSamples);
        report.ComparedFeatureCount = report.Features.Count;
        report.WorstFeatureDriftPercent = report.Features.Count == 0 ? 0d : Round(report.Features.Max(static feature => feature.MaximumDriftPercent));
        PopulateYawStability(report, normalizedSamples);
        PopulateAStability(report, normalizedSamples);
        PopulateCStability(report, normalizedSamples);
        PopulateZStability(report, normalizedSamples);
        report.HealthPercent = ScoreHealth(report.WorstFeatureDriftPercent, report.ComparedFeatureCount);
        report.Status = report.HealthPercent >= 82d
            ? "head-locked features are stable"
            : report.HealthPercent >= 62d
                ? "review head-locked feature drift"
                : "feature sliding likely; review before trusting avatar data";

        foreach (var feature in report.Features.Where(static feature => feature.MaximumDriftPercent >= ReviewDriftPercent))
        {
            report.Findings.Add($"{feature.Label} drifted {feature.MaximumDriftPercent:0.#}% in head-locked coordinates; verify it stays attached to the head during turns.");
        }

        if (report.Findings.Count == 0)
        {
            report.Findings.Add("Head-locked feature centers stayed within the current drift tolerance across the retained samples.");
        }

        return report;
    }

    private static void PopulateYawStability(
        LastGoodFeatureMeshStabilityReport report,
        IReadOnlyList<NormalizedSample> normalizedSamples)
    {
        report.YawLeftSampleCount = normalizedSamples.Count(static sample => sample.YawDegrees <= -UsefulSingleSideYawDegrees);
        report.YawRightSampleCount = normalizedSamples.Count(static sample => sample.YawDegrees >= UsefulSingleSideYawDegrees);
        report.YawRangeDegrees = normalizedSamples.Count == 0
            ? 0d
            : Round(normalizedSamples.Max(static sample => sample.YawDegrees) - normalizedSamples.Min(static sample => sample.YawDegrees));

        if (normalizedSamples.Count < MinimumUsefulSamples)
        {
            report.YawHealthPercent = normalizedSamples.Count == 0 ? 0d : 45d;
            report.YawStatus = $"warming up B head-turn lock ({normalizedSamples.Count}/{MinimumUsefulSamples} samples)";
            report.YawFindings.Add($"Need at least {MinimumUsefulSamples} head-lockable samples before left/right head turns can be scored.");
            return;
        }

        if (report.YawRangeDegrees < MinimumUsefulYawRangeDegrees)
        {
            report.YawHealthPercent = 0d;
            report.YawStatus = "waiting for left/right head-turn samples";
            report.YawFindings.Add($"B range is {report.YawRangeDegrees:0.#} deg; slowly look left and right until the recent sample range reaches about {MinimumUsefulYawRangeDegrees:0.#} deg.");
            return;
        }

        var yawDrift = CalculateAxisDrift(SelectYawStabilitySamples(normalizedSamples));
        report.YawComparedFeatureCount = yawDrift.ComparedFeatureCount;
        report.YawWorstFeatureDriftPercent = yawDrift.WorstDriftPercent;
        report.YawHealthPercent = ScoreHealth(report.YawWorstFeatureDriftPercent, report.YawComparedFeatureCount);
        if (report.YawLeftSampleCount == 0 || report.YawRightSampleCount == 0)
        {
            report.YawHealthPercent = Math.Min(report.YawHealthPercent, 72d);
            report.YawFindings.Add("B-axis range exists, but the retained samples do not yet cover both left and right turns.");
        }

        report.YawStatus = report.YawHealthPercent >= 82d
            ? "B head-turn lock is stable"
            : report.YawHealthPercent >= 62d
                ? "review B head-turn lock"
                : "B-axis feature sliding likely; review before trusting avatar data";

        foreach (var feature in yawDrift.Features.Where(static feature => feature.MaximumDriftPercent >= ReviewDriftPercent))
        {
            report.YawFindings.Add($"{feature.Label} drifted {feature.MaximumDriftPercent:0.#}% while B range was {report.YawRangeDegrees:0.#} deg; verify it rotates with the head instead of sliding across the face.");
        }

        if (report.YawFindings.Count == 0)
        {
            report.YawFindings.Add("During recent left/right head turns, feature centers stayed attached in head-locked coordinates.");
        }
    }

    private static void PopulateAStability(
        LastGoodFeatureMeshStabilityReport report,
        IReadOnlyList<NormalizedSample> normalizedSamples)
    {
        report.ANegativeSampleCount = normalizedSamples.Count(static sample => sample.PitchDegrees <= -UsefulSingleSideADegrees);
        report.APositiveSampleCount = normalizedSamples.Count(static sample => sample.PitchDegrees >= UsefulSingleSideADegrees);
        report.ARangeDegrees = normalizedSamples.Count == 0
            ? 0d
            : Round(normalizedSamples.Max(static sample => sample.PitchDegrees) - normalizedSamples.Min(static sample => sample.PitchDegrees));

        if (normalizedSamples.Count < MinimumUsefulSamples)
        {
            report.AHealthPercent = normalizedSamples.Count == 0 ? 0d : 45d;
            report.AStatus = $"warming up A-axis tilt lock ({normalizedSamples.Count}/{MinimumUsefulSamples} samples)";
            report.AFindings.Add($"Need at least {MinimumUsefulSamples} head-lockable samples before A-axis tilt can be scored.");
            return;
        }

        if (report.ARangeDegrees < MinimumUsefulARangeDegrees)
        {
            report.AHealthPercent = 0d;
            report.AStatus = "waiting for A-axis tilt samples";
            report.AFindings.Add($"A range is {report.ARangeDegrees:0.#} deg; slowly tilt around X until the recent sample range reaches about {MinimumUsefulARangeDegrees:0.#} deg.");
            return;
        }

        var aDrift = CalculateAxisDrift(SelectAStabilitySamples(normalizedSamples));
        report.AComparedFeatureCount = aDrift.ComparedFeatureCount;
        report.AWorstFeatureDriftPercent = aDrift.WorstDriftPercent;
        report.AHealthPercent = ScoreHealth(report.AWorstFeatureDriftPercent, report.AComparedFeatureCount);
        if (report.ANegativeSampleCount == 0 || report.APositiveSampleCount == 0)
        {
            report.AHealthPercent = Math.Min(report.AHealthPercent, 72d);
            report.AFindings.Add("A-axis range exists, but retained samples do not yet cover both negative and positive A tilt.");
        }

        report.AStatus = report.AHealthPercent >= 82d
            ? "A-axis tilt lock is stable"
            : report.AHealthPercent >= 62d
                ? "review A-axis tilt lock"
                : "A-axis feature sliding likely; review before trusting avatar data";

        foreach (var feature in aDrift.Features.Where(static feature => feature.MaximumDriftPercent >= ReviewDriftPercent))
        {
            report.AFindings.Add($"{feature.Label} drifted {feature.MaximumDriftPercent:0.#}% while A range was {report.ARangeDegrees:0.#} deg; verify it rotates with the head instead of sliding across the face.");
        }

        if (report.AFindings.Count == 0)
        {
            report.AFindings.Add("During recent A-axis tilts, feature centers stayed attached in head-locked coordinates.");
        }
    }

    private static void PopulateCStability(
        LastGoodFeatureMeshStabilityReport report,
        IReadOnlyList<NormalizedSample> normalizedSamples)
    {
        report.CNegativeSampleCount = normalizedSamples.Count(static sample => sample.RollDegrees <= -UsefulSingleSideCDegrees);
        report.CPositiveSampleCount = normalizedSamples.Count(static sample => sample.RollDegrees >= UsefulSingleSideCDegrees);
        report.CRangeDegrees = normalizedSamples.Count == 0
            ? 0d
            : Round(normalizedSamples.Max(static sample => sample.RollDegrees) - normalizedSamples.Min(static sample => sample.RollDegrees));

        if (normalizedSamples.Count < MinimumUsefulSamples)
        {
            report.CHealthPercent = normalizedSamples.Count == 0 ? 0d : 45d;
            report.CStatus = $"warming up C-axis tilt lock ({normalizedSamples.Count}/{MinimumUsefulSamples} samples)";
            report.CFindings.Add($"Need at least {MinimumUsefulSamples} head-lockable samples before C-axis tilt can be scored.");
            return;
        }

        if (report.CRangeDegrees < MinimumUsefulCRangeDegrees)
        {
            report.CHealthPercent = 0d;
            report.CStatus = "waiting for C-axis tilt samples";
            report.CFindings.Add($"C range is {report.CRangeDegrees:0.#} deg; gently tilt around Z until the recent sample range reaches about {MinimumUsefulCRangeDegrees:0.#} deg.");
            return;
        }

        var cDrift = CalculateAxisDrift(SelectCStabilitySamples(normalizedSamples));
        report.CComparedFeatureCount = cDrift.ComparedFeatureCount;
        report.CWorstFeatureDriftPercent = cDrift.WorstDriftPercent;
        report.CHealthPercent = ScoreHealth(report.CWorstFeatureDriftPercent, report.CComparedFeatureCount);
        if (report.CNegativeSampleCount == 0 || report.CPositiveSampleCount == 0)
        {
            report.CHealthPercent = Math.Min(report.CHealthPercent, 72d);
            report.CFindings.Add("C-axis range exists, but retained samples do not yet cover both negative and positive C tilt.");
        }

        report.CStatus = report.CHealthPercent >= 82d
            ? "C-axis tilt lock is stable"
            : report.CHealthPercent >= 62d
                ? "review C-axis tilt lock"
                : "C-axis feature sliding likely; review before trusting avatar data";

        foreach (var feature in cDrift.Features.Where(static feature => feature.MaximumDriftPercent >= ReviewDriftPercent))
        {
            report.CFindings.Add($"{feature.Label} drifted {feature.MaximumDriftPercent:0.#}% while C range was {report.CRangeDegrees:0.#} deg; verify it rotates with the head instead of sliding across the face.");
        }

        if (report.CFindings.Count == 0)
        {
            report.CFindings.Add("During recent C-axis tilts, feature centers stayed attached in head-locked coordinates.");
        }
    }

    private static void PopulateZStability(
        LastGoodFeatureMeshStabilityReport report,
        IReadOnlyList<NormalizedSample> normalizedSamples)
    {
        if (normalizedSamples.Count == 0)
        {
            report.ZHealthPercent = 0d;
            report.ZStatus = "waiting for Z distance-change samples";
            report.ZFindings.Add("Need head-lockable samples before Z distance changes can be scored.");
            return;
        }

        var averageScale = normalizedSamples.Average(static sample => sample.FaceScale);
        if (averageScale <= 0d)
        {
            report.ZHealthPercent = 0d;
            report.ZStatus = "waiting for usable Z face scale";
            report.ZFindings.Add("The recent samples did not expose a usable face scale for Z distance review.");
            return;
        }

        report.ZFaceScaleRangePercent = Round(
            (normalizedSamples.Max(static sample => sample.FaceScale) - normalizedSamples.Min(static sample => sample.FaceScale))
            / averageScale
            * 100d);
        report.ZCloseSampleCount = normalizedSamples.Count(sample => sample.FaceScale >= averageScale * (1d + UsefulSingleSideZScalePercent / 100d));
        report.ZFarSampleCount = normalizedSamples.Count(sample => sample.FaceScale <= averageScale * (1d - UsefulSingleSideZScalePercent / 100d));

        if (normalizedSamples.Count < MinimumUsefulSamples)
        {
            report.ZHealthPercent = normalizedSamples.Count == 0 ? 0d : 45d;
            report.ZStatus = $"warming up Z distance lock ({normalizedSamples.Count}/{MinimumUsefulSamples} samples)";
            report.ZFindings.Add($"Need at least {MinimumUsefulSamples} head-lockable samples before Z distance changes can be scored.");
            return;
        }

        if (report.ZFaceScaleRangePercent < MinimumUsefulZScaleRangePercent)
        {
            report.ZHealthPercent = 0d;
            report.ZStatus = "waiting for Z distance-change samples";
            report.ZFindings.Add($"Z face-scale range is {report.ZFaceScaleRangePercent:0.#}%; lean closer and farther until recent face scale changes by about {MinimumUsefulZScaleRangePercent:0.#}%.");
            return;
        }

        var zDrift = CalculateAxisDrift(SelectZStabilitySamples(normalizedSamples, averageScale));
        report.ZComparedFeatureCount = zDrift.ComparedFeatureCount;
        report.ZWorstFeatureDriftPercent = zDrift.WorstDriftPercent;
        report.ZHealthPercent = ScoreHealth(report.ZWorstFeatureDriftPercent, report.ZComparedFeatureCount);
        if (report.ZCloseSampleCount == 0 || report.ZFarSampleCount == 0)
        {
            report.ZHealthPercent = Math.Min(report.ZHealthPercent, 72d);
            report.ZFindings.Add("Z face-scale range exists, but retained samples do not yet cover both closer and farther positions.");
        }

        report.ZStatus = report.ZHealthPercent >= 82d
            ? "Z distance lock is stable"
            : report.ZHealthPercent >= 62d
                ? "review Z distance lock"
                : "Z distance feature sliding likely; review before trusting avatar data";

        foreach (var feature in zDrift.Features.Where(static feature => feature.MaximumDriftPercent >= ReviewDriftPercent))
        {
            report.ZFindings.Add($"{feature.Label} drifted {feature.MaximumDriftPercent:0.#}% while Z face-scale range was {report.ZFaceScaleRangePercent:0.#}%; verify closer/farther motion is not reshaping the face.");
        }

        if (report.ZFindings.Count == 0)
        {
            report.ZFindings.Add("During recent closer/farther samples, feature centers stayed attached after head-scale normalization.");
        }
    }

    private static AxisDrift CalculateAxisDrift(IReadOnlyList<NormalizedSample> axisSamples)
    {
        if (axisSamples.Count < 2)
        {
            return AxisDrift.None;
        }

        var features = BuildFeatureRows(axisSamples);
        var worstDrift = features.Count == 0
            ? 0d
            : Round(features.Max(static feature => feature.MaximumDriftPercent));
        return new AxisDrift(features.Count, worstDrift, features);
    }

    private static IReadOnlyList<NormalizedSample> SelectYawStabilitySamples(
        IReadOnlyList<NormalizedSample> normalizedSamples)
    {
        var averageScale = AverageFaceScale(normalizedSamples);
        return normalizedSamples
            .Where(sample => Math.Abs(sample.YawDegrees) >= UsefulSingleSideYawDegrees
                || IsNeutralReferenceSample(sample, averageScale))
            .ToList();
    }

    private static IReadOnlyList<NormalizedSample> SelectAStabilitySamples(
        IReadOnlyList<NormalizedSample> normalizedSamples)
    {
        var averageScale = AverageFaceScale(normalizedSamples);
        return normalizedSamples
            .Where(sample => Math.Abs(sample.PitchDegrees) >= UsefulSingleSideADegrees
                || IsNeutralReferenceSample(sample, averageScale))
            .ToList();
    }

    private static IReadOnlyList<NormalizedSample> SelectCStabilitySamples(
        IReadOnlyList<NormalizedSample> normalizedSamples)
    {
        var averageScale = AverageFaceScale(normalizedSamples);
        return normalizedSamples
            .Where(sample => Math.Abs(sample.RollDegrees) >= UsefulSingleSideCDegrees
                || IsNeutralReferenceSample(sample, averageScale))
            .ToList();
    }

    private static IReadOnlyList<NormalizedSample> SelectZStabilitySamples(
        IReadOnlyList<NormalizedSample> normalizedSamples,
        double averageScale)
    {
        return normalizedSamples
            .Where(sample => IsNeutralRotationSample(sample)
                && (FaceScaleDeltaPercent(sample, averageScale) >= UsefulSingleSideZScalePercent
                    || IsNeutralScaleSample(sample, averageScale)))
            .ToList();
    }

    private static double AverageFaceScale(IReadOnlyList<NormalizedSample> normalizedSamples)
    {
        return normalizedSamples.Count == 0
            ? 0d
            : normalizedSamples.Average(static sample => sample.FaceScale);
    }

    private static bool IsNeutralReferenceSample(NormalizedSample sample, double averageScale)
    {
        return IsNeutralRotationSample(sample) && IsNeutralScaleSample(sample, averageScale);
    }

    private static bool IsNeutralRotationSample(NormalizedSample sample)
    {
        return Math.Abs(sample.YawDegrees) < UsefulSingleSideYawDegrees
            && Math.Abs(sample.PitchDegrees) < UsefulSingleSideADegrees
            && Math.Abs(sample.RollDegrees) < UsefulSingleSideCDegrees;
    }

    private static bool IsNeutralScaleSample(NormalizedSample sample, double averageScale)
    {
        return FaceScaleDeltaPercent(sample, averageScale) < UsefulSingleSideZScalePercent;
    }

    private static double FaceScaleDeltaPercent(NormalizedSample sample, double averageScale)
    {
        return averageScale <= 0d
            ? 0d
            : Math.Abs(sample.FaceScale - averageScale) / averageScale * 100d;
    }

    private static List<LastGoodFeatureMeshFeatureStability> BuildFeatureRows(IReadOnlyList<NormalizedSample> samples)
    {
        var featureIds = samples
            .SelectMany(static sample => sample.Features.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rows = new List<LastGoodFeatureMeshFeatureStability>();
        foreach (var featureId in featureIds)
        {
            var centers = samples
                .Select(sample => sample.Features.TryGetValue(featureId, out var feature) ? feature : null)
                .Where(static feature => feature is not null)
                .Select(static feature => feature!)
                .ToList();
            if (centers.Count == 0)
            {
                continue;
            }

            var average = new MeshPoint3D(
                centers.Average(static feature => feature.Center.X),
                centers.Average(static feature => feature.Center.Y),
                centers.Average(static feature => feature.Center.Z));
            var drifts = centers
                .Select(feature => Distance(feature.Center, average) * 100d)
                .ToList();
            var maximumDrift = Round(drifts.Max());
            rows.Add(new LastGoodFeatureMeshFeatureStability
            {
                FeatureId = featureId,
                Label = centers[0].Label,
                Role = centers[0].Role,
                SampleCount = centers.Count,
                AverageX = Round(average.X),
                AverageY = Round(average.Y),
                AverageZ = Round(average.Z),
                AverageDriftPercent = Round(drifts.Average()),
                MaximumDriftPercent = maximumDrift,
                Status = DriftStatus(maximumDrift)
            });
        }

        return rows
            .OrderByDescending(static row => row.MaximumDriftPercent)
            .ThenBy(static row => row.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static NormalizedSample? TryNormalizeSample(LastGoodFeatureMeshSample sample)
    {
        var rawPoints = sample.Points.ToDictionary(static point => point.Index, ToMeshPoint);
        if (!TryGetFeatureCenter(sample, rawPoints, "left_eye", EyeA, out var leftEye)
            || !TryGetFeatureCenter(sample, rawPoints, "right_eye", EyeB, out var rightEye)
            || !TryGetFeatureCenter(sample, rawPoints, "jaw", JawCenter, out var chin))
        {
            return null;
        }

        var eyeMid = Multiply(Add(leftEye, rightEye), 0.5d);
        var xAxis = Subtract(rightEye, leftEye);
        var interEyeDistance = Length(xAxis);
        if (interEyeDistance < 0.0001d)
        {
            return null;
        }

        xAxis = Normalize(xAxis);
        var yAxis = Subtract(chin, eyeMid);
        yAxis = Subtract(yAxis, Multiply(xAxis, Dot(yAxis, xAxis)));
        if (Length(yAxis) < 0.0001d)
        {
            return null;
        }

        yAxis = Normalize(yAxis);
        var zAxis = Normalize(Cross(xAxis, yAxis));
        if (Length(zAxis) < 0.0001d)
        {
            return null;
        }

        yAxis = Normalize(Cross(zAxis, xAxis));
        var faceHeight = Distance(eyeMid, chin);
        var faceScale = Math.Max(0.0001d, Math.Max(interEyeDistance * 2.35d, faceHeight * 1.28d));
        var featureCenters = new Dictionary<string, NormalizedFeature>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in sample.FeatureGroups)
        {
            if (string.IsNullOrWhiteSpace(group.Id)
                || !TryGetCenter(rawPoints, group.LandmarkIndices, out var center))
            {
                continue;
            }

            var relative = Subtract(center, eyeMid);
            featureCenters[group.Id] = new NormalizedFeature(
                group.Id,
                group.Label,
                group.Role,
                new MeshPoint3D(
                    Dot(relative, xAxis) / faceScale,
                    Dot(relative, yAxis) / faceScale,
                    Dot(relative, zAxis) / faceScale));
        }

        return new NormalizedSample(
            sample.SampleId,
            sample.HeadYawDegrees,
            sample.HeadPitchDegrees,
            sample.HeadRollDegrees,
            faceScale,
            featureCenters);
    }

    private static bool TryGetFeatureCenter(
        LastGoodFeatureMeshSample sample,
        IReadOnlyDictionary<int, MeshPoint3D> points,
        string featureGroupId,
        IReadOnlyList<int> fallbackIndices,
        out MeshPoint3D center)
    {
        var group = sample.FeatureGroups.FirstOrDefault(group =>
            string.Equals(group.Id, featureGroupId, StringComparison.OrdinalIgnoreCase));
        if (group is not null && TryGetCenter(points, group.LandmarkIndices, out center))
        {
            return true;
        }

        return TryGetCenter(points, fallbackIndices, out center);
    }

    private static bool TryGetCenter(
        IReadOnlyDictionary<int, MeshPoint3D> points,
        IReadOnlyList<int> indices,
        out MeshPoint3D center)
    {
        var values = indices
            .Where(points.ContainsKey)
            .Select(index => points[index])
            .ToList();
        if (values.Count == 0)
        {
            center = default;
            return false;
        }

        center = new MeshPoint3D(
            values.Average(static point => point.X),
            values.Average(static point => point.Y),
            values.Average(static point => point.Z));
        return true;
    }

    private static double ScoreHealth(double worstFeatureDriftPercent, int comparedFeatureCount)
    {
        if (comparedFeatureCount <= 0)
        {
            return 0d;
        }

        if (worstFeatureDriftPercent <= StableDriftPercent)
        {
            return Round(Math.Clamp(100d - worstFeatureDriftPercent * 2.6d, 86d, 100d));
        }

        if (worstFeatureDriftPercent <= ReviewDriftPercent)
        {
            return Round(86d - (worstFeatureDriftPercent - StableDriftPercent) / (ReviewDriftPercent - StableDriftPercent) * 24d);
        }

        if (worstFeatureDriftPercent <= HoldDriftPercent)
        {
            return Round(62d - (worstFeatureDriftPercent - ReviewDriftPercent) / (HoldDriftPercent - ReviewDriftPercent) * 32d);
        }

        return Round(Math.Clamp(30d - (worstFeatureDriftPercent - HoldDriftPercent) * 2d, 5d, 30d));
    }

    private static string DriftStatus(double maximumDriftPercent)
    {
        if (maximumDriftPercent < StableDriftPercent)
        {
            return "stable";
        }

        if (maximumDriftPercent < ReviewDriftPercent)
        {
            return "warming";
        }

        if (maximumDriftPercent < HoldDriftPercent)
        {
            return "review";
        }

        return "sliding";
    }

    private static MeshPoint3D ToMeshPoint(FaceMeshLandmarkPoint point)
    {
        return new MeshPoint3D(point.X, point.Y, point.Z);
    }

    private static MeshPoint3D Add(MeshPoint3D first, MeshPoint3D second)
    {
        return new MeshPoint3D(first.X + second.X, first.Y + second.Y, first.Z + second.Z);
    }

    private static MeshPoint3D Subtract(MeshPoint3D first, MeshPoint3D second)
    {
        return new MeshPoint3D(first.X - second.X, first.Y - second.Y, first.Z - second.Z);
    }

    private static MeshPoint3D Multiply(MeshPoint3D point, double scale)
    {
        return new MeshPoint3D(point.X * scale, point.Y * scale, point.Z * scale);
    }

    private static double Dot(MeshPoint3D first, MeshPoint3D second)
    {
        return first.X * second.X + first.Y * second.Y + first.Z * second.Z;
    }

    private static MeshPoint3D Cross(MeshPoint3D first, MeshPoint3D second)
    {
        return new MeshPoint3D(
            first.Y * second.Z - first.Z * second.Y,
            first.Z * second.X - first.X * second.Z,
            first.X * second.Y - first.Y * second.X);
    }

    private static double Length(MeshPoint3D point)
    {
        return Math.Sqrt(Dot(point, point));
    }

    private static double Distance(MeshPoint3D first, MeshPoint3D second)
    {
        return Length(Subtract(first, second));
    }

    private static MeshPoint3D Normalize(MeshPoint3D point)
    {
        var length = Math.Max(0.000001d, Length(point));
        return Multiply(point, 1d / length);
    }

    private static double Round(double value)
    {
        return double.IsFinite(value) ? Math.Round(value, 6, MidpointRounding.AwayFromZero) : 0d;
    }

    private readonly record struct MeshPoint3D(double X, double Y, double Z);

    private sealed record NormalizedSample(
        string SampleId,
        double YawDegrees,
        double PitchDegrees,
        double RollDegrees,
        double FaceScale,
        IReadOnlyDictionary<string, NormalizedFeature> Features);

    private sealed record NormalizedFeature(
        string FeatureId,
        string Label,
        string Role,
        MeshPoint3D Center);

    private sealed record AxisDrift(
        int ComparedFeatureCount,
        double WorstDriftPercent,
        IReadOnlyList<LastGoodFeatureMeshFeatureStability> Features)
    {
        public static AxisDrift None { get; } = new(0, 0d, []);
    }
}
