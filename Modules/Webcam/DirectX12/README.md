# Webcam.Dx12 Module

Owns GPU camera preview and texture-native rendering.

Responsibilities:
- Render NV12 and BGRA camera preview frames through DX12.
- Reuse pooled NV12 frame buffers and coalesce pending render work to the newest frame instead of building a preview queue.
- Draw face/eye/mouth regions plus face, jaw, brow, eye, and inner/outer-lip contours in the native DX12 target so the child-window viewport cannot cover the overlay.
- Own GPU denoise and color-polish shader paths for preview.
- Manage texture-native camera stream preview.
- Report the active GPU preview path, render FPS, dropped frames, format, processing state, recording mode, and fallback reason.
- Keep expensive preview work off the camera capture thread.
- Own the webcam-local WPF child-window viewport host so the webcam module can move without a separate shared DX12 folder.

Current entry points:
- `Direct3D12DeviceManager.cs`
- `Direct3D12PreviewHost.cs`
- `Direct3D12PreviewDiagnostics.cs`
- `ICameraPreviewPresenter.cs`
- `WebcamDirectX12ViewportHost.cs`
- `Dx12Camera.cs`
- `Dx12CameraOptions.cs`
- `TextureNativePreviewPolicy.cs`
- `TextureNativeCameraRecorder.cs`
- `PreviewTrackingOverlay.cs`

Drop-in boundary:
- Use `ICameraPreviewPresenter` when another program needs a camera preview surface without knowing whether the backing renderer is DX12, WPF, or a fallback.
- Use `Direct3D12PreviewDiagnostics.FormatStatusLine()` for a compact status overlay or log line.
- `WebcamDirectX12ViewportHost.cs` is the copied, renamed, webcam-owned WPF child-window host. Take this folder with the webcam module; no extra viewport module is required.

Do not put generic camera enumeration or session playback here.
