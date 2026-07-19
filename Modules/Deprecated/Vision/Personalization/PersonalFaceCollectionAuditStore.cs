using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class PersonalFaceCollectionAuditStore
{
    public const string DefaultJsonFileName = "personal_face_collection_audit.json";
    public const string DefaultHtmlFileName = "personal_face_collection_audit.html";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string FileName { get; }

    public PersonalFaceCollectionAuditStore(string fileName = DefaultJsonFileName)
    {
        FileName = fileName;
    }

    public string Write(string folder, PersonalFaceCollectionAudit audit)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        AtomicTextFileWriter.WriteAllText(path, JsonSerializer.Serialize(audit, JsonOptions), Encoding.UTF8);
        AtomicTextFileWriter.WriteAllText(GetHtmlPath(path), BuildHtml(audit), Encoding.UTF8);
        return path;
    }

    public static string WriteFile(string path, PersonalFaceCollectionAudit audit)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        AtomicTextFileWriter.WriteAllText(path, JsonSerializer.Serialize(audit, JsonOptions), Encoding.UTF8);
        AtomicTextFileWriter.WriteAllText(GetHtmlPath(path), BuildHtml(audit), Encoding.UTF8);
        return path;
    }

    public static string GetHtmlPath(string jsonPath)
    {
        return Path.ChangeExtension(jsonPath, ".html");
    }

    private static string BuildHtml(PersonalFaceCollectionAudit audit)
    {
        var rows = string.Concat(new[]
        {
            MetricRow("Frames reviewed", audit.TotalFramesReviewed.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Frames with face", $"{audit.FramesWithFace} ({FormatRate(audit.FaceDetectionRate)})"),
            MetricRow("Subject confirmed", $"{audit.SubjectConfirmedFrames} ({FormatRate(audit.SubjectConfirmedRate)})"),
            MetricRow("Personal model accepted", $"{audit.PersonalModelAcceptedFrames} ({FormatRate(audit.PersonalModelAcceptedRate)})"),
            MetricRow("Capture collectable", $"{audit.CaptureQualityCanCollectFrames} ({FormatRate(audit.CaptureQualityCollectableRate)})"),
            MetricRow("Avatar grade", $"{audit.CaptureQualityAvatarGradeFrames} ({FormatRate(audit.CaptureQualityAvatarGradeRate)})"),
            MetricRow("Subject gate off", audit.SubjectGateOffFrames.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Event/calibration hold", audit.EventLikeGateFrames.ToString(CultureInfo.InvariantCulture)),
            MetricRow("No-face gate", audit.NoFaceGateFrames.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Low-quality gate", audit.LowQualityGateFrames.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Tracking artifact gate", audit.TrackingArtifactGateFrames.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Subject mismatch gate", audit.SubjectMismatchGateFrames.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Tracking audit hold", audit.TrackingAuditHoldFrames.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Identity measured", audit.IdentityMeasuredFrames.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Identity gate ready", audit.IdentityAutoGateReadyFrames.ToString(CultureInfo.InvariantCulture)),
            MetricRow("Identity confidence", $"{Format(audit.AverageIdentityConfidencePercent)}% avg; {Format(audit.MinimumIdentityConfidencePercent)}% min"),
            MetricRow("Identity outlier frames", $"{audit.IdentityOutlierFrames}; max {audit.MaximumIdentityOutlierFeatureCount} feature(s)"),
            MetricRow("Average quality", $"{Format(audit.AverageCaptureQualityScorePercent)}%"),
            MetricRow("Minimum quality", $"{Format(audit.MinimumCaptureQualityScorePercent)}%"),
            MetricRow("Camera mode score", $"{Format(audit.AverageCaptureQualityCameraModeScorePercent)}%"),
            MetricRow("Face scale score", $"{Format(audit.AverageCaptureQualityFaceScaleScorePercent)}%"),
            MetricRow("Eye score", $"{Format(audit.AverageCaptureQualityEyeScorePercent)}%"),
            MetricRow("Mouth score", $"{Format(audit.AverageCaptureQualityMouthScorePercent)}%"),
            MetricRow("Glasses score", $"{Format(audit.AverageCaptureQualityGlassesScorePercent)}%"),
            MetricRow("Face width range", $"{Format(audit.MinimumFaceWidthPercent)}% to {Format(audit.MaximumFaceWidthPercent)}%"),
            MetricRow("Face height range", $"{Format(audit.MinimumFaceHeightPercent)}% to {Format(audit.MaximumFaceHeightPercent)}%")
        });
        var bars = string.Concat(new[]
        {
            ScoreBar("Face lock", audit.FaceDetectionRate * 100d),
            ScoreBar("Subject gate", audit.SubjectConfirmedRate * 100d),
            ScoreBar("Accepted", audit.PersonalModelAcceptedRate * 100d),
            ScoreBar("Collectable", audit.CaptureQualityCollectableRate * 100d),
            ScoreBar("Avatar grade", audit.CaptureQualityAvatarGradeRate * 100d),
            ScoreBar("Identity", audit.AverageIdentityConfidencePercent ?? 0d),
            ScoreBar("Quality", audit.AverageCaptureQualityScorePercent ?? 0d),
            ScoreBar("Camera", audit.AverageCaptureQualityCameraModeScorePercent ?? 0d),
            ScoreBar("Eyes", audit.AverageCaptureQualityEyeScorePercent ?? 0d),
            ScoreBar("Mouth", audit.AverageCaptureQualityMouthScorePercent ?? 0d)
        });
        return $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta http-equiv="refresh" content="5">
<title>{{H(audit.SubjectDisplayName)}} collection audit</title>
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
  grid-template-columns: minmax(320px, 1.15fr) minmax(300px, 0.85fr);
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
      <h1>{{H(audit.SubjectDisplayName)}} collection audit</h1>
      <div class="subtle">{{H(audit.Status)}}</div>
      <div class="subtle">Subject: {{H(audit.SubjectDisplayName)}} ({{H(audit.SubjectId)}}) | {{H(audit.StoragePolicy)}}</div>
    </div>
    <div class="big">{{FormatRate(audit.CaptureQualityCollectableRate)}}</div>
  </section>
  <section class="grid">
    <div class="panel">
      <h2>Collection Gates</h2>
      {{bars}}
    </div>
    <div class="panel">
      <h2>Next Actions</h2>
      <ul>{{BuildList(audit.NextActions, "No next actions.")}}</ul>
    </div>
    <div class="panel">
      <h2>Measurement Audit</h2>
      <table>{{rows}}</table>
    </div>
    <div class="panel">
      <h2>Top Rejections</h2>
      <ul>{{BuildList(audit.TopPersonalModelRejectionReasons, "No personal-model rejections.")}}</ul>
      <h2>Capture Issues</h2>
      <ul>{{BuildList(audit.TopCaptureQualityIssues, "No capture-quality issues.")}}</ul>
      <h2>Suggestions</h2>
      <ul>{{BuildList(audit.TopCaptureQualitySuggestions, "No capture-quality suggestions.")}}</ul>
    </div>
  </section>
</main>
</body>
</html>
""";
    }

    private static string ScoreBar(string label, double value)
    {
        value = Math.Clamp(value, 0d, 100d);
        return $"<div class=\"score\"><div>{H(label)}</div><div class=\"track\"><div class=\"fill\" style=\"--value:{value.ToString("0.###", CultureInfo.InvariantCulture)}%\"></div></div><div>{Format(value)}%</div></div>";
    }

    private static string MetricRow(string label, string value)
    {
        return $"<tr><th>{H(label)}</th><td>{H(value)}</td></tr>";
    }

    private static string BuildList(IReadOnlyList<string> values, string fallback)
    {
        if (values.Count == 0)
        {
            return $"<li>{H(fallback)}</li>";
        }

        return string.Concat(values.Select(value => $"<li>{H(value)}</li>"));
    }

    private static string FormatRate(double value)
    {
        return value.ToString("P0", CultureInfo.InvariantCulture);
    }

    private static string Format(double? value)
    {
        return value.HasValue ? Format(value.Value) : "n/a";
    }

    private static string Format(double value)
    {
        return value.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private static string H(string? value)
    {
        return WebUtility.HtmlEncode(value ?? "");
    }
}
