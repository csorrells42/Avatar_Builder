# Avatar Builder

Standalone WPF camera-based 3D avatar capture and reconstruction application.

## Repository split

Avatar Builder was separated from Episode Monitor at verified commit `56d07c9` after that commit was built, committed, and pushed to `csorrells42/Episode-Monitor`. Avatar development continues in this repository. The inherited narcolepsy event pipeline remains in this initial fork so no working behavior or evidence-processing code was lost during the split; it can be removed from Avatar Builder deliberately after the independent application identity and runtime have been verified.

This app records observable sustained low-motion events, optional start/end snapshots, event video clips, symptom markers, and event-list CSV exports for review. It is a data-gathering tool only and does not diagnose a medical condition.

The right-side control panel is split into **Narcolepsy Tracking** and **Avatar Building**. Narcolepsy Tracking contains episode watch controls, thresholds, alert-baseline calibration, symptom capture, event-list export, the 30-day event-log viewer, and evidence output. Avatar Building contains the remembered avatar-user selector, selected-user subject check, 3DDFA avatar capture controls, **Open Avatar System**, tracking fidelity, DX12 preview, and camera controls.

The output folder is user-selectable and can be set to an external HDD. The app remembers the selected folder in `AvatarBuilderOutputFolder.txt` beside the exe; that text file contains the folder where all runtime data is stored. If the pointer file is missing, empty, or points at a folder that no longer exists, startup creates the pointer file and asks the user to choose the storage folder. On this workstation the intended default is `D:\Avatar Builder Output`; otherwise the app falls back to `AvatarBuilderSessions` beside the exe only if the user cancels and the D drive is unavailable. Detected events are saved into timestamped folders containing `event_summary.json`, `event_summary.csv`, landmark timeline files, and optional start/end snapshots or annotated `event_video.mp4` evidence depending on the selected checkboxes. The visible event list is stored in SQLite under `EventData\episode_events.sqlite` in the output folder; each database event row has one event folder and stores the snapshot/video paths when those files exist. Today's rows are reloaded when the app opens. **View Event Log** shows the last 30 days. The app prunes database rows and tied event folders older than 30 days on startup. Right-click an event in either grid to delete that one database row and its tied event folder/evidence.

For a guided avatar-collection launch from a command prompt in the repo, run:

```cmd
make-avatar.cmd
```

That script builds the app if needed, starts Avatar Builder with `--easy-avatar --output-folder "D:\Avatar Builder Output"`, opens the Avatar System dashboard, and requests easy avatar capture. It still does **not** auto-confirm the subject, turn the camera on, or bypass capture-quality gates; choose the correct avatar user, check **This is [selected user]**, turn on the camera, and let the overlay lock before 3DDFA capture can run. The executable also accepts `--easy-avatar`, `--open-avatar-system`, `--start-avatar-learning`, and `--output-folder "D:\path"` directly; the legacy argument name is retained for launch-script compatibility.

Every successful project build refreshes `desktop-runtime\AvatarBuilder.exe` and its managed dependencies. The desktop shortcut and `make-avatar.cmd` both launch that stable location, so Debug and Release builds cannot leave them pointing at different old executables. The build-owned manifest removes files from the previous deployment while preserving runtime state such as `AvatarBuilderOutputFolder.txt` and startup logs. Run `tools\InstallDesktopShortcut.ps1` to create or repair the shortcut.

The live preview includes an overlay showing the current tracking state, motion threshold, low-motion timer, and active event duration so the user can confirm what the monitor is treating as acceptable baseline versus an event cue. The same overlay is burned into saved event videos, and videos include up to 60 seconds of pre-event lead-in frames from the rolling buffer.

