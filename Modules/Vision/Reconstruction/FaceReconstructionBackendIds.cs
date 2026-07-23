using System;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public static class FaceReconstructionBackendIds
{
	public const string MediaPipeGeometryMeasurement = "mediapipe-geometry-measurement-v1";

	public const string ThreeDdfaV2OnnxReconstruction = "3ddfa-v2-onnx-reconstruction";

	public const string DecaFlameReconstruction = "deca-flame-recurrent-v4";

	public const string DecaFlameStandardModelCheckpoint = "deca-flame-standard-model-checkpoint-v1";

	public static bool IsDecaFlame(string backendId)
	{
		if (!string.Equals(backendId, "deca-flame-recurrent-v4", StringComparison.Ordinal))
		{
			return string.Equals(backendId, "deca-flame-standard-model-checkpoint-v1", StringComparison.Ordinal);
		}
		return true;
	}
}
