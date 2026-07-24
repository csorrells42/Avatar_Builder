namespace AvatarBuilder.Modules.Webcam.DirectX12;

public readonly record struct PreviewTrackedPerson(
	PreviewOverlayRect Bounds,
	bool IsRemembered);
