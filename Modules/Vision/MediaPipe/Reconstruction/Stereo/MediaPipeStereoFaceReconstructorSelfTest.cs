using System;
using System.Collections.Generic;
using System.Linq;

namespace AvatarBuilder.Modules.Vision.MediaPipe.Reconstruction.Stereo;

public static class MediaPipeStereoFaceReconstructorSelfTest
{
	private readonly record struct Point3(double X, double Y, double Z);

	public static MediaPipeStereoFaceReconstructorSelfTestResult Run()
	{
		Point3[] canonical = CreateCanonicalFace();
		MediaPipeStereoFaceReconstructor mediaPipeStereoFaceReconstructor = new MediaPipeStereoFaceReconstructor();
		mediaPipeStereoFaceReconstructor.Reset("stereo-test", "Stereo Test");
		for (int i = 0; i < 36; i++)
		{
			double yawDegrees = -22.0 + (double)(i % 12) * 4.0;
			double pitchDegrees = -8.0 + (double)(i % 5) * 4.0;
			double rollDegrees = -5.0 + (double)(i % 3) * 5.0;
			if (!mediaPipeStereoFaceReconstructor.TryAddFrame(CreateFrame(canonical, i, yawDegrees, pitchDegrees, rollDegrees)))
			{
				return new MediaPipeStereoFaceReconstructorSelfTestResult(Succeeded: false, $"Stereo reconstruction rejected synthetic frame {i}.");
			}
		}
		MediaPipeStereoFaceModel mediaPipeStereoFaceModel = mediaPipeStereoFaceReconstructor.CreateModel();
		if (mediaPipeStereoFaceModel.AcceptedFrameCount != 36 || mediaPipeStereoFaceModel.Vertices.Count != 478 || mediaPipeStereoFaceModel.ConfidentVertexPercent < 70.0 || mediaPipeStereoFaceModel.MedianVertexDeviationInches > 0.01 || Math.Abs(mediaPipeStereoFaceModel.FaceHeightInches - 6.5) > 0.02)
		{
			return new MediaPipeStereoFaceReconstructorSelfTestResult(Succeeded: false, $"Stereo fusion was not rigid-pose invariant: frames {mediaPipeStereoFaceModel.AcceptedFrameCount}, confidence {mediaPipeStereoFaceModel.ConfidentVertexPercent:0.0}%, spread {mediaPipeStereoFaceModel.MedianVertexDeviationInches:0.0000} in, height {mediaPipeStereoFaceModel.FaceHeightInches:0.000} in.");
		}
		MediaPipeStereoFaceReconstructor mediaPipeStereoFaceReconstructor2 = new MediaPipeStereoFaceReconstructor();
		mediaPipeStereoFaceReconstructor2.Restore(mediaPipeStereoFaceReconstructor.CreateState(), "stereo-test", "Stereo Test");
		MediaPipeStereoFaceModel mediaPipeStereoFaceModel2 = mediaPipeStereoFaceReconstructor2.CreateModel();
		if (mediaPipeStereoFaceModel2.AcceptedFrameCount != mediaPipeStereoFaceModel.AcceptedFrameCount || Math.Abs(mediaPipeStereoFaceModel2.Vertices[100].XInches - mediaPipeStereoFaceModel.Vertices[100].XInches) > 1E-09)
		{
			return new MediaPipeStereoFaceReconstructorSelfTestResult(Succeeded: false, "Stereo reconstruction state did not round-trip.");
		}
		if (!mediaPipeStereoFaceReconstructor2.TryAddFrame(CreateFrame(canonical, 37, 0.0, 0.0, 0.0, "stereo-reconstruction-self-test-v2")))
		{
			return new MediaPipeStereoFaceReconstructorSelfTestResult(Succeeded: false, "Stereo reconstruction rejected the first frame from a replacement calibration.");
		}
		MediaPipeStereoFaceModel mediaPipeStereoFaceModel3 = mediaPipeStereoFaceReconstructor2.CreateModel();
		if (mediaPipeStereoFaceModel3.AcceptedFrameCount != 1 || mediaPipeStereoFaceModel3.CalibrationId != "stereo-reconstruction-self-test-v2")
		{
			return new MediaPipeStereoFaceReconstructorSelfTestResult(Succeeded: false, "Stereo reconstruction mixed geometry from different physical calibrations.");
		}
		MediaPipeStereoFaceState state = new MediaPipeStereoFaceState
		{
			SubjectId = "raw-evidence-test",
			SubjectDisplayName = "Raw Evidence Test",
			RawTriangulatedObservationCount = 11L,
			RawUnstoredObservationCount = 2L,
			RawPointBins =
			[
				new MediaPipeStereoRawPointBinState
				{
					BinX = 1,
					BinY = 2,
					BinZ = 3,
					MeanXInches = 0.04,
					MeanYInches = 0.08,
					MeanZInches = 0.12,
					ObservationCount = 3L,
					AcceptedObservationCount = 2L
				},
				new MediaPipeStereoRawPointBinState
				{
					BinX = -1,
					BinY = -2,
					BinZ = -3,
					MeanXInches = -0.04,
					MeanYInches = -0.08,
					MeanZInches = -0.12,
					ObservationCount = 6L,
					AcceptedObservationCount = 5L
				}
			]
		};
		MediaPipeStereoFaceReconstructor mediaPipeStereoFaceReconstructor3 = new MediaPipeStereoFaceReconstructor();
		mediaPipeStereoFaceReconstructor3.Restore(state, "raw-evidence-test", "Raw Evidence Test");
		MediaPipeStereoFaceModel mediaPipeStereoFaceModel4 = mediaPipeStereoFaceReconstructor3.CreateModel();
		MediaPipeStereoFaceState mediaPipeStereoFaceState = mediaPipeStereoFaceReconstructor3.CreateState();
		string text = MediaPipeStereoRawEvidenceViewerPage.Build(mediaPipeStereoFaceState);
		if (mediaPipeStereoFaceModel4.RawTriangulatedObservationCount != 11 || mediaPipeStereoFaceModel4.RawPointBinCount != 2 || mediaPipeStereoFaceModel4.RawMaximumBinObservationCount != 6 || mediaPipeStereoFaceState.RawPointBins.Sum((MediaPipeStereoRawPointBinState bin) => bin.ObservationCount) != 9 || !text.Contains("Raw Stereo Evidence", StringComparison.Ordinal) || !text.Contains("#ff4057", StringComparison.Ordinal) || !text.Contains("#b889ff", StringComparison.Ordinal))
		{
			return new MediaPipeStereoFaceReconstructorSelfTestResult(Succeeded: false, "Raw stereo evidence did not round-trip into its rainbow viewer.");
		}
		MediaPipeStereoFaceState mediaPipeStereoFaceState2 = CreateProbabilityFaceState();
		MediaPipeStereoProbabilityFaceModel mediaPipeStereoProbabilityFaceModel = MediaPipeStereoProbabilityFaceBuilder.Build(mediaPipeStereoFaceState2, new MediaPipeStereoFaceModel
		{
			SubjectId = mediaPipeStereoFaceState2.SubjectId,
			SubjectDisplayName = mediaPipeStereoFaceState2.SubjectDisplayName,
			FaceWidthInches = 5.2,
			FaceHeightInches = 6.4,
			MeasuredDepthInches = 1.2
		});
		string text2 = MediaPipeStereoProbabilityFaceViewerPage.Build(mediaPipeStereoProbabilityFaceModel);
		MediaPipeStereoProbabilityFaceVertex? mediaPipeStereoProbabilityFaceVertex = mediaPipeStereoProbabilityFaceModel.Vertices.OrderBy((MediaPipeStereoProbabilityFaceVertex vertex) => Math.Abs(vertex.XInches) + Math.Abs(vertex.YInches)).FirstOrDefault();
		MediaPipeStereoProbabilityFaceVertex? mediaPipeStereoProbabilityFaceVertex2 = mediaPipeStereoProbabilityFaceModel.Vertices.OrderByDescending((MediaPipeStereoProbabilityFaceVertex vertex) => Math.Abs(vertex.XInches)).FirstOrDefault();
		MediaPipeStereoProbabilityFaceVertex? mediaPipeStereoProbabilityFaceVertex3 = mediaPipeStereoProbabilityFaceModel.SmoothedVertices.OrderBy((MediaPipeStereoProbabilityFaceVertex vertex) => Math.Abs(vertex.XInches) + Math.Abs(vertex.YInches)).FirstOrDefault();
		MediaPipeStereoProbabilityFaceVertex? mediaPipeStereoProbabilityFaceVertex4 = mediaPipeStereoProbabilityFaceModel.SmoothedVertices.OrderByDescending((MediaPipeStereoProbabilityFaceVertex vertex) => Math.Abs(vertex.XInches)).FirstOrDefault();
		if (!mediaPipeStereoProbabilityFaceModel.HasSurface || !mediaPipeStereoProbabilityFaceModel.HasSmoothedSurface || mediaPipeStereoProbabilityFaceModel.Vertices.Count < 800 || mediaPipeStereoProbabilityFaceModel.Triangles.Count < 1000 || mediaPipeStereoProbabilityFaceModel.SmoothedVertices.Count < mediaPipeStereoProbabilityFaceModel.Vertices.Count || mediaPipeStereoProbabilityFaceModel.SmoothedTriangles.Count < mediaPipeStereoProbabilityFaceModel.Triangles.Count || mediaPipeStereoProbabilityFaceVertex == null || mediaPipeStereoProbabilityFaceVertex2 == null || mediaPipeStereoProbabilityFaceVertex3 == null || mediaPipeStereoProbabilityFaceVertex4 == null || mediaPipeStereoProbabilityFaceVertex.ZInches <= mediaPipeStereoProbabilityFaceVertex2.ZInches + 0.35 || mediaPipeStereoProbabilityFaceVertex3.ZInches <= mediaPipeStereoProbabilityFaceVertex4.ZInches + 0.3 || !text2.Contains("Probability Face", StringComparison.Ordinal) || !text2.Contains("Smooth Face", StringComparison.Ordinal) || !text2.Contains("gl.TRIANGLES", StringComparison.Ordinal))
		{
			return new MediaPipeStereoFaceReconstructorSelfTestResult(Succeeded: false, $"Probability surface extraction failed: {mediaPipeStereoProbabilityFaceModel.Vertices.Count} vertices, {mediaPipeStereoProbabilityFaceModel.Triangles.Count} triangles.");
		}
		return new MediaPipeStereoFaceReconstructorSelfTestResult(Succeeded: true, $"PASS: calibrated stereo fusion removed rigid head motion across {mediaPipeStereoFaceModel.AcceptedFrameCount} frames; median spread {mediaPipeStereoFaceModel.MedianVertexDeviationInches:0.0000} in; raw evidence round-trip passed.");
	}

