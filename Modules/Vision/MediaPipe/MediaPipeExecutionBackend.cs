namespace AvatarBuilder.Modules.Vision.MediaPipe;

public enum MediaPipeExecutionBackend
{
	Cpu,
	Gpu
}

public static class MediaPipeExecutionBackendExtensions
{
	public static string ToProtocolValue(this MediaPipeExecutionBackend backend)
	{
		return backend == MediaPipeExecutionBackend.Gpu ? "gpu" : "cpu";
	}

	public static string ToDisplayName(this MediaPipeExecutionBackend backend)
	{
		return backend == MediaPipeExecutionBackend.Gpu ? "GPU" : "CPU";
	}
}