Face cue tracking adds local, offline eye-region and lower-face/jaw-region change scores compared with an alert baseline. Eye openness is the primary face cue; jaw/lower-face droop is treated as supporting evidence. The landmark aperture path also builds an alert baseline, then contributes eyelid-closure, mouth-opening, and structural jaw-contour droop cue scores to event triggers, overlays, event videos, and event summaries. A rolling landmark trend layer separately tracks whether average eyelid aperture is steadily falling and whether mouth/lip aperture is rising over the recent window, using robust median pairwise slopes so a brief noisy spike or backend handoff does not dominate the trend. This helps capture gradual onset instead of only final threshold crossings. Use **Calibrate Alert Baseline** only when awake, centered, alert, and not experiencing symptoms before relying on the eye/jaw cue thresholds. The saved alert baseline lives under `AlertBaseline\alert_baseline.json` in the output folder and is reused on the next app launch. The app does not nag for recalibration just because time passed; it asks for calibration when no usable saved baseline exists and only gently suggests recalibration if the camera/mode setup changes or tracking looks off after lighting, glasses, seating distance, or camera changes. These cues document visible changes only.

Event summaries include both the final-frame landmark state and event-level landmark aggregates such as minimum eye opening, maximum eyelid closure, maximum mouth opening/change, maximum mouth-opening velocity, maximum jaw-contour droop/change/velocity, maximum landmark cue score, strongest rolling eye-closing trend, strongest rolling mouth-opening trend, landmark sample count, sources, and backend statuses observed during the event. Each event folder also gets `landmark_timeline.json` and `landmark_timeline.csv`, a frame-by-frame evidence trail with motion, eye/mouth openings, jaw-contour droop, confidence/quality, reconstruction flags, artifact flags, cue scores, and trend slopes. Landmark metrics include raw aperture ratios, confidence-smoothed aperture ratios, structural jaw-contour droop normalized against face width, left/right eye asymmetry, eye agreement, possible one-eye artifact flags, eye/mouth measurement-quality percentages, cue eligibility flags, rolling trend slopes, and event-level quality aggregates so the trigger path is less sensitive to one noisy frame and easier to review afterward. Saved summaries and timelines also carry temporal face-lock reliability, continuity, eye reliability, mouth reliability, and capture-quality fields so a captured event can show whether the tracker was strongly locked, still warming up, limited, acceptable for measurement collection, or avatar-grade at the moment evidence was recorded. When MediaPipe is active, summaries and timelines also retain Face Landmarker blendshape corroboration such as left/right/average blink, jaw open, and mouth close percentages, plus baseline-relative blink/jaw/mouth-close changes when an awake MediaPipe baseline has been established. They also export MediaPipe eye/mouth opening correction ratios, corrected-frame booleans, corrected-sample counts, and maximum absolute correction magnitudes so reviewers can distinguish raw contour measurements from blendshape-stabilized working measurements. When the aperture backend supplies the eye or mouth contour, summaries also include image-quality diagnostics such as glare, contrast, sharpness, and dark-aperture coverage so glasses-heavy clips can be reviewed with context instead of treating every measurement as equally trustworthy.

The live avatar path is now 3DDFA_V2 ONNX capture, not the older measurement-learning model. On startup the app asks which avatar user is at the camera, and remembered users are stored in `AvatarSystem\avatar_profiles.json` under the chosen output folder. Each new user gets a separate folder under `AvatarSystem\People\<profile-id>`. Live capture is gated by the selected-user checkbox plus **Start Avatar Capture**, so reconstruction data does not silently collect from a spouse, child, guest, or anyone else using the computer. MediaPipe/OpenCV still track eyes, brows, lips, mouth, jaw, face lock, and quality for overlays, event evidence, capture-quality gating, and the lightweight **MediaPipe Last 5** review page. 3DDFA_V2 ONNX owns avatar head pose, dense face geometry, shape/expression coefficients, the separate **3DDFA Last 5** dense reconstruction page, and Avatar Model Progress. The active avatar model stores bounded measurement-only 3DDFA observations, separates base identity from expression range, and rebuilds a pose-neutral averaged dense face model over time. The retired measurement-learning model is archived under `Modules\Deprecated` and excluded from active compilation.

