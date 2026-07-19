# Vision ONNX

Namespace: `AvatarBuilder.Modules.Vision.Onnx`

This module owns ONNX-backed model bundle discovery and sidecar adapters. It should not own avatar acceptance decisions, WPF controls, or long-term model policy.

Files:

- `ThreeDdfaOnnxModelInfo.cs`: reads `dependencies/vision/3ddfa-onnx/three_ddfa_onnx_manifest.json`, checks whether the official 3DDFA_V2 repo files and `mb1_120x120` checkpoint/ONNX weight are present, and reports model-bundle readiness.
- `ThreeDdfaOnnxSidecarEnvironment.cs`: finds the Python executable, the sidecar script, the bundled or configured 3DDFA_V2 repo, config file, and import readiness.
- `ThreeDdfaOnnxSidecarProtocol.cs`: JSON DTOs shared by the C# client and Python sidecar, including dense vertices and topology edges for preview/review consumers.
- `ThreeDdfaOnnxReconstructionClient.cs`: starts the Python worker, sends a latest avatar frame plus optional face box, reads one JSON-line reconstruction result, and restarts the sidecar after failures.
- `Sidecar/three_ddfa_onnx_sidecar.py`: calls the official 3DDFA_V2 `TDDFA_ONNX` solver and returns dense vertices, dense-mesh topology edges, sparse landmarks, A/B/C pose, shape/expression coefficients, and trust warnings. `returnDenseVertices=true` returns the full 38k-vertex mesh and full topology for Last 10 review; otherwise `denseSampleStride` limits the preview payload while the solver still reconstructs the full dense face for pose and confidence.

Setup:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\SetupThreeDdfaOnnxSidecar.ps1 -Python "C:\Path\To\python.exe"
```

Place the official `mb1_120x120.onnx` or `mb1_120x120.pth` weight under `dependencies\vision\3ddfa-onnx\3DDFA_V2\weights`. The sidecar uses Avatar Builder's existing MediaPipe/OpenCV face box first, so the optional 3DDFA FaceBoxes fallback can be missing without blocking frames that already have a live face lock.

Rules:

- Keep 3DDFA/ONNX work separate from the MediaPipe live tracking lane.
- The avatar system may use this lane for dense reconstruction, head pose, coefficients, and trust checks.
- The fast MediaPipe/OpenCV feature tracker should not wait on 3DDFA inference.
- Add `Microsoft.ML.OnnxRuntime` in-process only if it gives a concrete benefit over the sidecar and does not pull UI/camera rendering into model code.
