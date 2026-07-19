# Avatar Builder

Standalone WPF application for login-gated webcam capture, dense 3D face reconstruction, and long-term avatar model building.

Avatar Builder was split from Episode Monitor after the shared camera and vision work had matured. The medical event watch, event database, evidence recorder, alert calibration, and symptom workflow are not part of this application. Shared face landmark measurements remain because they are useful for visible tracking, capture quality, and avatar inspection.

## Runtime lanes

Avatar Builder deliberately runs three asynchronous lanes:

1. The webcam lane displays the newest camera frame as quickly as the selected capture mode and renderer allow.
2. The MediaPipe/OpenCV lane consumes the newest available frame for fast face lock, eye, brow, nose, lip, mouth, and jaw measurements. It may skip stale frames when analysis is busy.
3. The 3DDFA_V2 ONNX lane consumes frames only during an explicit logged-in capture session for dense vertices, A/B/C head pose, shape coefficients, expression coefficients, and persistent avatar observations.

The analysis lanes never own the camera frame rate. Slow inference drops stale analysis work instead of building a queue that freezes preview or the WPF UI.

## Avatar capture

The selected avatar user is stored in `AvatarSystem\avatar_profiles.json`. Each person receives a separate folder under `AvatarSystem\People\<profile-id>` so observations from different people cannot be blended accidentally.

Capture requires both controls:

- Use **File > Login** to select or create the profile for the person in front of the camera.
- Click **Start Avatar Capture**.

The app then applies camera, face-lock, quality, and storage gates before accepting 3DDFA observations. **Stop Avatar Capture** stops new observations but leaves camera preview and fast facial tracking available.

User-facing pose follows XYZABC:

- X: horizontal position.
- Y: vertical position.
- Z: camera-facing depth or apparent scale.
- A: rotation around X.
- B: rotation around Y.
- C: rotation around Z.

3DDFA_V2 is the authority for avatar pose and dense geometry. MediaPipe is the fast feature-tracking and measurement lane.

The **View** menu owns preview presentation and tracking workload. **DX12 Preview Viewport** and **Show Live Wireframe** are independent checkmark settings. Hover over **Tracking Fidelity** to open the mutually exclusive 4K, HD, and Safe Preview choices.

## Stored data

`AvatarBuilderOutputFolder.txt` beside the executable contains the selected data-folder path. **File > Choose Data Folder** opens the path and drive-capacity dialog. If the pointer file is missing, empty, or points to a missing folder, startup asks for a new location and saves it. The intended workstation location is `D:\Avatar Builder Output`.

All user-generated avatar data belongs under that selected folder. The app stores bounded, measurement-oriented data rather than continuous webcam video:

- `avatar_model_observations.json`: accepted 3DDFA observations.
- `avatar_model.json`: current pose-normalized avatar model.
- `avatar_model_history.jsonl`: compact improvement and regression history.
- `avatar_model_progress.html`: interactive current-model viewer.
- `avatar_model_regression.html`: model-change audit.
- `last_5_3ddfa_reconstructions.html`: full-resolution 3DDFA Last 5 review page.
- Avatar System JSON/HTML reports that link the current capture, quality, model, and review state.

No passive continuous video, room imagery, medical-event database, event clips, or alert-baseline files are produced by Avatar Builder.

## Model and review flow

Accepted 3DDFA samples are bounded and rebuilt into the current avatar model every 30 seconds while capture is active. The builder pose-normalizes dense vertices, uses shape and geometry evidence for base identity, tracks expression ranges separately, and records confidence, coverage, stability, regional RMS movement, and outlier candidates. Review flags do not silently delete observations.

**Open Avatar System** writes and opens the local dashboard. **Open 3DDFA Last 5** shows the latest dense reconstruction samples, while the live camera-relative MediaPipe wireframe reads directly from the current tracking frame without storing a second review cache. Report writing runs in the background from immutable snapshots.

## Module layout

Runtime code lives under `Modules`:

- `Modules\Webcam`: camera discovery, controls, capture, DX11 bridge, and DX12 viewport.
- `Modules\Vision\Common`: backend-neutral face and landmark contracts.
- `Modules\Vision\Analysis`: reusable landmark measurements, temporal repair, face geometry, and lock stability.
- `Modules\Vision\OpenCv`: YuNet, LBF, and aperture fallback implementations.
- `Modules\Vision\MediaPipe`: MediaPipe Face Landmarker sidecar and mapping.
- `Modules\Vision\Onnx`: 3DDFA_V2 ONNX sidecar client and runtime discovery.
- `Modules\Vision\Pipeline`: backend composition and fusion.
- `Modules\Vision\Personalization`: avatar profiles, user login session, and capture quality.
- `Modules\Vision\Reconstruction`: observation stores, model building, audit history, dashboards, and review pages.
- `Modules\Infrastructure`: small shared runtime helpers.
See `Modules\README.md` and each module README before changing a backend.

## Build and run

```powershell
dotnet restore .\AvatarBuilder.csproj
dotnet build .\AvatarBuilder.csproj --no-restore
.\desktop-runtime\AvatarBuilder.exe
```

Every successful build refreshes `desktop-runtime\AvatarBuilder.exe` and its build-owned dependencies. The desktop shortcut and `make-avatar.cmd` target that stable runtime, so Debug and Release builds cannot leave them pointing at different executables. `tools\InstallDesktopShortcut.ps1` creates or repairs the shortcut.

For guided capture from the repository:

```cmd
make-avatar.cmd
```

This requests easy avatar mode and the configured output folder. It does not log in a user, turn on the camera, or bypass quality gates.

## Sidecar setup

MediaPipe and 3DDFA run as local Python sidecars behind C# clients. Model/runtime assets remain inside the repository and are copied beside the executable.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\SetupThreeDdfaOnnxSidecar.ps1 -Python "C:\Path\To\python.exe"
```

The official 3DDFA_V2 repository and `mb1_120x120` checkpoint or converted ONNX weight belong under `dependencies\vision\3ddfa-onnx\3DDFA_V2` according to the bundled manifest.

## Safety and identity

Avatar Builder gathers geometric and expression evidence for a digital representation. Any downstream assistant or avatar must identify itself as a digital representation of a real person, never as the real person. Do not use this project for authentication, financial authority, legal identity, or autonomous impersonation.
