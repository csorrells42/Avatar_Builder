namespace EpisodeMonitor.Video;

public sealed class EpisodeMonitorEvent
{
    public DateTime StartedAt { get; init; } = DateTime.Now;

    public DateTime? EndedAt { get; init; }

    public string StartLabel => StartedAt.ToString("g");

    public string EndLabel => EndedAt?.ToString("g") ?? "";

    public string Duration
    {
        get
        {
            var duration = (EndedAt ?? DateTime.Now) - StartedAt;
            if (duration.TotalHours >= 1d)
            {
                return $"{duration.TotalHours:0.0} hr";
            }

            if (duration.TotalMinutes >= 1d)
            {
                return $"{duration.TotalMinutes:0.0} min";
            }

            return $"{duration.TotalSeconds:0}s";
        }
    }

    public string Event { get; init; } = "";

    public string AvgMotion { get; init; } = "";

    public string Notes { get; init; } = "";

    public string File { get; init; } = "";
}

