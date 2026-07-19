# Webcam Module

Namespace root: `AvatarBuilder.Modules.Webcam`

This module owns camera input only. It should answer: which camera, which mode, which frames, and which camera controls. It should not decide whether a frame is suitable for avatar capture.

`WebcamModule.cs` is the root facade copied from the Jericho Down module shape and adapted for Avatar Builder. It exposes camera discovery, preview-service factories, DirectShow controls, and DX12 host/camera creation so the app shell can stay out of backend construction details.

## Common

Namespace: `AvatarBuilder.Modules.Webcam.Common`

Shared camera models and contracts:

- `CameraDevice.cs`: selected camera identity, display name, Media Foundation/DirectShow fallback pairing, and source-device enumeration.
- `CameraDeviceCatalog.cs`: merges Media Foundation and DirectShow camera lists into one physical-camera picker row when they describe the same camera.
- `CameraSourceSelection.cs`: facade for merged camera discovery, default camera lookup, selected-source matching, and DirectShow fallback checks.
- `CameraFrame.cs`: in-memory camera frame container for BGRA and NV12 frame data.
- `CameraModeRecommendation.cs`: chooses the best camera mode for the selected tracking fidelity, including 4K/HD/safe preferences.
- `CameraVideoMode.cs`: camera resolution, frame rate, input format, and Auto mode model.
- `ICameraPreviewService.cs`: common preview service contract used by backend adapters and the preview pipeline; it emits WPF bitmaps for analysis/overlay work and raw `CameraFrame` payloads for GPU presenters.
- `VideoFrameColorSettings.cs`: shared color/denoise adjustment model consumed by the DX12 presenter.
- `VideoFrameDenoiser.cs`: small temporal BGRA denoiser used by processed recording fallbacks.

Change this folder when shared camera vocabulary or mode-selection policy changes.

## DirectShow

Namespace: `AvatarBuilder.Modules.Webcam.DirectShow`

DirectShow device enumeration and camera-control sliders. This is also the fallback identity used when a physical camera has both Media Foundation and DirectShow endpoints.

Files:

- `CameraControlItem.cs`: one driver-exposed camera control and its current/default/range/auto state.
- `CameraControlKind.cs`: supported DirectShow camera/video-processing control categories.
- `CameraControlText.cs`: UI-facing camera-control labels, value formatting, step rounding, and default-value magnet behavior.
- `DirectShowCameraControlService.cs`: reads and writes Windows DirectShow camera controls such as exposure, focus, zoom, brightness, contrast, sharpness, gain, and white balance.
- `DirectShowCameraEnumerator.cs`: enumerates DirectShow video input devices and captures friendly name/device path identity.

Change this folder when camera sliders, camera driver controls, or DirectShow fallback identity need work.

## MediaFoundation

Namespace: `AvatarBuilder.Modules.Webcam.MediaFoundation`

Windows Media Foundation camera enumeration, mode probing, source-reader setup, and bitmap preview frame extraction. This is the preferred live capture path for HD/4K modes.

Files:

- `MediaFoundationBitmapCameraPreviewService.cs`: opens a Media Foundation source reader, reads camera samples, converts NV12/RGB32 frames to WPF bitmaps, and throttles UI preview delivery.
- `MediaFoundationCameraDeviceFactory.cs`: activates the selected physical camera, creates source readers, configures selected modes, exposes D3D-backed texture source readers, and rejects silent low-resolution fallback for explicit modes.
- `MediaFoundationCameraEnumerator.cs`: enumerates Windows Media Foundation video devices.
- `MediaFoundationCameraModeService.cs`: probes native Media Foundation camera modes and adds known Insta360 fallback modes when the driver does not report them cleanly.
- `MediaFoundationGuids.cs`: Media Foundation GUID constants used by interop calls.
- `MediaFoundationInterop.cs`: COM interfaces, P/Invoke declarations, and helpers for Media Foundation source readers, sink writers, D3D device managers, and media types.
- `MediaFoundationVideoRecorder.cs`: Media Foundation sink-writer recorder for processed BGRA fallback output.

Change this folder when HD/4K capture, source-reader setup, Media Foundation mode probing, or Windows camera interop needs work.

## Ffmpeg

Namespace: `AvatarBuilder.Modules.Webcam.Ffmpeg`