Z remains an apparent camera-space value until zoom and physical distance are explicitly calibrated. User-facing pose readouts use XYZABC: X is horizontal frame position, Y is vertical frame position, Z is camera-facing depth, A rotates around X, B rotates around Y, and C rotates around Z. The active 3DDFA lane reports A/B/C plus dense vertices for avatar reconstruction; MediaPipe/OpenCV pose is kept as fast tracking and comparison evidence.

Storage policy for personalization:

The live app no longer writes passive measurement journals, readiness reports from the old rolling model, collection audits, measurement face previews, or measurement avatar packages.

- Keep by default: weighted eye/mouth/jaw/pose/blendshape measurements, pose bucket summaries, aggregate eye/lip/jaw shape profiles, quality scores, reliability scores, aggregate distributions, and accept/reject reasons.
- Keep only during explicit event capture: event video, start/end snapshots, burned-in overlay video, event summaries, and landmark timelines.
- Keep only during explicit avatar-training sessions: high-quality source video or stills intended for future face/avatar training.
- Do not keep during passive learning: continuous webcam video, raw frame images, room imagery, per-frame contour dumps, or redundant per-frame mesh dumps unless a review/testing command explicitly asks for them.

Raw video remains the high-value but storage-heavy artifact, so it should be captured deliberately with a clear purpose. Routine avatar review data is stored as JSON/HTML reports and bounded 3DDFA observation/model files in the selected output folder; event video and snapshots are created only when event capture options are enabled. The current avatar model files are `avatar_model_observations.json`, `avatar_model.json`, and `avatar_model_progress.html` in the selected user's avatar folder.

The avatar/3D-preview path builds on the same subject gate. The **Open Avatar System** button writes one dark local dashboard in the selected user's avatar folder. That page shows whether avatar capture is active, the current capture-quality state, the 3DDFA_V2 ONNX reconstruction lane status, fast eye/jaw/brow tracking status, model confidence/coverage, and links to the separate review pages plus Avatar Model Progress. **Open MediaPipe Last 5** writes a lightweight feature-lock page with only fast-tracker mesh, feature-loop, head-lock, and XYZABC/Z audit data. **Open 3DDFA Last 5** writes the heavier dense 3DDFA reconstruction viewer with the last five full-resolution 3DDFA samples only. **Open Avatar Model Progress** writes and opens the accumulated model viewer directly. The review/model pages refresh every 30 seconds and include pause controls where useful, so browser review trails behind the camera instead of competing with it frame-for-frame.

3D reconstruction is intentionally a sidecar contract, not WPF app logic. `Modules\Vision\Reconstruction` defines JSON work items, result manifests, and two-lane trust reports. MediaPipe remains the fast live tracking lane for overlays, eyelid/mouth/jaw evidence, and narcolepsy event cues. `Modules\Vision\Onnx` now contains the active 3DDFA_V2 ONNX avatar reconstruction lane: a C# sidecar client plus `Sidecar\three_ddfa_onnx_sidecar.py`, which calls the official 3DDFA_V2 `TDDFA_ONNX` solver out of process and returns dense vertices, sparse landmarks, 3DMM coefficients, and A/B/C pose evidence for avatar trust comparison. Run `tools\SetupThreeDdfaOnnxSidecar.ps1`, add the official `mb1_120x120` checkpoint or converted ONNX weight under `dependencies\vision\3ddfa-onnx\3DDFA_V2\weights`, and configure Python before that lane can run. The 3DDFA lane is asynchronous and latest-frame based; it must never block camera preview or the narcolepsy tracker. The older Deep3D/PyTorch sidecar contract is archived under `Modules\Deprecated`; Unity remains a future renderer/consumer candidate, not capture, event, or clinician-review core.

Use **Capture Symptoms** when symptoms are active, recently active, or an episode feels like it is beginning before automatic thresholds fire. This marks the event list, locks alert-baseline calibration until one symptom-free hour has passed, starts sleepy-state event evidence recording when the camera is on, and saves the same database entry, overlay, summaries, and optional video/snapshots as automatic captures.