	private static MediaPipeStereoFaceState CreateProbabilityFaceState()
	{
		List<MediaPipeStereoRawPointBinState> list = new List<MediaPipeStereoRawPointBinState>();
		for (int i = -26; i <= 26; i++)
		{
			for (int j = -22; j <= 22; j++)
			{
				double num = (double)j * 0.12;
				double num2 = (double)i * 0.12;
				double num3 = 1.0 - Math.Pow(num / 2.75, 2.0) - Math.Pow(num2 / 3.35, 2.0);
				if (!(num3 <= 0.0))
				{
					double num4 = Math.Sqrt(num3) * 1.15;
					list.Add(new MediaPipeStereoRawPointBinState
					{
						BinX = j * 3,
						BinY = i * 3,
						BinZ = (int)Math.Round(num4 / 0.04),
						MeanXInches = num,
						MeanYInches = num2,
						MeanZInches = num4,
						ObservationCount = 14L,
						AcceptedObservationCount = 13L
					});
					list.Add(new MediaPipeStereoRawPointBinState
					{
						BinX = j * 3,
						BinY = i * 3,
						BinZ = (int)Math.Round((num4 - 0.7) / 0.04),
						MeanXInches = num,
						MeanYInches = num2,
						MeanZInches = num4 - 0.7,
						ObservationCount = 3L,
						AcceptedObservationCount = 3L
					});
				}
			}
		}
		return new MediaPipeStereoFaceState
		{
			SubjectId = "probability-test",
			SubjectDisplayName = "Probability Test",
			CalibrationId = "probability-calibration",
			UpdatedAtUtc = DateTime.UtcNow,
			RawTriangulatedObservationCount = list.Sum((MediaPipeStereoRawPointBinState bin) => bin.ObservationCount),
			RawPointBins = list
		};
	}

