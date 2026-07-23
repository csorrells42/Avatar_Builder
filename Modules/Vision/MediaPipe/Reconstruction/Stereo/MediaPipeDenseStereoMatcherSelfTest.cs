using System;
using System.Linq;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public static class MediaPipeDenseStereoMatcherSelfTest
{
	private const int Width = 640;

	private const int Height = 480;

	private const int DisparityPixels = 20;

	private const double FocalLengthPixels = 500.0;

	private const double BaselineInches = 4.0;

	public static MediaPipeDenseStereoMatcherSelfTestResult Run()
	{
		MediaPipeDenseStereoDiagnostics diagnostics;
		MediaPipeStereoDenseRigPoint[] array = MediaPipeDenseStereoMatcher.Match(CreatePair(), out diagnostics);
		if (diagnostics.MaximumSampleCount < 15000)
		{
			return new MediaPipeDenseStereoMatcherSelfTestResult(Succeeded: false, $"Dense topology exposed only {diagnostics.MaximumSampleCount:n0} samples.");
		}
		if ((double)array.Length < (double)diagnostics.MaximumSampleCount * 0.7)
		{
			return new MediaPipeDenseStereoMatcherSelfTestResult(Succeeded: false, $"Dense stereo recovered only {array.Length:n0}/{diagnostics.MaximumSampleCount:n0} synthetic matches.");
		}
		double[] array2 = array.Select((MediaPipeStereoDenseRigPoint point) => point.ZInches).Order().ToArray();
		double num = array2[array2.Length / 2];
		double num2 = 100.0;
		if (Math.Abs(num - num2) > 0.75)
		{
			return new MediaPipeDenseStereoMatcherSelfTestResult(Succeeded: false, $"Dense stereo depth was {num:0.000} in; expected {num2:0.000} in.");
		}
		return new MediaPipeDenseStereoMatcherSelfTestResult(Succeeded: true, $"PASS: dense stereo recovered {array.Length:n0}/{diagnostics.MaximumSampleCount:n0} image-matched surface samples at {num:0.000} in depth.");
	}

	private static MediaPipeStereoImagePair CreatePair()
	{
		int num = 2560;
		byte[] array = new byte[num * 480];
		byte[] array2 = new byte[num * 480];
		new Random(5366496).NextBytes(array);
		for (int i = 0; i < 480; i++)
		{
			for (int j = 0; j < 620; j++)
			{
				int num2 = i * num + j * 4;
				int num3 = num2 + 80;
				array2[num2] = array[num3];
				array2[num2 + 1] = array[num3 + 1];
				array2[num2 + 2] = array[num3 + 2];
				array2[num2 + 3] = byte.MaxValue;
			}
		}
		MediaPipeStereoImageLandmark[] array3 = new MediaPipeStereoImageLandmark[478];
		MediaPipeStereoImageLandmark[] array4 = new MediaPipeStereoImageLandmark[478];
		for (int k = 0; k < array3.Length; k++)
		{
			double num4 = 0.28 + (double)(k * 37 % 100) / 230.0;
			double y = 0.24 + (double)(k * 61 % 100) / 190.0;
			array3[k] = new MediaPipeStereoImageLandmark(num4, y, IsValid: true);
			array4[k] = new MediaPipeStereoImageLandmark(num4 - 1.0 / 32.0, y, IsValid: true);
		}
		return new MediaPipeStereoImagePair
		{
			CameraAWidth = 640,
			CameraAHeight = 480,
			CameraAStride = num,
			CameraABgraPixels = array,
			CameraBWidth = 640,
			CameraBHeight = 480,
			CameraBStride = num,
			CameraBBgraPixels = array2,
			CameraALandmarks = array3,
			CameraBLandmarks = array4,
			CalibrationWidth = 640,
			CalibrationHeight = 480,
			CameraAMatrix = new double[9] { 500.0, 0.0, 320.0, 0.0, 500.0, 240.0, 0.0, 0.0, 1.0 },
			CameraADistortion = Array.Empty<double>(),
			CameraBMatrix = new double[9] { 500.0, 0.0, 320.0, 0.0, 500.0, 240.0, 0.0, 0.0, 1.0 },
			CameraBDistortion = Array.Empty<double>(),
			CameraAToBRotation = new double[9] { 1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0 },
			CameraAToBTranslationInches = new double[3] { -4.0, 0.0, 0.0 },
			FundamentalMatrix = new double[9] { 0.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, -1.0, 0.0 }
		};
	}
}
