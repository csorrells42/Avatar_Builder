using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

public static class MediaPipeFaceCanonicalizer
{
	private readonly record struct Vector3d(double X, double Y, double Z)
	{
		public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

		public bool TryNormalize(out Vector3d normalized)
		{
			double length = Length;
			if (length <= 1E-12 || !double.IsFinite(length))
			{
				normalized = default(Vector3d);
				return false;
			}
			normalized = this * (1.0 / length);
			return true;
		}

		public static double Dot(Vector3d first, Vector3d second)
		{
			return first.X * second.X + first.Y * second.Y + first.Z * second.Z;
		}

		public static Vector3d Cross(Vector3d first, Vector3d second)
		{
			return new Vector3d(first.Y * second.Z - first.Z * second.Y, first.Z * second.X - first.X * second.Z, first.X * second.Y - first.Y * second.X);
		}

		public static Vector3d operator +(Vector3d first, Vector3d second)
		{
			return new Vector3d(first.X + second.X, first.Y + second.Y, first.Z + second.Z);
		}

		public static Vector3d operator -(Vector3d first, Vector3d second)
		{
			return new Vector3d(first.X - second.X, first.Y - second.Y, first.Z - second.Z);
		}

		public static Vector3d operator *(Vector3d value, double scale)
		{
			return new Vector3d(value.X * scale, value.Y * scale, value.Z * scale);
		}
	}

	private const int LeftEyeOuterIndex = 33;

	private const int LeftEyeInnerIndex = 133;

	private const int RightEyeInnerIndex = 362;

	private const int RightEyeOuterIndex = 263;

	private const int NoseTipIndex = 1;

	private const int ChinIndex = 152;

	private const double MinimumEyeSpan = 0.0001;

	public static bool TryCanonicalize(IReadOnlyList<FaceMeshLandmarkPoint> landmarks, int frameWidth, int frameHeight, out MediaPipeCanonicalFace canonicalFace)
	{
		canonicalFace = MediaPipeCanonicalFace.Empty;
		if (landmarks.Count < 468 || frameWidth <= 0 || frameHeight <= 0)
		{
			return false;
		}
		int num = -1;
		for (int i = 0; i < landmarks.Count; i++)
		{
			num = Math.Max(num, landmarks[i].Index);
		}
		if (num < 152)
		{
			return false;
		}
		Vector3d[] array = new Vector3d[num + 1];
		bool[] array2 = new bool[num + 1];
		double num2 = (double)frameHeight / (double)frameWidth;
		for (int j = 0; j < landmarks.Count; j++)
		{
			FaceMeshLandmarkPoint faceMeshLandmarkPoint = landmarks[j];
			if ((uint)faceMeshLandmarkPoint.Index < (uint)array.Length && double.IsFinite(faceMeshLandmarkPoint.X) && double.IsFinite(faceMeshLandmarkPoint.Y) && double.IsFinite(faceMeshLandmarkPoint.Z))
			{
				array[faceMeshLandmarkPoint.Index] = new Vector3d(faceMeshLandmarkPoint.X, faceMeshLandmarkPoint.Y * num2, faceMeshLandmarkPoint.Z);
				array2[faceMeshLandmarkPoint.Index] = true;
			}
		}
		if (!TryGet(array, array2, 33, out var point) || !TryGet(array, array2, 133, out var point2) || !TryGet(array, array2, 362, out var point3) || !TryGet(array, array2, 263, out var point4) || !TryGet(array, array2, 1, out var point5) || !TryGet(array, array2, 152, out var point6))
		{
			return false;
		}
		Vector3d vector3d = (point + point2) * 0.5;
		Vector3d vector3d2 = (point3 + point4) * 0.5;
		Vector3d vector3d3 = (vector3d + vector3d2) * 0.5;
		Vector3d vector3d4 = vector3d2 - vector3d;
		double length = vector3d4.Length;
		if (length <= 0.0001 || !vector3d4.TryNormalize(out var normalized))
		{
			return false;
		}
		Vector3d vector3d5 = point6 - vector3d3;
		if (!(vector3d5 - normalized * Vector3d.Dot(vector3d5, normalized)).TryNormalize(out var normalized2))
		{
			return false;
		}
		if (!Vector3d.Cross(normalized, normalized2).TryNormalize(out var normalized3))
		{
			return false;
		}
		if (Vector3d.Dot(point5 - vector3d3, normalized3) < 0.0)
		{
			normalized3 *= -1.0;
		}
		if (!Vector3d.Cross(normalized3, normalized).TryNormalize(out normalized2))
		{
			return false;
		}
		MediaPipeCanonicalPoint[] array3 = new MediaPipeCanonicalPoint[array.Length];
		for (int k = 0; k < array.Length; k++)
		{
			if (array2[k])
			{
				Vector3d first = array[k] - vector3d3;
				array3[k] = new MediaPipeCanonicalPoint(k, Vector3d.Dot(first, normalized) / length, Vector3d.Dot(first, normalized2) / length, Vector3d.Dot(first, normalized3) / length, IsValid: true);
			}
		}
		canonicalFace = new MediaPipeCanonicalFace(array3, length);
		return true;
	}

	public static double CalculateRmsDifference(MediaPipeCanonicalFace first, MediaPipeCanonicalFace second, IReadOnlyList<int>? landmarkIndices = null)
	{
		double sumSquared = 0.0;
		int count = 0;
		if (landmarkIndices == null)
		{
			int num = Math.Min(first.Points.Count, second.Points.Count);
			for (int i = 0; i < num; i++)
			{
				AccumulateDifference(first.Points[i], second.Points[i], ref sumSquared, ref count);
			}
		}
		else
		{
			for (int j = 0; j < landmarkIndices.Count; j++)
			{
				int num2 = landmarkIndices[j];
				if ((uint)num2 < (uint)first.Points.Count && (uint)num2 < (uint)second.Points.Count)
				{
					AccumulateDifference(first.Points[num2], second.Points[num2], ref sumSquared, ref count);
				}
			}
		}
		if (count != 0)
		{
			return Math.Sqrt(sumSquared / (double)count);
		}
		return double.NaN;
	}

	private static void AccumulateDifference(MediaPipeCanonicalPoint first, MediaPipeCanonicalPoint second, ref double sumSquared, ref int count)
	{
		if (first.IsValid && second.IsValid)
		{
			double num = first.X - second.X;
			double num2 = first.Y - second.Y;
			double num3 = first.Z - second.Z;
			sumSquared += num * num + num2 * num2 + num3 * num3;
			count++;
		}
	}

	private static bool TryGet(Vector3d[] points, bool[] valid, int index, out Vector3d point)
	{
		point = default(Vector3d);
		if ((uint)index >= (uint)points.Length || !valid[index])
		{
			return false;
		}
		point = points[index];
		return true;
	}
}
