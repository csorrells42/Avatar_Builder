# Infrastructure Module

Namespace: `AvatarBuilder.Modules.Infrastructure`

This module owns tiny host/runtime helpers shared by multiple modules. Keep it small; domain-specific helpers should live with their domain.

Files:

- `AtomicTextFileWriter.cs`: writes JSON/HTML text files through a temp file and atomic replace so browser refreshes never see partial live reports.
- `FfmpegLocator.cs`: locates bundled FFmpeg under `AppContext.BaseDirectory/dependencies`, preferring `dependencies/ffmpeg/win-x64/ffmpeg.exe` and falling back to a dependency-tree search.

Change this module when dependency lookup rules or shared host-level utilities need work.
