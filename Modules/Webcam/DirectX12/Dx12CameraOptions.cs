using System;
using AvatarBuilder.Modules.Webcam.Common;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed class Dx12CameraOptions
{
	public CameraDevice? Camera { get; init; }

	public CameraVideoMode? Mode { get; init; }

	public bool DenoiseEnabled { get; init; }

	public double DenoiseStrength { get; init; } = 2.0;

	public VideoFrameColorSettings ColorSettings { get; init; } = VideoFrameColorSettings.Off;

	public double MaxPreviewRenderFramesPerSecond { get; init; }

	public EventHandler<TextureNativeFrameInfo>? FrameAvailable { get; init; }

	public EventHandler<TextureNativeFrameLease>? TextureFrameAvailable { get; init; }

	public EventHandler<Direct3D12PreviewDiagnostics>? DiagnosticsChanged { get; init; }

	public EventHandler<string>? StatusChanged { get; init; }
}
