# Vision MediaPipe

Namespace: `AvatarBuilder.Modules.Vision.MediaPipe`

This folder owns the MediaPipe Face Landmarker integration behind the shared landmark tracker contracts. Avatar Builder uses MediaPipe as a local sidecar/backend, not as UI logic.

Reference material:

- `https://developers.google.com/edge/mediapipe/solutions/guide`: official MediaPipe Solutions guide.
- `https://developers.google.com/edge/mediapipe/solutions/vision/face_landmarker`: official Face Landmarker guide.
- The current MediaPipe Solutions direction is Tasks plus packaged models. Legacy Face Mesh and Iris are listed as upgraded into Face Landmark detection, so new work should target Face Landmarker rather than older legacy APIs.
- Face Landmarker supports still images, decoded video frames, and live video streams. The live stream mode returns results asynchronously, which matches this folder's sidecar/client boundary.
- Face Landmarker can output a dense face mesh, blendshape scores, and facial transformation matrices. The app exploits those outputs for feature/mesh review, blink/jaw/mouth corroboration, overlays, narcolepsy cues, and frame-geometry summaries. 3DDFA_V2 ONNX owns avatar pose and dense face reconstruction.

Implementation rules:

- Keep Python, MediaPipe Tasks, and model-bundle details inside this module.
- Keep the model file under `dependencies/vision/dense-face-landmarks` so the app remains portable.
- Do not call MediaPipe directly from `MainWindow.xaml.cs`; route through `CompositeFaceLandmarkTracker`.
- The sidecar intentionally uses explicit, slightly tolerant face detection/presence/tracking thresholds so glasses, partially closed eyes, lower-resolution frames, and camera movement do not drop dense lock as quickly as MediaPipe's defaults did in proof clips.
- Treat blendshape evidence as corroboration unless quality/reliability gates say it is safe to use.
- If future code uses transformation matrices for 3D preview or avatar alignment, expose them through `Vision.Common` or `Vision.Reconstruction` DTOs rather than leaking sidecar JSON into app code.
