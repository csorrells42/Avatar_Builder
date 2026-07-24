# Webcam Dual Camera Module

Namespace: `AvatarBuilder.Modules.Webcam.DualCamera`

This module owns the side-by-side camera workspace used for synchronized multi-view face capture.

Responsibilities:

- Discover Media Foundation and DirectShow cameras without blocking the WPF dispatcher.
- Give each viewport its own camera selector, mode selector, and on/off lifecycle.
- Prefer a 4K MJPEG mode near 30 fps when a camera provides one.
- Capture physical cameras through the texture-native path when available and route DirectShow-only virtual cameras through a compatible capture adapter into the same DX12 presenter.
- Keep each camera preview at the source frame rate while vision accepts a frame only when its analysis slot is empty.
- Run one independent MediaPipe tracker and analysis worker per active camera.
- Pair only the newest timestamp-compatible observations for optional cross-camera registration; never queue registration work.
- Translate each camera's 3D MediaPipe scan into the other camera's normalized face space and visualize the residual alignment.
- Fuse overlapping landmarks by choosing the camera with the larger normalized projected surface area at that point, which favors the less-foreshortened view.
- Measure each lane's live source, preview, and MediaPipe rates rather than repeating the camera's advertised rate.
- Refresh a stalled DX12 presenter while capture is healthy, and reopen only the affected lane when its camera source stops producing frames.
- Reject attempts to open the same physical camera in both lanes.
- Stop camera capture, in-flight frame ownership, analysis work, and the MediaPipe sidecar together when a lane or the window closes.

## Ownership Contract

`DualCameraLane` owns exactly one active capture adapter, one `MediaPipeFaceLandmarkerSidecarTracker`, and one analysis worker. The adapter is either a texture-native `Dx12Camera` or a compatible DirectShow capture feeding `Dx12UploadCamera`; they never run together in one lane. The lane reserves its only analysis slot before duplicating frame data, finishes that accepted frame, and ignores new arrivals until the slot is free. There is no cross-camera frame sharing and no analysis queue.

The preview path is not throttled by analysis. MediaPipe runs as fast as its worker allows while arrivals during active work are ignored. The camera continues presenting source frames independently, so inference cannot create latency or catch-up work.

The health monitor is also lane-local. A fault in one camera never restarts the other camera or its MediaPipe sidecar. Both lanes prefer 4K/30 when the selected camera exposes it. Single-in-flight handoffs keep camera presentation and MediaPipe analysis self-throttling instead of reducing the requested camera mode.

## Coordinate Translation View

`Coordinate Translation` is the live registration diagnostic. With a matching saved physical calibration, it intersects the two camera rays to recover each shared landmark in rig-space inches. Without calibration, it falls back to a rigid 3D similarity transform in normalized face space. Each native DX12 preview shows its own mesh in cyan, the partner scan transformed into that viewport in amber, and the direct-view fused points in green. The displayed projected RMS is normalized by face width so alignment can be compared across camera distance.

Direct-view ownership is computed from the projected area of the MediaPipe triangles incident to each landmark. A surface patch that is broad in one camera and foreshortened in the other is owned by the broader view. Physical reconstruction also records each point's reprojection residual and directness evidence so hidden or poorly intersecting rays are not presented as measured anatomy.

## Build 3D Face

`Build 3D Face` consumes only physically calibrated pairs. It converts rig-space landmarks into a head-fixed coordinate system with the eye midpoint as origin, X along the eyes, Y from chin toward forehead, and Z toward the nose. This removes head translation and A/B/C rotation while preserving dimensions in inches.

The reconstruction worker is independent of both camera lanes and accepts one synchronized pair only when idle. It robustly accumulates the 478 directly measured anchors and a separate image-matched stereo layer, rejects high-residual and hidden-side observations, and keeps eyes and lips as expression evidence rather than allowing them to deform identity shape. The matcher reuses the lanes' existing analysis images; it does not add another 4K copy or run on either camera thread. The resumable model and self-contained viewer are stored at `<profile folder>\StereoFaceGeometry`.

## Entry Points

- `DualCameraWorkspaceWindow.xaml`: side-by-side WPF workspace and controls.
- `DualCameraWorkspaceWindow.xaml.cs`: UI composition and per-lane lifecycle coordination.
- `DualCameraLane.cs`: camera, MediaPipe, frame ownership, throttling, status, and shutdown.
- `../Pipeline/Dx12UploadCamera.cs`: DirectShow-compatible capture adapter that presents only the newest decoded frame through DX12.
- `DualCameraObservation.cs`: immutable per-lane landmark snapshot passed to registration.
- `DualCameraRegistrationCoordinator.cs`: latest-pair matching, rigid 3D translation, residual measurement, and direct-view fusion.
- `DualCameraRegistrationFrame.cs`: renderer-ready translated/fused points and diagnostics.
- `CameraDiscoveryService.cs`: merged camera discovery.

The main app opens this workspace from `View > Dual Camera Workspace...` after stopping its single-camera preview so a physical device has only one owner.
