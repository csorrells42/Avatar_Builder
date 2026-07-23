using System;
using System.Collections.Generic;
using System.Linq;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Deca.StandardModel;

public static class AvatarStandardIdentityFusion
{
	public static AvatarStandardIdentityFusionResult Fuse(IReadOnlyDictionary<string, AvatarStandardPoseSample> poseAtlas, IReadOnlyList<double>? legacyShapeCoefficients = null, IReadOnlyList<FaceMeshLandmarkPoint>? legacyCanonicalVertices = null)
	{
		ArgumentNullException.ThrowIfNull(poseAtlas, "poseAtlas");
		List<AvatarStandardPoseSample> list = poseAtlas.Values.Where(AvatarStandardPoseGrid.HasCompleteIdentityEvidence).OrderBy<AvatarStandardPoseSample, string>((AvatarStandardPoseSample sample) => sample.DirectionKey, StringComparer.Ordinal).ToList();
		List<IReadOnlyList<double>> list2 = list.Select((AvatarStandardPoseSample sample) => sample.IdentityShapeCoefficients).ToList();
		List<IReadOnlyList<FaceMeshLandmarkPoint>> list3 = SelectConsistentVertexSets(list);
		bool usesLegacyAnchor = false;
		if (list.Count < poseAtlas.Count && IsCompleteShape(legacyShapeCoefficients) && IsCompleteVertices(legacyCanonicalVertices) && (list3.Count == 0 || list3[0].Count == legacyCanonicalVertices.Count))
		{
			list2.Add(legacyShapeCoefficients);
			list3.Add(legacyCanonicalVertices);
			usesLegacyAnchor = true;
		}
		if (list2.Count == 0 || list3.Count == 0)
		{
			throw new InvalidOperationException("No complete Standard Model identity evidence was available to fuse.");
		}
		return new AvatarStandardIdentityFusionResult(AverageCoefficients(list2), AverageVertices(list3), list.Count, list2.Count, usesLegacyAnchor);
	}

	private static List<IReadOnlyList<FaceMeshLandmarkPoint>> SelectConsistentVertexSets(IReadOnlyList<AvatarStandardPoseSample> poseEvidence)
	{
		if (poseEvidence.Count == 0)
		{
			return new List<IReadOnlyList<FaceMeshLandmarkPoint>>();
		}
		int expectedCount = (from sample in poseEvidence
			group sample by sample.CanonicalIdentityVertices.Count into @group
			orderby @group.Count() descending, @group.Key descending
			select @group.Key).First();
		return (from sample in poseEvidence
			where sample.CanonicalIdentityVertices.Count == expectedCount
			select sample.CanonicalIdentityVertices).ToList();
	}

	private static IReadOnlyList<double> AverageCoefficients(IReadOnlyList<IReadOnlyList<double>> vectors)
	{
		double[] array = new double[100];
		foreach (IReadOnlyList<double> vector in vectors)
		{
			for (int i = 0; i < array.Length; i++)
			{
				array[i] += vector[i];
			}
		}
		int count = vectors.Count;
		for (int j = 0; j < array.Length; j++)
		{
			array[j] /= count;
		}
		return array;
	}

	private static IReadOnlyList<FaceMeshLandmarkPoint> AverageVertices(IReadOnlyList<IReadOnlyList<FaceMeshLandmarkPoint>> vertexSets)
	{
		int count = vertexSets[0].Count;
		FaceMeshLandmarkPoint[] array = new FaceMeshLandmarkPoint[count];
		for (int i = 0; i < count; i++)
		{
			double num = 0.0;
			double num2 = 0.0;
			double num3 = 0.0;
			foreach (IReadOnlyList<FaceMeshLandmarkPoint> vertexSet in vertexSets)
			{
				num += vertexSet[i].X;
				num2 += vertexSet[i].Y;
				num3 += vertexSet[i].Z;
			}
			int count2 = vertexSets.Count;
			array[i] = new FaceMeshLandmarkPoint
			{
				Index = vertexSets[0][i].Index,
				X = num / (double)count2,
				Y = num2 / (double)count2,
				Z = num3 / (double)count2
			};
		}
		return array;
	}

	private static bool IsCompleteShape(IReadOnlyList<double>? values)
	{
		if (values != null && values.Count == 100)
		{
			return values.All(double.IsFinite);
		}
		return false;
	}

	private static bool IsCompleteVertices(IReadOnlyList<FaceMeshLandmarkPoint>? values)
	{
		if (values != null && values.Count >= 1000)
		{
			return values.All((FaceMeshLandmarkPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z));
		}
		return false;
	}
}
