using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarModelHistoryReport
{
	public string SchemaVersion { get; init; } = "avatar-model-history-report-v3-mapped-identity";

	public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

	public string HistoryFileName { get; init; } = "avatar_model_history.jsonl";

	public string RetentionPolicy { get; init; } = "Keeps up to 30 days or 86,400 rebuild records. Recent-page data is bounded to the latest 240 rebuilds.";

	public string MeasurementPolicy { get; init; } = "Geometry movement is RMS displacement in the current recurrent FLAME identity, expressed as a percentage of face span. Landmark RMSE measures projection agreement with the exact-frame MediaPipe observations. Outlier candidates are review flags and are not silently deleted.";

	public AvatarModelHistoryEntry Latest { get; init; } = new AvatarModelHistoryEntry();

	public List<AvatarModelHistoryEntry> RecentEntries { get; init; } = new List<AvatarModelHistoryEntry>();
}
