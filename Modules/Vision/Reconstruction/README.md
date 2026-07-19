# Vision Reconstruction

Namespace: `AvatarBuilder.Modules.Vision.Reconstruction`

This module owns login-gated 3DDFA observation persistence, avatar model construction, history audits, and local review reports. MediaPipe and OpenCV remain outside this module, while the active 3DDFA sidecar client lives under `Vision.Onnx`.

Coordinate rule: points and learned surface-profile samples carry X/Y/Z positions. A/B/C orientation is carried by the containing frame, pose bucket, motion observation, or future local surface patch; do not duplicate A/B/C on every isolated point unless that point has an explicit local tangent/normal frame.

Files:

- `FaceReconstructionBackendIds.cs`: stable backend identifiers for fast tracking comparison and the 3DDFA_V2 ONNX reconstruction lane.
- `FaceReconstructionLaneStatus.cs`: two-lane status DTO that explains what the selected live face-box tracker is doing, what the 3DDFA/ONNX avatar reconstruction lane is doing, whether dense reconstruction is available, and how much downstream avatar consumers should trust the current result.
- `AvatarCaptureGuidanceInput.cs`, `AvatarCaptureGuidanceState.cs`, `AvatarCaptureGuidanceAdvisor.cs`: backend-neutral capture guidance. It combines avatar-user login, camera/face-lock state, and capture quality into one plain-language status for the WPF panel. Start/stop behavior lives only on the main Avatar Capture button.
- `AvatarModelObservationSet.cs`, `AvatarModelObservationStore.cs`: bounded per-user 3DDFA observation store. It merges new accepted full-resolution samples into `avatar_model_observations.json.gz`, migrates legacy uncompressed JSON after a successful write, caches the active profile in-process, and reports whether the set changed. Canonical identity geometry is retained for every learning sample; camera-space geometry is limited to the five newest review samples. Webcam video and raw frame images are not stored.
- `AvatarModel.cs`, `AvatarModelBuilder.cs`, `AvatarModelStore.cs`: persistent avatar model path. The builder averages canonical 3DDFA BFM identity vertices directly in their shared model coordinates, uses rigid generalized Procrustes only for legacy camera-space observations, tracks expression coefficients separately, scores pose/depth coverage, and writes `avatar_model.json` plus the interactive `avatar_model_progress.html` viewer. The model store caches the active profile, and the viewer includes every identity vertex while sampling only topology edges.
- `DenseMeshRigidAligner.cs`: reflection-safe Kabsch rigid alignment for legacy complete meshes. It may rotate and translate a scan but never deform it.
- `ThreeDdfaReconstructionSnapshot.cs`, `MeshTopologyEdge.cs`: reusable full-resolution 3DDFA snapshot and mesh-topology contracts shared by review and persistent model storage.
- `LastGoodThreeDdfaReport.cs`, `LastGoodThreeDdfaStore.cs`: rebuild the self-contained `last_5_3ddfa_reconstructions.html` viewer from the persisted observation database so review survives restarts. It carries five observed full-resolution meshes, one shared topology, A/B/C pose, confidence, trust status, and warnings without duplicating either the canonical identity mesh or a standalone JSON cache.
Rules:

- Never learn without an explicit login for the person in front of the camera.
- Accept full dense observations from the 3DDFA-owned pose/depth lane when login, capture, camera, face-lock, quality, and storage gates pass. Keep the exact-frame MediaPipe/3DDFA A/B/C audit advisory; it must never veto 3DDFA reconstruction.
- Rebuild large model and Last 5 artifacts only when observations change; lightweight dashboards may refresh independently.
- Keep raw continuous webcam video out of passive avatar collection.
- Keep identity and expression separate: sleepy/jaw-droop/speech/blink frames can improve expression range, but expression-heavy frames are downweighted for the base identity mesh.
- Treat canonical 3DDFA identity vertices as the learning source of truth. Preserve observed camera-space vertices only for the five-sample review window, and never feed them through a homegrown inverse-pose transform.
- Use explicit training images or deliberate training clips only when photoreal 3D reconstruction needs pixels.
- Keep worker-specific dependencies out of the WPF app. A sidecar can be Python, ONNX Runtime, WSL, or Linux-only as long as the contract is JSON and the app can inspect the result.
- Treat live face-box tracking and persistent avatar reconstruction as separate responsibilities. MediaPipe is the default fast live feature/overlay tracker. 3DDFA-V2 can own live FaceBoxes and sparse features for comparison, but 3DDFA remains the authority for dense head/face depth, coefficients, and avatar trust.

The active Avatar System dashboard is a lightweight live report: user login, capture state, capture quality, selected face-box tracker status, 3DDFA_V2 ONNX reconstruction status, current face-frame geometry, model confidence/coverage, Avatar Model Progress link, and 3DDFA Last 5 link.
