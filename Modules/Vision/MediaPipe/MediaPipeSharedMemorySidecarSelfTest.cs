using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.Diagnostics;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

public static class MediaPipeSharedMemorySidecarSelfTest
{
	public static MediaPipeSharedMemorySidecarSelfTestResult Run(string imagePath)
	{
		try
		{
			string fullPath = Path.GetFullPath(imagePath);
			if (!File.Exists(fullPath))
			{
				return new MediaPipeSharedMemorySidecarSelfTestResult(Succeeded: false, "MediaPipe shared-memory test image does not exist: " + fullPath);
			}
			BitmapSource bitmap = LoadBitmap(fullPath);
			using MediaPipeFaceLandmarkerSidecarTracker mediaPipeFaceLandmarkerSidecarTracker = new MediaPipeFaceLandmarkerSidecarTracker
			{
				MaxDetectionDimension = 1920
			};
			FaceLandmarkTrackingResult faceLandmarkTrackingResult = mediaPipeFaceLandmarkerSidecarTracker.Detect(bitmap, DateTime.UtcNow);
			if (!faceLandmarkTrackingResult.LandmarkFrame.HasDenseMesh)
			{
				return new MediaPipeSharedMemorySidecarSelfTestResult(Succeeded: false, "MediaPipe shared-memory sidecar warmup failed: " + faceLandmarkTrackingResult.BackendStatus);
			}
			List<double> list = new List<double>(8);
			List<double> list2 = new List<double>(8);
			List<double> list3 = new List<double>(8);
			List<double> list4 = new List<double>(8);
			for (int i = 0; i < 8; i++)
			{
				faceLandmarkTrackingResult = mediaPipeFaceLandmarkerSidecarTracker.Detect(bitmap, DateTime.UtcNow.AddMilliseconds(i + 1));
				list.Add(faceLandmarkTrackingResult.Diagnostics.ClientPrepareMilliseconds);
				list2.Add(Stage(faceLandmarkTrackingResult.Diagnostics.SidecarStagesMilliseconds, "sharedMemoryRead"));
				list3.Add(Stage(faceLandmarkTrackingResult.Diagnostics.SidecarStagesMilliseconds, "inference"));
				list4.Add(faceLandmarkTrackingResult.Diagnostics.SidecarRoundTripMilliseconds);
			}
			VisionPipelineDiagnostics diagnostics = faceLandmarkTrackingResult.Diagnostics;
			int num = checked(diagnostics.InputWidth * diagnostics.InputHeight * 4);
			int count = faceLandmarkTrackingResult.LandmarkFrame.DenseMeshPoints.Count;
			int num2;
			object obj;
			if (faceLandmarkTrackingResult.LandmarkFrame.HasDenseMesh && diagnostics.Mode == "video-tracking-shared-memory")
			{
				num2 = ((diagnostics.EncodedPayloadBytes == num) ? 1 : 0);
				if (num2 != 0)
				{
					obj = $"MediaPipe shared-memory sidecar passed: {count} landmarks; {diagnostics.InputWidth}x{diagnostics.InputHeight}; {diagnostics.EncodedPayloadBytes:n0} raw bytes; steady median prepare {Median(list):0.##} ms; shared read {Median(list2):0.##} ms; inference {Median(list3):0.##} ms; round trip {Median(list4):0.##} ms.";
					goto IL_0370;
				}
			}
			else
			{
				num2 = 0;
			}
			obj = $"MediaPipe shared-memory sidecar failed: {faceLandmarkTrackingResult.BackendStatus}; mode {diagnostics.Mode}; landmarks {count}; payload {diagnostics.EncodedPayloadBytes:n0}/{num:n0} bytes.";
			goto IL_0370;
			IL_0370:
			string detail = (string)obj;
			return new MediaPipeSharedMemorySidecarSelfTestResult((byte)num2 != 0, detail);
		}
		catch (Exception ex)
		{
			return new MediaPipeSharedMemorySidecarSelfTestResult(Succeeded: false, "MediaPipe shared-memory sidecar failed: " + ex.Message);
		}
	}

	private static BitmapSource LoadBitmap(string path)
	{
		using FileStream bitmapStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		BitmapFrame bitmapFrame = BitmapDecoder.Create(bitmapStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
		if (bitmapFrame.CanFreeze)
		{
			bitmapFrame.Freeze();
		}
		return bitmapFrame;
	}

	private static double Stage(IReadOnlyDictionary<string, double> stages, string name)
	{
		if (!stages.TryGetValue(name, out var value))
		{
			return double.NaN;
		}
		return value;
	}

	private static double Median(List<double> values)
	{
		values.RemoveAll((double value) => !double.IsFinite(value));
		if (values.Count == 0)
		{
			return double.NaN;
		}
		values.Sort();
		int num = values.Count / 2;
		if (values.Count % 2 != 0)
		{
			return values[num];
		}
		return (values[num - 1] + values[num]) * 0.5;
	}
}
