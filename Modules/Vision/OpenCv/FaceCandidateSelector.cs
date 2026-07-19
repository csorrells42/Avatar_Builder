using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed record FaceCandidate(
    CvRect Face,
    string Source,
    YuNetFaceDetection? YuNetFace = null,
    double DetectorScore = 0.5d);

public static class FaceCandidateSelector
{
    public static FaceCandidate? SelectBest(
        IEnumerable<FaceCandidate> candidates,
        CvRect? previousFace,
        int frameWidth,
        int frameHeight)
    {
        return candidates
            .Where(candidate => IsPlausibleFace(candidate.Face, frameWidth, frameHeight))
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = ScoreCandidate(candidate.Face, previousFace, frameWidth, frameHeight, candidate.DetectorScore)
            })
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault()
            ?.Candidate;
    }

    public static double ScoreCandidate(
        CvRect face,
        CvRect? previousFace,
        int frameWidth,
        int frameHeight,
        double detectorScore)
    {
        var frameArea = Math.Max(1d, frameWidth * frameHeight);
        var faceArea = Math.Max(1d, face.Width * face.Height);
        var areaRatio = faceArea / frameArea;
        var detector = Math.Clamp(detectorScore, 0d, 1d);
        var aspect = face.Width / (double)Math.Max(1, face.Height);
        var aspectScore = 1d - Math.Clamp(Math.Abs(aspect - 0.86d) / 0.70d, 0d, 1d);
        var centerX = face.X + face.Width / 2d;
        var centerY = face.Y + face.Height / 2d;
        var frameCenterX = frameWidth / 2d;
        var frameCenterY = frameHeight / 2d;
        var frameDiagonal = Math.Sqrt(frameWidth * frameWidth + frameHeight * frameHeight);
        var centrality = 1d - Math.Clamp(
            Math.Sqrt(Math.Pow(centerX - frameCenterX, 2d) + Math.Pow(centerY - frameCenterY, 2d)) / Math.Max(1d, frameDiagonal * 0.72d),
            0d,
            1d);

        var targetArea = previousFace is CvRect previous
            ? Math.Max(1d, previous.Width * previous.Height) / frameArea
            : 0.12d;
        var sizeScore = LogSimilarity(areaRatio, targetArea, toleranceFactor: 6d);

        var score = detector * 0.45d
            + sizeScore * 0.25d
            + aspectScore * 0.15d
            + centrality * 0.05d;

        if (previousFace is CvRect last)
        {
            var overlap = OverlapOverSmaller(face, last);
            var proximity = CenterProximity(face, last);
            var scaleSimilarity = LogSimilarity(faceArea, Math.Max(1d, last.Width * last.Height), toleranceFactor: 4d);
            score += overlap * 0.80d
                + proximity * 0.35d
                + scaleSimilarity * 0.20d;

            if (overlap >= 0.18d)
            {
                score += 0.45d;
            }
        }

        return score;
    }

    public static bool IsContinuousWithPrevious(CvRect face, CvRect? previousFace)
    {
        if (previousFace is not CvRect previous)
        {
            return true;
        }

        if (face.Width <= 0 || face.Height <= 0 || previous.Width <= 0 || previous.Height <= 0)
        {
            return false;
        }

        var overlap = OverlapOverSmaller(face, previous);
        if (overlap >= 0.08d)
        {
            return true;
        }

        var proximity = CenterProximity(face, previous);
        var scaleSimilarity = LogSimilarity(
            Math.Max(1d, face.Width * face.Height),
            Math.Max(1d, previous.Width * previous.Height),
            toleranceFactor: 5d);
        return proximity >= 0.48d && scaleSimilarity >= 0.20d;
    }

    public static bool IsAcceptableTrackingCandidate(
        FaceCandidate candidate,
        CvRect? previousFace,
        int frameWidth,
        int frameHeight,
        int missedFrames)
    {
        if (previousFace is null || IsContinuousWithPrevious(candidate.Face, previousFace))
        {
            return true;
        }

        return IsStrongGlobalReacquireCandidate(candidate, previousFace.Value, frameWidth, frameHeight, missedFrames);
    }

    public static bool IsStrongGlobalReacquireCandidate(
        FaceCandidate candidate,
        CvRect previousFace,
        int frameWidth,
        int frameHeight,
        int missedFrames)
    {
        if (missedFrames < 2 || !IsPlausibleFace(candidate.Face, frameWidth, frameHeight))
        {
            return false;
        }

        var detector = Math.Clamp(candidate.DetectorScore, 0d, 1d);
        if (detector < 0.90d)
        {
            return false;
        }

        var face = candidate.Face;
        var frameArea = Math.Max(1d, frameWidth * frameHeight);
        var faceArea = Math.Max(1d, face.Width * face.Height);
        var areaRatio = faceArea / frameArea;
        if (areaRatio < 0.012d)
        {
            return false;
        }

        var aspect = face.Width / (double)Math.Max(1, face.Height);
        var aspectScore = 1d - Math.Clamp(Math.Abs(aspect - 0.86d) / 0.70d, 0d, 1d);
        var centerX = face.X + face.Width / 2d;
        var centerY = face.Y + face.Height / 2d;
        var frameDiagonal = Math.Sqrt(frameWidth * frameWidth + frameHeight * frameHeight);
        var centrality = 1d - Math.Clamp(
            Math.Sqrt(Math.Pow(centerX - frameWidth / 2d, 2d) + Math.Pow(centerY - frameHeight / 2d, 2d)) / Math.Max(1d, frameDiagonal * 0.72d),
            0d,
            1d);
        var sizeSimilarity = LogSimilarity(
            faceArea,
            Math.Max(1d, previousFace.Width * previousFace.Height),
            toleranceFactor: 7d);
        if (sizeSimilarity < 0.12d)
        {
            return false;
        }

        var missedFrameConfidence = Math.Clamp(missedFrames / 6d, 0d, 1d);
        var reacquireScore = detector * 0.54d
            + aspectScore * 0.18d
            + sizeSimilarity * 0.16d
            + centrality * 0.08d
            + missedFrameConfidence * 0.04d;

        return reacquireScore >= 0.62d;
    }

    private static bool IsPlausibleFace(CvRect face, int frameWidth, int frameHeight)
    {
        if (face.Width <= 0 || face.Height <= 0)
        {
            return false;
        }

        var minimumDimension = Math.Max(18d, Math.Min(frameWidth, frameHeight) / 80d);
        if (face.Width < minimumDimension || face.Height < minimumDimension)
        {
            return false;
        }

        var aspect = face.Width / (double)Math.Max(1, face.Height);
        var areaRatio = face.Width * face.Height / (double)Math.Max(1, frameWidth * frameHeight);
        return aspect is > 0.45d and < 1.75d
            && areaRatio is > 0.0015d and < 0.92d;
    }

    private static double CenterProximity(CvRect current, CvRect previous)
    {
        var currentCenterX = current.X + current.Width / 2d;
        var currentCenterY = current.Y + current.Height / 2d;
        var previousCenterX = previous.X + previous.Width / 2d;
        var previousCenterY = previous.Y + previous.Height / 2d;
        var distance = Math.Sqrt(Math.Pow(currentCenterX - previousCenterX, 2d) + Math.Pow(currentCenterY - previousCenterY, 2d));
        var currentDiagonal = Math.Sqrt(current.Width * current.Width + current.Height * current.Height);
        var previousDiagonal = Math.Sqrt(previous.Width * previous.Width + previous.Height * previous.Height);
        return 1d - Math.Clamp(distance / Math.Max(1d, Math.Max(currentDiagonal, previousDiagonal) * 1.25d), 0d, 1d);
    }

    private static double OverlapOverSmaller(CvRect first, CvRect second)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        var width = Math.Max(0, right - left);
        var height = Math.Max(0, bottom - top);
        var intersection = width * height;
        var smaller = Math.Min(first.Width * first.Height, second.Width * second.Height);
        return smaller <= 0 ? 0d : intersection / (double)smaller;
    }

    private static double LogSimilarity(double value, double target, double toleranceFactor)
    {
        if (value <= 0d || target <= 0d)
        {
            return 0d;
        }

        var distance = Math.Abs(Math.Log(value / target));
        return 1d - Math.Clamp(distance / Math.Log(Math.Max(1.01d, toleranceFactor)), 0d, 1d);
    }
}
