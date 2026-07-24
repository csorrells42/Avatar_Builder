using AvatarBuilder.Modules.Webcam.DirectX12;

namespace AvatarBuilder.Modules.Webcam;

public static class WebcamModule
{
	public static Direct3D12PreviewHost CreateDirect3D12PreviewHost(nint nativeD3D12Device = 0)
	{
		return new Direct3D12PreviewHost(nativeD3D12Device);
	}

	public static Dx12Camera StartDx12Camera(Dx12Camera.PreviewTarget target, Dx12CameraOptions? options = null)
	{
		return Dx12Camera.Start(target, options);
	}

	public static Dx12CameraWindow CreateDx12CameraWindow(
		Dx12CameraOptions? options = null,
		string title = "Avatar Builder Camera Monitor")
	{
		return new Dx12CameraWindow(options, title);
	}
}
