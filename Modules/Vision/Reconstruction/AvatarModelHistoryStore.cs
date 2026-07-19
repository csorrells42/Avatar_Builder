using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarModelHistoryStore
{
    public const string JsonLinesFileName = "avatar_model_history.jsonl";
    public const string LatestJsonFileName = "avatar_model_history_latest.json";
    public const string RecentJsonFileName = "avatar_model_history_recent.json";
    public const string HtmlFileName = "avatar_model_regression.html";

    private const int RecentEntryCount = 240;
    private const int MaxHistoryEntryCount = 86_400;
    private const int RebuildsPerCompactionCheck = 2_880;
    private const double MatureModelSampleCount = 8d;
    private const double ModelMovementWarningPercent = 2d;
    private const double RegionMovementWarningPercent = 3d;
    private const double SingleSampleMovementWarningPercent = 1.5d;
    private const double ObservationOutlierWarningPercent = 10d;
    private const double ShapeCoefficientMovementWarningPercent = 5d;

    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions JsonLineOptions = new(JsonSerializerDefaults.Web);

    public AvatarModelHistoryReport RecordAndWrite(
        string folder,
        AvatarModelObservationSet observationSet,
        AvatarModel currentModel,
        AvatarModel? previousModel)
    {
        Directory.CreateDirectory(folder);
        var previousEntry = ReadLatest(GetLatestJsonPath(folder));
        var entry = BuildEntry(observationSet, currentModel, previousModel, previousEntry);

        AppendHistoryEntry(GetJsonLinesPath(folder), entry);
        AtomicTextFileWriter.WriteAllText(
            GetLatestJsonPath(folder),
            JsonSerializer.Serialize(entry, JsonOptions),
            Encoding.UTF8);

        var recentEntries = ReadRecent(GetRecentJsonPath(folder));
        recentEntries.Add(entry);
        recentEntries = recentEntries
            .OrderBy(static item => item.RebuildNumber)
            .TakeLast(RecentEntryCount)
            .ToList();
        AtomicTextFileWriter.WriteAllText(
            GetRecentJsonPath(folder),
            JsonSerializer.Serialize(recentEntries, JsonOptions),
            Encoding.UTF8);

        var report = new AvatarModelHistoryReport
        {
            CreatedAtUtc = DateTime.UtcNow,
            Latest = entry,
            RecentEntries = recentEntries
        };
        AtomicTextFileWriter.WriteAllText(GetHtmlPath(folder), BuildHtml(report), Encoding.UTF8);

        if (entry.RebuildNumber > 0 && entry.RebuildNumber % RebuildsPerCompactionCheck == 0)
        {
            CompactHistory(GetJsonLinesPath(folder), entry.EvaluatedAtUtc);
        }

        return report;
    }

    public AvatarModelHistoryReport ReadReport(string folder)
    {
        return new AvatarModelHistoryReport
        {
            CreatedAtUtc = DateTime.UtcNow,
            Latest = ReadLatest(GetLatestJsonPath(folder)) ?? new AvatarModelHistoryEntry(),
            RecentEntries = ReadRecent(GetRecentJsonPath(folder))
        };
    }

    public static string GetJsonLinesPath(string folder)
    {
        return Path.Combine(folder, JsonLinesFileName);
    }

    public static string GetLatestJsonPath(string folder)
    {
        return Path.Combine(folder, LatestJsonFileName);
    }

    public static string GetRecentJsonPath(string folder)
    {
        return Path.Combine(folder, RecentJsonFileName);
    }

    public static string GetHtmlPath(string folder)
    {
        return Path.Combine(folder, HtmlFileName);
    }

    private static AvatarModelHistoryEntry BuildEntry(
        AvatarModelObservationSet observationSet,
        AvatarModel current,
        AvatarModel? previous,
        AvatarModelHistoryEntry? previousEntry)
    {
        var sampleDelta = previous is null ? current.Identity.SampleCount : current.Identity.SampleCount - previous.Identity.SampleCount;
        var latestPreviousObservationUtc = previousEntry?.LatestObservationCapturedAtUtc ?? DateTime.MinValue;
        var newObservationCount = previousEntry is null
            ? observationSet.Observations.Count
            : observationSet.Observations.Count(observation => observation.CapturedAtUtc > latestPreviousObservationUtc);
        var latestObservationUtc = observationSet.Observations.Count == 0
            ? latestPreviousObservationUtc
            : observationSet.Observations.Max(static observation => observation.CapturedAtUtc);
        var confidenceDelta = Delta(current.Identity.ConfidencePercent, previous?.Identity.ConfidencePercent);
        var coverageDelta = Delta(current.PoseCoverage.CoveragePercent, previous?.PoseCoverage.CoveragePercent);
        var stabilityDelta = Delta(current.Identity.ShapeCoefficientStabilityPercent, previous?.Identity.ShapeCoefficientStabilityPercent);
        var overallMovement = previous is null
            ? 0d
            : CalculateVertexRmsPercent(current.Identity.MeanDenseVertices, previous.Identity.MeanDenseVertices);
        var regionMovement = previous is null
            ? CreateEmptyRegionMovement()
            : CalculateRegionMovement(current.Identity.MeanDenseVertices, previous.Identity.MeanDenseVertices);
        var shapeCoefficientDelta = previous is null
            ? 0d
            : CalculateCoefficientRelativeRmsPercent(current.Identity.MeanShapeCoefficients, previous.Identity.MeanShapeCoefficients);
        var expressionRange = MeanExpressionRange(current);
        var expressionRangeDelta = previous is null ? 0d : expressionRange - MeanExpressionRange(previous);
        var regionConfidence = CalculateRegionConfidenceDeltas(current, previous);

        var warningBearingCount = observationSet.Observations.Count(static observation => observation.Warnings.Count > 0);
        var downweightedCount = observationSet.Observations.Count(static observation =>
            observation.ExpressionWeightPercent - observation.IdentityWeightPercent > 0.1d);
        var excludedCount = observationSet.Observations.Count(static observation => observation.IdentityWeightPercent <= 0.001d);
        var outlierAudit = current.Identity.SampleCount >= MatureModelSampleCount
            ? AuditObservationGeometry(observationSet.Observations, current.Identity.MeanDenseVertices)
            : ObservationGeometryAudit.Empty;

        var warnings = BuildWarnings(
            current,
            previous,
            newObservationCount,
            confidenceDelta,
            coverageDelta,
            stabilityDelta,
            overallMovement,
            regionMovement,
            shapeCoefficientDelta,
            outlierAudit);
        var status = previous is null
            ? "baseline recorded"
            : warnings.Count > 0
                ? "review recommended"
                : newObservationCount > 0 && (coverageDelta > 0.01d || confidenceDelta > 0.25d || stabilityDelta > 0.25d)
                    ? "improving"
                    : newObservationCount > 0 ? "learning within tolerance" : "stable";
        var summary = BuildSummary(status, sampleDelta, newObservationCount, overallMovement, warnings.Count);

        return new AvatarModelHistoryEntry
        {
            RebuildNumber = Math.Max(1, (previousEntry?.RebuildNumber ?? 0) + 1),
            EvaluatedAtUtc = DateTime.UtcNow,
            SubjectId = current.SubjectId,
            SubjectDisplayName = current.SubjectDisplayName,
            Status = status,
            Summary = summary,
            SampleCount = current.Identity.SampleCount,
            SampleCountDelta = sampleDelta,
            NewObservationCount = newObservationCount,
            LatestObservationCapturedAtUtc = latestObservationUtc,
            IdentityConfidencePercent = Round(current.Identity.ConfidencePercent),
            IdentityConfidenceDeltaPoints = Round(confidenceDelta),
            PoseCoveragePercent = Round(current.PoseCoverage.CoveragePercent),
            PoseCoverageDeltaPoints = Round(coverageDelta),
            ShapeStabilityPercent = Round(current.Identity.ShapeCoefficientStabilityPercent),
            ShapeStabilityDeltaPoints = Round(stabilityDelta),
            DenseVertexCount = current.Identity.DenseVertexCount,
            OverallVertexRmsFaceSpanPercent = Round(overallMovement),
            ShapeCoefficientRelativeRmsPercent = Round(shapeCoefficientDelta),
            MeanExpressionRange = Round(expressionRange),
            MeanExpressionRangeDelta = Round(expressionRangeDelta),
            WarningBearingObservationCount = warningBearingCount,
            DownweightedIdentityObservationCount = downweightedCount,
            ExcludedIdentityObservationCount = excludedCount,
            GeometryOutlierCandidateCount = outlierAudit.Count,
            HighestObservationRmsFaceSpanPercent = Round(outlierAudit.HighestRmsPercent),
            RegionMovement = regionMovement,
            RegionConfidence = regionConfidence,
            Warnings = warnings
        };
    }

    private static List<string> BuildWarnings(
        AvatarModel current,
        AvatarModel? previous,
        int newObservationCount,
        double confidenceDelta,
        double coverageDelta,
        double stabilityDelta,
        double overallMovement,
        IReadOnlyList<AvatarModelRegionMovement> regionMovement,
        double shapeCoefficientDelta,
        ObservationGeometryAudit outlierAudit)
    {
        if (previous is null)
        {
            return [];
        }

        var warnings = new List<string>();
        if (confidenceDelta <= -3d)
        {
            warnings.Add($"Identity confidence fell {Math.Abs(confidenceDelta):0.#} percentage points in one rebuild.");
        }

        if (coverageDelta < -0.01d)
        {
            warnings.Add($"Pose/depth coverage fell {Math.Abs(coverageDelta):0.#} percentage points; inspect retention-window rollover.");
        }

        if (stabilityDelta <= -5d)
        {
            warnings.Add($"Shape-coefficient stability fell {Math.Abs(stabilityDelta):0.#} percentage points.");
        }

        var mature = current.Identity.SampleCount >= MatureModelSampleCount;
        if (mature && overallMovement > ModelMovementWarningPercent)
        {
            warnings.Add($"The canonical mean face moved {overallMovement:0.###}% of face span in one rebuild.");
        }

        foreach (var region in regionMovement.Where(static region => region.RmsFaceSpanPercent > RegionMovementWarningPercent))
        {
            warnings.Add($"{region.Region} moved {region.RmsFaceSpanPercent:0.###}% of face span; review the newest reconstruction.");
        }

        if (mature && newObservationCount == 1 && overallMovement > SingleSampleMovementWarningPercent)
        {
            warnings.Add("One new observation moved the mature mean face more than the single-sample tolerance.");
        }

        if (newObservationCount == 0 && overallMovement > 0.05d)
        {
            warnings.Add("The model geometry changed without a new stored observation; this indicates a nondeterministic rebuild or data mismatch.");
        }

        if (shapeCoefficientDelta > ShapeCoefficientMovementWarningPercent && newObservationCount <= 1)
        {
            warnings.Add($"Shape coefficients shifted {shapeCoefficientDelta:0.###}% relative RMS in one rebuild.");
        }

        if (outlierAudit.Count > 0)
        {
            warnings.Add($"{outlierAudit.Count} stored observation(s) differ from the current mean by more than {ObservationOutlierWarningPercent:0.#}% of face span; these are review candidates, not automatic deletions.");
        }

        return warnings;
    }

    private static string BuildSummary(
        string status,
        int sampleDelta,
        int newObservationCount,
        double movement,
        int warningCount)
    {
        if (status == "baseline recorded")
        {
            return "First auditable model baseline recorded; future rebuilds will be compared against it.";
        }

        var sampleText = newObservationCount switch
        {
            > 0 => $"{newObservationCount} new observation(s)",
            _ => "no new observations"
        };
        var retentionText = sampleDelta == 0
            ? "retained count unchanged"
            : $"retained count {Signed(sampleDelta)}";
        var warningText = warningCount == 0 ? "no regression warnings" : $"{warningCount} review warning(s)";
        return $"{status}: {sampleText}, {retentionText}; mean-face movement {movement:0.###}% of face span; {warningText}.";
    }

    private static ObservationGeometryAudit AuditObservationGeometry(
        IReadOnlyList<AvatarModelObservation> observations,
        IReadOnlyList<FaceMeshLandmarkPoint> meanVertices)
    {
        if (meanVertices.Count == 0)
        {
            return ObservationGeometryAudit.Empty;
        }

        var outlierCount = 0;
        var highest = 0d;
        foreach (var observation in observations)
        {
            if (observation.IdentityWeightPercent <= 0.001d
                || (observation.CanonicalIdentityVertices.Count == 0 && observation.Vertices.Count == 0))
            {
                continue;
            }

            var normalized = AvatarModelBuilder.NormalizeIdentityVerticesForAudit(observation, meanVertices);
            var rms = CalculateVertexRmsPercent(normalized, meanVertices);
            highest = Math.Max(highest, rms);
            if (rms > ObservationOutlierWarningPercent)
            {
                outlierCount++;
            }
        }

        return new ObservationGeometryAudit(outlierCount, highest);
    }

    private static List<AvatarModelRegionMovement> CalculateRegionMovement(
        IReadOnlyList<FaceMeshLandmarkPoint> current,
        IReadOnlyList<FaceMeshLandmarkPoint> previous)
    {
        if (current.Count == 0 || previous.Count == 0)
        {
            return CreateEmptyRegionMovement();
        }

        var previousByIndex = previous.ToDictionary(static point => point.Index);
        var minX = current.Min(static point => point.X);
        var maxX = current.Max(static point => point.X);
        var minY = current.Min(static point => point.Y);
        var maxY = current.Max(static point => point.Y);
        var width = Math.Max(0.0001d, maxX - minX);
        var height = Math.Max(0.0001d, maxY - minY);
        var centerX = (minX + maxX) * 0.5d;
        var accumulators = new Dictionary<string, RmsAccumulator>(StringComparer.Ordinal)
        {
            ["Eyes"] = new(),
            ["Nose"] = new(),
            ["Mouth"] = new(),
            ["Chin and jaw"] = new()
        };

        foreach (var point in current)
        {
            if (!previousByIndex.TryGetValue(point.Index, out var oldPoint))
            {
                continue;
            }

            var y = (point.Y - minY) / height;
            var horizontal = Math.Abs(point.X - centerX) / width;
            var squaredDistance = SquaredDistance(point, oldPoint);
            if (y is >= 0.25d and <= 0.52d)
            {
                accumulators["Eyes"].Add(squaredDistance);
            }

            if (y is >= 0.34d and <= 0.69d && horizontal <= 0.22d)
            {
                accumulators["Nose"].Add(squaredDistance);
            }

            if (y is >= 0.58d and <= 0.82d && horizontal <= 0.34d)
            {
                accumulators["Mouth"].Add(squaredDistance);
            }

            if (y >= 0.72d)
            {
                accumulators["Chin and jaw"].Add(squaredDistance);
            }
        }

        return accumulators.Select(pair => new AvatarModelRegionMovement
        {
            Region = pair.Key,
            MatchedVertexCount = pair.Value.Count,
            RmsFaceSpanPercent = Round(pair.Value.Rms * 100d)
        }).ToList();
    }

    private static List<AvatarModelRegionMovement> CreateEmptyRegionMovement()
    {
        return [
            new AvatarModelRegionMovement { Region = "Eyes" },
            new AvatarModelRegionMovement { Region = "Nose" },
            new AvatarModelRegionMovement { Region = "Mouth" },
            new AvatarModelRegionMovement { Region = "Chin and jaw" }
        ];
    }

    private static List<AvatarModelRegionConfidenceDelta> CalculateRegionConfidenceDeltas(
        AvatarModel current,
        AvatarModel? previous)
    {
        var previousByRegion = previous?.Identity.RegionConfidence.ToDictionary(
            static region => region.Region,
            static region => region.ConfidencePercent,
            StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        return current.Identity.RegionConfidence.Select(region => new AvatarModelRegionConfidenceDelta
        {
            Region = region.Region,
            ConfidencePercent = Round(region.ConfidencePercent),
            DeltaPoints = previousByRegion.TryGetValue(region.Region, out var previousConfidence)
                ? Round(region.ConfidencePercent - previousConfidence)
                : 0d
        }).ToList();
    }

    private static double CalculateVertexRmsPercent(
        IReadOnlyList<FaceMeshLandmarkPoint> current,
        IReadOnlyList<FaceMeshLandmarkPoint> previous)
    {
        if (current.Count == 0 || previous.Count == 0)
        {
            return 0d;
        }

        var previousByIndex = previous.ToDictionary(static point => point.Index);
        var accumulator = new RmsAccumulator();
        foreach (var point in current)
        {
            if (previousByIndex.TryGetValue(point.Index, out var oldPoint))
            {
                accumulator.Add(SquaredDistance(point, oldPoint));
            }
        }

        return accumulator.Rms * 100d;
    }

    private static double SquaredDistance(FaceMeshLandmarkPoint current, FaceMeshLandmarkPoint previous)
    {
        var dx = current.X - previous.X;
        var dy = current.Y - previous.Y;
        var dz = current.Z - previous.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static double CalculateCoefficientRelativeRmsPercent(IReadOnlyList<double> current, IReadOnlyList<double> previous)
    {
        var count = Math.Min(current.Count, previous.Count);
        if (count == 0)
        {
            return 0d;
        }

        var deltaSquared = 0d;
        var currentSquared = 0d;
        var previousSquared = 0d;
        for (var index = 0; index < count; index++)
        {
            var delta = current[index] - previous[index];
            deltaSquared += delta * delta;
            currentSquared += current[index] * current[index];
            previousSquared += previous[index] * previous[index];
        }

        var scale = Math.Max(Math.Sqrt(currentSquared / count), Math.Sqrt(previousSquared / count));
        return scale <= double.Epsilon
            ? 0d
            : Math.Sqrt(deltaSquared / count) / scale * 100d;
    }

    private static double MeanExpressionRange(AvatarModel model)
    {
        return model.Expression.ExpressionRanges.Count == 0
            ? 0d
            : model.Expression.ExpressionRanges.Average(static range => range.Range);
    }

    private static double Delta(double current, double? previous)
    {
        return previous is null ? 0d : current - previous.Value;
    }

    private static void AppendHistoryEntry(string path, AvatarModelHistoryEntry entry)
    {
        File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonLineOptions) + Environment.NewLine, Utf8WithoutBom);
    }

    private static void CompactHistory(string path, DateTime utcNow)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var cutoff = utcNow - TimeSpan.FromDays(30);
        var retained = File.ReadLines(path)
            .Select(TryDeserializeEntry)
            .Where(entry => entry is not null && entry.EvaluatedAtUtc >= cutoff)
            .Select(static entry => entry!)
            .TakeLast(MaxHistoryEntryCount)
            .Select(entry => JsonSerializer.Serialize(entry, JsonLineOptions))
            .ToList();
        var contents = retained.Count == 0 ? "" : string.Join(Environment.NewLine, retained) + Environment.NewLine;
        AtomicTextFileWriter.WriteAllText(path, contents, Utf8WithoutBom);
    }

    private static AvatarModelHistoryEntry? ReadLatest(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AvatarModelHistoryEntry>(File.ReadAllText(path), JsonOptions)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<AvatarModelHistoryEntry> ReadRecent(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<AvatarModelHistoryEntry>>(File.ReadAllText(path), JsonOptions) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static AvatarModelHistoryEntry? TryDeserializeEntry(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<AvatarModelHistoryEntry>(line.TrimStart('\uFEFF'), JsonLineOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildHtml(AvatarModelHistoryReport report)
    {
        var latest = report.Latest;
        var statusClass = latest.Status == "review recommended"
            ? "bad"
            : latest.Status is "improving" or "learning within tolerance" ? "good" : "muted";
        var warningItems = latest.Warnings.Count == 0
            ? "<li class=\"good\">No current regression warnings.</li>"
            : string.Concat(latest.Warnings.Select(warning => $"<li class=\"bad\">{H(warning)}</li>"));
        var regionRows = string.Concat(latest.RegionMovement.Select(region =>
            $"<tr><td>{H(region.Region)}</td><td>{region.MatchedVertexCount:n0}</td><td>{region.RmsFaceSpanPercent:0.###}%</td></tr>"));
        var confidenceRows = latest.RegionConfidence.Count == 0
            ? "<tr><td colspan=\"3\" class=\"muted\">Waiting for region confidence.</td></tr>"
            : string.Concat(latest.RegionConfidence.Select(region =>
                $"<tr><td>{H(region.Region)}</td><td>{region.ConfidencePercent:0.#}%</td><td>{Signed(region.DeltaPoints)} pp</td></tr>"));
        var rebuildRows = string.Concat(report.RecentEntries
            .OrderByDescending(static entry => entry.RebuildNumber)
            .Take(36)
            .Select(entry =>
                $"<tr><td>{entry.RebuildNumber}</td><td>{H(entry.EvaluatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}</td><td>{H(entry.Status)}</td><td>{entry.SampleCount} retained / {entry.NewObservationCount} new</td><td>{entry.IdentityConfidencePercent:0.#}% ({Signed(entry.IdentityConfidenceDeltaPoints)} pp)</td><td>{entry.PoseCoveragePercent:0.#}% ({Signed(entry.PoseCoverageDeltaPoints)} pp)</td><td>{entry.OverallVertexRmsFaceSpanPercent:0.###}%</td><td>{entry.Warnings.Count}</td></tr>"));
        var historyJson = JsonSerializer.Serialize(report.RecentEntries, JsonLineOptions);

        return $$$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta http-equiv="refresh" content="30">
<title>Avatar Model Regression Audit</title>
<style>
:root{color-scheme:dark;--bg:#050b10;--panel:#0b141c;--line:#28435b;--text:#e7f6ff;--muted:#9db7c9;--good:#80e0a4;--warn:#ffd27a;--bad:#ff9a9a;--cyan:#66d9ff}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 Segoe UI,Arial,sans-serif}main{max-width:1320px;margin:0 auto;padding:20px}.panel{border:1px solid var(--line);background:var(--panel);border-radius:6px;padding:14px;margin:14px 0}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:8px}.metric{background:#07121c;border:1px solid #1d2c38;padding:10px}.label{color:var(--muted);font-size:12px;text-transform:uppercase}.value{font-size:18px;font-weight:700}.good{color:var(--good)}.warn{color:var(--warn)}.bad{color:var(--bad)}.muted{color:var(--muted)}h1{margin:0 0 4px;font-size:24px}h2{font-size:17px;margin:0 0 10px}canvas{width:100%;height:280px;display:block;background:#061019;border:1px solid #193149}table{width:100%;border-collapse:collapse}td,th{border-bottom:1px solid #1c3042;padding:7px 5px;text-align:left;vertical-align:top}th{color:var(--muted);font-weight:600}.split{display:grid;grid-template-columns:1fr 1fr;gap:14px}.scroll{overflow:auto}code{color:#b9d7ef}@media(max-width:900px){.split{grid-template-columns:1fr}}
</style>
</head>
<body>
<main>
  <h1>Avatar Model Regression Audit</h1>
  <p class="muted">Auto-refreshes every 30 seconds. Rebuild {{{latest.RebuildNumber}}}, evaluated {{{H(latest.EvaluatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}}}.</p>
  <section class="panel">
    <h2 class="{{{statusClass}}}">{{{H(latest.Status)}}}</h2>
    <p>{{{H(latest.Summary)}}}</p>
    <div class="grid">
      <div class="metric"><div class="label">Samples</div><div class="value">{{{latest.SampleCount}}} retained</div><div class="muted">{{{latest.NewObservationCount}}} new this rebuild</div></div>
      <div class="metric"><div class="label">Identity confidence</div><div class="value">{{{latest.IdentityConfidencePercent.ToString("0.#", CultureInfo.InvariantCulture)}}}%</div><div class="muted">{{{Signed(latest.IdentityConfidenceDeltaPoints)}}} pp</div></div>
      <div class="metric"><div class="label">Pose coverage</div><div class="value">{{{latest.PoseCoveragePercent.ToString("0.#", CultureInfo.InvariantCulture)}}}%</div><div class="muted">{{{Signed(latest.PoseCoverageDeltaPoints)}}} pp</div></div>
      <div class="metric"><div class="label">Shape stability</div><div class="value">{{{latest.ShapeStabilityPercent.ToString("0.#", CultureInfo.InvariantCulture)}}}%</div><div class="muted">{{{Signed(latest.ShapeStabilityDeltaPoints)}}} pp</div></div>
      <div class="metric"><div class="label">Mean-face movement</div><div class="value">{{{latest.OverallVertexRmsFaceSpanPercent.ToString("0.###", CultureInfo.InvariantCulture)}}}%</div><div class="muted">of face span</div></div>
      <div class="metric"><div class="label">Shape coefficient change</div><div class="value">{{{latest.ShapeCoefficientRelativeRmsPercent.ToString("0.###", CultureInfo.InvariantCulture)}}}%</div><div class="muted">relative RMS</div></div>
      <div class="metric"><div class="label">Outlier candidates</div><div class="value">{{{latest.GeometryOutlierCandidateCount}}}</div><div class="muted">highest {{{latest.HighestObservationRmsFaceSpanPercent.ToString("0.###", CultureInfo.InvariantCulture)}}}%</div></div>
      <div class="metric"><div class="label">Expression range</div><div class="value">{{{latest.MeanExpressionRange.ToString("0.###", CultureInfo.InvariantCulture)}}}</div><div class="muted">delta {{{Signed(latest.MeanExpressionRangeDelta)}}}</div></div>
    </div>
  </section>
  <section class="panel">
    <h2>Model Trend</h2>
    <canvas id="trend" aria-label="Recent confidence, coverage, and shape-stability trend"></canvas>
    <p class="muted">Confidence <span class="good">green</span>, pose coverage <span class="warn">gold</span>, shape stability <span style="color:var(--cyan)">cyan</span>. Each point is one 30-second rebuild.</p>
  </section>
  <div class="split">
    <section class="panel"><h2>Geometry Movement By Region</h2><table><tr><th>Region</th><th>Matched vertices</th><th>RMS movement</th></tr>{{{regionRows}}}</table><p class="muted">Regions are fixed geometric bands in the normalized dense face: eyes, central nose, mouth, and lower chin/jaw.</p></section>
    <section class="panel"><h2>Region Confidence</h2><table><tr><th>Region</th><th>Current</th><th>Change</th></tr>{{{confidenceRows}}}</table></section>
  </div>
  <section class="panel"><h2>Current Warnings</h2><ul>{{{warningItems}}}</ul><p class="muted">Downweighted identity observations: {{{latest.DownweightedIdentityObservationCount}}}. Excluded identity observations: {{{latest.ExcludedIdentityObservationCount}}}. Observations carrying backend warnings: {{{latest.WarningBearingObservationCount}}}.</p></section>
  <section class="panel scroll"><h2>Recent Rebuild Ledger</h2><table><tr><th>#</th><th>Time</th><th>Status</th><th>Samples</th><th>Confidence</th><th>Coverage</th><th>Mean movement</th><th>Warnings</th></tr>{{{rebuildRows}}}</table></section>
  <section class="panel"><h2>How To Read This</h2><p>{{{H(report.MeasurementPolicy)}}}</p><p>{{{H(report.RetentionPolicy)}}}</p><p class="muted">Source: <code>{{{H(report.HistoryFileName)}}}</code>, generated from the accepted 3DDFA observation store and the derived canonical identity model. Increasing coverage and confidence are positive. A large one-rebuild geometry jump, falling confidence, or movement without a new observation is a regression warning. Early models are allowed to move while proportions are still settling.</p></section>
</main>
<script type="application/json" id="historyJson">{{{historyJson}}}</script>
<script>
(() => {
  const entries = JSON.parse(document.getElementById('historyJson')?.textContent || '[]');
  const canvas = document.getElementById('trend');
  const ctx = canvas?.getContext('2d');
  if (!canvas || !ctx) return;
  const series = [
    { key: 'identityConfidencePercent', color: '#80e0a4' },
    { key: 'poseCoveragePercent', color: '#ffd27a' },
    { key: 'shapeStabilityPercent', color: '#66d9ff' }
  ];
  const resize = () => {
    const rect = canvas.getBoundingClientRect();
    const dpr = window.devicePixelRatio || 1;
    canvas.width = Math.max(360, Math.round(rect.width * dpr));
    canvas.height = Math.round(280 * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    draw(rect.width, 280);
  };
  const draw = (width, height) => {
    ctx.clearRect(0, 0, width, height);
    ctx.fillStyle = '#061019'; ctx.fillRect(0, 0, width, height);
    ctx.strokeStyle = '#193149'; ctx.lineWidth = 1;
    for (let value = 0; value <= 100; value += 20) {
      const y = height - 24 - value / 100 * (height - 44);
      ctx.beginPath(); ctx.moveTo(42, y); ctx.lineTo(width - 12, y); ctx.stroke();
      ctx.fillStyle = '#9db7c9'; ctx.fillText(String(value), 8, y + 4);
    }
    if (entries.length < 2) return;
    for (const item of series) {
      ctx.strokeStyle = item.color; ctx.lineWidth = 2; ctx.beginPath();
      entries.forEach((entry, index) => {
        const x = 42 + index / Math.max(1, entries.length - 1) * (width - 56);
        const value = Math.max(0, Math.min(100, Number(entry[item.key] ?? 0)));
        const y = height - 24 - value / 100 * (height - 44);
        if (index === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      });
      ctx.stroke();
    }
  };
  new ResizeObserver(resize).observe(canvas);
  resize();
})();
</script>
</body>
</html>
""";
    }

    private static string Signed(double value)
    {
        return value > 0d ? $"+{value:0.###}" : value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string H(string? value)
    {
        return WebUtility.HtmlEncode(value ?? "");
    }

    private static double Round(double value)
    {
        return double.IsFinite(value)
            ? Math.Round(value, 6, MidpointRounding.AwayFromZero)
            : 0d;
    }

    private sealed class RmsAccumulator
    {
        private double _sumSquared;

        public int Count { get; private set; }

        public double Rms => Count == 0 ? 0d : Math.Sqrt(_sumSquared / Count);

        public void Add(double squaredDistance)
        {
            if (!double.IsFinite(squaredDistance))
            {
                return;
            }

            _sumSquared += squaredDistance;
            Count++;
        }
    }

    private sealed record ObservationGeometryAudit(int Count, double HighestRmsPercent)
    {
        public static ObservationGeometryAudit Empty { get; } = new(0, 0d);
    }
}
