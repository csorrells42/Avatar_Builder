# Vision Reconstruction Contracts

Namespace: `AvatarBuilder.Modules.Vision.Reconstruction`

This folder contains the small topology contracts shared by the active MediaPipe and stereo reconstruction viewers.

- `MeshTopologyEdge.cs`: immutable pair of vertex indices describing one display edge.

Persistent measured geometry lives under `Vision.MediaPipe.Reconstruction`. Camera-pair calibration and stereo input coordination live under `Webcam.DualCamera`.

Coordinate rule: points carry X/Y/Z positions. A/B/C orientation belongs to the containing frame, pose bucket, or local surface patch; do not duplicate orientation on every isolated point.

Rules:

- Require an explicit logged-in profile before storing measurements.
- Keep identity-shape evidence separate from eyes, lips, irises, and other expression evidence.
- Mark predicted or underconstrained geometry honestly in viewers.
- Keep continuous webcam video out of passive avatar collection.
- Move disk writes and model publication away from camera and WPF threads.
- Bound every real-time consumer to one active job and one replaceable newest pending input.
