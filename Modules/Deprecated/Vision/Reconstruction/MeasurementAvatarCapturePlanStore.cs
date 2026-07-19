using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class MeasurementAvatarCapturePlanStore
{
    public const string JsonFileName = "measurement_avatar_capture_plan.json";
    public const string HtmlFileName = "measurement_avatar_capture_plan.html";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public MeasurementAvatarCapturePlanFiles Write(string folder, MeasurementAvatarCapturePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        Directory.CreateDirectory(folder);
        var jsonPath = Path.Combine(folder, JsonFileName);
        var htmlPath = Path.Combine(folder, HtmlFileName);
        AtomicTextFileWriter.WriteAllText(jsonPath, JsonSerializer.Serialize(plan, JsonOptions), Encoding.UTF8);
        AtomicTextFileWriter.WriteAllText(htmlPath, BuildHtml(plan), Encoding.UTF8);
        return new MeasurementAvatarCapturePlanFiles(jsonPath, htmlPath);
    }

    private static string BuildHtml(MeasurementAvatarCapturePlan plan)
    {
        var title = $"{plan.SubjectDisplayName} avatar capture plan";
        var items = string.Concat(plan.Items.Select(item =>
            $$"""
<tr>
  <td>{{item.Priority.ToString(CultureInfo.InvariantCulture)}}</td>
  <td>{{Escape(item.Category)}}</td>
  <td><strong>{{Escape(item.Title)}}</strong><br><span class="subtle">{{Escape(item.Instructions)}}</span></td>
  <td>{{Escape(item.WhyItMatters)}}</td>
  <td>{{Escape(item.RelatedScoreName)}} {{Format(item.RelatedScorePercent)}}%</td>
  <td>{{item.TargetMinutes.ToString(CultureInfo.InvariantCulture)}} min<br><span class="subtle">{{Escape(FormatBytes(item.EstimatedMeasurementBytes))}}</span></td>
  <td>{{Escape(item.CompleteWhen)}}</td>
</tr>
"""));

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
.subtle { color: var(--muted); }
.panel {
  border: 1px solid var(--line);
  background: var(--panel);
  border-radius: 6px;
  padding: 14px;
  margin-top: 14px;
}
.hero {
  display: flex;
  justify-content: space-between;
  gap: 14px;
  align-items: end;
}
.big {
  font-size: 36px;
  line-height: 1;
  color: var(--accent);
}
table { width: 100%; border-collapse: collapse; }
th, td {
  border-bottom: 1px solid #1c3042;
  padding: 8px;
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
  .hero { display: block; }
}
</style>
</head>
<body>
<main>
  <section class="panel hero">
    <div>
      <h1>{{Escape(title)}}</h1>
      <div class="subtle">{{Escape(plan.CollectionDecision)}}</div>
      <div class="subtle">Subject: {{Escape(plan.SubjectDisplayName)}} ({{Escape(plan.SubjectId)}}) | Gate: {{Escape(plan.SubjectGate.GateDecision)}} - {{Escape(plan.SubjectGate.Reason)}}</div>
      <div class="subtle">{{Escape(plan.StoragePolicy)}}</div>
      <div class="subtle">{{Escape(plan.SafetyBoundary)}}</div>
    </div>
    <div class="big">{{plan.TotalTargetMinutes.ToString(CultureInfo.InvariantCulture)}} min</div>
  </section>
  <section class="panel">
    <h2>Session Budget</h2>
    <table>
      <tr><th>Estimated measurement data</th><td>{{Escape(FormatBytes(plan.EstimatedMeasurementBytes))}}</td></tr>
      <tr><th>Current measurement storage</th><td>{{Escape(FormatBytes(plan.MeasurementJournalBytes))}} / {{Escape(FormatBytes(plan.MeasurementBudgetBytes))}} ({{Format(plan.MeasurementBudgetUsedPercent)}}%)</td></tr>
      <tr><th>Lowest readiness score</th><td>{{Format(plan.LowestReadinessScorePercent)}}%</td></tr>
      <tr><th>Can collect</th><td>{{plan.CanCollectMeasurements.ToString(CultureInfo.InvariantCulture)}}</td></tr>
    </table>
  </section>
  <section class="panel">
    <h2>Capture Items</h2>
    <table>
      <tr><th>Priority</th><th>Category</th><th>What to do</th><th>Why</th><th>Score</th><th>Target</th><th>Complete when</th></tr>
      {{items}}
    </table>
  </section>
  <section class="panel">
    <h2>Pre-Session Checks</h2>
    <ul>{{BuildList(plan.PreSessionChecks, "No pre-session checks.")}}</ul>
    <h2 style="margin-top:16px">Stop Rules</h2>
    <ul>{{BuildList(plan.StopRules, "No stop rules.")}}</ul>
  </section>
</main>
</body>
</html>
""";
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

    private static string Format(double value)
    {
        return value.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private static string Escape(string value)
    {
        return WebUtility.HtmlEncode(value);
    }
}

public sealed record MeasurementAvatarCapturePlanFiles(string JsonPath, string HtmlPath);
