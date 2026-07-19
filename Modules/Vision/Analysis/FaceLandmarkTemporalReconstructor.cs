using AvatarBuilder.Modules.Vision.Common;
using System.Windows;

namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceLandmarkTemporalReconstructor
{
    private const double DefaultEyeSymmetryScale = 1d;
    private double? _lastLeftEyeOpeningRatio;
    private double? _lastRightEyeOpeningRatio;
    private double? _lastMouthOpeningRatio;
    private double _leftToRightEyeOpeningScale = DefaultEyeSymmetryScale;
    private Rect? _lastLeftEyeBounds;
    private Rect? _lastRightEyeBounds;
    private Rect? _lastMouthBounds;
    private Rect? _lastFaceBounds;
    private DateTime? _lastCapturedAtUtc;

    public void Reset()
    {
        _lastLeftEyeOpeningRatio = null;
        _lastRightEyeOpeningRatio = null;
        _lastMouthOpeningRatio = null;
        _leftToRightEyeOpeningScale = DefaultEyeSymmetryScale;
        _lastLeftEyeBounds = null;
        _lastRightEyeBounds = null;
        _lastMouthBounds = null;
        _lastFaceBounds = null;
        _lastCapturedAtUtc = null;
    }

    public FaceLandmarkFrame Update(FaceLandmarkFrame frame)
    {
        if (!frame.HasFace)
        {
            Reset();
            return frame;
        }

        var elapsedSeconds = CalculateElapsedSeconds(frame.CapturedAtUtc);
        var leftBounds = TryGetBounds(frame.LeftEyeContour);
        var rightBounds = TryGetBounds(frame.RightEyeContour);
        var mouthBounds = TryGetBounds(frame.InnerLipContour.Count >= 4 ? frame.InnerLipContour : frame.OuterLipContour);
        var faceBounds = EstimateFaceBounds(frame);
        var faceMappedLastLeftEyeBounds = MapPreviousBoundsToCurrentFace(_lastLeftEyeBounds, _lastFaceBounds, faceBounds);
        var faceMappedLastRightEyeBounds = MapPreviousBoundsToCurrentFace(_lastRightEyeBounds, _lastFaceBounds, faceBounds);
        var faceMappedLastMouthBounds = MapPreviousBoundsToCurrentFace(_lastMouthBounds, _lastFaceBounds, faceBounds);
        var usePairedLeftEye = ShouldUsePairedAverage(frame.Source, frame.LeftEyeContour, isEye: true);
        var usePairedRightEye = ShouldUsePairedAverage(frame.Source, frame.RightEyeContour, isEye: true);
        var mouthContour = frame.InnerLipContour.Count >= 4 ? frame.InnerLipContour : frame.OuterLipContour;
        var usePairedMouth = ShouldUsePairedAverage(frame.Source, mouthContour, isEye: false);
        var measuredLeftEye = ContourOpeningEstimator.CalculateOpeningRatio(frame.LeftEyeContour, usePairedLeftEye);
        var measuredRightEye = ContourOpeningEstimator.CalculateOpeningRatio(frame.RightEyeContour, usePairedRightEye);
        var measuredMouth = ContourOpeningEstimator.CalculateOpeningRatio(mouthContour, usePairedMouth);
        var mediaPipeLeftBlink = BlendshapePercent(frame, "eyeBlinkLeft");
        var mediaPipeRightBlink = BlendshapePercent(frame, "eyeBlinkRight");
        var mediaPipeAverageBlink = Average(mediaPipeLeftBlink, mediaPipeRightBlink);
        var mediaPipeJawOpen = BlendshapePercent(frame, "jawOpen");
        var mediaPipeMouthClose = BlendshapePercent(frame, "mouthClose");
        var eyeConfidence = frame.EyeConfidence;
        var mouthConfidence = frame.MouthConfidence;
        var reconstructed = false;
        var leftEyeReconstructed = false;
        var rightEyeReconstructed = false;
        var mouthReconstructed = false;
        var eyeArtifactSuppressed = false;
        var guardLowFidelityEyeOpening = !IsHighFidelityLandmarkSource(frame.Source) || frame.BlendshapeScores.Count == 0;

        var leftShapeArtifact = IsLikelyEyeContourShapeArtifact(leftBounds, faceMappedLastLeftEyeBounds, rightBounds, frame);
        var rightShapeArtifact = IsLikelyEyeContourShapeArtifact(rightBounds, faceMappedLastRightEyeBounds, leftBounds, frame);
        if (leftShapeArtifact)
        {
            measuredLeftEye = null;
            reconstructed = true;
            leftEyeReconstructed = true;
            eyeArtifactSuppressed = true;
        }

        if (rightShapeArtifact)
        {
            measuredRightEye = null;
            reconstructed = true;
            rightEyeReconstructed = true;
            eyeArtifactSuppressed = true;
        }

        UpdateEyeSymmetryScale(measuredLeftEye, measuredRightEye, eyeConfidence);

        var leftEye = ReconstructEyeRatio(
            measuredLeftEye,
            measuredRightEye,
            _lastLeftEyeOpeningRatio,
            _lastRightEyeOpeningRatio,
            _leftToRightEyeOpeningScale,
            elapsedSeconds,
            eyeConfidence,
            mediaPipeLeftBlink ?? mediaPipeAverageBlink,
            guardLowFidelityEyeOpening,
            isLeftEye: true,
            ref reconstructed,
            ref leftEyeReconstructed,
            ref eyeArtifactSuppressed);
        var rightEye = ReconstructEyeRatio(
            measuredRightEye,
            measuredLeftEye,
            _lastRightEyeOpeningRatio,
            _lastLeftEyeOpeningRatio,
            _leftToRightEyeOpeningScale,
            elapsedSeconds,
            eyeConfidence,
            mediaPipeRightBlink ?? mediaPipeAverageBlink,
            guardLowFidelityEyeOpening,
            isLeftEye: false,
            ref reconstructed,
            ref rightEyeReconstructed,
            ref eyeArtifactSuppressed);
        var mouth = ReconstructMouthRatio(
            measuredMouth,
            _lastMouthOpeningRatio,
            elapsedSeconds,
            mouthConfidence,
            mediaPipeJawOpen,
            mediaPipeMouthClose,
            ref reconstructed,
            ref mouthReconstructed);

        var leftReconstructionBounds = ChooseEyeReconstructionBounds(frame, leftBounds, faceMappedLastLeftEyeBounds, rightBounds, leftShapeArtifact, isLeftEye: true);
        var rightReconstructionBounds = ChooseEyeReconstructionBounds(frame, rightBounds, faceMappedLastRightEyeBounds, leftBounds, rightShapeArtifact, isLeftEye: false);
        var leftContour = BuildReconstructedContour(frame.LeftEyeContour, leftReconstructionBounds, leftEye, usePairedLeftEye, ref reconstructed, ref leftEyeReconstructed);
        var rightContour = BuildReconstructedContour(frame.RightEyeContour, rightReconstructionBounds, rightEye, usePairedRightEye, ref reconstructed, ref rightEyeReconstructed);
        var innerLipContour = BuildReconstructedContour(
            frame.InnerLipContour,
            mouthBounds ?? faceMappedLastMouthBounds,
            mouth,
            usePairedMouth,
            ref reconstructed,
            ref mouthReconstructed);

        Remember(frame.CapturedAtUtc, leftEye, rightEye, mouth, leftReconstructionBounds, rightReconstructionBounds, mouthBounds ?? faceMappedLastMouthBounds, faceBounds);

        if (!reconstructed)
        {
            return frame;
        }

        return new FaceLandmarkFrame
        {
            HasFace = true,
            Source = string.IsNullOrWhiteSpace(frame.Source)
                ? "temporal reconstruction"
                : $"{frame.Source}; temporal reconstruction",
            CapturedAtUtc = frame.CapturedAtUtc,
            TrackingConfidence = frame.TrackingConfidence,
            EyeConfidence = Math.Max(frame.EyeConfidence, leftEye.HasValue || rightEye.HasValue ? 0.32d : frame.EyeConfidence),
            MouthConfidence = Math.Max(frame.MouthConfidence, mouth.HasValue ? 0.24d : frame.MouthConfidence),
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
            LeftEyeReconstructed = frame.LeftEyeReconstructed || leftEyeReconstructed,
            RightEyeReconstructed = frame.RightEyeReconstructed || rightEyeReconstructed,
            MouthReconstructed = frame.MouthReconstructed || mouthReconstructed,
            EyeArtifactSuppressed = frame.EyeArtifactSuppressed || eyeArtifactSuppressed,
            HeadYawDegrees = frame.HeadYawDegrees,
            HeadPitchDegrees = frame.HeadPitchDegrees,
            HeadRollDegrees = frame.HeadRollDegrees,
            BlendshapeScores = frame.BlendshapeScores,
            DenseMeshTopology = frame.DenseMeshTopology,
            DenseMeshPoints = frame.DenseMeshPoints,
            FacialTransformationMatrix = frame.FacialTransformationMatrix,
            FaceContour = frame.FaceContour,
            LeftEyeContour = leftContour,
            RightEyeContour = rightContour,
            LeftBrowContour = frame.LeftBrowContour,
            RightBrowContour = frame.RightBrowContour,
            OuterLipContour = frame.OuterLipContour,
            InnerLipContour = innerLipContour,
            JawContour = frame.JawContour
        };
    }

    private double CalculateElapsedSeconds(DateTime capturedAtUtc)
    {
        if (_lastCapturedAtUtc is not DateTime last)
        {
            return 0.5d;
        }

        return Math.Clamp((capturedAtUtc - last).TotalSeconds, 0.1d, 3d);
    }

    private void UpdateEyeSymmetryScale(double? leftEye, double? rightEye, double eyeConfidence)
    {
        if (eyeConfidence < 0.45d
            || leftEye is not double left
            || rightEye is not double right
            || right <= 0.001d)
        {
            return;
        }

        var scale = Math.Clamp(left / right, 0.55d, 1.45d);
        _leftToRightEyeOpeningScale += (scale - _leftToRightEyeOpeningScale) * 0.08d;
    }

    private static double? ReconstructEyeRatio(
        double? measured,
        double? pairedMeasured,
        double? previous,
        double? pairedPrevious,
        double leftToRightScale,
        double elapsedSeconds,
        double confidence,
        double? mediaPipeBlinkPercent,
        bool guardLowFidelityOpening,
        bool isLeftEye,
        ref bool reconstructed,
        ref bool featureReconstructed,
        ref bool artifactSuppressed)
    {
        var estimatedFromPair = EstimateFromPair(pairedMeasured, leftToRightScale, isLeftEye);
        var hasDirectMeasurement = measured.HasValue;
        var blendshapeGuidedClosure = false;
        var value = measured;

        if (value is null && estimatedFromPair is double pairValue)
        {
            value = pairValue;
            reconstructed = true;
            featureReconstructed = true;
        }
        else if (value is null && previous is double previousValue)
        {
            value = previousValue;
            reconstructed = true;
            featureReconstructed = true;
        }

        if (value is double current
            && estimatedFromPair is double pairEstimate
            && previous is double prior
            && confidence < 0.58d
            && IsLikelyEyeArtifact(current, pairEstimate, prior))
        {
            value = pairEstimate * 0.70d + prior * 0.30d;
            reconstructed = true;
            featureReconstructed = true;
            artifactSuppressed = true;
        }

        if (value is double blinkCandidate
            && previous is double blinkPrior
            && mediaPipeBlinkPercent is double blink
            && blink >= 58d
            && (!hasDirectMeasurement || featureReconstructed || confidence < 0.45d))
        {
            var closedFraction = Math.Clamp(1d - blink / 100d, 0.08d, 1d);
            var blinkGuidedValue = Math.Min(blinkCandidate, blinkPrior * closedFraction);
            if (blinkGuidedValue < blinkCandidate - 0.0001d)
            {
                value = blinkGuidedValue;
                reconstructed = true;
                featureReconstructed = true;
                blendshapeGuidedClosure = true;
            }
        }

        var shouldRateLimit =
            blendshapeGuidedClosure
            || guardLowFidelityOpening
            || featureReconstructed
            || !hasDirectMeasurement
            || confidence < 0.72d;
        if (shouldRateLimit && value is double limitedCandidate && previous is double previousRatio)
        {
            var closingRate = blendshapeGuidedClosure ? 1.20d : 0.24d;
            var openingRate = guardLowFidelityOpening
                && limitedCandidate > previousRatio
                && (featureReconstructed || !hasDirectMeasurement || confidence < 0.72d || mediaPipeBlinkPercent is null)
                    ? 0.025d
                    : 0.18d;
            var limited = LimitRatioChange(limitedCandidate, previousRatio, elapsedSeconds, closingRatePerSecond: closingRate, openingRatePerSecond: openingRate);
            if (Math.Abs(limited - limitedCandidate) > 0.0001d)
            {
                value = limited;
                reconstructed = true;
                featureReconstructed = true;
            }
        }

        if (value is double finalValue)
        {
            return Math.Clamp(finalValue, 0.015d, 0.85d);
        }

        if (pairedPrevious is double pairPrevious)
        {
            reconstructed = true;
            featureReconstructed = true;
            return EstimateFromPair(pairPrevious, leftToRightScale, isLeftEye);
        }

        return null;
    }

    private static double? ReconstructMouthRatio(
        double? measured,
        double? previous,
        double elapsedSeconds,
        double confidence,
        double? mediaPipeJawOpenPercent,
        double? mediaPipeMouthClosePercent,
        ref bool reconstructed,
        ref bool featureReconstructed)
    {
        var hasDirectMeasurement = measured.HasValue;
        var blendshapeGuidedOpening = false;
        var blendshapeGuidedClosing = false;
        var value = measured;
        if (value is null && previous is double previousValue)
        {
            value = previousValue;
            reconstructed = true;
            featureReconstructed = true;
        }

        if (value is double mouthCandidate
            && previous is double mouthPrior
            && (!hasDirectMeasurement || featureReconstructed || confidence < 0.40d))
        {
            var jawOpen = mediaPipeJawOpenPercent.GetValueOrDefault(double.NaN);
            var mouthOpenEvidence = jawOpen;
            if (jawOpen >= 35d && mediaPipeMouthClosePercent is double close && close <= 35d)
            {
                mouthOpenEvidence = Math.Max(jawOpen, 100d - close);
            }

            if (!double.IsNaN(mouthOpenEvidence) && mouthOpenEvidence >= 58d)
            {
                var targetRise = Math.Max(0.12d, mouthPrior * 1.35d) * Math.Clamp(mouthOpenEvidence / 100d, 0.40d, 1d);
                var blendshapeGuidedValue = Math.Max(mouthCandidate, mouthPrior + targetRise);
                if (blendshapeGuidedValue > mouthCandidate + 0.0001d)
                {
                    value = blendshapeGuidedValue;
                    reconstructed = true;
                    featureReconstructed = true;
                    blendshapeGuidedOpening = true;
                }
            }

            var strongClosedEvidence = mediaPipeJawOpenPercent is double closedJaw
                && mediaPipeMouthClosePercent is double closedMouth
                && closedJaw <= 28d
                && closedMouth >= 60d;
            if (strongClosedEvidence)
            {
                var blendshapeGuidedValue = Math.Min(mouthCandidate, Math.Clamp(mouthPrior * 0.90d, 0.01d, 0.55d));
                if (blendshapeGuidedValue < mouthCandidate - 0.0001d)
                {
                    value = blendshapeGuidedValue;
                    reconstructed = true;
                    featureReconstructed = true;
                    blendshapeGuidedClosing = true;
                }
            }
        }

        if (value is double candidate && previous is double prior && confidence < 0.40d)
        {
            var closingRate = blendshapeGuidedClosing ? 0.65d : 0.22d;
            var openingRate = blendshapeGuidedOpening ? 0.85d : 0.28d;
            var limited = LimitRatioChange(candidate, prior, elapsedSeconds, closingRatePerSecond: closingRate, openingRatePerSecond: openingRate);
            if (Math.Abs(limited - candidate) > 0.0001d)
            {
                value = limited;
                reconstructed = true;
                featureReconstructed = true;
            }
        }

        return value is double finalValue ? Math.Clamp(finalValue, 0.01d, 1.2d) : null;
    }

    private static double? BlendshapePercent(FaceLandmarkFrame frame, string categoryName)
    {
        return frame.BlendshapeScores.TryGetValue(categoryName, out var score)
            ? Math.Clamp(score, 0d, 1d) * 100d
            : null;
    }

    private static double? Average(double? first, double? second)
    {
        return (first, second) switch
        {
            (double left, double right) => (left + right) / 2d,
            (double left, null) => left,
            (null, double right) => right,
            _ => null
        };
    }

    private static double? EstimateFromPair(double? pairedRatio, double leftToRightScale, bool isLeftEye)
    {
        if (pairedRatio is not double pair)
        {
            return null;
        }

        return isLeftEye
            ? pair * leftToRightScale
            : pair / Math.Max(0.001d, leftToRightScale);
    }

    private static bool IsLikelyEyeArtifact(double measured, double pairEstimate, double previous)
    {
        var pairDifference = Math.Abs(measured - pairEstimate);
        var previousDifference = Math.Abs(measured - previous);
        var pairAndPreviousAgree = Math.Abs(pairEstimate - previous) < 0.08d;
        return pairDifference > 0.10d
            && previousDifference > 0.10d
            && pairAndPreviousAgree;
    }

    private static bool IsLikelyEyeContourShapeArtifact(Rect? current, Rect? previous, Rect? paired, FaceLandmarkFrame frame)
    {
        if (current is not Rect currentRect)
        {
            return false;
        }

        var imageArtifactSignal = frame.EyeImageQualityAvailable
            && (frame.EyeGlarePercent >= 8d || frame.EyeContrastPercent < 30d || frame.EyeSharpnessPercent < 22d);
        if (frame.EyeConfidence >= 0.58d && !imageArtifactSignal)
        {
            return false;
        }

        var aspect = currentRect.Width <= 0.0001d ? 2d : currentRect.Height / currentRect.Width;
        var implausibleShape = currentRect.Width > 0.24d || currentRect.Height > 0.18d || aspect > 0.90d;
        var previousSizeOutlier = previous is Rect previousRect && IsEyeBoundsSizeOutlier(currentRect, previousRect);
        var previousCenterOutlier = previous is Rect previousCenterRect
            && Distance(Center(currentRect), Center(previousCenterRect)) > Math.Max(0.055d, previousCenterRect.Width * 0.85d);
        var pairedSizeOutlier = paired is Rect pairedRect && IsEyeBoundsSizeOutlier(currentRect, pairedRect);
        return implausibleShape
            || previousSizeOutlier
            || pairedSizeOutlier
            || (previousCenterOutlier && paired is null && imageArtifactSignal);
    }

    private static bool IsEyeBoundsSizeOutlier(Rect current, Rect reference)
    {
        if (reference.Width <= 0.0001d || reference.Height <= 0.0001d)
        {
            return false;
        }

        var widthRatio = current.Width / reference.Width;
        var heightRatio = current.Height / reference.Height;
        return widthRatio is > 1.75d or < 0.48d
            || heightRatio > 2.65d;
    }

    private static bool IsHighFidelityLandmarkSource(string source)
    {
        return source.Contains("MediaPipe", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Face Landmarker", StringComparison.OrdinalIgnoreCase)
            || source.Contains("dense", StringComparison.OrdinalIgnoreCase)
            || source.Contains("face mesh", StringComparison.OrdinalIgnoreCase);
    }

    private static Rect? ChooseEyeReconstructionBounds(
        FaceLandmarkFrame frame,
        Rect? current,
        Rect? previous,
        Rect? paired,
        bool shapeArtifact,
        bool isLeftEye)
    {
        if (shapeArtifact)
        {
            return previous ?? EstimateEyeBoundsFromPairedEye(frame, paired, isLeftEye);
        }

        return current
            ?? previous
            ?? EstimateEyeBoundsFromPairedEye(frame, paired, isLeftEye);
    }

    private static Rect? EstimateEyeBoundsFromPairedEye(FaceLandmarkFrame frame, Rect? paired, bool isLeftEye)
    {
        if (paired is not Rect pairedRect)
        {
            return null;
        }

        var faceCenter = Center(frame.FaceContour)
            ?? Center(frame.JawContour)
            ?? Center(frame.OuterLipContour);
        if (faceCenter is not Point center)
        {
            return null;
        }

        var pairedCenter = Center(pairedRect);
        var mirroredCenter = new Point(
            center.X - (pairedCenter.X - center.X),
            center.Y - (pairedCenter.Y - center.Y));
        var halfWidth = pairedRect.Width / 2d;
        var halfHeight = pairedRect.Height / 2d;
        var left = Math.Clamp(mirroredCenter.X - halfWidth, 0d, 1d);
        var top = Math.Clamp(mirroredCenter.Y - halfHeight, 0d, 1d);
        var width = Math.Min(pairedRect.Width, 1d - left);
        var height = Math.Min(pairedRect.Height, 1d - top);
        if (width <= 0.0001d || height <= 0.0001d)
        {
            return null;
        }

        return new Rect(left, top, width, height);
    }

    private static double LimitRatioChange(
        double current,
        double previous,
        double elapsedSeconds,
        double closingRatePerSecond,
        double openingRatePerSecond)
    {
        var delta = current - previous;
        var maximumDelta = (delta >= 0d ? openingRatePerSecond : closingRatePerSecond) * elapsedSeconds;
        return previous + Math.Clamp(delta, -maximumDelta, maximumDelta);
    }

    private static IReadOnlyList<Point> BuildReconstructedContour(
        IReadOnlyList<Point> original,
        Rect? bounds,
        double? ratio,
        bool preferPairedAverage,
        ref bool reconstructed,
        ref bool featureReconstructed)
    {
        if (ratio is not double openingRatio || bounds is not Rect rect || rect.Width <= 0d)
        {
            return original;
        }

        var currentRatio = original.Count >= 4
            ? ContourOpeningEstimator.CalculateOpeningRatio(original, preferPairedAverage)
            : CalculateOpeningRatio(rect);
        var originalBounds = TryGetBounds(original);
        var boundsMatch = originalBounds is Rect originalRect && AreBoundsClose(originalRect, rect);
        if (original.Count >= 4 && boundsMatch && currentRatio is double current && Math.Abs(current - openingRatio) < 0.006d)
        {
            return original;
        }

        reconstructed = true;
        featureReconstructed = true;
        var centerX = rect.Left + rect.Width / 2d;
        var centerY = rect.Top + rect.Height / 2d;
        var halfWidth = rect.Width / 2d;
        var halfHeight = Math.Max(0.0025d, rect.Width * openingRatio / 2d);
        return CreateOvalContour(centerX, centerY, halfWidth, halfHeight);
    }

    private static bool ShouldUsePairedAverage(string source, IReadOnlyList<Point> contour, bool isEye)
    {
        if (contour.Count < 4)
        {
            return false;
        }

        if (isEye && contour.Count == 6)
        {
            return true;
        }

        if (source.Contains("dense", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!isEye
            && contour.Count == 8
            && source.Contains("LBF", StringComparison.OrdinalIgnoreCase)
            && !source.Contains("fused", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private void Remember(
        DateTime capturedAtUtc,
        double? leftEye,
        double? rightEye,
        double? mouth,
        Rect? leftBounds,
        Rect? rightBounds,
        Rect? mouthBounds,
        Rect? faceBounds)
    {
        _lastCapturedAtUtc = capturedAtUtc;
        _lastLeftEyeOpeningRatio = leftEye ?? _lastLeftEyeOpeningRatio;
        _lastRightEyeOpeningRatio = rightEye ?? _lastRightEyeOpeningRatio;
        _lastMouthOpeningRatio = mouth ?? _lastMouthOpeningRatio;
        _lastLeftEyeBounds = leftBounds ?? _lastLeftEyeBounds;
        _lastRightEyeBounds = rightBounds ?? _lastRightEyeBounds;
        _lastMouthBounds = mouthBounds ?? _lastMouthBounds;
        _lastFaceBounds = faceBounds ?? _lastFaceBounds;
    }

    private static Rect? EstimateFaceBounds(FaceLandmarkFrame frame)
    {
        return TryGetBounds(frame.FaceContour)
            ?? TryGetBounds(frame.JawContour)
            ?? UnionBounds(
                TryGetBounds(frame.LeftEyeContour),
                TryGetBounds(frame.RightEyeContour),
                TryGetBounds(frame.OuterLipContour),
                TryGetBounds(frame.InnerLipContour));
    }

    private static Rect? MapPreviousBoundsToCurrentFace(Rect? previousFeatureBounds, Rect? previousFaceBounds, Rect? currentFaceBounds)
    {
        if (previousFeatureBounds is not Rect feature
            || previousFaceBounds is not Rect previousFace
            || currentFaceBounds is not Rect currentFace
            || previousFace.Width <= 0.0001d
            || previousFace.Height <= 0.0001d
            || currentFace.Width <= 0.0001d
            || currentFace.Height <= 0.0001d)
        {
            return previousFeatureBounds;
        }

        var relativeLeft = (feature.Left - previousFace.Left) / previousFace.Width;
        var relativeTop = (feature.Top - previousFace.Top) / previousFace.Height;
        var relativeWidth = feature.Width / previousFace.Width;
        var relativeHeight = feature.Height / previousFace.Height;
        var mappedLeft = currentFace.Left + relativeLeft * currentFace.Width;
        var mappedTop = currentFace.Top + relativeTop * currentFace.Height;
        var mappedWidth = relativeWidth * currentFace.Width;
        var mappedHeight = relativeHeight * currentFace.Height;
        return ClampRect(mappedLeft, mappedTop, mappedWidth, mappedHeight);
    }

    private static Rect? UnionBounds(params Rect?[] bounds)
    {
        Rect? union = null;
        foreach (var bound in bounds)
        {
            if (bound is not Rect rect)
            {
                continue;
            }

            union = union is Rect current
                ? Rect.Union(current, rect)
                : rect;
        }

        return union;
    }

    private static Rect? ClampRect(double left, double top, double width, double height)
    {
        var clampedLeft = Math.Clamp(left, 0d, 1d);
        var clampedTop = Math.Clamp(top, 0d, 1d);
        var clampedRight = Math.Clamp(left + width, clampedLeft, 1d);
        var clampedBottom = Math.Clamp(top + height, clampedTop, 1d);
        var clampedWidth = clampedRight - clampedLeft;
        var clampedHeight = clampedBottom - clampedTop;
        return clampedWidth <= 0.0001d || clampedHeight <= 0.0001d
            ? null
            : new Rect(clampedLeft, clampedTop, clampedWidth, clampedHeight);
    }

    private static double? CalculateOpeningRatio(Rect? bounds)
    {
        return bounds is Rect rect ? CalculateOpeningRatio(rect) : null;
    }

    private static double? CalculateOpeningRatio(Rect rect)
    {
        if (rect.Width <= 0.0001d || rect.Height <= 0.0001d)
        {
            return null;
        }

        return Math.Clamp(rect.Height / rect.Width, 0d, 2d);
    }

    private static Rect? TryGetBounds(IReadOnlyList<Point> points)
    {
        if (points.Count < 4)
        {
            return null;
        }

        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxY = points.Max(point => point.Y);
        if (maxX <= minX || maxY <= minY)
        {
            return null;
        }

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static bool AreBoundsClose(Rect first, Rect second)
    {
        var centerDistance = Distance(Center(first), Center(second));
        var widthDelta = Math.Abs(first.Width - second.Width);
        var heightDelta = Math.Abs(first.Height - second.Height);
        return centerDistance < Math.Max(0.006d, second.Width * 0.08d)
            && widthDelta < Math.Max(0.006d, second.Width * 0.10d)
            && heightDelta < Math.Max(0.006d, second.Height * 0.16d);
    }

    private static Point Center(Rect rect)
    {
        return new Point(rect.Left + rect.Width / 2d, rect.Top + rect.Height / 2d);
    }

    private static Point? Center(IReadOnlyList<Point> points)
    {
        if (points.Count == 0)
        {
            return null;
        }

        return new Point(
            points.Average(static point => point.X),
            points.Average(static point => point.Y));
    }

    private static double Distance(Point first, Point second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static IReadOnlyList<Point> CreateOvalContour(double centerX, double centerY, double halfWidth, double halfHeight)
    {
        return
        [
            new(centerX - halfWidth, centerY),
            new(centerX - halfWidth * 0.72d, centerY - halfHeight * 0.70d),
            new(centerX, centerY - halfHeight),
            new(centerX + halfWidth * 0.72d, centerY - halfHeight * 0.70d),
            new(centerX + halfWidth, centerY),
            new(centerX + halfWidth * 0.72d, centerY + halfHeight * 0.70d),
            new(centerX, centerY + halfHeight),
            new(centerX - halfWidth * 0.72d, centerY + halfHeight * 0.70d)
        ];
    }
}
