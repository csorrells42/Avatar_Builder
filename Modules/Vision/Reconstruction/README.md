# Vision Reconstruction

Namespace: `AvatarBuilder.Modules.Vision.Reconstruction`

This module owns login-gated 3DDFA observation persistence, avatar model construction, history audits, and local review reports. MediaPipe and OpenCV remain outside this module, while the active 3DDFA sidecar client lives under `Vision.Onnx`.

Coordinate rule: points and learned surface-profile samples carry X/Y/Z positions. A/B/C orientation is carried by the containing frame, pose bucket, motion observation, or future local surface patch; do not duplicate A/B/C on every isolated point unless that point has an explicit local tangent/normal frame.

Files:

- `FaceReconstructionBackendIds.cs`: stable backend identifiers for fast tracking comparison and the 3DDFA_V2 ONNX reconstruction lane.
- `FaceReconstructionLaneStatus.cs`: two-lane status DTO that explains what the fast MediaPipe/OpenCV tracking lane is doing, what the 3DDFA/ONNX avatar reconstruction lane is doing, whether dense reconstruction is available, and how much downstream avatar consumers should trust the current result.
- `AvatarCaptureGuidanceInput.cs`, `AvatarCaptureGuidanceState.cs`, `AvatarCaptureGuidanceAdvisor.cs`: backend-neutral capture guidance. It combines avatar-user login, camera/face-lock state, and capture quality into one plain-language status for the WPF panel. Start/stop behavior lives only on the main Avatar Capture button.
- `AvatarModelObservationSet.cs`, `AvatarModelObservationStore.cs`: bounded per-user 3DDFA observation store. It merges new accepted full-resolution 3DDFA samples into `avatar_model_observations.json`, keeps only the newest capped observation set, and stores measurement-only vertices/coefficients instead of webcam video or raw frame images.
- `AvatarModel.cs`, `AvatarModelBuilder.cs`, `AvatarModelStore.cs`: persistent avatar model path. The builder pose-normalizes accepted 3DDFA vertices, builds a weighted base identity mesh from shape/geometry evidence, tracks expression coefficients separately, scores pose/depth coverage, and writes `avatar_model.json` plus the interactive `avatar_model_progress.html` viewer.
- `ThreeDdfaReconstructionSnapshot.cs`, `MeshTopologyEdge.cs`: reusable full-resolution 3DDFA snapshot and mesh-topology contracts shared by review and persistent model storage.
- `LastGoodThreeDdfaReport.cs`, `LastGoodThreeDdfaStore.cs`: write `last_5_3ddfa_reconstructions.json` and `last_5_3ddfa_reconstructions.html` as the dense 3DDFA Last 5 audit page. It carries full-resolution vertices/topology, A/B/C pose, confidence, trust status, and warnings.
Rules:

- Never learn without an explicit login for the person in front of the camera.
- Keep raw continuous webcam video out of passive avatar collection.
- Keep identity and expression separate: sleepy/jaw-droop/speech/blink frames can improve expression range, but expression-heavy frames are downweighted for the base identity mesh.
- Use explicit training images or deliberate training clips only when photoreal 3D reconstruction needs pixels.
- Keep worker-specific dependencies out of the WPF app. A sidecar can be Python, ONNX Runtime, WSL, or Linux-only as long as the contract is JSON and the app can inspect the result.
- Treat MediaPipe/OpenCV and 3DDFA/ONNX as different lanes. MediaPipe is the fast live feature/overlay tracker. 3DDFA is the slower avatar reconstruction lane and should be used for dense head/face depth, coefficients, and trust comparison.

The active Avatar System dashboard is a lightweight live report: user login, capture state, capture quality, fast MediaPipe/OpenCV cue status, 3DDFA_V2 ONNX reconstruction status, current face-frame geometry, model confidence/coverage, Avatar Model Progress link, and 3DDFA Last 5 link.
