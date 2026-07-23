# Vision Diagnostics

Namespace: `AvatarBuilder.Modules.Vision.Diagnostics`

This module measures vision behavior without owning camera capture, face tracking, temporal reconstruction, or avatar storage. Diagnostic work must remain optional and must never create backpressure in the live preview pipeline.

## MediaPipe convergence audit

`MediaPipeConvergenceAuditor` receives both sides of the existing handoff:

- raw MediaPipe landmarks before Avatar Builder temporal reconstruction;
- reconstructed landmarks after Avatar Builder contour stabilization.

It uses one detached single-in-flight worker. If diagnostics are busy, incoming audit frames are dropped; an accepted audit always finishes, and camera frames and live face tracking never wait for report generation or disk I/O.

The audit writes one scalar sample and one exact 478-point raw landmark snapshot per second under:

`<profile>/Benchmarks/MediaPipeConvergence/<session-id>/`

Artifacts:

- `mediapipe_convergence_audit.html`: live five-second-refresh report;
- `mediapipe_convergence_summary.json`: machine-readable session summary;
- `mediapipe_convergence_samples.csv`: canonical stability and motion measurements;
- `mediapipe_raw_landmarks.mpaudit`: exact raw landmark evidence;
- `mediapipe_convergence_markers.csv`: human observations and reset markers.

The canonical RMS values remove translation, rigid rotation, and scale before comparison. `stable-shell` uses face oval, cheek, and nose landmarks while excluding highly expressive eye and lip regions. `app contour correction` measures how much Avatar Builder changed the raw MediaPipe result.

Use the View menu to start a controlled session, add a human observation marker, reset only MediaPipe's persistent VIDEO tracker, or reset only Avatar Builder's temporal layer. Those separate resets let us determine which layer produces the apparent five-to-ten-minute improvement.

`MediaPipeConvergenceAuditorSelfTest` verifies report persistence. `MediaPipeFaceCanonicalizerSelfTest` verifies the rigid-motion normalization math.
