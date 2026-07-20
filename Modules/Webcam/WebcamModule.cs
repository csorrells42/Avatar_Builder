using AvatarBuilder.Modules.Webcam.DirectX12;

namespace AvatarBuilder.Modules.Webcam;

public static class WebcamModule
{
    public static Direct3D12PreviewHost CreateDirect3D12PreviewHost(IntPtr nativeD3D12Device = default)
    {
        return new Direct3D12PreviewHost(nativeD3D12Device);
    }

    public static Dx12Camera StartDx12Camera(Dx12Camera.PreviewTarget target, Dx12CameraOptions? options = null)
    {
        return Dx12Camera.Start(target, options);
    }
}
