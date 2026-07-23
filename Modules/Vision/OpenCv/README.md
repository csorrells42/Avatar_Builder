# Vision OpenCv

Namespace: `AvatarBuilder.Modules.Vision.OpenCv`

This folder owns OpenCV-backed implementations behind the backend-neutral vision contracts. OpenCvSharp is the .NET wrapper reference for this layer, but OpenCvSharp types should stay contained here where possible so the rest of Avatar Builder can keep using neutral landmark, cue, and measurement DTOs.

Reference material:

- `https://github.com/shimat/opencvsharp`: OpenCvSharp wrapper source, package layout, examples, and API docs.
- Current app packages: `OpenCvSharp4` and `OpenCvSharp4.runtime.win`.
- OpenCvSharp4 is the maintenance family currently used by the app. OpenCvSharp5 is the active family for new .NET 8+ projects, so future OpenCV upgrades should evaluate a controlled migration instead of mixing package families.
- OpenCvSharp does not provide CUDA support through the stock packages. GPU/OpenCV-CUDA experiments should stay in a separate sidecar or native backend instead of leaking CUDA-specific assumptions into this module.
- MediaPipe owns active face tracking and measured geometry; OpenCV remains an optional localization and feature-tracking fallback.

Implementation rules:

- Dispose `Mat`, detectors, and other OpenCvSharp native-resource wrappers promptly.
- Keep WPF conversion at module edges; analysis code should receive normalized landmarks, contours, metrics, or simple image-quality values.
- Keep detector/model-specific code here; cue scoring belongs in `Vision.Analysis`, backend selection belongs in `Vision.Pipeline`, and subject learning belongs in `Vision.Personalization`.
- Prefer adding a small adapter or DTO over passing OpenCvSharp objects through the app shell.
