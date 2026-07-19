namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class MeasurementAvatarCapturePlanItem
{
    public string Id { get; set; } = "";

    public int Priority { get; set; }

    public string Category { get; set; } = "";

    public string Title { get; set; } = "";

    public string Instructions { get; set; } = "";

    public string WhyItMatters { get; set; } = "";

    public string RelatedScoreName { get; set; } = "";

    public double RelatedScorePercent { get; set; }

    public int TargetMinutes { get; set; }

    public long EstimatedMeasurementBytes { get; set; }

    public string CompleteWhen { get; set; } = "";

    public bool RequiresSubjectConfirmation { get; set; } = true;

    public bool RequiresSymptomFreeState { get; set; } = true;

    public bool NoRawMediaNeeded { get; set; } = true;
}
