using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public static class AvatarModelBuilder
{
    private const double PoseBucketThresholdDegrees = 10d;
    private const double ExpressionHeavyThresholdPercent = 42d;

    public static AvatarModel Build(AvatarModelObservationSet observationSet)
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
                Status = "waiting for accepted 3DDFA observations",
                Findings =
                [
                    "Log in the person at the camera and start Avatar Capture. The model begins once 3DDFA full-resolution samples attach to accepted face-lock frames."
                ]
            };
        }

        var identity = BuildIdentity(observations, observationSet.DenseTopologyEdges);
        var expression = BuildExpression(observations);
        var coverage = BuildCoverage(observations);
        var findings = BuildFindings(observations, identity, expression, coverage);

        return new AvatarModel
        {
            SubjectId = observationSet.SubjectId,
            SubjectDisplayName = observationSet.SubjectDisplayName,
            Status = identity.SampleCount >= 8 && coverage.CoveragePercent >= 45d
                ? "avatar model is accumulating useful multi-angle 3DDFA evidence"
                : "avatar model is started; collect more angles and expressions",
            Identity = identity,
            Expression = expression,
            PoseCoverage = coverage,
            RecentSamples = observations
                .OrderByDescending(static observation => observation.CapturedAtUtc)
                .Take(24)
                .Select(CreateSampleSummary)
                .ToList(),
            Findings = findings
        };
    }

    private static AvatarIdentityModel BuildIdentity(
        IReadOnlyList<AvatarModelObservation> observations,
        IReadOnlyList<MeshTopologyEdge> topologyEdges)
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

        var canonicalObservations = weightedObservations
            .Where(static item => HasCanonicalIdentityGeometry(item.Observation))
            .ToList();
        foreach (var item in canonicalObservations)
        {
            AddIdentityGeometry(
                item,
                NormalizeIdentityVertices(item.Observation),
                vertexAccumulators,
                shapeAccumulators,
                ref totalWeight,
                ref confidenceWeight);
        }

        var legacyObservations = weightedObservations
            .Where(static item => !HasCanonicalIdentityGeometry(item.Observation))
            .ToList();
        if (legacyObservations.Count > 0)
        {
            if (canonicalObservations.Count > 0)
            {
                var canonicalMean = CreateMeanVertices(vertexAccumulators.Values);
                foreach (var item in legacyObservations)
                {
                    var aligned = DenseMeshRigidAligner.Align(
                        NormalizeIdentityVertices(item.Observation),
                        canonicalMean);
                    AddIdentityGeometry(
                        item,
                        aligned,
                        vertexAccumulators,
                        shapeAccumulators,
                        ref totalWeight,
                        ref confidenceWeight);
                }
            }
            else
            {
                var alignedLegacy = AlignLegacyIdentityGeometries(legacyObservations
                    .Select(item => new WeightedIdentityGeometry(
                        item.Observation,
                        item.Weight,
                        NormalizeIdentityVertices(item.Observation)))
                    .ToList());
                foreach (var geometry in alignedLegacy)
                {
                    AddIdentityGeometry(
                        new WeightedObservation(geometry.Observation, geometry.Weight),
                        geometry.Vertices,
                        vertexAccumulators,
                        shapeAccumulators,
                        ref totalWeight,
                        ref confidenceWeight);
                }
            }
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
            DenseTopologyEdgeCount = topologyEdges.Count,
            ShapeCoefficientCount = shapeAccumulators.Count,
            ShapeCoefficientStabilityPercent = Round(CalculateCoefficientStability(shapeAccumulators)),
            MeanShapeCoefficients = shapeAccumulators.Select(static accumulator => Round(accumulator.Mean)).ToList(),
            MeanDenseVertices = meanVertices,
            TopologyEdges = topologyEdges.ToList(),
            RegionConfidence = BuildRegionConfidence(observations, confidence)
        };
    }

    private static AvatarExpressionModel BuildExpression(IReadOnlyList<AvatarModelObservation> observations)
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

    private static AvatarPoseCoverage BuildCoverage(IReadOnlyList<AvatarModelObservation> observations)
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
        AvatarModelObservation observation,
        IReadOnlyList<FaceMeshLandmarkPoint> target)
    {
        var normalized = NormalizeIdentityVertices(observation);
        return target.Count == 0 || HasCanonicalIdentityGeometry(observation)
            ? normalized
            : DenseMeshRigidAligner.Align(normalized, target);
    }

    private static IReadOnlyList<FaceMeshLandmarkPoint> NormalizeIdentityVertices(AvatarModelObservation observation)
    {
        var source = HasCanonicalIdentityGeometry(observation)
            ? observation.CanonicalIdentityVertices
            : observation.Vertices;
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

    private static List<WeightedIdentityGeometry> AlignLegacyIdentityGeometries(
        IReadOnlyList<WeightedIdentityGeometry> geometries)
    {
        if (geometries.Count <= 1)
        {
            return geometries.ToList();
        }

        IReadOnlyList<FaceMeshLandmarkPoint> mean = geometries[0].Vertices;
        var aligned = geometries.ToList();
        for (var iteration = 0; iteration < 4; iteration++)
        {
            aligned = geometries
                .Select(geometry => geometry with
                {
                    Vertices = DenseMeshRigidAligner.Align(geometry.Vertices, mean)
                })
                .ToList();
            mean = CalculateWeightedMean(aligned);
        }

        return aligned;
    }

    private static IReadOnlyList<FaceMeshLandmarkPoint> CalculateWeightedMean(
        IReadOnlyList<WeightedIdentityGeometry> geometries)
    {
        var accumulators = new Dictionary<int, WeightedPointAccumulator>();
        foreach (var geometry in geometries)
        {
            foreach (var point in geometry.Vertices)
            {
                if (!accumulators.TryGetValue(point.Index, out var accumulator))
                {
                    accumulator = new WeightedPointAccumulator(point.Index);
                    accumulators[point.Index] = accumulator;
                }

                accumulator.Add(point, geometry.Weight);
            }
        }

        return accumulators.Values
            .OrderBy(static accumulator => accumulator.Index)
            .Select(static accumulator => accumulator.ToPoint())
            .ToList();
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

    private static bool HasIdentityGeometry(AvatarModelObservation observation)
    {
        return HasCanonicalIdentityGeometry(observation) || observation.Vertices.Count > 0;
    }

    private static bool HasCanonicalIdentityGeometry(AvatarModelObservation observation)
    {
        return observation.CanonicalIdentityVertices.Count > 0;
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
        IReadOnlyList<AvatarModelObservation> observations,
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

    private static List<AvatarExpressionBucket> BuildExpressionBuckets(IReadOnlyList<AvatarModelObservation> observations)
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
        IReadOnlyList<AvatarModelObservation> observations,
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
        IReadOnlyList<AvatarModelObservation> observations,
        AvatarIdentityModel identity,
        AvatarExpressionModel expression,
        AvatarPoseCoverage coverage)
    {
        var findings = new List<string>
        {
            $"Stored {observations.Count} bounded 3DDFA observation(s); identity uses direct canonical expression-free geometry, legacy scans use rigid non-deforming alignment, and the expression model stays separate.",
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

    private static AvatarModelSampleSummary CreateSampleSummary(AvatarModelObservation observation)
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
            VertexCount = HasCanonicalIdentityGeometry(observation)
                ? observation.CanonicalIdentityVertices.Count
                : observation.Vertices.Count,
            IdentityUse = observation.IdentityUse
        };
    }

    private static bool IsFront(AvatarModelObservation observation)
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

    private sealed record WeightedIdentityGeometry(
        AvatarModelObservation Observation,
        double Weight,
        IReadOnlyList<FaceMeshLandmarkPoint> Vertices);

    private sealed record WeightedObservation(
        AvatarModelObservation Observation,
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
