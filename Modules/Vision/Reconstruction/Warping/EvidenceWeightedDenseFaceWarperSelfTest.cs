using System;
using System.Collections.Generic;
using System.Linq;

namespace AvatarBuilder.Modules.Vision.Reconstruction.Warping;

public static class EvidenceWeightedDenseFaceWarperSelfTest
{
	public static EvidenceWeightedDenseFaceWarperSelfTestResult Run()
	{
		try
		{
			List<DenseFaceWarpVertex> list = CreateGrid();
			List<DenseFaceWarpControlPoint> list2 = CreateControls(list);
			DenseFaceWarpResult denseFaceWarpResult = EvidenceWeightedDenseFaceWarper.Warp(new DenseFaceWarpInput
			{
				SubjectId = "self-test",
				SubjectDisplayName = "Self Test",
				CreatedAtUtc = DateTime.UtcNow,
				SourceVertices = list,
				ControlPoints = list2
			});
			if (!denseFaceWarpResult.HasGeometry)
			{
				return new EvidenceWeightedDenseFaceWarperSelfTestResult(Succeeded: false, "FAIL: " + denseFaceWarpResult.Status);
			}
			if (!(denseFaceWarpResult.WarpedAnchorRms < denseFaceWarpResult.SourceAnchorRms * 0.45))
			{
				return new EvidenceWeightedDenseFaceWarperSelfTestResult(Succeeded: false, $"FAIL: anchor RMS did not improve enough ({denseFaceWarpResult.SourceAnchorRms:0.0000} -> {denseFaceWarpResult.WarpedAnchorRms:0.0000}).");
			}
			DenseFaceWarpVertex first = list[0];
			DenseFaceWarpVertex second = denseFaceWarpResult.WarpedVertices[0];
			double num = Distance(first, second);
			if (num > 0.002)
			{
				return new EvidenceWeightedDenseFaceWarperSelfTestResult(Succeeded: false, $"FAIL: compact support moved a distant vertex by {num:0.000000}.");
			}
			if (denseFaceWarpResult.WarpedVertices.Any((DenseFaceWarpVertex vertex) => !double.IsFinite(vertex.X) || !double.IsFinite(vertex.Y) || !double.IsFinite(vertex.Z)))
			{
				return new EvidenceWeightedDenseFaceWarperSelfTestResult(Succeeded: false, "FAIL: warped mesh contains a non-finite coordinate.");
			}
			if (denseFaceWarpResult.SafetyClampVertexPercent > 0.0 || denseFaceWarpResult.Percentile95AppliedDisplacement > 0.2)
			{
				return new EvidenceWeightedDenseFaceWarperSelfTestResult(Succeeded: false, $"FAIL: warp is over-aggressive (p95 {denseFaceWarpResult.Percentile95AppliedDisplacement:0.0000}, safety clamp {denseFaceWarpResult.SafetyClampVertexPercent:0.00}%).");
			}
			return new EvidenceWeightedDenseFaceWarperSelfTestResult(Succeeded: true, $"Dense face warp self-test passed: {list.Count:n0} vertices, {list2.Count:n0} controls, anchor RMS {denseFaceWarpResult.SourceAnchorRms:0.0000} -> {denseFaceWarpResult.WarpedAnchorRms:0.0000}, p95 movement {denseFaceWarpResult.Percentile95AppliedDisplacement:0.0000}, no safety clamping.");
		}
		catch (Exception ex)
		{
			return new EvidenceWeightedDenseFaceWarperSelfTestResult(Succeeded: false, "FAIL: " + ex.Message);
		}
	}

	private static List<DenseFaceWarpVertex> CreateGrid()
	{
		List<DenseFaceWarpVertex> list = new List<DenseFaceWarpVertex>(625);
		for (int i = 0; i < 25; i++)
		{
			for (int j = 0; j < 25; j++)
			{
				double num = -1.0 + 2.0 * (double)j / 24.0;
				double num2 = -1.0 + 2.0 * (double)i / 24.0;
				list.Add(new DenseFaceWarpVertex
				{
					Index = list.Count,
					X = num,
					Y = num2,
					Z = 0.05 * (1.0 - num * num) * (1.0 - num2 * num2)
				});
			}
		}
		return list;
	}

	private static List<DenseFaceWarpControlPoint> CreateControls(IReadOnlyList<DenseFaceWarpVertex> vertices)
	{
		List<DenseFaceWarpControlPoint> list = new List<DenseFaceWarpControlPoint>(16);
		int[] array = new int[4] { 8, 11, 14, 17 };
		int[] array2 = array;
		foreach (int num in array2)
		{
			int[] array3 = array;
			foreach (int num2 in array3)
			{
				DenseFaceWarpVertex denseFaceWarpVertex = vertices[num * 25 + num2];
				list.Add(new DenseFaceWarpControlPoint
				{
					SparseLandmarkIndex = list.Count,
					MediaPipeLandmarkIndex = list.Count,
					Role = "self-test",
					Confidence = 1.0,
					InfluenceRadius = 0.62,
					Source = denseFaceWarpVertex,
					Target = new DenseFaceWarpVertex
					{
						Index = denseFaceWarpVertex.Index,
						X = denseFaceWarpVertex.X + 0.12,
						Y = denseFaceWarpVertex.Y - 0.04,
						Z = denseFaceWarpVertex.Z + 0.08
					}
				});
			}
		}
		return list;
	}

	private static double Distance(DenseFaceWarpVertex first, DenseFaceWarpVertex second)
	{
		double num = first.X - second.X;
		double num2 = first.Y - second.Y;
		double num3 = first.Z - second.Z;
		return Math.Sqrt(num * num + num2 * num2 + num3 * num3);
	}
}
