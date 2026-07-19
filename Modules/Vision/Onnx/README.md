# Vision ONNX

Namespace: `AvatarBuilder.Modules.Vision.Onnx`

This module owns ONNX-backed model bundle discovery and sidecar adapters. It should not own avatar acceptance decisions, WPF controls, or long-term model policy.

Files:

- `ThreeDdfaOnnxModelInfo.cs`: reads `dependencies/vision/3ddfa-onnx/three_ddfa_onnx_manifest.json`, checks whether the official 3DDFA_V2 repo files and `mb1_120x120` checkpoint/ONNX weight are present, and reports model-bundle readiness.
- `ThreeDdfaOnnxSidecarEnvironment.cs`: finds the Python executable, the sidecar script, the bundled or configured 3DDFA_V2 repo, config file, and import readiness.
- `ThreeDdfaOnnxSidecarProtocol.cs`: JSON DTOs shared by the C# client and Python sidecar. Full meshes cross the pipe as compact flat coordinate/index arrays and are expanded into observed vertices, expression-free canonical identity vertices, and topology edges on the C# side. Fixed topology is transferred once per client and reused for later samples.
- `ThreeDdfaOnnxReconstructionClient.cs`: starts the Python worker, sends a latest avatar frame plus optional face box, reads one JSON-line reconstruction result, caches the fixed dense topology after the first full response, and restarts the sidecar after failures.
- `ThreeDdfaOnnxFaceTrackingMapper.cs`: maps the 3DDFA FaceBoxes result and standard 68 sparse landmarks into Avatar Builder's backend-neutral face, eye, brow, lip, and jaw tracking contracts.
- `Sidecar/three_ddfa_onnx_sidecar.py`: calls the official 3DDFA_V2 FaceBoxes and `TDDFA_ONNX` solvers. `faceBoxOnly` stops after bounded 640-pixel FaceBoxes detection; `tracking` returns parameters, 68 sparse landmarks, pose, and coefficients without dense reconstruction; `preview` returns a sampled observed mesh; `full` returns the complete 38,365-point observed mesh, topology, and a second 38,365-point canonical BFM identity mesh as compact flat arrays decoded from the shape coefficients with identity rotation, zero translation, and zero expression.

Setup:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\SetupThreeDdfaOnnxSidecar.ps1 -Python "C:\Path\To\python.exe"
```

Place the official `mb1_120x120.onnx` or `mb1_120x120.pth` weight under `dependencies\vision\3ddfa-onnx\3DDFA_V2\weights`. MediaPipe face-box mode supplies its selected box to 3DDFA avatar reconstruction, so FaceBoxes is optional in that mode. Selecting 3DDFA-V2 as the live Face Box System passes no caller box and therefore requires the bundled 3DDFA FaceBoxes detector.

Rules:

- Keep 3DDFA/ONNX reconstruction ownership separate from the selectable live face-box lane.
- Keep the fast tracking lane queue-free and request full dense reconstruction no more than once every 10 seconds; model/report publication has its own 30-second cadence.
- Treat MediaPipe/3DDFA A/B/C agreement as diagnostic evidence, never as a gate that selects `tracking` instead of `full` mode.
- Keep observed camera-space geometry and canonical identity geometry as separate protocol fields. Never estimate the persistent identity by applying application-defined inverse A/B/C rotations to image-space vertices.
- The avatar system may use this lane for dense reconstruction, head pose, coefficients, and trust checks.
- In MediaPipe face-box mode, the fast MediaPipe/OpenCV feature tracker should not wait on 3DDFA inference.
- In 3DDFA-V2 face-box mode, one 3DDFA pass supplies FaceBoxes, 68-point feature tracking, pose, and any due avatar sample; do not launch a duplicate reconstruction for the same frame.
- Reacquire with FaceBoxes periodically and use the latest 3DDFA sparse landmarks as the intervening temporal box. Retry once with FaceBoxes when a caller box loses the face.
- Emit decode, face-box, inference, parameter, sparse, dense, pose, and serialization timings so performance changes are measurable.
- Switching face-box systems invalidates pending results, disposes the inactive tracker, and prevents stale frames from crossing the backend boundary.
- Add `Microsoft.ML.OnnxRuntime` in-process only if it gives a concrete benefit over the sidecar and does not pull UI/camera rendering into model code.
