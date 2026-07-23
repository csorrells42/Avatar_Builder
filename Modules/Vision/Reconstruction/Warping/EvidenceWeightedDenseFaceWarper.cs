using System;
using System.Collections.Generic;
using System.Linq;

namespace AvatarBuilder.Modules.Vision.Reconstruction.Warping;

public static class EvidenceWeightedDenseFaceWarper
{
	private const double MinimumWeight = 1E-10;

	private const double AnchorDistanceFloor = 0.0025;

	private const double MaximumDisplacement = 0.4;

	private const double ClampTolerance = 1E-06;

	public static DenseFaceWarpResult Warp(DenseFaceWarpInput input)
	{
		ArgumentNullException.ThrowIfNull(input, "input");
		if (input.SourceVertices.Count == 0)
		{
			return Empty(input, "Dense warp rejected an empty source mesh.");
		}
		DenseFaceWarpControlPoint[] array = input.ControlPoints.Where((DenseFaceWarpControlPoint point) => point.Confidence >= 0.05 && point.InfluenceRadius > 0.0 && IsFinite(point.Source) && IsFinite(point.Target)).ToArray();
		if (array.Length < 12)
		{
			return Empty(input, $"Dense warp needs at least 12 trusted semantic controls; {array.Length} were available.");
		}
		DenseFaceWarpVertex[] array2 = new DenseFaceWarpVertex[input.SourceVertices.Count];
		double[] array3 = new double[input.SourceVertices.Count];
		double num = 0.0;
		for (int num2 = 0; num2 < input.SourceVertices.Count; num2++)
		{
			DenseFaceWarpVertex denseFaceWarpVertex = input.SourceVertices[num2];
			double num3 = 0.0;
			double num4 = 0.0;
			double num5 = 0.0;
			double num6 = 0.0;
			double num7 = 0.0;
			foreach (DenseFaceWarpControlPoint denseFaceWarpControlPoint in array)
			{
				double num9 = Distance(denseFaceWarpVertex, denseFaceWarpControlPoint.Source);
				double num10 = num9 / denseFaceWarpControlPoint.InfluenceRadius;
				if (!(num10 >= 1.0))
				{
					double num11 = WendlandC2(num10);
					double num12 = 1.0 / (0.0025 + num9 * num9);
					double num13 = denseFaceWarpControlPoint.Confidence * num11 * num12;
					num3 += (denseFaceWarpControlPoint.Target.X - denseFaceWarpControlPoint.Source.X) * num13;
					num4 += (denseFaceWarpControlPoint.Target.Y - denseFaceWarpControlPoint.Source.Y) * num13;
					num5 += (denseFaceWarpControlPoint.Target.Z - denseFaceWarpControlPoint.Source.Z) * num13;
					num6 += num13;
					num7 += denseFaceWarpControlPoint.Confidence * num11;
				}
			}
			double num14 = 1.0 - Math.Exp(-2.5 * num7);
			double x = ((num6 <= 1E-10) ? 0.0 : (num3 / num6 * num14));
			double y = ((num6 <= 1E-10) ? 0.0 : (num4 / num6 * num14));
			double z = ((num6 <= 1E-10) ? 0.0 : (num5 / num6 * num14));
			ClampLength(ref x, ref y, ref z, 0.4);
			num = Math.Max(num, array3[num2] = Math.Sqrt(x * x + y * y + z * z));
			array2[num2] = new DenseFaceWarpVertex
			{
				Index = denseFaceWarpVertex.Index,
				X = denseFaceWarpVertex.X + x,
				Y = denseFaceWarpVertex.Y + y,
				Z = denseFaceWarpVertex.Z + z
			};
		}
		int[] anchorVertexIndices = FindAnchorVertexIndices(array, input.SourceVertices);
		double sourceAnchorRms = CalculateAnchorRms(array, input.SourceVertices, anchorVertexIndices);
		double warpedAnchorRms = CalculateAnchorRms(array, array2, anchorVertexIndices);
		Array.Sort(array3);
		int num15 = array3.Count((double displacement) => displacement >= 0.39999900000000005);
		return new DenseFaceWarpResult
		{
			SubjectId = input.SubjectId,
			SubjectDisplayName = input.SubjectDisplayName,
			CreatedAtUtc = input.CreatedAtUtc,
			Status = $"Warped {array2.Length:n0} dense 3DDFA vertices from {array.Length} confidence-gated MediaPipe controls.",
			SourceVertexCount = input.SourceVertices.Count,
			AppliedControlPointCount = array.Length,
			SourceAnchorRms = sourceAnchorRms,
			WarpedAnchorRms = warpedAnchorRms,
			MaximumAppliedDisplacement = num,
			MedianAppliedDisplacement = Percentile(array3, 0.5),
			Percentile95AppliedDisplacement = Percentile(array3, 0.95),
			SafetyClampVertexPercent = 100.0 * (double)num15 / (double)array3.Length,
			SourceVertices = input.SourceVertices,
			WarpedVertices = array2,
			TopologyEdges = input.TopologyEdges,
			ControlPoints = array
		};
	}

