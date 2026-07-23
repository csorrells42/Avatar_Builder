using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;
using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Personalization;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarSystemDashboardStore
{
	public const string DefaultJsonFileName = "avatar_system.json";

	public const string DefaultHtmlFileName = "avatar_system.html";

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
	{
		WriteIndented = true
	};

	public string Write(string folder, AvatarSystemDashboard dashboard)
	{
		Directory.CreateDirectory(folder);
		string text = Path.Combine(folder, "avatar_system.json");
		AtomicTextFileWriter.WriteAllText(text, JsonSerializer.Serialize(dashboard, JsonOptions), Encoding.UTF8);
		AtomicTextFileWriter.WriteAllText(GetHtmlPath(text), BuildHtml(dashboard), Encoding.UTF8);
		return text;
	}

	public static string GetHtmlPath(string jsonPath)
	{
		return Path.Combine(Path.GetDirectoryName(jsonPath) ?? "", "avatar_system.html");
	}

	private static string BuildHtml(AvatarSystemDashboard dashboard)
	{
		string value = (dashboard.AvatarCaptureActive ? "good" : (dashboard.AvatarCaptureRequested ? "warn" : "muted"));
		AvatarCaptureQualityAssessment currentCaptureQuality = dashboard.CurrentCaptureQuality;
		FaceFrameGeometry currentFaceFrameGeometry = dashboard.CurrentFaceFrameGeometry;
		FaceReconstructionLaneStatus reconstructionLane = dashboard.ReconstructionLane;
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"30\"><title>Avatar System</title>");
		stringBuilder.AppendLine("<style>");
		stringBuilder.AppendLine("body{margin:0;background:#080d12;color:#f5f8fb;font-family:Segoe UI,Arial,sans-serif;line-height:1.45}main{max-width:1040px;margin:0 auto;padding:28px}section{border:1px solid #243545;background:#101820;margin:16px 0;padding:18px}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:12px}.metric{background:#0c1218;border:1px solid #1d2c38;padding:12px}.label{color:#b9d7ef;font-size:12px;text-transform:uppercase;letter-spacing:.04em}.value{font-size:20px;font-weight:700}.good{color:#80e0a4}.warn{color:#ffd27a}.bad{color:#ff9a9a}.muted{color:#b9d7ef}a{color:#8fc7ff}ul{padding-left:20px}li{margin:5px 0}table{border-collapse:collapse;width:100%}td,th{border-bottom:1px solid #243545;padding:8px;text-align:left}th{color:#b9d7ef;font-weight:600}</style>");
		stringBuilder.AppendLine("</head><body><main>");
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder3 = stringBuilder2;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(102, 1, stringBuilder2);
		handler.AppendLiteral("<h1>Avatar System</h1><p class=\"muted\">Live report auto-refreshes every 30 seconds. Last updated ");
		handler.AppendFormatted(H(dashboard.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)));
		handler.AppendLiteral(".</p>");
		stringBuilder3.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder4 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(21, 1, stringBuilder2);
		handler.AppendLiteral("<p class=\"muted\">");
		handler.AppendFormatted(H(dashboard.StoragePolicy));
		handler.AppendLiteral("</p>");
		stringBuilder4.AppendLine(ref handler);
		stringBuilder.AppendLine("<section>");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder5 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(18, 2, stringBuilder2);
		handler.AppendLiteral("<h2 class=\"");
		handler.AppendFormatted(value);
		handler.AppendLiteral("\">");
		handler.AppendFormatted(H(dashboard.AvatarCaptureStatus));
		handler.AppendLiteral("</h2>");
		stringBuilder5.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder6 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder2);
		handler.AppendLiteral("<p>");
		handler.AppendFormatted(H(dashboard.AvatarCaptureCorrection));
		handler.AppendLiteral("</p>");
		stringBuilder6.AppendLine(ref handler);
		stringBuilder.AppendLine("<div class=\"grid\">");
		stringBuilder.AppendLine(Metric("Subject", string.IsNullOrWhiteSpace(dashboard.SubjectDisplayName) ? dashboard.SubjectId : dashboard.SubjectDisplayName));
		stringBuilder.AppendLine(Metric("Avatar user logged in", dashboard.UserLoggedIn ? "Yes" : "No"));
		stringBuilder.AppendLine(Metric("Capture switch", dashboard.AvatarCaptureRequested ? "Started" : "Stopped"));
		stringBuilder.AppendLine(Metric("Currently capturing", dashboard.AvatarCaptureActive ? "Yes" : "No"));
		stringBuilder.AppendLine(Metric("Current quality", $"{currentCaptureQuality.Label} {currentCaptureQuality.ScorePercent:0}%"));
		stringBuilder.AppendLine(Metric("Reconstruction lane", reconstructionLane.AvatarReconstructionStatus));
		stringBuilder.AppendLine(Metric("Dense reconstruction", reconstructionLane.TrustLevel));
		stringBuilder.AppendLine(Metric("Fast tracking", reconstructionLane.FastTrackingStatus));
		stringBuilder.AppendLine(Metric("Retained observations", dashboard.RetainedAvatarObservationCount.ToString(CultureInfo.InvariantCulture)));
		stringBuilder.AppendLine(Metric("Storage revision", dashboard.StorageRevision.ToString(CultureInfo.InvariantCulture)));
		stringBuilder.AppendLine(Metric("Accepted lifetime", dashboard.LifetimeAcceptedObservationCount.ToString(CultureInfo.InvariantCulture)));
		stringBuilder.AppendLine(Metric("Rejected lifetime", dashboard.LifetimeRejectedObservationCount.ToString(CultureInfo.InvariantCulture)));
		stringBuilder.AppendLine(Metric("Model confidence", $"{dashboard.AvatarModelConfidencePercent:0.#}%"));
		stringBuilder.AppendLine(Metric("Model coverage", $"{dashboard.AvatarModelCoveragePercent:0.#}%"));
		stringBuilder.AppendLine(Metric("Model convergence", $"{dashboard.AvatarModelConvergencePercent:0.#}% {dashboard.AvatarModelConvergenceLabel}"));
		stringBuilder.AppendLine(Metric("Identity fit RMSE", $"{dashboard.AvatarIdentityMappingLandmarkRmsePercent:0.##}%"));
		stringBuilder.AppendLine(Metric("Last identity improvement", $"{dashboard.AvatarIdentityMappingImprovementPercent:0.##}%"));
		stringBuilder.AppendLine(Metric("Model audit", dashboard.AvatarModelAuditStatus));
		object value2;
		if (currentFaceFrameGeometry.HasFace)
		{
			double? apparentDistanceUnits = currentFaceFrameGeometry.ApparentDistanceUnits;
			if (apparentDistanceUnits.HasValue)
			{
				double valueOrDefault = apparentDistanceUnits.GetValueOrDefault();
				value2 = $"{valueOrDefault:0.###} {currentFaceFrameGeometry.ApparentDistanceUnitName}";
				goto IL_056a;
			}
		}
		value2 = "waiting";
		goto IL_056a;
		IL_056a:
		stringBuilder.AppendLine(Metric("Z apparent", (string)value2));
		stringBuilder.AppendLine(Metric("Z source", string.IsNullOrWhiteSpace(currentFaceFrameGeometry.DistanceSource) ? "waiting" : currentFaceFrameGeometry.DistanceSource));
		stringBuilder.AppendLine(Metric("Selected tracker A/B/C", currentFaceFrameGeometry.HasFace ? $"{currentFaceFrameGeometry.ARotationAroundXDegrees:0.#} / {currentFaceFrameGeometry.BRotationAroundYDegrees:0.#} / {currentFaceFrameGeometry.CRotationAroundZDegrees:0.#} deg" : "waiting"));
		stringBuilder.AppendLine("</div>");
		stringBuilder.AppendLine("</section>");
		stringBuilder.AppendLine("<section><h2>Active Backend Boundary</h2>");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder7 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder2);
		handler.AppendLiteral("<p>");
		handler.AppendFormatted(H(dashboard.FastTrackingSummary));
		handler.AppendLiteral("</p>");
		stringBuilder7.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder8 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder2);
		handler.AppendLiteral("<p>");
		handler.AppendFormatted(H(dashboard.AvatarReconstructionSummary));
		handler.AppendLiteral("</p>");
		stringBuilder8.AppendLine(ref handler);
		stringBuilder.AppendLine("<ul>");
		stringBuilder.AppendLine("<li>Eye, jaw, brow, mouth, face lock, and overlay cues stay in the fast tracking lane.</li>");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder9 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(81, 1, stringBuilder2);
		handler.AppendLiteral("<li>");
		handler.AppendFormatted(H(reconstructionLane.AvatarReconstructionLaneName));
		handler.AppendLiteral(" owns dense avatar geometry, head pose, depth, and reconstruction trust.</li>");
		stringBuilder9.AppendLine(ref handler);
		stringBuilder.AppendLine("<li>The retired measurement-learning backend is not updating avatar geometry from face-cue measurements.</li>");
		stringBuilder.AppendLine("<li>The stored avatar model separates base identity shape from expression range so sleepy/jaw-droop frames can teach motion without permanently reshaping the identity face.</li>");
		stringBuilder.AppendLine("</ul>");
		stringBuilder.AppendLine("</section>");
		stringBuilder.AppendLine("<section><h2>Review Links</h2>");
		if (!string.IsNullOrWhiteSpace(dashboard.AvatarModelHtmlPath))
		{
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder10 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(48, 1, stringBuilder2);
			handler.AppendLiteral("<p><a href=\"");
			handler.AppendFormatted(H(dashboard.AvatarModelHtmlPath));
			handler.AppendLiteral("\">Open Avatar Model Progress</a></p>");
			stringBuilder10.AppendLine(ref handler);
		}
		if (!string.IsNullOrWhiteSpace(dashboard.AvatarModelAuditHtmlPath))
		{
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder11 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(56, 1, stringBuilder2);
			handler.AppendLiteral("<p><a href=\"");
			handler.AppendFormatted(H(dashboard.AvatarModelAuditHtmlPath));
			handler.AppendLiteral("\">Open Avatar Model Regression Audit</a></p>");
			stringBuilder11.AppendLine(ref handler);
		}
		stringBuilder.AppendLine("<p class=\"muted\">Use Review Avatar Data in Avatar Builder to browse every retained reconstruction scan and its paired camera image.</p>");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder12 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(36, 2, stringBuilder2);
		handler.AppendLiteral("<p class=\"muted\">Avatar model: ");
		handler.AppendFormatted(H(dashboard.AvatarModelStatus));
		handler.AppendLiteral(" ");
		handler.AppendFormatted(H(dashboard.AvatarModelCoverageSummary));
		handler.AppendLiteral("</p>");
		stringBuilder12.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder13 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(39, 1, stringBuilder2);
		handler.AppendLiteral("<p class=\"muted\">Identity mapping: ");
		handler.AppendFormatted(H(dashboard.AvatarIdentityMappingStatus));
		handler.AppendLiteral("</p>");
		stringBuilder13.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder14 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(34, 1, stringBuilder2);
		handler.AppendLiteral("<p class=\"muted\">Model audit: ");
		handler.AppendFormatted(H(dashboard.AvatarModelAuditSummary));
		handler.AppendLiteral("</p>");
		stringBuilder14.AppendLine(ref handler);
		stringBuilder.AppendLine("</section>");
		stringBuilder.AppendLine("<section><h2>Reconstruction Lane Warnings</h2>");
		stringBuilder.AppendLine(List(reconstructionLane.Warnings, "No current reconstruction-lane warnings."));
		stringBuilder.AppendLine("</section>");
		stringBuilder.AppendLine("</main></body></html>");
		return stringBuilder.ToString();
	}

	private static string Metric(string label, string value)
	{
		return $"<div class=\"metric\"><div class=\"label\">{H(label)}</div><div class=\"value\">{H(value)}</div></div>";
	}

	private static string List(IEnumerable<string> values, string fallback)
	{
		List<string> list = (from item in values
			where !string.IsNullOrWhiteSpace(item)
			select "<li>" + H(item) + "</li>").ToList();
		if (list.Count != 0)
		{
			return "<ul>" + string.Join("", list) + "</ul>";
		}
		return "<p class=\"muted\">" + H(fallback) + "</p>";
	}

	private static string H(string? value)
	{
		return WebUtility.HtmlEncode(value ?? "");
	}
}
