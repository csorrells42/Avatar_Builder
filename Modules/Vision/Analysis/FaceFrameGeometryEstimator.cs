using System.Windows;
using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceFrameGeometryEstimator
{
    private static readonly int[] EyeA =
    [
        33, 246, 161, 160, 159, 158, 157, 173, 133, 155, 154, 153, 145, 144, 163, 7
    ];

    private static readonly int[] EyeB =
    [
        362, 398, 384, 385, 386, 387, 388, 466, 263, 249, 390, 373, 374, 380, 381, 382
    ];

    public FaceFrameGeometry Estimate(FaceFrameGeometryEstimatorInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var frame = input.Frame;
        if (!frame.HasFace)
        {
            return FaceFrameGeometry.None;
        }

        var pose = EstimateOrientationDegrees(frame);
        var faceBounds = EstimateFaceBounds(frame);
        var eyeSpan = TryGetInterEyeFrameWidth(frame, out var interEyeFrameWidth)
            ? interEyeFrameWidth
            : (double?)null;
        var apparentDistance = EstimateApparentDistance(input.Calibration, pose.YawDegrees, eyeSpan);
        var relativeScale = EstimateRelativeScale(input.Calibration, apparentDistance.Units);
        var distance = EstimateDistanceInches(input, pose.YawDegrees, eyeSpan);
        var confidence = CalculateConfidence(frame, eyeSpan, distance.DistanceInches);
        var zConfidence = CalculateZConfidence(frame, eyeSpan, apparentDistance, relativeScale, distance);
        var status = FormatStatus(
            pose,
            distance,
            apparentDistance.Units,
            relativeScale.Scale,
            faceBounds,
            eyeSpan,
            confidence,
            zConfidence);

        return new FaceFrameGeometry
        {
            HasFace = true,
            CapturedAtUtc = frame.CapturedAtUtc,
            YawDegrees = Round(pose.YawDegrees),
            PitchDegrees = Round(pose.PitchDegrees),
            RollDegrees = Round(pose.RollDegrees),
            XHorizontalPercent = Round((faceBounds?.CenterX ?? 0.5d) * 100d),
            YVerticalPercent = Round((faceBounds?.CenterY ?? 0.5d) * 100d),
            DistanceInches = RoundNullable(distance.DistanceInches),
            ApparentDistanceUnits = RoundNullable(apparentDistance.Units),
            FaceFillWidthPercent = RoundNullable(faceBounds?.Width * 100d),
            FaceFillHeightPercent = RoundNullable(faceBounds?.Height * 100d),
            RelativeDistanceScale = RoundNullable(relativeScale.Scale),
            InterEyeFrameWidthPercent = RoundNullable(eyeSpan * 100d),
            ConfidencePercent = Round(confidence),
            ZConfidencePercent = Round(zConfidence),
            DistanceCalibrated = distance.Calibrated,
            ZUsesCameraFov = apparentDistance.UsesCameraFov || distance.UsesCameraFov,
            ZUsesLearnedReference = relativeScale.UsesLearnedReference || distance.UsesLearnedReference,
            ZEstimateKind = ChooseZEstimateKind(distance, apparentDistance, relativeScale),
            ZQualityLabel = FormatZQualityLabel(zConfidence, distance, apparentDistance, relativeScale),
            RotationSource = pose.Source,
            DistanceSource = distance.DistanceInches is > 0d
                ? distance.Source
                : apparentDistance.Units is > 0d ? apparentDistance.Source : distance.Source,
            ReferenceScaleSource = relativeScale.Source,
            ScaleCaveat = apparentDistance.Caveat,
            StatusLine = status
        };
    }

    private static (double YawDegrees, double PitchDegrees, double RollDegrees, string Source) EstimateOrientationDegrees(
        FaceLandmarkFrame frame)
    {
        var hasMatrixPose = TryEstimatePoseFromMatrix(frame.FacialTransformationMatrix, out var matrixPose);
        var hasDensePose = TryEstimatePoseFromDenseMesh(frame, out var densePose);
        if (hasMatrixPose && hasDensePose && ShouldPreferDensePose(matrixPose, densePose))
        {
            return (densePose.YawDegrees, densePose.PitchDegrees, densePose.RollDegrees, "dense mesh geometry; transform matrix looked flat");
        }

        if (hasMatrixPose)
        {
            return (matrixPose.YawDegrees, matrixPose.PitchDegrees, matrixPose.RollDegrees, "facial transform matrix");
        }

        if (hasDensePose)
        {
            return (densePose.YawDegrees, densePose.PitchDegrees, densePose.RollDegrees, "dense mesh geometry");
        }

        return (frame.HeadYawDegrees, frame.HeadPitchDegrees, frame.HeadRollDegrees, "landmark frame pose");
    }

    private static bool ShouldPreferDensePose(
        (double YawDegrees, double PitchDegrees, double RollDegrees) matrixPose,
        (double YawDegrees, double PitchDegrees, double RollDegrees) densePose)
    {
        var matrixAB = Math.Max(Math.Abs(matrixPose.YawDegrees), Math.Abs(matrixPose.PitchDegrees));
        var matrixC = Math.Abs(matrixPose.RollDegrees);
        var denseAB = Math.Max(Math.Abs(densePose.YawDegrees), Math.Abs(densePose.PitchDegrees));
        return matrixAB <= 1.75d && matrixC <= 1.75d && denseAB >= 8d;
    }

    private static bool TryEstimatePoseFromDenseMesh(
        FaceLandmarkFrame frame,
        out (double YawDegrees, double PitchDegrees, double RollDegrees) pose)
    {
        pose = default;
        if (!frame.HasDenseMesh)
        {
            return false;
        }

        var points = frame.DenseMeshPoints.ToDictionary(static point => point.Index);
        if (!TryLandmark(points, 1, out var noseTip)
            || !TryLandmark(points, 10, out var forehead)
            || !TryLandmark(points, 152, out var chin)
            || !TryLandmark(points, 234, out var leftCheek)
            || !TryLandmark(points, 454, out var rightCheek))
        {
            return false;
        }

        var faceCenterX = (leftCheek.X + rightCheek.X) / 2d;
        var halfWidth = Math.Abs(rightCheek.X - leftCheek.X) / 2d;
        var faceHeight = chin.Y - forehead.Y;
        if (halfWidth <= 0.001d || faceHeight <= 0.001d)
        {
            return false;
        }

        var lateralOffset = (noseTip.X - faceCenterX) / halfWidth;
        var depthAsymmetry = (rightCheek.Z - leftCheek.Z) / Math.Max(0.02d, halfWidth);
        var yaw = Math.Clamp(lateralOffset * 24d + depthAsymmetry * 10d, -45d, 45d);

        var pitch = double.IsFinite(frame.HeadPitchDegrees) ? frame.HeadPitchDegrees : 0d;

        var roll = frame.HasEyeContours
            ? EstimateRollDegrees(frame.LeftEyeContour, frame.RightEyeContour)
            : frame.HeadRollDegrees;
        pose = (yaw, pitch, Math.Clamp(roll, -55d, 55d));
        return true;
    }

    private static bool TryLandmark(
        IReadOnlyDictionary<int, FaceMeshLandmarkPoint> points,
        int index,
        out FaceMeshLandmarkPoint point)
    {
        if (points.TryGetValue(index, out var value))
        {
            point = value;
            return true;
        }

        point = new FaceMeshLandmarkPoint { Index = index };
        return false;
    }

    private static double EstimateRollDegrees(IReadOnlyList<Point> leftEye, IReadOnlyList<Point> rightEye)
    {
        if (leftEye.Count == 0 || rightEye.Count == 0)
        {
            return 0d;
        }

        var leftCenter = new Point(leftEye.Average(static point => point.X), leftEye.Average(static point => point.Y));
        var rightCenter = new Point(rightEye.Average(static point => point.X), rightEye.Average(static point => point.Y));
        return Math.Atan2(rightCenter.Y - leftCenter.Y, rightCenter.X - leftCenter.X) * 180d / Math.PI;
    }

    private static bool TryEstimatePoseFromMatrix(
        IReadOnlyList<double> values,
        out (double YawDegrees, double PitchDegrees, double RollDegrees) pose)
    {
        pose = default;
        if (values.Count < 16 || values.Any(static value => double.IsNaN(value) || double.IsInfinity(value)))
        {
            return false;
        }

        var r02 = values[2];
        var r10 = values[4];
        var r11 = values[5];
        var r12 = values[6];
        var r22 = values[10];
        var yaw = Math.Atan2(r02, r22) * 180d / Math.PI;
        var pitch = Math.Asin(Math.Clamp(-r12, -1d, 1d)) * 180d / Math.PI;
        var roll = Math.Atan2(r10, r11) * 180d / Math.PI;
        if (Math.Abs(yaw) > 80d || Math.Abs(pitch) > 70d || Math.Abs(roll) > 80d)
        {
            return false;
        }

        pose = (
            Math.Clamp(yaw, -55d, 55d),
            Math.Clamp(pitch, -45d, 45d),
            Math.Clamp(roll, -55d, 55d));
        return true;
    }

    private static bool TryGetInterEyeFrameWidth(FaceLandmarkFrame frame, out double interEyeFrameWidth)
    {
        interEyeFrameWidth = 0d;
        if (frame.HasDenseMesh
            && TryGetCenter(frame.DenseMeshPoints, EyeA, out var firstEye)
            && TryGetCenter(frame.DenseMeshPoints, EyeB, out var secondEye))
        {
            interEyeFrameWidth = Math.Abs(firstEye.X - secondEye.X);
            return interEyeFrameWidth > 0.0001d;
        }

        if (TryGetCenter(frame.LeftEyeContour, out var leftEye)
            && TryGetCenter(frame.RightEyeContour, out var rightEye))
        {
            interEyeFrameWidth = Math.Abs(leftEye.X - rightEye.X);
            return interEyeFrameWidth > 0.0001d;
        }

        return false;
    }

    private static (double? DistanceInches, bool Calibrated, bool UsesCameraFov, bool UsesLearnedReference, string Source) EstimateDistanceInches(
        FaceFrameGeometryEstimatorInput input,
        double yawDegrees,
        double? interEyeFrameWidth)
    {
        if (interEyeFrameWidth is not > 0d)
        {
            return (null, false, false, false, "waiting for eye-span measurement");
        }

        var yawCosine = CalculateYawCosine(yawDegrees);
        var calibration = input.Calibration ?? FaceFrameGeometryCalibration.None;
        if (calibration.HasDistanceReference)
        {
            var distance = calibration.ReferenceDistanceInches!.Value
                * calibration.ReferenceInterEyeFrameWidth!.Value
                * yawCosine
                / interEyeFrameWidth.Value;
            return (distance, true, false, true, "known-distance face calibration");
        }

        if (calibration.HasCameraIntrinsics && input.FrameWidthPixels is > 0)
        {
            var fovRadians = calibration.CameraHorizontalFovDegrees!.Value * Math.PI / 180d;
            var focalPixels = input.FrameWidthPixels.Value / (2d * Math.Tan(fovRadians / 2d));
            var interEyePixels = interEyeFrameWidth.Value * input.FrameWidthPixels.Value;
            if (interEyePixels > 0.1d)
            {
                var distance = calibration.InterpupillaryDistanceInches!.Value
                    * focalPixels
                    * yawCosine
                    / interEyePixels;
                return (distance, false, true, false, "camera FOV and interpupillary-distance estimate");
            }
        }

        return (null, false, false, false, "distance needs calibration");
    }

    private static (double? Units, bool UsesCameraFov, string Source, string Caveat) EstimateApparentDistance(
        FaceFrameGeometryCalibration? calibration,
        double yawDegrees,
        double? interEyeFrameWidth)
    {
        if (interEyeFrameWidth is not > 0d)
        {
            return (null, false, "waiting for eye-span measurement", "No apparent scale is available until both eye centers are visible.");
        }

        // Apparent units deliberately avoid claiming real inches before zoom/FOV calibration.
        // The observed inter-eye span is treated as one stable face unit in the current camera image.
        var yawScale = CalculateYawCosine(yawDegrees);
        if (calibration?.CameraHorizontalFovDegrees is > 0d and < 180d)
        {
            var fovRadians = calibration.CameraHorizontalFovDegrees.Value * Math.PI / 180d;
            var normalizedFocalLength = 1d / (2d * Math.Tan(fovRadians / 2d));
            return (
                normalizedFocalLength * yawScale / interEyeFrameWidth.Value,
                true,
                $"apparent face units from eye span and {calibration.CameraHorizontalFovDegrees.Value:0.#} deg horizontal FOV",
                "Zoom changes effective FOV, so this is apparent camera-space distance until zoom/FOV is calibrated.");
        }

        return (
            yawScale / interEyeFrameWidth.Value,
            false,
            "apparent face units from eye span and current zoom",
            "No camera FOV is known, so apparent distance is relative to the current camera framing.");
    }

    private static (double? Scale, bool UsesLearnedReference, string Source) EstimateRelativeScale(
        FaceFrameGeometryCalibration? calibration,
        double? apparentDistanceUnits)
    {
        if (apparentDistanceUnits is not > 0d)
        {
            return (null, false, "waiting for apparent Z measurement");
        }

        if (calibration is null || !calibration.HasApparentReference)
        {
            return (null, false, "waiting for learned reference face scale");
        }

        var referenceApparentDistance = EstimateApparentDistance(
            calibration,
            yawDegrees: 0d,
            interEyeFrameWidth: calibration.ReferenceInterEyeFrameWidth).Units;
        if (referenceApparentDistance is not > 0d)
        {
            return (null, false, "waiting for learned reference face scale");
        }

        var source = string.IsNullOrWhiteSpace(calibration.ReferenceSource)
            ? "learned reference face scale"
            : calibration.ReferenceSource;
        return (apparentDistanceUnits.Value / referenceApparentDistance.Value, true, source);
    }

    private static double CalculateYawCosine(double yawDegrees)
    {
        var yawRadians = Math.Abs(yawDegrees) * Math.PI / 180d;
        return Math.Clamp(Math.Cos(yawRadians), 0.45d, 1d);
    }

    private static double CalculateConfidence(
        FaceLandmarkFrame frame,
        double? interEyeFrameWidth,
        double? distanceInches)
    {
        var confidence = Math.Clamp(frame.TrackingConfidence, 0d, 1d) * 55d;
        confidence += Math.Clamp(frame.EyeConfidence, 0d, 1d) * 25d;
        confidence += frame.FacialTransformationMatrix.Count >= 16 ? 12d : 4d;
        confidence += interEyeFrameWidth is > 0d ? 8d : 0d;
        if (distanceInches is > 0d)
        {
            confidence = Math.Min(100d, confidence + 4d);
        }

        return Math.Clamp(confidence, 0d, 100d);
    }

    private static double CalculateZConfidence(
        FaceLandmarkFrame frame,
        double? interEyeFrameWidth,
        (double? Units, bool UsesCameraFov, string Source, string Caveat) apparentDistance,
        (double? Scale, bool UsesLearnedReference, string Source) relativeScale,
        (double? DistanceInches, bool Calibrated, bool UsesCameraFov, bool UsesLearnedReference, string Source) distance)
    {
        if (interEyeFrameWidth is not > 0d || apparentDistance.Units is not > 0d)
        {
            return 0d;
        }

        var confidence = Math.Clamp(frame.TrackingConfidence, 0d, 1d) * 34d;
        confidence += Math.Clamp(frame.EyeConfidence, 0d, 1d) * 26d;
        confidence += frame.FacialTransformationMatrix.Count >= 16 ? 10d : 4d;
        confidence += apparentDistance.UsesCameraFov || distance.UsesCameraFov ? 10d : 0d;
        confidence += relativeScale.UsesLearnedReference || distance.UsesLearnedReference ? 12d : 0d;
        confidence += distance.Calibrated ? 14d : distance.DistanceInches is > 0d ? 6d : 0d;
        return Math.Clamp(confidence, 0d, 100d);
    }

    private static string ChooseZEstimateKind(
        (double? DistanceInches, bool Calibrated, bool UsesCameraFov, bool UsesLearnedReference, string Source) distance,
        (double? Units, bool UsesCameraFov, string Source, string Caveat) apparentDistance,
        (double? Scale, bool UsesLearnedReference, string Source) relativeScale)
    {
        if (distance.Calibrated && distance.DistanceInches is > 0d)
        {
            return "calibrated-known-distance";
        }

        if (distance.UsesCameraFov && distance.DistanceInches is > 0d)
        {
            return "camera-fov-ipd-estimate";
        }

        if (apparentDistance.UsesCameraFov && relativeScale.UsesLearnedReference && relativeScale.Scale is > 0d)
        {
            return "camera-fov-learned-reference-apparent-scale";
        }

        if (apparentDistance.UsesCameraFov && apparentDistance.Units is > 0d)
        {
            return "camera-fov-apparent-scale";
        }

        if (relativeScale.UsesLearnedReference && relativeScale.Scale is > 0d)
        {
            return "learned-reference-apparent-scale";
        }

        return apparentDistance.Units is > 0d ? "apparent-scale-only" : "waiting";
    }

    private static string FormatZQualityLabel(
        double zConfidence,
        (double? DistanceInches, bool Calibrated, bool UsesCameraFov, bool UsesLearnedReference, string Source) distance,
        (double? Units, bool UsesCameraFov, string Source, string Caveat) apparentDistance,
        (double? Scale, bool UsesLearnedReference, string Source) relativeScale)
    {
        if (apparentDistance.Units is not > 0d)
        {
            return "waiting for eye-span Z";
        }

        var strength = zConfidence switch
        {
            >= 86d => "strong",
            >= 70d => "usable",
            >= 50d => "rough",
            _ => "weak"
        };

        if (distance.Calibrated)
        {
            return $"{strength} calibrated Z";
        }

        if (distance.UsesCameraFov)
        {
            return $"{strength} camera-FOV Z estimate";
        }

        if (relativeScale.UsesLearnedReference)
        {
            return $"{strength} learned-reference apparent Z";
        }

        return $"{strength} apparent Z";
    }

    private static string FormatStatus(
        (double YawDegrees, double PitchDegrees, double RollDegrees, string Source) pose,
        (double? DistanceInches, bool Calibrated, bool UsesCameraFov, bool UsesLearnedReference, string Source) distance,
        double? apparentDistanceUnits,
        double? relativeDistanceScale,
        FaceBounds? faceBounds,
        double? interEyeFrameWidth,
        double confidence,
        double zConfidence)
    {
        var distanceLabel = apparentDistanceUnits is double units
            ? $"{units:0.##} apparent face units"
            : "distance waiting";
        if (distance.DistanceInches is double inches)
        {
            distanceLabel = distance.Calibrated
                ? $"{distanceLabel} ({inches:0.#} in calibrated)"
                : $"{distanceLabel} ({inches:0.#} in est)";
        }
        else if (interEyeFrameWidth is double span)
        {
            distanceLabel = $"{distanceLabel}; eye span {span * 100d:0.#}% frame";
        }

        var relativeLabel = relativeDistanceScale is double relative
            ? $" | Z ref {relative:0.##}x"
            : "";
        var fillLabel = faceBounds is { } bounds
            ? $" | center X/Y/Z {bounds.CenterX * 100d:0.#}%, {bounds.CenterY * 100d:0.#}%, {apparentDistanceUnits?.ToString("0.##") ?? "--"} | fill {bounds.Width * 100d:0.#}% x {bounds.Height * 100d:0.#}%"
            : "";
        return $"Face frame: {distanceLabel}{relativeLabel}{fillLabel} | MediaPipe A around X {pose.PitchDegrees:0.#} deg | B around Y {pose.YawDegrees:0.#} deg | C around Z {pose.RollDegrees:0.#} deg | frame q {confidence:0}% | Z q {zConfidence:0}%";
    }

    private static FaceBounds? EstimateFaceBounds(FaceLandmarkFrame frame)
    {
        var densePoints = frame.DenseMeshPoints
            .Where(static point => double.IsFinite(point.X) && double.IsFinite(point.Y))
            .Select(static point => new MeshPoint(point.X, point.Y))
            .ToList();
        if (densePoints.Count > 0)
        {
            return FaceBounds.From(densePoints);
        }

        var contourPoints = frame.FaceContour
            .Where(static point => double.IsFinite(point.X) && double.IsFinite(point.Y))
            .Select(static point => new MeshPoint(point.X, point.Y))
            .ToList();
        return contourPoints.Count > 0 ? FaceBounds.From(contourPoints) : null;
    }

    private static bool TryGetCenter(
        IReadOnlyList<FaceMeshLandmarkPoint> points,
        IReadOnlyList<int> indices,
        out MeshPoint point)
    {
        var lookup = points.ToDictionary(static item => item.Index);
        var values = indices
            .Where(lookup.ContainsKey)
            .Select(index => lookup[index])
            .ToList();
        if (values.Count == 0)
        {
            point = default;
            return false;
        }

        point = new MeshPoint(
            values.Average(static value => value.X),
            values.Average(static value => value.Y));
        return true;
    }

    private static bool TryGetCenter(IReadOnlyList<Point> points, out MeshPoint point)
    {
        if (points.Count == 0)
        {
            point = default;
            return false;
        }

        point = new MeshPoint(
            points.Average(static value => value.X),
            points.Average(static value => value.Y));
        return true;
    }

    private static double Round(double value)
    {
        return double.IsFinite(value) ? Math.Round(value, 6, MidpointRounding.AwayFromZero) : 0d;
    }

    private static double? RoundNullable(double? value)
    {
        return value is double number && double.IsFinite(number)
            ? Math.Round(number, 6, MidpointRounding.AwayFromZero)
            : null;
    }

    private readonly record struct MeshPoint(double X, double Y);

    private readonly record struct FaceBounds(double Left, double Top, double Right, double Bottom)
    {
        public double Width => Math.Max(0d, Right - Left);

        public double Height => Math.Max(0d, Bottom - Top);

        public double CenterX => (Left + Right) / 2d;

        public double CenterY => (Top + Bottom) / 2d;

        public static FaceBounds From(IReadOnlyList<MeshPoint> points)
        {
            return new FaceBounds(
                points.Min(static point => point.X),
                points.Min(static point => point.Y),
                points.Max(static point => point.X),
                points.Max(static point => point.Y));
        }
    }
}
