using System.Globalization;
using System.IO.Compression;
using System.IO;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarModelObservationStore
{
    public const string JsonFileName = "avatar_model_observations.json.gz";
    public const string LegacyJsonFileName = "avatar_model_observations.json";
    public const int MaxObservationCount = 120;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public AvatarModelObservationMergeResult MergeAndWrite(
        string folder,
        string subjectId,
        string subjectDisplayName,
        IReadOnlyList<ThreeDdfaReconstructionSnapshot> threeDdfaSamples)
    {
        Directory.CreateDirectory(folder);
        var path = GetJsonPath(folder);
        var existing = Read(folder);
        var retainedThreeDdfaObservations = existing.Observations
            .Where(static observation => observation.Source.Contains("3DDFA", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var byRequestId = retainedThreeDdfaObservations
            .Where(static observation => !string.IsNullOrWhiteSpace(observation.RequestId))
            .GroupBy(static observation => observation.RequestId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.OrderByDescending(item => item.CapturedAtUtc).First(), StringComparer.Ordinal);

        var bySampleFallback = retainedThreeDdfaObservations
            .Where(static observation => string.IsNullOrWhiteSpace(observation.RequestId))
            .GroupBy(static observation => observation.SampleId, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.OrderByDescending(item => item.CapturedAtUtc).First(), StringComparer.Ordinal);

        var topology = existing.DenseTopologyEdges.ToList();
        foreach (var snapshot in threeDdfaSamples)
        {
            if (TryCreateObservation(snapshot, out var observation))
            {
                if (!string.IsNullOrWhiteSpace(observation.RequestId))
                {
                    byRequestId[observation.RequestId] = observation;
                }
                else
                {
                    bySampleFallback[observation.SampleId] = observation;
                }

                if (snapshot.TopologyEdges.Count > topology.Count)
                {
                    topology = snapshot.TopologyEdges.ToList();
                }
            }
        }

        var retained = byRequestId.Values
            .Concat(bySampleFallback.Values)
            .OrderByDescending(static observation => observation.CapturedAtUtc)
            .Take(MaxObservationCount)
            .OrderBy(static observation => observation.CapturedAtUtc)
            .ToList();

        var changed = HasChanged(existing, subjectId, subjectDisplayName, retained, topology);
        var updated = changed
            ? new AvatarModelObservationSet
            {
                UpdatedAtUtc = DateTime.UtcNow,
                SubjectId = subjectId,
                SubjectDisplayName = subjectDisplayName,
                MaxObservationCount = MaxObservationCount,
                DenseTopologyEdges = topology,
                Observations = retained
            }
            : existing;

        if (changed || !File.Exists(path))
        {
            WriteCompressed(path, updated);
            TryDeleteLegacyFile(folder);
        }

        return new AvatarModelObservationMergeResult(updated, changed);
    }

    public static string GetJsonPath(string folder)
    {
        return Path.Combine(folder, JsonFileName);
    }

    public AvatarModelObservationSet Read(string folder)
    {
        var path = GetJsonPath(folder);
        if (File.Exists(path))
        {
            try
            {
                using var file = File.OpenRead(path);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                return JsonSerializer.Deserialize<AvatarModelObservationSet>(gzip, JsonOptions)
                    ?? new AvatarModelObservationSet();
            }
            catch
            {
            }
        }

        var legacyPath = Path.Combine(folder, LegacyJsonFileName);
        try
        {
            return File.Exists(legacyPath)
                ? (JsonSerializer.Deserialize<AvatarModelObservationSet>(File.ReadAllText(legacyPath), JsonOptions)
                    ?? new AvatarModelObservationSet())
                : new AvatarModelObservationSet();
        }
        catch
        {
            return new AvatarModelObservationSet();
        }
    }

    private static bool HasChanged(
        AvatarModelObservationSet existing,
        string subjectId,
        string subjectDisplayName,
        IReadOnlyList<AvatarModelObservation> observations,
        IReadOnlyList<MeshTopologyEdge> topology)
    {
        if (!string.Equals(existing.SchemaVersion, AvatarModelObservationSet.CurrentSchemaVersion, StringComparison.Ordinal)
            || !string.Equals(existing.SubjectId, subjectId, StringComparison.Ordinal)
            || !string.Equals(existing.SubjectDisplayName, subjectDisplayName, StringComparison.Ordinal)
            || existing.Observations.Count != observations.Count
            || existing.DenseTopologyEdges.Count != topology.Count)
        {
            return true;
        }

        for (var index = 0; index < observations.Count; index++)
        {
            var oldItem = existing.Observations[index];
            var newItem = observations[index];
            if (!string.Equals(oldItem.RequestId, newItem.RequestId, StringComparison.Ordinal)
                || !string.Equals(oldItem.SampleId, newItem.SampleId, StringComparison.Ordinal)
                || oldItem.CapturedAtUtc != newItem.CapturedAtUtc
                || oldItem.Vertices.Count != newItem.Vertices.Count
                || oldItem.CanonicalIdentityVertices.Count != newItem.CanonicalIdentityVertices.Count
                || !string.Equals(oldItem.IdentityGeometrySource, newItem.IdentityGeometrySource, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteCompressed(string path, AvatarModelObservationSet observations)
    {
        var directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = File.Create(tempPath))
            using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
            {
                JsonSerializer.Serialize(gzip, observations, JsonOptions);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDeleteLegacyFile(string folder)
    {
        TryDelete(Path.Combine(folder, LegacyJsonFileName));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static bool TryCreateObservation(ThreeDdfaReconstructionSnapshot snapshot, out AvatarModelObservation observation)
    {
        observation = new AvatarModelObservation();
        if (snapshot.Vertices.Count == 0)
        {
            return false;
        }

        var expressionEnergy = CalculateExpressionEnergy(snapshot.ExpressionCoefficients);
        var baseWeight = Math.Clamp(snapshot.ReconstructionConfidencePercent, 0d, 100d);
        var identityExpressionPenalty = Math.Clamp(expressionEnergy * 0.30d, 0d, 35d);
        var identityWeight = Math.Clamp(baseWeight - identityExpressionPenalty, 0d, 100d);
        var sampleId = string.IsNullOrWhiteSpace(snapshot.RequestId)
            ? $"3ddfa-{snapshot.CapturedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture)}"
            : $"3ddfa-{snapshot.RequestId}";

        observation = new AvatarModelObservation
        {
            RequestId = snapshot.RequestId,
            SampleId = sampleId,
            CapturedAtUtc = snapshot.CapturedAtUtc == default ? DateTime.UtcNow : snapshot.CapturedAtUtc,
            Source = snapshot.Source,
            ReconstructionConfidencePercent = Round(snapshot.ReconstructionConfidencePercent),
            SampleQualityPercent = Round(baseWeight),
            EyeQualityPercent = 0d,
            MouthQualityPercent = 0d,
            BrowQualityPercent = 0d,
            ARotationAroundXDegrees = Round(snapshot.ARotationAroundXDegrees),
            BRotationAroundYDegrees = Round(snapshot.BRotationAroundYDegrees),
            CRotationAroundZDegrees = Round(snapshot.CRotationAroundZDegrees),
            XHorizontalPercent = 0d,
            YVerticalPercent = 0d,
            RelativeDistanceScale = null,
            ApparentDistanceUnits = null,
            IdentityWeightPercent = Round(identityWeight),
            ExpressionWeightPercent = Round(baseWeight),
            IdentityUse = identityExpressionPenalty > 20d
                ? "3DDFA expression-heavy frame: useful for expression range, downweighted for base identity"
                : "3DDFA identity frame: canonical expression-free geometry contributes to the base face model",
            TrustDecision = snapshot.TrustDecision,
            Vertices = snapshot.Vertices.Select(CopyPoint).ToList(),
            CanonicalIdentityVertices = snapshot.CanonicalIdentityVertices.Select(CopyPoint).ToList(),
            IdentityGeometrySource = snapshot.CanonicalIdentityVertices.Count == snapshot.Vertices.Count
                && snapshot.CanonicalIdentityVertices.Count > 0
                ? "3DDFA canonical expression-free BFM identity geometry"
                : "legacy image-space 3DDFA vertices; rigid alignment required",
            CameraMatrixCoefficients = snapshot.CameraMatrixCoefficients.Select(Round).ToList(),
            ShapeCoefficients = snapshot.ShapeCoefficients.Select(Round).ToList(),
            ExpressionCoefficients = snapshot.ExpressionCoefficients.Select(Round).ToList(),
            Warnings = snapshot.Warnings.ToList()
        };
        return true;
    }

    private static FaceMeshLandmarkPoint CopyPoint(FaceMeshLandmarkPoint point)
    {
        return new FaceMeshLandmarkPoint
        {
            Index = point.Index,
            X = Round(point.X),
            Y = Round(point.Y),
            Z = Round(point.Z)
        };
    }

    private static double CalculateExpressionEnergy(IReadOnlyList<double> coefficients)
    {
        if (coefficients.Count == 0)
        {
            return 0d;
        }

        var mean = coefficients.Select(Math.Abs).Average();
        return Math.Clamp(mean * 100d, 0d, 100d);
    }

    private static double Round(double value)
    {
        return double.IsFinite(value)
            ? Math.Round(value, 6, MidpointRounding.AwayFromZero)
            : 0d;
    }

}

public sealed record AvatarModelObservationMergeResult(
    AvatarModelObservationSet ObservationSet,
    bool Changed);
