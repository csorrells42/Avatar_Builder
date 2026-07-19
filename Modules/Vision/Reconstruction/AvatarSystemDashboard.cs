using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Personalization;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarSystemDashboard
{
    public string SchemaVersion { get; set; } = "avatar-capture-dashboard-v1";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public string SubjectId { get; set; } = "";

    public string SubjectDisplayName { get; set; } = "";

    public bool UserLoggedIn { get; set; }

    public bool AvatarCaptureRequested { get; set; }

    public bool AvatarCaptureActive { get; set; }

    public string AvatarCaptureStatus { get; set; } = "";

    public string AvatarCaptureCorrection { get; set; } = "";

    public AvatarCaptureQualityAssessment CurrentCaptureQuality { get; set; } = AvatarCaptureQualityAssessment.Waiting;

    public FaceFrameGeometry CurrentFaceFrameGeometry { get; set; } = FaceFrameGeometry.None;

    public FaceReconstructionLaneStatus ReconstructionLane { get; set; } = FaceReconstructionLaneStatus.Waiting;

    public string FastTrackingSummary { get; set; } =
        "MediaPipe/OpenCV eye, jaw, brow, mouth, and face tracking remains live for overlays and capture measurements.";

    public string AvatarReconstructionSummary { get; set; } =
        "3DDFA/ONNX is the active avatar reconstruction lane for dense face geometry, pose, and depth.";

    public int LastGoodThreeDdfaSampleCount { get; set; }

    public string LastGoodThreeDdfaHtmlPath { get; set; } = "";

    public string AvatarModelStatus { get; set; } = "waiting for stored 3DDFA observations";

    public int AvatarModelObservationCount { get; set; }

    public double AvatarModelConfidencePercent { get; set; }

    public double AvatarModelCoveragePercent { get; set; }

    public string AvatarModelCoverageSummary { get; set; } = "waiting";

    public string AvatarModelHtmlPath { get; set; } = "";

    public string AvatarModelAuditStatus { get; set; } = "waiting for the first model baseline";

    public string AvatarModelAuditSummary { get; set; } = "";

    public string AvatarModelAuditHtmlPath { get; set; } = "";

    public string StoragePolicy { get; set; } =
        "Avatar capture stores bounded 3DDFA observation data, review JSON/HTML, a derived identity/expression model, and a 30-day rebuild audit. The retired measurement-learning backend is not updating avatar geometry.";
}
