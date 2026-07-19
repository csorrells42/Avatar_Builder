# Webcam.Dx12 Module

Owns GPU camera preview and texture-native rendering.

Responsibilities:
- Render NV12 and BGRA camera preview frames through DX12.
- Prefer validated NV12 staging uploads for D3D11 cameras; import a shared NT texture only when a valid NV12 payload is unavailable.
- Reuse pooled NV12 frame buffers and coalesce upload work to the newest frame instead of building a preview queue.
- Render face/eye/mouth regions plus face, jaw, brow, eye, and inner/outer-lip contours through one bounded instanced DX12 draw call.
- Composite tracking geometry after the camera draw on every native-texture, shared-bridge, NV12-upload, and BGRA fallback route.
- Own GPU denoise and color-polish shader paths for preview.
- Manage texture-native camera stream preview.
- Report the active GPU preview path, render FPS, dropped frames, format, processing state, recording mode, and fallback reason.
- Keep upload and analysis work off the camera capture thread. The last-resort D3D11 shared-texture presentation is synchronous because its `WaitForGpu()` protects the reusable bridge texture.
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
- `Direct3D12TrackingOverlayRenderer.cs`

Timing invariant:
- A validated D3D11 NV12 payload never creates or copies the shared bridge texture.
- If no valid payload exists, `TextureNativeCameraRecorder` may copy into the shared bridge texture.
- `Direct3D12PreviewHost` must finish that shared-texture presentation and call `WaitForGpu()` before capture may copy another frame into the bridge texture.
- Analysis receives `DuplicatePreviewData()`, which owns only the pooled CPU bytes.
- `HwndHost` owns native window airspace. Never place a WPF overlay above the DX12 child window; composite it into the swap-chain render pass.

Drop-in boundary:
- Use `ICameraPreviewPresenter` when another program needs a camera preview surface without knowing whether the backing renderer is DX12, WPF, or a fallback.
- Use `Direct3D12PreviewDiagnostics.FormatStatusLine()` for a compact status overlay or log line.
- `WebcamDirectX12ViewportHost.cs` is the copied, renamed, webcam-owned WPF child-window host. Take this folder with the webcam module; no extra viewport module is required.

Do not put generic camera enumeration or session playback here.
