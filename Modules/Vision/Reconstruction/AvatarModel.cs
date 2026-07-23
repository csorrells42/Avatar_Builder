using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarModel
{
	public const string CurrentSchemaVersion = "avatar-model-v9-multiframe-identity-mapping";

	public string SchemaVersion { get; init; } = "avatar-model-v9-multiframe-identity-mapping";

	public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

	public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;

	public string SubjectId { get; init; } = "";

	public string SubjectDisplayName { get; init; } = "";

	public string Status { get; init; } = "waiting for reconstruction observations";

	public string StoragePolicy { get; init; } = "SQLite indexes ranked observations. Every retained observation has a checksummed binary reconstruction scan and its paired high-quality camera image.";

	public long SourceObservationRevision { get; init; }

	public AvatarIdentityModel Identity { get; init; } = new AvatarIdentityModel();

	public AvatarExpressionModel Expression { get; init; } = new AvatarExpressionModel();

	public AvatarPoseCoverage PoseCoverage { get; init; } = new AvatarPoseCoverage();

	public AvatarModelConvergence Convergence { get; init; } = new AvatarModelConvergence();

	public List<AvatarModelSampleSummary> RecentSamples { get; init; } = new List<AvatarModelSampleSummary>();

	public List<string> Findings { get; set; } = new List<string>();
}