Camera mode discovery uses both Windows Media Foundation and bundled FFmpeg DirectShow probing. Camera enumeration follows the stronger Jericho Down camera catalog pattern: physical cameras exposed through both Media Foundation and DirectShow are merged into one picker row, with the DirectShow twin retained as the fallback/control path while Media Foundation remains the standard fallback capture path. Live preview can still use Windows Media Foundation directly so selected HD/4K modes are requested from the camera driver, then fall back to bundled FFmpeg if Windows capture cannot open the device. For selected non-Auto modes, the Media Foundation path must keep the requested resolution; if it silently falls back to a lower-resolution stream, Avatar Builder rejects that path and tries the bundled FFmpeg fallback instead. The mode picker prefers high-quality compressed camera modes such as MJPEG/H.264 when available, and the **Tracking fidelity** selector can choose a real HD or 4K input mode instead of leaving the driver on its low-resolution Auto default. The app now starts with a 4K camera target by default while limiting analysis frames to 1920px at 15 fps, preserving face/eye/mouth detail without making the visible preview carry every analysis cost. Media Foundation now emits raw NV12 camera frames immediately to the DX12 preview renderer and only builds the smaller WPF analysis bitmap when the tracking loop needs it, which reduces choppy fallback preview behavior. The default-on **DX12 preview viewport** attempts the Jericho Down native texture camera path first; if that path cannot open the camera, the retry cooldown is short and the standard Media Foundation/FFmpeg path can still use the DX12 BGRA/NV12 upload renderer.

Camera preview and data collection are intentionally separated. The camera path emits frames to the live preview as quickly as the camera and renderer can sustain; episode tracking, 3DDFA avatar capture, and landmark analysis consume the newest available frame asynchronously and may skip intermediate frames when processing is busy. The live landmark detector uses a latest-frame worker instead of a FIFO queue, so if dense face tracking falls behind during movement it drops stale analysis frames and catches up to the newest camera frame. The live overlay is intentionally compact and throttled so WPF text layout does not become the next CPU bottleneck; full cue, event, capture-quality, and avatar lane details stay in event summaries, timelines, CSV/JSON outputs, and Avatar System reports.

The **Camera Controls** section uses the standard Windows DirectShow camera-control interfaces modeled after the Jericho Down camera stack. When the camera driver exposes them, it can show and adjust controls such as exposure, focus, zoom, brightness, contrast, sharpness, gain, and white balance. These controls include default ticks and a slight snap-to-default pull so lighting/focus/glare tuning can be repeatable without losing the dark UI.

Runtime code is split by module under `Modules`. Camera discovery/capture is further split into `Webcam/Common`, `Webcam/DirectShow`, `Webcam/Ffmpeg`, `Webcam/MediaFoundation`, `Webcam/Pipeline`, `Webcam/DirectX11`, and `Webcam/DirectX12`. Face/eye/mouth tracking is split into `Vision/Common`, `Vision/Analysis`, `Vision/OpenCv`, `Vision/MediaPipe`, `Vision/Pipeline`, `Vision/Personalization`, and `Vision/Reconstruction`. Event evidence lives in `AvatarBuilder.Modules.Episodes`, event video writing lives in `AvatarBuilder.Modules.Recording`, and shared runtime lookup lives in `AvatarBuilder.Modules.Infrastructure`. The architecture rule is that reusable capabilities stay self-contained and backend-neutral wherever possible, especially face identity, eyelid state, jaw/lip droop, personalization, event packaging, 3D/avatar reconstruction, and integration data for the main AI program. See `Modules/README.md` before adding new backends so sidecars and AI components enter through the narrowest useful interface.

