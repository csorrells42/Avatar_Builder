using System;
using System.Collections.Generic;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Webcam.DualCamera;

internal sealed record DualCameraObservation(DateTime CapturedAtUtc, int FrameWidth, int FrameHeight, int BgraStride, byte[]? BgraPixels, double TrackingConfidence, double HeadYawDegrees, double HeadPitchDegrees, double HeadRollDegrees, IReadOnlyList<DualCameraLandmark> Landmarks)
{
	public static DualCameraObservation? Create(FaceLandmarkFrame frame, int frameWidth, int frameHeight, int bgraStride = 0, byte[]? bgraPixels = null)
	{
		if (!frame.HasFace || frame.DenseMeshPoints.Count < 468)
		{
			return null;
		}
		int num = 0;
		foreach (FaceMeshLandmarkPoint denseMeshPoint in frame.DenseMeshPoints)
		{
			num = Math.Max(num, denseMeshPoint.Index + 1);
		}
		if (num < 468)
		{
			return null;
		}
		DualCameraLandmark[] array = new DualCameraLandmark[num];
		foreach (FaceMeshLandmarkPoint denseMeshPoint2 in frame.DenseMeshPoints)
		{
			if ((uint)denseMeshPoint2.Index < (uint)array.Length && double.IsFinite(denseMeshPoint2.X) && double.IsFinite(denseMeshPoint2.Y) && double.IsFinite(denseMeshPoint2.Z))
			{
				array[denseMeshPoint2.Index] = new DualCameraLandmark(denseMeshPoint2.X, denseMeshPoint2.Y, denseMeshPoint2.Z, IsValid: true);
			}
		}
		return new DualCameraObservation(frame.CapturedAtUtc, frameWidth, frameHeight, bgraStride, bgraPixels, frame.TrackingConfidence, frame.HeadYawDegrees, frame.HeadPitchDegrees, frame.HeadRollDegrees, array);
	}
}
