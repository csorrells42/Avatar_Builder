# Vision MediaPipe

Namespace: `AvatarBuilder.Modules.Vision.MediaPipe`

This folder owns the MediaPipe Face Landmarker integration behind the shared landmark tracker contracts. Avatar Builder uses MediaPipe as a local sidecar/backend, not as UI logic.

Reference material:

- `https://developers.google.com/edge/mediapipe/solutions/guide`: official MediaPipe Solutions guide.
- `https://developers.google.com/edge/mediapipe/solutions/vision/face_landmarker`: official Face Landmarker guide.
- The current MediaPipe Solutions direction is Tasks plus packaged models. Legacy Face Mesh and Iris are listed as upgraded into Face Landmark detection, so new work should target Face Landmarker rather than older legacy APIs.
- Face Landmarker supports still images, decoded video frames, and live video streams. The live stream mode returns results asynchronously, which matches this folder's sidecar/client boundary.
- Face Landmarker can output a face mesh, blendshape scores, and facial transformation matrices. The app uses those outputs for feature and mesh review, blink, jaw, and mouth measurements, overlays, capture quality, and measured multiview geometry.

Implementation rules:

- Keep Python, MediaPipe Tasks, and model-bundle details inside this module.
- Keep the model file under `dependencies/vision/dense-face-landmarks` so the app remains portable.
- Do not call MediaPipe directly from `MainWindow.xaml.cs`; route through `CompositeFaceLandmarkTracker`.
- The sidecar intentionally uses explicit, slightly tolerant face detection/presence/tracking thresholds so glasses, partially closed eyes, lower-resolution frames, and camera movement do not drop dense lock as quickly as MediaPipe's defaults did in proof clips.
- Live image transport uses one reusable Windows named-memory surface per tracker. C# writes raw BGRA pixels directly into that surface and sends only dimensions, timing, and the mapping name through the JSON control channel. Do not reintroduce JPEG/Base64 frame transport; it adds compression latency, allocation pressure, and roughly one-third wire expansion before inference.
- Each MediaPipe tracker owns its sidecar client and named-memory surface. This ownership rule lets dual-camera tracking run two independent single-in-flight workers without sharing mutable image buffers or retaining a frame backlog.
- Treat blendshape evidence as corroboration unless quality/reliability gates say it is safe to use.
- If future code uses transformation matrices for 3D preview or avatar alignment, expose them through `Vision.Common` or `Vision.Reconstruction` DTOs rather than leaking sidecar JSON into app code.
- `MediaPipeFaceCanonicalizer` removes frame translation, rigid rotation, and scale from the 478 landmarks. Diagnostics use this face-relative coordinate system so moving closer to the camera or turning the head is not misreported as identity-geometry improvement.
- `MediaPipeFaceCanonicalizerSelfTest` proves that a synthetic face survives rigid XYZ rotation, translation, and scale without geometry drift.
- Long-session convergence measurement belongs to `Modules/Vision/Diagnostics/MediaPipeConvergenceAuditor`; do not add file writing, charts, or experimental reset controls to the live tracker.