The vision code uses a composite landmark tracker. The first backend is the local MediaPipe Face Landmarker Python sidecar under `Modules/Vision/MediaPipe/Sidecar`, using the model bundle under `dependencies/vision/dense-face-landmarks`. It is designed to output a dense 478-point face mesh that the C# mapper turns into eye, brow, nose, lip, jaw, cheek, forehead, and face contours. It also carries MediaPipe blendshape scores and facial transformation matrices into the same evidence model, giving independent blink/jaw/mouth-close signals that can corroborate contour measurements when glasses, glare, facial hair, or shadows make eyelid/lip edges noisy. `Modules/Vision/Analysis/FaceLandmarkMetricCalculator.cs` keeps raw contour aperture values for audit, then uses MediaPipe blink/jaw-open/mouth-close evidence conservatively to cap false-open eyelid/lip contours or lift false-closed lip contours in the working smoothed metrics; the delta is exposed as correction evidence instead of being hidden. `Modules/Vision/Analysis/FaceLandmarkCueAnalyzer.cs` turns those same blendshapes into awake-baseline-relative blink and mouth evidence for cue scoring. `Modules/Vision/Analysis/FaceFrameGeometryEstimator.cs` summarizes X/Y face position, frame fill, and apparent Z for overlays and review; 3DDFA_V2 ONNX owns avatar pose and dense face reconstruction. If Python or the MediaPipe imports are not configured, the app reports the sidecar status and continues down the stack. The next model-backed slot is the bundled OpenCV LBF 68-point facemark backend under `dependencies/vision/opencv/facemark`; it maps model output into face, eye, lip, and jaw contours before the aperture fallback runs. Face localization can also use OpenCV YuNet under `dependencies/vision/opencv/yunet`; when `face_detection_yunet_2023mar.onnx` is bundled, YuNet gets first chance to find the face before Haar fallback. YuNet's eye points and mouth-corner points also seed or repair the fallback eye/mouth aperture boxes, which helps when glasses make Haar eye or smile detections unreliable. If multiple tiers lock on the same face, the composite layer fuses them: high-confidence MediaPipe/dense landmarks keep the face, eye, lip, and jaw geometry unless the model result is weak or artifact-suppressed, while the aperture fallback can still repair weaker model contours and lower-confidence LBF detections. If a model tier is missing or cannot lock, the app reports that backend status and continues down the stack. The fallback detects the face, refines eye and mouth aperture contours inside the detected regions, and emits the same landmark metrics that the model-backed tiers use.

The OpenCV LBF tier and aperture fallback both keep temporal face-lock continuity. After a good 68-point LBF lock, the app can carry a short decayed landmark hold through brief detector misses so the overlay and metric pipeline do not blink off immediately. The aperture fallback also tries local expanded re-detection before giving up, then provides a short low-confidence face hold. Before measuring, the fallback refines each predicted eye and mouth region by probing nearby boxes and choosing the strongest plausible aperture evidence, which helps when glasses, glare, or camera auto-follow make the first cue box slightly wrong. Aperture measurements now use a managed 3x3 smoothing pass plus a column-profile pass inside the detected eye or mouth opening, which averages many vertical samples across the feature instead of trusting a single bounding-box height; this reduces the influence of outlier pixels, glasses frame fragments, and glare artifacts while avoiding an observed native OpenCV blur crash in bad candidate regions. Landmark metrics measure eye and lip opening along each contour's own corner-to-corner axis, so C-axis head tilt does not inflate eyelid or mouth aperture the way a screen-axis bounding box can. For structural model contours such as LBF and MediaPipe dense mesh output, the metric averages paired upper/lower eyelid or lip gaps instead of letting one noisy landmark define the whole opening. Before landmark metrics are calculated, a temporal reconstruction stage repairs likely glasses/occlusion artifacts. It can infer a missing eye contour from the paired eye, mirror first-frame paired-eye geometry around the face center when no prior frame exists, suppress abrupt one-eye aperture spikes, suppress shifted or oversized glasses/glare contours even when their aperture ratio looks plausible, remember the last safe eye geometry instead of the rejected artifact geometry, prevent low-fidelity fallback contours from rapidly reopening eyelids without stronger evidence, and carry forward short low-confidence mouth aperture gaps. Reconstructed left-eye, right-eye, mouth, and eye-artifact-suppression flags are carried into metrics, overlays, event summaries, CSV output, and offline evaluator summaries so review files can distinguish directly observed contours from inferred continuity. In live and burned-in event overlays, directly observed contours draw solid, reconstructed contours draw dashed, and artifact-suppressed eye contours draw dashed amber. This is not a substitute for real footage validation, but it makes the proof-of-concept better at tracking gradually closing eyelids and jaw/lip opening across imperfect webcam frames.

