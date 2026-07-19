# Vision Module

Namespace root: `AvatarBuilder.Modules.Vision`

Vision owns reusable visible-face tracking and avatar reconstruction. It answers where the face and features are, how reliable their measurements are, and what dense 3D geometry and pose 3DDFA reconstructs.

## Common

Backend-neutral contracts and data shapes:

- `FaceCueGuideLayout.cs`: normalized face/eye/mouth guide layout used by overlays and fallback tracking.
- `FaceFeatureDetection.cs`: face and feature boxes, contours, confidence, and image-quality diagnostics.
- `FaceFeatureDetectionExtensions.cs`: conversion from region detections to landmark frames.
- `FaceLandmarkFrame.cs`: face, eye, brow, nose, lip, jaw, dense mesh, blendshape, and transform evidence.
- `FaceLandmarkTrackingResult.cs`: one backend result plus status and availability.
- `FaceLandmarkCropMapper.cs`: maps cropped inference coordinates back to the source frame.
- `IFaceLandmarkTracker.cs`, `IStatefulFaceLandmarkTracker.cs`, `IFaceLandmarkCropRefiner.cs`: backend contracts.

Dense points own X/Y/Z. Frame orientation owns A/B/C unless a point explicitly has its own local frame.

## Analysis

Backend-neutral measurements and temporal quality logic:

- `ContourOpeningEstimator.cs`: normalized eye and lip opening from landmark contours.
- `FaceCueAutoLayoutEstimator.cs`: follows the detected face with the visible guide layout.
- `FaceLandmarkMetricCalculator.cs`: raw and smoothed eye/mouth opening, jaw-contour droop, asymmetry, quality, velocity, and conservative MediaPipe blendshape corrections.
- `FaceLandmarkMetrics.cs`: current-frame measurement record used by overlays and capture-quality scoring.
- `FaceLandmarkTemporalReconstructor.cs`: repairs brief landmark gaps and suppresses glare/occlusion artifacts without learning an alert baseline.
- `FaceFrameGeometry*.cs`: X/Y center, frame fill, apparent Z/scale, camera-FOV calibration, and source confidence. This lane does not decide avatar A/B/C pose.
- `FaceLockStability*.cs`: temporal face-lock continuity and feature reliability.

This folder contains no medical-event trigger, alert calibration, or symptom classifier.

## OpenCv

OpenCV-backed localization and fallback tracking:

- `OpenCvYuNetFaceDetector.cs`: YuNet ONNX face detector.
- `OpenCvFacemarkLandmarkTracker.cs`: LBF 68-point landmark backend.
- `OpenCvFaceFeatureTracker.cs`: YuNet/Haar/aperture face and feature regions.
- `OpenCvApertureLandmarkTracker.cs`: fallback landmarks from detected regions and aperture contours.
- `OpenCvApertureEstimator.cs`, `ApertureRegionRefiner.cs`: low-level eye/mouth aperture extraction and candidate refinement.
- `FaceCandidateSelector.cs`: continuity-aware face selection and delayed reacquisition.

Keep OpenCvSharp types inside this folder where practical.

## MediaPipe

The fast dense landmark lane. The local Python sidecar returns a 478-point face mesh, blendshapes, and facial transformation matrices. C# maps those results into the shared eye, brow, nose, lip, jaw, cheek, forehead, contour, and quality models.

MediaPipe owns live feature lock and measurements. It does not own the persistent avatar pose or identity mesh.

## ONNX

The 3DDFA_V2 dense reconstruction lane. The C# client communicates with the local Python `TDDFA_ONNX` sidecar and receives dense vertices, topology, sparse landmarks, A/B/C pose, face/ROI data, and shape/expression coefficients.

3DDFA runs asynchronously from the camera and fast landmark lanes.

## Pipeline

`CompositeFaceLandmarkTracker.cs` owns backend ordering and fusion. Strong MediaPipe geometry is preserved; OpenCV can fill weak or unavailable eye/mouth regions through the same common contracts.

## Personalization

- `AvatarProfile.cs`, `AvatarProfileStore.cs`: per-user identity registry and storage folders.
- `AvatarCaptureQuality*.cs`: subject, camera, face-lock, measurement, artifact, and storage quality gate for accepted 3DDFA samples.

This folder does not own the camera, UI controls, or reconstruction worker.

## Reconstruction

Owns 3DDFA work/result contracts, bounded observation persistence, pose-normalized model building, model history/regression audits, capture guidance, the Avatar System dashboard, and the 3DDFA Last 5 review page. MediaPipe remains a live tracking and measurement lane without a stored Last 5 cache.

The base model and expression range remain separate. Never accept observations without the selected-user subject gate.
