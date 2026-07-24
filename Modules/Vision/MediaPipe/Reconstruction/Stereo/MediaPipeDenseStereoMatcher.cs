using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

internal static class MediaPipeDenseStereoMatcher
{
	private readonly record struct Triangle(int A, int B, int C);

	private readonly record struct SampleDefinition(int SampleIndex, int TriangleIndex, double WeightA, double WeightB, double WeightC, bool IsExpressionSurface);

	private readonly record struct SampleCandidate(SampleDefinition Definition, Point2f PointA, Point2f PointB, DenseSourceCamera SourceCamera);

	private readonly record struct MatchedCandidate(SampleDefinition Definition, DenseSourceCamera SourceCamera, Point2f PointA, Point2f PointB, double MatchErrorPixels, double PhotometricError);

	private enum DenseSourceCamera
	{
		CameraA,
		CameraB
	}

	private readonly record struct Point3(double X, double Y, double Z);

	private readonly record struct CameraParameters(
		double Fx,
		double Fy,
		double Cx,
		double Cy,
		int Width,
		int Height,
		double K1,
		double K2,
		double P1,
		double P2,
		double K3,
		double K4,
		double K5,
		double K6)
	{
		public static CameraParameters Create(IReadOnlyList<double> matrix, IReadOnlyList<double> distortion, int calibrationWidth, int calibrationHeight, int frameWidth, int frameHeight)
		{
			double num = (double)frameWidth / Math.Max(1.0, calibrationWidth);
			double num2 = (double)frameHeight / Math.Max(1.0, calibrationHeight);
			return new CameraParameters(
				matrix[0] * num,
				matrix[4] * num2,
				matrix[2] * num,
				matrix[5] * num2,
				frameWidth,
				frameHeight,
				ReadCoefficient(distortion, 0),
				ReadCoefficient(distortion, 1),
				ReadCoefficient(distortion, 2),
				ReadCoefficient(distortion, 3),
				ReadCoefficient(distortion, 4),
				ReadCoefficient(distortion, 5),
				ReadCoefficient(distortion, 6),
				ReadCoefficient(distortion, 7));
		}

		public Point2d Undistort(double pixelX, double pixelY)
		{
			double num = (pixelX - Cx) / Math.Max(1E-09, Fx);
			double num2 = (pixelY - Cy) / Math.Max(1E-09, Fy);
			double num3 = num;
			double num4 = num2;
			for (int i = 0; i < 8; i++)
			{
				double num5 = num3 * num3 + num4 * num4;
				double num6 = num5 * num5;
				double num7 = num6 * num5;
				double val = (1.0 + K1 * num5 + K2 * num6 + K3 * num7) / Math.Max(1E-09, 1.0 + K4 * num5 + K5 * num6 + K6 * num7);
				double num8 = 2.0 * P1 * num3 * num4 + P2 * (num5 + 2.0 * num3 * num3);
				double num9 = P1 * (num5 + 2.0 * num4 * num4) + 2.0 * P2 * num3 * num4;
				num3 = (num - num8) / Math.Max(1E-09, val);
				num4 = (num2 - num9) / Math.Max(1E-09, val);
			}
			return new Point2d(num3, num4);
		}

		public Point2d Project(double x, double y, double z)
		{
			if (!double.IsFinite(z) || z <= 1E-09)
			{
				return new Point2d(double.NaN, double.NaN);
			}
			double num = x / z;
			double num2 = y / z;
			double num3 = num * num + num2 * num2;
			double num4 = num3 * num3;
			double num5 = num4 * num3;
			double num6 = (1.0 + K1 * num3 + K2 * num4 + K3 * num5) / Math.Max(1E-09, 1.0 + K4 * num3 + K5 * num4 + K6 * num5);
			double num7 = 2.0 * P1 * num * num2 + P2 * (num3 + 2.0 * num * num);
			double num8 = P1 * (num3 + 2.0 * num2 * num2) + 2.0 * P2 * num * num2;
			return new Point2d(Fx * (num * num6 + num7) + Cx, Fy * (num2 * num6 + num8) + Cy);
		}

		private static double ReadCoefficient(IReadOnlyList<double> distortion, int index)
		{
			if ((uint)index >= (uint)distortion.Count || !double.IsFinite(distortion[index]))
			{
				return 0.0;
			}
			return distortion[index];
		}
	}

