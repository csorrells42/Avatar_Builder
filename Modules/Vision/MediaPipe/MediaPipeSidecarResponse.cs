using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using AvatarBuilder.Modules.Vision.Diagnostics;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

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
	public IReadOnlyList<MediaPipeSidecarLandmark> Landmarks { get; init; } = Array.Empty<MediaPipeSidecarLandmark>();

	[JsonPropertyName("blendshapes")]
	public IReadOnlyList<MediaPipeSidecarBlendshape> Blendshapes { get; init; } = Array.Empty<MediaPipeSidecarBlendshape>();

	[JsonPropertyName("facialTransformationMatrix")]
	public IReadOnlyList<double> FacialTransformationMatrix { get; init; } = Array.Empty<double>();

	[JsonPropertyName("timingsMilliseconds")]
	public IReadOnlyDictionary<string, double> TimingsMilliseconds { get; init; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

	[JsonIgnore]
	public VisionPipelineDiagnostics Diagnostics { get; set; } = VisionPipelineDiagnostics.None;
}
