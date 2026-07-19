namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarCaptureGuidanceState
{
    public string Title { get; set; } = "Capture Guidance";

    public string Detail { get; set; } = "Waiting for capture guidance.";

    public string Severity { get; set; } = AvatarCaptureGuidanceSeverity.Idle;
}

public static class AvatarCaptureGuidanceSeverity
{
    public const string Good = "good";

    public const string Warning = "warning";

    public const string Blocked = "blocked";

    public const string Idle = "idle";
}
