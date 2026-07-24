# Webcam.Dx12 Module

Owns GPU camera preview and texture-native rendering.

Responsibilities:
- Render NV12 and BGRA camera preview frames through DX12.
- Use native D3D12 NV12 resources directly. For D3D11 capture, import the shared NT texture when the bridge is available and create a pooled CPU NV12 fallback only after that bridge fails.
- Reuse pooled NV12 frame buffers. Accept one render handoff only while the presenter is idle, finish it, and ignore arrivals while that slot is occupied.
- Render face/eye/mouth regions plus face, jaw, brow, eye, and inner/outer-lip contours through one bounded instanced DX12 draw call.
- Composite tracking geometry after the camera draw on every native-texture, shared-bridge, NV12-upload, and BGRA fallback route.
- Own GPU denoise and color-polish shader paths for preview.
- Manage texture-native camera stream preview.
- Report the active GPU preview path, render FPS, dropped frames, format, processing state, recording mode, and fallback reason.
- Keep every render path and all analysis work off the camera capture and frame-handoff threads. Native D3D12, D3D11 shared-texture, NV12 upload, and BGRA upload all execute on the dedicated render worker.
- Own the webcam-local WPF child-window viewport host so the webcam module can move without a separate shared DX12 folder.

Current entry points:
- `Direct3D12DeviceManager.cs`
- `Direct3D12PreviewHost.cs`
- `Direct3D12PreviewDiagnostics.cs`
- `ICameraPreviewPresenter.cs`
- `WebcamDirectX12ViewportHost.cs`
- `Dx12Camera.cs`
- `Dx12CameraWindow.cs`
- `Dx12CameraOptions.cs`
- `TextureNativePreviewPolicy.cs`
- `TextureNativeCameraRecorder.cs`
- `PreviewTrackingOverlay.cs`
- `Direct2DTrackingOverlayRenderer.cs`
  - Wraps the DX12 swap-chain back buffers through D3D11On12.
  - Uses Direct2D per-primitive antialiasing for thin face-mesh and feature lines.
  - Records each distinct tracking result once, then replays one cached command list per camera frame.
  - Uses a multithreaded Direct2D factory because WPF owns viewport lifecycle while the camera worker owns drawing.
  - Invalidates only the retained command list after preview resume; focus changes do not rewrap live swap-chain targets.
  - Disables the optional overlay after any Direct2D failure so camera-only DX12 presentation remains available.

Timing invariant:
- Camera ingestion is a highest-priority source-reader loop. After `ReadSample`, it may only timestamp the sample and perform a non-blocking ownership handoff; it never converts, renders, analyzes, records, or calls UI code.
- Display, observers, analysis, and recording each own a dedicated thread and exactly one in-flight item. There is no waiting mailbox.
- A lane may reject an arrival only before taking ownership. Once accepted, that lane finishes the frame.
- Display receives first offer. Observer, analysis, and recording work can never delay that offer.
- Lane transitions retain a reference only. NV12-to-BGRA conversion, resize, bitmap construction, inference, and encoding happen after the destination lane owns the frame.
- GPU tracking and overlay state older than 250 ms is unknown, not current; the native renderer self-expires stale overlays.
- Native D3D12 frames do not create a pooled CPU preview copy.
- The preview queue publishes a GPU fence for each native frame. The observer/analysis lane waits for that preview read to finish before using the same texture on its compute queue; preview and camera ingestion never wait for analysis.
- GPU fences protect shared-texture reuse inside the render/observer lanes. Every fence wait is bounded; a non-responsive submission is abandoned and recovered without blocking camera ingestion.
- Analysis receives `Duplicate()`, an O(1) reference-counted texture handoff. CPU preview bytes exist only on a compatibility fallback.
- Source ingestion and successful on-screen presentation have independent heartbeats. The continuity guard treats either heartbeat stopping as a failed lane, never gives up while Camera On remains requested, and temporarily falls back when the preferred video-card path must be reopened.
- `HwndHost` owns native window airspace. Never place a WPF overlay above the DX12 child window; composite it onto the DX12 swap-chain back buffer through `Direct2DTrackingOverlayRenderer`.

Drop-in boundary:
- Use `ICameraPreviewPresenter` when another program needs a camera preview surface without knowing whether the backing renderer is DX12, WPF, or a fallback.
- Use `Direct3D12PreviewDiagnostics.FormatStatusLine()` for a compact status overlay or log line.
- `WebcamDirectX12ViewportHost.cs` is the copied, renamed, webcam-owned WPF child-window host. Take this folder with the webcam module; no extra viewport module is required.

Do not put generic camera enumeration or session playback here.

Acceptance test:
- `dotnet run --project tools\VideoPipelineSmoke\VideoPipelineSmoke.csproj -c Release -- <video>`
- `dotnet run --project tools\VisionSmoke\VisionSmoke.csproj -c Release -- --camera-soak-seconds 600`
- `dotnet run --project tools\VisionSmoke\VisionSmoke.csproj -c Release -- --camera-reopen-cycles 10`
- `dotnet run --project tools\VisionSmoke\VisionSmoke.csproj -c Release -- --dx12-preview-soak-seconds 300`
- The test warms the 4K renderer and GPU tracker before starting its clock, decodes through end-of-stream, renders every decoded frame at source timing, allows tracking skips only before work is accepted, and fails on any display drop.
- Report advertised source FPS, measured playback/display FPS, and measured analysis FPS separately. An isolated inference FPS number is never a live-pipeline FPS number.
