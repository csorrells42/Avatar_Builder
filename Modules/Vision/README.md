# Vision Module

Namespace root: `AvatarBuilder.Modules.Vision`

This module owns visible-face evidence only. It should answer: where is the face, where are the eyes/lips, how open are they, how trustworthy is the measurement, and whether the trend is changing.

Recognition responsibilities should stay split into reusable capability layers. Face localization/identity gating, eyelid opening and droop, jaw/lip opening and droop, measurement quality, temporal reconstruction, personalization, and future avatar/3D reconstruction can share data models, but each should remain callable without the WPF shell. When a new model or AI wrapper is added, put the backend adapter in its library folder and keep the decision/measurement logic in `Analysis`, `Personalization`, or a new focused submodule with backend-neutral DTOs.

## Common

Namespace: `AvatarBuilder.Modules.Vision.Common`

Backend-neutral contracts and frame/result models:

- `FaceCueGuideLayout.cs`: normalized guide-box layout used by cue tracking and overlay alignment.
- `FaceFeatureDetection.cs`: coarse face/eye/mouth boxes, contours, confidence, and image-quality diagnostics emitted by region trackers.
- `FaceFeatureDetectionExtensions.cs`: conversion from feature detections into landmark frames.
- `FaceLandmarkFrame.cs`: backend-neutral face, eye, lip, and jaw landmarks plus reconstruction flags.
- `FaceLandmarkTrackingResult.cs`: one tracker result with frame, backend status, and availability information.
- `IFaceLandmarkTracker.cs`: common contract for stateless landmark trackers.
- `IStatefulFaceLandmarkTracker.cs`: common contract for trackers that keep temporal state and need reset/dispose semantics.

Coordinate rule: dense landmark and learned surface points own X/Y/Z position only. A/B/C orientation belongs to the frame, pose bucket, motion observation, or a future local surface patch built from neighboring points.

Change this folder when backend-neutral data shapes or tracker interfaces need to change.

## Analysis

Namespace: `AvatarBuilder.Modules.Vision.Analysis`

Backend-neutral measurement and scoring logic.

Files:

- `ContourOpeningEstimator.cs`: measures eye/lip opening from normalized contours without depending on OpenCV.
- `EyeInsetAgreementAnalyzer.cs`: compares full-frame eye opening measurements with a zoomed eye-inset reference, including paired rate, correlation, normalized error, direction agreement, slope-direction agreement, and an agreement trust score.
- `EyeInsetCueAnalysis.cs`: result object for baseline-relative zoomed eye-inset closure evidence.
- `EyeInsetCueAnalyzer.cs`: builds an awake baseline from high-quality zoomed eye-inset measurements and turns later eyelid closure into a cue score.
- `FaceCueAnalysis.cs`: result object for baseline-relative eye/jaw cue scoring.
- `FaceCueAnalyzer.cs`: image-region baseline analyzer used for older guide-box eye/jaw cues.
- `FaceCueAutoLayoutEstimator.cs`: estimates default guide-box placement from a face feature detection.
- `FaceLandmarkCueAnalysis.cs`: result object for landmark-driven eyelid, mouth, and structural jaw-droop cue scoring, including baseline-relative MediaPipe blink and mouth corroboration fields.
- `FaceLandmarkCueAnalyzer.cs`: compares current landmark metrics against an awake baseline, turns structural jaw-contour droop and MediaPipe blendshapes into baseline-relative corroboration, and produces cue eligibility/scores.
- `FaceLandmarkMetricCalculator.cs`: turns landmark contours into raw and smoothed eye/mouth opening metrics, structural jaw-contour droop, quality, asymmetry, and velocity; when MediaPipe blendshapes are available, it conservatively stabilizes false-open eyelid/lip contours and false-closed lip contours while keeping raw contour values and signed correction deltas for audit. The blink stabilizer ramps in early for glasses-heavy frames only after an open-eye reference has been learned.
- `FaceLandmarkMetrics.cs`: current-frame landmark measurement record consumed by overlays, triggers, summaries, and evaluators, including raw eye/mouth openings, working corrected/smoothed openings, MediaPipe/guard correction ratios, and corrected-frame flags. Offline evaluator CSV/HTML output keeps raw-versus-working values visible so glasses or fallback-contour repairs can be audited frame by frame.
- `FaceLandmarkTemporalReconstructor.cs`: repairs short missing-eye/mouth gaps, mirrors first-frame missing-eye geometry from the paired eye, suppresses likely glasses/occlusion shape artifacts, remembers safe eye geometry instead of rejected glare contours, and prevents low-fidelity fallback contours from rapidly reopening eyelids without stronger evidence.
- `FaceLandmarkTrendAnalysis.cs`: result object for rolling eye-closing and mouth-opening trend scoring.
- `FaceLandmarkTrendAnalyzer.cs`: robust rolling-slope trend detector for gradual eyelid closure and jaw/lip opening.
- `FaceFrameGeometryCalibration.cs`, `FaceFrameGeometry.cs`, `FaceFrameGeometryEstimator.cs`, `FaceFrameGeometryEstimatorInput.cs`: MediaPipe frame-geometry summary. It reports X/Y face center, frame fill, apparent Z/scale, camera-FOV-assisted scale, and Z confidence/source labels for overlays and review. 3DDFA_V2 owns avatar head pose and dense reconstruction; this lane does not run solvePnP or decide avatar orientation.
- `IFaceCueAnalyzer.cs`: contract for guide-box cue analyzers.

