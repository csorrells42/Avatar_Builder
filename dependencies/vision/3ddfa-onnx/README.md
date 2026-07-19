# 3DDFA/ONNX Avatar Reconstruction Lane

This folder holds the portable 3DDFA/ONNX avatar reconstruction bundle.

The app looks for:

```text
dependencies/vision/3ddfa-onnx/three_ddfa_onnx_manifest.json
dependencies/vision/3ddfa-onnx/3DDFA_V2/TDDFA_ONNX.py
dependencies/vision/3ddfa-onnx/3DDFA_V2/configs/mb1_120x120.yml
dependencies/vision/3ddfa-onnx/3DDFA_V2/configs/bfm_noneck_v3.pkl
dependencies/vision/3ddfa-onnx/3DDFA_V2/configs/param_mean_std_62d_120x120.pkl
dependencies/vision/3ddfa-onnx/3DDFA_V2/weights/mb1_120x120.onnx
  or
dependencies/vision/3ddfa-onnx/3DDFA_V2/weights/mb1_120x120.pth
```

This lane is for avatar reconstruction, dense face/head pose, depth, and quality checks. It is intentionally separate from the fast MediaPipe/OpenCV tracking lane used for live overlays and narcolepsy event cues.

Run `tools\SetupThreeDdfaOnnxSidecar.ps1` from the repo root to clone or update the official 3DDFA_V2 repository and install Python packages. The script does not invent or redistribute third-party weights; add the official `mb1_120x120` checkpoint or converted ONNX weight under `3DDFA_V2\weights`.

The generated `3DDFA_V2` clone is intentionally ignored by Avatar Builder's Git repo because it is an external repository plus model bundle. Re-run the setup script on a new machine or checkout to recreate the local sidecar dependency.

Avatar Builder supplies its existing MediaPipe/OpenCV face box to the 3DDFA sidecar. The official 3DDFA FaceBoxes detector remains optional fallback detection for frames that do not already have a supplied face box.

Until the official 3DDFA_V2 repository, model weights, Python ONNX Runtime dependency, and sidecar adapter are present, Avatar Builder reports this lane as waiting and continues using the current measurement-only avatar system.
