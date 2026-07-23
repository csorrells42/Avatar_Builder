# Calibrated MediaPipe Stereo Reconstruction

Namespace: `AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo`

This module turns synchronized, physically calibrated dual-camera landmarks into a persistent face surface measured in inches.

## Ownership

- `MediaPipeStereoGeometryFrame` carries the calibrated 478-point solve and optional references to the two existing analysis images. It does not copy another pair of 4K frames.
- `MediaPipeStereoFacePipeline` owns one active observation and one replaceable newest pending observation. The active observation finishes; newer arrivals replace the pending input instead of forming a queue.
- `MediaPipeStereoFaceReconstructor` removes rigid head translation and rotation, applies per-point directness/reprojection gates, and robustly accumulates canonical vertices.
- `MediaPipeDenseStereoMatcher` measures additional stable surface locations inside the MediaPipe triangles with pyramidal optical flow. Forward/backward flow, epipolar distance, and calibrated reprojection gates reject mismatches before triangulation.
- `MediaPipeStereoFaceStore` atomically persists resumable state, the inspectable model, and the viewer beneath the active profile.
- Persistent geometry is bound to the exact physical calibration session that produced it. A newly solved camera rig starts a fresh coordinate model automatically instead of averaging incompatible calibrations together.
- `MediaPipeStereoFaceViewerPage` renders the physical point cloud and MediaPipe topology with confidence/evidence coloring.
- `MediaPipeStereoProbabilityFaceBuilder` extracts one dominant repeated depth per 0.12-inch face region, rejects isolated specks, and connects physically continuous neighbors without changing the stored evidence. It separately derives a display skin by filling only well-supported interior holes and applying confidence-weighted bilateral depth smoothing that preserves strong facial edges.
- High-resolution probability-face requests share one asynchronous worker. Repeated requests coalesce into one rerun from the newest stereo state, so extraction cannot backlog or compete with the camera lanes.
- `MediaPipeStereoProbabilityFaceViewerPage` renders the derived smooth face and original measured surface side by side through selectable modes, with optional wireframe and measured-sample overlays.
- The raw-evidence probability pileup records both direct MediaPipe triangulations and dense image matches, including rejected measurements. Dense matching applies independent local contrast normalization before optical flow so cameras with different exposure and color processing can still contribute comparable image evidence.

## Coordinate convention

- Origin: midpoint between the two eye centers.
- X: left-to-right eye axis.
- Y: chin toward forehead.
- Z: out of the face toward the nose.
- Units: calibrated inches.

Dynamic eye and lip landmarks are retained for future animation evidence but are down-weighted and excluded from stable identity confidence. A point is called directly measured only when both camera rays intersect with acceptable reprojection error and at least one camera has a useful direct view.

The 478 MediaPipe anchors and 15,372 possible dense image-matched samples remain separate evidence layers in storage and in the viewer, for 15,850 possible measured points in total. The dense layer does not invent new MediaPipe landmarks or interpolate a generic surface. It finds corresponding image evidence on a stable interior lattice inside each MediaPipe triangle and stores only successfully triangulated locations in head-fixed inches. Each triangle originates from the camera with the larger normalized projected area, so both cheeks can use their more direct camera view instead of forcing every dense match through Camera A.

Dense work executes only on the reconstruction worker. If it cannot keep up, one newest synchronized pair is retained while older pending input is replaced; camera preview and each MediaPipe lane continue independently.

## Verification

`MediaPipeStereoFaceReconstructorSelfTest` moves one synthetic face through changing translation and A/B/C rotation, then verifies that the recovered canonical dimensions and point locations remain invariant. `MediaPipeDenseStereoMatcherSelfTest` gives two calibrated synthetic cameras a known image disparity and verifies both dense match coverage and recovered physical depth.
