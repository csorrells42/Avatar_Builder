using AvatarBuilder.Modules.Vision.Personalization;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class MeasurementAvatarCapturePlan
{
    public string SchemaVersion { get; set; } = "measurement-avatar-capture-plan-v1";

    public string PlanKind { get; set; } = "measurement-only-avatar-capture-plan";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public string SubjectId { get; set; } = PersonalFaceSubject.DefaultSubjectId;

    public string SubjectDisplayName { get; set; } = PersonalFaceSubject.DefaultSubjectDisplayName;

    public string SubjectCollectionMode { get; set; } = PersonalFaceSubject.ManualConfirmationMode;

    public string UnknownSubjectPolicy { get; set; } = PersonalFaceSubject.UnknownSubjectPolicy;

    public FaceReconstructionSubjectGate SubjectGate { get; set; } = new();

    public bool CanCollectMeasurements { get; set; }

    public string CollectionDecision { get; set; } = "waiting for subject-gated measurements";

    public long MeasurementJournalBytes { get; set; }

    public long MeasurementBudgetBytes { get; set; } = PersonalFaceMeasurementJournal.DefaultBudgetBytes;

    public double MeasurementBudgetUsedPercent { get; set; }

    public int TotalTargetMinutes { get; set; }

    public long EstimatedMeasurementBytes { get; set; }

    public double LowestReadinessScorePercent { get; set; }

    public List<MeasurementAvatarCapturePlanItem> Items { get; set; } = [];

    public List<string> PreSessionChecks { get; set; } = [];

    public List<string> StopRules { get; set; } = [];

    public string StoragePolicy { get; set; } =
        "Measurement-only capture plan. It describes what to collect next and stores no raw frames, images, video, full landmark meshes, or contour dumps.";

    public string SafetyBoundary { get; set; } =
        "Only collect for the confirmed subject, while symptom-free, with the subject checkbox enabled. Pause if the subject changes or symptoms start.";
}
