# Vision Reconstruction

Namespace: `AvatarBuilder.Modules.Vision.Reconstruction`

This module is the reusable boundary for 3D face reconstruction and avatar-preview work. It does not run MediaPipe, OpenCV, PyTorch, WSL, or a renderer directly. It defines subject-gated job and result contracts so Avatar Builder can export clean evidence to an out-of-process reconstruction worker.

Coordinate rule: points and learned surface-profile samples carry X/Y/Z positions. A/B/C orientation is carried by the containing frame, pose bucket, motion observation, or future local surface patch; do not duplicate A/B/C on every isolated point unless that point has an explicit local tangent/normal frame.

Files:

- `FaceReconstructionBackendIds.cs`: stable backend identifiers for fast tracking comparison and the 3DDFA_V2 ONNX reconstruction lane.
- `FaceReconstructionLaneStatus.cs`: two-lane status DTO that explains what the fast MediaPipe/OpenCV tracking lane is doing, what the 3DDFA/ONNX avatar reconstruction lane is doing, whether dense reconstruction is available, and how much downstream avatar consumers should trust the current result.
- `FaceReconstructionSourceKinds.cs`: stable source-kind strings for live avatar frames and explicit training media.
- `FaceReconstructionSubjectGate.cs`: subject identity gate built from the active avatar profile and manual subject confirmation.
- `FaceReconstructionSourceFrame.cs`: metadata for explicit avatar-training media; it references files and quality scores instead of storing image bytes.
- `FaceReconstructionWorkItem.cs`: portable job manifest for a reconstruction worker.
- `FaceReconstructionResult.cs`: portable result manifest for meshes, coefficients, textures, preview renders, and quality reports.
- `FaceReconstructionJobStore.cs`: JSON read/write helper for work items.
- `ThreeDdfaOnnxReconstructionSpec.cs`: sidecar contract and work-item factory for the 3DDFA_V2 ONNX avatar reconstruction lane. It requests dense 3D vertices, A/B/C head pose, shape coefficients, expression coefficients, landmark correspondence, and a quality report.
- `AvatarCaptureGuidanceInput.cs`, `AvatarCaptureGuidanceState.cs`, `AvatarCaptureGuidanceAdvisor.cs`: backend-neutral capture guidance. It combines subject confirmation, camera/face-lock state, capture quality, and tracking audit holds into one plain-language status for the WPF panel. Start/stop behavior lives only on the main Avatar Capture button.
- `AvatarModelObservationSet.cs`, `AvatarModelObservationStore.cs`: bounded per-user 3DDFA observation store. It merges new accepted full-resolution 3DDFA samples into `avatar_model_observations.json`, keeps only the newest capped observation set, and stores measurement-only vertices/coefficients instead of webcam video or raw frame images.
- `AvatarModel.cs`, `AvatarModelBuilder.cs`, `AvatarModelStore.cs`: persistent avatar model path. The builder pose-normalizes accepted 3DDFA vertices, builds a weighted base identity mesh from shape/geometry evidence, tracks expression coefficients separately, scores pose/depth coverage, and writes `avatar_model.json` plus the interactive `avatar_model_progress.html` viewer.
- `LastGoodFeatureMeshSample.cs`, `LastGoodFeatureMeshFeatureGroup.cs`, `LastGoodFeatureMeshWireframeEdge.cs`: rolling inspection DTOs for the last five accepted MediaPipe feature-lock frames. These keep full MediaPipe landmark points, named facial feature loops, generated wireframe edges, and frame geometry/Z provenance; dense 3DDFA snapshots are stored separately so the fast audit page stays lightweight.
- `LastGoodFeatureMeshStabilityReport.cs`, `LastGoodFeatureMeshFeatureStability.cs`, `LastGoodFeatureMeshStabilityAnalyzer.cs`: compact head-locked drift audit for the last-good dense samples. It normalizes each sample to the eye-line/chin coordinate frame, compares feature centers across the retained samples, and separately scores B/head-turn stability once the rolling cache contains enough left/right turn range. A/B/C/Z health is scored from samples relevant to that physical axis plus true neutral reference samples, and the report exposes compared-feature counts for each axis so off-axis lip/eye drift does not falsely contaminate another axis. It also reports separate A-axis tilt, C-axis tilt, and Z distance-change lock health so closer/farther motion, up/down tilt, and C-axis tilt can be reviewed without confusing them with B turns. The report is an inspection signal for 3DDFA/avatar review, not a live measurement-learning gate.
- `LastGoodFeatureMeshSampleFactory.cs`: accepts only dense mesh frames with direct eye and mouth feature lock, strong quality, and no reconstruction/artifact flags before preserving the 468-point sample. When `FaceFrameGeometry` is supplied, it stores X/Y frame center, apparent Z, learned-reference Z, and MediaPipe orientation metadata for sample review; 3DDFA remains the avatar pose authority.
- `LastGoodFeatureMeshWireframeBuilder.cs`: builds the surface wireframe from MediaPipe's official Face Mesh tessellation topology and promotes known eye, lip, brow, jaw, nose, cheek, forehead, and face-outline feature edges. Surface edges are still length-gated and provenance-tagged so display bugs are auditable, but the face wiring itself comes from the solved MediaPipe topology instead of an app-specific hand sketch.
- `LastGoodFeatureMeshStore.cs`: writes `last_10_good_features.json` and `last_10_good_features.html` as the lightweight MediaPipe Last 5 audit page. It includes the interactive point-cloud, wireframe, feature-loop, ghost-last-5 viewer, head-locked coordinate mode, frame-axis overlay, selected-sample frame/Z provenance, and an Avatar Model Progress link.
- `LastGoodThreeDdfaReport.cs`, `LastGoodThreeDdfaStore.cs`: write `last_5_3ddfa_reconstructions.json` and `last_5_3ddfa_reconstructions.html` as the separate dense 3DDFA Last 5 audit page. It carries full-resolution 3DDFA vertices/topology, A/B/C pose, confidence, trust status, and warnings without making the MediaPipe page load the same heavy payload.
Rules:

- Never learn from an unconfirmed or unknown subject.
- Keep raw continuous webcam video out of passive avatar collection.
- Keep identity and expression separate: sleepy/jaw-droop/speech/blink frames can improve expression range, but expression-heavy frames are downweighted for the base identity mesh.
- Use explicit training images or deliberate training clips only when photoreal 3D reconstruction needs pixels.
- Keep worker-specific dependencies out of the WPF app. A sidecar can be Python, ONNX Runtime, WSL, or Linux-only as long as the contract is JSON and the app can inspect the result.
- Treat MediaPipe/OpenCV and 3DDFA/ONNX as different lanes. MediaPipe is the fast live narcolepsy/overlay tracker. 3DDFA is the slower avatar reconstruction lane and should be used for dense head/face depth, coefficients, and trust comparison.

The active Avatar System dashboard is a lightweight live report: subject gate, capture state, capture quality, fast MediaPipe/OpenCV cue status, 3DDFA_V2 ONNX reconstruction status, current face-frame geometry, model confidence/coverage, Avatar Model Progress link, MediaPipe Last 5 link, and 3DDFA Last 5 link. The retired measurement-only preview/package/plan pipeline lives under `Modules\Deprecated\Vision\Reconstruction` for reference only.
