namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

internal readonly record struct MediaPipeDenseStereoDiagnostics(int CandidateCount, int FlowMatchedCount, int TriangulatedCount, int MaximumSampleCount, int CameraASourceCandidateCount, int CameraBSourceCandidateCount, int CameraASourceMatchedCount, int CameraBSourceMatchedCount)
{
	public static MediaPipeDenseStereoDiagnostics Empty { get; } = new MediaPipeDenseStereoDiagnostics(0, 0, 0, 0, 0, 0, 0, 0);
}
