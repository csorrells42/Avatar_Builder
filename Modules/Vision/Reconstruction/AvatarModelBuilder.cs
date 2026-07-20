using System.IO;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Storage.AvatarObservations;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public static class AvatarModelBuilder
{
    private const double PoseBucketThresholdDegrees = 10d;
    private const double ExpressionHeavyThresholdPercent = 42d;

    public static AvatarModel Build(
        AvatarObservationDataset observationSet,
        AvatarObservationRepository repository)
    {
        var observations = observationSet.Observations
            .Where(HasIdentityGeometry)
            .OrderBy(static observation => observation.CapturedAtUtc)
            .ToList();

        if (observations.Count == 0)
        {
            return new AvatarModel
            {
                SubjectId = observationSet.SubjectId,
                SubjectDisplayName = observationSet.SubjectDisplayName,
                SourceObservationRevision = observationSet.Revision,
                Status = "waiting for accepted 3DDFA observations",
                Findings =
                [
                    "Log in the person at the camera and start Avatar Capture. The model begins once 3DDFA full-resolution samples attach to accepted face-lock frames."
                ]
            };
        }

        var identity = BuildIdentity(observationSet, observations, repository);
        var expression = BuildExpression(observations);
        var coverage = BuildCoverage(observations);
        var convergence = BuildConvergence(observations, identity, coverage);
        var findings = BuildFindings(observations, identity, expression, coverage);

        return new AvatarModel
        {
            SubjectId = observationSet.SubjectId,
            SubjectDisplayName = observationSet.SubjectDisplayName,
            SourceObservationRevision = observationSet.Revision,
            Status = convergence.IsMatureCandidate
                ? "avatar model is a mature candidate; continue collecting only when new evidence improves it"
                : "avatar model is accumulating ranked multi-angle 3DDFA evidence",
            Identity = identity,
            Expression = expression,
            PoseCoverage = coverage,
            Convergence = convergence,
            RecentSamples = observations
                .OrderByDescending(static observation => observation.CapturedAtUtc)
                .Take(24)
                .Select(observation => CreateSampleSummary(
                    observation,
                    repository.GetImagePath(observationSet, observation)))
                .ToList(),
            Findings = findings
        };
    }

    private static AvatarIdentityModel BuildIdentity(
        AvatarObservationDataset dataset,
        IReadOnlyList<AvatarObservation> observations,
        AvatarObservationRepository repository)
    {
        var vertexAccumulators = new Dictionary<int, WeightedPointAccumulator>();
        var shapeAccumulators = CreateCoefficientAccumulators(observations.Max(static observation => observation.ShapeCoefficients.Count));
        var totalWeight = 0d;
        var confidenceWeight = 0d;

        var weightedObservations = observations
            .Select(observation => new WeightedObservation(
                observation,
                Math.Clamp(observation.IdentityWeightPercent, 0d, 100d) / 100d))
            .Where(static item => item.Weight > 0.001d)
            .ToList();

        foreach (var item in weightedObservations)
        {
            var loaded = repository.LoadObservation(dataset, item.Observation);
            AddIdentityGeometry(
                new WeightedObservation(loaded, item.Weight),
                NormalizeIdentityVertices(loaded),
                vertexAccumulators,
                shapeAccumulators,
                ref totalWeight,
                ref confidenceWeight);
        }

        var meanVertices = CreateMeanVertices(vertexAccumulators.Values);
        var confidence = totalWeight <= 0d
            ? 0d
            : confidenceWeight / totalWeight;

        return new AvatarIdentityModel
        {
            SampleCount = observations.Count,
            ConfidencePercent = Round(confidence),
            DenseVertexCount = meanVertices.Count,
            DenseTopologyEdgeCount = dataset.DenseTopologyEdges.Count,
            ShapeCoefficientCount = shapeAccumulators.Count,
            ShapeCoefficientStabilityPercent = Round(CalculateCoefficientStability(shapeAccumulators)),
            MeanShapeCoefficients = shapeAccumulators.Select(static accumulator => Round(accumulator.Mean)).ToList(),
            MeanDenseVertices = meanVertices,
            TopologyEdges = dataset.DenseTopologyEdges.ToList(),
            RegionConfidence = BuildRegionConfidence(observations, confidence)
        };
    }

    private static AvatarExpressionModel BuildExpression(IReadOnlyList<AvatarObservation> observations)
    {
        var coefficientCount = observations.Max(static observation => observation.ExpressionCoefficients.Count);
        var accumulators = CreateCoefficientAccumulators(coefficientCount);
        var totalWeight = 0d;
        foreach (var observation in observations)
        {
            var weight = Math.Clamp(observation.ExpressionWeightPercent, 0d, 100d) / 100d;
            if (weight <= 0.001d)
            {
                continue;
            }

            AddCoefficients(accumulators, observation.ExpressionCoefficients, weight);
            totalWeight += weight;
        }

        var energy = observations.Count == 0
            ? 0d
            : observations.Select(observation => CalculateExpressionEnergy(observation.ExpressionCoefficients)).Average();
        return new AvatarExpressionModel
        {
            SampleCount = observations.Count,
            ConfidencePercent = Round(observations.Average(static observation => observation.ExpressionWeightPercent)),
            ExpressionCoefficientCount = coefficientCount,
            ExpressionEnergyPercent = Round(energy),
            MeanExpressionCoefficients = accumulators.Select(static accumulator => Round(accumulator.Mean)).ToList(),
            ExpressionRanges = accumulators
                .Select((accumulator, index) => new AvatarCoefficientRange
                {
                    Index = index,
                    Minimum = Round(accumulator.Minimum),
                    Maximum = Round(accumulator.Maximum),
                    Range = Round(accumulator.Range)
                })
                .ToList(),
            Buckets = BuildExpressionBuckets(observations)
        };
    }

    private static AvatarPoseCoverage BuildCoverage(IReadOnlyList<AvatarObservation> observations)
    {
        var aValues = observations.Select(static observation => observation.ARotationAroundXDegrees).ToList();
        var bValues = observations.Select(static observation => observation.BRotationAroundYDegrees).ToList();
        var cValues = observations.Select(static observation => observation.CRotationAroundZDegrees).ToList();
        var zValues = observations
            .Select(static observation => observation.RelativeDistanceScale)
            .Where(static value => value is > 0d)
            .Select(static value => value!.Value)
            .ToList();
        var front = observations.Count(IsFront);
        var leftB = observations.Count(static observation => observation.BRotationAroundYDegrees <= -PoseBucketThresholdDegrees);
        var rightB = observations.Count(static observation => observation.BRotationAroundYDegrees >= PoseBucketThresholdDegrees);
        var negativeA = observations.Count(static observation => observation.ARotationAroundXDegrees <= -PoseBucketThresholdDegrees);
        var positiveA = observations.Count(static observation => observation.ARotationAroundXDegrees >= PoseBucketThresholdDegrees);
        var negativeC = observations.Count(static observation => observation.CRotationAroundZDegrees <= -PoseBucketThresholdDegrees);
        var positiveC = observations.Count(static observation => observation.CRotationAroundZDegrees >= PoseBucketThresholdDegrees);
        var closeZ = 0;
        var farZ = 0;
        if (zValues.Count > 0)
        {
            var median = zValues.Order().ElementAt(zValues.Count / 2);
            closeZ = observations.Count(observation => observation.RelativeDistanceScale is { } scale && scale >= median * 1.08d);
            farZ = observations.Count(observation => observation.RelativeDistanceScale is { } scale && scale <= median * 0.92d);
        }

        var coveredBuckets = new[]
        {
            front > 0,
            leftB > 0,
            rightB > 0,
            negativeA > 0,
            positiveA > 0,
            negativeC > 0,
            positiveC > 0,
            closeZ > 0,
            farZ > 0
        }.Count(static covered => covered);
        var coverage = coveredBuckets / 9d * 100d;

        return new AvatarPoseCoverage
        {
            TotalSampleCount = observations.Count,
            FrontSampleCount = front,
            LeftBTurnSampleCount = leftB,
            RightBTurnSampleCount = rightB,
            NegativeATiltSampleCount = negativeA,
            PositiveATiltSampleCount = positiveA,
            NegativeCTiltSampleCount = negativeC,
            PositiveCTiltSampleCount = positiveC,
            CloseZSampleCount = closeZ,
            FarZSampleCount = farZ,
            ARangeDegrees = Round(Range(aValues)),
            BRangeDegrees = Round(Range(bValues)),
            CRangeDegrees = Round(Range(cValues)),
            ZScaleRangePercent = Round(Range(zValues) * 100d),
            CoveragePercent = Round(coverage),
            Summary = $"{coveredBuckets}/9 pose/depth buckets covered"
        };
    }

    internal static IReadOnlyList<FaceMeshLandmarkPoint> NormalizeIdentityVerticesForAudit(
        AvatarObservation observation)
    {
        return NormalizeIdentityVertices(observation);
    }

    private static AvatarModelConvergence BuildConvergence(
        IReadOnlyList<AvatarObservation> observations,
        AvatarIdentityModel identity,
        AvatarPoseCoverage coverage)
    {
        var sampleAdequacy = Math.Clamp(observations.Count / 180d * 100d, 0d, 100d);
        var quality = observations.Count == 0
            ? 0d
            : observations.Average(static observation => observation.RetentionScorePercent);
        var score = identity.ShapeCoefficientStabilityPercent * 0.35d
            + coverage.CoveragePercent * 0.25d
            + quality * 0.20d
            + sampleAdequacy * 0.20d;
        var mature = observations.Count >= 120
            && identity.ShapeCoefficientStabilityPercent >= 75d
            && coverage.CoveragePercent >= 77d
            && score >= 82d;
        var label = mature
            ? "mature candidate"
            : observations.Count < 36
                ? "early collection"
                : identity.ShapeCoefficientStabilityPercent < 65d
                    ? "identity still stabilizing"
                    : coverage.CoveragePercent < 65d
                        ? "needs broader pose coverage"
                        : "converging";
        return new AvatarModelConvergence
        {
            ScorePercent = Round(score),
            SampleAdequacyPercent = Round(sampleAdequacy),
            QualityPercent = Round(quality),
            IsMatureCandidate = mature,
            Label = label,
            Basis = $"{observations.Count} ranked observations; {identity.ShapeCoefficientStabilityPercent:0.#}% coefficient stability; {coverage.CoveragePercent:0.#}% pose/depth coverage; {quality:0.#}% retained quality."
        };
    }

    private static IReadOnlyList<FaceMeshLandmarkPoint> NormalizeIdentityVertices(AvatarObservation observation)
    {
        var source = observation.CanonicalIdentityVertices;
        if (source.Count == 0)
        {
            throw new InvalidDataException($"Observation {observation.ObservationId} did not contain canonical identity geometry.");
        }
        var bounds = Bounds.From(source);
        var centerX = (bounds.MinX + bounds.MaxX) * 0.5d;
        var centerY = (bounds.MinY + bounds.MaxY) * 0.5d;
        var centerZ = (bounds.MinZ + bounds.MaxZ) * 0.5d;
        var scale = Math.Max(0.0001d, Math.Max(bounds.MaxX - bounds.MinX, bounds.MaxY - bounds.MinY));
        var normalized = new List<FaceMeshLandmarkPoint>(source.Count);
        foreach (var vertex in source)
        {
            normalized.Add(new FaceMeshLandmarkPoint
            {
                Index = vertex.Index,
                X = Round((vertex.X - centerX) / scale),
                Y = Round((vertex.Y - centerY) / scale),
                Z = Round((vertex.Z - centerZ) / scale)
            });
        }

        return normalized;
    }

    private static void AddIdentityGeometry(
        WeightedObservation item,
        IReadOnlyList<FaceMeshLandmarkPoint> vertices,
        IDictionary<int, WeightedPointAccumulator> vertexAccumulators,
        IReadOnlyList<WeightedCoefficientAccumulator> shapeAccumulators,
        ref double totalWeight,
        ref double confidenceWeight)
    {
        foreach (var point in vertices)
        {
            if (!vertexAccumulators.TryGetValue(point.Index, out var accumulator))
            {
                accumulator = new WeightedPointAccumulator(point.Index);
                vertexAccumulators[point.Index] = accumulator;
            }

            accumulator.Add(point, item.Weight);
        }

        AddCoefficients(shapeAccumulators, item.Observation.ShapeCoefficients, item.Weight);
        totalWeight += item.Weight;
        confidenceWeight += item.Observation.ReconstructionConfidencePercent * item.Weight;
    }

    private static List<FaceMeshLandmarkPoint> CreateMeanVertices(
        IEnumerable<WeightedPointAccumulator> accumulators)
    {
        return accumulators
            .Where(static accumulator => accumulator.Weight > 0d)
            .OrderBy(static accumulator => accumulator.Index)
            .Select(static accumulator => accumulator.ToPoint())
            .ToList();
    }

    private static bool HasIdentityGeometry(AvatarObservation observation)
    {
        return observation.CanonicalVertexCount >= 1_000;
    }

    private static List<WeightedCoefficientAccumulator> CreateCoefficientAccumulators(int count)
    {
        return Enumerable.Range(0, Math.Max(0, count))
            .Select(static _ => new WeightedCoefficientAccumulator())
            .ToList();
    }

    private static void AddCoefficients(
        IReadOnlyList<WeightedCoefficientAccumulator> accumulators,
        IReadOnlyList<double> coefficients,
        double weight)
    {
        for (var index = 0; index < accumulators.Count && index < coefficients.Count; index++)
        {
            accumulators[index].Add(coefficients[index], weight);
        }
    }

    private static double CalculateCoefficientStability(IReadOnlyList<WeightedCoefficientAccumulator> accumulators)
    {
        if (accumulators.Count == 0)
        {
            return 0d;
        }

        var signalRms = Math.Sqrt(accumulators.Select(static accumulator => accumulator.Mean * accumulator.Mean).DefaultIfEmpty(0d).Average());
        var noiseRms = Math.Sqrt(accumulators.Select(static accumulator => accumulator.StandardDeviation * accumulator.StandardDeviation).DefaultIfEmpty(0d).Average());
        return signalRms + noiseRms <= 0.000001d
            ? 0d
            : Math.Clamp(signalRms / (signalRms + noiseRms) * 100d, 0d, 100d);
    }

    private static List<AvatarRegionConfidence> BuildRegionConfidence(
        IReadOnlyList<AvatarObservation> observations,
        double identityConfidencePercent)
    {
        var averageReconstruction = observations.Average(static observation => observation.ReconstructionConfidencePercent);
        return
        [
            new AvatarRegionConfidence { Region = "Face surface", ConfidencePercent = Round(Blend(identityConfidencePercent, averageReconstruction)), Basis = "3DDFA reconstruction confidence plus identity sample weight" },
            new AvatarRegionConfidence { Region = "Eyes", ConfidencePercent = Round(averageReconstruction), Basis = "3DDFA canonical expression-free BFM geometry" },
            new AvatarRegionConfidence { Region = "Mouth and jaw", ConfidencePercent = Round(averageReconstruction), Basis = "3DDFA canonical identity geometry; expression coefficients are modeled separately" },
            new AvatarRegionConfidence { Region = "Eyebrows", ConfidencePercent = Round(averageReconstruction), Basis = "3DDFA canonical expression-free BFM geometry" },
            new AvatarRegionConfidence { Region = "Nose, cheeks, forehead", ConfidencePercent = Round(averageReconstruction), Basis = "3DDFA dense model topology" }
        ];
    }

    private static List<AvatarExpressionBucket> BuildExpressionBuckets(IReadOnlyList<AvatarObservation> observations)
    {
        var relaxed = observations
            .Where(observation => CalculateExpressionEnergy(observation.ExpressionCoefficients) < ExpressionHeavyThresholdPercent)
            .ToList();
        var expressive = observations
            .Where(observation => CalculateExpressionEnergy(observation.ExpressionCoefficients) >= ExpressionHeavyThresholdPercent)
            .ToList();
        var mouthHeavy = observations
            .Where(static observation => observation.MouthQualityPercent >= 55d)
            .ToList();
        return
        [
            CreateBucket("Relaxed / identity-friendly", relaxed, "Frames with lower 3DDFA expression energy; strongest candidates for base identity."),
            CreateBucket("Expression range", expressive, "Frames with higher expression energy; useful for motion without reshaping the identity model."),
            CreateBucket("Mouth and jaw evidence", mouthHeavy, "Frames with usable mouth/jaw evidence for speech, jaw droop, and open-mouth range.")
        ];
    }

    private static AvatarExpressionBucket CreateBucket(
        string name,
        IReadOnlyList<AvatarObservation> observations,
        string meaning)
    {
        return new AvatarExpressionBucket
        {
            Name = name,
            SampleCount = observations.Count,
            AverageEnergyPercent = observations.Count == 0
                ? 0d
                : Round(observations.Select(observation => CalculateExpressionEnergy(observation.ExpressionCoefficients)).Average()),
            Meaning = meaning
        };
    }

    private static List<string> BuildFindings(
        IReadOnlyList<AvatarObservation> observations,
        AvatarIdentityModel identity,
        AvatarExpressionModel expression,
        AvatarPoseCoverage coverage)
    {
        var findings = new List<string>
        {
            $"Stored {observations.Count} ranked 3DDFA observation(s); identity uses direct canonical expression-free geometry and the expression model stays separate.",
            $"Current dense identity preview has {identity.DenseVertexCount:n0} averaged vertices and {identity.DenseTopologyEdgeCount:n0} topology edges."
        };
        if (observations.Count < 12)
        {
            findings.Add("Collect more relaxed front-facing and small-turn samples before trusting fine facial proportions.");
        }

        if (coverage.LeftBTurnSampleCount == 0 || coverage.RightBTurnSampleCount == 0)
        {
            findings.Add("Need both left and right B-axis head turns to improve cheek, nose, and side-depth confidence.");
        }

        if (coverage.NegativeATiltSampleCount == 0 || coverage.PositiveATiltSampleCount == 0)
        {
            findings.Add("Need gentle A-axis up/down tilt samples to improve forehead, chin, and nose-depth confidence.");
        }

        if (coverage.CloseZSampleCount == 0 || coverage.FarZSampleCount == 0)
        {
            findings.Add("Need closer/farther Z samples to verify scale instead of treating camera zoom or distance as face shape.");
        }

        if (expression.Buckets.Any(static bucket => bucket.Name == "Expression range" && bucket.SampleCount == 0))
        {
            findings.Add("Expression model is still mostly relaxed; natural talking, blinks, and jaw movement will improve motion coverage.");
        }

        return findings;
    }

    private static AvatarModelSampleSummary CreateSampleSummary(
        AvatarObservation observation,
        string? sourceImagePath)
    {
        return new AvatarModelSampleSummary
        {
            RequestId = observation.RequestId,
            SampleId = observation.SampleId,
            CapturedAtUtc = observation.CapturedAtUtc,
            WeightPercent = Round(observation.IdentityWeightPercent),
            ReconstructionConfidencePercent = Round(observation.ReconstructionConfidencePercent),
            SampleQualityPercent = Round(observation.SampleQualityPercent),
            ARotationAroundXDegrees = Round(observation.ARotationAroundXDegrees),
            BRotationAroundYDegrees = Round(observation.BRotationAroundYDegrees),
            CRotationAroundZDegrees = Round(observation.CRotationAroundZDegrees),
            VertexCount = observation.CanonicalVertexCount > 0
                ? observation.CanonicalVertexCount
                : observation.DenseVertexCount,
            IdentityUse = observation.IdentityUse,
            SourceImageUri = string.IsNullOrWhiteSpace(sourceImagePath)
                ? ""
                : new Uri(sourceImagePath).AbsoluteUri
        };
    }

    private static bool IsFront(AvatarObservation observation)
    {
        return Math.Abs(observation.ARotationAroundXDegrees) < PoseBucketThresholdDegrees
            && Math.Abs(observation.BRotationAroundYDegrees) < PoseBucketThresholdDegrees
            && Math.Abs(observation.CRotationAroundZDegrees) < PoseBucketThresholdDegrees;
    }

    private static double CalculateExpressionEnergy(IReadOnlyList<double> coefficients)
    {
        if (coefficients.Count == 0)
        {
            return 0d;
        }

        var mean = coefficients.Select(static coefficient => Math.Abs(coefficient)).Average();
        return Math.Clamp(mean * 100d, 0d, 100d);
    }

    private static double Range(IReadOnlyList<double> values)
    {
        return values.Count == 0 ? 0d : values.Max() - values.Min();
    }

    private static double Blend(double first, double second)
    {
        return first * 0.55d + second * 0.45d;
    }

    private static double Round(double value)
    {
        return double.IsFinite(value)
            ? Math.Round(value, 6, MidpointRounding.AwayFromZero)
            : 0d;
    }

    private sealed class WeightedPointAccumulator(int index)
    {
        public int Index { get; } = index;

        public double Weight { get; private set; }

        private double X { get; set; }

        private double Y { get; set; }

        private double Z { get; set; }

        public void Add(FaceMeshLandmarkPoint point, double weight)
        {
            Weight += weight;
            X += point.X * weight;
            Y += point.Y * weight;
            Z += point.Z * weight;
        }

        public FaceMeshLandmarkPoint ToPoint()
        {
            var weight = Math.Max(0.000001d, Weight);
            return new FaceMeshLandmarkPoint
            {
                Index = Index,
                X = Round(X / weight),
                Y = Round(Y / weight),
                Z = Round(Z / weight)
            };
        }
    }

    private sealed class WeightedCoefficientAccumulator
    {
        public double Weight { get; private set; }

        public double Mean => Weight <= 0d ? 0d : Sum / Weight;

        public double Minimum { get; private set; } = double.PositiveInfinity;

        public double Maximum { get; private set; } = double.NegativeInfinity;

        public double Range => double.IsFinite(Minimum) && double.IsFinite(Maximum) ? Maximum - Minimum : 0d;

        public double StandardDeviation => Weight <= 0d
            ? 0d
            : Math.Sqrt(Math.Max(0d, SumSquares / Weight - Mean * Mean));

        private double Sum { get; set; }

        private double SumSquares { get; set; }

        public void Add(double value, double weight)
        {
            if (!double.IsFinite(value) || weight <= 0d)
            {
                return;
            }

            Weight += weight;
            Sum += value * weight;
            SumSquares += value * value * weight;
            Minimum = Math.Min(Minimum, value);
            Maximum = Math.Max(Maximum, value);
        }
    }

    private sealed record WeightedObservation(
        AvatarObservation Observation,
        double Weight);

    private readonly record struct Bounds(double MinX, double MaxX, double MinY, double MaxY, double MinZ, double MaxZ)
    {
        public static Bounds From(IReadOnlyList<FaceMeshLandmarkPoint> points)
        {
            return new Bounds(
                points.Min(static point => point.X),
                points.Max(static point => point.X),
                points.Min(static point => point.Y),
                points.Max(static point => point.Y),
                points.Min(static point => point.Z),
                points.Max(static point => point.Z));
        }
    }
}