Bundled FFmpeg DirectShow option probing and image-pipe preview fallback.

Files:

- `FfmpegCameraModeService.cs`: probes DirectShow camera modes with bundled FFmpeg and combines them with Media Foundation modes for the picker.
- `FfmpegCameraPreviewService.cs`: starts bundled FFmpeg as a fallback camera preview path, requests selected size/fps/format, reads MJPEG frames from stdout, and reports simplified camera errors.

Change this folder when the FFmpeg fallback fails, DirectShow option parsing is wrong, or FFmpeg arguments need tuning.

## Pipeline

Namespace: `AvatarBuilder.Modules.Webcam.Pipeline`

Composition layer. `CameraPreviewService` tries Media Foundation first, then FFmpeg fallback. UI code should depend on this layer rather than backend classes when it just wants preview frames.

Files:

- `CameraPreviewService.cs`: high-level preview facade that applies tracking-fidelity limits, tries Media Foundation first, and falls back to FFmpeg using the DirectShow paired camera.

Change this folder when backend ordering, fallback behavior, or shared preview settings need work.

## DirectX11

Namespace: `AvatarBuilder.Modules.Webcam.DirectX11`

Direct3D 11 device setup used by the texture-native Media Foundation camera source reader.

Files:

- `Direct3D11DeviceManager.cs`: creates the D3D11 device/context and Media Foundation DXGI device manager used by texture-native source readers.
- `Direct3D11SharedTextureBridge.cs`: copies each D3D11 NV12 camera texture into the reusable NT-handle texture that the DX12 presenter opens.

Change this folder when the D3D11 device-manager setup needs work. Keep high-level camera selection in `Pipeline` or `DirectX12`.

## DirectX12

Namespace: `AvatarBuilder.Modules.Webcam.DirectX12`

Jericho Down-derived Direct3D 12 preview host, native texture camera wrapper, and presenter code. This module owns the native child-window viewport, the BGRA/NV12 upload renderer, the texture-native camera stream, and recording support around that stream. `MainWindow` starts `Dx12Camera`, which presents the newest available camera frame without waiting for face analysis.

Files:

- `WebcamDirectX12ViewportHost.cs`: WPF `HwndHost` wrapper that owns the native child window used by the DX12 swap chain.
- `Direct3D12PreviewHost.cs`: Direct3D 12 renderer that imports the D3D11 shared NV12 texture, uploads fallback BGRA/NV12 frames, coalesces upload work to the newest frame, draws native tracking overlays, manages the swap chain, and reports render diagnostics.
- `Direct3D12PreviewDiagnostics.cs`: compact render-path, FPS, frame-count, and fallback status model for overlays/logging.
- `ICameraPreviewPresenter.cs`: narrow UI-facing presenter contract for a camera preview surface.
- `Direct3D12DeviceManager.cs`: D3D12-backed Media Foundation device-manager implementation for native texture capture.
- `Dx12Camera.cs`: high-level texture-native camera wrapper that owns the preview host, native stream, fallback preview, status, diagnostics, and recording controls.
- `Dx12CameraOptions.cs`: startup options and event hooks for `Dx12Camera`.
- `TextureNativePreviewPolicy.cs`: remembers short-lived native DX12 camera open failures per camera/mode so fallback can proceed without retry storms.
- `TextureNativeCameraRecorder.cs`: texture-native stream, pooled ref-counted frame leases, NV12 conversion, raw/processed recording sessions, and texture sink writer.
- `PreviewTrackingOverlay.cs`: immutable normalized face/eye/mouth regions passed from analysis into the native renderer.

Change this folder when GPU preview rendering, swap-chain management, Direct3D shader upload, or texture-native preview/recording needs work. Keep generic camera enumeration in `Common`, Media Foundation source-reader setup in `MediaFoundation`, and high-level backend choice in `Pipeline` or the UI integration layer.

The D3D11 bridge is timing-critical. Its shared texture is reusable, so bridge presentation remains synchronous through `WaitForGpu()` before the capture loop may copy the next frame. CPU analysis uses a data-only frame duplicate and must never hold the GPU resource or shared handle. Do not remove the bridge because an NV12 upload fallback exists; the upload path is recovery, not the primary D3D11-to-DX12 camera path.
