using System;
using System.Collections.Generic;
using System.Linq;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Deca;

public static class DecaProjectionFitEvaluatorSelfTest
{
	public static DecaProjectionFitEvaluatorSelfTestResult Run()
	{
		try
		{
			FaceLandmarkFrame observedMediaPipeFrame = CreateObservedFrame();
			DecaProjectionFitEvaluation decaProjectionFitEvaluation = DecaProjectionFitEvaluator.Evaluate(CreateProjectedLandmarks(), observedMediaPipeFrame, 1000, 1000);
			Require(decaProjectionFitEvaluation.IsMeasured && decaProjectionFitEvaluation.PassesRetentionGate && decaProjectionFitEvaluation.FitConfidencePercent >= 80.0, "An aligned projection did not pass: " + decaProjectionFitEvaluation.Summary);
			DecaProjectionFitEvaluation decaProjectionFitEvaluation2 = DecaProjectionFitEvaluator.Evaluate(CreateProjectedLandmarks(1.45), observedMediaPipeFrame, 1000, 1000);
			Require(decaProjectionFitEvaluation2.IsMeasured && !decaProjectionFitEvaluation2.PassesRetentionGate && decaProjectionFitEvaluation2.FitConfidencePercent < 55.0, "A projection with a 45% facial-width error passed retention.");
			DecaProjectionFitEvaluation decaProjectionFitEvaluation3 = DecaProjectionFitEvaluator.Evaluate(CreateProjectedLandmarks(1.0, 150.0), observedMediaPipeFrame, 1000, 1000);
			Require(decaProjectionFitEvaluation3.IsMeasured && !decaProjectionFitEvaluation3.PassesRetentionGate && decaProjectionFitEvaluation3.FitConfidencePercent < 55.0, "A projection shifted 25% of the observed face width passed retention.");
			return new DecaProjectionFitEvaluatorSelfTestResult(Succeeded: true, "PASS: same-frame DECA/MediaPipe fit accepts aligned anchors and rejects widened or shifted template geometry.");
		}
		catch (Exception ex)
		{
			return new DecaProjectionFitEvaluatorSelfTestResult(Succeeded: false, "FAIL: " + ex.Message);
		}
	}

	private static FaceLandmarkFrame CreateObservedFrame()
	{
		FaceMeshLandmarkPoint[] array = (from index3 in Enumerable.Range(0, 478)
			select new FaceMeshLandmarkPoint
			{
				Index = index3
			}).ToArray();
		int[] array2 = new int[21]
		{
			234, 93, 132, 58, 172, 136, 150, 149, 176, 148,
			152, 377, 400, 378, 379, 365, 397, 288, 361, 323,
			454
		};
		for (int num = 0; num < array2.Length; num++)
		{
			double num2 = (double)num / ((double)array2.Length - 1.0);
			Set(array, array2[num], 0.2 + 0.6 * num2, 0.5 + 0.35 * Math.Sin(Math.PI * num2));
		}
		Set(array, 152, 0.5, 0.85);
		Set(array, 168, 0.5, 0.32);
		Set(array, 1, 0.5, 0.52);
		Set(array, 98, 0.44, 0.55);
		Set(array, 327, 0.56, 0.55);
		Set(array, 61, 0.4, 0.65);
		Set(array, 291, 0.6, 0.65);
		Set(array, 0, 0.5, 0.62);
		Set(array, 17, 0.5, 0.69);
		int[] array3 = new int[16]
		{
			33, 246, 161, 160, 159, 158, 157, 173, 133, 155,
			154, 153, 145, 144, 163, 7
		};
		foreach (int index in array3)
		{
			Set(array, index, 0.37, 0.41);
		}
		array3 = new int[16]
		{
			362, 398, 384, 385, 386, 387, 388, 466, 263, 249,
			390, 373, 374, 380, 381, 382
		};
		foreach (int index2 in array3)
		{
			Set(array, index2, 0.63, 0.41);
		}
		return new FaceLandmarkFrame
		{
			HasFace = true,
			DenseMeshPoints = array
		};
	}

	private static IReadOnlyList<FaceMeshLandmarkPoint> CreateProjectedLandmarks(double scaleX = 1.0, double offsetX = 0.0)
	{
		FaceMeshLandmarkPoint[] array = (from index in Enumerable.Range(0, 68)
			select new FaceMeshLandmarkPoint
			{
				Index = index
			}).ToArray();
		for (int num = 0; num <= 16; num++)
		{
			double num2 = (double)num / 16.0;
			SetProjected(array, num, 200.0 + 600.0 * num2, 500.0 + 350.0 * Math.Sin(Math.PI * num2), scaleX, offsetX);
		}
		SetProjected(array, 27, 500.0, 320.0, scaleX, offsetX);
		SetProjected(array, 30, 500.0, 520.0, scaleX, offsetX);
		SetProjected(array, 31, 440.0, 550.0, scaleX, offsetX);
		SetProjected(array, 35, 560.0, 550.0, scaleX, offsetX);
		for (int num3 = 36; num3 <= 41; num3++)
		{
			SetProjected(array, num3, 370.0, 410.0, scaleX, offsetX);
		}
		for (int num4 = 42; num4 <= 47; num4++)
		{
			SetProjected(array, num4, 630.0, 410.0, scaleX, offsetX);
		}
		SetProjected(array, 48, 400.0, 650.0, scaleX, offsetX);
		SetProjected(array, 54, 600.0, 650.0, scaleX, offsetX);
		SetProjected(array, 51, 500.0, 620.0, scaleX, offsetX);
		SetProjected(array, 57, 500.0, 690.0, scaleX, offsetX);
		return array;
	}

	private static void Set(FaceMeshLandmarkPoint[] points, int index, double x, double y)
	{
		points[index] = new FaceMeshLandmarkPoint
		{
			Index = index,
			X = x,
			Y = y
		};
	}

	private static void SetProjected(FaceMeshLandmarkPoint[] points, int index, double x, double y, double scaleX, double offsetX)
	{
		points[index] = new FaceMeshLandmarkPoint
		{
			Index = index,
			X = (x - 500.0) * scaleX + 500.0 + offsetX,
			Y = y
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
