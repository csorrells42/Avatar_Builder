# MediaPipe Visible-Evidence Reconstruction

This module builds normalized 3D face geometry from MediaPipe observations over time without treating the hidden-side 2D projection as a physical measurement.

## Data flow

- `MediaPipeGeometryFrame` is the immutable handoff from live tracking. It keeps camera identity, capture time, source dimensions, field of view, A/B/C head rotation, 478 landmarks, and the MediaPipe facial transform.
- `MediaPipeGeometryPipeline` permits exactly one frame in flight and one replaceable newest pending frame. The active frame always finishes; faster arrivals replace stale pending work, so camera and UI work cannot accumulate behind reconstruction.
- `MediaPipeNormalizedFaceReconstructor` normalizes translation and scale, solves canonical XYZ from directly visible multiview equations, rejects compressed hidden-side predictions, and accumulates the face oval in five-degree silhouette bins.
- `MediaPipeNormalizedFaceStore` atomically writes resumable solver state, an inspectable model, and a self-contained browser viewer beneath the selected profile's output folder.

## Evidence meaning

- `directly-measured`: enough angularly separated, low-residual observations constrain the point.
- `partially-measured`: useful direct evidence exists but depth or angular coverage is still weak.
- `expression-only`: eyes, lips, and irises are retained for animation evidence but are not treated as stable identity shape.
- `underconstrained`: the viewer may show a location for topology continuity, but it is deliberately dim and must not be interpreted as measured anatomy.

The visual hull uses only angle-binned occluding-contour support. Its rear and uncovered boundaries remain marked as underconstrained until additional calibrated views constrain them.

## Calibrated stereo lane

`Stereo` is the physical two-camera reconstruction path. It receives synchronized triangulated landmarks and their per-point reprojection/directness evidence from `Modules.Webcam.DualCamera`, transforms them into a stable head-fixed coordinate system, and robustly accumulates a persistent 478-point identity surface in inches.

The stereo lane deliberately remains separate from the monocular normalized solver. Its immutable frame handoff, latest-only worker, atomic store, and browser viewer live under `Reconstruction\Stereo`; output lives at `<profile folder>\StereoFaceGeometry`.
