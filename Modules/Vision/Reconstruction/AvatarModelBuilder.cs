using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using AvatarBuilder.Modules.Storage.AvatarObservations;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

public static class AvatarModelBuilder
{
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
			double num = Math.Max(1E-06, Weight);
			return new FaceMeshLandmarkPoint
			{
				Index = Index,
				X = X / num,
				Y = Y / num,
				Z = Z / num
			};
		}
	}

	private sealed class WeightedCoefficientAccumulator
	{
		public double Weight { get; private set; }

		public double Mean
		{
			get
			{
				if (!(Weight <= 0.0))
				{
					return Sum / Weight;
				}
				return 0.0;
			}
		}

		public double MeanSquare
		{
			get
			{
				if (!(Weight <= 0.0))
				{
					return SumSquares / Weight;
				}
				return 0.0;
			}
		}

		public double Minimum { get; private set; } = double.PositiveInfinity;

		public double Maximum { get; private set; } = double.NegativeInfinity;

		public double Range
		{
			get
			{
				if (!double.IsFinite(Minimum) || !double.IsFinite(Maximum))
				{
					return 0.0;
				}
				return Maximum - Minimum;
			}
		}

		public double StandardDeviation
		{
			get
			{
				if (!(Weight <= 0.0))
				{
					return Math.Sqrt(Math.Max(0.0, SumSquares / Weight - Mean * Mean));
				}
				return 0.0;
			}
		}

		private double Sum { get; set; }

		private double SumSquares { get; set; }

		public void Add(double value, double weight)
		{
			if (double.IsFinite(value) && !(weight <= 0.0))
			{
				Weight += weight;
				Sum += value * weight;
				SumSquares += value * value * weight;
				Minimum = Math.Min(Minimum, value);
				Maximum = Math.Max(Maximum, value);
			}
		}
	}

	private sealed record WeightedObservation(AvatarObservation Observation, double Weight);

	private readonly record struct Bounds(double MinX, double MaxX, double MinY, double MaxY, double MinZ, double MaxZ)
	{
		public static Bounds From(IReadOnlyList<FaceMeshLandmarkPoint> points)
		{
			return new Bounds(points.Min((FaceMeshLandmarkPoint point) => point.X), points.Max((FaceMeshLandmarkPoint point) => point.X), points.Min((FaceMeshLandmarkPoint point) => point.Y), points.Max((FaceMeshLandmarkPoint point) => point.Y), points.Min((FaceMeshLandmarkPoint point) => point.Z), points.Max((FaceMeshLandmarkPoint point) => point.Z));
		}
	}

	private const double PoseBucketThresholdDegrees = 10.0;

	private const double ExpressionHeavyThresholdPercent = 42.0;

	public static AvatarModel Build(AvatarObservationDataset observationSet, AvatarObservationRepository repository)
	{
		List<AvatarObservation> list = (from observation in observationSet.Observations.Where(HasIdentityGeometry)
			orderby observation.CapturedAtUtc
			select observation).ToList();
		if (list.Count == 0)
		{
			AvatarModel obj = new AvatarModel
			{
				SubjectId = observationSet.SubjectId,
				SubjectDisplayName = observationSet.SubjectDisplayName,
				SourceObservationRevision = observationSet.Revision,
				Status = "waiting for accepted reconstruction observations"
			};
			int num = 1;
			List<string> list2 = new List<string>(num);
			CollectionsMarshal.SetCount(list2, num);
			CollectionsMarshal.AsSpan(list2)[0] = "Log in the person at the camera and start Avatar Capture. The model begins once full-resolution reconstruction samples attach to accepted face-lock frames.";
			obj.Findings = list2;
			return obj;
		}
		AvatarIdentityModel identity = BuildIdentity(observationSet, list, repository);
		AvatarExpressionModel expression = BuildExpression(list);
		AvatarPoseCoverage avatarPoseCoverage = BuildCoverage(list);
		AvatarModelConvergence avatarModelConvergence = BuildConvergence(list, identity, avatarPoseCoverage);
		List<string> findings = BuildFindings(list, identity, expression, avatarPoseCoverage);
		return new AvatarModel
		{
			SubjectId = observationSet.SubjectId,
			SubjectDisplayName = observationSet.SubjectDisplayName,
			SourceObservationRevision = observationSet.Revision,
			Status = (avatarModelConvergence.IsMatureCandidate ? "avatar model is a mature candidate; continue collecting only when new evidence improves it" : "avatar model is accumulating ranked multi-angle reconstruction evidence"),
			Identity = identity,
			Expression = expression,
			PoseCoverage = avatarPoseCoverage,
			Convergence = avatarModelConvergence,
			RecentSamples = (from observation in list.OrderByDescending((AvatarObservation observation) => observation.CapturedAtUtc).Take(24)
				select CreateSampleSummary(observation, repository.GetImagePath(observationSet, observation))).ToList(),
			Findings = findings
		};
	}

	public static AvatarModel CreateWaiting(AvatarObservationDataset observationSet)
	{
		AvatarModel obj = new AvatarModel
		{
			SubjectId = observationSet.SubjectId,
			SubjectDisplayName = observationSet.SubjectDisplayName,
			SourceObservationRevision = observationSet.Revision,
			Status = "avatar model will be calculated after user login"
		};
		int num = 1;
		List<string> list = new List<string>(num);
		CollectionsMarshal.SetCount(list, num);
		CollectionsMarshal.AsSpan(list)[0] = "Log in this avatar user to calculate the retained reconstruction observations before capture starts.";
		obj.Findings = list;
		return obj;
	}

	public static AvatarModel UpdateIncrementally(AvatarObservationDataset observationSet, AvatarObservationRepository repository, AvatarModel previousModel, IReadOnlyList<AvatarObservationWriteResult> writeResults)
	{
		ArgumentNullException.ThrowIfNull(observationSet, "observationSet");
		ArgumentNullException.ThrowIfNull(repository, "repository");
		ArgumentNullException.ThrowIfNull(previousModel, "previousModel");
		ArgumentNullException.ThrowIfNull(writeResults, "writeResults");
		if (!string.Equals(previousModel.SchemaVersion, "avatar-model-v9-multiframe-identity-mapping", StringComparison.Ordinal))
		{
			throw new InvalidDataException("The stored avatar model must be recalculated at login before incremental capture can continue.");
		}
		List<AvatarObservationWriteResult> list = writeResults.Where((AvatarObservationWriteResult result) => result.Accepted && (object)result.AcceptedObservation != null).ToList();
		if (list.Count == 0)
		{
			throw new InvalidDataException("The accepted batch did not carry incremental avatar geometry.");
		}
		List<AvatarObservation> list2 = (from observation in observationSet.Observations.Where(HasIdentityGeometry)
			orderby observation.CapturedAtUtc
			select observation).ToList();
		if (list2.Count == 0)
		{
			return CreateWaiting(observationSet);
		}
		AvatarIdentityModel identity = UpdateIdentityIncrementally(observationSet, list2, previousModel.Identity, list);
		AvatarExpressionModel expression = BuildExpression(list2);
		AvatarPoseCoverage avatarPoseCoverage = BuildCoverage(list2);
		AvatarModelConvergence avatarModelConvergence = BuildConvergence(list2, identity, avatarPoseCoverage);
		return new AvatarModel
		{
			CreatedAtUtc = previousModel.CreatedAtUtc,
			UpdatedAtUtc = DateTime.UtcNow,
			SubjectId = observationSet.SubjectId,
			SubjectDisplayName = observationSet.SubjectDisplayName,
			SourceObservationRevision = observationSet.Revision,
			Status = (avatarModelConvergence.IsMatureCandidate ? "avatar model is a mature candidate; continue collecting only when new evidence improves it" : "avatar model is accumulating ranked multi-angle reconstruction evidence"),
			Identity = identity,
			Expression = expression,
			PoseCoverage = avatarPoseCoverage,
			Convergence = avatarModelConvergence,
			RecentSamples = (from observation in list2.OrderByDescending((AvatarObservation observation) => observation.CapturedAtUtc).Take(24)
				select CreateSampleSummary(observation, repository.GetImagePath(observationSet, observation))).ToList(),
			Findings = BuildFindings(list2, identity, expression, avatarPoseCoverage)
		};
	}

	public static AvatarModel ApplyIdentityMapping(AvatarModel model, AvatarIdentityMappingUpdate mapping)
	{
		ArgumentNullException.ThrowIfNull(model, "model");
		ArgumentNullException.ThrowIfNull(mapping, "mapping");
		AvatarIdentityModel identity = model.Identity;
		bool flag = mapping.Accepted && mapping.ShapeCoefficients.Count > 0 && mapping.CanonicalIdentityVertices.Count > 0;
		List<double> mappedShapeCoefficients = (flag ? mapping.ShapeCoefficients.ToList() : identity.MappedShapeCoefficients.ToList());
		List<FaceMeshLandmarkPoint> list = (flag ? NormalizeIdentityVertices(mapping.CanonicalIdentityVertices, "mapped FLAME identity").ToList() : identity.MappedDenseVertices.ToList());
		List<string> list2 = model.Findings.Where((string finding) => !finding.StartsWith("Identity mapper:", StringComparison.Ordinal)).ToList();
		list2.Insert(0, "Identity mapper: " + mapping.Status);
		return new AvatarModel
		{
			CreatedAtUtc = model.CreatedAtUtc,
			UpdatedAtUtc = DateTime.UtcNow,
			SubjectId = model.SubjectId,
			SubjectDisplayName = model.SubjectDisplayName,
			Status = model.Status,
			StoragePolicy = model.StoragePolicy,
			SourceObservationRevision = model.SourceObservationRevision,
			Identity = new AvatarIdentityModel
			{
				CoordinateSpace = identity.CoordinateSpace,
				SampleCount = identity.SampleCount,
				ConfidencePercent = identity.ConfidencePercent,
				DenseVertexCount = (flag ? list.Count : identity.DenseVertexCount),
				DenseTopologyEdgeCount = identity.DenseTopologyEdgeCount,
				ShapeCoefficientCount = identity.ShapeCoefficientCount,
				ShapeCoefficientStabilityPercent = identity.ShapeCoefficientStabilityPercent,
				TotalIdentityWeight = identity.TotalIdentityWeight,
				MeanShapeCoefficients = identity.MeanShapeCoefficients.ToList(),
				ShapeCoefficientWeights = identity.ShapeCoefficientWeights.ToList(),
				MeanShapeCoefficientSquares = identity.MeanShapeCoefficientSquares.ToList(),
				MeanDenseVertices = identity.MeanDenseVertices.ToList(),
				MappingStatus = mapping.Status,
				MappingUpdatedAtUtc = mapping.UpdatedAtUtc,
				MappingFrameCount = mapping.FrameCount,
				MappingIterationCount = mapping.IterationCount,
				MappingInitialLandmarkRmsePercent = mapping.InitialLandmarkRmsePercent,
				MappingFinalLandmarkRmsePercent = mapping.FinalLandmarkRmsePercent,
				MappingImprovementPercent = mapping.ImprovementPercent,
				GenericIdentityDisplacementPercent = (flag ? mapping.GenericIdentityDisplacementPercent : identity.GenericIdentityDisplacementPercent),
				MappedShapeCoefficients = mappedShapeCoefficients,
				MappedDenseVertices = list,
				TopologyEdges = identity.TopologyEdges.ToList(),
				RegionConfidence = identity.RegionConfidence.ToList()
			},
			Expression = model.Expression,
			PoseCoverage = model.PoseCoverage,
			Convergence = model.Convergence,
			RecentSamples = model.RecentSamples.ToList(),
			Findings = list2
		};
	}

	private static AvatarIdentityModel BuildIdentity(AvatarObservationDataset dataset, IReadOnlyList<AvatarObservation> observations, AvatarObservationRepository repository)
	{
		Dictionary<int, WeightedPointAccumulator> dictionary = new Dictionary<int, WeightedPointAccumulator>();
		List<WeightedCoefficientAccumulator> list = CreateCoefficientAccumulators(observations.Max((AvatarObservation avatarObservation) => avatarObservation.ShapeCoefficients.Count));
		double totalWeight = 0.0;
		double confidenceWeight = 0.0;
		foreach (WeightedObservation item in (from avatarObservation in observations
			select new WeightedObservation(avatarObservation, Math.Clamp(avatarObservation.IdentityWeightPercent, 0.0, 100.0) / 100.0) into item
			where item.Weight > 0.001
			select item).ToList())
		{
			AvatarObservation observation = repository.LoadObservation(dataset, item.Observation);
			AddIdentityGeometry(new WeightedObservation(observation, item.Weight), NormalizeIdentityVertices(observation), dictionary, list, ref totalWeight, ref confidenceWeight);
		}
		List<FaceMeshLandmarkPoint> list2 = CreateMeanVertices(dictionary.Values);
		double num = ((totalWeight <= 0.0) ? 0.0 : (confidenceWeight / totalWeight));
		return new AvatarIdentityModel
		{
			SampleCount = observations.Count,
			ConfidencePercent = Round(num),
			DenseVertexCount = list2.Count,
			DenseTopologyEdgeCount = dataset.DenseTopologyEdges.Count,
			ShapeCoefficientCount = list.Count,
			ShapeCoefficientStabilityPercent = Round(CalculateCoefficientStability(list)),
			TotalIdentityWeight = totalWeight,
			MeanShapeCoefficients = list.Select((WeightedCoefficientAccumulator accumulator) => accumulator.Mean).ToList(),
			ShapeCoefficientWeights = list.Select((WeightedCoefficientAccumulator accumulator) => accumulator.Weight).ToList(),
			MeanShapeCoefficientSquares = list.Select((WeightedCoefficientAccumulator accumulator) => accumulator.MeanSquare).ToList(),
			MeanDenseVertices = list2,
			TopologyEdges = dataset.DenseTopologyEdges.ToList(),
			RegionConfidence = BuildRegionConfidence(observations, num)
		};
	}

	private static AvatarIdentityModel UpdateIdentityIncrementally(AvatarObservationDataset dataset, IReadOnlyList<AvatarObservation> observations, AvatarIdentityModel previous, IReadOnlyList<AvatarObservationWriteResult> acceptedChanges)
	{
		bool flag = previous.MeanDenseVertices.Count == 0 || previous.TotalIdentityWeight <= 0.0;
		IReadOnlyList<FaceMeshLandmarkPoint> readOnlyList;
		if (flag)
		{
			HashSet<string> acceptedIds = acceptedChanges.Select((AvatarObservationWriteResult change) => change.AcceptedObservation.ObservationId).ToHashSet<string>(StringComparer.Ordinal);
			if (observations.Any((AvatarObservation observation) => !acceptedIds.Contains(observation.ObservationId)))
			{
				throw new InvalidDataException("The stored avatar model has no incremental identity accumulator. Log in again to recalculate it.");
			}
			readOnlyList = NormalizeIdentityVertices(acceptedChanges.Select((AvatarObservationWriteResult change) => change.AcceptedObservation).FirstOrDefault((AvatarObservation observation) => IdentityWeight(observation) > 0.001) ?? throw new InvalidDataException("The first accepted batch did not contain identity-weighted geometry."));
		}
		else
		{
			readOnlyList = previous.MeanDenseVertices;
		}
		int count = readOnlyList.Count;
		int[] array = new int[count];
		double[] array2 = new double[count];
		double[] array3 = new double[count];
		double[] array4 = new double[count];
		Dictionary<int, int> dictionary = new Dictionary<int, int>(count);
		for (int num = 0; num < count; num++)
		{
			FaceMeshLandmarkPoint faceMeshLandmarkPoint = readOnlyList[num];
			array[num] = faceMeshLandmarkPoint.Index;
			dictionary[faceMeshLandmarkPoint.Index] = num;
			if (!flag)
			{
				array2[num] = faceMeshLandmarkPoint.X * previous.TotalIdentityWeight;
				array3[num] = faceMeshLandmarkPoint.Y * previous.TotalIdentityWeight;
				array4[num] = faceMeshLandmarkPoint.Z * previous.TotalIdentityWeight;
			}
		}
		int num2 = Math.Max(observations.Max((AvatarObservation observation) => observation.ShapeCoefficients.Count), previous.MeanShapeCoefficients.Count);
		double[] array5 = new double[num2];
		double[] array6 = new double[num2];
		double[] array7 = new double[num2];
		for (int num3 = 0; num3 < num2; num3++)
		{
			double num4 = ValueAt(previous.ShapeCoefficientWeights, num3);
			double num5 = ValueAt(previous.MeanShapeCoefficients, num3);
			double num6 = ValueAt(previous.MeanShapeCoefficientSquares, num3);
			array5[num3] = num4;
			array6[num3] = num5 * num4;
			array7[num3] = num6 * num4;
		}
		double totalWeight = (flag ? 0.0 : previous.TotalIdentityWeight);
		foreach (AvatarObservationWriteResult acceptedChange in acceptedChanges)
		{
			AvatarObservation replacedObservation = acceptedChange.ReplacedObservation;
			if ((object)replacedObservation != null)
			{
				ApplyIdentityDelta(replacedObservation, 0.0 - IdentityWeight(replacedObservation), dictionary, array2, array3, array4, array5, array6, array7, ref totalWeight);
			}
			ApplyIdentityDelta(acceptedChange.AcceptedObservation, IdentityWeight(acceptedChange.AcceptedObservation), dictionary, array2, array3, array4, array5, array6, array7, ref totalWeight);
		}
		if (totalWeight <= 1E-06)
		{
			throw new InvalidDataException("The incremental identity accumulator reached zero weight. Log in again to recalculate it.");
		}
		List<FaceMeshLandmarkPoint> list = new List<FaceMeshLandmarkPoint>(count);
		for (int num7 = 0; num7 < count; num7++)
		{
			list.Add(new FaceMeshLandmarkPoint
			{
				Index = array[num7],
				X = array2[num7] / totalWeight,
				Y = array3[num7] / totalWeight,
				Z = array4[num7] / totalWeight
			});
		}
		List<double> list2 = new List<double>(num2);
		List<double> list3 = new List<double>(num2);
		for (int num8 = 0; num8 < num2; num8++)
		{
			double num9 = array5[num8];
			list2.Add((num9 <= 0.0) ? 0.0 : (array6[num8] / num9));
			list3.Add((num9 <= 0.0) ? 0.0 : (array7[num8] / num9));
		}
		double num10 = observations.Sum((AvatarObservation observation) => observation.ReconstructionConfidencePercent * IdentityWeight(observation));
		double num11 = ((totalWeight <= 0.0) ? 0.0 : (num10 / totalWeight));
		return new AvatarIdentityModel
		{
			SampleCount = observations.Count,
			ConfidencePercent = Round(num11),
			DenseVertexCount = list.Count,
			DenseTopologyEdgeCount = dataset.DenseTopologyEdges.Count,
			ShapeCoefficientCount = num2,
			ShapeCoefficientStabilityPercent = Round(CalculateCoefficientStability(array5, list2, list3)),
			TotalIdentityWeight = totalWeight,
			MeanShapeCoefficients = list2,
			ShapeCoefficientWeights = array5.ToList(),
			MeanShapeCoefficientSquares = list3,
			MeanDenseVertices = list,
			MappingStatus = previous.MappingStatus,
			MappingUpdatedAtUtc = previous.MappingUpdatedAtUtc,
			MappingFrameCount = previous.MappingFrameCount,
			MappingIterationCount = previous.MappingIterationCount,
			MappingInitialLandmarkRmsePercent = previous.MappingInitialLandmarkRmsePercent,
			MappingFinalLandmarkRmsePercent = previous.MappingFinalLandmarkRmsePercent,
			MappingImprovementPercent = previous.MappingImprovementPercent,
			GenericIdentityDisplacementPercent = previous.GenericIdentityDisplacementPercent,
			MappedShapeCoefficients = previous.MappedShapeCoefficients.ToList(),
			MappedDenseVertices = previous.MappedDenseVertices.ToList(),
			TopologyEdges = dataset.DenseTopologyEdges.ToList(),
			RegionConfidence = BuildRegionConfidence(observations, num11)
		};
	}

	private static void ApplyIdentityDelta(AvatarObservation observation, double signedWeight, IReadOnlyDictionary<int, int> vertexIndexToPosition, double[] xSums, double[] ySums, double[] zSums, double[] coefficientWeights, double[] coefficientSums, double[] coefficientSquareSums, ref double totalWeight)
	{
		if (Math.Abs(signedWeight) <= 0.001)
		{
			return;
		}
		IReadOnlyList<FaceMeshLandmarkPoint> readOnlyList = NormalizeIdentityVertices(observation);
		if (readOnlyList.Count != vertexIndexToPosition.Count)
		{
			throw new InvalidDataException("Incremental reconstruction geometry does not match the calculated avatar topology. Log in again to recalculate it.");
		}
		foreach (FaceMeshLandmarkPoint item in readOnlyList)
		{
			if (!vertexIndexToPosition.TryGetValue(item.Index, out var value))
			{
				throw new InvalidDataException("Incremental reconstruction vertex indexes do not match the calculated avatar topology.");
			}
			xSums[value] += item.X * signedWeight;
			ySums[value] += item.Y * signedWeight;
			zSums[value] += item.Z * signedWeight;
		}
		int num = Math.Min(observation.ShapeCoefficients.Count, coefficientWeights.Length);
		for (int i = 0; i < num; i++)
		{
			double num2 = observation.ShapeCoefficients[i];
			if (double.IsFinite(num2))
			{
				coefficientWeights[i] += signedWeight;
				coefficientSums[i] += num2 * signedWeight;
				coefficientSquareSums[i] += num2 * num2 * signedWeight;
				if (coefficientWeights[i] < 1E-06)
				{
					coefficientWeights[i] = 0.0;
					coefficientSums[i] = 0.0;
					coefficientSquareSums[i] = 0.0;
				}
			}
		}
		totalWeight += signedWeight;
	}

	private static double IdentityWeight(AvatarObservation observation)
	{
		return Math.Clamp(observation.IdentityWeightPercent, 0.0, 100.0) / 100.0;
	}

	private static double ValueAt(IReadOnlyList<double> values, int index)
	{
		if (index >= values.Count || !double.IsFinite(values[index]))
		{
			return 0.0;
		}
		return values[index];
	}

	private static AvatarExpressionModel BuildExpression(IReadOnlyList<AvatarObservation> observations)
	{
		int num = observations.Max((AvatarObservation observation) => observation.ExpressionCoefficients.Count);
		List<WeightedCoefficientAccumulator> list = CreateCoefficientAccumulators(num);
		double num2 = 0.0;
		foreach (AvatarObservation observation in observations)
		{
			double num3 = Math.Clamp(observation.ExpressionWeightPercent, 0.0, 100.0) / 100.0;
			if (!(num3 <= 0.001))
			{
				AddCoefficients(list, observation.ExpressionCoefficients, num3);
				num2 += num3;
			}
		}
		double value = ((observations.Count == 0) ? 0.0 : observations.Select((AvatarObservation observation) => CalculateExpressionEnergy(observation.ExpressionCoefficients)).Average());
		return new AvatarExpressionModel
		{
			SampleCount = observations.Count,
			ConfidencePercent = Round(observations.Average((AvatarObservation observation) => observation.ExpressionWeightPercent)),
			ExpressionCoefficientCount = num,
			ExpressionEnergyPercent = Round(value),
			MeanExpressionCoefficients = list.Select((WeightedCoefficientAccumulator accumulator) => Round(accumulator.Mean)).ToList(),
			ExpressionRanges = list.Select((WeightedCoefficientAccumulator accumulator, int index) => new AvatarCoefficientRange
			{
				Index = index,
				Minimum = Round(accumulator.Minimum),
				Maximum = Round(accumulator.Maximum),
				Range = Round(accumulator.Range)
			}).ToList(),
			Buckets = BuildExpressionBuckets(observations)
		};
	}

	private static AvatarPoseCoverage BuildCoverage(IReadOnlyList<AvatarObservation> observations)
	{
		List<double> values = observations.Select((AvatarObservation observation) => observation.ARotationAroundXDegrees).ToList();
		List<double> values2 = observations.Select((AvatarObservation observation) => observation.BRotationAroundYDegrees).ToList();
		List<double> values3 = observations.Select((AvatarObservation observation) => observation.CRotationAroundZDegrees).ToList();
		List<double> list = (from observation in observations
			select observation.RelativeDistanceScale into num11
			where num11.HasValue && num11.GetValueOrDefault() > 0.0
			select num11.Value).ToList();
		int num = observations.Count(IsFront);
		int num2 = observations.Count((AvatarObservation observation) => observation.BRotationAroundYDegrees <= -10.0);
		int num3 = observations.Count((AvatarObservation observation) => observation.BRotationAroundYDegrees >= 10.0);
		int num4 = observations.Count((AvatarObservation observation) => observation.ARotationAroundXDegrees <= -10.0);
		int num5 = observations.Count((AvatarObservation observation) => observation.ARotationAroundXDegrees >= 10.0);
		int num6 = observations.Count((AvatarObservation observation) => observation.CRotationAroundZDegrees <= -10.0);
		int num7 = observations.Count((AvatarObservation observation) => observation.CRotationAroundZDegrees >= 10.0);
		int num8 = 0;
		int num9 = 0;
		if (list.Count > 0)
		{
			double median = list.Order().ElementAt(list.Count / 2);
			num8 = observations.Count(delegate(AvatarObservation observation)
			{
				double? relativeDistanceScale = observation.RelativeDistanceScale;
				if (relativeDistanceScale.HasValue)
				{
					double valueOrDefault = relativeDistanceScale.GetValueOrDefault();
					return valueOrDefault >= median * 1.08;
				}
				return false;
			});
			num9 = observations.Count(delegate(AvatarObservation observation)
			{
				double? relativeDistanceScale = observation.RelativeDistanceScale;
				if (relativeDistanceScale.HasValue)
				{
					double valueOrDefault = relativeDistanceScale.GetValueOrDefault();
					return valueOrDefault <= median * 0.92;
				}
				return false;
			});
		}
		int num10 = new bool[9]
		{
			num > 0,
			num2 > 0,
			num3 > 0,
			num4 > 0,
			num5 > 0,
			num6 > 0,
			num7 > 0,
			num8 > 0,
			num9 > 0
		}.Count((bool covered) => covered);
		double value = (double)num10 / 9.0 * 100.0;
		return new AvatarPoseCoverage
		{
			TotalSampleCount = observations.Count,
			FrontSampleCount = num,
			LeftBTurnSampleCount = num2,
			RightBTurnSampleCount = num3,
			NegativeATiltSampleCount = num4,
			PositiveATiltSampleCount = num5,
			NegativeCTiltSampleCount = num6,
			PositiveCTiltSampleCount = num7,
			CloseZSampleCount = num8,
			FarZSampleCount = num9,
			ARangeDegrees = Round(Range(values)),
			BRangeDegrees = Round(Range(values2)),
			CRangeDegrees = Round(Range(values3)),
			ZScaleRangePercent = Round(Range(list) * 100.0),
			CoveragePercent = Round(value),
			Summary = $"{num10}/9 pose/depth buckets covered"
		};
	}

	internal static IReadOnlyList<FaceMeshLandmarkPoint> NormalizeIdentityVerticesForAudit(AvatarObservation observation)
	{
		return NormalizeIdentityVertices(observation);
	}

	private static AvatarModelConvergence BuildConvergence(IReadOnlyList<AvatarObservation> observations, AvatarIdentityModel identity, AvatarPoseCoverage coverage)
	{
		double num = Math.Clamp((double)observations.Count / 180.0 * 100.0, 0.0, 100.0);
		double num2 = ((observations.Count == 0) ? 0.0 : observations.Average((AvatarObservation observation) => observation.RetentionScorePercent));
		double num3 = identity.ShapeCoefficientStabilityPercent * 0.35 + coverage.CoveragePercent * 0.25 + num2 * 0.2 + num * 0.2;
		bool flag = observations.Count >= 120 && identity.ShapeCoefficientStabilityPercent >= 75.0 && coverage.CoveragePercent >= 77.0 && num3 >= 82.0;
		string label = (flag ? "mature candidate" : ((observations.Count < 36) ? "early collection" : ((identity.ShapeCoefficientStabilityPercent < 65.0) ? "identity still stabilizing" : ((coverage.CoveragePercent < 65.0) ? "needs broader pose coverage" : "converging"))));
		return new AvatarModelConvergence
		{
			ScorePercent = Round(num3),
			SampleAdequacyPercent = Round(num),
			QualityPercent = Round(num2),
			IsMatureCandidate = flag,
			Label = label,
			Basis = $"{observations.Count} ranked observations; {identity.ShapeCoefficientStabilityPercent:0.#}% coefficient stability; {coverage.CoveragePercent:0.#}% pose/depth coverage; {num2:0.#}% retained quality."
		};
	}

	private static IReadOnlyList<FaceMeshLandmarkPoint> NormalizeIdentityVertices(AvatarObservation observation)
	{
		List<FaceMeshLandmarkPoint> canonicalIdentityVertices = observation.CanonicalIdentityVertices;
		if (canonicalIdentityVertices.Count == 0)
		{
			throw new InvalidDataException("Observation " + observation.ObservationId + " did not contain canonical identity geometry.");
		}
		return NormalizeIdentityVertices(canonicalIdentityVertices, "observation " + observation.ObservationId);
	}

	private static IReadOnlyList<FaceMeshLandmarkPoint> NormalizeIdentityVertices(IReadOnlyList<FaceMeshLandmarkPoint> source, string sourceName)
	{
		if (source.Count == 0)
		{
			throw new InvalidDataException(sourceName + " did not contain canonical identity geometry.");
		}
		Bounds bounds = Bounds.From(source);
		double num = (bounds.MinX + bounds.MaxX) * 0.5;
		double num2 = (bounds.MinY + bounds.MaxY) * 0.5;
		double num3 = (bounds.MinZ + bounds.MaxZ) * 0.5;
		double num4 = Math.Max(0.0001, Math.Max(bounds.MaxX - bounds.MinX, bounds.MaxY - bounds.MinY));
		List<FaceMeshLandmarkPoint> list = new List<FaceMeshLandmarkPoint>(source.Count);
		foreach (FaceMeshLandmarkPoint item in source)
		{
			list.Add(new FaceMeshLandmarkPoint
			{
				Index = item.Index,
				X = Round((item.X - num) / num4),
				Y = Round((item.Y - num2) / num4),
				Z = Round((item.Z - num3) / num4)
			});
		}
		return list;
	}

	private static void AddIdentityGeometry(WeightedObservation item, IReadOnlyList<FaceMeshLandmarkPoint> vertices, IDictionary<int, WeightedPointAccumulator> vertexAccumulators, IReadOnlyList<WeightedCoefficientAccumulator> shapeAccumulators, ref double totalWeight, ref double confidenceWeight)
	{
		foreach (FaceMeshLandmarkPoint vertex in vertices)
		{
			if (!vertexAccumulators.TryGetValue(vertex.Index, out WeightedPointAccumulator value))
			{
				value = new WeightedPointAccumulator(vertex.Index);
				vertexAccumulators[vertex.Index] = value;
			}
			value.Add(vertex, item.Weight);
		}
		AddCoefficients(shapeAccumulators, item.Observation.ShapeCoefficients, item.Weight);
		totalWeight += item.Weight;
		confidenceWeight += item.Observation.ReconstructionConfidencePercent * item.Weight;
	}

	private static List<FaceMeshLandmarkPoint> CreateMeanVertices(IEnumerable<WeightedPointAccumulator> accumulators)
	{
		return (from accumulator in accumulators
			where accumulator.Weight > 0.0
			orderby accumulator.Index
			select accumulator.ToPoint()).ToList();
	}

	private static bool HasIdentityGeometry(AvatarObservation observation)
	{
		if (observation.CanonicalVertexCount >= 1000)
		{
			return !string.Equals(observation.BackendId, "deca-flame-standard-model-checkpoint-v1", StringComparison.Ordinal);
		}
		return false;
	}

	private static List<WeightedCoefficientAccumulator> CreateCoefficientAccumulators(int count)
	{
		return (from _ in Enumerable.Range(0, Math.Max(0, count))
			select new WeightedCoefficientAccumulator()).ToList();
	}

	private static void AddCoefficients(IReadOnlyList<WeightedCoefficientAccumulator> accumulators, IReadOnlyList<double> coefficients, double weight)
	{
		for (int i = 0; i < accumulators.Count && i < coefficients.Count; i++)
		{
			accumulators[i].Add(coefficients[i], weight);
		}
	}

	private static double CalculateCoefficientStability(IReadOnlyList<WeightedCoefficientAccumulator> accumulators)
	{
		if (accumulators.Count == 0)
		{
			return 0.0;
		}
		double num = Math.Sqrt(accumulators.Select((WeightedCoefficientAccumulator accumulator) => accumulator.Mean * accumulator.Mean).DefaultIfEmpty(0.0).Average());
		double num2 = Math.Sqrt(accumulators.Select((WeightedCoefficientAccumulator accumulator) => accumulator.StandardDeviation * accumulator.StandardDeviation).DefaultIfEmpty(0.0).Average());
		if (!(num + num2 <= 1E-06))
		{
			return Math.Clamp(num / (num + num2) * 100.0, 0.0, 100.0);
		}
		return 0.0;
	}

	private static double CalculateCoefficientStability(IReadOnlyList<double> weights, IReadOnlyList<double> means, IReadOnlyList<double> meanSquares)
	{
		int num = Math.Min(weights.Count, Math.Min(means.Count, meanSquares.Count));
		if (num == 0)
		{
			return 0.0;
		}
		double num2 = 0.0;
		double num3 = 0.0;
		int num4 = 0;
		for (int i = 0; i < num; i++)
		{
			if (!(weights[i] <= 0.0))
			{
				num2 += means[i] * means[i];
				num3 += Math.Max(0.0, meanSquares[i] - means[i] * means[i]);
				num4++;
			}
		}
		if (num4 == 0)
		{
			return 0.0;
		}
		double num5 = Math.Sqrt(num2 / (double)num4);
		double num6 = Math.Sqrt(num3 / (double)num4);
		if (!(num5 + num6 <= 1E-06))
		{
			return Math.Clamp(num5 / (num5 + num6) * 100.0, 0.0, 100.0);
		}
		return 0.0;
	}

	private static List<AvatarRegionConfidence> BuildRegionConfidence(IReadOnlyList<AvatarObservation> observations, double identityConfidencePercent)
	{
		double num = observations.Average((AvatarObservation observation) => observation.ReconstructionConfidencePercent);
		int num2 = 5;
		List<AvatarRegionConfidence> list = new List<AvatarRegionConfidence>(num2);
		CollectionsMarshal.SetCount(list, num2);
		Span<AvatarRegionConfidence> span = CollectionsMarshal.AsSpan(list);
		span[0] = new AvatarRegionConfidence
		{
			Region = "Face surface",
			ConfidencePercent = Round(Blend(identityConfidencePercent, num)),
			Basis = "reconstruction confidence plus identity sample weight"
		};
		span[1] = new AvatarRegionConfidence
		{
			Region = "Eyes",
			ConfidencePercent = Round(num),
			Basis = "canonical expression-free backend geometry"
		};
		span[2] = new AvatarRegionConfidence
		{
			Region = "Mouth and jaw",
			ConfidencePercent = Round(num),
			Basis = "canonical identity geometry; expression coefficients are modeled separately"
		};
		span[3] = new AvatarRegionConfidence
		{
			Region = "Eyebrows",
			ConfidencePercent = Round(num),
			Basis = "canonical expression-free backend geometry"
		};
		span[4] = new AvatarRegionConfidence
		{
			Region = "Nose, cheeks, forehead",
			ConfidencePercent = Round(num),
			Basis = "dense reconstruction topology"
		};
		return list;
	}

	private static List<AvatarExpressionBucket> BuildExpressionBuckets(IReadOnlyList<AvatarObservation> observations)
	{
		List<AvatarObservation> observations2 = observations.Where((AvatarObservation observation) => CalculateExpressionEnergy(observation.ExpressionCoefficients) < 42.0).ToList();
		List<AvatarObservation> observations3 = observations.Where((AvatarObservation observation) => CalculateExpressionEnergy(observation.ExpressionCoefficients) >= 42.0).ToList();
		List<AvatarObservation> observations4 = observations.Where((AvatarObservation observation) => observation.MouthQualityPercent >= 55.0).ToList();
		int num = 3;
		List<AvatarExpressionBucket> list = new List<AvatarExpressionBucket>(num);
		CollectionsMarshal.SetCount(list, num);
		Span<AvatarExpressionBucket> span = CollectionsMarshal.AsSpan(list);
		span[0] = CreateBucket("Relaxed / identity-friendly", observations2, "Frames with lower expression energy; strongest candidates for base identity.");
		span[1] = CreateBucket("Expression range", observations3, "Frames with higher expression energy; useful for motion without reshaping the identity model.");
		span[2] = CreateBucket("Mouth and jaw evidence", observations4, "Frames with usable mouth/jaw evidence for speech, jaw droop, and open-mouth range.");
		return list;
	}

	private static AvatarExpressionBucket CreateBucket(string name, IReadOnlyList<AvatarObservation> observations, string meaning)
	{
		return new AvatarExpressionBucket
		{
			Name = name,
			SampleCount = observations.Count,
			AverageEnergyPercent = ((observations.Count == 0) ? 0.0 : Round(observations.Select((AvatarObservation observation) => CalculateExpressionEnergy(observation.ExpressionCoefficients)).Average())),
			Meaning = meaning
		};
	}

	private static List<string> BuildFindings(IReadOnlyList<AvatarObservation> observations, AvatarIdentityModel identity, AvatarExpressionModel expression, AvatarPoseCoverage coverage)
	{
		List<string> list = new List<string>
		{
			$"Stored {observations.Count} ranked reconstruction observation(s); identity uses direct canonical expression-free geometry and the expression model stays separate.",
			$"Current dense identity preview has {identity.DenseVertexCount:n0} averaged vertices and {identity.DenseTopologyEdgeCount:n0} topology edges."
		};
		if (observations.Count < 12)
		{
			list.Add("Collect more relaxed front-facing and small-turn samples before trusting fine facial proportions.");
		}
		if (coverage.LeftBTurnSampleCount == 0 || coverage.RightBTurnSampleCount == 0)
		{
			list.Add("Need both left and right B-axis head turns to improve cheek, nose, and side-depth confidence.");
		}
		if (coverage.NegativeATiltSampleCount == 0 || coverage.PositiveATiltSampleCount == 0)
		{
			list.Add("Need gentle A-axis up/down tilt samples to improve forehead, chin, and nose-depth confidence.");
		}
		if (coverage.CloseZSampleCount == 0 || coverage.FarZSampleCount == 0)
		{
			list.Add("Need closer/farther Z samples to verify scale instead of treating camera zoom or distance as face shape.");
		}
		if (expression.Buckets.Any((AvatarExpressionBucket bucket) => bucket.Name == "Expression range" && bucket.SampleCount == 0))
		{
			list.Add("Expression model is still mostly relaxed; natural talking, blinks, and jaw movement will improve motion coverage.");
		}
		return list;
	}

	private static AvatarModelSampleSummary CreateSampleSummary(AvatarObservation observation, string? sourceImagePath)
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
			VertexCount = ((observation.CanonicalVertexCount > 0) ? observation.CanonicalVertexCount : observation.DenseVertexCount),
			IdentityUse = observation.IdentityUse,
			SourceImageUri = (string.IsNullOrWhiteSpace(sourceImagePath) ? "" : new Uri(sourceImagePath).AbsoluteUri)
		};
	}

	private static bool IsFront(AvatarObservation observation)
	{
		if (Math.Abs(observation.ARotationAroundXDegrees) < 10.0 && Math.Abs(observation.BRotationAroundYDegrees) < 10.0)
		{
			return Math.Abs(observation.CRotationAroundZDegrees) < 10.0;
		}
		return false;
	}

	private static double CalculateExpressionEnergy(IReadOnlyList<double> coefficients)
	{
		if (coefficients.Count == 0)
		{
			return 0.0;
		}
		return Math.Clamp(coefficients.Select((double coefficient) => Math.Abs(coefficient)).Average() * 100.0, 0.0, 100.0);
	}

	private static double Range(IReadOnlyList<double> values)
	{
		if (values.Count != 0)
		{
			return values.Max() - values.Min();
		}
		return 0.0;
	}

	private static double Blend(double first, double second)
	{
		return first * 0.55 + second * 0.45;
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
