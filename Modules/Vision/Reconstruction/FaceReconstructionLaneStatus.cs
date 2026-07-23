using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class FaceReconstructionLaneStatus
{
	public string SchemaVersion { get; init; } = "face-reconstruction-lane-status-v1";

	public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

	public string FastTrackingLaneName { get; init; } = "Selected face-box tracking lane";

	public string FastTrackingPurpose { get; init; } = "Live face, eye, mouth, brow, overlay, and capture-measurement tracking.";

	public bool FastTrackingAvailable { get; init; }

	public bool FastTrackingHasDenseFace { get; init; }

	public string FastTrackingStatus { get; init; } = "waiting";

	public string AvatarReconstructionLaneName { get; init; } = "avatar reconstruction lane";

	public string AvatarReconstructionPurpose { get; init; } = "Whole-face/head pose, dense reconstruction, depth, coefficients, and avatar trust checks.";

	public string AvatarReconstructionBackendId { get; init; } = "3ddfa-v2-onnx-reconstruction";

	public bool AvatarReconstructionManifestPresent { get; init; }

	public bool AvatarReconstructionModelPresent { get; init; }

	public bool AvatarReconstructionCanRunInference { get; init; }

	public string AvatarReconstructionStatus { get; init; } = "avatar reconstruction waiting";

	public string AvatarReconstructionRuntime { get; init; } = "";

	public string AvatarReconstructionModelDirectory { get; init; } = "";

	public string AvatarReconstructionManifestPath { get; init; } = "";

	public IReadOnlyList<string> AvatarReconstructionModelFiles { get; set; } = Array.Empty<string>();

	public IReadOnlyList<string> AvatarReconstructionExpectedOutputs { get; set; } = Array.Empty<string>();

	public string TrustLevel { get; set; } = "measurement-only";

	public string TrustDecision { get; set; } = "Avatar capture waits for validated dense reconstruction while the selected live face-box tracker remains independent.";

	public string LearningImpact { get; set; } = "Does not block live feature tracking. Avatar fitting should trust dense depth/head-shape output only after the selected reconstruction lane is ready.";

	public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();

	public static FaceReconstructionLaneStatus Waiting { get; } = new FaceReconstructionLaneStatus
	{
		FastTrackingStatus = "waiting for face tracker",
		AvatarReconstructionStatus = "avatar reconstruction waiting for model bundle",
		Warnings = new global::_003C_003Ez__ReadOnlySingleElementList<string>("Dense avatar reconstruction is not active yet; do not treat measurement-only previews as a reconstructed model.")
	};
}