	private static Point3[] CreateCanonicalFace()
	{
		Point3[] array = new Point3[478];
		for (int i = 0; i < array.Length; i++)
		{
			double num = (double)(i % 24) / 23.0;
			double num2 = (double)(i / 24) / 19.0;
			double num3 = (num - 0.5) * 5.2;
			double num4 = (0.5 - num2) * 6.2;
			double d = Math.Max(0.0, 1.0 - Math.Pow(num3 / 3.0, 2.0) - Math.Pow(num4 / 4.0, 2.0));
			array[i] = new Point3(num3, num4, Math.Sqrt(d) * 1.15);
		}
		array[33] = new Point3(-1.45, 0.0, 0.18);
		array[133] = new Point3(-1.05, 0.0, 0.18);
		array[263] = new Point3(1.45, 0.0, 0.18);
		array[362] = new Point3(1.05, 0.0, 0.18);
		array[10] = new Point3(0.0, 3.0, 0.0);
		array[152] = new Point3(0.0, -3.5, 0.0);
		array[1] = new Point3(0.0, -0.65, 1.2);
		array[234] = new Point3(-2.7, -0.3, 0.0);
		array[454] = new Point3(2.7, -0.3, 0.0);
		return array;
	}

	private static MediaPipeStereoGeometryFrame CreateFrame(IReadOnlyList<Point3> canonical, int frameIndex, double yawDegrees, double pitchDegrees, double rollDegrees, string calibrationId = "stereo-reconstruction-self-test-v1")
	{
		MediaPipeStereoRigLandmark[] array = new MediaPipeStereoRigLandmark[canonical.Count];
		for (int i = 0; i < array.Length; i++)
		{
			Point3 point = Transform(canonical[i], yawDegrees, pitchDegrees, rollDegrees);
			array[i] = new MediaPipeStereoRigLandmark(i, point.X + 4.0, point.Y + 2.0, point.Z + 32.0, IsValid: true, 0.45, 1.0, 1.0, IsDirectlyMeasured: true);
		}
		return new MediaPipeStereoGeometryFrame
		{
			CalibrationId = calibrationId,
			CapturedAtUtc = DateTime.UtcNow.AddMilliseconds((double)frameIndex * 40.0),
			PairSkew = TimeSpan.FromMilliseconds(12.0),
			BaselineInches = 18.926,
			FrameReprojectionResidualPercent = 0.8,
			CameraATrackingConfidence = 0.95,
			CameraBTrackingConfidence = 0.96,
			Landmarks = array
		};
	}

	private static Point3 Transform(Point3 point, double yawDegrees, double pitchDegrees, double rollDegrees)
	{
		double num = yawDegrees * Math.PI / 180.0;
		double num2 = pitchDegrees * Math.PI / 180.0;
		double num3 = rollDegrees * Math.PI / 180.0;
		double num4 = Math.Cos(num);
		double num5 = Math.Sin(num);
		double num6 = Math.Cos(num2);
		double num7 = Math.Sin(num2);
		double num8 = Math.Cos(num3);
		double num9 = Math.Sin(num3);
		Point3 point2 = new Point3(num4 * point.X + num5 * point.Z, point.Y, (0.0 - num5) * point.X + num4 * point.Z);
		Point3 point3 = new Point3(point2.X, num6 * point2.Y - num7 * point2.Z, num7 * point2.Y + num6 * point2.Z);
		return new Point3(num8 * point3.X - num9 * point3.Y, num9 * point3.X + num8 * point3.Y, point3.Z);
	}
}
