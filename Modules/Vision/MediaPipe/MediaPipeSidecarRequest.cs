using System.Text.Json.Serialization;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

internal sealed class MediaPipeSidecarRequest
{
	[JsonPropertyName("requestId")]
	public string RequestId { get; init; } = "";

	[JsonPropertyName("sharedMemoryName")]
	public string SharedMemoryName { get; init; } = "";

	[JsonPropertyName("sharedMemoryCapacityBytes")]
	public long SharedMemoryCapacityBytes { get; init; }

	[JsonPropertyName("imageByteLength")]
	public int ImageByteLength { get; init; }

	[JsonPropertyName("imageWidth")]
	public int ImageWidth { get; init; }

	[JsonPropertyName("imageHeight")]
	public int ImageHeight { get; init; }

	[JsonPropertyName("imageStride")]
	public int ImageStride { get; init; }

	[JsonPropertyName("imagePixelFormat")]
	public string ImagePixelFormat { get; init; } = "BGRA32";

	[JsonPropertyName("capturedAtUtc")]
	public string CapturedAtUtc { get; init; } = "";

	[JsonPropertyName("timestampMilliseconds")]
	public long TimestampMilliseconds { get; init; }
}