This folder should not know which camera backend produced a frame.

Change this folder when trigger math, calibration behavior, quality gates, reconstruction behavior, or trend detection needs work.

## OpenCv

Namespace: `AvatarBuilder.Modules.Vision.OpenCv`

OpenCV-backed implementations.

Files:

- `ApertureRegionRefiner.cs`: probes nearby eye/mouth boxes and chooses the strongest aperture evidence when initial boxes are slightly wrong.
- `EyeInsetApertureAnalyzer.cs`: measures the bottom-right zoomed eye inset used by synthetic/sample videos; auto mode now requires a credible sharp aperture region, so ordinary webcam clips skip optional inset evidence instead of false-locking on background or a normal face crop.
- `FaceCandidateSelector.cs`: chooses the best face candidate while preserving continuity with the previously tracked head, with a delayed high-confidence reacquire path for camera auto-follow reframes.
- `OpenCvApertureEstimator.cs`: low-level aperture extraction and image-quality diagnostics for eyes and mouth; uses managed 3x3 smoothing before thresholding to avoid native OpenCV blur failures on bad candidate regions.
- `OpenCvApertureLandmarkTracker.cs`: fallback landmark tracker built from detected face boxes and aperture contours.
- `OpenCvFaceFeatureTracker.cs`: face/eye/mouth region tracker using YuNet/Haar/aperture heuristics.
- `OpenCvFacemarkLandmarkTracker.cs`: OpenCV LBF 68-point landmark backend.
- `OpenCvFacemarkModelInfo.cs`: portable LBF model manifest/path/status reader.
- `OpenCvYuNetFaceDetector.cs`: YuNet ONNX face detector wrapper.
- `OpenCvYuNetModelInfo.cs`: portable YuNet model path/status reader.
- `README.md`: OpenCvSharp reference notes, package-family guidance, CUDA boundary, and resource-lifetime rules.

Keep OpenCvSharp types contained here where possible.

Change this folder when OpenCV detection/tracking behavior, model loading, glasses-related aperture refinement, or synthetic evaluator measurements need work.

## MediaPipe

Namespace: `AvatarBuilder.Modules.Vision.MediaPipe`

Target home for MediaPipe Face Landmarker integration. The proof-of-concept Python/MediaPipe sidecar lives behind the same tracker interface as OpenCV so the UI and episode pipeline do not care whether landmarks come from Python, OpenCV, or a later native runtime.

Files:

- `DenseFaceLandmarkModelInfo.cs`: MediaPipe dense face landmark manifest/model/runtime status reader.
- `DenseFaceMeshLandmarkTracker.cs`: reserved dense tracker slot; currently reports unavailable until an inference bridge is implemented.
- `MediaPipeFaceLandmarkerSidecarTracker.cs`: stateful tracker adapter that prefers the local Python sidecar when model, script, Python, and imports are ready.
- `MediaPipeFaceLandmarkerSidecarClient.cs`: process runner for the sidecar, including JSON line requests, JPEG frame encoding, timeouts, and restart-on-failure behavior.
- `MediaPipeFaceLandmarkerMapper.cs`: maps dense MediaPipe landmarks, blendshape scores, and facial transformation matrices into backend-neutral face, eye, brow, lip, jaw, blink, jaw-open, mouth-close, and pose evidence.
- `MediaPipeSidecarProtocol.cs`: request/response DTOs shared by the C# sidecar client, including dense landmarks and Face Landmarker blendshapes.
- `MediaPipeSidecarPythonEnvironment.cs`: finds the sidecar script, model file, configured Python executable, and import readiness.
- `README.md`: official MediaPipe Solutions/Face Landmarker reference notes and sidecar boundary rules.
- `Sidecar/mediapipe_face_landmarker_sidecar.py`: Python MediaPipe Tasks Face Landmarker process that returns dense normalized landmarks, blendshapes, and facial transformation matrices.
- `Sidecar/requirements.txt`: Python package requirements for the local proof-of-concept sidecar.

