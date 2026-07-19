using System.Text.Json.Serialization;
using AvatarBuilder.Modules.Vision.Diagnostics;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

internal sealed class MediaPipeSidecarRequest
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = "";

    [JsonPropertyName("imageBase64")]
    public string ImageBase64 { get; init; } = "";

    [JsonPropertyName("capturedAtUtc")]
    public string CapturedAtUtc { get; init; } = "";

    [JsonPropertyName("timestampMilliseconds")]
    public long TimestampMilliseconds { get; init; }
}

internal sealed class MediaPipeSidecarResponse
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; init; } = "";

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("hasFace")]
    public bool HasFace { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("landmarks")]
    public IReadOnlyList<MediaPipeSidecarLandmark> Landmarks { get; init; } = [];

    [JsonPropertyName("blendshapes")]
    public IReadOnlyList<MediaPipeSidecarBlendshape> Blendshapes { get; init; } = [];

    [JsonPropertyName("facialTransformationMatrix")]
    public IReadOnlyList<double> FacialTransformationMatrix { get; init; } = [];

    [JsonPropertyName("timingsMilliseconds")]
    public IReadOnlyDictionary<string, double> TimingsMilliseconds { get; init; }
        = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public VisionPipelineDiagnostics Diagnostics { get; set; } = VisionPipelineDiagnostics.None;
}

internal sealed class MediaPipeSidecarLandmark
{
    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("z")]
    public double Z { get; init; }
}

internal sealed class MediaPipeSidecarBlendshape
{
    [JsonPropertyName("categoryName")]
    public string CategoryName { get; init; } = "";

    [JsonPropertyName("score")]
    public double Score { get; init; }
}
