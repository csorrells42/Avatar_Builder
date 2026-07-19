# Avatar Builder

Standalone WPF application for login-gated webcam capture, dense 3D face reconstruction, and long-term avatar model building.

Avatar Builder was split from Episode Monitor after the shared camera and vision work had matured. The medical event watch, event database, evidence recorder, alert calibration, and symptom workflow are not part of this application. Shared face landmark measurements remain because they are useful for visible tracking, capture quality, and avatar inspection.

## Runtime lanes

Avatar Builder deliberately runs independent camera, tracking, and reconstruction lanes:

1. The webcam lane displays the newest camera frame as quickly as the selected capture mode and renderer allow.
2. The selected **File > Face Box System** consumes the newest available analysis frame. MediaPipe is the default and runs its temporal video model; a strong dense result returns immediately without spending the same frame on fallback models. 3DDFA-V2 uses its own FaceBoxes detector plus 68 sparse landmarks for live face, eye, brow, lip, mouth, jaw, and pose measurements.
3. The 3DDFA_V2 ONNX lane has explicit face-box-only, sparse tracking, sampled preview, and full dense modes. 3DDFA owns persistent avatar pose and depth. A logged-in, active capture session with a camera/face/quality lock requests a full 38,365-vertex reconstruction at most once every 10 seconds.

The analysis lanes never own the camera frame rate. Slow inference replaces stale analysis work with the newest frame instead of building a queue that freezes preview or the WPF UI. Switching face-box systems invalidates queued results and disposes the inactive tracker.

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

3DDFA_V2 is the authority for persistent avatar pose and dense geometry. MediaPipe is the default live feature tracker; 3DDFA-V2 can be selected as the complete live face-box and sparse-feature tracker for measured performance comparison.

The **File** menu owns login, storage, and the hover-open **Face Box System** selector. The **View** menu owns preview presentation and tracking workload. **DX12 Preview Viewport** and **Show Live Wireframe** are independent checkmark settings; the live wireframe is MediaPipe-specific and is disabled while 3DDFA-V2 owns tracking. Hover over **Tracking Fidelity** to open the mutually exclusive 4K, HD, and Safe Preview choices.

## Stored data

`AvatarBuilderOutputFolder.txt` beside the executable contains the selected data-folder path. **File > Choose Data Folder** opens the path and drive-capacity dialog. If the pointer file is missing, empty, or points to a missing folder, startup asks for a new location and saves it. The intended workstation location is `D:\Avatar Builder Output`.

All user-generated avatar data belongs under that selected folder. The app stores bounded, measurement-oriented data rather than continuous webcam video:

- `avatar_model_observations.json.gz`: bounded accepted 3DDFA observations in the current compact format. Every accepted sample keeps its expression-free canonical 3DDFA identity mesh for learning; only the five newest samples retain the larger observed camera-space scan needed by Last 5 review. A legacy uncompressed JSON file is migrated after a successful compressed write.
- `avatar_model.json`: current canonical 3DDFA identity model.
- `avatar_model_history.jsonl`: compact improvement and regression history.
- `avatar_model_progress.html`: interactive current-model viewer.
- `avatar_model_regression.html`: model-change audit.
- `last_5_3ddfa_reconstructions.html`: full-resolution 3DDFA Last 5 review page.
- Avatar System JSON/HTML reports that link the current capture, quality, model, and review state.

No passive continuous video, room imagery, medical-event database, event clips, or alert-baseline files are produced by Avatar Builder.

## Model and review flow

Full 3DDFA samples are requested at most once every 10 seconds while capture is active. Accepted samples are bounded, and the current avatar model is considered for rebuild every 30 seconds. The large model and Last 5 files are rebuilt only when the observation set actually changes. New samples use 3DDFA's own expression-free canonical BFM geometry for the persistent identity; those shared model coordinates are centered, scaled, and averaged directly without redundant pose alignment. The five newest observed camera-space meshes remain available for Last 5 review. Only legacy camera-space observations are aligned with rigid Procrustes rotation and translation, never non-rigidly warped. Observation and model stores are cached in-process, report file writes are serialized, and the compressed archive favors capture-time throughput over maximum compression. The model viewer converts canonical Y-up geometry to browser-canvas coordinates explicitly. Last 5 is rebuilt from the persisted observation database across restarts and stores shared topology once. Expression ranges remain separate, and model history records confidence, coverage, scale-independent shape stability, regional RMS movement, and outlier candidates. Review flags do not silently delete observations.

`tools\AuditAvatarModelData.py <profile-folder>` independently checks observation provenance, identity and review vertex counts, exact direct-canonical reproducibility, legacy alignment error, coefficient variation, and model-history movement. Use it whenever the accumulated face looks distorted or unexpectedly sparse.

**View > A/B/C Alignment Audit** opens a live five-second diagnostic report built from exact-frame MediaPipe and 3DDFA pose pairs. It shows raw and calibrated MediaPipe A/B/C, 3DDFA A/B/C, the measured scale/offset transform, correlation, motion range, mean error, p95 error, and a per-axis readiness decision. This comparison never blocks 3DDFA capture or learning; it exists to expose disagreement between the two systems before their outputs are combined downstream.

**Open Avatar System** writes and opens the local dashboard. **Open 3DDFA Last 5** shows the latest dense reconstruction samples, while the live camera-relative MediaPipe wireframe reads directly from the current tracking frame without storing a second review cache. Report writing runs in the background from immutable snapshots.

## Module layout

Runtime code lives under `Modules`:

- `Modules\Webcam`: camera discovery, controls, capture, DX11 device management, and the DX12 viewport.
- `Modules\Vision\Common`: backend-neutral face and landmark contracts.
- `Modules\Vision\Analysis`: reusable landmark measurements, temporal repair, face geometry, and lock stability.
- `Modules\Vision\OpenCv`: YuNet, LBF, and aperture fallback implementations.
- `Modules\Vision\MediaPipe`: MediaPipe Face Landmarker sidecar and mapping.
- `Modules\Vision\Onnx`: 3DDFA_V2 ONNX sidecar client and runtime discovery.
- `Modules\Vision\Pipeline`: backend composition and fusion.
- `Modules\Vision\Personalization`: avatar profiles, user login session, and capture quality.
- `Modules\Vision\Reconstruction`: observation stores, model building, audit history, dashboards, and review pages.
- `Modules\Vision\Diagnostics`: common timing records, batched live benchmark CSV output, and exact-frame A/B/C alignment auditing.
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

Live timing samples are batched off the UI thread into `Benchmarks\vision-pipeline-YYYYMMDD.csv` under the selected output folder. For a repeatable offline comparison across saved clips, run:

```powershell
.\.venv\Scripts\python.exe .\tools\BenchmarkVisionPipelines.py <video1> <video2> <video3> --sampling sequential --frames-per-video 24 --warmup-frames 3 --full-frames-per-video 1 --output <benchmark-folder>
```

## Safety and identity

Avatar Builder gathers geometric and expression evidence for a digital representation. Any downstream assistant or avatar must identify itself as a digital representation of a real person, never as the real person. Do not use this project for authentication, financial authority, legal identity, or autonomous impersonation.
