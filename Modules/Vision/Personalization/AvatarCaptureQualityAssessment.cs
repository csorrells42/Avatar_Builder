namespace AvatarBuilder.Modules.Vision.Personalization;

public sealed class AvatarCaptureQualityAssessment
{
    public static AvatarCaptureQualityAssessment Waiting { get; } = new()
    {
        Label = "waiting",
        PrimaryReason = "waiting for face landmarks",
        StatusLine = "Capture quality: waiting for face landmarks"
    };

    public string Label { get; init; } = "waiting";

    public double ScorePercent { get; init; }

    public bool CanCollectMeasurements { get; init; }

    public bool StrongEnoughForAvatarLearning { get; init; }

    public string PrimaryReason { get; init; } = "";

    public string StatusLine { get; init; } = "";

    public double CameraModeScorePercent { get; init; }

    public double FaceScaleScorePercent { get; init; }

    public double EyeEvidenceScorePercent { get; init; }

    public double MouthEvidenceScorePercent { get; init; }

    public double StabilityScorePercent { get; init; }

    public double GlassesRiskScorePercent { get; init; }

    public double StorageScorePercent { get; init; }

    public double? FaceWidthPercent { get; init; }

    public double? FaceHeightPercent { get; init; }

    public IReadOnlyList<string> Issues { get; init; } = [];

    public IReadOnlyList<string> Suggestions { get; init; } = [];
}
