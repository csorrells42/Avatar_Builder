using System;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction;

public static class MediaPipeNormalizedFaceReconstructorSelfTest
{
	private readonly record struct TestPoint(double X, double Y, double Z);

	private const int TargetIndex = 1;

	public static MediaPipeNormalizedFaceReconstructorSelfTestResult Run()
	{
		TestPoint[] array = CreateCanonicalFace();
		MediaPipeNormalizedFaceReconstructor mediaPipeNormalizedFaceReconstructor = new MediaPipeNormalizedFaceReconstructor();
		mediaPipeNormalizedFaceReconstructor.Reset("synthetic", "Synthetic Face");
		DateTime capturedAtUtc = DateTime.UtcNow;
		for (int i = -55; i <= 55; i += 5)
		{
			for (int j = 0; j < 2; j++)
			{
				if (!mediaPipeNormalizedFaceReconstructor.TryAddFrame(CreateFrame(array, capturedAtUtc, i)))
				{
					return new MediaPipeNormalizedFaceReconstructorSelfTestResult(Succeeded: false, $"MediaPipe geometry self-test rejected synthetic B={i} frame.");
				}
				capturedAtUtc = capturedAtUtc.AddMilliseconds(40.0);
			}
		}
		MediaPipeNormalizedFaceModel mediaPipeNormalizedFaceModel = mediaPipeNormalizedFaceReconstructor.CreateModel();
		MediaPipeNormalizedFaceVertex mediaPipeNormalizedFaceVertex = mediaPipeNormalizedFaceModel.Vertices[1];
		double num = Math.Sqrt(1.5);
		double num2 = array[1].X / num;
		double num3 = array[1].Y / num;
		double num4 = array[1].Z / num;
		double num5 = Math.Sqrt(Math.Pow(mediaPipeNormalizedFaceVertex.X - num2, 2.0) + Math.Pow(mediaPipeNormalizedFaceVertex.Y - num3, 2.0) + Math.Pow(mediaPipeNormalizedFaceVertex.Z - num4, 2.0));
		MediaPipeNormalizedFaceReconstructor mediaPipeNormalizedFaceReconstructor2 = new MediaPipeNormalizedFaceReconstructor();
		mediaPipeNormalizedFaceReconstructor2.Restore(mediaPipeNormalizedFaceReconstructor.CreateState(), "synthetic", "Synthetic Face");
		MediaPipeNormalizedFaceModel mediaPipeNormalizedFaceModel2 = mediaPipeNormalizedFaceReconstructor2.CreateModel();
		int num6;
		object detail;
		if (mediaPipeNormalizedFaceModel.AcceptedFrameCount == 46 && mediaPipeNormalizedFaceModel2.AcceptedFrameCount == mediaPipeNormalizedFaceModel.AcceptedFrameCount && Math.Abs(mediaPipeNormalizedFaceVertex.Z) > 0.12 && num5 < 0.015 && mediaPipeNormalizedFaceVertex.ResidualPercent < 0.5 && mediaPipeNormalizedFaceModel.HiddenLandmarkRejectionCount > 0 && mediaPipeNormalizedFaceModel.SilhouetteProfiles.Count >= 15)
		{
			num6 = ((mediaPipeNormalizedFaceModel.VisualHullSlices.Count > 0) ? 1 : 0);
			if (num6 != 0)
			{
				detail = $"MediaPipe geometry self-test passed: recovered XYZ error {num5:0.000000}, Z {mediaPipeNormalizedFaceVertex.Z:0.000}, {mediaPipeNormalizedFaceModel.HiddenLandmarkRejectionCount:n0} hidden predictions rejected, {mediaPipeNormalizedFaceModel.SilhouetteProfiles.Count:n0} angle profiles, {mediaPipeNormalizedFaceModel.VisualHullSlices.Count:n0} hull slices.";
				goto IL_038d;
			}
		}
		else
		{
			num6 = 0;
		}
		detail = $"MediaPipe geometry self-test failed: frames {mediaPipeNormalizedFaceModel.AcceptedFrameCount}, XYZ error {num5:0.000000}, Z {mediaPipeNormalizedFaceVertex.Z:0.000}, residual {mediaPipeNormalizedFaceVertex.ResidualPercent:0.000}%, hidden rejects {mediaPipeNormalizedFaceModel.HiddenLandmarkRejectionCount}, profiles {mediaPipeNormalizedFaceModel.SilhouetteProfiles.Count}, hull slices {mediaPipeNormalizedFaceModel.VisualHullSlices.Count}.";
		goto IL_038d;
		IL_038d:
		return new MediaPipeNormalizedFaceReconstructorSelfTestResult((byte)num6 != 0, (string)detail);
	}

	private static TestPoint[] CreateCanonicalFace()
	{
		TestPoint[] array = new TestPoint[478];
		for (int i = 0; i < array.Length; i++)
		{
			double num = (double)(i % 29) / 28.0;
			double num2 = (double)(i / 29 % 17) / 16.0;
			double num3 = (num - 0.5) * 0.9;
			double num4 = (0.5 - num2) * 1.25;
			double z = 0.2 - 0.3 * num3 * num3 - 0.12 * num4 * num4;
			array[i] = new TestPoint(num3, num4, z);
		}
		int[] faceOvalIndices = MediaPipeFaceMeshTopology.FaceOvalIndices;
		for (int j = 0; j < faceOvalIndices.Length; j++)
		{
			double num5 = Math.PI / 2.0 - (double)j * Math.PI * 2.0 / (double)faceOvalIndices.Length;
			array[faceOvalIndices[j]] = new TestPoint(0.62 * Math.Cos(num5), 0.75 * Math.Sin(num5), -0.05 + 0.1 * Math.Cos(num5));
		}
		array[168] = new TestPoint(0.0, 0.0, 0.0);
		array[33] = new TestPoint(-0.5, 0.0, 0.0);
		array[263] = new TestPoint(0.5, 0.0, 0.0);
		array[10] = new TestPoint(0.0, 0.75, 0.0);
		array[152] = new TestPoint(0.0, -0.75, 0.0);
		array[1] = new TestPoint(0.05, 0.08, 0.35);
		return array;
	}

	private static MediaPipeGeometryFrame CreateFrame(TestPoint[] canonical, DateTime capturedAtUtc, double yawDegrees)
	{
		double num = yawDegrees * Math.PI / 180.0;
		double num2 = Math.Cos(num);
		double num3 = Math.Sin(num);
		FaceMeshLandmarkPoint[] array = new FaceMeshLandmarkPoint[canonical.Length];
		for (int i = 0; i < canonical.Length; i++)
		{
			TestPoint testPoint = canonical[i];
			double num4 = num2 * testPoint.X + num3 * testPoint.Z;
			double y = testPoint.Y;
			array[i] = new FaceMeshLandmarkPoint
			{
				Index = i,
				X = 0.5 + 700.0 * num4 / 3840.0,
				Y = 0.5 - 700.0 * y / 2160.0,
				Z = 0.0
			};
		}
		return new MediaPipeGeometryFrame
		{
			CapturedAtUtc = capturedAtUtc,
			CameraId = "synthetic-camera",
			FrameWidthPixels = 3840,
			FrameHeightPixels = 2160,
			HorizontalFieldOfViewDegrees = 71.4,
			ARotationAroundXDegrees = 0.0,
			BRotationAroundYDegrees = yawDegrees,
			CRotationAroundZDegrees = 0.0,
			Landmarks = array,
			FacialTransformationMatrix = Array.Empty<double>()
		};
	}
}