Vision model files:

```text
dependencies/vision/dense-face-landmarks/README.md
dependencies/vision/dense-face-landmarks/face_landmarker_manifest.json
dependencies/vision/opencv/facemark/README.md
dependencies/vision/opencv/facemark/lbfmodel.yaml
dependencies/vision/opencv/facemark/lbfmodel_manifest.json
dependencies/vision/opencv/yunet/README.md
dependencies/vision/opencv/yunet/face_detection_yunet_2023mar.onnx
dependencies/vision/opencv/yunet/yunet_manifest.json
dependencies/vision/3ddfa-onnx/README.md
dependencies/vision/3ddfa-onnx/three_ddfa_onnx_manifest.json
Modules/Vision/MediaPipe/Sidecar/mediapipe_face_landmarker_sidecar.py
Modules/Vision/MediaPipe/Sidecar/requirements.txt
Modules/Vision/Onnx/Sidecar/three_ddfa_onnx_sidecar.py
```

To download the official MediaPipe Face Landmarker model bundle into the portable dependency folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\InstallDenseFaceLandmarkerModel.ps1
```

This only installs the model file. To enable the local proof-of-concept MediaPipe sidecar, create the local Python environment:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\SetupMediaPipeSidecar.ps1 -PythonPath "C:\Path\To\python.exe"
```

Then point Avatar Builder at the created environment if it is not found automatically:

```powershell
$env:AVATAR_BUILDER_MEDIAPIPE_PYTHON = "C:\Users\clsor\Documents\Codex\2026-06-15\i-want-to-build-a-new\AvatarBuilder\.venv\Scripts\python.exe"
```

Without a configured Python environment that can import `mediapipe` and `cv2`, the dense sidecar stays inactive and the app uses the OpenCV LBF/aperture fallback path.

To install the official 3DDFA_V2 ONNX avatar reconstruction sidecar into the portable dependency folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\SetupThreeDdfaOnnxSidecar.ps1 -Python "C:\Path\To\python.exe"
```

That script clones or updates `dependencies\vision\3ddfa-onnx\3DDFA_V2` and installs the Python packages needed by the official ONNX adapter. It does not invent or bundle third-party weights; place the official `mb1_120x120.onnx` or `mb1_120x120.pth` in `dependencies\vision\3ddfa-onnx\3DDFA_V2\weights`. When active, this lane runs beside MediaPipe and exposes disagreement instead of replacing the live tracking path.

Retired vision evaluator, smoke, batch, and verifier tools from the old measurement-learning path live under `Modules\Deprecated\Tools`. They are kept for source reference only and are excluded from the active app build.

Digital legacy/avatar boundary:

Avatar Builder's face-motion learning data may be useful to a future local legacy avatar or "digital self" project, but that product must keep a clear identity rule: whenever it identifies itself, starts a session with a new person, communicates outside a trusted private context, or touches legal, medical, financial, identity, authorization, or agency topics, it must say it is a digital representation of a real person and not the living person. In private companion mode that disclosure can be handled through onboarding, settings/about text, and contextual reminders instead of repeatedly interrupting grief-sensitive conversation, but the system must never hide what it is, claim authority as the real person, call institutions as the person, or invent certainty.

Current local verification is the application build plus real camera testing:

```powershell
dotnet build .\AvatarBuilder.csproj --no-restore
```

## Runtime Dependency

FFmpeg is bundled under:

```text
dependencies/ffmpeg/win-x64/ffmpeg.exe
```

The app resolves FFmpeg relative to the executable folder and the project copies `dependencies` beside the exe during build and publish.

## Build

```powershell
dotnet build .\AvatarBuilder.csproj --no-restore
```
