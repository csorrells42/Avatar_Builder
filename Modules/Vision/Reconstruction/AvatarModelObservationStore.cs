using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AvatarBuilder.Modules.Infrastructure;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public sealed class AvatarModelObservationStore
{
    public const string JsonFileName = "avatar_model_observations.json";
    public const int MaxObservationCount = 120;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public AvatarModelObservationSet MergeAndWrite(
        string folder,
        string subjectId,
        string subjectDisplayName,
        IReadOnlyList<ThreeDdfaReconstructionSnapshot> threeDdfaSamples)
    {
        Directory.CreateDirectory(folder);
        var path = GetJsonPath(folder);
        var existing = Read(path);
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

        var updated = new AvatarModelObservationSet
        {
            UpdatedAtUtc = DateTime.UtcNow,
            SubjectId = subjectId,
            SubjectDisplayName = subjectDisplayName,
            MaxObservationCount = MaxObservationCount,
            DenseTopologyEdges = topology,
            Observations = retained
        };

        AtomicTextFileWriter.WriteAllText(path, JsonSerializer.Serialize(updated, JsonOptions), Encoding.UTF8);
        return updated;
    }

    public static string GetJsonPath(string folder)
    {
        return Path.Combine(folder, JsonFileName);
    }

    private static AvatarModelObservationSet Read(string path)
    {
        if (!File.Exists(path))
        {
            return new AvatarModelObservationSet();
        }

        try
        {
            return JsonSerializer.Deserialize<AvatarModelObservationSet>(File.ReadAllText(path), JsonOptions)
                ?? new AvatarModelObservationSet();
        }
        catch
        {
            return new AvatarModelObservationSet();
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
                : "3DDFA identity frame: pose-normalized into the base face model",
            TrustDecision = snapshot.TrustDecision,
            Vertices = snapshot.Vertices.Select(CopyPoint).ToList(),
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
