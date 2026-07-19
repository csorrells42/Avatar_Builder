namespace AvatarBuilder.Modules.Vision.Reconstruction;

public static class Deep3DFaceReconstructionSidecarSpec
{
    public const string BackendId = "deep3d-face-recon-pytorch-sidecar";
    public const string WorkItemFileName = "face_reconstruction_job.json";
    public const string ResultFileName = "face_reconstruction_result.json";
    public const string RecommendedRuntime = "WSL2 Ubuntu or Linux worker with CUDA-capable PyTorch environment";
    public const string OriginalTensorFlowRepository = "https://github.com/microsoft/Deep3DFaceReconstruction";
    public const string PytorchRepository = "https://github.com/sicxu/Deep3DFaceRecon_pytorch";

    public static FaceReconstructionWorkItem CreateWorkItem(
        FaceReconstructionSubjectGate subjectGate,
        string personalFaceModelPath,
        string measurementJournalFolder,
        string outputFolder,
        IEnumerable<FaceReconstructionSourceFrame>? sourceFrames = null)
    {
        return new FaceReconstructionWorkItem
        {
            BackendId = BackendId,
            SubjectGate = subjectGate,
            PersonalFaceModelPath = personalFaceModelPath,
            MeasurementJournalFolder = measurementJournalFolder,
            OutputFolder = outputFolder,
            SourceFrames = sourceFrames?.ToList() ?? [],
            Notes = "Deep3D-style reconstruction should run out of process. Avatar Builder owns subject gating and evidence export; the sidecar owns Linux/PyTorch reconstruction."
        };
    }
}
