using System;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public static class AvatarCaptureGuidanceAdvisor
{
	public static AvatarCaptureGuidanceState Create(AvatarCaptureGuidanceInput input)
	{
		ArgumentNullException.ThrowIfNull(input, "input");
		if (!input.UserLoggedIn)
		{
			return Blocked("Login required", "Avatar capture is stopped. Use File > Login to identify the person in front of the camera.");
		}
		if (!input.CameraActive)
		{
			return Warning("Turn camera on", "Turn the camera on, keep your face visible, then use Start Avatar Capture when you want reconstruction samples collected.");
		}
		if (!input.FaceLocked)
		{
			return Warning("Get face lock", "Avatar capture guidance is waiting for a stable face, eye, and mouth lock. Sit where the overlay can see your full face and glasses clearly.");
		}
		if (!input.AvatarLearningRequested)
		{
			return Warning("Ready for 3D capture", "Click Start Avatar Capture to collect dense reconstruction samples. Natural blinks, speech, small head turns, and distance changes are useful once capture is running.");
		}
		if (!input.CaptureQuality.CanCollectMeasurements)
		{
			string text = input.CaptureQuality.Suggestions.Count > 0 ? input.CaptureQuality.Suggestions[0] : input.CaptureQuality.PrimaryReason ?? "Improve camera mode, lighting, face scale, eye visibility, mouth visibility, or stability.";
			return Warning("Fix capture quality", "Avatar capture is on, but sample collection is paused: " + Clean(input.CaptureQuality.PrimaryReason, "capture quality is not ready") + ". Fix: " + text);
		}
		return Good("3D capture running", "The selected dense backend is the avatar reconstruction lane. Keep a relaxed, logged-in session running with natural blinks, speech, small head turns, and distance changes.");
	}

	private static AvatarCaptureGuidanceState Good(string title, string detail, string severity = "good")
	{
		return CreateState(title, detail, severity);
	}

	private static AvatarCaptureGuidanceState Warning(string title, string detail)
	{
		return CreateState(title, detail, "warning");
	}

	private static AvatarCaptureGuidanceState Blocked(string title, string detail)
	{
		return CreateState(title, detail, "blocked");
	}

	private static AvatarCaptureGuidanceState CreateState(string title, string detail, string severity)
	{
		return new AvatarCaptureGuidanceState(title, detail, severity);
	}

	private static string Clean(string? value, string fallback)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			return value.Trim();
		}
		return fallback;
	}
}
