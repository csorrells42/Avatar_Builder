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
- GPU texture DirectML is the default live-camera path. It reads the capture-owned D3D12 NV12 texture, performs color conversion, crop, resize, and normalization in a compute shader, and binds the resulting D3D12 buffers directly to the detector and 478-point ONNX sessions.
- The in-process DirectML sessions use the same D3D12 device and compute queue as their GPU tensor allocations. Do not replace the custom-device DML binding with the device-index overload.
- CPU (Stable) remains the explicit compatibility fallback and runs the official MediaPipe Tasks graph.
- The GPU startup check confirms both ONNX models. Hardware/session activation happens on the analysis lane when its first texture arrives, never on camera ingestion or display.
- Bitmap tracking routes through `CompositeFaceLandmarkTracker`; native D3D12 tracking routes through `MediaPipeDirectMlTextureTracker`.
- The sidecar intentionally uses explicit, slightly tolerant face detection/presence/tracking thresholds so glasses, partially closed eyes, lower-resolution frames, and camera movement do not drop dense lock as quickly as MediaPipe's defaults did in proof clips.
- Live image transport uses one reusable Windows named-memory surface per tracker. C# writes raw BGRA pixels directly into that surface and sends only dimensions, timing, and the mapping name through the JSON control channel. Do not reintroduce JPEG/Base64 frame transport; it adds compression latency, allocation pressure, and roughly one-third wire expansion before inference.
- Each MediaPipe tracker owns its sidecar client and named-memory surface. This ownership rule lets dual-camera tracking run two independent single-in-flight workers without sharing mutable image buffers or retaining a frame backlog.
- A tracker accepts work only while its analysis slot is empty. Busy arrivals are discarded before pixel conversion or shared-memory copying, and accepted work is never replaced. Do not add a latest-frame mailbox or a waiting queue.
- Treat blendshape evidence as corroboration unless quality/reliability gates say it is safe to use.
- If future code uses transformation matrices for 3D preview or avatar alignment, expose them through `Vision.Common` or `Vision.Reconstruction` DTOs rather than leaking sidecar JSON into app code.
Validation:

- `tools/VisionSmoke/compare_mediapipe_backends.py` compares CPU Tasks and DirectML landmarks on a saved still.
- `tools/VisionSmoke/probe_directml_transport.py` exercises the exact Windows named-memory protocol used by the GPU sidecar.
- `tools/VisionSmoke` can run the C# tracker, process launcher, named-memory transport, JSON response, and landmark mapper end to end with `--directml-image`.
- `tools/VisionSmoke --directml-texture-video <video>` uploads a decoded frame as a real planar NV12 D3D12 texture and exercises the in-process GPU preprocess, DML resource binding, both ONNX sessions, and landmark mapping.
- The development baseline returned all 478 points, 94.8% face-box intersection-over-union, and 3.1 px median landmark disagreement (0.48% of face diagonal). The complete warmed C# path measured about 8.4 ms per saved 4K frame, or roughly 119 processed frames per second.
- The 2026-07-23 GPU-texture hardware smoke test on a real 3840x2160 video frame returned all 478 points and measured 2.60 ms steady GPU preprocess plus inference, about 385 fps; its one-time session startup was about 1.00 s and ran entirely on the analysis lane.
