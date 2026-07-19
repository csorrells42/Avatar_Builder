# Avatar Builder Modules

Avatar Builder keeps runtime code in purpose-named modules. Folder path and namespace should match so the codebase is navigable from either the file tree or a symbol search.

## Reusable Module Rule

Build every new capability as the smallest reusable module that can stand on its own. The WPF app shell may compose modules, draw controls, and handle user workflow, but recognition logic, camera logic, event evidence, recording, personalization, identity gating, 3D/avatar reconstruction, and AI-assistant integration data should live behind narrow module contracts.

This is a project rule so Avatar Builder can later feed the main AI program without copying WPF-specific code. Prefer backend-neutral input/output models, analyzers that accept measurements instead of UI controls, and durable DTOs that can be consumed by another process. If a feature can naturally be separated as face identity, eyelid state, jaw/lip droop, subject personalization, event packaging, or avatar reconstruction, keep that capability self-contained and let `MainWindow.xaml.cs` orchestrate it.

## App Shell

`MainWindow.xaml.cs` composes modules and owns WPF controls, drawing, and user workflow. New camera or vision backends should not be implemented directly in the app shell; they should enter through a module interface.

## Webcam

Folder: `Modules/Webcam`

Owns camera discovery, camera controls, camera mode selection, preview frame delivery, and camera backend adapters.

The root `WebcamModule.cs` file is the entry point for common camera operations and DX12 viewport creation. Use it when wiring app-shell code to camera discovery, preview services, or the Jericho Down-derived DX12 camera host.

Key submodules:

- `Common`: shared camera models and contracts.
- `DirectShow`: Windows DirectShow enumeration and camera-control support.
- `Ffmpeg`: bundled FFmpeg camera probing and fallback preview.
- `MediaFoundation`: Windows Media Foundation capture and mode probing.
- `Pipeline`: composition that chooses Media Foundation first and FFmpeg fallback second.
- `DirectX11`: D3D11 device-manager and shared-texture bridge code used by the texture-native camera path.
- `DirectX12`: Jericho Down-derived DX12 viewport, presenter, texture-native camera wrapper, recorder/probe support, and latest-frame pumps.

See `Webcam/README.md`.

## Vision

Folder: `Modules/Vision`

Owns face detection, landmarks, eye/lip aperture measurement, measurement quality, reconstruction, and trend analysis.

Key submodules:

- `Common`: shared landmark contracts and frame/result models.
- `Analysis`: backend-neutral cue scoring, contour metrics, reconstruction, and trend logic.
- `OpenCv`: OpenCV/YuNet/LBF/aperture implementations.
- `MediaPipe`: MediaPipe Face Landmarker model metadata, Python sidecar bridge, and dense-landmark mapping.
- `Onnx`: ONNX-backed avatar reconstruction adapters. The current adapter is a 3DDFA_V2 Python sidecar that returns dense reconstruction, pose, and coefficient evidence without blocking live tracking.
- `Pipeline`: composite tracker that chooses and fuses vision backends.
- `Personalization`: avatar-user registry, subject-gate helpers, and avatar capture-quality scoring.
- `Reconstruction`: backend-neutral 3D/avatar reconstruction job/result contracts, two-lane tracking/reconstruction status reports, Avatar System reports, separate MediaPipe Last 5 and 3DDFA Last 5 review pages, bounded persistent Avatar Model observation/model stores, and the active 3DDFA_V2 ONNX dense reconstruction work-item spec.
- `Deprecated`: retired module experiments kept for reference outside the active app build, including the old rolling measurement-learning model, old measurement preview/package/plan path, old evaluator/smoke/verifier tooling, and old Deep3D/PyTorch sidecar contract.

See `Vision/README.md`.

## Episodes

Folder and namespace: `Modules/Episodes`, `AvatarBuilder.Modules.Episodes`

Owns clinician-review event data: episode rows, landmark timeline samples, and aggregate evidence.

Look here when changing:

- event grid row fields
- event summary evidence
- landmark timeline JSON/CSV contents

See `Episodes/README.md`.

## Recording

Folder and namespace: `Modules/Recording`, `AvatarBuilder.Modules.Recording`

Owns event video writing and recorder lifecycle. It accepts already-rendered frames and paths instead of reaching into webcam or vision internals.

Look here when changing:

- event clip encoder behavior
- pre-roll frame writing
- FFmpeg recording lifecycle

See `Recording/README.md`.

## Infrastructure

Folder and namespace: `Modules/Infrastructure`, `AvatarBuilder.Modules.Infrastructure`

Owns shared host-level utilities such as locating bundled FFmpeg. Keep this small; if a helper has domain meaning, move it to that domain module.

Look here when changing:

- dependency lookup paths
- shared host/runtime helpers

See `Infrastructure/README.md`.

## Dependency Direction

Preferred direction:

```text
UI -> Webcam
UI -> Vision
UI -> Episodes
UI -> Recording

Episodes -> Vision.Analysis
Vision.Pipeline -> Vision.OpenCv / Vision.MediaPipe / Vision.Common
Vision.Onnx -> external 3DDFA_V2 Python sidecar
Vision.Analysis -> Vision.Common
Vision.Reconstruction -> Vision.Personalization
Webcam.Pipeline -> Webcam.MediaFoundation / Webcam.Ffmpeg / Webcam.Common
Webcam.Ffmpeg -> Infrastructure
Recording -> Infrastructure
```

Avoid making backend implementations depend on WPF controls. Avoid making `Vision` depend on `Webcam` unless the dependency is a tiny frame abstraction.
