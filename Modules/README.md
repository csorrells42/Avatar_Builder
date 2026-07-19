# Avatar Builder Modules

Runtime capabilities live in purpose-named modules. Folder path and namespace should agree so a developer can navigate by either the file tree or symbol search.

## Reusable module rule

The WPF shell composes modules and owns controls, drawing, and workflow. Camera capture, facial measurement, user-session gating, reconstruction, storage, and report generation belong behind narrow module contracts. Backend implementations must not depend on WPF controls.

Prefer backend-neutral DTOs and latest-frame asynchronous workers. Reusable modules must be independently callable by a future AI application without copying `MainWindow.xaml.cs`.

## App shell

`MainWindow.xaml` defines the visible workflow. `MainWindow.xaml.cs` coordinates camera state, fast tracking, avatar-user login, 3DDFA capture, immutable report snapshots, and UI status. `AvatarDataFolderDialog.xaml` owns the File-menu storage dialog and pending folder selection. New backend algorithms do not belong in the app shell.

## Webcam

Folder: `Modules/Webcam`

Owns camera discovery, controls, mode selection, frame delivery, recording primitives used by avatar tooling, and preview presentation.

- `Common`: camera DTOs, source selection, frame types, color settings, and preview contracts.
- `DirectShow`: camera enumeration and control access.
- `Ffmpeg`: bundled FFmpeg mode probing and preview fallback.
- `MediaFoundation`: Windows capture, mode probing, and texture-native recording primitives.
- `Pipeline`: selects the available camera implementation.
- `DirectX11`: D3D11 device manager and shared-texture bridge.
- `DirectX12`: DX12 viewport, presenter, camera wrapper, diagnostics, and latest-frame pumps.

## Vision

Folder: `Modules/Vision`

Owns face localization, landmarks, feature measurements, capture quality, reconstruction, persistence, and inspection reports.

- `Common`: landmark contracts and frame/result models shared by every backend.
- `Analysis`: contour measurements, temporal repair, camera-space face geometry, and face-lock stability.
- `OpenCv`: YuNet, LBF, and aperture fallback implementations.
- `MediaPipe`: Face Landmarker model discovery, Python sidecar, and dense-result mapping.
- `Onnx`: 3DDFA_V2 model discovery, Python sidecar client, and dense reconstruction protocol.
- `Pipeline`: tracker ordering and fusion rules.
- `Personalization`: profile registry, explicit user login session, and avatar capture-quality scoring.
- `Reconstruction`: persistent 3DDFA observations, model builder, regression history, dashboard, and dense reconstruction review page.

## Infrastructure

Folder: `Modules/Infrastructure`

Contains small host-level helpers such as atomic text writes and bundled FFmpeg lookup. Domain behavior belongs in its owning module.

## Dependency direction

```text
UI -> Webcam
UI -> Vision
Vision.Pipeline -> Vision.MediaPipe / Vision.OpenCv / Vision.Common
Vision.Analysis -> Vision.Common
Vision.Onnx -> external 3DDFA_V2 Python sidecar
Vision.Reconstruction -> Vision.Personalization / Vision.Common
Webcam.Pipeline -> Webcam.MediaFoundation / Webcam.Ffmpeg / Webcam.Common
Webcam.Ffmpeg -> Infrastructure
```

`Vision` should not depend on concrete webcam backends. Pass only the smallest frame abstraction needed at the composition boundary.
