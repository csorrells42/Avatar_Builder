# Episodes Module

Namespace: `AvatarBuilder.Modules.Episodes`

This module owns event data meant for later review. It should describe what happened and what evidence was collected, not how frames were captured or landmarks were detected.

Files:

- `AvatarBuilderEvent.cs`: event-grid row model with start/end labels, duration formatting, event type, notes, and file summary.
- `LandmarkEventAggregate.cs`: accumulates event-level landmark extrema and counts, such as minimum eye opening, maximum mouth opening, strongest trend score, quality ranges, capture-quality rollups, source backends, reconstruction/artifact counts, and MediaPipe correction counts/max magnitudes.
- `LandmarkEventTimeline.cs`: writes frame-by-frame landmark evidence to `landmark_timeline.json` and `landmark_timeline.csv`, including raw/stabilized aperture measurements, capture-quality verdicts, and MediaPipe correction fields.

Change this module when event summaries, event-grid fields, timeline CSV/JSON columns, or clinician-review evidence fields need work.
