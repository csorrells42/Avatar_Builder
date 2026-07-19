using AvatarBuilder.Modules.Webcam.Common;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public interface ICameraPreviewPresenter : IDisposable
{
    event EventHandler<string>? StatusChanged;

    event EventHandler<Direct3D12PreviewDiagnostics>? DiagnosticsChanged;

    bool IsReady { get; }

    string DeviceDescription { get; }

    string PreviewPathDescription { get; }

    string RecordingMode { get; }

    Direct3D12PreviewDiagnostics Diagnostics { get; }

    void SetRecordingMode(string recordingMode);

    void RenderBgraFrame(
        CameraFrame frame,
        long frameNumber,
        VideoFrameColorSettings colorSettings = default,
        bool denoiseEnabled = false,
        double denoiseStrength = 0d);

    void RenderTextureFrame(
        TextureNativeFrameLease frame,
        bool denoiseEnabled,
        double denoiseStrength,
        VideoFrameColorSettings colorSettings = default);

    void RenderProofFrame(TextureNativeFrameInfo frame);
}