Change this folder when adding or debugging MediaPipe Face Landmarker integration.

## ONNX

Namespace: `AvatarBuilder.Modules.Vision.Onnx`

ONNX-backed model adapters and sidecar runtime checks. This folder is not a general dumping ground for model experiments; each adapter should name the concrete model family and the concrete job it performs.

Files:

- `ThreeDdfaOnnxModelInfo.cs`: reads `dependencies/vision/3ddfa-onnx/three_ddfa_onnx_manifest.json`, verifies the official 3DDFA_V2 repo files plus the required `mb1_120x120` checkpoint or converted ONNX weight, and reports model-bundle readiness.
- `ThreeDdfaOnnxSidecarEnvironment.cs`: finds Python, the sidecar script, the bundled or configured 3DDFA_V2 repo, the 3DDFA config, and import readiness.
- `ThreeDdfaOnnxSidecarProtocol.cs`: JSON request/response DTOs for dense vertices, sparse landmarks, A/B/C pose, face box, ROI, camera/shape/expression coefficients, and warnings.
- `ThreeDdfaOnnxReconstructionClient.cs`: process runner for the 3DDFA sidecar. It sends latest avatar frames as JPEG, reads one JSON-line response, times out safely, and restarts the worker after failures.
- `Sidecar/three_ddfa_onnx_sidecar.py`: Python bridge that calls the official 3DDFA_V2 `TDDFA_ONNX` solver. It prefers the face box supplied by Avatar Builder's live tracker and treats 3DDFA FaceBoxes as an optional fallback.

The 3DDFA lane is for avatar reconstruction: whole-face/head pose, dense geometry, coefficients, and pose-trust comparison. It must run beside MediaPipe/OpenCV and surface disagreement before any downstream avatar code trusts it. It must not replace the MediaPipe live tracker for narcolepsy events or block camera preview.

## Pipeline

Namespace: `AvatarBuilder.Modules.Vision.Pipeline`

Composition layer. `CompositeFaceLandmarkTracker` owns backend order and fusion rules, then emits the shared landmark frame/metrics path used by overlays, triggers, summaries, and offline evaluation.

Files:

- `CompositeFaceLandmarkTracker.cs`: orchestrates dense/MediaPipe, OpenCV LBF, and aperture fallback trackers; protects strong dense/MediaPipe contours from lower-confidence aperture overrides while still allowing aperture repair for weak model/LBF eye and mouth contours.

Change this folder when backend priority, fallback rules, or multi-backend fusion behavior needs work.

## Personalization

Namespace: `AvatarBuilder.Modules.Vision.Personalization`

Avatar-user registry, subject-gate support, and capture-quality scoring. The live WPF app uses this folder for selected-user identity, subject confirmation, per-user output folders, and 3DDFA avatar capture-quality gates.

Files:

- `AvatarProfile.cs`, `AvatarProfileStore.cs`: remembered avatar-user registry stored under the chosen output folder at `AvatarSystem\avatar_profiles.json`. New users get separate `AvatarSystem\People\<profile-id>` folders so 3DDFA avatar reports do not blend multiple people.
- `AvatarCaptureQualityInput.cs`, `AvatarCaptureQualityAssessment.cs`, `AvatarCaptureQualityAnalyzer.cs`: backend-neutral live capture-quality gate for 3DDFA avatar capture. It combines camera mode, normalized face scale, eye/mouth evidence, temporal stability, glasses/artifact risk, subject-gate state, and storage readiness into an inspectable score and correction suggestions.

Change this folder when the app needs better selected-user handling, subject-gate behavior, or capture-quality gates. This folder should not own camera capture, event recording, UI controls, or the active 3DDFA reconstruction worker. Retired rolling learning classes live under `Modules\Deprecated\Vision\Personalization`.

## Reconstruction

Namespace: `AvatarBuilder.Modules.Vision.Reconstruction`

