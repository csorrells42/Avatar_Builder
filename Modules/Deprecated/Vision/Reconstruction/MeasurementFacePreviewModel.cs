using AvatarBuilder.Modules.Vision.Personalization;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class MeasurementFacePreviewModel
{
    public string SchemaVersion { get; set; } = "measurement-face-preview-v1";

    public string BackendId { get; set; } = FaceReconstructionBackendIds.MeasurementOnlyPreview;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public string SubjectId { get; set; } = PersonalFaceSubject.DefaultSubjectId;

    public string SubjectDisplayName { get; set; } = PersonalFaceSubject.DefaultSubjectDisplayName;

    public string SubjectCollectionMode { get; set; } = PersonalFaceSubject.ManualConfirmationMode;

    public string UnknownSubjectPolicy { get; set; } = PersonalFaceSubject.UnknownSubjectPolicy;

    public FaceReconstructionSubjectGate SubjectGate { get; set; } = new();

    public bool CanRender { get; set; }

    public string RenderDecision { get; set; } = "waiting for subject-gated measurements";

    public string CoordinateSpace { get; set; } =
        "Normalized centered face space. Points carry X/Y/Z positions; A/B/C orientation is represented by preview metrics and pose buckets, not duplicated on every point.";

    public int ObservedSamples { get; set; }

    public int AcceptedSamples { get; set; }

    public int RejectedSamples { get; set; }

    public double AcceptedSampleWeight { get; set; }

    public double AcceptedRate { get; set; }

    public double ConfidencePercent { get; set; }

    public string GeometryProvenance { get; set; } = "waiting for subject-gated measurements";

    public bool TemplatePriorUsed { get; set; }

    public double TemplatePriorContributionPercent { get; set; }

    public double MeasurementContributionPercent { get; set; }

    public string TemplatePriorPolicy { get; set; } =
        "A canonical face scaffold may seed the preview only as a low-trust visual prior. It must not be written back into the personal model or counted as observed measurements.";

    public Dictionary<string, double?> Metrics { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, PersonalFaceContourShapeProfile> ContourShapeProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<MeasurementFacePreviewPoseBucket> PoseBuckets { get; set; } = [];

    public PersonalFacePoseBucketConsistencyReport PoseBucketConsistency { get; set; } = new();

    public List<MeasurementFacePreviewSurfaceEvidence> SurfaceEvidence { get; set; } = [];

    public List<MeasurementFacePreviewPoint> Points { get; set; } = [];

    public List<MeasurementFacePreviewPolyline> Polylines { get; set; } = [];

    public List<MeasurementFacePreviewSurfacePatch> SurfacePatches { get; set; } = [];

    public List<string> Warnings { get; set; } = [];

    public string StoragePolicy { get; set; } =
        "Measurement-only preview. No raw frames, images, video, or per-frame contour dumps are stored here; contour shape profiles are aggregate distributions only.";

    public string SafetyBoundary { get; set; } =
        "Output is a digital representation of a real person, not the living person.";
}
