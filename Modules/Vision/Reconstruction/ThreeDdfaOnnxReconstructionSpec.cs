namespace AvatarBuilder.Modules.Vision.Reconstruction;

public static class ThreeDdfaOnnxReconstructionSpec
{
    public const string BackendId = FaceReconstructionBackendIds.ThreeDdfaV2OnnxReconstruction;
    public const string WorkItemFileName = "three_ddfa_onnx_reconstruction_job.json";
    public const string ResultFileName = "three_ddfa_onnx_reconstruction_result.json";
    public const string SourceRepository = "https://github.com/cleardusk/3DDFA_V2";
    public const string RecommendedRuntime = "ONNX Runtime adapter running out of process or in a dedicated Vision.Onnx adapter";

    public static FaceReconstructionWorkItem CreateWorkItem(
        FaceReconstructionSubjectGate subjectGate,
        string outputFolder,
        IEnumerable<FaceReconstructionSourceFrame>? sourceFrames = null)
    {
        return new FaceReconstructionWorkItem
        {
            BackendId = BackendId,
            SubjectGate = subjectGate,
            OutputFolder = outputFolder,
            SourceFrames = sourceFrames?.ToList() ?? [],
            RequestedOutputs =
            [
                "dense_3d_face_vertices",
                "head_pose_xyzabc",
                "shape_coefficients",
                "expression_coefficients",
                "landmark_correspondence",
                "quality_report"
            ],
            Notes = "3DDFA/ONNX should run as the avatar reconstruction lane. MediaPipe remains the fast live narcolepsy/overlay tracker; this lane should consume subject-gated frames or explicit avatar-training media and return dense reconstruction evidence for trust/audit."
        };
    }
}
