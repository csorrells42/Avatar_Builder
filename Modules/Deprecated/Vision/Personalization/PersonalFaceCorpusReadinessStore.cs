using System.IO;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceCorpusReadinessStore
{
    public const string DefaultJsonFileName = "personal_face_corpus_readiness.json";
    public const string DefaultHtmlFileName = "personal_face_corpus_readiness.html";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string FileName { get; }

    public PersonalFaceCorpusReadinessStore(string fileName = DefaultJsonFileName)
    {
        FileName = fileName;
    }

    public string Write(string folder, PersonalFaceCorpusReadiness model)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        AtomicTextFileWriter.WriteAllText(path, JsonSerializer.Serialize(model, JsonOptions), Encoding.UTF8);
        AtomicTextFileWriter.WriteAllText(GetHtmlPath(path), BuildHtml(model), Encoding.UTF8);
        return path;
    }

    public static string WriteFile(string path, PersonalFaceCorpusReadiness model)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        AtomicTextFileWriter.WriteAllText(path, JsonSerializer.Serialize(model, JsonOptions), Encoding.UTF8);
        AtomicTextFileWriter.WriteAllText(GetHtmlPath(path), BuildHtml(model), Encoding.UTF8);
        return path;
    }

    public static string GetHtmlPath(string jsonPath)
    {
        return Path.ChangeExtension(jsonPath, ".html");
    }

    private static string BuildHtml(PersonalFaceCorpusReadiness readiness)
    {
        var title = $"{readiness.SubjectDisplayName} learning data health";
        var warnings = BuildList(readiness.Warnings, "No readiness warnings.");
        var suggestions = BuildList(readiness.NextCaptureSuggestions, "No next capture suggestions.");
        var strengths = BuildList(readiness.Strengths, "No strong learning-data areas yet.");
        var dataAuditFindings = BuildList(readiness.DataAuditFindings, "No data-audit findings.");
        var rows = string.Concat(new[]
        {
            MetricRow("Accepted baseline samples", readiness.AcceptedBaselineSamples.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Learning anchor", $"{Format(readiness.LearningAnchorPercent)}% ({readiness.LearningAnchorStatus})"),
            MetricRow("Weakest tracked weight", Format(readiness.MinimumTrackedDistributionWeight)),
            MetricRow("Max next-sample influence", $"{Format(readiness.MaximumNextSampleInfluencePercent)}%"),
            MetricRow("Max event-like influence", $"{Format(readiness.MaximumEventLikeNextSampleInfluencePercent)}%"),
            MetricRow("Recent samples reviewed", readiness.RecentMeasurementSamplesReviewed.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Motion observations", readiness.MotionUsableObservations.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Motion pairs", readiness.MotionPairs.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Pose buckets covered", $"{readiness.PoseBucketCoveredCount.ToString(CultureInfo.InvariantCulture)} / {readiness.PoseBucketRequiredCount.ToString(CultureInfo.InvariantCulture)}"),
            MetricRow("Identity signature samples", readiness.IdentitySignatureSamples.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Identity session health", $"{Format(readiness.IdentitySessionHealthPercent)}% ({readiness.IdentitySessionAuditStage})"),
            MetricRow("Identity session status", readiness.IdentitySessionAuditStatus),
            MetricRow("Recent identity confidence", $"avg {Format(readiness.AverageRecentIdentityConfidencePercent)}%, min {Format(readiness.MinimumRecentIdentityConfidencePercent)}%, outlier frames {FormatRate(readiness.RecentIdentityOutlierFrameRate)}"),
            MetricRow("Left eye shape samples", readiness.LeftEyeShapeSamples.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Right eye shape samples", readiness.RightEyeShapeSamples.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Outer lip shape samples", readiness.OuterLipShapeSamples.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Inner lip shape samples", readiness.InnerLipShapeSamples.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Jaw shape samples", readiness.JawShapeSamples.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Left brow surface samples", readiness.LeftBrowShapeSamples.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Right brow surface samples", readiness.RightBrowShapeSamples.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Nose bridge surface samples", readiness.NoseBridgeShapeSamples.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Nose base surface samples", readiness.NoseBaseShapeSamples.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Left cheek surface samples", readiness.LeftCheekSurfaceSamples.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Right cheek surface samples", readiness.RightCheekSurfaceSamples.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Forehead surface samples", readiness.ForeheadSurfaceSamples.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Eye behind glasses trust", $"{Format(readiness.EyeBehindGlassesTrustPercent)}%"),
            MetricRow("Mouth/jaw trust", $"{Format(readiness.MouthJawTrustPercent)}%"),
            MetricRow("Direct feature trust", $"{Format(readiness.DirectFeatureMeasurementTrustPercent)}%"),
            MetricRow("Aperture consistency health", $"{Format(readiness.ApertureConsistencyHealthPercent)}%"),
            MetricRow("Aperture consistency", $"{readiness.ApertureConsistency.Status} ({readiness.ApertureConsistency.EyeComparedSampleCount.ToString(CultureInfo.InvariantCulture)} eye / {readiness.ApertureConsistency.MouthComparedSampleCount.ToString(CultureInfo.InvariantCulture)} mouth / {readiness.ApertureConsistency.JawComparedSampleCount.ToString(CultureInfo.InvariantCulture)} jaw samples)"),
            MetricRow("Eye aperture reliability health", $"{Format(readiness.EyeApertureReliabilityHealthPercent)}%"),
            MetricRow("Eye agreement", $"avg {Format(readiness.EyeAgreementAveragePercent)}%, min {Format(readiness.EyeAgreementMinimumPercent)}%"),
            MetricRow("Data audit health", $"{Format(readiness.DataAuditHealthPercent)}%"),
            MetricRow("Pose estimation health", $"{Format(readiness.PoseEstimationHealthPercent)}%"),
            MetricRow("Feature anchoring health", $"{Format(readiness.FeatureAnchoringHealthPercent)}%"),
            MetricRow("Pose-explained feature motion", $"{Format(readiness.PoseExplainedFeatureMotionHealthPercent)}%"),
            MetricRow("Pose-explained feature range", $"observed {Format(readiness.PoseExplainedFeatureObservedRange)}, expected {Format(readiness.PoseExplainedFeatureExpectedRange)}"),
            MetricRow("Mouth vertical anchor health", $"{Format(readiness.MouthVerticalAnchorHealthPercent)}%"),
            MetricRow("Mouth vertical anchor review", $"{readiness.MouthVerticalAnchorSamplesReviewed.ToString(CultureInfo.InvariantCulture)} samples, suspicious {FormatRate(readiness.MouthVerticalAnchorSuspiciousSampleRate)}"),
            MetricRow("Pose bucket consistency health", $"{Format(readiness.PoseBucketConsistencyHealthPercent)}%"),
            MetricRow("Pose bucket consistency", $"{readiness.PoseBucketConsistency.Status} ({readiness.PoseBucketConsistency.ComparedPoseBucketCount.ToString(CultureInfo.InvariantCulture)} compared)"),
            MetricRow("Jaw droop scale health", $"{Format(readiness.JawDroopScaleHealthPercent)}%"),
            MetricRow("Journal coverage", $"{Format(readiness.MeasurementJournalCoveragePercent)}%"),
            MetricRow("Measurement storage", $"{FormatBytes(readiness.MeasurementJournalBytes)} / {FormatBytes(readiness.MeasurementBudgetBytes)} ({Format(readiness.MeasurementBudgetUsedPercent)}%)"),
            MetricRow("Capture quality samples", readiness.CaptureQualitySamples.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Capture collectable rate", FormatRate(readiness.CaptureQualityCanCollectRate)),
            MetricRow("Capture avatar-grade rate", FormatRate(readiness.CaptureQualityAvatarGradeRate)),
            MetricRow("Average capture quality", $"{Format(readiness.AverageCaptureQualityScorePercent)}%"),
            MetricRow("Minimum capture quality", $"{Format(readiness.MinimumCaptureQualityScorePercent)}%"),
            MetricRow("Average camera mode score", $"{Format(readiness.AverageCaptureQualityCameraModeScorePercent)}%"),
            MetricRow("Average face scale score", $"{Format(readiness.AverageCaptureQualityFaceScaleScorePercent)}%"),
            MetricRow("Average eye evidence score", $"{Format(readiness.AverageCaptureQualityEyeScorePercent)}%"),
            MetricRow("Average mouth evidence score", $"{Format(readiness.AverageCaptureQualityMouthScorePercent)}%"),
            MetricRow("Average glasses score", $"{Format(readiness.AverageCaptureQualityGlassesScorePercent)}%"),
            MetricRow("Face width range", Format(readiness.FaceWidthRange)),
            MetricRow("Face height range", Format(readiness.FaceHeightRange)),
            MetricRow("Z distance coverage", $"{Format(readiness.ZDistanceCoveragePercent)}%"),
            MetricRow("Z evidence health", $"{Format(readiness.ZDistanceEvidenceHealthPercent)}%"),
            MetricRow("Z estimate samples", readiness.ZEstimateSamples.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Z apparent distance range", Format(readiness.ZApparentDistanceRange)),
            MetricRow("Z relative reference range", Format(readiness.ZRelativeToReferenceRange)),
            MetricRow("Z confidence", $"avg {Format(readiness.AverageZConfidencePercent)}%, min {Format(readiness.MinimumZConfidencePercent)}%"),
            MetricRow("Z source mix", $"calibrated {FormatRate(readiness.ZCalibratedRate)}, camera/FOV {FormatRate(readiness.ZCameraFovEstimatedRate)}, learned ref {FormatRate(readiness.ZLearnedReferenceRate)}, apparent only {FormatRate(readiness.ZApparentOnlyRate)}"),
            MetricRow("B range", $"{Format(readiness.HeadYawRangeDegrees)} deg"),
            MetricRow("B rotation coverage", $"{Format(readiness.BRotationAroundYCoveragePercent)}%"),
            MetricRow("A range", $"{Format(readiness.HeadPitchRangeDegrees)} deg"),
            MetricRow("A rotation coverage", $"{Format(readiness.ARotationAroundXCoveragePercent)}%"),
            MetricRow("C range", $"{Format(readiness.HeadRollRangeDegrees)} deg"),
            MetricRow("C rotation coverage", $"{Format(readiness.CRotationAroundZCoveragePercent)}%"),
            MetricRow("XYZABC coverage", $"{Format(readiness.XYZABCCoveragePercent)}%"),
            MetricRow("Eye opening range", Format(readiness.EyeOpeningRange)),
            MetricRow("Mouth opening range", Format(readiness.MouthOpeningRange)),
            MetricRow("Jaw droop range", Format(readiness.JawDroopRange)),
            MetricRow("MediaPipe blink range", $"{Format(readiness.MediaPipeBlinkRangePercent)}%"),
            MetricRow("MediaPipe jaw-open range", $"{Format(readiness.MediaPipeJawOpenRangePercent)}%"),
            MetricRow("Face aspect range", Format(readiness.FaceAspectRatioRange)),
            MetricRow("Eye horizontal range", Format(readiness.EyeMidlineXToFaceWidthRange)),
            MetricRow("Mouth horizontal range", Format(readiness.MouthCenterXToFaceWidthRange)),
            MetricRow("Eye-mouth horizontal offset range", Format(readiness.EyeToMouthXOffsetToFaceWidthRange)),
            MetricRow("Eye spacing range", Format(readiness.InterEyeDistanceToFaceWidthRange)),
            MetricRow("Mouth width range", Format(readiness.MouthWidthToFaceWidthRange)),
            MetricRow("Eye vertical range", Format(readiness.EyeMidlineYToFaceHeightRange)),
            MetricRow("Mouth vertical range", Format(readiness.MouthCenterYToFaceHeightRange)),
            MetricRow("Eye-mouth vertical span range", Format(readiness.EyeToMouthYDistanceToFaceHeightRange)),
            MetricRow("Eye artifact suppressed rate", FormatRate(readiness.EyeArtifactSuppressedRate)),
            MetricRow("Possible one-eye artifact rate", FormatRate(readiness.PossibleOneEyeArtifactRate)),
            MetricRow("Eye reconstructed rate", FormatRate(readiness.EyeReconstructedRate)),
            MetricRow("Mouth reconstructed rate", FormatRate(readiness.MouthReconstructedRate)),
            MetricRow("Contour Z profile health", $"{Format(readiness.ContourDepthProfileHealthPercent)}%"),
            MetricRow("Surface Z profile health", $"{Format(readiness.SurfaceDepthProfileHealthPercent)}%"),
            MetricRow("Surface geometry health", $"{Format(readiness.SurfaceGeometryHealthPercent)}%"),
            MetricRow("Surface geometry status", readiness.SurfaceGeometryStatus)
        });
        var bars = string.Concat(new[]
        {
            ScoreBar("Overall", readiness.OverallReadinessPercent),
            ScoreBar("Baseline", readiness.BaselineCoveragePercent),
            ScoreBar("Learning Stability", readiness.LearningStabilityCoveragePercent),
            ScoreBar("Motion", readiness.MotionCoveragePercent),
            ScoreBar("Pose", readiness.PoseCoveragePercent),
            ScoreBar("Pose Buckets", readiness.PoseBucketCoveragePercent),
            ScoreBar("Distance", readiness.DistanceCoveragePercent),
            ScoreBar("Z Distance", readiness.ZDistanceCoveragePercent),
            ScoreBar("Z Evidence", readiness.ZDistanceEvidenceHealthPercent),
            ScoreBar("A Rotate X", readiness.ARotationAroundXCoveragePercent),
            ScoreBar("B Rotate Y", readiness.BRotationAroundYCoveragePercent),
            ScoreBar("C Rotate Z", readiness.CRotationAroundZCoveragePercent),
            ScoreBar("XYZABC", readiness.XYZABCCoveragePercent),
            ScoreBar("Expression", readiness.ExpressionCoveragePercent),
            ScoreBar("Identity", readiness.IdentityCoveragePercent),
            ScoreBar("Identity Session", readiness.IdentitySessionHealthPercent),
            ScoreBar("Contour Shape", readiness.ContourShapeCoveragePercent),
            ScoreBar("Contour Z Profile", readiness.ContourDepthProfileHealthPercent),
            ScoreBar("Surface Shape", readiness.SurfaceShapeCoveragePercent),
            ScoreBar("Surface Z Profile", readiness.SurfaceDepthProfileHealthPercent),
            ScoreBar("Surface Geometry", readiness.SurfaceGeometryHealthPercent),
            ScoreBar("Eye Glasses Trust", readiness.EyeBehindGlassesTrustPercent),
            ScoreBar("Mouth Jaw Trust", readiness.MouthJawTrustPercent),
            ScoreBar("Direct Feature Trust", readiness.DirectFeatureMeasurementTrustPercent),
            ScoreBar("Aperture Consistency", readiness.ApertureConsistencyHealthPercent),
            ScoreBar("Eye Aperture Reliability", readiness.EyeApertureReliabilityHealthPercent),
            ScoreBar("Quality", readiness.QualityCoveragePercent),
            ScoreBar("Capture Quality", readiness.CaptureQualityCoveragePercent),
            ScoreBar("Storage", readiness.StorageHealthPercent),
            ScoreBar("Data Audit", readiness.DataAuditHealthPercent),
            ScoreBar("Pose-Explained Features", readiness.PoseExplainedFeatureMotionHealthPercent),
            ScoreBar("Mouth Anchor", readiness.MouthVerticalAnchorHealthPercent),
            ScoreBar("Pose Bucket Consistency", readiness.PoseBucketConsistencyHealthPercent)
        });
        var poseBuckets = BuildPoseBucketTable(readiness.PoseBuckets);
        var poseConsistency = BuildPoseConsistencyTable(readiness.PoseBucketConsistency);
        var apertureConsistency = BuildApertureConsistencyTable(readiness.ApertureConsistency);

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
  max-width: 1180px;
  margin: 0 auto;
  padding: 18px;
}
h1 { margin: 0 0 4px; font-size: 24px; }
h2 { margin: 0 0 10px; font-size: 17px; }
.subtle { color: var(--muted); }
.grid {
  display: grid;
  grid-template-columns: minmax(320px, 1.2fr) minmax(300px, 0.8fr);
  gap: 14px;
  margin-top: 14px;
}
.panel {
  border: 1px solid var(--line);
  background: var(--panel);
  border-radius: 6px;
  padding: 14px;
}
.score {
  display: grid;
  grid-template-columns: 96px 1fr 58px;
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
  padding: 6px 0;
  vertical-align: top;
}
th {
  text-align: left;
  color: var(--muted);
  font-weight: 600;
}
ul { margin: 8px 0 0; padding-left: 18px; }
li { margin: 5px 0; }
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
@media (max-width: 860px) {
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
      <div class="subtle">{{Escape(readiness.Status)}} | {{Escape(readiness.StoragePolicy)}}</div>
      <div class="subtle">Live report auto-refreshes every 5 seconds from the latest saved measurement snapshot.</div>
      <div class="subtle">Subject: {{Escape(readiness.SubjectDisplayName)}} ({{Escape(readiness.SubjectId)}}) | Unknown subject policy: {{Escape(readiness.UnknownSubjectPolicy)}}</div>
    </div>
    <div class="big">{{Format(readiness.OverallReadinessPercent)}}%</div>
  </section>
  <section class="grid">
    <div class="panel">
      <h2>Coverage Scores</h2>
      {{bars}}
    </div>
    <div class="panel">
      <h2>Next Capture Suggestions</h2>
      <ul>{{suggestions}}</ul>
    </div>
    <div class="panel">
      <h2>Learning Data Measurements</h2>
      <table>{{rows}}</table>
    </div>
    <div class="panel">
      <h2>Pose Buckets</h2>
      {{poseBuckets}}
      <h2 style="margin-top:16px">Pose Bucket Consistency</h2>
      {{poseConsistency}}
    </div>
    <div class="panel">
      <h2>Data Audit Findings</h2>
      <ul>{{dataAuditFindings}}</ul>
      <h2 style="margin-top:16px">Aperture Consistency</h2>
      {{apertureConsistency}}
    </div>
    <div class="panel">
      <h2>Warnings</h2>
      <ul>{{warnings}}</ul>
      <h2 style="margin-top:16px">Strengths</h2>
      <ul>{{strengths}}</ul>
    </div>
  </section>
</main>
</body>
</html>
""";
    }

    private static string ScoreBar(string label, double value)
    {
        var clamped = Math.Clamp(value, 0d, 100d);
        return $"<div class=\"score\"><div>{Escape(label)}</div><div class=\"track\"><div class=\"fill\" style=\"--value:{Format(clamped)}%\"></div></div><div>{Format(clamped)}%</div></div>";
    }

    private static string MetricRow(string label, string value)
    {
        return $"<tr><th>{Escape(label)}</th><td>{Escape(value)}</td></tr>";
    }

    private static string BuildPoseBucketTable(IReadOnlyList<PersonalFacePoseBucketProfile> buckets)
    {
        if (buckets.Count == 0)
        {
            return "<p class=\"subtle\">No pose buckets available yet.</p>";
        }

        var rows = string.Concat(buckets.Select(bucket =>
            $"<tr><th>{Escape(bucket.Label)}</th><td>{bucket.SampleCount.ToString(CultureInfo.InvariantCulture)}</td><td>{Format(bucket.TotalWeight)}</td><td>{Format(bucket.HeadPitchDegrees.Average)}</td><td>{Format(bucket.HeadYawDegrees.Average)}</td><td>{Format(bucket.HeadRollDegrees.Average)}</td></tr>"));
        return $"<table><tr><th>Pose</th><th>Samples</th><th>Weight</th><th>A</th><th>B</th><th>C</th></tr>{rows}</table>";
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
            + $"<tr><th>Eye aperture health</th><td>{Format(report.EyeApertureHealthPercent)}% | {report.EyeComparedSampleCount.ToString(CultureInfo.InvariantCulture)} samples | blink correlation {Format(report.EyeOpeningBlinkCorrelation)}</td></tr>"
            + $"<tr><th>Mouth aperture health</th><td>{Format(report.MouthApertureHealthPercent)}% | {report.MouthComparedSampleCount.ToString(CultureInfo.InvariantCulture)} samples | mouth evidence correlation {Format(report.MouthOpeningEvidenceCorrelation)}</td></tr>"
            + $"<tr><th>Jaw droop health</th><td>{Format(report.JawDroopAgreementHealthPercent)}% | {report.JawComparedSampleCount.ToString(CultureInfo.InvariantCulture)} samples | jaw evidence correlation {Format(report.JawDroopEvidenceCorrelation)}</td></tr>"
            + $"<tr><th>Correction rates</th><td>eye {Format(report.EyeMediaPipeCorrectionRate * 100d)}%, mouth {Format(report.MouthMediaPipeCorrectionRate * 100d)}%</td></tr>"
            + $"<tr><th>Artifact/reconstruction rates</th><td>eye artifact {Format(report.EyeArtifactRate * 100d)}%, eye reconstructed {Format(report.EyeReconstructedRate * 100d)}%, mouth reconstructed {Format(report.MouthReconstructedRate * 100d)}%</td></tr>"
            + "</table>";
    }

    private static string BuildList(IReadOnlyCollection<string> values, string emptyText)
    {
        return values.Count == 0
            ? $"<li>{Escape(emptyText)}</li>"
            : string.Concat(values.Select(value => $"<li>{Escape(value)}</li>"));
    }

    private static string FormatRate(double? rate)
    {
        return rate is double value ? $"{Format(value * 100d)}%" : "";
    }

    private static string Format(double? value)
    {
        return value is double number ? Format(number) : "";
    }

    private static string Format(double value)
    {
        return value.ToString("0.#", CultureInfo.InvariantCulture);
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

    private static string Escape(string value)
    {
        return WebUtility.HtmlEncode(value);
    }
}
