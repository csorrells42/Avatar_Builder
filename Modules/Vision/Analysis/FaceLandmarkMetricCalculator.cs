using AvatarBuilder.Modules.Vision.Common;
using System.Windows;

namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceLandmarkMetricCalculator
{
    private double? _previousMouthOpeningRatio;
    private double? _previousJawDroopRatio;
    private double? _previousAverageBrowHeightRatio;
    private double? _smoothedLeftEyeOpeningRatio;
    private double? _smoothedRightEyeOpeningRatio;
    private double? _smoothedAverageEyeOpeningRatio;
    private double? _smoothedMouthOpeningRatio;
    private double? _smoothedJawDroopRatio;
    private double? _smoothedLeftBrowHeightRatio;
    private double? _smoothedRightBrowHeightRatio;
    private double? _smoothedAverageBrowHeightRatio;
    private double? _mediaPipeOpenEyeReferenceRatio;
    private double? _mediaPipeClosedMouthReferenceRatio;
    private DateTime? _previousCapturedAtUtc;

    public FaceLandmarkMetrics Update(FaceLandmarkFrame frame)
    {
        if (!frame.HasFace)
        {
            Reset();
            return FaceLandmarkMetrics.None;
        }

        var highFidelityLandmarkSource = IsHighFidelityLandmarkSource(frame.Source);
        var leftEyeOpening = ContourOpeningEstimator.CalculateOpeningRatio(
            frame.LeftEyeContour,
            ShouldUsePairedAverage(frame, frame.LeftEyeContour, isEye: true, highFidelityLandmarkSource));
        var rightEyeOpening = ContourOpeningEstimator.CalculateOpeningRatio(
            frame.RightEyeContour,
            ShouldUsePairedAverage(frame, frame.RightEyeContour, isEye: true, highFidelityLandmarkSource));
        var averageEyeOpening = Average(leftEyeOpening, rightEyeOpening);
        var mediaPipeLeftBlink = BlendshapePercent(frame, "eyeBlinkLeft");
        var mediaPipeRightBlink = BlendshapePercent(frame, "eyeBlinkRight");
        var mediaPipeAverageBlink = Average(mediaPipeLeftBlink, mediaPipeRightBlink);
        UpdateMediaPipeOpenEyeReference(frame, averageEyeOpening, mediaPipeAverageBlink, highFidelityLandmarkSource);
        var rawEyeAsymmetry = CalculateAsymmetryPercent(leftEyeOpening, rightEyeOpening, averageEyeOpening);
        var possibleOneEyeArtifact = IsPossibleOneEyeArtifact(frame, rawEyeAsymmetry, highFidelityLandmarkSource);
        var stabilizedLeftEyeOpening = StabilizeEyeOpeningWithMediaPipe(leftEyeOpening, mediaPipeLeftBlink ?? mediaPipeAverageBlink);
        var stabilizedRightEyeOpening = StabilizeEyeOpeningWithMediaPipe(rightEyeOpening, mediaPipeRightBlink ?? mediaPipeAverageBlink);
        var stabilizedAverageEyeOpening = Average(stabilizedLeftEyeOpening, stabilizedRightEyeOpening)
            ?? StabilizeEyeOpeningWithMediaPipe(averageEyeOpening, mediaPipeAverageBlink);
        (stabilizedLeftEyeOpening, stabilizedRightEyeOpening, stabilizedAverageEyeOpening) = ApplyLowFidelityEyeOpeningGuard(
            frame,
            stabilizedLeftEyeOpening,
            stabilizedRightEyeOpening,
            stabilizedAverageEyeOpening,
            mediaPipeAverageBlink,
            highFidelityLandmarkSource);
        var mediaPipeEyeOpeningCorrection = CalculateCorrection(stabilizedAverageEyeOpening, averageEyeOpening);
        var mouthContour = frame.InnerLipContour.Count >= 4 ? frame.InnerLipContour : frame.OuterLipContour;
        var mouthOpening = ContourOpeningEstimator.CalculateOpeningRatio(
            mouthContour,
            ShouldUsePairedAverage(frame, mouthContour, isEye: false, highFidelityLandmarkSource));
        var mediaPipeJawOpen = BlendshapePercent(frame, "jawOpen");
        var mediaPipeMouthClose = BlendshapePercent(frame, "mouthClose");
        UpdateMediaPipeClosedMouthReference(
            frame,
            mouthOpening,
            mediaPipeJawOpen,
            mediaPipeMouthClose,
            highFidelityLandmarkSource);
        var stabilizedMouthOpening = StabilizeMouthOpeningWithMediaPipe(mouthOpening, mediaPipeJawOpen, mediaPipeMouthClose);
        var mediaPipeMouthOpeningCorrection = CalculateCorrection(stabilizedMouthOpening, mouthOpening);
        var jawDroop = CalculateJawDroopRatio(frame, mediaPipeJawOpen);
        var brow = CalculateBrowHeight(frame);
        var eyeQuality = CalculateEyeMeasurementQuality(frame, stabilizedLeftEyeOpening, stabilizedRightEyeOpening, stabilizedAverageEyeOpening, possibleOneEyeArtifact);
        var mouthQuality = CalculateMouthMeasurementQuality(frame, stabilizedMouthOpening);
        var browQuality = CalculateBrowMeasurementQuality(frame, brow.Average, brow.AsymmetryPercent);
        var smoothing = CalculateSmoothingFactor(frame);
        _smoothedLeftEyeOpeningRatio = Smooth(_smoothedLeftEyeOpeningRatio, stabilizedLeftEyeOpening, smoothing);
        _smoothedRightEyeOpeningRatio = Smooth(_smoothedRightEyeOpeningRatio, stabilizedRightEyeOpening, smoothing);
        _smoothedAverageEyeOpeningRatio = Smooth(_smoothedAverageEyeOpeningRatio, stabilizedAverageEyeOpening, smoothing);
        _smoothedMouthOpeningRatio = Smooth(_smoothedMouthOpeningRatio, stabilizedMouthOpening, smoothing);
        _smoothedJawDroopRatio = Smooth(_smoothedJawDroopRatio, jawDroop, smoothing);
        _smoothedLeftBrowHeightRatio = Smooth(_smoothedLeftBrowHeightRatio, brow.Left, smoothing);
        _smoothedRightBrowHeightRatio = Smooth(_smoothedRightBrowHeightRatio, brow.Right, smoothing);
        _smoothedAverageBrowHeightRatio = Smooth(_smoothedAverageBrowHeightRatio, brow.Average, smoothing);
        var mouthVelocity = CalculateVelocity(frame.CapturedAtUtc, _smoothedMouthOpeningRatio, _previousMouthOpeningRatio);
        var jawDroopVelocity = CalculateVelocity(frame.CapturedAtUtc, _smoothedJawDroopRatio, _previousJawDroopRatio);
        var browVelocity = CalculateVelocity(frame.CapturedAtUtc, _smoothedAverageBrowHeightRatio, _previousAverageBrowHeightRatio);
        var eyeAsymmetry = CalculateAsymmetryPercent(
            _smoothedLeftEyeOpeningRatio,
            _smoothedRightEyeOpeningRatio,
            _smoothedAverageEyeOpeningRatio);
        var browAsymmetry = CalculateAsymmetryPercent(
            _smoothedLeftBrowHeightRatio,
            _smoothedRightBrowHeightRatio,
            _smoothedAverageBrowHeightRatio);
        _previousMouthOpeningRatio = _smoothedMouthOpeningRatio;
        _previousJawDroopRatio = _smoothedJawDroopRatio;
        _previousAverageBrowHeightRatio = _smoothedAverageBrowHeightRatio;
        _previousCapturedAtUtc = frame.CapturedAtUtc;

        return new FaceLandmarkMetrics
        {
            HasFace = true,
            Source = frame.Source,
            ConfidenceLabel = frame.ConfidenceLabel,
            CapturedAtUtc = frame.CapturedAtUtc,
            TrackingConfidence = frame.TrackingConfidence,
            EyeConfidence = frame.EyeConfidence,
            MouthConfidence = frame.MouthConfidence,
            EyeMeasurementQualityPercent = eyeQuality,
            MouthMeasurementQualityPercent = mouthQuality,
            BrowMeasurementQualityPercent = browQuality,
            EyeImageQualityAvailable = frame.EyeImageQualityAvailable,
            MouthImageQualityAvailable = frame.MouthImageQualityAvailable,
            EyeGlarePercent = frame.EyeGlarePercent,
            MouthGlarePercent = frame.MouthGlarePercent,
            EyeContrastPercent = frame.EyeContrastPercent,
            MouthContrastPercent = frame.MouthContrastPercent,
            EyeSharpnessPercent = frame.EyeSharpnessPercent,
            MouthSharpnessPercent = frame.MouthSharpnessPercent,
            EyeDarkCoveragePercent = frame.EyeDarkCoveragePercent,
            MouthDarkCoveragePercent = frame.MouthDarkCoveragePercent,
            RawEyeAsymmetryPercent = rawEyeAsymmetry,
            EyeAsymmetryPercent = eyeAsymmetry,
            PossibleOneEyeArtifact = possibleOneEyeArtifact,
            LeftEyeReconstructed = frame.LeftEyeReconstructed,
            RightEyeReconstructed = frame.RightEyeReconstructed,
            MouthReconstructed = frame.MouthReconstructed,
            EyeArtifactSuppressed = frame.EyeArtifactSuppressed,
            RawLeftEyeOpeningRatio = leftEyeOpening,
            RawRightEyeOpeningRatio = rightEyeOpening,
            RawAverageEyeOpeningRatio = averageEyeOpening,
            RawMouthOpeningRatio = mouthOpening,
            RawJawDroopRatio = jawDroop,
            RawLeftBrowHeightRatio = brow.Left,
            RawRightBrowHeightRatio = brow.Right,
            RawAverageBrowHeightRatio = brow.Average,
            LeftEyeOpeningRatio = _smoothedLeftEyeOpeningRatio,
            RightEyeOpeningRatio = _smoothedRightEyeOpeningRatio,
            AverageEyeOpeningRatio = _smoothedAverageEyeOpeningRatio,
            MouthOpeningRatio = _smoothedMouthOpeningRatio,
            MouthOpeningVelocityPerSecond = mouthVelocity,
            JawDroopRatio = _smoothedJawDroopRatio,
            JawDroopVelocityPerSecond = jawDroopVelocity,
            LeftBrowHeightRatio = _smoothedLeftBrowHeightRatio,
            RightBrowHeightRatio = _smoothedRightBrowHeightRatio,
            AverageBrowHeightRatio = _smoothedAverageBrowHeightRatio,
            BrowHeightVelocityPerSecond = browVelocity,
            BrowAsymmetryPercent = browAsymmetry,
            MediaPipeLeftEyeBlinkPercent = mediaPipeLeftBlink,
            MediaPipeRightEyeBlinkPercent = mediaPipeRightBlink,
            MediaPipeAverageEyeBlinkPercent = mediaPipeAverageBlink,
            MediaPipeJawOpenPercent = mediaPipeJawOpen,
            MediaPipeMouthClosePercent = mediaPipeMouthClose,
            MediaPipeEyeOpeningCorrectionRatio = mediaPipeEyeOpeningCorrection,
            MediaPipeMouthOpeningCorrectionRatio = mediaPipeMouthOpeningCorrection,
            HeadYawDegrees = frame.HeadYawDegrees,
            HeadPitchDegrees = frame.HeadPitchDegrees,
            HeadRollDegrees = frame.HeadRollDegrees
        };
    }

    public void Reset()
    {
        _previousMouthOpeningRatio = null;
        _previousJawDroopRatio = null;
        _previousAverageBrowHeightRatio = null;
        _smoothedLeftEyeOpeningRatio = null;
        _smoothedRightEyeOpeningRatio = null;
        _smoothedAverageEyeOpeningRatio = null;
        _smoothedMouthOpeningRatio = null;
        _smoothedJawDroopRatio = null;
        _smoothedLeftBrowHeightRatio = null;
        _smoothedRightBrowHeightRatio = null;
        _smoothedAverageBrowHeightRatio = null;
        _mediaPipeOpenEyeReferenceRatio = null;
        _mediaPipeClosedMouthReferenceRatio = null;
        _previousCapturedAtUtc = null;
    }

    private static double CalculateSmoothingFactor(FaceLandmarkFrame frame)
    {
        var confidence = Math.Min(frame.TrackingConfidence, Math.Min(frame.EyeConfidence, frame.MouthConfidence));
        return confidence >= 0.65d ? 0.62d : confidence >= 0.35d ? 0.46d : 0.32d;
    }

    private static double CalculateEyeMeasurementQuality(
        FaceLandmarkFrame frame,
        double? leftEyeOpening,
        double? rightEyeOpening,
        double? averageEyeOpening,
        bool possibleOneEyeArtifact)
    {
        if (averageEyeOpening is not double average)
        {
            return 0d;
        }

        var quality = Math.Clamp(frame.EyeConfidence, 0d, 1d) * 100d;
        var eyeAgreement = 0d;
        if (leftEyeOpening is double left && rightEyeOpening is double right)
        {
            var asymmetry = Math.Abs(left - right) / Math.Max(Math.Abs(average), 0.025d);
            eyeAgreement = 1d - Math.Clamp(asymmetry, 0d, 1d);
            quality *= 0.58d + eyeAgreement * 0.42d;
        }
        else
        {
            quality *= 0.72d;
        }

        if (average < 0.012d || average > 0.70d)
        {
            quality *= 0.70d;
        }

        quality *= CalculateImageQualityMultiplier(
            frame.EyeImageQualityAvailable,
            frame.EyeGlarePercent,
            frame.EyeContrastPercent,
            frame.EyeSharpnessPercent);
        if (possibleOneEyeArtifact)
        {
            quality *= 0.70d;
        }

        if (frame.LeftEyeReconstructed || frame.RightEyeReconstructed)
        {
            var symmetricDualEyeReconstruction = frame.LeftEyeReconstructed
                && frame.RightEyeReconstructed
                && !frame.EyeArtifactSuppressed
                && !possibleOneEyeArtifact
                && eyeAgreement >= 0.82d;
            quality *= frame.LeftEyeReconstructed && frame.RightEyeReconstructed
                ? symmetricDualEyeReconstruction ? 0.84d : 0.78d
                : 0.88d;
        }

        if (frame.EyeArtifactSuppressed)
        {
            quality *= 0.82d;
        }

        quality *= CalculateSourceQualityMultiplier(frame.Source, isEye: true);
        return Math.Clamp(quality, 0d, 100d);
    }

    private static double CalculateMouthMeasurementQuality(FaceLandmarkFrame frame, double? mouthOpening)
    {
        if (mouthOpening is not double opening)
        {
            return 0d;
        }

        var quality = Math.Clamp(frame.MouthConfidence, 0d, 1d) * 100d;
        if (frame.InnerLipContour.Count < 4)
        {
            quality *= 0.74d;
        }

        if (opening < 0.008d || opening > 0.95d)
        {
            quality *= 0.72d;
        }

        quality *= CalculateImageQualityMultiplier(
            frame.MouthImageQualityAvailable,
            frame.MouthGlarePercent,
            frame.MouthContrastPercent,
            frame.MouthSharpnessPercent);
        if (frame.MouthReconstructed)
        {
            quality *= 0.88d;
        }

        quality *= CalculateSourceQualityMultiplier(frame.Source, isEye: false);
        return Math.Clamp(quality, 0d, 100d);
    }

    private static double CalculateBrowMeasurementQuality(
        FaceLandmarkFrame frame,
        double? averageBrowHeight,
        double? browAsymmetryPercent)
    {
        if (averageBrowHeight is not double height)
        {
            return 0d;
        }

        var quality = Math.Min(Math.Clamp(frame.TrackingConfidence, 0d, 1d), Math.Clamp(frame.EyeConfidence, 0d, 1d)) * 100d;
        if (frame.LeftBrowContour.Count < 5 || frame.RightBrowContour.Count < 5)
        {
            quality *= 0.72d;
        }

        if (height < 0.025d || height > 0.38d)
        {
            quality *= 0.74d;
        }

        if (browAsymmetryPercent is double asymmetry && asymmetry > 95d)
        {
            quality *= 0.82d;
        }

        quality *= CalculateSourceQualityMultiplier(frame.Source, isEye: true);
        return Math.Clamp(quality, 0d, 100d);
    }

    private static double CalculateImageQualityMultiplier(bool available, double glarePercent, double contrastPercent, double sharpnessPercent)
    {
        if (!available)
        {
            return 1d;
        }

        var glarePenalty = Math.Clamp((glarePercent - 6d) / 32d, 0d, 0.45d);
        var contrastPenalty = contrastPercent < 24d ? Math.Clamp((24d - contrastPercent) / 60d, 0d, 0.25d) : 0d;
        var sharpnessPenalty = sharpnessPercent < 18d ? Math.Clamp((18d - sharpnessPercent) / 55d, 0d, 0.22d) : 0d;
        return Math.Clamp(1d - glarePenalty - contrastPenalty - sharpnessPenalty, 0.45d, 1.04d);
    }

    private static double CalculateSourceQualityMultiplier(string source, bool isEye)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return 1d;
        }

        var multiplier = 1d;
        if (source.Contains("temporal face hold", StringComparison.OrdinalIgnoreCase)
            || source.Contains("temporal hold", StringComparison.OrdinalIgnoreCase)
            || source.Contains("landmark hold", StringComparison.OrdinalIgnoreCase))
        {
            multiplier *= isEye ? 0.93d : 0.92d;
        }

        if (source.Contains("temporal reconstruction", StringComparison.OrdinalIgnoreCase))
        {
            multiplier *= isEye ? 0.95d : 0.96d;
        }

        if (source.Contains("fused", StringComparison.OrdinalIgnoreCase))
        {
            multiplier *= 1.04d;
        }

        return Math.Clamp(multiplier, 0.25d, 1.08d);
    }

    private static double? Smooth(double? previous, double? current, double amount)
    {
        if (current is not double value)
        {
            return previous;
        }

        return previous is double prior ? prior + (value - prior) * amount : value;
    }

    private double? CalculateVelocity(DateTime capturedAtUtc, double? currentValue, double? previousValue)
    {
        if (currentValue is not double current
            || previousValue is not double previous
            || _previousCapturedAtUtc is not DateTime previousAt)
        {
            return null;
        }

        var elapsedSeconds = (capturedAtUtc - previousAt).TotalSeconds;
        if (elapsedSeconds <= 0.05d)
        {
            return null;
        }

        return (current - previous) / elapsedSeconds;
    }

    private (double? Left, double? Right, double? Average) ApplyLowFidelityEyeOpeningGuard(
        FaceLandmarkFrame frame,
        double? leftEyeOpening,
        double? rightEyeOpening,
        double? averageEyeOpening,
        double? mediaPipeAverageBlink,
        bool highFidelityLandmarkSource)
    {
        if (averageEyeOpening is not double current
            || _smoothedAverageEyeOpeningRatio is not double previous
            || current <= previous
            || mediaPipeAverageBlink.HasValue)
        {
            return (leftEyeOpening, rightEyeOpening, averageEyeOpening);
        }

        var lowFidelityEyeFrame =
            frame.LeftEyeReconstructed
            || frame.RightEyeReconstructed
            || frame.EyeArtifactSuppressed
            || !highFidelityLandmarkSource
            || frame.BlendshapeScores.Count == 0;
        if (!lowFidelityEyeFrame)
        {
            return (leftEyeOpening, rightEyeOpening, averageEyeOpening);
        }

        var elapsedSeconds = _previousCapturedAtUtc is DateTime previousAt
            ? Math.Clamp((frame.CapturedAtUtc - previousAt).TotalSeconds, 0.1d, 3d)
            : 0.5d;
        var maximumOpeningDelta = 0.006d * elapsedSeconds;
        var cappedAverage = Math.Min(current, previous + maximumOpeningDelta);
        if (cappedAverage >= current - 0.000001d)
        {
            return (leftEyeOpening, rightEyeOpening, averageEyeOpening);
        }

        var scale = current <= 0.000001d ? 1d : cappedAverage / current;
        return (
            ScaleOpening(leftEyeOpening, scale),
            ScaleOpening(rightEyeOpening, scale),
            cappedAverage);
    }

    private static double? CalculateJawDroopRatio(FaceLandmarkFrame frame, double? mediaPipeJawOpenPercent)
    {
        if (frame.JawContour.Count < 3)
        {
            return null;
        }

        var leftEyeCenter = Center(frame.LeftEyeContour);
        var rightEyeCenter = Center(frame.RightEyeContour);
        var eyeCenter = AveragePoint(leftEyeCenter, rightEyeCenter);
        if (eyeCenter is not Point eyePoint)
        {
            return null;
        }

        var horizontal = CreateFaceHorizontalAxis(leftEyeCenter, rightEyeCenter);
        var vertical = new Vector(-horizontal.Y, horizontal.X);
        if (vertical.Y < 0d)
        {
            vertical = new Vector(-vertical.X, -vertical.Y);
        }

        var pointCount = frame.FaceContour.Count
            + frame.JawContour.Count
            + frame.LeftEyeContour.Count
            + frame.RightEyeContour.Count
            + frame.OuterLipContour.Count
            + frame.InnerLipContour.Count;
        if (pointCount < 4)
        {
            return null;
        }

        var faceLeft = double.PositiveInfinity;
        var faceRight = double.NegativeInfinity;
        ExpandProjectedRange(frame.FaceContour, horizontal, ref faceLeft, ref faceRight);
        ExpandProjectedRange(frame.JawContour, horizontal, ref faceLeft, ref faceRight);
        ExpandProjectedRange(frame.LeftEyeContour, horizontal, ref faceLeft, ref faceRight);
        ExpandProjectedRange(frame.RightEyeContour, horizontal, ref faceLeft, ref faceRight);
        ExpandProjectedRange(frame.OuterLipContour, horizontal, ref faceLeft, ref faceRight);
        ExpandProjectedRange(frame.InnerLipContour, horizontal, ref faceLeft, ref faceRight);
        var faceWidth = faceRight - faceLeft;
        if (faceWidth <= 0.001d)
        {
            return null;
        }

        var eyeProjection = Dot(eyePoint, vertical);
        var chinProjection = MaximumProjection(frame.JawContour, vertical);
        var absoluteLowerFaceSpan = (chinProjection - eyeProjection) / faceWidth;
        var contourDroop = Math.Clamp((absoluteLowerFaceSpan - 0.92d) * 0.42d, 0d, 0.35d);
        if (mediaPipeJawOpenPercent is not double jawOpen || double.IsNaN(jawOpen) || double.IsInfinity(jawOpen))
        {
            return contourDroop;
        }

        var mediaPipeDroop = Math.Clamp(jawOpen / 100d * 0.35d, 0d, 0.35d);
        return Math.Max(contourDroop, mediaPipeDroop);
    }

    private static (double? Left, double? Right, double? Average, double? AsymmetryPercent) CalculateBrowHeight(FaceLandmarkFrame frame)
    {
        if (frame.LeftBrowContour.Count < 2
            || frame.RightBrowContour.Count < 2
            || frame.LeftEyeContour.Count < 2
            || frame.RightEyeContour.Count < 2)
        {
            return (null, null, null, null);
        }

        var leftBrowCenter = Center(frame.LeftBrowContour);
        var rightBrowCenter = Center(frame.RightBrowContour);
        var leftEyeCenter = Center(frame.LeftEyeContour);
        var rightEyeCenter = Center(frame.RightEyeContour);
        if (leftBrowCenter is not Point leftBrow
            || rightBrowCenter is not Point rightBrow
            || leftEyeCenter is not Point leftEye
            || rightEyeCenter is not Point rightEye)
        {
            return (null, null, null, null);
        }

        var horizontal = CreateFaceHorizontalAxis(leftEyeCenter, rightEyeCenter);
        var vertical = new Vector(-horizontal.Y, horizontal.X);
        if (vertical.Y < 0d)
        {
            vertical = new Vector(-vertical.X, -vertical.Y);
        }

        var faceWidth = CalculateProjectedFaceWidth(frame, horizontal);
        if (faceWidth <= 0.001d)
        {
            return (null, null, null, null);
        }

        var leftHeight = Math.Clamp((Dot(leftEye, vertical) - Dot(leftBrow, vertical)) / faceWidth, 0d, 0.60d);
        var rightHeight = Math.Clamp((Dot(rightEye, vertical) - Dot(rightBrow, vertical)) / faceWidth, 0d, 0.60d);
        var average = (leftHeight + rightHeight) / 2d;
        var asymmetry = CalculateAsymmetryPercent(leftHeight, rightHeight, average);
        return (leftHeight, rightHeight, average, asymmetry);
    }

    private static double CalculateProjectedFaceWidth(FaceLandmarkFrame frame, Vector horizontal)
    {
        var pointCount = frame.FaceContour.Count
            + frame.JawContour.Count
            + frame.LeftBrowContour.Count
            + frame.RightBrowContour.Count
            + frame.LeftEyeContour.Count
            + frame.RightEyeContour.Count
            + frame.OuterLipContour.Count
            + frame.InnerLipContour.Count;
        if (pointCount < 2)
        {
            return 0d;
        }

        var left = double.PositiveInfinity;
        var right = double.NegativeInfinity;
        ExpandProjectedRange(frame.FaceContour, horizontal, ref left, ref right);
        ExpandProjectedRange(frame.JawContour, horizontal, ref left, ref right);
        ExpandProjectedRange(frame.LeftBrowContour, horizontal, ref left, ref right);
        ExpandProjectedRange(frame.RightBrowContour, horizontal, ref left, ref right);
        ExpandProjectedRange(frame.LeftEyeContour, horizontal, ref left, ref right);
        ExpandProjectedRange(frame.RightEyeContour, horizontal, ref left, ref right);
        ExpandProjectedRange(frame.OuterLipContour, horizontal, ref left, ref right);
        ExpandProjectedRange(frame.InnerLipContour, horizontal, ref left, ref right);
        return Math.Max(0d, right - left);
    }

    private static Vector CreateFaceHorizontalAxis(Point? leftEyeCenter, Point? rightEyeCenter)
    {
        if (leftEyeCenter is Point left && rightEyeCenter is Point right)
        {
            var axis = new Vector(right.X - left.X, right.Y - left.Y);
            if (axis.Length >= 0.001d)
            {
                axis.Normalize();
                return axis;
            }
        }

        return new Vector(1d, 0d);
    }

    private static Point? Center(IReadOnlyList<Point> contour)
    {
        if (contour.Count == 0)
        {
            return null;
        }

        var sumX = 0d;
        var sumY = 0d;
        foreach (var point in contour)
        {
            sumX += point.X;
            sumY += point.Y;
        }

        return new Point(sumX / contour.Count, sumY / contour.Count);
    }

    private static Point? AveragePoint(Point? first, Point? second)
    {
        if (first.HasValue && second.HasValue)
        {
            var left = first.Value;
            var right = second.Value;
            return new Point((left.X + right.X) / 2d, (left.Y + right.Y) / 2d);
        }

        return first ?? second;
    }

    private static double Dot(Point point, Vector axis)
    {
        return point.X * axis.X + point.Y * axis.Y;
    }

    private static double? CalculateAsymmetryPercent(double? leftEyeOpening, double? rightEyeOpening, double? averageEyeOpening)
    {
        if (leftEyeOpening is not double left || rightEyeOpening is not double right)
        {
            return null;
        }

        var denominator = Math.Max(Math.Abs(averageEyeOpening ?? ((left + right) / 2d)), 0.025d);
        return Math.Clamp(Math.Abs(left - right) / denominator * 100d, 0d, 300d);
    }

    private static bool IsPossibleOneEyeArtifact(
        FaceLandmarkFrame frame,
        double? rawEyeAsymmetryPercent,
        bool highFidelityLandmarkSource)
    {
        if (rawEyeAsymmetryPercent is not double asymmetry)
        {
            return false;
        }

        var glareOrWeakImageEvidence = frame.EyeImageQualityAvailable
            && (frame.EyeGlarePercent >= 5d || frame.EyeContrastPercent < 35d || frame.EyeSharpnessPercent < 25d);
        var reconstructedOrSuppressed = frame.EyeArtifactSuppressed
            || frame.LeftEyeReconstructed
            || frame.RightEyeReconstructed;
        var highFidelityDirectEye = highFidelityLandmarkSource
            && frame.EyeConfidence >= 0.72d
            && !reconstructedOrSuppressed;
        return (asymmetry >= 85d
                && !highFidelityDirectEye
                && (glareOrWeakImageEvidence || reconstructedOrSuppressed || frame.EyeConfidence < 0.58d))
            || (asymmetry >= 55d && glareOrWeakImageEvidence)
            || (asymmetry >= 45d && reconstructedOrSuppressed && frame.EyeConfidence < 0.72d);
    }

    private static double? Average(double? first, double? second)
    {
        if (first is double left && second is double right)
        {
            return (left + right) / 2d;
        }

        return first ?? second;
    }

    private static double? ScaleOpening(double? opening, double scale)
    {
        return opening is double value
            ? Math.Clamp(value * scale, 0d, 0.85d)
            : null;
    }

    private static double? BlendshapePercent(FaceLandmarkFrame frame, string categoryName)
    {
        return frame.BlendshapeScores.TryGetValue(categoryName, out var score)
            ? Math.Clamp(score, 0d, 1d) * 100d
            : null;
    }

    private static bool ShouldUsePairedAverage(
        FaceLandmarkFrame frame,
        IReadOnlyList<Point> contour,
        bool isEye,
        bool highFidelityLandmarkSource)
    {
        if (contour.Count < 4)
        {
            return false;
        }

        if (isEye && contour.Count == 6)
        {
            return true;
        }

        if (highFidelityLandmarkSource)
        {
            return true;
        }

        if (!isEye
            && contour.Count == 8
            && frame.Source.Contains("LBF", StringComparison.OrdinalIgnoreCase)
            && !frame.Source.Contains("fused", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private void UpdateMediaPipeOpenEyeReference(
        FaceLandmarkFrame frame,
        double? averageEyeOpening,
        double? mediaPipeAverageBlink,
        bool highFidelityLandmarkSource)
    {
        if (!highFidelityLandmarkSource
            || averageEyeOpening is not double opening
            || mediaPipeAverageBlink is not double blink
            || blink > 35d
            || frame.EyeConfidence < 0.45d
            || opening < 0.025d
            || opening > 0.65d)
        {
            return;
        }

        _mediaPipeOpenEyeReferenceRatio = _mediaPipeOpenEyeReferenceRatio is double reference
            ? Math.Max(reference * 0.995d, opening)
            : opening;
    }

    private double? StabilizeEyeOpeningWithMediaPipe(double? contourOpening, double? mediaPipeBlinkPercent)
    {
        if (contourOpening is not double opening
            || mediaPipeBlinkPercent is not double blink
            || _mediaPipeOpenEyeReferenceRatio is not double openReference
            || blink < 38d)
        {
            return contourOpening;
        }

        var closureStrength = Math.Clamp((blink - 38d) / 54d, 0d, 1d);
        var closedFraction = Math.Clamp(1d - Math.Pow(closureStrength, 0.85d) * 0.92d, 0.08d, 1d);
        var blinkCappedOpening = openReference * closedFraction;
        return Math.Min(opening, blinkCappedOpening);
    }

    private void UpdateMediaPipeClosedMouthReference(
        FaceLandmarkFrame frame,
        double? mouthOpening,
        double? mediaPipeJawOpenPercent,
        double? mediaPipeMouthClosePercent,
        bool highFidelityLandmarkSource)
    {
        if (!highFidelityLandmarkSource
            || mouthOpening is not double opening
            || mediaPipeJawOpenPercent is not double jawOpen
            || mediaPipeMouthClosePercent is not double mouthClose
            || jawOpen > 26d
            || mouthClose < 48d
            || frame.MouthConfidence < 0.42d
            || opening < 0.004d
            || opening > 0.45d)
        {
            return;
        }

        _mediaPipeClosedMouthReferenceRatio = _mediaPipeClosedMouthReferenceRatio is double reference
            ? Math.Min(reference * 1.005d, opening)
            : opening;
    }

    private double? StabilizeMouthOpeningWithMediaPipe(
        double? contourOpening,
        double? mediaPipeJawOpenPercent,
        double? mediaPipeMouthClosePercent)
    {
        if (contourOpening is not double opening
            || _mediaPipeClosedMouthReferenceRatio is not double closedReference)
        {
            return contourOpening;
        }

        var jawOpen = mediaPipeJawOpenPercent.GetValueOrDefault(double.NaN);
        var mouthOpenByCloseDrop = mediaPipeMouthClosePercent is double close ? 100d - close : double.NaN;
        var mouthOpenEvidence = double.IsNaN(jawOpen)
            ? mouthOpenByCloseDrop
            : double.IsNaN(mouthOpenByCloseDrop)
                ? jawOpen
                : Math.Max(jawOpen, mouthOpenByCloseDrop);
        if (!double.IsNaN(mouthOpenEvidence) && mouthOpenEvidence >= 58d)
        {
            var targetRise = Math.Max(0.12d, closedReference * 1.35d) * Math.Clamp(mouthOpenEvidence / 100d, 0.40d, 1d);
            var blendshapeMinimum = Math.Clamp(closedReference + targetRise, 0d, 0.85d);
            return Math.Max(opening, blendshapeMinimum);
        }

        var strongClosedEvidence = mediaPipeJawOpenPercent is double closedJaw
            && mediaPipeMouthClosePercent is double closedMouth
            && closedJaw <= 28d
            && closedMouth >= 60d;
        if (strongClosedEvidence)
        {
            return Math.Min(opening, Math.Clamp(closedReference * 1.35d, 0d, 0.55d));
        }

        return contourOpening;
    }

    private static bool IsHighFidelityLandmarkSource(string source)
    {
        return source.Contains("MediaPipe", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Face Landmarker", StringComparison.OrdinalIgnoreCase)
            || source.Contains("dense", StringComparison.OrdinalIgnoreCase)
            || source.Contains("face mesh", StringComparison.OrdinalIgnoreCase);
    }

    private static void ExpandProjectedRange(
        IReadOnlyList<Point> points,
        Vector axis,
        ref double minimum,
        ref double maximum)
    {
        foreach (var point in points)
        {
            var projection = Dot(point, axis);
            minimum = Math.Min(minimum, projection);
            maximum = Math.Max(maximum, projection);
        }
    }

    private static double MaximumProjection(IReadOnlyList<Point> points, Vector axis)
    {
        var maximum = double.NegativeInfinity;
        foreach (var point in points)
        {
            maximum = Math.Max(maximum, Dot(point, axis));
        }

        return maximum;
    }

    private static double? CalculateCorrection(double? adjusted, double? raw)
    {
        if (adjusted is not double adjustedValue || raw is not double rawValue)
        {
            return null;
        }

        var correction = adjustedValue - rawValue;
        return Math.Abs(correction) < 0.000001d ? null : correction;
    }
}
