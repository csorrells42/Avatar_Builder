using System;
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

	public string FastTrackingSummary { get; set; } = "The selected face-box system supplies live eye, jaw, brow, mouth, face, overlay, and capture measurements.";

	public string AvatarReconstructionSummary { get; set; } = "The selected dense backend is the active avatar reconstruction lane for face geometry, pose, and depth.";

	public string AvatarModelStatus { get; set; } = "waiting for stored reconstruction observations";

	public int RetainedAvatarObservationCount { get; set; }

	public long StorageRevision { get; set; }

	public long LifetimeAcceptedObservationCount { get; set; }

	public long LifetimeRejectedObservationCount { get; set; }

	public double AvatarModelConfidencePercent { get; set; }

	public double AvatarModelCoveragePercent { get; set; }

	public string AvatarModelCoverageSummary { get; set; } = "waiting";

	public double AvatarModelConvergencePercent { get; set; }

	public string AvatarModelConvergenceLabel { get; set; } = "waiting";

	public string AvatarIdentityMappingStatus { get; set; } = "waiting";

	public double AvatarIdentityMappingLandmarkRmsePercent { get; set; }

	public double AvatarIdentityMappingImprovementPercent { get; set; }

	public string AvatarModelHtmlPath { get; set; } = "";

	public string AvatarModelAuditStatus { get; set; } = "waiting for the first model baseline";

	public string AvatarModelAuditSummary { get; set; } = "";

	public string AvatarModelAuditHtmlPath { get; set; } = "";

	public string StoragePolicy { get; set; } = "SQLite indexes ranked observations while immutable binary scans and paired source photos hold bulk data. Capture writes are bounded and asynchronous; weaker duplicates are rejected or replaced without blocking preview.";
}
