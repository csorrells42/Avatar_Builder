namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarModelHistoryEntry
{
    public string SchemaVersion { get; init; } = "avatar-model-history-v1";

    public long RebuildNumber { get; init; }

    public DateTime EvaluatedAtUtc { get; init; } = DateTime.UtcNow;

    public string SubjectId { get; init; } = "";

    public string SubjectDisplayName { get; init; } = "";

    public string Status { get; init; } = "waiting";

    public string Summary { get; init; } = "Waiting for a model rebuild.";

    public int SampleCount { get; init; }

    public int SampleCountDelta { get; init; }

    public int NewObservationCount { get; init; }

    public DateTime LatestObservationCapturedAtUtc { get; init; }

    public double IdentityConfidencePercent { get; init; }

    public double IdentityConfidenceDeltaPoints { get; init; }

    public double PoseCoveragePercent { get; init; }

    public double PoseCoverageDeltaPoints { get; init; }

    public double ShapeStabilityPercent { get; init; }

    public double ShapeStabilityDeltaPoints { get; init; }

    public int DenseVertexCount { get; init; }

    public double OverallVertexRmsFaceSpanPercent { get; init; }

    public double ShapeCoefficientRmsDelta { get; init; }

    public double MeanExpressionRange { get; init; }

    public double MeanExpressionRangeDelta { get; init; }

    public int WarningBearingObservationCount { get; init; }

    public int DownweightedIdentityObservationCount { get; init; }

    public int ExcludedIdentityObservationCount { get; init; }

    public int GeometryOutlierCandidateCount { get; init; }

    public double HighestObservationRmsFaceSpanPercent { get; init; }

    public List<AvatarModelRegionMovement> RegionMovement { get; init; } = [];

    public List<AvatarModelRegionConfidenceDelta> RegionConfidence { get; init; } = [];

    public List<string> Warnings { get; init; } = [];
}

public sealed class AvatarModelRegionMovement
{
    public string Region { get; init; } = "";

    public int MatchedVertexCount { get; init; }

    public double RmsFaceSpanPercent { get; init; }
}

public sealed class AvatarModelRegionConfidenceDelta
{
    public string Region { get; init; } = "";

    public double ConfidencePercent { get; init; }

    public double DeltaPoints { get; init; }
}

public sealed class AvatarModelHistoryReport
{
    public string SchemaVersion { get; init; } = "avatar-model-history-report-v1";

    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

    public string HistoryFileName { get; init; } = AvatarModelHistoryStore.JsonLinesFileName;

    public string RetentionPolicy { get; init; } =
        "Keeps up to 30 days or 86,400 rebuild records. Recent-page data is bounded to the latest 240 rebuilds.";

    public string MeasurementPolicy { get; init; } =
        "Geometry movement is RMS displacement in pose-neutral 3DDFA space, expressed as a percentage of the current face span. Outlier candidates are review flags and are not silently deleted.";

    public AvatarModelHistoryEntry Latest { get; init; } = new();

    public List<AvatarModelHistoryEntry> RecentEntries { get; init; } = [];
}
