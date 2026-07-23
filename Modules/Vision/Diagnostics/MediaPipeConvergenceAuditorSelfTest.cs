using System;
using System.IO;

namespace AvatarBuilder.Modules.Vision.Diagnostics;

public static class MediaPipeConvergenceAuditorSelfTest
{
	public static MediaPipeConvergenceAuditorSelfTestResult Run(string outputRoot)
	{
		try
		{
			using MediaPipeConvergenceAuditor mediaPipeConvergenceAuditor = new MediaPipeConvergenceAuditor();
			mediaPipeConvergenceAuditor.SetOutputRoot(outputRoot);
			mediaPipeConvergenceAuditor.MarkEvent("Self-test marker", "Verifies report and marker persistence.");
			mediaPipeConvergenceAuditor.EnsureReport();
			string htmlPath = mediaPipeConvergenceAuditor.GetHtmlPath();
			Require(File.Exists(htmlPath), "The HTML audit report was not created.");
			Require(File.ReadAllText(htmlPath).Contains("MediaPipe Convergence Audit", StringComparison.Ordinal), "The HTML audit report did not contain its expected title.");
			string text = Path.GetDirectoryName(htmlPath) ?? throw new InvalidOperationException("The audit report did not have a session folder.");
			Require(File.Exists(Path.Combine(text, "mediapipe_convergence_summary.json")), "The JSON summary was not created.");
			Require(File.Exists(Path.Combine(text, "mediapipe_convergence_markers.csv")), "The marker log was not created.");
			return new MediaPipeConvergenceAuditorSelfTestResult(Succeeded: true, "PASS: MediaPipe convergence audit created its HTML, JSON, and marker artifacts at " + text + ".");
		}
		catch (Exception ex)
		{
			return new MediaPipeConvergenceAuditorSelfTestResult(Succeeded: false, "FAIL: " + ex.Message);
		}
	}

	private static void Require(bool condition, string message)
	{
		if (!condition)
		{
			throw new InvalidOperationException(message);
		}
	}
}
