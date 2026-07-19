using AvatarBuilder.Modules.Vision.Personalization;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class MeasurementAvatarTrainingPackage
{
    public string SchemaVersion { get; set; } = "measurement-avatar-training-package-v1";

    public string PackageKind { get; set; } = "measurement-only-avatar-training-package";

    public string BackendId { get; set; } = FaceReconstructionBackendIds.MeasurementOnlyPreview;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public string SubjectId { get; set; } = PersonalFaceSubject.DefaultSubjectId;

    public string SubjectDisplayName { get; set; } = PersonalFaceSubject.DefaultSubjectDisplayName;

    public string SubjectCollectionMode { get; set; } = PersonalFaceSubject.ManualConfirmationMode;

    public string UnknownSubjectPolicy { get; set; } = PersonalFaceSubject.UnknownSubjectPolicy;

    public string IdentityGatePolicy { get; set; } = PersonalFaceSubject.IdentityGatePolicy;

    public FaceReconstructionSubjectGate SubjectGate { get; set; } = new();

    public bool CanUseForAvatarTraining { get; set; }

    public string TrainingDecision { get; set; } = "waiting for subject-gated measurements";

    public string CoordinateSpace { get; set; } =
        "Normalized measurement space. Values describe learned face proportions and motion behavior, not raw frame pixels.";

    public string ProvenancePolicy { get; set; } =
        "Template priors may bootstrap visualization and rig topology only; personal model fields remain measurement-derived and subject-gated.";

    public bool AllowsTemplatePriorBootstrap { get; set; } = true;

    public double TemplatePriorContributionPercent { get; set; }

    public double MeasurementContributionPercent { get; set; }

    public string TemplatePriorPolicy { get; set; } =
        "Canonical seed geometry is a low-trust prior for early preview behavior. It is not evidence that the subject has those measurements.";

    public int ObservedSamples { get; set; }

    public int AcceptedBaselineSamples { get; set; }

    public double AcceptedSampleWeight { get; set; }

    public PersonalFaceLearningStability LearningStability { get; set; } = new();

    public int MotionUsableObservations { get; set; }

    public int MotionPairs { get; set; }

    public int IdentitySignatureSamples { get; set; }

    public long MeasurementJournalBytes { get; set; }

    public long MeasurementBudgetBytes { get; set; } = PersonalFaceMeasurementJournal.DefaultBudgetBytes;

    public double MeasurementBudgetUsedPercent { get; set; }

    public MeasurementAvatarReadinessScores Readiness { get; set; } = new();

    public Dictionary<string, MeasurementAvatarTrainingMetric> NeutralFaceProfile { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, MeasurementAvatarTrainingMetric> MotionProfile { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, MeasurementAvatarTrainingMetric> IdentityProfile { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<PersonalFacePoseBucketProfile> PoseCoverageProfile { get; set; } = [];

    public PersonalFacePoseBucketConsistencyReport PoseBucketConsistency { get; set; } = new();

    public Dictionary<string, PersonalFaceContourShapeProfile> ContourShapeProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public PersonalFaceApertureConsistencyReport ApertureConsistency { get; set; } = new();

    public FaceReconstructionLaneStatus ReconstructionLane { get; set; } = FaceReconstructionLaneStatus.Waiting;

    public Dictionary<string, MeasurementAvatarTrainingMetric> QualityProfile { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<MeasurementAvatarTrainingArtifact> SourceArtifacts { get; set; } = [];

    public List<string> Strengths { get; set; } = [];

    public List<string> Warnings { get; set; } = [];

    public List<string> NextCaptureSuggestions { get; set; } = [];

    public List<string> IntegrationNotes { get; set; } = [];

    public string StoragePolicy { get; set; } =
        "Measurement-only avatar training package. No raw frames, images, video, full landmark meshes, or per-frame contour dumps are stored here; contour shape profiles are aggregate distributions only.";

    public string SafetyBoundary { get; set; } =
        "Any consumer must identify itself as a digital representation of a real person and not the living person whenever identity matters.";
}
