using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

public static class MediaPipeFaceCanonicalizerSelfTest
{
	public static MediaPipeFaceCanonicalizerSelfTestResult Run()
	{
		try
		{
			IReadOnlyList<FaceMeshLandmarkPoint> readOnlyList = CreateSyntheticFace();
			IReadOnlyList<FaceMeshLandmarkPoint> landmarks = Transform(readOnlyList, 0.73, 0.21, -0.34, 0.18);
			Require(MediaPipeFaceCanonicalizer.TryCanonicalize(readOnlyList, 1000, 1000, out MediaPipeCanonicalFace canonicalFace), "The synthetic source face did not canonicalize.");
			Require(MediaPipeFaceCanonicalizer.TryCanonicalize(landmarks, 1000, 1000, out MediaPipeCanonicalFace canonicalFace2), "The transformed synthetic face did not canonicalize.");
			double num = MediaPipeFaceCanonicalizer.CalculateRmsDifference(canonicalFace, canonicalFace2);
			Require(double.IsFinite(num) && num < 1E-07, $"Rigid pose leaked into canonical geometry: RMS {num:0.000000000}.");
			return new MediaPipeFaceCanonicalizerSelfTestResult(Succeeded: true, $"PASS: MediaPipe canonicalizer removed rigid scale/rotation/translation; RMS {num:0.000000000}.");
		}
		catch (Exception ex)
		{
			return new MediaPipeFaceCanonicalizerSelfTestResult(Succeeded: false, "FAIL: " + ex.Message);
		}
	}

	private static IReadOnlyList<FaceMeshLandmarkPoint> CreateSyntheticFace()
	{
		FaceMeshLandmarkPoint[] array = new FaceMeshLandmarkPoint[478];
		for (int i = 0; i < array.Length; i++)
		{
			double num = (double)i * Math.PI * 2.0 / (double)array.Length;
			array[i] = new FaceMeshLandmarkPoint
			{
				Index = i,
				X = 0.5 + Math.Cos(num) * 0.16,
				Y = 0.5 + Math.Sin(num) * 0.23,
				Z = -0.04 + Math.Cos(num * 2.0) * 0.025
			};
		}
		array[33] = Point(33, 0.39, 0.44, -0.01);
		array[133] = Point(133, 0.46, 0.44, -0.015);
		array[362] = Point(362, 0.54, 0.44, -0.015);
		array[263] = Point(263, 0.61, 0.44, -0.01);
		array[1] = Point(1, 0.5, 0.54, -0.1);
		array[152] = Point(152, 0.5, 0.76, 0.015);
		return array;
	}

	private static IReadOnlyList<FaceMeshLandmarkPoint> Transform(IReadOnlyList<FaceMeshLandmarkPoint> source, double scale, double angleX, double angleY, double angleZ)
	{
		double num = Math.Sin(angleX);
		double num2 = Math.Cos(angleX);
		double num3 = Math.Sin(angleY);
		double num4 = Math.Cos(angleY);
		double num5 = Math.Sin(angleZ);
		double num6 = Math.Cos(angleZ);
		FaceMeshLandmarkPoint[] array = new FaceMeshLandmarkPoint[source.Count];
		for (int i = 0; i < source.Count; i++)
		{
			FaceMeshLandmarkPoint faceMeshLandmarkPoint = source[i];
			double num7 = faceMeshLandmarkPoint.X - 0.5;
			double num8 = faceMeshLandmarkPoint.Y - 0.5;
			double z = faceMeshLandmarkPoint.Z;
			double num9 = num8 * num2 - z * num;
			double num10 = num8 * num + z * num2;
			double num11 = num7 * num4 + num10 * num3;
			double num12 = (0.0 - num7) * num3 + num10 * num4;
			double num13 = num11 * num6 - num9 * num5;
			double num14 = num11 * num5 + num9 * num6;
			array[i] = new FaceMeshLandmarkPoint
			{
				Index = faceMeshLandmarkPoint.Index,
				X = 0.42 + num13 * scale,
				Y = 0.57 + num14 * scale,
				Z = -0.02 + num12 * scale
			};
		}
		return array;
	}

	private static FaceMeshLandmarkPoint Point(int index, double x, double y, double z)
	{
		return new FaceMeshLandmarkPoint
		{
			Index = index,
			X = x,
			Y = y,
			Z = z
		};
	}

	private static void Require(bool condition, string message)
	{
		if (!condition)
		{
			throw new InvalidOperationException(message);
		}
	}
}
