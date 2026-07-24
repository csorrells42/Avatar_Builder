# Avatar Builder

Avatar Builder is a standalone Windows WPF application for gathering visible facial geometry for a future digital avatar. It uses MediaPipe for face landmarks and keeps the camera preview independent from analysis so expensive work can slow down without delaying the live view.

This application is not a medical device, an identity-authentication system, or proof that the person in front of the camera is a particular individual.

## Current workflow

1. Use **File > Login** to select the person whose measurements may be stored.
2. Select a camera and mode, then turn the camera on.
3. Keep one consenting person visible until **People memory** reports that the face is remembered.
4. Optionally enable **View > Show Face Mesh Overlay** or **Show Live Wireframe Only**.
5. Click **Start Avatar Capture** to link that remembered person to exactly one avatar profile and allow accepted MediaPipe geometry to update it.
6. Use **View Measured 3D Face** to inspect the accumulated visible-evidence model.

Stopping avatar capture leaves camera preview and live MediaPipe tracking available. Logging out stops profile capture.

## Self-throttling architecture

Camera capture and preview never wait for face analysis, model building, or disk writes.

- The camera renderer presents frames independently.
- Each MediaPipe analysis lane has one worker and one accepted frame slot.
- Passive multi-person recognition has its own lower-priority, one-in-flight observer.
- Main and stereo geometry builders have one worker and no waiting slot.
- While a slot is occupied, new inputs are ignored before copying or conversion. Accepted work always finishes.
- Model publication and persistence run away from the WPF UI thread.

This keeps latency and memory bounded when camera input is faster than the available processing rate.

## Menus

**File** contains Login/Logout, Choose Data Folder, and Exit.

**View** contains:

- Dual Camera Workspace
- DX12 Preview Viewport
- Show Face Mesh Overlay
- Show Live Wireframe Only
- MediaPipe Processor: GPU Texture (DirectML, Default) or CPU (Fallback)

MediaPipe is the primary face-landmark and measured-geometry backend. GPU is the default and runs converted copies of the official MediaPipe detector and 478-point landmark models through ONNX Runtime DirectML. The GPU path is enabled only after its detector, landmarker, and DirectML provider pass a startup probe; a failed probe leaves the official MediaPipe Tasks CPU graph available as the fallback. 3DDFA-V2 remains available as a backup face-box tracker and as the explicit dense scaffold that measured MediaPipe evidence reshapes.

The CPU and GPU paths share the same one-in-flight, zero-waiting contract. A frame is accepted only while the processor is idle, every accepted frame finishes, and arrivals during that work are dropped before pixel conversion or shared-memory copying. On the development workstation, the DirectML path measured about 8.4 ms per saved 4K frame through the complete C# client and shared-memory transport after a one-time process warmup. That is roughly 119 processed frames per second of available inference capacity; live camera rate and preview presentation remain independent limits.

## Dual-camera workspace

The preserved dual-camera module runs two independent camera and MediaPipe lanes. It supports physical checkerboard calibration, coordinate translation, stereo face capture, raw-point inspection, and probability-surface inspection.

Each camera lane accepts a frame only when its analysis slot is empty. Calibration and registration coordinators operate on the newest compatible observations without growing queues. Stereo model construction is asynchronous, one-in-flight, and zero-waiting.

## Stored data

`AvatarBuilderOutputFolder.txt` beside the executable stores the selected data-folder path. If the pointer is missing or invalid, startup asks for a folder. The normal workstation location is `D:\Avatar Builder Output`.

Each person has a separate directory under:

`AvatarSystem\People\<profile-id>`

Passive people memory is stored separately under:

`AvatarSystem\IdentityMemory\person_identity_memory.json`

People memory observes up to eight faces without blocking the camera. A brief sighting remains temporary and expires; a sustained, coherent encounter may retain normalized SFace embeddings, timestamps, encounter counts, and an optional avatar-profile link. It never stores the observation images. Avatar likeness, expressions, motion, and geometry remain gated behind the explicit **Start Avatar Capture** action, and one remembered person can be linked to only one avatar profile.

MediaPipe measured geometry and stereo geometry remain separate data products. Passive continuous webcam video is not stored.

## Module map

- `Modules\Webcam`: camera discovery, controls, capture, preview, DX11/DX12 interop, and dual-camera operation.
- `Modules\Vision\Common`: backend-neutral face and landmark contracts.
- `Modules\Vision\Analysis`: contour measurements, temporal repair, and lock quality.
- `Modules\Vision\Identity`: short-lived multi-person association, durable face memory, SFace embeddings, and explicit one-person/one-avatar consent linkage.
- `Modules\Vision\MediaPipe`: local Face Landmarker sidecar, overlay mapping, and measured reconstruction.
- `Modules\Vision\OpenCv`: supplemental localization and aperture fallbacks.
- `Modules\Vision\Pipeline`: tracker composition.
- `Modules\Vision\Personalization`: profiles, login session, and capture-quality assessment.
- `Modules\Vision\Diagnostics`: per-frame timing and backend-status contracts.
- `Modules\Infrastructure`: small runtime helpers.

See `Modules\README.md` and the README in each module before changing ownership boundaries.

## Build and run

```powershell
dotnet restore .\AvatarBuilder.csproj
dotnet build .\AvatarBuilder.csproj --no-restore
.\desktop-runtime\AvatarBuilder.exe
```

Every successful build refreshes `desktop-runtime`. The desktop shortcut and `make-avatar.cmd` target that stable build-owned location.

## Local sidecar

MediaPipe runs through the repository-local Python environment and bundled model assets. Runtime dependencies are copied beside the executable.

Live timing and backend status remain transient; they are not stored as report files.

## Digital-representation rule

Any downstream assistant or avatar must identify itself as a digital representation of a real person, never as the real person. Do not grant it financial authority, legal identity, or autonomous impersonation privileges.
