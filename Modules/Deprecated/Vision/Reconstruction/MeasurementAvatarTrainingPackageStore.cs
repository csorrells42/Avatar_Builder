using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;
using AvatarBuilder.Modules.Vision.Personalization;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class MeasurementAvatarTrainingPackageStore
{
    public const string JsonFileName = "measurement_avatar_training_package.json";
    public const string HtmlFileName = "measurement_avatar_training_package.html";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public MeasurementAvatarTrainingPackageFiles Write(string folder, MeasurementAvatarTrainingPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        Directory.CreateDirectory(folder);
        var jsonPath = Path.Combine(folder, JsonFileName);
        var htmlPath = Path.Combine(folder, HtmlFileName);
        AtomicTextFileWriter.WriteAllText(jsonPath, JsonSerializer.Serialize(package, JsonOptions), Encoding.UTF8);
        AtomicTextFileWriter.WriteAllText(htmlPath, BuildHtml(package), Encoding.UTF8);
        return new MeasurementAvatarTrainingPackageFiles(jsonPath, htmlPath);
    }

    private static string BuildHtml(MeasurementAvatarTrainingPackage package)
    {
        var title = $"{package.SubjectDisplayName} measurement avatar package";
        var scoreBars = string.Concat(new[]
        {
            ScoreBar("Overall", package.Readiness.OverallReadinessPercent),
            ScoreBar("Baseline", package.Readiness.BaselineCoveragePercent),
            ScoreBar("Learning Stability", package.Readiness.LearningStabilityCoveragePercent),
            ScoreBar("Motion", package.Readiness.MotionCoveragePercent),
            ScoreBar("Pose", package.Readiness.PoseCoveragePercent),
            ScoreBar("Pose Buckets", package.Readiness.PoseBucketCoveragePercent),
            ScoreBar("Distance", package.Readiness.DistanceCoveragePercent),
            ScoreBar("Z Distance", package.Readiness.ZDistanceCoveragePercent),
            ScoreBar("Z Evidence", package.Readiness.ZDistanceEvidenceHealthPercent),
            ScoreBar("A Rotate X", package.Readiness.ARotationAroundXCoveragePercent),
            ScoreBar("B Rotate Y", package.Readiness.BRotationAroundYCoveragePercent),
            ScoreBar("C Rotate Z", package.Readiness.CRotationAroundZCoveragePercent),
            ScoreBar("XYZABC", package.Readiness.XYZABCCoveragePercent),
            ScoreBar("Expression", package.Readiness.ExpressionCoveragePercent),
            ScoreBar("Identity", package.Readiness.IdentityCoveragePercent),
            ScoreBar("Identity Session", package.Readiness.IdentitySessionHealthPercent),
            ScoreBar("Contour Shape", package.Readiness.ContourShapeCoveragePercent),
            ScoreBar("Contour Z Profile", package.Readiness.ContourDepthProfileHealthPercent),
            ScoreBar("Surface Shape", package.Readiness.SurfaceShapeCoveragePercent),
            ScoreBar("Surface Z Profile", package.Readiness.SurfaceDepthProfileHealthPercent),
            ScoreBar("Surface Geometry", package.Readiness.SurfaceGeometryHealthPercent),
            ScoreBar("Eye Glasses Trust", package.Readiness.EyeBehindGlassesTrustPercent),
            ScoreBar("Mouth Jaw Trust", package.Readiness.MouthJawTrustPercent),
            ScoreBar("Direct Feature Trust", package.Readiness.DirectFeatureMeasurementTrustPercent),
            ScoreBar("Aperture Consistency", package.Readiness.ApertureConsistencyHealthPercent),
            ScoreBar("Eye Aperture Reliability", package.Readiness.EyeApertureReliabilityHealthPercent),
            ScoreBar("Quality", package.Readiness.QualityCoveragePercent),
            ScoreBar("Capture Quality", package.Readiness.CaptureQualityCoveragePercent),
            ScoreBar("Storage", package.Readiness.StorageHealthPercent),
            ScoreBar("Data Audit", package.Readiness.DataAuditHealthPercent),
            ScoreBar("Pose Estimation", package.Readiness.PoseEstimationHealthPercent),
            ScoreBar("Feature Anchoring", package.Readiness.FeatureAnchoringHealthPercent),
            ScoreBar("Pose-Explained Features", package.Readiness.PoseExplainedFeatureMotionHealthPercent),
            ScoreBar("Mouth Anchor", package.Readiness.MouthVerticalAnchorHealthPercent),
            ScoreBar("Pose Bucket Consistency", package.Readiness.PoseBucketConsistencyHealthPercent),
            ScoreBar("Jaw Scale", package.Readiness.JawDroopScaleHealthPercent),
            ScoreBar("Journal Coverage", package.Readiness.MeasurementJournalCoveragePercent)
        });
        var artifacts = string.Concat(package.SourceArtifacts.Select(artifact =>
            $"<tr><th>{Escape(artifact.Name)}</th><td>{Escape(artifact.FileName)}</td><td>{Escape(artifact.Kind)}</td><td>{Escape(artifact.Description)}</td></tr>"));
        var contourShapeRows = BuildContourShapeTable(package.ContourShapeProfiles);
        var poseRows = BuildPoseBucketTable(package.PoseCoverageProfile);
        var poseConsistencyRows = BuildPoseConsistencyTable(package.PoseBucketConsistency);
        var apertureConsistencyRows = BuildApertureConsistencyTable(package.ApertureConsistency);
        var reconstructionLane = package.ReconstructionLane;
        var reconstructionReadyLabel = reconstructionLane.AvatarReconstructionCanRunInference ? "Yes" : "No";

        return $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta http-equiv="refresh" content="5">
<title>{{Escape(title)}}</title>
<style>
:root {
  color-scheme: dark;
  --bg: #050b10;
  --panel: #0b141c;
  --line: #28435b;
  --text: #e7f6ff;
  --muted: #9db7c9;
  --accent: #65c8ff;
  --good: #8ff2c5;
  --warn: #ffd166;
  --bad: #ff9fbd;
}
* { box-sizing: border-box; }
body {
  margin: 0;
  background: var(--bg);
  color: var(--text);
  font: 14px/1.45 "Segoe UI", Arial, sans-serif;
}
main {
  max-width: 1280px;
  margin: 0 auto;
  padding: 18px;
}
h1 { margin: 0 0 4px; font-size: 24px; }
h2 { margin: 0 0 10px; font-size: 17px; }
h3 { margin: 16px 0 8px; font-size: 15px; color: var(--accent); }
.subtle { color: var(--muted); }
.grid {
  display: grid;
  grid-template-columns: minmax(320px, 0.9fr) minmax(360px, 1.1fr);
  gap: 14px;
  margin-top: 14px;
}
.panel {
  border: 1px solid var(--line);
  background: var(--panel);
  border-radius: 6px;
  padding: 14px;
}
.hero {
  display: flex;
  justify-content: space-between;
  gap: 14px;
  align-items: end;
}
.big {
  font-size: 42px;
  line-height: 1;
  color: var(--accent);
}
.score {
  display: grid;
  grid-template-columns: 92px 1fr 58px;
  gap: 10px;
  align-items: center;
  margin: 10px 0;
}
.track {
  height: 12px;
  border: 1px solid #274359;
  background: #07141c;
  border-radius: 999px;
  overflow: hidden;
}
.fill {
  height: 100%;
  width: var(--value);
  background: linear-gradient(90deg, var(--bad), var(--warn), var(--good));
}
table { width: 100%; border-collapse: collapse; }
th, td {
  border-bottom: 1px solid #1c3042;
  padding: 6px 8px 6px 0;
  vertical-align: top;
}
th {
  text-align: left;
  color: var(--muted);
  font-weight: 600;
}
ul { margin: 8px 0 0; padding-left: 18px; }
li { margin: 5px 0; }
@media (max-width: 900px) {
  .grid { grid-template-columns: 1fr; }
  .hero { display: block; }
}
</style>
</head>
<body>
<main>
  <section class="panel hero">
    <div>
      <h1>{{Escape(title)}}</h1>
      <div class="subtle">{{Escape(package.TrainingDecision)}}</div>
      <div class="subtle">Subject: {{Escape(package.SubjectDisplayName)}} ({{Escape(package.SubjectId)}}) | Gate: {{Escape(package.SubjectGate.GateDecision)}} - {{Escape(package.SubjectGate.Reason)}}</div>
      <div class="subtle">Measured contribution {{Format(package.MeasurementContributionPercent)}}% | template prior contribution {{Format(package.TemplatePriorContributionPercent)}}%</div>
      <div class="subtle">{{Escape(package.StoragePolicy)}}</div>
      <div class="subtle">{{Escape(package.SafetyBoundary)}}</div>
    </div>
    <div class="big">{{Format(package.Readiness.OverallReadinessPercent)}}%</div>
  </section>
  <section class="grid">
    <div class="panel">
      <h2>Readiness</h2>
      {{scoreBars}}
      <table>
        <tr><th>Accepted baseline samples</th><td>{{package.AcceptedBaselineSamples.ToString(CultureInfo.InvariantCulture)}}</td></tr>
        <tr><th>Accepted sample weight</th><td>{{Format(package.AcceptedSampleWeight)}}</td></tr>
        <tr><th>Learning anchor</th><td>{{Format(package.LearningStability.AnchorPercent)}}% ({{Escape(package.LearningStability.AnchorStatus)}})</td></tr>
        <tr><th>Weakest tracked weight</th><td>{{Format(package.LearningStability.MinimumTrackedDistributionWeight)}}</td></tr>
        <tr><th>Max next-sample influence</th><td>{{Format(package.LearningStability.MaximumNextSampleInfluencePercent)}}%</td></tr>
        <tr><th>Max event-like influence</th><td>{{Format(package.LearningStability.MaximumEventLikeNextSampleInfluencePercent)}}%</td></tr>
        <tr><th>Motion observations</th><td>{{package.MotionUsableObservations.ToString(CultureInfo.InvariantCulture)}}</td></tr>
        <tr><th>Motion pairs</th><td>{{package.MotionPairs.ToString(CultureInfo.InvariantCulture)}}</td></tr>
        <tr><th>Identity signature samples</th><td>{{package.IdentitySignatureSamples.ToString(CultureInfo.InvariantCulture)}}</td></tr>
        <tr><th>Identity session stage</th><td>{{Escape(package.Readiness.IdentitySessionAuditStage)}}</td></tr>
        <tr><th>Identity session status</th><td>{{Escape(package.Readiness.IdentitySessionAuditStatus)}}</td></tr>
        <tr><th>Provenance policy</th><td>{{Escape(package.ProvenancePolicy)}}</td></tr>
        <tr><th>Template prior policy</th><td>{{Escape(package.TemplatePriorPolicy)}}</td></tr>
        <tr><th>Fast tracking lane</th><td>{{Escape(reconstructionLane.FastTrackingStatus)}}</td></tr>
        <tr><th>Avatar reconstruction lane</th><td>{{Escape(reconstructionLane.AvatarReconstructionStatus)}}</td></tr>
        <tr><th>Reconstruction trust</th><td>{{Escape(reconstructionLane.TrustDecision)}}</td></tr>
        <tr><th>Measurement storage</th><td>{{Escape(FormatBytes(package.MeasurementJournalBytes))}} / {{Escape(FormatBytes(package.MeasurementBudgetBytes))}} ({{Format(package.MeasurementBudgetUsedPercent)}}%)</td></tr>
      </table>
      <h3>Two-Lane Reconstruction</h3>
      <table>
        <tr><th>Fast lane purpose</th><td>{{Escape(reconstructionLane.FastTrackingPurpose)}}</td></tr>
        <tr><th>Avatar lane purpose</th><td>{{Escape(reconstructionLane.AvatarReconstructionPurpose)}}</td></tr>
        <tr><th>Avatar lane ready</th><td>{{reconstructionReadyLabel}}</td></tr>
        <tr><th>Learning impact</th><td>{{Escape(reconstructionLane.LearningImpact)}}</td></tr>
      </table>
      <ul>{{BuildList(reconstructionLane.Warnings, "No reconstruction-lane warnings.")}}</ul>
      <h3>Next Capture Suggestions</h3>
      <ul>{{BuildList(package.NextCaptureSuggestions, "No next capture suggestions.")}}</ul>
      <h3>Warnings</h3>
      <ul>{{BuildList(package.Warnings, "No package warnings.")}}</ul>
    </div>
    <div class="panel">
      <h2>Package Artifacts</h2>
      <table>
        <tr><th>Name</th><th>File</th><th>Kind</th><th>Description</th></tr>
        {{artifacts}}
      </table>
      <h3>Integration Notes</h3>
      <ul>{{BuildList(package.IntegrationNotes, "No integration notes.")}}</ul>
      <h3>Strengths</h3>
      <ul>{{BuildList(package.Strengths, "No strong learning-data areas yet.")}}</ul>
    </div>
    <div class="panel">
      <h2>Neutral Face Profile</h2>
      {{BuildMetricTable(package.NeutralFaceProfile)}}
    </div>
    <div class="panel">
      <h2>Motion Profile</h2>
      {{BuildMetricTable(package.MotionProfile)}}
    </div>
    <div class="panel">
      <h2>Identity Profile</h2>
      {{BuildMetricTable(package.IdentityProfile)}}
    </div>
    <div class="panel">
      <h2>Pose Coverage Profile</h2>
      {{poseRows}}
      <h3>Pose Bucket Consistency</h3>
      {{poseConsistencyRows}}
    </div>
    <div class="panel">
      <h2>Contour Shape Profiles</h2>
      {{contourShapeRows}}
    </div>
    <div class="panel">
      <h2>Quality Profile</h2>
      <h3>Aperture Consistency</h3>
      {{apertureConsistencyRows}}
      {{BuildMetricTable(package.QualityProfile)}}
    </div>
  </section>
</main>
</body>
</html>
""";
    }

    private static string BuildMetricTable(IReadOnlyDictionary<string, MeasurementAvatarTrainingMetric> metrics)
    {
        if (metrics.Count == 0)
        {
            return "<p class=\"subtle\">No metrics available yet.</p>";
        }

        var rows = string.Concat(metrics.Select(metric =>
        {
            var value = metric.Value;
            var range = value.Minimum is double minimum && value.Maximum is double maximum
                ? $"{Format(minimum)} to {Format(maximum)}"
                : "";
            var normal = value.NormalLow is double low && value.NormalHigh is double high
                ? $"{Format(low)} to {Format(high)}"
                : "";
            return $"<tr><th>{Escape(value.Label)}</th><td>{Format(value.Average)}</td><td>{Escape(value.Units)}</td><td>{value.SampleCount.ToString(CultureInfo.InvariantCulture)}</td><td>{Escape(range)}</td><td>{Escape(normal)}</td><td>{Escape(value.AvatarUse)}</td></tr>";
        }));
        return $"<table><tr><th>Metric</th><th>Average</th><th>Units</th><th>Samples</th><th>Range</th><th>Normal</th><th>Avatar use</th></tr>{rows}</table>";
    }

    private static string BuildContourShapeTable(IReadOnlyDictionary<string, PersonalFaceContourShapeProfile> profiles)
    {
        if (profiles.Count == 0)
        {
            return "<p class=\"subtle\">No aggregate contour or surface shape profiles available yet.</p>";
        }

        var rows = string.Concat(profiles.Values.Select(profile =>
            $"<tr><th>{Escape(profile.Label)}</th><td>{profile.SampleCount.ToString(CultureInfo.InvariantCulture)}</td><td>{Format(profile.TotalWeight)}</td><td>{profile.PointCount.ToString(CultureInfo.InvariantCulture)}</td><td>{(profile.Closed ? "closed" : "open")}</td><td>{Escape(profile.CoordinateSpace)}</td></tr>"));
        return $"<table><tr><th>Shape</th><th>Samples</th><th>Weight</th><th>Points</th><th>Path</th><th>Coordinate space</th></tr>{rows}</table>";
    }

    private static string BuildPoseBucketTable(IReadOnlyList<PersonalFacePoseBucketProfile> buckets)
    {
        if (buckets.Count == 0)
        {
            return "<p class=\"subtle\">No pose buckets available yet.</p>";
        }

        var rows = string.Concat(buckets.Select(bucket =>
            $"<tr><th>{Escape(bucket.Label)}</th><td>{bucket.SampleCount.ToString(CultureInfo.InvariantCulture)}</td><td>{Format(bucket.TotalWeight)}</td><td>{Format(bucket.HeadPitchDegrees.Average)}</td><td>{Format(bucket.HeadYawDegrees.Average)}</td><td>{Format(bucket.HeadRollDegrees.Average)}</td><td>{Escape(bucket.PrimaryNeutralReference ? "neutral reference" : "pose coverage")}</td><td>{Escape(bucket.CaptureInstruction)}</td></tr>"));
        return $"<table><tr><th>Pose</th><th>Samples</th><th>Weight</th><th>A</th><th>B</th><th>C</th><th>Use</th><th>Next capture</th></tr>{rows}</table>";
    }

    private static string BuildPoseConsistencyTable(PersonalFacePoseBucketConsistencyReport report)
    {
        if (report.Comparisons.Count == 0)
        {
            return $"<p class=\"subtle\">{Escape(report.Status)}</p>";
        }

        var rows = string.Concat(report.Comparisons.Select(comparison =>
            $"<tr><th>{Escape(comparison.Label)}</th><td>{Escape(comparison.Status)}</td><td>{Format(comparison.PoseAxisHealthPercent)}%</td><td>{Escape(comparison.PoseAxisReason)}</td><td>{Format(comparison.DriftScorePercent)}%</td><td>{Format(comparison.EyeMidlineXToFaceWidthDelta)}</td><td>{Format(comparison.MouthCenterXToFaceWidthDelta)}</td><td>{Format(comparison.EyeToMouthXOffsetToFaceWidthDelta)}</td><td>{Format(comparison.InterEyeDistanceToFaceWidthDelta)}</td><td>{Format(comparison.MouthWidthToFaceWidthDelta)}</td><td>{Format(comparison.EyeMidlineYToFaceHeightDelta)}</td><td>{Format(comparison.MouthCenterYToFaceHeightDelta)}</td></tr>"));
        return $"<p class=\"subtle\">{Escape(report.Status)} | health {Format(report.HealthPercent)}%</p><table><tr><th>Pose</th><th>Status</th><th>Axis</th><th>Axis reason</th><th>Drift</th><th>Eye X</th><th>Mouth X</th><th>Eye-mouth X</th><th>Eye spacing</th><th>Mouth width</th><th>Eye height</th><th>Mouth height</th></tr>{rows}</table>";
    }

    private static string BuildApertureConsistencyTable(PersonalFaceApertureConsistencyReport report)
    {
        return "<table>"
            + $"<tr><th>Status</th><td>{Escape(report.Status)}</td></tr>"
            + $"<tr><th>Overall health</th><td>{Format(report.HealthPercent)}%</td></tr>"
            + $"<tr><th>Eye aperture</th><td>{Format(report.EyeApertureHealthPercent)}% | {report.EyeComparedSampleCount.ToString(CultureInfo.InvariantCulture)} samples | blink correlation {Format(report.EyeOpeningBlinkCorrelation)}</td></tr>"
            + $"<tr><th>Mouth aperture</th><td>{Format(report.MouthApertureHealthPercent)}% | {report.MouthComparedSampleCount.ToString(CultureInfo.InvariantCulture)} samples | mouth correlation {Format(report.MouthOpeningEvidenceCorrelation)}</td></tr>"
            + $"<tr><th>Jaw droop</th><td>{Format(report.JawDroopAgreementHealthPercent)}% | {report.JawComparedSampleCount.ToString(CultureInfo.InvariantCulture)} samples | jaw correlation {Format(report.JawDroopEvidenceCorrelation)}</td></tr>"
            + "</table>";
    }

    private static string ScoreBar(string label, double value)
    {
        var clamped = Math.Clamp(value, 0d, 100d);
        return $"<div class=\"score\"><div>{Escape(label)}</div><div class=\"track\"><div class=\"fill\" style=\"--value:{Format(clamped)}%\"></div></div><div>{Format(clamped)}%</div></div>";
    }

    private static string BuildList(IReadOnlyCollection<string> values, string emptyText)
    {
        return values.Count == 0
            ? $"<li>{Escape(emptyText)}</li>"
            : string.Concat(values.Select(value => $"<li>{Escape(value)}</li>"));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024L)
        {
            return $"{Math.Max(0L, bytes).ToString(CultureInfo.InvariantCulture)} B";
        }

        var size = (double)bytes;
        string[] units = ["KB", "MB", "GB"];
        var unitIndex = -1;
        do
        {
            size /= 1024d;
            unitIndex++;
        }
        while (size >= 1024d && unitIndex < units.Length - 1);

        return $"{size.ToString("0.#", CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }

    private static string Format(double? value)
    {
        return value is double number ? Format(number) : "";
    }

    private static string Format(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string Escape(string value)
    {
        return WebUtility.HtmlEncode(value);
    }
}

public sealed record MeasurementAvatarTrainingPackageFiles(string JsonPath, string HtmlPath);
