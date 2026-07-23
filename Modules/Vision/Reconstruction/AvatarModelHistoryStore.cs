using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;
using AvatarBuilder.Modules.Storage.AvatarObservations;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarModelHistoryStore
{
	private sealed class RmsAccumulator
	{
		private double _sumSquared;

		public int Count { get; private set; }

		public double Rms
		{
			get
			{
				if (Count != 0)
				{
					return Math.Sqrt(_sumSquared / (double)Count);
				}
				return 0.0;
			}
		}

		public void Add(double squaredDistance)
		{
			if (double.IsFinite(squaredDistance))
			{
				_sumSquared += squaredDistance;
				Count++;
			}
		}
	}

	private sealed record ObservationGeometryAudit(int Count, double HighestRmsPercent)
	{
		public static ObservationGeometryAudit Empty { get; } = new ObservationGeometryAudit(0, 0.0);
	}

	public const string JsonLinesFileName = "avatar_model_history.jsonl";

	public const string LatestJsonFileName = "avatar_model_history_latest.json";

	public const string RecentJsonFileName = "avatar_model_history_recent.json";

	public const string HtmlFileName = "avatar_model_regression.html";

	private const int RecentEntryCount = 240;

	private const int MaxHistoryEntryCount = 86400;

	private const int RebuildsPerCompactionCheck = 2880;

	private const double MatureModelSampleCount = 8.0;

	private const double ModelMovementWarningPercent = 2.0;

	private const double RegionMovementWarningPercent = 3.0;

	private const double SingleSampleMovementWarningPercent = 1.5;

	private const double ObservationOutlierWarningPercent = 10.0;

	private const double ShapeCoefficientMovementWarningPercent = 5.0;

	private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
	{
		WriteIndented = true
	};

	private static readonly JsonSerializerOptions JsonLineOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

	public AvatarModelHistoryReport RecordAndWrite(string folder, AvatarObservationDataset observationSet, AvatarObservationRepository repository, AvatarModel currentModel, AvatarModel? previousModel, IReadOnlyList<AvatarObservation>? observationsToAudit = null)
	{
		Directory.CreateDirectory(folder);
		AvatarModelHistoryEntry previousEntry = ReadLatest(GetLatestJsonPath(folder));
		AvatarModelHistoryEntry avatarModelHistoryEntry = BuildEntry(observationSet, repository, currentModel, previousModel, previousEntry, observationsToAudit);
		AppendHistoryEntry(GetJsonLinesPath(folder), avatarModelHistoryEntry);
		AtomicTextFileWriter.WriteAllText(GetLatestJsonPath(folder), JsonSerializer.Serialize(avatarModelHistoryEntry, JsonOptions), Encoding.UTF8);
		List<AvatarModelHistoryEntry> list = ReadRecent(GetRecentJsonPath(folder));
		list.Add(avatarModelHistoryEntry);
		list = list.OrderBy((AvatarModelHistoryEntry item) => item.RebuildNumber).TakeLast(240).ToList();
		AtomicTextFileWriter.WriteAllText(GetRecentJsonPath(folder), JsonSerializer.Serialize(list, JsonOptions), Encoding.UTF8);
		AvatarModelHistoryReport avatarModelHistoryReport = new AvatarModelHistoryReport
		{
			CreatedAtUtc = DateTime.UtcNow,
			Latest = avatarModelHistoryEntry,
			RecentEntries = list
		};
		AtomicTextFileWriter.WriteAllText(GetHtmlPath(folder), BuildHtml(avatarModelHistoryReport), Encoding.UTF8);
		if (avatarModelHistoryEntry.RebuildNumber > 0 && avatarModelHistoryEntry.RebuildNumber % 2880 == 0L)
		{
			CompactHistory(GetJsonLinesPath(folder), avatarModelHistoryEntry.EvaluatedAtUtc);
		}
		return avatarModelHistoryReport;
	}

	public AvatarModelHistoryReport ReadReport(string folder)
	{
		return new AvatarModelHistoryReport
		{
			CreatedAtUtc = DateTime.UtcNow,
			Latest = (ReadLatest(GetLatestJsonPath(folder)) ?? new AvatarModelHistoryEntry()),
			RecentEntries = ReadRecent(GetRecentJsonPath(folder))
		};
	}

	public static string GetJsonLinesPath(string folder)
	{
		return Path.Combine(folder, "avatar_model_history.jsonl");
	}

	public static string GetLatestJsonPath(string folder)
	{
		return Path.Combine(folder, "avatar_model_history_latest.json");
	}

	public static string GetRecentJsonPath(string folder)
	{
		return Path.Combine(folder, "avatar_model_history_recent.json");
	}

	public static string GetHtmlPath(string folder)
	{
		return Path.Combine(folder, "avatar_model_regression.html");
	}

	private static AvatarModelHistoryEntry BuildEntry(AvatarObservationDataset observationSet, AvatarObservationRepository repository, AvatarModel current, AvatarModel? previous, AvatarModelHistoryEntry? previousEntry, IReadOnlyList<AvatarObservation>? observationsToAudit)
	{
		int num = ((previous == null) ? current.Identity.SampleCount : (current.Identity.SampleCount - previous.Identity.SampleCount));
		DateTime latestPreviousObservationUtc = previousEntry?.LatestObservationCapturedAtUtc ?? DateTime.MinValue;
		int num2 = ((previousEntry == null) ? observationSet.Observations.Count : observationSet.Observations.Count((AvatarObservation observation) => observation.CapturedAtUtc > latestPreviousObservationUtc));
		DateTime latestObservationCapturedAtUtc = ((observationSet.Observations.Count == 0) ? latestPreviousObservationUtc : observationSet.Observations.Max((AvatarObservation observation) => observation.CapturedAtUtc));
		double num3 = Delta(current.Identity.ConfidencePercent, previous?.Identity.ConfidencePercent);
		double num4 = Delta(current.PoseCoverage.CoveragePercent, previous?.PoseCoverage.CoveragePercent);
		double num5 = Delta(current.Identity.ShapeCoefficientStabilityPercent, previous?.Identity.ShapeCoefficientStabilityPercent);
		IReadOnlyList<FaceMeshLandmarkPoint> readOnlyList = PreferredIdentityVertices(current.Identity);
		IReadOnlyList<FaceMeshLandmarkPoint> readOnlyList2;
		if (previous != null)
		{
			readOnlyList2 = PreferredIdentityVertices(previous.Identity);
		}
		else
		{
			IReadOnlyList<FaceMeshLandmarkPoint> readOnlyList3 = Array.Empty<FaceMeshLandmarkPoint>();
			readOnlyList2 = readOnlyList3;
		}
		IReadOnlyList<FaceMeshLandmarkPoint> previous2 = readOnlyList2;
		IReadOnlyList<double> current2 = PreferredShapeCoefficients(current.Identity);
		IReadOnlyList<double> readOnlyList4;
		if (previous != null)
		{
			readOnlyList4 = PreferredShapeCoefficients(previous.Identity);
		}
		else
		{
			IReadOnlyList<double> readOnlyList5 = Array.Empty<double>();
			readOnlyList4 = readOnlyList5;
		}
		IReadOnlyList<double> previous3 = readOnlyList4;
		double num6 = ((previous == null) ? 0.0 : CalculateVertexRmsPercent(readOnlyList, previous2));
		List<AvatarModelRegionMovement> regionMovement = ((previous == null) ? CreateEmptyRegionMovement() : CalculateRegionMovement(readOnlyList, previous2));
		double num7 = ((previous == null) ? 0.0 : CalculateCoefficientRelativeRmsPercent(current2, previous3));
		double num8 = MeanExpressionRange(current);
		double value = ((previous == null) ? 0.0 : (num8 - MeanExpressionRange(previous)));
		List<AvatarModelRegionConfidenceDelta> regionConfidence = CalculateRegionConfidenceDeltas(current, previous);
		int warningBearingObservationCount = observationSet.Observations.Count((AvatarObservation observation) => observation.Warnings.Count > 0);
		int downweightedIdentityObservationCount = observationSet.Observations.Count((AvatarObservation observation) => observation.ExpressionWeightPercent - observation.IdentityWeightPercent > 0.1);
		int excludedIdentityObservationCount = observationSet.Observations.Count((AvatarObservation observation) => observation.IdentityWeightPercent <= 0.001);
		ObservationGeometryAudit observationGeometryAudit = ((!((double)current.Identity.SampleCount >= 8.0)) ? ObservationGeometryAudit.Empty : ((observationsToAudit == null) ? AuditObservationGeometry(observationSet, repository, readOnlyList) : AuditObservationGeometry(observationsToAudit, readOnlyList)));
		List<string> list = BuildWarnings(current, previous, num2, num3, num4, num5, num6, regionMovement, num7, observationGeometryAudit);
		string status = ((previous == null) ? "baseline recorded" : ((list.Count > 0) ? "review recommended" : ((num2 > 0 && (num4 > 0.01 || num3 > 0.25 || num5 > 0.25)) ? "improving" : ((num2 > 0) ? "learning within tolerance" : "stable"))));
		string summary = BuildSummary(status, num, num2, num6, list.Count);
		return new AvatarModelHistoryEntry
		{
			RebuildNumber = Math.Max(1L, (previousEntry?.RebuildNumber ?? 0) + 1),
			EvaluatedAtUtc = DateTime.UtcNow,
			SubjectId = current.SubjectId,
			SubjectDisplayName = current.SubjectDisplayName,
			Status = status,
			Summary = summary,
			SampleCount = current.Identity.SampleCount,
			SampleCountDelta = num,
			NewObservationCount = num2,
			LatestObservationCapturedAtUtc = latestObservationCapturedAtUtc,
			IdentityConfidencePercent = Round(current.Identity.ConfidencePercent),
			IdentityConfidenceDeltaPoints = Round(num3),
			PoseCoveragePercent = Round(current.PoseCoverage.CoveragePercent),
			PoseCoverageDeltaPoints = Round(num4),
			ShapeStabilityPercent = Round(current.Identity.ShapeCoefficientStabilityPercent),
			ShapeStabilityDeltaPoints = Round(num5),
			DenseVertexCount = current.Identity.DenseVertexCount,
			OverallVertexRmsFaceSpanPercent = Round(num6),
			ShapeCoefficientRelativeRmsPercent = Round(num7),
			IdentityMappingLandmarkRmsePercent = Round(current.Identity.MappingFinalLandmarkRmsePercent),
			IdentityMappingImprovementPercent = Round(current.Identity.MappingImprovementPercent),
			GenericIdentityDisplacementPercent = Round(current.Identity.GenericIdentityDisplacementPercent),
			IdentityMappingStatus = current.Identity.MappingStatus,
			MeanExpressionRange = Round(num8),
			MeanExpressionRangeDelta = Round(value),
			WarningBearingObservationCount = warningBearingObservationCount,
			DownweightedIdentityObservationCount = downweightedIdentityObservationCount,
			ExcludedIdentityObservationCount = excludedIdentityObservationCount,
			GeometryOutlierCandidateCount = observationGeometryAudit.Count,
			HighestObservationRmsFaceSpanPercent = Round(observationGeometryAudit.HighestRmsPercent),
			RegionMovement = regionMovement,
			RegionConfidence = regionConfidence,
			Warnings = list
		};
	}

	private static List<string> BuildWarnings(AvatarModel current, AvatarModel? previous, int newObservationCount, double confidenceDelta, double coverageDelta, double stabilityDelta, double overallMovement, IReadOnlyList<AvatarModelRegionMovement> regionMovement, double shapeCoefficientDelta, ObservationGeometryAudit outlierAudit)
	{
		if (previous == null)
		{
			return new List<string>();
		}
		List<string> list = new List<string>();
		if (confidenceDelta <= -3.0)
		{
			list.Add($"Identity confidence fell {Math.Abs(confidenceDelta):0.#} percentage points in one rebuild.");
		}
		if (coverageDelta < -0.01)
		{
			list.Add($"Pose/depth coverage fell {Math.Abs(coverageDelta):0.#} percentage points; inspect retention-window rollover.");
		}
		if (stabilityDelta <= -5.0)
		{
			list.Add($"Shape-coefficient stability fell {Math.Abs(stabilityDelta):0.#} percentage points.");
		}
		bool flag = (double)current.Identity.SampleCount >= 8.0;
		if (flag && overallMovement > 2.0)
		{
			list.Add($"The canonical mean face moved {overallMovement:0.###}% of face span in one rebuild.");
		}
		foreach (AvatarModelRegionMovement item in regionMovement.Where((AvatarModelRegionMovement region) => region.RmsFaceSpanPercent > 3.0))
		{
			list.Add($"{item.Region} moved {item.RmsFaceSpanPercent:0.###}% of face span; review the newest reconstruction.");
		}
		if (flag && newObservationCount == 1 && overallMovement > 1.5)
		{
			list.Add("One new observation moved the mature mean face more than the single-sample tolerance.");
		}
		if (newObservationCount == 0 && overallMovement > 0.05)
		{
			list.Add("The model geometry changed without a new stored observation; this indicates a nondeterministic rebuild or data mismatch.");
		}
		if (shapeCoefficientDelta > 5.0 && newObservationCount <= 1)
		{
			list.Add($"Shape coefficients shifted {shapeCoefficientDelta:0.###}% relative RMS in one rebuild.");
		}
		if (outlierAudit.Count > 0)
		{
			list.Add($"{outlierAudit.Count} stored observation(s) differ from the current mean by more than {10.0:0.#}% of face span; these are review candidates, not automatic deletions.");
		}
		return list;
	}

	private static string BuildSummary(string status, int sampleDelta, int newObservationCount, double movement, int warningCount)
	{
		if (status == "baseline recorded")
		{
			return "First auditable model baseline recorded; future rebuilds will be compared against it.";
		}
		string text = ((newObservationCount <= 0) ? "no new observations" : $"{newObservationCount} new observation(s)");
		string value = text;
		string value2 = ((sampleDelta == 0) ? "retained count unchanged" : ("retained count " + Signed(sampleDelta)));
		string value3 = ((warningCount == 0) ? "no regression warnings" : $"{warningCount} review warning(s)");
		return $"{status}: {value}, {value2}; mean-face movement {movement:0.###}% of face span; {value3}.";
	}

	private static ObservationGeometryAudit AuditObservationGeometry(AvatarObservationDataset dataset, AvatarObservationRepository repository, IReadOnlyList<FaceMeshLandmarkPoint> meanVertices)
	{
		if (meanVertices.Count == 0)
		{
			return ObservationGeometryAudit.Empty;
		}
		int num = 0;
		double num2 = 0.0;
		foreach (AvatarObservation observation in dataset.Observations)
		{
			if (!(observation.IdentityWeightPercent <= 0.001) && (observation.CanonicalVertexCount != 0 || observation.DenseVertexCount != 0))
			{
				double num3 = CalculateVertexRmsPercent(AvatarModelBuilder.NormalizeIdentityVerticesForAudit(repository.LoadObservation(dataset, observation)), meanVertices);
				num2 = Math.Max(num2, num3);
				if (num3 > 10.0)
				{
					num++;
				}
			}
		}
		return new ObservationGeometryAudit(num, num2);
	}

	private static ObservationGeometryAudit AuditObservationGeometry(IReadOnlyList<AvatarObservation> observations, IReadOnlyList<FaceMeshLandmarkPoint> meanVertices)
	{
		if (meanVertices.Count == 0)
		{
			return ObservationGeometryAudit.Empty;
		}
		int num = 0;
		double num2 = 0.0;
		foreach (AvatarObservation observation in observations)
		{
			if (!(observation.IdentityWeightPercent <= 0.001) && observation.CanonicalIdentityVertices.Count != 0)
			{
				double num3 = CalculateVertexRmsPercent(AvatarModelBuilder.NormalizeIdentityVerticesForAudit(observation), meanVertices);
				num2 = Math.Max(num2, num3);
				if (num3 > 10.0)
				{
					num++;
				}
			}
		}
		return new ObservationGeometryAudit(num, num2);
	}

	private static List<AvatarModelRegionMovement> CalculateRegionMovement(IReadOnlyList<FaceMeshLandmarkPoint> current, IReadOnlyList<FaceMeshLandmarkPoint> previous)
	{
		if (current.Count == 0 || previous.Count == 0)
		{
			return CreateEmptyRegionMovement();
		}
		Dictionary<int, FaceMeshLandmarkPoint> dictionary = previous.ToDictionary((FaceMeshLandmarkPoint point) => point.Index);
		double num = current.Min((FaceMeshLandmarkPoint point) => point.X);
		double num2 = current.Max((FaceMeshLandmarkPoint point) => point.X);
		double num3 = current.Min((FaceMeshLandmarkPoint point) => point.Y);
		double num4 = current.Max((FaceMeshLandmarkPoint point) => point.Y);
		double num5 = Math.Max(0.0001, num2 - num);
		double num6 = Math.Max(0.0001, num4 - num3);
		double num7 = (num + num2) * 0.5;
		Dictionary<string, RmsAccumulator> dictionary2 = new Dictionary<string, RmsAccumulator>(StringComparer.Ordinal)
		{
			["Eyes"] = new RmsAccumulator(),
			["Nose"] = new RmsAccumulator(),
			["Mouth"] = new RmsAccumulator(),
			["Chin and jaw"] = new RmsAccumulator()
		};
		foreach (FaceMeshLandmarkPoint item in current)
		{
			if (dictionary.TryGetValue(item.Index, out var value))
			{
				double num8 = (item.Y - num3) / num6;
				double num9 = Math.Abs(item.X - num7) / num5;
				double squaredDistance = SquaredDistance(item, value);
				if (num8 >= 0.25 && num8 <= 0.52)
				{
					dictionary2["Eyes"].Add(squaredDistance);
				}
				if (num8 >= 0.34 && num8 <= 0.69 && num9 <= 0.22)
				{
					dictionary2["Nose"].Add(squaredDistance);
				}
				if (num8 >= 0.58 && num8 <= 0.82 && num9 <= 0.34)
				{
					dictionary2["Mouth"].Add(squaredDistance);
				}
				if (num8 >= 0.72)
				{
					dictionary2["Chin and jaw"].Add(squaredDistance);
				}
			}
		}
		return dictionary2.Select((KeyValuePair<string, RmsAccumulator> pair) => new AvatarModelRegionMovement
		{
			Region = pair.Key,
			MatchedVertexCount = pair.Value.Count,
			RmsFaceSpanPercent = Round(pair.Value.Rms * 100.0)
		}).ToList();
	}

	private static List<AvatarModelRegionMovement> CreateEmptyRegionMovement()
	{
		int num = 4;
		List<AvatarModelRegionMovement> list = new List<AvatarModelRegionMovement>(num);
		CollectionsMarshal.SetCount(list, num);
		Span<AvatarModelRegionMovement> span = CollectionsMarshal.AsSpan(list);
		span[0] = new AvatarModelRegionMovement
		{
			Region = "Eyes"
		};
		span[1] = new AvatarModelRegionMovement
		{
			Region = "Nose"
		};
		span[2] = new AvatarModelRegionMovement
		{
			Region = "Mouth"
		};
		span[3] = new AvatarModelRegionMovement
		{
			Region = "Chin and jaw"
		};
		return list;
	}

	private static List<AvatarModelRegionConfidenceDelta> CalculateRegionConfidenceDeltas(AvatarModel current, AvatarModel? previous)
	{
		Dictionary<string, double> previousByRegion = previous?.Identity.RegionConfidence.ToDictionary<AvatarRegionConfidence, string, double>((AvatarRegionConfidence region) => region.Region, (AvatarRegionConfidence region) => region.ConfidencePercent, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
		return current.Identity.RegionConfidence.Select((AvatarRegionConfidence region) => new AvatarModelRegionConfidenceDelta
		{
			Region = region.Region,
			ConfidencePercent = Round(region.ConfidencePercent),
			DeltaPoints = (previousByRegion.TryGetValue(region.Region, out var value) ? Round(region.ConfidencePercent - value) : 0.0)
		}).ToList();
	}

	private static IReadOnlyList<FaceMeshLandmarkPoint> PreferredIdentityVertices(AvatarIdentityModel identity)
	{
		if (identity.MappedDenseVertices.Count <= 0)
		{
			return identity.MeanDenseVertices;
		}
		return identity.MappedDenseVertices;
	}

	private static IReadOnlyList<double> PreferredShapeCoefficients(AvatarIdentityModel identity)
	{
		if (identity.MappedShapeCoefficients.Count <= 0)
		{
			return identity.MeanShapeCoefficients;
		}
		return identity.MappedShapeCoefficients;
	}

	private static double CalculateVertexRmsPercent(IReadOnlyList<FaceMeshLandmarkPoint> current, IReadOnlyList<FaceMeshLandmarkPoint> previous)
	{
		if (current.Count == 0 || previous.Count == 0)
		{
			return 0.0;
		}
		Dictionary<int, FaceMeshLandmarkPoint> dictionary = previous.ToDictionary((FaceMeshLandmarkPoint point) => point.Index);
		RmsAccumulator rmsAccumulator = new RmsAccumulator();
		foreach (FaceMeshLandmarkPoint item in current)
		{
			if (dictionary.TryGetValue(item.Index, out var value))
			{
				rmsAccumulator.Add(SquaredDistance(item, value));
			}
		}
		return rmsAccumulator.Rms * 100.0;
	}

	private static double SquaredDistance(FaceMeshLandmarkPoint current, FaceMeshLandmarkPoint previous)
	{
		double num = current.X - previous.X;
		double num2 = current.Y - previous.Y;
		double num3 = current.Z - previous.Z;
		return num * num + num2 * num2 + num3 * num3;
	}

	private static double CalculateCoefficientRelativeRmsPercent(IReadOnlyList<double> current, IReadOnlyList<double> previous)
	{
		int num = Math.Min(current.Count, previous.Count);
		if (num == 0)
		{
			return 0.0;
		}
		double num2 = 0.0;
		double num3 = 0.0;
		double num4 = 0.0;
		for (int i = 0; i < num; i++)
		{
			double num5 = current[i] - previous[i];
			num2 += num5 * num5;
			num3 += current[i] * current[i];
			num4 += previous[i] * previous[i];
		}
		double num6 = Math.Max(Math.Sqrt(num3 / (double)num), Math.Sqrt(num4 / (double)num));
		if (!(num6 <= double.Epsilon))
		{
			return Math.Sqrt(num2 / (double)num) / num6 * 100.0;
		}
		return 0.0;
	}

	private static double MeanExpressionRange(AvatarModel model)
	{
		if (model.Expression.ExpressionRanges.Count != 0)
		{
			return model.Expression.ExpressionRanges.Average((AvatarCoefficientRange range) => range.Range);
		}
		return 0.0;
	}

	private static double Delta(double current, double? previous)
	{
		if (previous.HasValue)
		{
			return current - previous.Value;
		}
		return 0.0;
	}

	private static void AppendHistoryEntry(string path, AvatarModelHistoryEntry entry)
	{
		File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonLineOptions) + Environment.NewLine, Utf8WithoutBom);
	}

	private static void CompactHistory(string path, DateTime utcNow)
	{
		if (File.Exists(path))
		{
			DateTime cutoff = utcNow - TimeSpan.FromDays(30);
			List<string> list = (from entry in (from entry in File.ReadLines(path).Select(TryDeserializeEntry)
					where entry != null && entry.EvaluatedAtUtc >= cutoff
					select (entry)).TakeLast(86400)
				select JsonSerializer.Serialize(entry, JsonLineOptions)).ToList();
			string contents = ((list.Count == 0) ? "" : (string.Join(Environment.NewLine, list) + Environment.NewLine));
			AtomicTextFileWriter.WriteAllText(path, contents, Utf8WithoutBom);
		}
	}

	private static AvatarModelHistoryEntry? ReadLatest(string path)
	{
		try
		{
			return File.Exists(path) ? JsonSerializer.Deserialize<AvatarModelHistoryEntry>(File.ReadAllText(path), JsonOptions) : null;
		}
		catch
		{
			return null;
		}
	}

	private static List<AvatarModelHistoryEntry> ReadRecent(string path)
	{
		try
		{
			return File.Exists(path) ? (JsonSerializer.Deserialize<List<AvatarModelHistoryEntry>>(File.ReadAllText(path), JsonOptions) ?? new List<AvatarModelHistoryEntry>()) : new List<AvatarModelHistoryEntry>();
		}
		catch
		{
			return new List<AvatarModelHistoryEntry>();
		}
	}

	private static AvatarModelHistoryEntry? TryDeserializeEntry(string line)
	{
		try
		{
			return JsonSerializer.Deserialize<AvatarModelHistoryEntry>(line.TrimStart('\ufeff'), JsonLineOptions);
		}
		catch
		{
			return null;
		}
	}

	private static string BuildHtml(AvatarModelHistoryReport report)
	{
		AvatarModelHistoryEntry latest = report.Latest;
		string text;
		if (latest.Status == "review recommended")
		{
			text = "bad";
		}
		else
		{
			string status = latest.Status;
			bool flag = ((status == "improving" || status == "learning within tolerance") ? true : false);
			text = (flag ? "good" : "muted");
		}
		string value = text;
		string value2 = ((latest.Warnings.Count == 0) ? "<li class=\"good\">No current regression warnings.</li>" : string.Concat(latest.Warnings.Select((string warning) => "<li class=\"bad\">" + H(warning) + "</li>")));
		string value3 = string.Concat(latest.RegionMovement.Select((AvatarModelRegionMovement region) => $"<tr><td>{H(region.Region)}</td><td>{region.MatchedVertexCount:n0}</td><td>{region.RmsFaceSpanPercent:0.###}%</td></tr>"));
		string value4 = ((latest.RegionConfidence.Count == 0) ? "<tr><td colspan=\"3\" class=\"muted\">Waiting for region confidence.</td></tr>" : string.Concat(latest.RegionConfidence.Select((AvatarModelRegionConfidenceDelta region) => $"<tr><td>{H(region.Region)}</td><td>{region.ConfidencePercent:0.#}%</td><td>{Signed(region.DeltaPoints)} pp</td></tr>")));
		string value5 = string.Concat(from entry in report.RecentEntries.OrderByDescending((AvatarModelHistoryEntry entry) => entry.RebuildNumber).Take(36)
			select $"<tr><td>{entry.RebuildNumber}</td><td>{H(entry.EvaluatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}</td><td>{H(entry.Status)}</td><td>{entry.SampleCount} retained / {entry.NewObservationCount} new</td><td>{entry.IdentityConfidencePercent:0.#}% ({Signed(entry.IdentityConfidenceDeltaPoints)} pp)</td><td>{entry.PoseCoveragePercent:0.#}% ({Signed(entry.PoseCoverageDeltaPoints)} pp)</td><td>{entry.IdentityMappingLandmarkRmsePercent:0.###}%</td><td>{entry.OverallVertexRmsFaceSpanPercent:0.###}%</td><td>{entry.Warnings.Count}</td></tr>");
		string value6 = JsonSerializer.Serialize(report.RecentEntries, JsonLineOptions);
		return $"<!doctype html>\r\n<html lang=\"en\">\r\n<head>\r\n<meta charset=\"utf-8\">\r\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\r\n<meta http-equiv=\"refresh\" content=\"30\">\r\n<title>Avatar Model Regression Audit</title>\r\n<style>\r\n:root{{color-scheme:dark;--bg:#050b10;--panel:#0b141c;--line:#28435b;--text:#e7f6ff;--muted:#9db7c9;--good:#80e0a4;--warn:#ffd27a;--bad:#ff9a9a;--cyan:#66d9ff}}\r\n*{{box-sizing:border-box}}body{{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 Segoe UI,Arial,sans-serif}}main{{max-width:1320px;margin:0 auto;padding:20px}}.panel{{border:1px solid var(--line);background:var(--panel);border-radius:6px;padding:14px;margin:14px 0}}.grid{{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:8px}}.metric{{background:#07121c;border:1px solid #1d2c38;padding:10px}}.label{{color:var(--muted);font-size:12px;text-transform:uppercase}}.value{{font-size:18px;font-weight:700}}.good{{color:var(--good)}}.warn{{color:var(--warn)}}.bad{{color:var(--bad)}}.muted{{color:var(--muted)}}h1{{margin:0 0 4px;font-size:24px}}h2{{font-size:17px;margin:0 0 10px}}canvas{{width:100%;height:280px;display:block;background:#061019;border:1px solid #193149}}table{{width:100%;border-collapse:collapse}}td,th{{border-bottom:1px solid #1c3042;padding:7px 5px;text-align:left;vertical-align:top}}th{{color:var(--muted);font-weight:600}}.split{{display:grid;grid-template-columns:1fr 1fr;gap:14px}}.scroll{{overflow:auto}}code{{color:#b9d7ef}}@media(max-width:900px){{.split{{grid-template-columns:1fr}}}}\r\n</style>\r\n</head>\r\n<body>\r\n<main>\r\n  <h1>Avatar Model Regression Audit</h1>\r\n  <p class=\"muted\">Auto-refreshes every 30 seconds. Rebuild {latest.RebuildNumber}, evaluated {H(latest.EvaluatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}.</p>\r\n  <section class=\"panel\">\r\n    <h2 class=\"{value}\">{H(latest.Status)}</h2>\r\n    <p>{H(latest.Summary)}</p>\r\n    <div class=\"grid\">\r\n      <div class=\"metric\"><div class=\"label\">Samples</div><div class=\"value\">{latest.SampleCount} retained</div><div class=\"muted\">{latest.NewObservationCount} new this rebuild</div></div>\r\n      <div class=\"metric\"><div class=\"label\">Identity confidence</div><div class=\"value\">{latest.IdentityConfidencePercent.ToString("0.#", CultureInfo.InvariantCulture)}%</div><div class=\"muted\">{Signed(latest.IdentityConfidenceDeltaPoints)} pp</div></div>\r\n      <div class=\"metric\"><div class=\"label\">Pose coverage</div><div class=\"value\">{latest.PoseCoveragePercent.ToString("0.#", CultureInfo.InvariantCulture)}%</div><div class=\"muted\">{Signed(latest.PoseCoverageDeltaPoints)} pp</div></div>\r\n      <div class=\"metric\"><div class=\"label\">Shape stability</div><div class=\"value\">{latest.ShapeStabilityPercent.ToString("0.#", CultureInfo.InvariantCulture)}%</div><div class=\"muted\">{Signed(latest.ShapeStabilityDeltaPoints)} pp</div></div>\r\n      <div class=\"metric\"><div class=\"label\">Mean-face movement</div><div class=\"value\">{latest.OverallVertexRmsFaceSpanPercent.ToString("0.###", CultureInfo.InvariantCulture)}%</div><div class=\"muted\">of face span</div></div>\n      <div class=\"metric\"><div class=\"label\">Shape coefficient change</div><div class=\"value\">{latest.ShapeCoefficientRelativeRmsPercent.ToString("0.###", CultureInfo.InvariantCulture)}%</div><div class=\"muted\">relative RMS</div></div>\n      <div class=\"metric\"><div class=\"label\">Mapped landmark fit</div><div class=\"value\">{latest.IdentityMappingLandmarkRmsePercent.ToString("0.###", CultureInfo.InvariantCulture)}%</div><div class=\"muted\">{latest.IdentityMappingImprovementPercent.ToString("0.###", CultureInfo.InvariantCulture)}% last improvement</div></div>\n      <div class=\"metric\"><div class=\"label\">Mapped from generic</div><div class=\"value\">{latest.GenericIdentityDisplacementPercent.ToString("0.###", CultureInfo.InvariantCulture)}%</div><div class=\"muted\">normalized RMS</div></div>\n      <div class=\"metric\"><div class=\"label\">Outlier candidates</div><div class=\"value\">{latest.GeometryOutlierCandidateCount}</div><div class=\"muted\">highest {latest.HighestObservationRmsFaceSpanPercent.ToString("0.###", CultureInfo.InvariantCulture)}%</div></div>\r\n      <div class=\"metric\"><div class=\"label\">Expression range</div><div class=\"value\">{latest.MeanExpressionRange.ToString("0.###", CultureInfo.InvariantCulture)}</div><div class=\"muted\">delta {Signed(latest.MeanExpressionRangeDelta)}</div></div>\r\n    </div>\r\n  </section>\r\n  <section class=\"panel\">\r\n    <h2>Model Trend</h2>\r\n    <canvas id=\"trend\" aria-label=\"Recent confidence, coverage, and shape-stability trend\"></canvas>\r\n    <p class=\"muted\">Confidence <span class=\"good\">green</span>, pose coverage <span class=\"warn\">gold</span>, shape stability <span style=\"color:var(--cyan)\">cyan</span>. Each point is one accepted-batch rebuild.</p>\n  </section>\r\n  <div class=\"split\">\r\n    <section class=\"panel\"><h2>Geometry Movement By Region</h2><table><tr><th>Region</th><th>Matched vertices</th><th>RMS movement</th></tr>{value3}</table><p class=\"muted\">Regions are fixed geometric bands in the normalized dense face: eyes, central nose, mouth, and lower chin/jaw.</p></section>\r\n    <section class=\"panel\"><h2>Region Confidence</h2><table><tr><th>Region</th><th>Current</th><th>Change</th></tr>{value4}</table></section>\r\n  </div>\r\n  <section class=\"panel\"><h2>Current Warnings</h2><ul>{value2}</ul><p class=\"muted\">Downweighted identity observations: {latest.DownweightedIdentityObservationCount}. Excluded identity observations: {latest.ExcludedIdentityObservationCount}. Observations carrying backend warnings: {latest.WarningBearingObservationCount}.</p></section>\r\n  <section class=\"panel scroll\"><h2>Recent Rebuild Ledger</h2><table><tr><th>#</th><th>Time</th><th>Status</th><th>Samples</th><th>Confidence</th><th>Coverage</th><th>Fit RMSE</th><th>Mean movement</th><th>Warnings</th></tr>{value5}</table></section>\n  <section class=\"panel\"><h2>How To Read This</h2><p>{H(report.MeasurementPolicy)}</p><p>{H(report.RetentionPolicy)}</p><p class=\"muted\">Source: <code>{H(report.HistoryFileName)}</code>, generated from the accepted dense observation store and the derived canonical identity model. Increasing coverage and confidence are positive. A large one-rebuild geometry jump, falling confidence, or movement without a new observation is a regression warning. Early models are allowed to move while proportions are still settling.</p></section>\n</main>\r\n<script type=\"application/json\" id=\"historyJson\">{value6}</script>\r\n<script>\r\n(() => {{\r\n  const entries = JSON.parse(document.getElementById('historyJson')?.textContent || '[]');\r\n  const canvas = document.getElementById('trend');\r\n  const ctx = canvas?.getContext('2d');\r\n  if (!canvas || !ctx) return;\r\n  const series = [\r\n    {{ key: 'identityConfidencePercent', color: '#80e0a4' }},\r\n    {{ key: 'poseCoveragePercent', color: '#ffd27a' }},\r\n    {{ key: 'shapeStabilityPercent', color: '#66d9ff' }}\r\n  ];\r\n  const resize = () => {{\r\n    const rect = canvas.getBoundingClientRect();\r\n    const dpr = window.devicePixelRatio || 1;\r\n    canvas.width = Math.max(360, Math.round(rect.width * dpr));\r\n    canvas.height = Math.round(280 * dpr);\r\n    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);\r\n    draw(rect.width, 280);\r\n  }};\r\n  const draw = (width, height) => {{\r\n    ctx.clearRect(0, 0, width, height);\r\n    ctx.fillStyle = '#061019'; ctx.fillRect(0, 0, width, height);\r\n    ctx.strokeStyle = '#193149'; ctx.lineWidth = 1;\r\n    for (let value = 0; value <= 100; value += 20) {{\r\n      const y = height - 24 - value / 100 * (height - 44);\r\n      ctx.beginPath(); ctx.moveTo(42, y); ctx.lineTo(width - 12, y); ctx.stroke();\r\n      ctx.fillStyle = '#9db7c9'; ctx.fillText(String(value), 8, y + 4);\r\n    }}\r\n    if (entries.length < 2) return;\r\n    for (const item of series) {{\r\n      ctx.strokeStyle = item.color; ctx.lineWidth = 2; ctx.beginPath();\r\n      entries.forEach((entry, index) => {{\r\n        const x = 42 + index / Math.max(1, entries.length - 1) * (width - 56);\r\n        const value = Math.max(0, Math.min(100, Number(entry[item.key] ?? 0)));\r\n        const y = height - 24 - value / 100 * (height - 44);\r\n        if (index === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);\r\n      }});\r\n      ctx.stroke();\r\n    }}\r\n  }};\r\n  new ResizeObserver(resize).observe(canvas);\r\n  resize();\r\n}})();\r\n</script>\r\n</body>\r\n</html>";
	}

	private static string Signed(double value)
	{
		if (!(value > 0.0))
		{
			return value.ToString("0.###", CultureInfo.InvariantCulture);
		}
		return $"+{value:0.###}";
	}

	private static string H(string? value)
	{
		return WebUtility.HtmlEncode(value ?? "");
	}

	private static double Round(double value)
	{
		if (!double.IsFinite(value))
		{
			return 0.0;
		}
		return Math.Round(value, 6, MidpointRounding.AwayFromZero);
	}
}
