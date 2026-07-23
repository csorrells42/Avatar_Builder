using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AvatarBuilder.Modules.Vision.Deca;

internal sealed class DecaSidecarRequest
{
	[JsonPropertyName("operation")]
	public string Operation { get; init; } = "reconstruct";

	[JsonPropertyName("requestId")]
	public string RequestId { get; init; } = "";

	[JsonPropertyName("capturedAtUtc")]
	public string CapturedAtUtc { get; init; } = "";

	[JsonPropertyName("imageBase64")]
	public string ImageBase64 { get; init; } = "";

	[JsonPropertyName("faceBox")]
	public DecaSidecarFaceBox? FaceBox { get; init; }

	[JsonPropertyName("includeTopology")]
	public bool IncludeTopology { get; init; }

	[JsonPropertyName("previousModelShapeCoefficients")]
	public IReadOnlyList<double> PreviousModelShapeCoefficients { get; init; } = Array.Empty<double>();

	[JsonPropertyName("identityAnchorShapeCoefficients")]
	public IReadOnlyList<double> IdentityAnchorShapeCoefficients { get; init; } = Array.Empty<double>();

	[JsonPropertyName("identityFrames")]
	public IReadOnlyList<DecaIdentityFitFrame> IdentityFrames { get; init; } = Array.Empty<DecaIdentityFitFrame>();

	[JsonPropertyName("identityFitProfile")]
	public string IdentityFitProfile { get; init; } = "flame-68";

	[JsonPropertyName("maximumIterations")]
	public int MaximumIterations { get; init; } = 48;

	[JsonPropertyName("previousModelSequenceNumber")]
	public long PreviousModelSequenceNumber { get; init; }

	[JsonPropertyName("pinnedStillMaximumPasses")]
	public int PinnedStillMaximumPasses { get; init; } = 1;

	[JsonPropertyName("pinnedStillStablePassesRequired")]
	public int PinnedStillStablePassesRequired { get; init; } = 1;

	[JsonPropertyName("pinnedStillCoefficientDeltaThreshold")]
	public double PinnedStillCoefficientDeltaThreshold { get; init; }
}
