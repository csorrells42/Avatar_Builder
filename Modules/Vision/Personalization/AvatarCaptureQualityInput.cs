using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class AvatarCaptureQualityInput
{
    public int? VideoWidth { get; init; }

    public int? VideoHeight { get; init; }

    public double? FramesPerSecond { get; init; }

    public string? InputFormat { get; init; }

    public bool IsAutoCameraMode { get; init; }

    public FaceLandmarkFrame LandmarkFrame { get; init; } = FaceLandmarkFrame.None;

    public FaceLandmarkMetrics Metrics { get; init; } = FaceLandmarkMetrics.None;

    public FaceLockStabilityAnalysis Stability { get; init; } = FaceLockStabilityAnalysis.Waiting;

    public bool SubjectConfirmed { get; init; }

    public bool AvatarCaptureRequested { get; init; }

    public bool CaptureGateAccepted { get; init; }

    public string CaptureGateReason { get; init; } = "not started";
}
