# Recording Module

Namespace: `AvatarBuilder.Modules.Recording`

This module owns writing event video clips. It should accept already-rendered JPEG frames and output paths from the app shell, then manage encoder lifecycle.

Files:

- `FfmpegEventRecorderService.cs`: starts bundled FFmpeg, queues pre-roll/current event frames, writes MJPEG image-pipe input, finalizes H.264 MP4 clips, and reports encoder status.

Change this module when event video encoding, pre-roll handling, FFmpeg recording arguments, or recorder shutdown behavior needs work.
