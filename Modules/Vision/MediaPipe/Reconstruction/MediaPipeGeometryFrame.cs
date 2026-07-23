using System;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction;

public sealed class MediaPipeGeometryFrame
{
	public required DateTime CapturedAtUtc { get; init; }

	public required string CameraId { get; init; }

	public required int FrameWidthPixels { get; init; }

	public required int FrameHeightPixels { get; init; }

	public required double HorizontalFieldOfViewDegrees { get; init; }

	public required double ARotationAroundXDegrees { get; init; }

	public required double BRotationAroundYDegrees { get; init; }

	public required double CRotationAroundZDegrees { get; init; }

	public required FaceMeshLandmarkPoint[] Landmarks { get; init; }

	public required double[] FacialTransformationMatrix { get; init; }

	public static MediaPipeGeometryFrame Create(FaceLandmarkFrame frame, int frameWidthPixels, int frameHeightPixels, string cameraId, double horizontalFieldOfViewDegrees)
	{
		ArgumentNullException.ThrowIfNull(frame, "frame");
		FaceMeshLandmarkPoint[] array = new FaceMeshLandmarkPoint[frame.DenseMeshPoints.Count];
		for (int i = 0; i < array.Length; i++)
		{
			FaceMeshLandmarkPoint faceMeshLandmarkPoint = frame.DenseMeshPoints[i];
			array[i] = new FaceMeshLandmarkPoint
			{
				Index = faceMeshLandmarkPoint.Index,
				X = faceMeshLandmarkPoint.X,
				Y = faceMeshLandmarkPoint.Y,
				Z = faceMeshLandmarkPoint.Z
			};
		}
		double[] array2 = new double[frame.FacialTransformationMatrix.Count];
		for (int j = 0; j < array2.Length; j++)
		{
			array2[j] = frame.FacialTransformationMatrix[j];
		}
		return new MediaPipeGeometryFrame
		{
			CapturedAtUtc = frame.CapturedAtUtc,
			CameraId = (string.IsNullOrWhiteSpace(cameraId) ? "camera" : cameraId.Trim()),
			FrameWidthPixels = Math.Max(1, frameWidthPixels),
			FrameHeightPixels = Math.Max(1, frameHeightPixels),
			HorizontalFieldOfViewDegrees = Math.Clamp(horizontalFieldOfViewDegrees, 20.0, 150.0),
			ARotationAroundXDegrees = frame.HeadPitchDegrees,
			BRotationAroundYDegrees = frame.HeadYawDegrees,
			CRotationAroundZDegrees = frame.HeadRollDegrees,
			Landmarks = array,
			FacialTransformationMatrix = array2
		};
	}
}
