# Avatar Builder Modules

Runtime capabilities live in purpose-named modules. Folder path and namespace should agree so a developer can navigate by either the file tree or symbol search.

## Reusable module rule

The WPF shell composes modules and owns controls, drawing, and workflow. Camera capture, facial measurement, user-session gating, reconstruction, storage, and inspection belong behind narrow module contracts. Backend implementations must not depend on WPF controls.

Prefer immutable DTOs and zero-waiting asynchronous workers. Every expensive real-time lane may own one accepted job. A faster producer must ignore new arrivals while that job is active, before copying or converting them, then accept the freshest available input after the slot becomes free.

## App shell

`MainWindow.xaml` defines the visible single-camera workflow. `MainWindow.xaml.cs` coordinates serialized camera lifetime, avatar-user login, MediaPipe tracking, measured-geometry capture, and UI status. `AvatarDataFolderDialog.xaml` owns the File-menu storage dialog. Vision algorithms do not belong in the app shell.

## Webcam

Folder: `Modules/Webcam`

Owns camera discovery, controls, mode selection, frame delivery, and preview presentation.

- `Common`: camera DTOs, source selection, frame types, and preview contracts.
- `DirectShow`: camera enumeration and control access.
- `Ffmpeg`: bundled FFmpeg mode probing and preview fallback.
- `MediaFoundation`: Windows capture, mode probing, and texture-native recording primitives.
- `Pipeline`: chooses the available capture implementation.
- `DirectX11`: D3D11 device management and shared-texture bridge.
- `DirectX12`: DX12 viewport, presenter, camera wrapper, diagnostics, and single-slot render handoffs.
- `DualCamera`: two independent camera and MediaPipe lanes, physical stereo calibration, coordinate translation, stereo face reconstruction, and browser viewers. Each lane presents every camera frame it can while analysis accepts work only when its one slot is empty.

## Vision

Folder: `Modules/Vision`

Owns MediaPipe face localization, landmarks, feature measurements, capture quality, measured reconstruction, persistence, and inspection.

- `Common`: neutral landmark contracts and frame/result models.
- `Analysis`: contour measurements, temporal repair, camera-space geometry, and lock stability.
- `OpenCv`: optional localization and aperture fallbacks behind neutral contracts.
- `MediaPipe`: Face Landmarker model discovery, shared-memory Python sidecar, mapping, and measured reconstruction.
- `Pipeline`: tracker composition and result routing.
- `Personalization`: profile registry, explicit user login session, and capture-quality scoring.
- `Reconstruction`: shared topology contracts plus the explicit MediaPipe-constrained 3DDFA dense-warp proof path.
- `Diagnostics`: optional stage-timing contracts.

## Infrastructure

Folder: `Modules/Infrastructure`

Contains small host-level helpers such as atomic text writes and bundled FFmpeg lookup. Domain behavior belongs in its owning module.

## Dependency direction

```text
UI -> Webcam
UI -> Vision
Vision.Pipeline -> Vision.MediaPipe / Vision.OpenCv / Vision.Common
Vision.Analysis -> Vision.Common
Vision.MediaPipe.Reconstruction -> Vision.MediaPipe / Vision.Reconstruction
Webcam.DualCamera -> Vision.MediaPipe / Vision.MediaPipe.Reconstruction.Stereo
Webcam.Pipeline -> Webcam.MediaFoundation / Webcam.Ffmpeg / Webcam.Common
Webcam.Ffmpeg -> Infrastructure
```

`Vision` must not depend on concrete webcam backends. Pass only the smallest immutable frame abstraction needed at the composition boundary.
