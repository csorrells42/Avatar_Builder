namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class FaceReconstructionLaneStatus
{
    public string SchemaVersion { get; init; } = "face-reconstruction-lane-status-v1";

    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    public string FastTrackingLaneName { get; init; } = "Selected face-box tracking lane";

    public string FastTrackingPurpose { get; init; } =
        "Live face, eye, mouth, brow, overlay, and capture-measurement tracking.";

    public bool FastTrackingAvailable { get; init; }

    public bool FastTrackingHasDenseFace { get; init; }

    public string FastTrackingStatus { get; init; } = "waiting";

    public string AvatarReconstructionLaneName { get; init; } = "3DDFA_V2 ONNX avatar reconstruction lane";

    public string AvatarReconstructionPurpose { get; init; } =
        "Whole-face/head pose, dense reconstruction, depth, coefficients, and avatar trust checks.";

    public string AvatarReconstructionBackendId { get; init; } = FaceReconstructionBackendIds.ThreeDdfaV2OnnxReconstruction;

    public bool AvatarReconstructionManifestPresent { get; init; }

    public bool AvatarReconstructionModelPresent { get; init; }

    public bool AvatarReconstructionCanRunInference { get; init; }

    public string AvatarReconstructionStatus { get; init; } = "3DDFA_V2 ONNX waiting";

    public string AvatarReconstructionRuntime { get; init; } = "";

    public string AvatarReconstructionModelDirectory { get; init; } = "";

    public string AvatarReconstructionManifestPath { get; init; } = "";

    public IReadOnlyList<string> AvatarReconstructionModelFiles { get; init; } = [];

    public IReadOnlyList<string> AvatarReconstructionExpectedOutputs { get; init; } = [];

    public string TrustLevel { get; init; } = "measurement-only";

    public string TrustDecision { get; init; } =
        "Avatar capture waits for validated 3DDFA_V2 ONNX dense reconstruction while the selected live face-box tracker remains independent.";

    public string LearningImpact { get; init; } =
        "Does not block live feature tracking. Avatar fitting should trust dense depth/head-shape output only after the 3DDFA_V2 ONNX lane is ready.";

    public IReadOnlyList<string> Warnings { get; init; } = [];

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
