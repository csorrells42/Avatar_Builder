using System;
using System.Collections.Generic;
using System.Windows;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceFrameGeometryEstimator
{
	private readonly record struct MeshPoint(double X, double Y);

	private readonly record struct FaceBounds(double Left, double Top, double Right, double Bottom)
	{
		public double Width => Math.Max(0.0, Right - Left);

		public double Height => Math.Max(0.0, Bottom - Top);

		public double CenterX => (Left + Right) / 2.0;

		public double CenterY => (Top + Bottom) / 2.0;
	}

	private static readonly int[] EyeA = new int[16]
	{
		33, 246, 161, 160, 159, 158, 157, 173, 133, 155,
		154, 153, 145, 144, 163, 7
	};

	private static readonly int[] EyeB = new int[16]
	{
		362, 398, 384, 385, 386, 387, 388, 466, 263, 249,
		390, 373, 374, 380, 381, 382
	};

	public FaceFrameGeometry Estimate(FaceFrameGeometryEstimatorInput input)
	{
		ArgumentNullException.ThrowIfNull(input, "input");
		FaceLandmarkFrame frame = input.Frame;
		if (!frame.HasFace)
		{
			return FaceFrameGeometry.None;
		}
		IReadOnlyList<FaceMeshLandmarkPoint> densePoints = (frame.HasDenseMesh ? frame.DenseMeshPoints : null);
		(double, double, double, string) pose = EstimateOrientationDegrees(frame, densePoints);
		FaceBounds? faceBounds = EstimateFaceBounds(frame);
		double interEyeFrameWidth;
		double? num = (TryGetInterEyeFrameWidth(frame, densePoints, out interEyeFrameWidth) ? new double?(interEyeFrameWidth) : ((double?)null));
		(double?, bool, string, string) apparentDistance = EstimateApparentDistance(input.Calibration, pose.Item1, num);
		(double?, bool, string) relativeScale = EstimateRelativeScale(input.Calibration, apparentDistance.Item1);
		(double?, bool, bool, bool, string) tuple = EstimateDistanceInches(input, pose.Item1, num);
		double num2 = CalculateConfidence(frame, num, tuple.Item1);
		double num3 = CalculateZConfidence(frame, num, apparentDistance, relativeScale, tuple);
		string statusLine = FormatStatus(pose, tuple, apparentDistance.Item1, relativeScale.Item1, faceBounds, num, num2, num3);
		FaceFrameGeometry obj = new FaceFrameGeometry
		{
			HasFace = true,
			CapturedAtUtc = frame.CapturedAtUtc,
			YawDegrees = Round(pose.Item1),
			PitchDegrees = Round(pose.Item2),
			RollDegrees = Round(pose.Item3),
			XHorizontalPercent = Round((faceBounds?.CenterX ?? 0.5) * 100.0),
			YVerticalPercent = Round((faceBounds?.CenterY ?? 0.5) * 100.0),
			DistanceInches = RoundNullable(tuple.Item1),
			ApparentDistanceUnits = RoundNullable(apparentDistance.Item1),
			FaceFillWidthPercent = RoundNullable(faceBounds.HasValue ? new double?(faceBounds.GetValueOrDefault().Width * 100.0) : ((double?)null)),
			FaceFillHeightPercent = RoundNullable(faceBounds.HasValue ? new double?(faceBounds.GetValueOrDefault().Height * 100.0) : ((double?)null)),
			RelativeDistanceScale = RoundNullable(relativeScale.Item1),
			InterEyeFrameWidthPercent = RoundNullable(num * 100.0),
			ConfidencePercent = Round(num2),
			ZConfidencePercent = Round(num3),
			DistanceCalibrated = tuple.Item2,
			ZUsesCameraFov = (apparentDistance.Item2 || tuple.Item3),
			ZUsesLearnedReference = (relativeScale.Item2 || tuple.Item4),
			ZEstimateKind = ChooseZEstimateKind(tuple, apparentDistance, relativeScale),
			ZQualityLabel = FormatZQualityLabel(num3, tuple, apparentDistance, relativeScale),
			RotationSource = pose.Item4
		};
		double? num4;
		(num4, _, _, _, _) = tuple;
		object distanceSource;
		if (!num4.HasValue || !(num4.GetValueOrDefault() > 0.0))
		{
			num4 = apparentDistance.Item1;
			distanceSource = ((num4.HasValue && num4.GetValueOrDefault() > 0.0) ? apparentDistance.Item3 : tuple.Item5);
		}
		else
		{
			distanceSource = tuple.Item5;
		}
		obj.DistanceSource = (string)distanceSource;
		obj.ReferenceScaleSource = relativeScale.Item3;
		obj.ScaleCaveat = apparentDistance.Item4;
		obj.StatusLine = statusLine;
		return obj;
	}

	private static (double YawDegrees, double PitchDegrees, double RollDegrees, string Source) EstimateOrientationDegrees(FaceLandmarkFrame frame, IReadOnlyList<FaceMeshLandmarkPoint>? densePoints)
	{
		(double, double, double) pose;
		bool flag = TryEstimatePoseFromMatrix(frame.FacialTransformationMatrix, out pose);
		(double, double, double) pose2;
		bool flag2 = TryEstimatePoseFromDenseMesh(frame, densePoints, out pose2);
		if (flag && flag2 && ShouldPreferDensePose(pose, pose2))
		{
			return (YawDegrees: pose2.Item1, PitchDegrees: pose2.Item2, RollDegrees: pose2.Item3, Source: "dense mesh geometry; transform matrix looked flat");
		}
		if (flag)
		{
			return (YawDegrees: pose.Item1, PitchDegrees: pose.Item2, RollDegrees: pose.Item3, Source: "facial transform matrix");
		}
		if (flag2)
		{
			return (YawDegrees: pose2.Item1, PitchDegrees: pose2.Item2, RollDegrees: pose2.Item3, Source: "dense mesh geometry");
		}
		return (YawDegrees: frame.HeadYawDegrees, PitchDegrees: frame.HeadPitchDegrees, RollDegrees: frame.HeadRollDegrees, Source: "landmark frame pose");
	}

	private static bool ShouldPreferDensePose((double YawDegrees, double PitchDegrees, double RollDegrees) matrixPose, (double YawDegrees, double PitchDegrees, double RollDegrees) densePose)
	{
		double num = Math.Max(Math.Abs(matrixPose.YawDegrees), Math.Abs(matrixPose.PitchDegrees));
		double num2 = Math.Abs(matrixPose.RollDegrees);
		double num3 = Math.Max(Math.Abs(densePose.YawDegrees), Math.Abs(densePose.PitchDegrees));
		if (num <= 1.75 && num2 <= 1.75)
		{
			return num3 >= 8.0;
		}
		return false;
	}

	private static bool TryEstimatePoseFromDenseMesh(FaceLandmarkFrame frame, IReadOnlyList<FaceMeshLandmarkPoint>? points, out (double YawDegrees, double PitchDegrees, double RollDegrees) pose)
	{
		pose = default((double, double, double));
		if (!frame.HasDenseMesh || points == null)
		{
			return false;
		}
		if (!TryLandmark(points, 1, out FaceMeshLandmarkPoint point) || !TryLandmark(points, 10, out FaceMeshLandmarkPoint point2) || !TryLandmark(points, 152, out FaceMeshLandmarkPoint point3) || !TryLandmark(points, 234, out FaceMeshLandmarkPoint point4) || !TryLandmark(points, 454, out FaceMeshLandmarkPoint point5))
		{
			return false;
		}
		double num = (point4.X + point5.X) / 2.0;
		double num2 = Math.Abs(point5.X - point4.X) / 2.0;
		double num3 = point3.Y - point2.Y;
		if (num2 <= 0.001 || num3 <= 0.001)
		{
			return false;
		}
		double num4 = (point.X - num) / num2;
		double num5 = (point5.Z - point4.Z) / Math.Max(0.02, num2);
		double item = Math.Clamp(num4 * 24.0 + num5 * 10.0, -45.0, 45.0);
		double item2 = (double.IsFinite(frame.HeadPitchDegrees) ? frame.HeadPitchDegrees : 0.0);
		double value = (frame.HasEyeContours ? EstimateRollDegrees(frame.LeftEyeContour, frame.RightEyeContour) : frame.HeadRollDegrees);
		pose = (YawDegrees: item, PitchDegrees: item2, RollDegrees: Math.Clamp(value, -55.0, 55.0));
		return true;
	}

	private static bool TryLandmark(IReadOnlyList<FaceMeshLandmarkPoint> points, int index, out FaceMeshLandmarkPoint point)
	{
		if ((uint)index < (uint)points.Count && points[index].Index == index)
		{
			point = points[index];
			return true;
		}
		foreach (FaceMeshLandmarkPoint point2 in points)
		{
			if (point2.Index == index)
			{
				point = point2;
				return true;
			}
		}
		point = new FaceMeshLandmarkPoint
		{
			Index = index
		};
		return false;
	}

	private static double EstimateRollDegrees(IReadOnlyList<Point> leftEye, IReadOnlyList<Point> rightEye)
	{
		if (leftEye.Count == 0 || rightEye.Count == 0)
		{
			return 0.0;
		}
		if (!TryGetCenter(leftEye, out var point) || !TryGetCenter(rightEye, out var point2))
		{
			return 0.0;
		}
		return Math.Atan2(point2.Y - point.Y, point2.X - point.X) * 180.0 / Math.PI;
	}

	private static bool TryEstimatePoseFromMatrix(IReadOnlyList<double> values, out (double YawDegrees, double PitchDegrees, double RollDegrees) pose)
	{
		pose = default((double, double, double));
		if (values.Count < 16)
		{
			return false;
		}
		for (int i = 0; i < values.Count; i++)
		{
			if (!double.IsFinite(values[i]))
			{
				return false;
			}
		}
		double y = values[2];
		double y2 = values[4];
		double x = values[5];
		double num = values[6];
		double x2 = values[10];
		double value = Math.Atan2(y, x2) * 180.0 / Math.PI;
		double value2 = Math.Asin(Math.Clamp(0.0 - num, -1.0, 1.0)) * 180.0 / Math.PI;
		double value3 = Math.Atan2(y2, x) * 180.0 / Math.PI;
		if (Math.Abs(value) > 80.0 || Math.Abs(value2) > 70.0 || Math.Abs(value3) > 80.0)
		{
			return false;
		}
		pose = (YawDegrees: Math.Clamp(value, -55.0, 55.0), PitchDegrees: Math.Clamp(value2, -45.0, 45.0), RollDegrees: Math.Clamp(value3, -55.0, 55.0));
		return true;
	}

	private static bool TryGetInterEyeFrameWidth(FaceLandmarkFrame frame, IReadOnlyList<FaceMeshLandmarkPoint>? densePoints, out double interEyeFrameWidth)
	{
		interEyeFrameWidth = 0.0;
		if (densePoints != null && TryGetCenter(densePoints, EyeA, out var point) && TryGetCenter(densePoints, EyeB, out var point2))
		{
			interEyeFrameWidth = Math.Abs(point.X - point2.X);
			return interEyeFrameWidth > 0.0001;
		}
		if (TryGetCenter(frame.LeftEyeContour, out var point3) && TryGetCenter(frame.RightEyeContour, out var point4))
		{
			interEyeFrameWidth = Math.Abs(point3.X - point4.X);
			return interEyeFrameWidth > 0.0001;
		}
		return false;
	}

	private static (double? DistanceInches, bool Calibrated, bool UsesCameraFov, bool UsesLearnedReference, string Source) EstimateDistanceInches(FaceFrameGeometryEstimatorInput input, double yawDegrees, double? interEyeFrameWidth)
	{
		if (!interEyeFrameWidth.HasValue || !(interEyeFrameWidth.GetValueOrDefault() > 0.0))
		{
			return (DistanceInches: null, Calibrated: false, UsesCameraFov: false, UsesLearnedReference: false, Source: "waiting for eye-span measurement");
		}
		double num = CalculateYawCosine(yawDegrees);
		FaceFrameGeometryCalibration faceFrameGeometryCalibration = input.Calibration ?? FaceFrameGeometryCalibration.None;
		if (faceFrameGeometryCalibration.HasDistanceReference)
		{
			return (DistanceInches: faceFrameGeometryCalibration.ReferenceDistanceInches.Value * faceFrameGeometryCalibration.ReferenceInterEyeFrameWidth.Value * num / interEyeFrameWidth.Value, Calibrated: true, UsesCameraFov: false, UsesLearnedReference: true, Source: "known-distance face calibration");
		}
		if (faceFrameGeometryCalibration.HasCameraIntrinsics)
		{
			int? frameWidthPixels = input.FrameWidthPixels;
			if (frameWidthPixels.HasValue && frameWidthPixels.GetValueOrDefault() > 0)
			{
				double num2 = faceFrameGeometryCalibration.CameraHorizontalFovDegrees.Value * Math.PI / 180.0;
				double num3 = (double)input.FrameWidthPixels.Value / (2.0 * Math.Tan(num2 / 2.0));
				double num4 = interEyeFrameWidth.Value * (double)input.FrameWidthPixels.Value;
				if (num4 > 0.1)
				{
					return (DistanceInches: faceFrameGeometryCalibration.InterpupillaryDistanceInches.Value * num3 * num / num4, Calibrated: false, UsesCameraFov: true, UsesLearnedReference: false, Source: "camera FOV and interpupillary-distance estimate");
				}
			}
		}
		return (DistanceInches: null, Calibrated: false, UsesCameraFov: false, UsesLearnedReference: false, Source: "distance needs calibration");
	}

	private static (double? Units, bool UsesCameraFov, string Source, string Caveat) EstimateApparentDistance(FaceFrameGeometryCalibration? calibration, double yawDegrees, double? interEyeFrameWidth)
	{
		if (!interEyeFrameWidth.HasValue || !(interEyeFrameWidth.GetValueOrDefault() > 0.0))
		{
			return (Units: null, UsesCameraFov: false, Source: "waiting for eye-span measurement", Caveat: "No apparent scale is available until both eye centers are visible.");
		}
		double num = CalculateYawCosine(yawDegrees);
		double? num2 = calibration?.CameraHorizontalFovDegrees;
		if (num2.HasValue)
		{
			double valueOrDefault = num2.GetValueOrDefault();
			if (valueOrDefault > 0.0 && valueOrDefault < 180.0)
			{
				double num3 = calibration.CameraHorizontalFovDegrees.Value * Math.PI / 180.0;
				return (Units: 1.0 / (2.0 * Math.Tan(num3 / 2.0)) * num / interEyeFrameWidth.Value, UsesCameraFov: true, Source: $"apparent face units from eye span and {calibration.CameraHorizontalFovDegrees.Value:0.#} deg horizontal FOV", Caveat: "Zoom changes effective FOV, so this is apparent camera-space distance until zoom/FOV is calibrated.");
			}
		}
		return (Units: num / interEyeFrameWidth.Value, UsesCameraFov: false, Source: "apparent face units from eye span and current zoom", Caveat: "No camera FOV is known, so apparent distance is relative to the current camera framing.");
	}

	private static (double? Scale, bool UsesLearnedReference, string Source) EstimateRelativeScale(FaceFrameGeometryCalibration? calibration, double? apparentDistanceUnits)
	{
		if (!apparentDistanceUnits.HasValue || !(apparentDistanceUnits.GetValueOrDefault() > 0.0))
		{
			return (Scale: null, UsesLearnedReference: false, Source: "waiting for apparent Z measurement");
		}
		if (calibration == null || !calibration.HasApparentReference)
		{
			return (Scale: null, UsesLearnedReference: false, Source: "waiting for learned reference face scale");
		}
		double? item = EstimateApparentDistance(calibration, 0.0, calibration.ReferenceInterEyeFrameWidth).Units;
		if (!item.HasValue || !(item.GetValueOrDefault() > 0.0))
		{
			return (Scale: null, UsesLearnedReference: false, Source: "waiting for learned reference face scale");
		}
		string item2 = (string.IsNullOrWhiteSpace(calibration.ReferenceSource) ? "learned reference face scale" : calibration.ReferenceSource);
		return (Scale: apparentDistanceUnits.Value / item.Value, UsesLearnedReference: true, Source: item2);
	}

	private static double CalculateYawCosine(double yawDegrees)
	{
		return Math.Clamp(Math.Cos(Math.Abs(yawDegrees) * Math.PI / 180.0), 0.45, 1.0);
	}

	private static double CalculateConfidence(FaceLandmarkFrame frame, double? interEyeFrameWidth, double? distanceInches)
	{
		double num = Math.Clamp(frame.TrackingConfidence, 0.0, 1.0) * 55.0;
		num += Math.Clamp(frame.EyeConfidence, 0.0, 1.0) * 25.0;
		num += ((frame.FacialTransformationMatrix.Count >= 16) ? 12.0 : 4.0);
		num += ((interEyeFrameWidth.HasValue && interEyeFrameWidth.GetValueOrDefault() > 0.0) ? 8.0 : 0.0);
		if (distanceInches.HasValue && distanceInches.GetValueOrDefault() > 0.0)
		{
			num = Math.Min(100.0, num + 4.0);
		}
		return Math.Clamp(num, 0.0, 100.0);
	}

	private static double CalculateZConfidence(FaceLandmarkFrame frame, double? interEyeFrameWidth, (double? Units, bool UsesCameraFov, string Source, string Caveat) apparentDistance, (double? Scale, bool UsesLearnedReference, string Source) relativeScale, (double? DistanceInches, bool Calibrated, bool UsesCameraFov, bool UsesLearnedReference, string Source) distance)
	{
		if (interEyeFrameWidth.HasValue && interEyeFrameWidth.GetValueOrDefault() > 0.0)
		{
			double? num;
			(num, _, _, _) = apparentDistance;
			if (num.HasValue && num.GetValueOrDefault() > 0.0)
			{
				double num2 = Math.Clamp(frame.TrackingConfidence, 0.0, 1.0) * 34.0 + Math.Clamp(frame.EyeConfidence, 0.0, 1.0) * 26.0 + ((frame.FacialTransformationMatrix.Count >= 16) ? 10.0 : 4.0) + ((apparentDistance.UsesCameraFov || distance.UsesCameraFov) ? 10.0 : 0.0) + ((relativeScale.UsesLearnedReference || distance.UsesLearnedReference) ? 12.0 : 0.0);
				double num3;
				if (!distance.Calibrated)
				{
					num = distance.DistanceInches;
					num3 = ((num.HasValue && num.GetValueOrDefault() > 0.0) ? 6.0 : 0.0);
				}
				else
				{
					num3 = 14.0;
				}
				return Math.Clamp(num2 + num3, 0.0, 100.0);
			}
		}
		return 0.0;
	}

	private static string ChooseZEstimateKind((double? DistanceInches, bool Calibrated, bool UsesCameraFov, bool UsesLearnedReference, string Source) distance, (double? Units, bool UsesCameraFov, string Source, string Caveat) apparentDistance, (double? Scale, bool UsesLearnedReference, string Source) relativeScale)
	{
		double? num;
		if (distance.Calibrated)
		{
			(num, _, _, _, _) = distance;
			if (num.HasValue && num.GetValueOrDefault() > 0.0)
			{
				return "calibrated-known-distance";
			}
		}
		if (distance.UsesCameraFov)
		{
			(num, _, _, _, _) = distance;
			if (num.HasValue && num.GetValueOrDefault() > 0.0)
			{
				return "camera-fov-ipd-estimate";
			}
		}
		if (apparentDistance.UsesCameraFov && relativeScale.UsesLearnedReference)
		{
			(num, _, _) = relativeScale;
			if (num.HasValue && num.GetValueOrDefault() > 0.0)
			{
				return "camera-fov-learned-reference-apparent-scale";
			}
		}
		if (apparentDistance.UsesCameraFov)
		{
			(num, _, _, _) = apparentDistance;
			if (num.HasValue && num.GetValueOrDefault() > 0.0)
			{
				return "camera-fov-apparent-scale";
			}
		}
		if (relativeScale.UsesLearnedReference)
		{
			(num, _, _) = relativeScale;
			if (num.HasValue && num.GetValueOrDefault() > 0.0)
			{
				return "learned-reference-apparent-scale";
			}
		}
		(num, _, _, _) = apparentDistance;
		if (!num.HasValue || !(num.GetValueOrDefault() > 0.0))
		{
			return "waiting";
		}
		return "apparent-scale-only";
	}

	private static string FormatZQualityLabel(double zConfidence, (double? DistanceInches, bool Calibrated, bool UsesCameraFov, bool UsesLearnedReference, string Source) distance, (double? Units, bool UsesCameraFov, string Source, string Caveat) apparentDistance, (double? Scale, bool UsesLearnedReference, string Source) relativeScale)
	{
		var (num, _, _, _) = apparentDistance;
		if (!num.HasValue || !(num.GetValueOrDefault() > 0.0))
		{
			return "waiting for eye-span Z";
		}
		string text = ((zConfidence >= 86.0) ? "strong" : ((zConfidence >= 70.0) ? "usable" : ((!(zConfidence >= 50.0)) ? "weak" : "rough")));
		string text2 = text;
		if (distance.Calibrated)
		{
			return text2 + " calibrated Z";
		}
		if (distance.UsesCameraFov)
		{
			return text2 + " camera-FOV Z estimate";
		}
		if (relativeScale.UsesLearnedReference)
		{
			return text2 + " learned-reference apparent Z";
		}
		return text2 + " apparent Z";
	}

	private static string FormatStatus((double YawDegrees, double PitchDegrees, double RollDegrees, string Source) pose, (double? DistanceInches, bool Calibrated, bool UsesCameraFov, bool UsesLearnedReference, string Source) distance, double? apparentDistanceUnits, double? relativeDistanceScale, FaceBounds? faceBounds, double? interEyeFrameWidth, double confidence, double zConfidence)
	{
		object obj;
		if (apparentDistanceUnits.HasValue)
		{
			double valueOrDefault = apparentDistanceUnits.GetValueOrDefault();
			obj = $"{valueOrDefault:0.##} apparent face units";
		}
		else
		{
			obj = "distance waiting";
		}
		string value = (string)obj;
		var (num, _, _, _, _) = distance;
		if (num.HasValue)
		{
			double valueOrDefault2 = num.GetValueOrDefault();
			value = (distance.Calibrated ? $"{value} ({valueOrDefault2:0.#} in calibrated)" : $"{value} ({valueOrDefault2:0.#} in est)");
		}
		else if (interEyeFrameWidth.HasValue)
		{
			double valueOrDefault3 = interEyeFrameWidth.GetValueOrDefault();
			value = $"{value}; eye span {valueOrDefault3 * 100.0:0.#}% frame";
		}
		object obj2;
		if (relativeDistanceScale.HasValue)
		{
			double valueOrDefault4 = relativeDistanceScale.GetValueOrDefault();
			obj2 = $" | Z ref {valueOrDefault4:0.##}x";
		}
		else
		{
			obj2 = "";
		}
		string value2 = (string)obj2;
		object obj3;
		if (faceBounds.HasValue)
		{
			FaceBounds valueOrDefault5 = faceBounds.GetValueOrDefault();
			obj3 = $" | center X/Y/Z {valueOrDefault5.CenterX * 100.0:0.#}%, {valueOrDefault5.CenterY * 100.0:0.#}%, {apparentDistanceUnits?.ToString("0.##") ?? "--"} | fill {valueOrDefault5.Width * 100.0:0.#}% x {valueOrDefault5.Height * 100.0:0.#}%";
		}
		else
		{
			obj3 = "";
		}
		string value3 = (string)obj3;
		return $"Face frame: {value}{value2}{value3} | tracker A around X {pose.PitchDegrees:0.#} deg | B around Y {pose.YawDegrees:0.#} deg | C around Z {pose.RollDegrees:0.#} deg | frame q {confidence:0}% | Z q {zConfidence:0}%";
	}

	private static FaceBounds? EstimateFaceBounds(FaceLandmarkFrame frame)
	{
		if (TryEstimateBounds(frame.DenseMeshPoints, out var bounds))
		{
			return bounds;
		}
		if (!TryEstimateBounds(frame.FaceContour, out var bounds2))
		{
			return null;
		}
		return bounds2;
	}

	private static bool TryGetCenter(IReadOnlyList<FaceMeshLandmarkPoint> points, IReadOnlyList<int> indices, out MeshPoint point)
	{
		double num = 0.0;
		double num2 = 0.0;
		int num3 = 0;
		foreach (int index in indices)
		{
			if (TryLandmark(points, index, out FaceMeshLandmarkPoint point2))
			{
				num += point2.X;
				num2 += point2.Y;
				num3++;
			}
		}
		if (num3 == 0)
		{
			point = default(MeshPoint);
			return false;
		}
		point = new MeshPoint(num / (double)num3, num2 / (double)num3);
		return true;
	}

	private static bool TryGetCenter(IReadOnlyList<Point> points, out MeshPoint point)
	{
		if (points.Count == 0)
		{
			point = default(MeshPoint);
			return false;
		}
		double num = 0.0;
		double num2 = 0.0;
		foreach (Point point2 in points)
		{
			num += point2.X;
			num2 += point2.Y;
		}
		point = new MeshPoint(num / (double)points.Count, num2 / (double)points.Count);
		return true;
	}

	private static bool TryEstimateBounds(IReadOnlyList<FaceMeshLandmarkPoint> points, out FaceBounds bounds)
	{
		double num = double.PositiveInfinity;
		double num2 = double.PositiveInfinity;
		double num3 = double.NegativeInfinity;
		double num4 = double.NegativeInfinity;
		bool flag = false;
		foreach (FaceMeshLandmarkPoint point in points)
		{
			if (double.IsFinite(point.X) && double.IsFinite(point.Y))
			{
				num = Math.Min(num, point.X);
				num2 = Math.Min(num2, point.Y);
				num3 = Math.Max(num3, point.X);
				num4 = Math.Max(num4, point.Y);
				flag = true;
			}
		}
		bounds = (flag ? new FaceBounds(num, num2, num3, num4) : default(FaceBounds));
		return flag;
	}

	private static bool TryEstimateBounds(IReadOnlyList<Point> points, out FaceBounds bounds)
	{
		double num = double.PositiveInfinity;
		double num2 = double.PositiveInfinity;
		double num3 = double.NegativeInfinity;
		double num4 = double.NegativeInfinity;
		bool flag = false;
		foreach (Point point in points)
		{
			if (double.IsFinite(point.X) && double.IsFinite(point.Y))
			{
				num = Math.Min(num, point.X);
				num2 = Math.Min(num2, point.Y);
				num3 = Math.Max(num3, point.X);
				num4 = Math.Max(num4, point.Y);
				flag = true;
			}
		}
		bounds = (flag ? new FaceBounds(num, num2, num3, num4) : default(FaceBounds));
		return flag;
	}

	private static double Round(double value)
	{
		if (!double.IsFinite(value))
		{
			return 0.0;
		}
		return Math.Round(value, 6, MidpointRounding.AwayFromZero);
	}

	private static double? RoundNullable(double? value)
	{
		if (value.HasValue)
		{
			double valueOrDefault = value.GetValueOrDefault();
			if (double.IsFinite(valueOrDefault))
			{
				return Math.Round(valueOrDefault, 6, MidpointRounding.AwayFromZero);
			}
		}
		return null;
	}
}
