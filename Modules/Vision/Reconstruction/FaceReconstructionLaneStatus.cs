namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class FaceReconstructionLaneStatus
{
    public string SchemaVersion { get; set; } = "face-reconstruction-lane-status-v1";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public string FastTrackingLaneName { get; set; } = "Selected face-box tracking lane";

    public string FastTrackingPurpose { get; set; } =
        "Live face, eye, mouth, brow, overlay, and capture-measurement tracking.";

    public bool FastTrackingAvailable { get; set; }

    public bool FastTrackingHasDenseFace { get; set; }

    public string FastTrackingStatus { get; set; } = "waiting";

    public string AvatarReconstructionLaneName { get; set; } = "3DDFA_V2 ONNX avatar reconstruction lane";

    public string AvatarReconstructionPurpose { get; set; } =
        "Whole-face/head pose, dense reconstruction, depth, coefficients, and avatar trust checks.";

    public string AvatarReconstructionBackendId { get; set; } = FaceReconstructionBackendIds.ThreeDdfaV2OnnxReconstruction;

    public bool AvatarReconstructionManifestPresent { get; set; }

    public bool AvatarReconstructionModelPresent { get; set; }

    public bool AvatarReconstructionCanRunInference { get; set; }

    public string AvatarReconstructionStatus { get; set; } = "3DDFA_V2 ONNX waiting";

    public string AvatarReconstructionRuntime { get; set; } = "";

    public string AvatarReconstructionModelDirectory { get; set; } = "";

    public string AvatarReconstructionManifestPath { get; set; } = "";

    public IReadOnlyList<string> AvatarReconstructionModelFiles { get; set; } = [];

    public IReadOnlyList<string> AvatarReconstructionExpectedOutputs { get; set; } = [];

    public string TrustLevel { get; set; } = "measurement-only";

    public string TrustDecision { get; set; } =
        "Avatar capture waits for validated 3DDFA_V2 ONNX dense reconstruction while the selected live face-box tracker remains independent.";

    public string LearningImpact { get; set; } =
        "Does not block live feature tracking. Avatar fitting should trust dense depth/head-shape output only after the 3DDFA_V2 ONNX lane is ready.";

    public List<string> Warnings { get; set; } = [];

    public static FaceReconstructionLaneStatus Waiting { get; } = new()
    {
        FastTrackingStatus = "waiting for face tracker",
        AvatarReconstructionStatus = "3DDFA_V2 ONNX waiting for model bundle",
        Warnings =
        [
            "3DDFA_V2 ONNX reconstruction is not active yet; do not treat legacy measurement-only previews as dense reconstruction."
        ]
    };
}