	private static DenseFaceWarpResult Empty(DenseFaceWarpInput input, string status)
	{
		return new DenseFaceWarpResult
		{
			SubjectId = input.SubjectId,
			SubjectDisplayName = input.SubjectDisplayName,
			CreatedAtUtc = input.CreatedAtUtc,
			Status = status,
			SourceVertexCount = input.SourceVertices.Count,
			SourceVertices = input.SourceVertices,
			TopologyEdges = input.TopologyEdges,
			ControlPoints = input.ControlPoints
		};
	}

	private static double CalculateAnchorRms(IReadOnlyList<DenseFaceWarpControlPoint> controls, IReadOnlyList<DenseFaceWarpVertex> vertices, IReadOnlyList<int> anchorVertexIndices)
	{
		double num = 0.0;
		double num2 = 0.0;
		for (int i = 0; i < controls.Count; i++)
		{
			DenseFaceWarpControlPoint denseFaceWarpControlPoint = controls[i];
			DenseFaceWarpVertex denseFaceWarpVertex = vertices[anchorVertexIndices[i]];
			double num3 = denseFaceWarpVertex.X - denseFaceWarpControlPoint.Target.X;
			double num4 = denseFaceWarpVertex.Y - denseFaceWarpControlPoint.Target.Y;
			double num5 = denseFaceWarpVertex.Z - denseFaceWarpControlPoint.Target.Z;
			num += (num3 * num3 + num4 * num4 + num5 * num5) * denseFaceWarpControlPoint.Confidence;
			num2 += denseFaceWarpControlPoint.Confidence;
		}
		if (!(num2 <= 1E-10))
		{
			return Math.Sqrt(num / num2);
		}
		return 0.0;
	}

	private static int[] FindAnchorVertexIndices(IReadOnlyList<DenseFaceWarpControlPoint> controls, IReadOnlyList<DenseFaceWarpVertex> vertices)
	{
		int[] array = new int[controls.Count];
		for (int i = 0; i < controls.Count; i++)
		{
			array[i] = FindNearestIndex(vertices, controls[i].Source);
		}
		return array;
	}

	private static int FindNearestIndex(IReadOnlyList<DenseFaceWarpVertex> vertices, DenseFaceWarpVertex target)
	{
		int result = 0;
		double num = DistanceSquared(vertices[0], target);
		for (int i = 1; i < vertices.Count; i++)
		{
			double num2 = DistanceSquared(vertices[i], target);
			if (num2 < num)
			{
				result = i;
				num = num2;
			}
		}
		return result;
	}

	private static double WendlandC2(double normalizedDistance)
	{
		double num = 1.0 - Math.Clamp(normalizedDistance, 0.0, 1.0);
		double num2 = num * num;
		return num2 * num2 * (1.0 + 4.0 * normalizedDistance);
	}

	private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
	{
		if (sortedValues.Count == 0)
		{
			return 0.0;
		}
		double num = Math.Clamp(percentile, 0.0, 1.0) * (double)(sortedValues.Count - 1);
		int num2 = (int)Math.Floor(num);
		int num3 = (int)Math.Ceiling(num);
		if (num2 == num3)
		{
			return sortedValues[num2];
		}
		double num4 = num - (double)num2;
		return sortedValues[num2] + (sortedValues[num3] - sortedValues[num2]) * num4;
	}

	private static void ClampLength(ref double x, ref double y, ref double z, double maximum)
	{
		double num = Math.Sqrt(x * x + y * y + z * z);
		if (!(num <= maximum) && !(num <= 1E-12))
		{
			double num2 = maximum / num;
			x *= num2;
			y *= num2;
			z *= num2;
		}
	}

	private static bool IsFinite(DenseFaceWarpVertex point)
	{
		if (double.IsFinite(point.X) && double.IsFinite(point.Y))
		{
			return double.IsFinite(point.Z);
		}
		return false;
	}

	private static double Distance(DenseFaceWarpVertex first, DenseFaceWarpVertex second)
	{
		return Math.Sqrt(DistanceSquared(first, second));
	}

	private static double DistanceSquared(DenseFaceWarpVertex first, DenseFaceWarpVertex second)
	{
		double num = first.X - second.X;
		double num2 = first.Y - second.Y;
		double num3 = first.Z - second.Z;
		return num * num + num2 * num2 + num3 * num3;
	}
}
