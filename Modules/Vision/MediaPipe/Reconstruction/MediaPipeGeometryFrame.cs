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
		FaceMeshLandmarkPoint[] landmarks = frame.DenseMeshPoints as FaceMeshLandmarkPoint[]
			?? CopyLandmarks(frame.DenseMeshPoints);
		double[] transformationMatrix = frame.FacialTransformationMatrix as double[]
			?? CopyTransformationMatrix(frame.FacialTransformationMatrix);
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
			Landmarks = landmarks,
			FacialTransformationMatrix = transformationMatrix
		};
	}

	private static FaceMeshLandmarkPoint[] CopyLandmarks(System.Collections.Generic.IReadOnlyList<FaceMeshLandmarkPoint> source)
	{
		FaceMeshLandmarkPoint[] copy = new FaceMeshLandmarkPoint[source.Count];
		for (int i = 0; i < copy.Length; i++)
		{
			copy[i] = source[i];
		}
		return copy;
	}

	private static double[] CopyTransformationMatrix(System.Collections.Generic.IReadOnlyList<double> source)
	{
		double[] copy = new double[source.Count];
		for (int i = 0; i < copy.Length; i++)
		{
			copy[i] = source[i];
		}
		return copy;
	}
}
