# Episode Monitor

Standalone WPF camera-based low-motion episode monitor.

This app records observable sustained low-motion events, optional start/end snapshots, manual markers, and CSV exports for review. It is a data-gathering tool only and does not diagnose a medical condition.

## Runtime Dependency

FFmpeg is bundled under:

```text
dependencies/ffmpeg/win-x64/ffmpeg.exe
```

The app resolves FFmpeg relative to the executable folder and the project copies `dependencies` beside the exe during build and publish.

## Build

```powershell
dotnet build .\EpisodeMonitor.csproj --no-restore
```

