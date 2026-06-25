using System.IO;

namespace EpisodeMonitor.Video;

public static class FfmpegLocator
{
    public static string? FindFfmpeg()
    {
        var dependencyRoot = Path.Combine(AppContext.BaseDirectory, "dependencies");
        var candidate = Path.Combine(dependencyRoot, "ffmpeg", "win-x64", "ffmpeg.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        if (!Directory.Exists(dependencyRoot))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(dependencyRoot, "ffmpeg.exe", SearchOption.AllDirectories)
                .OrderBy(path => path.Length)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }
}