	private const int LandmarkCount = 468;

	private const double MaximumForwardBackwardErrorPixels = 2.25;

	private const double MaximumInitialCorrectionPixels = 24.0;

	private const double MaximumEpipolarErrorPixels = 6.0;

	private const double MaximumReprojectionResidualPercent = 2.75;

	private static readonly bool[] DynamicMask = CreateDynamicMask();

	private static readonly Triangle[] Triangles = CreateTriangles();

	private static readonly SampleDefinition[] Samples = CreateSamples();

	public static int MaximumSampleCount => Samples.Length;

	public static MediaPipeStereoDenseRigPoint[] Match(MediaPipeStereoImagePair pair, out MediaPipeDenseStereoDiagnostics diagnostics)
	{
		ArgumentNullException.ThrowIfNull(pair, "pair");
		diagnostics = MediaPipeDenseStereoDiagnostics.Empty;
		if (!IsUsable(pair))
		{
			return Array.Empty<MediaPipeStereoDenseRigPoint>();
		}
		using Mat mat = Mat.FromPixelData(pair.CameraAHeight, pair.CameraAWidth, MatType.CV_8UC4, pair.CameraABgraPixels, pair.CameraAStride);
		using Mat mat2 = Mat.FromPixelData(pair.CameraBHeight, pair.CameraBWidth, MatType.CV_8UC4, pair.CameraBBgraPixels, pair.CameraBStride);
		using Mat mat3 = new Mat();
		using Mat mat4 = new Mat();
		using Mat mat5 = new Mat();
		using Mat mat6 = new Mat();
		Cv2.CvtColor(mat, mat3, ColorConversionCodes.BGRA2GRAY);
		Cv2.CvtColor(mat2, mat4, ColorConversionCodes.BGRA2GRAY);
		using (CLAHE cLAHE = Cv2.CreateCLAHE(2.0, new Size(8, 8)))
		{
			cLAHE.Apply(mat3, mat5);
			cLAHE.Apply(mat4, mat6);
		}
		int initialCapacity = Samples.Length / 2 + 1;
		List<SampleCandidate> cameraACandidates = new List<SampleCandidate>(initialCapacity);
		List<SampleCandidate> cameraBCandidates = new List<SampleCandidate>(initialCapacity);
		CreateCandidates(pair, cameraACandidates, cameraBCandidates);
		int candidateCount = cameraACandidates.Count + cameraBCandidates.Count;
		if (candidateCount == 0)
		{
			return Array.Empty<MediaPipeStereoDenseRigPoint>();
		}
		List<MatchedCandidate> matchedCandidates = new List<MatchedCandidate>(candidateCount);
		TrackCandidates(pair, mat5, mat6, cameraACandidates, sourceIsCameraA: true, matchedCandidates);
		int cameraAMatchedCount = matchedCandidates.Count;
		TrackCandidates(pair, mat6, mat5, cameraBCandidates, sourceIsCameraA: false, matchedCandidates);
		int cameraBMatchedCount = matchedCandidates.Count - cameraAMatchedCount;
		MediaPipeStereoDenseRigPoint[] triangulatedPoints = Triangulate(pair, matchedCandidates);
		diagnostics = new MediaPipeDenseStereoDiagnostics(candidateCount, matchedCandidates.Count, triangulatedPoints.Length, Samples.Length, cameraACandidates.Count, cameraBCandidates.Count, cameraAMatchedCount, cameraBMatchedCount);
		return triangulatedPoints;
	}

