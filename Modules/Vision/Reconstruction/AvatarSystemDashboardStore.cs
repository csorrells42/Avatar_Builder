using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarSystemDashboardStore
{
    public const string DefaultJsonFileName = "avatar_system.json";
    public const string DefaultHtmlFileName = "avatar_system.html";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string Write(string folder, AvatarSystemDashboard dashboard)
    {
        Directory.CreateDirectory(folder);
        var jsonPath = Path.Combine(folder, DefaultJsonFileName);
        AtomicTextFileWriter.WriteAllText(jsonPath, JsonSerializer.Serialize(dashboard, JsonOptions), Encoding.UTF8);
        AtomicTextFileWriter.WriteAllText(GetHtmlPath(jsonPath), BuildHtml(dashboard), Encoding.UTF8);
        return jsonPath;
    }

    public static string GetHtmlPath(string jsonPath)
    {
        return Path.Combine(Path.GetDirectoryName(jsonPath) ?? "", DefaultHtmlFileName);
    }

    private static string BuildHtml(AvatarSystemDashboard dashboard)
    {
        var captureClass = dashboard.AvatarCaptureActive
            ? "good"
            : dashboard.AvatarCaptureRequested ? "warn" : "muted";
        var quality = dashboard.CurrentCaptureQuality;
        var pose = dashboard.CurrentFaceFrameGeometry;
        var lane = dashboard.ReconstructionLane;

        var html = new StringBuilder();
        html.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"30\"><title>Avatar System</title>");
        html.AppendLine("<style>");
        html.AppendLine("body{margin:0;background:#080d12;color:#f5f8fb;font-family:Segoe UI,Arial,sans-serif;line-height:1.45}main{max-width:1040px;margin:0 auto;padding:28px}section{border:1px solid #243545;background:#101820;margin:16px 0;padding:18px}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:12px}.metric{background:#0c1218;border:1px solid #1d2c38;padding:12px}.label{color:#b9d7ef;font-size:12px;text-transform:uppercase;letter-spacing:.04em}.value{font-size:20px;font-weight:700}.good{color:#80e0a4}.warn{color:#ffd27a}.bad{color:#ff9a9a}.muted{color:#b9d7ef}a{color:#8fc7ff}ul{padding-left:20px}li{margin:5px 0}table{border-collapse:collapse;width:100%}td,th{border-bottom:1px solid #243545;padding:8px;text-align:left}th{color:#b9d7ef;font-weight:600}</style>");
        html.AppendLine("</head><body><main>");
        html.AppendLine($"<h1>Avatar System</h1><p class=\"muted\">Live report auto-refreshes every 30 seconds. Last updated {H(dashboard.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture))}.</p>");
        html.AppendLine($"<p class=\"muted\">{H(dashboard.StoragePolicy)}</p>");

        html.AppendLine("<section>");
        html.AppendLine($"<h2 class=\"{captureClass}\">{H(dashboard.AvatarCaptureStatus)}</h2>");
        html.AppendLine($"<p>{H(dashboard.AvatarCaptureCorrection)}</p>");
        html.AppendLine("<div class=\"grid\">");
        html.AppendLine(Metric("Subject", string.IsNullOrWhiteSpace(dashboard.SubjectDisplayName) ? dashboard.SubjectId : dashboard.SubjectDisplayName));
        html.AppendLine(Metric("Avatar user logged in", dashboard.UserLoggedIn ? "Yes" : "No"));
        html.AppendLine(Metric("Capture switch", dashboard.AvatarCaptureRequested ? "Started" : "Stopped"));
        html.AppendLine(Metric("Currently capturing", dashboard.AvatarCaptureActive ? "Yes" : "No"));
        html.AppendLine(Metric("Current quality", $"{quality.Label} {quality.ScorePercent:0}%"));
        html.AppendLine(Metric("3DDFA lane", lane.AvatarReconstructionStatus));
        html.AppendLine(Metric("Dense reconstruction", lane.TrustLevel));
        html.AppendLine(Metric("Fast tracking", lane.FastTrackingStatus));
        html.AppendLine(Metric("3DDFA samples", dashboard.LastGoodThreeDdfaSampleCount.ToString(CultureInfo.InvariantCulture)));
        html.AppendLine(Metric("Retained observations", dashboard.RetainedAvatarObservationCount.ToString(CultureInfo.InvariantCulture)));
        html.AppendLine(Metric("Storage revision", dashboard.StorageRevision.ToString(CultureInfo.InvariantCulture)));
        html.AppendLine(Metric("Accepted lifetime", dashboard.LifetimeAcceptedObservationCount.ToString(CultureInfo.InvariantCulture)));
        html.AppendLine(Metric("Rejected lifetime", dashboard.LifetimeRejectedObservationCount.ToString(CultureInfo.InvariantCulture)));
        html.AppendLine(Metric("Model confidence", $"{dashboard.AvatarModelConfidencePercent:0.#}%"));
        html.AppendLine(Metric("Model coverage", $"{dashboard.AvatarModelCoveragePercent:0.#}%"));
        html.AppendLine(Metric("Model convergence", $"{dashboard.AvatarModelConvergencePercent:0.#}% {dashboard.AvatarModelConvergenceLabel}"));
        html.AppendLine(Metric("Model audit", dashboard.AvatarModelAuditStatus));
        html.AppendLine(Metric("Z apparent", pose.HasFace && pose.ApparentDistanceUnits is { } apparent ? $"{apparent:0.###} {pose.ApparentDistanceUnitName}" : "waiting"));
        html.AppendLine(Metric("Z source", string.IsNullOrWhiteSpace(pose.DistanceSource) ? "waiting" : pose.DistanceSource));
        html.AppendLine(Metric("Selected tracker A/B/C", pose.HasFace ? $"{pose.ARotationAroundXDegrees:0.#} / {pose.BRotationAroundYDegrees:0.#} / {pose.CRotationAroundZDegrees:0.#} deg" : "waiting"));
        html.AppendLine("</div>");
        html.AppendLine("</section>");

        html.AppendLine("<section><h2>Active Backend Boundary</h2>");
        html.AppendLine($"<p>{H(dashboard.FastTrackingSummary)}</p>");
        html.AppendLine($"<p>{H(dashboard.AvatarReconstructionSummary)}</p>");
        html.AppendLine("<ul>");
        html.AppendLine("<li>Eye, jaw, brow, mouth, face lock, and overlay cues stay in the fast tracking lane.</li>");
        html.AppendLine("<li>3DDFA/ONNX owns dense avatar geometry, head pose, depth, and reconstruction trust.</li>");
        html.AppendLine("<li>The retired measurement-learning backend is not updating avatar geometry from face-cue measurements.</li>");
        html.AppendLine("<li>The stored avatar model separates base identity shape from expression range so sleepy/jaw-droop frames can teach motion without permanently reshaping the identity face.</li>");
        html.AppendLine("</ul>");
        html.AppendLine("</section>");

        html.AppendLine("<section><h2>Review Links</h2>");
        if (!string.IsNullOrWhiteSpace(dashboard.AvatarModelHtmlPath))
        {
            html.AppendLine($"<p><a href=\"{H(dashboard.AvatarModelHtmlPath)}\">Open Avatar Model Progress</a></p>");
        }

        if (!string.IsNullOrWhiteSpace(dashboard.AvatarModelAuditHtmlPath))
        {
            html.AppendLine($"<p><a href=\"{H(dashboard.AvatarModelAuditHtmlPath)}\">Open Avatar Model Regression Audit</a></p>");
        }

        if (!string.IsNullOrWhiteSpace(dashboard.LastGoodThreeDdfaHtmlPath))
        {
            html.AppendLine($"<p><a href=\"{H(dashboard.LastGoodThreeDdfaHtmlPath)}\">Open 3DDFA Last 5 Dense Reconstructions</a></p>");
        }

        html.AppendLine($"<p class=\"muted\">Avatar model: {H(dashboard.AvatarModelStatus)} {H(dashboard.AvatarModelCoverageSummary)}</p>");
        html.AppendLine($"<p class=\"muted\">Model audit: {H(dashboard.AvatarModelAuditSummary)}</p>");
        html.AppendLine("</section>");

        html.AppendLine("<section><h2>Reconstruction Lane Warnings</h2>");
        html.AppendLine(List(lane.Warnings, "No current reconstruction-lane warnings."));
        html.AppendLine("</section>");
        html.AppendLine("</main></body></html>");
        return html.ToString();
    }

    private static string Metric(string label, string value)
    {
        return $"<div class=\"metric\"><div class=\"label\">{H(label)}</div><div class=\"value\">{H(value)}</div></div>";
    }

    private static string List(IEnumerable<string> values, string fallback)
    {
        var items = values
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(item => $"<li>{H(item)}</li>")
            .ToList();
        return items.Count == 0 ? $"<p class=\"muted\">{H(fallback)}</p>" : $"<ul>{string.Join("", items)}</ul>";
    }

    private static string H(string? value)
    {
        return WebUtility.HtmlEncode(value ?? "");
    }
}
