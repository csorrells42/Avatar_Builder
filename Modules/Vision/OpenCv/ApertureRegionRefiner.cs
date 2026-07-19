using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public static class ApertureRegionRefiner
{
    public static ApertureRegionRefinement RefineEye(Mat gray, CvRect face, CvRect seed, bool? leftSide = null)
    {
        return Refine(
            gray,
            face,
            seed,
            isEye: true,
            leftSide,
            static (image, box) => OpenCvApertureEstimator.EstimateEye(image, box));
    }

    public static ApertureRegionRefinement RefineMouth(Mat gray, CvRect face, CvRect seed)
    {
        return Refine(
            gray,
            face,
            seed,
            isEye: false,
            leftSide: null,
            static (image, box) => OpenCvApertureEstimator.EstimateMouth(image, box));
    }

    private static ApertureRegionRefinement Refine(
        Mat gray,
        CvRect face,
        CvRect seed,
        bool isEye,
        bool? leftSide,
        Func<Mat, CvRect, ApertureEstimate> estimator)
    {
        var frameBounds = new CvRect(0, 0, gray.Width, gray.Height);
        var clampedSeed = ClampRect(seed, frameBounds);
        if (clampedSeed.Width <= 0 || clampedSeed.Height <= 0)
        {
            return new ApertureRegionRefinement(default, ApertureEstimate.None, 0d);
        }

        var bestBox = clampedSeed;
        var bestEstimate = estimator(gray, clampedSeed);
        var bestScore = ScoreEstimate(bestEstimate, clampedSeed, clampedSeed, face, isEye, leftSide);
        foreach (var candidate in CreateCandidates(clampedSeed, face, frameBounds, isEye))
        {
            if (!IsPlausibleCandidate(candidate, face, isEye, leftSide))
            {
                continue;
            }

            var estimate = estimator(gray, candidate);
            var score = ScoreEstimate(estimate, candidate, clampedSeed, face, isEye, leftSide);
            var improvementMargin = isEye ? 0.030d : 0.020d;
            if (score > bestScore + improvementMargin)
            {
                bestScore = score;
                bestBox = candidate;
                bestEstimate = estimate;
            }
        }

        return new ApertureRegionRefinement(bestBox, bestEstimate, bestScore);
    }

    private static IEnumerable<CvRect> CreateCandidates(CvRect seed, CvRect face, CvRect frameBounds, bool isEye)
    {
        yield return seed;

        var horizontalStep = Math.Max(2d, face.Width * (isEye ? 0.055d : 0.045d));
        var verticalStep = Math.Max(2d, face.Height * (isEye ? 0.075d : 0.065d));
        var offsets = isEye
            ? new (double X, double Y)[]
            {
                (-horizontalStep, 0d),
                (horizontalStep, 0d),
                (0d, -verticalStep),
                (0d, verticalStep),
                (-horizontalStep, -verticalStep),
                (horizontalStep, -verticalStep),
                (-horizontalStep, verticalStep),
                (horizontalStep, verticalStep)
            }
            : new (double X, double Y)[]
            {
                (-horizontalStep, 0d),
                (horizontalStep, 0d),
                (0d, -verticalStep),
                (0d, verticalStep),
                (0d, verticalStep * 1.75d),
                (-horizontalStep, verticalStep),
                (horizontalStep, verticalStep)
            };

        foreach (var offset in offsets)
        {
            yield return ClampRect(Transform(seed, offset.X, offset.Y, 1d, 1d), frameBounds);
        }

        if (!isEye)
        {
            foreach (var anatomicalCandidate in CreateAnatomicalMouthCandidates(seed, face, frameBounds))
            {
                yield return anatomicalCandidate;
            }
        }

        var scaleCandidates = isEye
            ? new (double ScaleX, double ScaleY)[] { (0.92d, 0.86d), (1.10d, 1.08d), (1.18d, 0.92d) }
            : new (double ScaleX, double ScaleY)[] { (0.92d, 0.90d), (1.12d, 1.12d), (1.18d, 0.95d) };
        foreach (var scale in scaleCandidates)
        {
            yield return ClampRect(Transform(seed, 0d, 0d, scale.ScaleX, scale.ScaleY), frameBounds);
        }
    }

    private static IEnumerable<CvRect> CreateAnatomicalMouthCandidates(CvRect seed, CvRect face, CvRect frameBounds)
    {
        var seedCenterX = seed.X + seed.Width / 2d;
        var faceCenterX = face.X + face.Width / 2d;
        var centerX = seedCenterX * 0.45d + faceCenterX * 0.55d;
        var centerY = face.Y + face.Height * 0.69d;
        var width = Math.Max(seed.Width, (int)Math.Round(face.Width * 0.48d));
        var height = Math.Max(seed.Height, (int)Math.Round(face.Height * 0.20d));
        var expected = new CvRect(
            (int)Math.Round(centerX - width / 2d),
            (int)Math.Round(centerY - height / 2d),
            Math.Max(1, width),
            Math.Max(1, height));

        yield return ClampRect(expected, frameBounds);
        yield return ClampRect(Transform(expected, 0d, face.Height * 0.045d, 1.10d, 1.05d), frameBounds);
        yield return ClampRect(Transform(expected, 0d, -face.Height * 0.035d, 0.94d, 0.92d), frameBounds);
    }

    private static double ScoreEstimate(
        ApertureEstimate estimate,
        CvRect candidate,
        CvRect seed,
        CvRect face,
        bool isEye,
        bool? leftSide)
    {
        var candidatePositionScore = ScoreExpectedPosition(candidate, face, isEye, leftSide);
        var positionScore = candidatePositionScore;
        var seedProximity = ScoreCenterProximity(candidate, seed);
        if (!estimate.HasAperture)
        {
            var diagnosticScore = Math.Clamp((estimate.ContrastScore + estimate.SharpnessScore) / 2d, 0d, 1d) * 0.04d;
            return diagnosticScore + positionScore * 0.06d + seedProximity * 0.04d;
        }

        if (!isEye)
        {
            var aperturePositionScore = ScoreExpectedAperturePosition(estimate, face);
            positionScore = candidatePositionScore * 0.30d + aperturePositionScore * 0.70d;
        }

        var openingRatio = estimate.AverageOpeningRatio
            ?? estimate.ApertureBox.Height / (double)Math.Max(1, estimate.ApertureBox.Width);
        var openingScore = ScoreOpeningPlausibility(openingRatio, isEye);
        var profileScore = Math.Clamp(estimate.ProfileCoverageRatio / (isEye ? 0.36d : 0.42d), 0d, 1d);
        var sampleScore = Math.Clamp(estimate.ProfileSampleCount / (double)Math.Max(8, candidate.Width * 0.22d), 0d, 1d);
        var darkCoverageScore = Math.Clamp(estimate.DarkCoverageRatio / (isEye ? 0.30d : 0.36d), 0d, 1d);
        var qualityScore = Math.Clamp(
            estimate.ContrastScore * 0.45d
            + estimate.SharpnessScore * 0.35d
            + (1d - Math.Clamp(estimate.GlareRatio / 0.20d, 0d, 1d)) * 0.20d,
            0d,
            1d);

        var rawScore = estimate.Confidence * 0.42d
            + profileScore * 0.16d
            + sampleScore * 0.12d
            + positionScore * 0.10d
            + qualityScore * 0.08d
            + openingScore * 0.06d
            + seedProximity * 0.04d
            + darkCoverageScore * 0.02d;
        return isEye ? rawScore : rawScore * ScoreMouthApertureBand(estimate, face);
    }

    private static bool IsPlausibleCandidate(CvRect candidate, CvRect face, bool isEye, bool? leftSide)
    {
        if (candidate.Width < 6 || candidate.Height < 4)
        {
            return false;
        }

        var centerX = candidate.X + candidate.Width / 2d;
        var centerY = candidate.Y + candidate.Height / 2d;
        var faceLeft = face.X;
        var faceTop = face.Y;
        var faceRight = face.Right;
        var normalizedX = (centerX - faceLeft) / Math.Max(1d, face.Width);
        var normalizedY = (centerY - faceTop) / Math.Max(1d, face.Height);

        if (centerX < faceLeft - face.Width * 0.12d
            || centerX > faceRight + face.Width * 0.12d
            || centerY < faceTop - face.Height * 0.08d
            || centerY > face.Bottom + face.Height * 0.08d)
        {
            return false;
        }

        if (isEye)
        {
            if (normalizedY is < 0.18d or > 0.58d)
            {
                return false;
            }

            if (leftSide == true && normalizedX > 0.58d)
            {
                return false;
            }

            if (leftSide == false && normalizedX < 0.42d)
            {
                return false;
            }

            return candidate.Width <= face.Width * 0.46d
                && candidate.Height <= face.Height * 0.30d;
        }

        return normalizedY is > 0.46d and < 0.86d
            && candidate.Width <= face.Width * 0.76d
            && candidate.Height <= face.Height * 0.34d;
    }

    private static double ScoreExpectedPosition(CvRect candidate, CvRect face, bool isEye, bool? leftSide)
    {
        var centerX = candidate.X + candidate.Width / 2d;
        var centerY = candidate.Y + candidate.Height / 2d;
        var targetX = isEye
            ? face.X + face.Width * (leftSide == false ? 0.67d : leftSide == true ? 0.33d : 0.50d)
            : face.X + face.Width * 0.50d;
        var targetY = isEye ? face.Y + face.Height * 0.38d : face.Y + face.Height * 0.68d;
        var toleranceX = Math.Max(1d, face.Width * (isEye && leftSide is not null ? 0.22d : 0.34d));
        var toleranceY = Math.Max(1d, face.Height * (isEye ? 0.16d : 0.18d));
        var xScore = 1d - Math.Clamp(Math.Abs(centerX - targetX) / toleranceX, 0d, 1d);
        var yScore = 1d - Math.Clamp(Math.Abs(centerY - targetY) / toleranceY, 0d, 1d);
        return xScore * 0.45d + yScore * 0.55d;
    }

    private static double ScoreExpectedAperturePosition(ApertureEstimate estimate, CvRect face)
    {
        if (!estimate.HasAperture || estimate.ApertureBox.Width <= 0 || estimate.ApertureBox.Height <= 0)
        {
            return 0d;
        }

        var centerX = estimate.ApertureBox.X + estimate.ApertureBox.Width / 2d;
        var centerY = estimate.ApertureBox.Y + estimate.ApertureBox.Height / 2d;
        var targetX = face.X + face.Width * 0.50d;
        var targetY = face.Y + face.Height * 0.69d;
        var xScore = 1d - Math.Clamp(Math.Abs(centerX - targetX) / Math.Max(1d, face.Width * 0.38d), 0d, 1d);
        var yScore = 1d - Math.Clamp(Math.Abs(centerY - targetY) / Math.Max(1d, face.Height * 0.17d), 0d, 1d);
        return xScore * 0.30d + yScore * 0.70d;
    }

    private static double ScoreMouthApertureBand(ApertureEstimate estimate, CvRect face)
    {
        if (!estimate.HasAperture || estimate.ApertureBox.Width <= 0 || estimate.ApertureBox.Height <= 0)
        {
            return 1d;
        }

        var centerY = estimate.ApertureBox.Y + estimate.ApertureBox.Height / 2d;
        var normalizedY = (centerY - face.Y) / Math.Max(1d, face.Height);
        if (normalizedY < 0.50d)
        {
            return 0.38d;
        }

        if (normalizedY < 0.58d)
        {
            return 0.38d + (normalizedY - 0.50d) / 0.08d * 0.34d;
        }

        if (normalizedY <= 0.82d)
        {
            return 1d;
        }

        if (normalizedY <= 0.90d)
        {
            return 1d - (normalizedY - 0.82d) / 0.08d * 0.18d;
        }

        return 0.72d;
    }

    private static double ScoreOpeningPlausibility(double openingRatio, bool isEye)
    {
        if (openingRatio <= 0d)
        {
            return 0d;
        }

        if (isEye)
        {
            if (openingRatio <= 0.40d)
            {
                return 1d;
            }

            if (openingRatio <= 0.62d)
            {
                return 1d - Math.Clamp((openingRatio - 0.40d) / 0.22d, 0d, 1d) * 0.72d;
            }

            return 0.28d - Math.Clamp((openingRatio - 0.62d) / 0.38d, 0d, 1d) * 0.24d;
        }

        if (openingRatio <= 0.92d)
        {
            return 1d;
        }

        return 1d - Math.Clamp((openingRatio - 0.92d) / 0.92d, 0d, 1d);
    }

    private static double ScoreCenterProximity(CvRect first, CvRect second)
    {
        var firstCenterX = first.X + first.Width / 2d;
        var firstCenterY = first.Y + first.Height / 2d;
        var secondCenterX = second.X + second.Width / 2d;
        var secondCenterY = second.Y + second.Height / 2d;
        var distance = Math.Sqrt(Math.Pow(firstCenterX - secondCenterX, 2d) + Math.Pow(firstCenterY - secondCenterY, 2d));
        var diagonal = Math.Sqrt(second.Width * second.Width + second.Height * second.Height);
        return 1d - Math.Clamp(distance / Math.Max(1d, diagonal * 0.65d), 0d, 1d);
    }

    private static CvRect Transform(CvRect rect, double offsetX, double offsetY, double scaleX, double scaleY)
    {
        var centerX = rect.X + rect.Width / 2d + offsetX;
        var centerY = rect.Y + rect.Height / 2d + offsetY;
        var width = Math.Max(1d, rect.Width * scaleX);
        var height = Math.Max(1d, rect.Height * scaleY);
        return new CvRect(
            (int)Math.Round(centerX - width / 2d),
            (int)Math.Round(centerY - height / 2d),
            Math.Max(1, (int)Math.Round(width)),
            Math.Max(1, (int)Math.Round(height)));
    }

    private static CvRect ClampRect(CvRect rect, CvRect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return default;
        }

        var x = Math.Clamp(rect.X, bounds.X, Math.Max(bounds.X, bounds.Right - 1));
        var y = Math.Clamp(rect.Y, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - 1));
        var right = Math.Clamp(rect.Right, x + 1, bounds.Right);
        var bottom = Math.Clamp(rect.Bottom, y + 1, bounds.Bottom);
        return new CvRect(x, y, right - x, bottom - y);
    }
}

public sealed record ApertureRegionRefinement(CvRect Box, ApertureEstimate Estimate, double Score);