	private static void TrackCandidates(MediaPipeStereoImagePair pair, Mat sourceImage, Mat destinationImage, IReadOnlyList<SampleCandidate> candidates, bool sourceIsCameraA, List<MatchedCandidate> acceptedCandidates)
	{
		if (candidates.Count == 0)
		{
			return;
		}
		Point2f[] array = new Point2f[candidates.Count];
		Point2f[] array2 = new Point2f[candidates.Count];
		for (int i = 0; i < candidates.Count; i++)
		{
			array[i] = (sourceIsCameraA ? candidates[i].PointA : candidates[i].PointB);
			array2[i] = (sourceIsCameraA ? candidates[i].PointB : candidates[i].PointA);
		}
		Point2f[] nextPts = (Point2f[])array2.Clone();
		Cv2.CalcOpticalFlowPyrLK(sourceImage, destinationImage, array, ref nextPts, out byte[] status, out float[] err, new Size(31, 31), 4, new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps, 30, 0.01), OpticalFlowFlags.UseInitialFlow);
		Point2f[] nextPts2 = (Point2f[])array.Clone();
		Cv2.CalcOpticalFlowPyrLK(destinationImage, sourceImage, nextPts, ref nextPts2, out byte[] status2, out float[] _, new Size(31, 31), 4, new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps, 30, 0.01), OpticalFlowFlags.UseInitialFlow);
		int width = (sourceIsCameraA ? pair.CameraBWidth : pair.CameraAWidth);
		int height = (sourceIsCameraA ? pair.CameraBHeight : pair.CameraAHeight);
		for (int j = 0; j < candidates.Count; j++)
		{
			if (status[j] != 0 && status2[j] != 0 && IsInside(nextPts[j], width, height))
			{
				Point2f pointA = (sourceIsCameraA ? array[j] : nextPts[j]);
				Point2f pointB = (sourceIsCameraA ? nextPts[j] : array[j]);
				double num = Distance(array[j], nextPts2[j]);
				double num2 = Distance(array2[j], nextPts[j]);
				double num3 = SymmetricEpipolarDistance(pair, pointA, pointB);
				if (!(num > 2.25) && !(num2 > 24.0) && !(num3 > 6.0) && float.IsFinite(err[j]))
				{
					acceptedCandidates.Add(new MatchedCandidate(candidates[j].Definition, candidates[j].SourceCamera, pointA, pointB, Math.Max(num, num3), err[j]));
				}
			}
		}
	}

	private static MediaPipeStereoDenseRigPoint[] Triangulate(MediaPipeStereoImagePair pair, IReadOnlyList<MatchedCandidate> matches)
	{
		if (matches.Count == 0)
		{
			return Array.Empty<MediaPipeStereoDenseRigPoint>();
		}
		CameraParameters cameraParameters = CameraParameters.Create(pair.CameraAMatrix, pair.CameraADistortion, pair.CalibrationWidth, pair.CalibrationHeight, pair.CameraAWidth, pair.CameraAHeight);
		CameraParameters cameraParameters2 = CameraParameters.Create(pair.CameraBMatrix, pair.CameraBDistortion, pair.CalibrationWidth, pair.CalibrationHeight, pair.CameraBWidth, pair.CameraBHeight);
		using Mat mat = new Mat(3, 4, MatType.CV_32FC1, Scalar.All(0.0));
		mat.Set(0, 0, 1f);
		mat.Set(1, 1, 1f);
		mat.Set(2, 2, 1f);
		using Mat mat2 = new Mat(3, 4, MatType.CV_32FC1);
		for (int i = 0; i < 3; i++)
		{
			for (int j = 0; j < 3; j++)
			{
				mat2.Set(i, j, (float)pair.CameraAToBRotation[i * 3 + j]);
			}
			mat2.Set(i, 3, (float)pair.CameraAToBTranslationInches[i]);
		}
		using Mat mat3 = new Mat(2, matches.Count, MatType.CV_32FC1);
		using Mat mat4 = new Mat(2, matches.Count, MatType.CV_32FC1);
		for (int k = 0; k < matches.Count; k++)
		{
			Point2d point2d = cameraParameters.Undistort(matches[k].PointA.X, matches[k].PointA.Y);
			Point2d point2d2 = cameraParameters2.Undistort(matches[k].PointB.X, matches[k].PointB.Y);
			mat3.Set(0, k, (float)point2d.X);
			mat3.Set(1, k, (float)point2d.Y);
			mat4.Set(0, k, (float)point2d2.X);
			mat4.Set(1, k, (float)point2d2.Y);
		}
		using Mat mat5 = new Mat();
		Cv2.TriangulatePoints(mat, mat2, mat3, mat4, mat5);
		List<MediaPipeStereoDenseRigPoint> list = new List<MediaPipeStereoDenseRigPoint>(matches.Count);
		double num = Math.Max(1.0, (LandmarkDistance(pair.CameraALandmarks, 234, 454, pair.CameraAWidth, pair.CameraAHeight) + LandmarkDistance(pair.CameraBLandmarks, 234, 454, pair.CameraBWidth, pair.CameraBHeight)) * 0.5);
		for (int l = 0; l < matches.Count; l++)
		{
			float num2 = mat5.At<float>(3, l);
			if (!float.IsFinite(num2) || Math.Abs(num2) <= 1E-09f)
			{
				continue;
			}
			float num3 = mat5.At<float>(0, l) / num2;
			float num4 = mat5.At<float>(1, l) / num2;
			float num5 = mat5.At<float>(2, l) / num2;
			if (float.IsFinite(num3) && float.IsFinite(num4) && float.IsFinite(num5) && !(num5 <= 0f))
			{
				Point2d first = cameraParameters.Project(num3, num4, num5);
				Point3 point = Transform(pair.CameraAToBRotation, pair.CameraAToBTranslationInches, num3, num4, num5);
				Point2d first2 = cameraParameters2.Project(point.X, point.Y, point.Z);
				MatchedCandidate matchedCandidate = matches[l];
				double num6 = Math.Sqrt((SquaredDistance(first, matchedCandidate.PointA) + SquaredDistance(first2, matchedCandidate.PointB)) * 0.5) / num * 100.0;
				if (double.IsFinite(num6) && !(num6 > 2.75))
				{
					list.Add(new MediaPipeStereoDenseRigPoint(matchedCandidate.Definition.SampleIndex, matchedCandidate.Definition.TriangleIndex, num3, num4, num5, num6, matchedCandidate.MatchErrorPixels, matchedCandidate.Definition.IsExpressionSurface));
				}
			}
		}
		return list.ToArray();
	}

	private static void CreateCandidates(MediaPipeStereoImagePair pair, List<SampleCandidate> cameraACandidates, List<SampleCandidate> cameraBCandidates)
	{
		double num = Math.Max(1E-09, NormalizedLandmarkDistance(pair.CameraALandmarks, 234, 454));
		double num2 = Math.Max(1E-09, NormalizedLandmarkDistance(pair.CameraBLandmarks, 234, 454));
		DenseSourceCamera[] array = new DenseSourceCamera[Triangles.Length];
		for (int i = 0; i < Triangles.Length; i++)
		{
			Triangle triangle = Triangles[i];
			double num3 = NormalizedTriangleArea(pair.CameraALandmarks, triangle) / (num * num);
			double num4 = NormalizedTriangleArea(pair.CameraBLandmarks, triangle) / (num2 * num2);
			array[i] = ((num4 > num3) ? DenseSourceCamera.CameraB : DenseSourceCamera.CameraA);
		}
		SampleDefinition[] samples = Samples;
		for (int j = 0; j < samples.Length; j++)
		{
			SampleDefinition definition = samples[j];
			Triangle triangle2 = Triangles[definition.TriangleIndex];
			if (TryInterpolate(pair.CameraALandmarks, triangle2, definition, out var point) && TryInterpolate(pair.CameraBLandmarks, triangle2, definition, out var point2))
			{
				Point2f point2f = new Point2f((float)(point.X * (double)pair.CameraAWidth), (float)(point.Y * (double)pair.CameraAHeight));
				Point2f point2f2 = new Point2f((float)(point2.X * (double)pair.CameraBWidth), (float)(point2.Y * (double)pair.CameraBHeight));
				if (IsInside(point2f, pair.CameraAWidth, pair.CameraAHeight) && IsInside(point2f2, pair.CameraBWidth, pair.CameraBHeight))
				{
					DenseSourceCamera sourceCamera = array[definition.TriangleIndex];
					SampleCandidate candidate = new SampleCandidate(definition, point2f, point2f2, sourceCamera);
					if (sourceCamera == DenseSourceCamera.CameraA)
					{
						cameraACandidates.Add(candidate);
					}
					else
					{
						cameraBCandidates.Add(candidate);
					}
				}
			}
		}
	}

	private static bool TryInterpolate(IReadOnlyList<MediaPipeStereoImageLandmark> landmarks, Triangle triangle, SampleDefinition definition, out Point2d point)
	{
		point = default(Point2d);
		if ((uint)triangle.A >= (uint)landmarks.Count || (uint)triangle.B >= (uint)landmarks.Count || (uint)triangle.C >= (uint)landmarks.Count)
		{
			return false;
		}
		MediaPipeStereoImageLandmark mediaPipeStereoImageLandmark = landmarks[triangle.A];
		MediaPipeStereoImageLandmark mediaPipeStereoImageLandmark2 = landmarks[triangle.B];
		MediaPipeStereoImageLandmark mediaPipeStereoImageLandmark3 = landmarks[triangle.C];
		if (!mediaPipeStereoImageLandmark.IsValid || !mediaPipeStereoImageLandmark2.IsValid || !mediaPipeStereoImageLandmark3.IsValid)
		{
			return false;
		}
		point = new Point2d(mediaPipeStereoImageLandmark.X * definition.WeightA + mediaPipeStereoImageLandmark2.X * definition.WeightB + mediaPipeStereoImageLandmark3.X * definition.WeightC, mediaPipeStereoImageLandmark.Y * definition.WeightA + mediaPipeStereoImageLandmark2.Y * definition.WeightB + mediaPipeStereoImageLandmark3.Y * definition.WeightC);
		if (double.IsFinite(point.X))
		{
			return double.IsFinite(point.Y);
		}
		return false;
	}

	private static double SymmetricEpipolarDistance(MediaPipeStereoImagePair pair, Point2f pointA, Point2f pointB)
	{
		double num = (double)(pointA.X * (float)pair.CalibrationWidth) / Math.Max(1.0, pair.CameraAWidth);
		double num2 = (double)(pointA.Y * (float)pair.CalibrationHeight) / Math.Max(1.0, pair.CameraAHeight);
		double num3 = (double)(pointB.X * (float)pair.CalibrationWidth) / Math.Max(1.0, pair.CameraBWidth);
		double num4 = (double)(pointB.Y * (float)pair.CalibrationHeight) / Math.Max(1.0, pair.CameraBHeight);
		double[] fundamentalMatrix = pair.FundamentalMatrix;
		double num5 = fundamentalMatrix[0] * num + fundamentalMatrix[1] * num2 + fundamentalMatrix[2];
		double num6 = fundamentalMatrix[3] * num + fundamentalMatrix[4] * num2 + fundamentalMatrix[5];
		double num7 = fundamentalMatrix[6] * num + fundamentalMatrix[7] * num2 + fundamentalMatrix[8];
		double num8 = fundamentalMatrix[0] * num3 + fundamentalMatrix[3] * num4 + fundamentalMatrix[6];
		double num9 = fundamentalMatrix[1] * num3 + fundamentalMatrix[4] * num4 + fundamentalMatrix[7];
		double num10 = fundamentalMatrix[2] * num3 + fundamentalMatrix[5] * num4 + fundamentalMatrix[8];
		double num11 = Math.Abs(num5 * num3 + num6 * num4 + num7) / Math.Max(1E-09, Math.Sqrt(num5 * num5 + num6 * num6));
		return (Math.Abs(num8 * num + num9 * num2 + num10) / Math.Max(1E-09, Math.Sqrt(num8 * num8 + num9 * num9)) + num11) * 0.5;
	}

	private static bool IsUsable(MediaPipeStereoImagePair pair)
	{
		if (pair.CameraAWidth > 0 && pair.CameraAHeight > 0 && pair.CameraBWidth > 0 && pair.CameraBHeight > 0 && pair.CameraAStride >= pair.CameraAWidth * 4 && pair.CameraBStride >= pair.CameraBWidth * 4 && pair.CameraABgraPixels.Length >= pair.CameraAStride * pair.CameraAHeight && pair.CameraBBgraPixels.Length >= pair.CameraBStride * pair.CameraBHeight && pair.CameraALandmarks.Length >= 468 && pair.CameraBLandmarks.Length >= 468 && pair.CameraAMatrix.Length == 9 && pair.CameraBMatrix.Length == 9 && pair.CameraAToBRotation.Length == 9 && pair.CameraAToBTranslationInches.Length == 3)
		{
			return pair.FundamentalMatrix.Length == 9;
		}
		return false;
	}

	private static bool IsInside(Point2f point, int width, int height)
	{
		if (float.IsFinite(point.X) && float.IsFinite(point.Y) && point.X >= 18f && point.Y >= 18f && point.X < (float)width - 18f)
		{
			return point.Y < (float)height - 18f;
		}
		return false;
	}

	private static double Distance(Point2f first, Point2f second)
	{
		return Math.Sqrt(SquaredDistance(first, second));
	}

	private static double SquaredDistance(Point2d first, Point2f second)
	{
		return Math.Pow(first.X - (double)second.X, 2.0) + Math.Pow(first.Y - (double)second.Y, 2.0);
	}

	private static double SquaredDistance(Point2f first, Point2f second)
	{
		return Math.Pow(first.X - second.X, 2.0) + Math.Pow(first.Y - second.Y, 2.0);
	}

	private static double LandmarkDistance(IReadOnlyList<MediaPipeStereoImageLandmark> landmarks, int first, int second, int width, int height)
	{
		if ((uint)first >= (uint)landmarks.Count || (uint)second >= (uint)landmarks.Count)
		{
			return 0.0;
		}
		MediaPipeStereoImageLandmark mediaPipeStereoImageLandmark = landmarks[first];
		MediaPipeStereoImageLandmark mediaPipeStereoImageLandmark2 = landmarks[second];
		return Math.Sqrt(Math.Pow((mediaPipeStereoImageLandmark.X - mediaPipeStereoImageLandmark2.X) * (double)width, 2.0) + Math.Pow((mediaPipeStereoImageLandmark.Y - mediaPipeStereoImageLandmark2.Y) * (double)height, 2.0));
	}

	private static double NormalizedLandmarkDistance(IReadOnlyList<MediaPipeStereoImageLandmark> landmarks, int first, int second)
	{
		if ((uint)first >= (uint)landmarks.Count || (uint)second >= (uint)landmarks.Count)
		{
			return 0.0;
		}
		MediaPipeStereoImageLandmark mediaPipeStereoImageLandmark = landmarks[first];
		MediaPipeStereoImageLandmark mediaPipeStereoImageLandmark2 = landmarks[second];
		return Math.Sqrt(Math.Pow(mediaPipeStereoImageLandmark.X - mediaPipeStereoImageLandmark2.X, 2.0) + Math.Pow(mediaPipeStereoImageLandmark.Y - mediaPipeStereoImageLandmark2.Y, 2.0));
	}

	private static double NormalizedTriangleArea(IReadOnlyList<MediaPipeStereoImageLandmark> landmarks, Triangle triangle)
	{
		if ((uint)triangle.A >= (uint)landmarks.Count || (uint)triangle.B >= (uint)landmarks.Count || (uint)triangle.C >= (uint)landmarks.Count)
		{
			return 0.0;
		}
		MediaPipeStereoImageLandmark mediaPipeStereoImageLandmark = landmarks[triangle.A];
		MediaPipeStereoImageLandmark mediaPipeStereoImageLandmark2 = landmarks[triangle.B];
		MediaPipeStereoImageLandmark mediaPipeStereoImageLandmark3 = landmarks[triangle.C];
		if (!mediaPipeStereoImageLandmark.IsValid || !mediaPipeStereoImageLandmark2.IsValid || !mediaPipeStereoImageLandmark3.IsValid)
		{
			return 0.0;
		}
		return Math.Abs((mediaPipeStereoImageLandmark2.X - mediaPipeStereoImageLandmark.X) * (mediaPipeStereoImageLandmark3.Y - mediaPipeStereoImageLandmark.Y) - (mediaPipeStereoImageLandmark2.Y - mediaPipeStereoImageLandmark.Y) * (mediaPipeStereoImageLandmark3.X - mediaPipeStereoImageLandmark.X)) * 0.5;
	}

	private static Point3 Transform(IReadOnlyList<double> rotation, IReadOnlyList<double> translation, double x, double y, double z)
	{
		return new Point3(rotation[0] * x + rotation[1] * y + rotation[2] * z + translation[0], rotation[3] * x + rotation[4] * y + rotation[5] * z + translation[1], rotation[6] * x + rotation[7] * y + rotation[8] * z + translation[2]);
	}

	private static Triangle[] CreateTriangles()
	{
		HashSet<int>[] array = new HashSet<int>[468];
		for (int i = 0; i < array.Length; i++)
		{
			array[i] = new HashSet<int>();
		}
		(int, int)[] tessellationEdges = MediaPipeFaceMeshTopology.TessellationEdges;
		for (int j = 0; j < tessellationEdges.Length; j++)
		{
			(int, int) tuple = tessellationEdges[j];
			if ((uint)tuple.Item1 < 468u && (uint)tuple.Item2 < 468u)
			{
				array[tuple.Item1].Add(tuple.Item2);
				array[tuple.Item2].Add(tuple.Item1);
			}
		}
		List<Triangle> list = new List<Triangle>();
		for (int k = 0; k < 468; k++)
		{
			foreach (int item in array[k])
			{
				if (item <= k)
				{
					continue;
				}
				foreach (int item2 in array[item])
				{
					if (item2 > item && array[k].Contains(item2))
					{
						list.Add(new Triangle(k, item, item2));
					}
				}
			}
		}
		list.Sort(delegate(Triangle left, Triangle right)
		{
			int num = left.A.CompareTo(right.A);
			if (num != 0)
			{
				return num;
			}
			num = left.B.CompareTo(right.B);
			return (num == 0) ? left.C.CompareTo(right.C) : num;
		});
		return list.ToArray();
	}

	private static SampleDefinition[] CreateSamples()
	{
		SampleDefinition[] array = new SampleDefinition[Triangles.Length * 18];
		int num = 0;
		for (int i = 0; i < Triangles.Length; i++)
		{
			Triangle triangle = Triangles[i];
			bool isExpressionSurface = DynamicMask[triangle.A] || DynamicMask[triangle.B] || DynamicMask[triangle.C];
			for (int j = 1; j <= 6; j++)
			{
				for (int k = 1; k <= 8 - j - 1; k++)
				{
					int num2 = 8 - j - k;
					if ((j != 6 || k != 1 || num2 != 1) && (k != 6 || j != 1 || num2 != 1) && (num2 != 6 || j != 1 || k != 1))
					{
						array[num] = new SampleDefinition(num, i, (double)j / 8.0, (double)k / 8.0, (double)num2 / 8.0, isExpressionSurface);
						num++;
					}
				}
			}
		}
		return array;
	}

	private static bool[] CreateDynamicMask()
	{
		bool[] array = new bool[468];
		int[] dynamicIdentityIndices = MediaPipeFaceMeshTopology.DynamicIdentityIndices;
		foreach (int num in dynamicIdentityIndices)
		{
			if ((uint)num < 468u)
			{
				array[num] = true;
			}
		}
		return array;
	}
}