Reusable 3D face reconstruction and avatar-preview contracts. This folder owns JSON work-item/result shapes for local reconstruction workers, active Avatar System reports, the lightweight MediaPipe Last 5 feature-lock audit, and the heavier 3DDFA Last 5 dense reconstruction audit. The active dense avatar lane is the 3DDFA_V2 ONNX contract; older measurement-preview/package/plan and Deep3D/PyTorch reference material is archived under `Modules\Deprecated`.

Files:

- `FaceReconstructionBackendIds.cs`: stable backend ids for MediaPipe/OpenCV comparison and 3DDFA_V2 ONNX reconstruction.
- `FaceReconstructionLaneStatus.cs`: two-lane status DTO for fast tracking versus dense avatar reconstruction.
- `FaceReconstructionSourceKinds.cs`: stable source-kind strings for live avatar frames and explicit training media.
- `FaceReconstructionSubjectGate.cs`: subject gate built from the active avatar profile and manual subject confirmation.
- `FaceReconstructionSourceFrame.cs`: metadata for explicit avatar-training media without storing image bytes in the manifest.
- `FaceReconstructionWorkItem.cs`: portable reconstruction job manifest.
- `FaceReconstructionResult.cs`: portable output manifest for meshes, coefficients, textures, preview renders, and quality reports.
- `FaceReconstructionJobStore.cs`: JSON read/write helper for work items.
- `ThreeDdfaOnnxReconstructionSpec.cs`: sidecar contract and work-item factory for the 3DDFA_V2 ONNX avatar reconstruction lane.
- `AvatarCaptureGuidanceInput.cs`, `AvatarCaptureGuidanceState.cs`, `AvatarCaptureGuidanceAdvisor.cs`: testable guidance layer that turns the current subject/camera/capture-quality state into one status/correction message for 3DDFA avatar capture. It does not start capture; the WPF Start/Stop Avatar Capture button is the only capture control.
- `AvatarModelObservationSet.cs`, `AvatarModelObservationStore.cs`, `AvatarModel.cs`, `AvatarModelBuilder.cs`, `AvatarModelStore.cs`: bounded persistent 3DDFA avatar model path. It stores measurement-only observations in the output folder, pose-normalizes dense vertices into a base identity mesh, keeps expression coefficients/ranges separate, scores pose/depth coverage, and writes `avatar_model_progress.html` for review.
- `AvatarModelHistory.cs`, `AvatarModelHistoryStore.cs`: persistent model-improvement and regression audit. Every 30-second rebuild appends a compact `avatar_model_history.jsonl` record, measures whole-face and eye/nose/mouth/chin RMS movement in face-span units, tracks confidence/coverage/stability/coefficient deltas and outlier candidates, and writes the live `avatar_model_regression.html` review page. The ledger is retained for up to 30 days or 86,400 rebuilds; review flags never silently delete observations.
- `LastGoodFeatureMeshSampleFactory.cs`, `LastGoodFeatureMeshWireframeBuilder.cs`, `LastGoodFeatureMeshStabilityAnalyzer.cs`, `LastGoodFeatureMeshStore.cs`: retain the last five subject-confirmed MediaPipe feature-lock frames that have direct high-quality eye and mouth feature lock, build the surface wireframe from MediaPipe's official Face Mesh tessellation topology plus highlighted facial feature edges, score head-locked feature-center drift, and write the dark interactive `last_10_good_features` JSON/HTML audit view. The retained samples store fast-tracker XYZABC/Z values and source labels for checking whether eyes, brows, nose, forehead, cheeks, lips, and jaw stay anchored to the head instead of sliding across it. Dense 3DDFA payloads are intentionally stripped from this page.
- `LastGoodThreeDdfaReport.cs`, `LastGoodThreeDdfaStore.cs`: retain the last five full-resolution 3DDFA_V2 ONNX dense reconstruction snapshots and write `last_5_3ddfa_reconstructions.json/html` as a separate heavy review page so dense vertex data is only loaded when that page is opened.
- The WPF live wireframe preview uses the same last-good dense samples and can draw them in the same eye-line/chin head-locked frame, giving a real-time sanity check before the saved audit page is opened.

Change this folder when adding a 3D reconstruction worker, avatar preview, mesh/coefficients export, or a main-AI integration path. Keep reconstruction worker dependencies out of the WPF app; the app should write/read manifests and let sidecars do backend-specific work. Retired measurement-only preview/package/plan classes live under `Modules\Deprecated\Vision\Reconstruction`.
