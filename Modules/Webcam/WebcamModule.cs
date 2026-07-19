using System.Windows.Controls;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectShow;
using AvatarBuilder.Modules.Webcam.DirectX12;
using AvatarBuilder.Modules.Webcam.Ffmpeg;
using AvatarBuilder.Modules.Webcam.MediaFoundation;
using AvatarBuilder.Modules.Webcam.Pipeline;

namespace AvatarBuilder.Modules.Webcam;

public static class WebcamModule
{
    public static IReadOnlyList<CameraDevice> GetCameras()
    {
        return CameraSourceSelection.GetCameras();
    }

    public static CameraDevice? GetDefaultCamera()
    {
        return CameraSourceSelection.GetDefaultCamera();
    }

    public static CameraDevice RequireDefaultCamera()
    {
        return CameraSourceSelection.RequireDefaultCamera();
    }

    public static CameraDevice? FindCamera(
        IReadOnlyList<CameraDevice> cameras,
        string? devicePath,
        string? source,
        string? name)
    {
        return CameraSourceSelection.FindCamera(cameras, devicePath, source, name);
    }

    public static FfmpegCameraModeService CreateFfmpegModeService()
    {
        return new FfmpegCameraModeService();
    }

    public static MediaFoundationCameraModeService CreateMediaFoundationModeService()
    {
        return new MediaFoundationCameraModeService();
    }

    public static CameraPreviewService CreatePreviewService()
    {
        return new CameraPreviewService();
    }

    public static FfmpegCameraPreviewService CreateFfmpegPreviewService()
    {
        return new FfmpegCameraPreviewService();
    }

    public static MediaFoundationBitmapCameraPreviewService CreateMediaFoundationPreviewService()
    {
        return new MediaFoundationBitmapCameraPreviewService();
    }

    public static DirectShowCameraControlService CreateDirectShowControlService()
    {
        return new DirectShowCameraControlService();
    }

    public static Direct3D12PreviewHost CreateDirect3D12PreviewHost(IntPtr nativeD3D12Device = default)
    {
        return new Direct3D12PreviewHost(nativeD3D12Device);
    }

    public static Dx12Camera StartDx12Camera(Panel previewPanel, Dx12CameraOptions? options = null)
    {
        return Dx12Camera.Start(previewPanel, options);
    }

    public static Dx12Camera StartDx12Camera(Dx12Camera.PreviewTarget target, Dx12CameraOptions? options = null)
    {
        return Dx12Camera.Start(target, options);
    }

    public static Dx12Camera StartDx12Camera(
        CameraDevice camera,
        CameraVideoMode? mode,
        Panel previewPanel,
        Dx12CameraOptions? options = null)
    {
        return Dx12Camera.Start(camera, mode, previewPanel, options);
    }
}
