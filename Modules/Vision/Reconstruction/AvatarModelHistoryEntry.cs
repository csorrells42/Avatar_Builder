using System;
using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarModelHistoryEntry
{
	public string SchemaVersion { get; init; } = "avatar-model-history-v3-mapped-identity";

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

	public double ShapeCoefficientRelativeRmsPercent { get; init; }

	public double IdentityMappingLandmarkRmsePercent { get; init; }

	public double IdentityMappingImprovementPercent { get; init; }

	public double GenericIdentityDisplacementPercent { get; init; }

	public string IdentityMappingStatus { get; init; } = "waiting";

	public double MeanExpressionRange { get; init; }

	public double MeanExpressionRangeDelta { get; init; }

	public int WarningBearingObservationCount { get; init; }

	public int DownweightedIdentityObservationCount { get; init; }

	public int ExcludedIdentityObservationCount { get; init; }

	public int GeometryOutlierCandidateCount { get; init; }

	public double HighestObservationRmsFaceSpanPercent { get; init; }

	public List<AvatarModelRegionMovement> RegionMovement { get; init; } = new List<AvatarModelRegionMovement>();

	public List<AvatarModelRegionConfidenceDelta> RegionConfidence { get; init; } = new List<AvatarModelRegionConfidenceDelta>();

	public List<string> Warnings { get; init; } = new List<string>();
}
