# Vision Module

Namespace root: `AvatarBuilder.Modules.Vision`

Vision owns reusable visible-face tracking and measured face reconstruction. It answers where the face and features are, how reliable those measurements are, and which geometry has direct evidence.

## Common

Backend-neutral contracts and data shapes:

- `FaceCueGuideLayout.cs`: normalized face, eye, and mouth guide layout.
- `FaceFeatureDetection.cs`: face and feature boxes, contours, confidence, and quality diagnostics.
- `FaceLandmarkFrame.cs`: face, eye, brow, nose, lip, jaw, mesh, blendshape, and transform evidence.
- `FaceLandmarkTrackingResult.cs`: one tracker result plus status and availability.
- `IFaceLandmarkTracker.cs`, `IStatefulFaceLandmarkTracker.cs`, `IFaceLandmarkCropRefiner.cs`: tracker contracts.

Dense points own X/Y/Z. Frame orientation owns A/B/C unless a point explicitly has a local frame.

## Analysis

- `ContourOpeningEstimator.cs`: normalized eye and lip opening.
- `FaceCueAutoLayoutEstimator.cs`: follows the detected face with the visible guide layout.
- `FaceLandmarkMetricCalculator.cs`: eye and mouth opening, jaw droop, asymmetry, quality, and velocity.
- `FaceLandmarkTemporalReconstructor.cs`: repairs brief gaps and suppresses glare or occlusion artifacts.
- `FaceFrameGeometry*.cs`: frame center, fill, apparent scale, field-of-view calibration, and confidence.
- `FaceLockStability*.cs`: temporal face-lock continuity and feature reliability.

This folder contains no medical-event trigger, alert calibration, or symptom classifier.

## MediaPipe

The active face-landmark lane. A local Python sidecar runs Face Landmarker in temporal video mode and returns a 478-point face mesh, blendshapes, facial transformation matrices, and timings. Shared memory carries image pixels without JPEG or Base64 serialization.

C# maps strong results into common eye, brow, nose, lip, jaw, cheek, forehead, contour, and quality models. The measured reconstruction submodule canonicalizes directly visible multiview evidence and persists it beneath the logged-in profile.

## OpenCv

Optional OpenCV-backed localization and aperture fallbacks. OpenCvSharp types remain inside this folder; the rest of the app consumes neutral landmarks and measurements.

## Diagnostics

`VisionPipelineDiagnostics.cs` is the stage-timing contract. `VisionBenchmarkRecorder.cs` batches samples away from the UI thread. `MediaPipeConvergenceAuditor` measures canonical landmark stability with bounded diagnostic work that may be skipped when busy.

## Pipeline

`CompositeFaceLandmarkTracker.cs` owns tracker composition. Strong MediaPipe geometry is preserved; OpenCV may fill a weak or unavailable region through the same contracts. The app exposes one face-tracking system and does not switch reconstruction backends at runtime.

## Personalization

- `AvatarProfile.cs`, `AvatarProfileStore.cs`: per-user registry and storage folders.
- `AvatarUserSession.cs`: current logged-in profile plus a generation token that rejects results finishing after logout or user change.
- `AvatarCaptureQuality*.cs`: login, camera, face-lock, measurement, artifact, and storage quality checks.

## Reconstruction

`MediaPipe/Reconstruction` owns monocular visible-evidence geometry and calibrated stereo accumulation. Each lane has one worker and one replaceable newest pending input. The current job finishes; stale pending inputs are discarded before they can create latency.

Never persist profile measurements without an active login for the selected user.
