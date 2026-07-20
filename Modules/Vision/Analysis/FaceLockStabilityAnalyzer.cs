using AvatarBuilder.Modules.Vision.Common;
using System.Windows;

namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceLockStabilityAnalyzer
{
    private static readonly TimeSpan StabilityWindow = TimeSpan.FromSeconds(12);
    private readonly Queue<Sample> _samples = new();

    public void Reset()
    {
        _samples.Clear();
    }

    public FaceLockStabilityAnalysis Update(
        FaceFeatureDetection featureDetection,
        FaceLandmarkFrame frame,
        FaceLandmarkMetrics metrics)
    {
        if (!metrics.HasFace && !frame.HasFace && !featureDetection.HasFace)
        {
            Reset();
            return FaceLockStabilityAnalysis.Waiting;
        }

        var capturedAtUtc = metrics.HasFace
            ? metrics.CapturedAtUtc
            : frame.HasFace ? frame.CapturedAtUtc : DateTime.UtcNow;
        _samples.Enqueue(new Sample(
            capturedAtUtc,
            GetFaceBounds(featureDetection, frame),
            metrics.IsEyeMeasurementUsable,
            metrics.IsMouthMeasurementUsable,
            metrics.EyeMeasurementQualityPercent,
            metrics.MouthMeasurementQualityPercent,
            metrics.OverallMeasurementQualityPercent));

        Trim(capturedAtUtc);
        return CreateAnalysis();
    }

    private void Trim(DateTime capturedAtUtc)
    {
        while (_samples.Count > 0 && capturedAtUtc - _samples.Peek().CapturedAtUtc > StabilityWindow)
        {
            _samples.Dequeue();
        }
    }

    private FaceLockStabilityAnalysis CreateAnalysis()
    {
        if (_samples.Count == 0)
        {
            return FaceLockStabilityAnalysis.Waiting;
        }

        var sampleCount = _samples.Count;
        var faceBoundsCount = 0;
        var eyeUsableCount = 0;
        var mouthUsableCount = 0;
        var eyeQualitySum = 0d;
        var mouthQualitySum = 0d;
        var overallQualitySum = 0d;
        var continuitySum = 0d;
        Rect? previousFaceBounds = null;
        var firstCapturedAtUtc = DateTime.MaxValue;
        var lastCapturedAtUtc = DateTime.MinValue;
        foreach (var sample in _samples)
        {
            firstCapturedAtUtc = firstCapturedAtUtc == DateTime.MaxValue
                ? sample.CapturedAtUtc
                : firstCapturedAtUtc;
            lastCapturedAtUtc = sample.CapturedAtUtc;
            eyeUsableCount += sample.EyeUsable ? 1 : 0;
            mouthUsableCount += sample.MouthUsable ? 1 : 0;
            eyeQualitySum += sample.EyeQualityPercent;
            mouthQualitySum += sample.MouthQualityPercent;
            overallQualitySum += sample.OverallMeasurementQualityPercent;
            if (sample.FaceBounds is not Rect currentFaceBounds)
            {
                continue;
            }

            if (previousFaceBounds is Rect previous)
            {
                continuitySum += CalculatePairContinuity(previous, currentFaceBounds);
            }

            previousFaceBounds = currentFaceBounds;
            faceBoundsCount++;
        }

        var faceBoundsRate = Rate(faceBoundsCount, sampleCount);
        var continuity = faceBoundsCount switch
        {
            0 => 0d,
            1 => 50d,
            _ => Math.Clamp(continuitySum / (faceBoundsCount - 1) * 100d, 0d, 100d)
        };
        var eyeUsableRate = Rate(eyeUsableCount, sampleCount);
        var mouthUsableRate = Rate(mouthUsableCount, sampleCount);
        var eyeQuality = eyeQualitySum / sampleCount;
        var mouthQuality = mouthQualitySum / sampleCount;
        var overallQuality = overallQualitySum / sampleCount;
        var eyeReliability = CalculateFeatureReliability(faceBoundsRate, continuity, eyeUsableRate, eyeQuality);
        var mouthReliability = CalculateFeatureReliability(faceBoundsRate, continuity, mouthUsableRate, mouthQuality);
        var composite = Math.Clamp(
            faceBoundsRate * 0.18d
            + continuity * 0.26d
            + eyeReliability * 0.34d
            + mouthReliability * 0.12d
            + overallQuality * 0.10d,
            0d,
            100d);

        return new FaceLockStabilityAnalysis
        {
            SampleCount = sampleCount,
            WindowSeconds = (lastCapturedAtUtc - firstCapturedAtUtc).TotalSeconds,
            FaceBoundsRatePercent = faceBoundsRate,
            FaceContinuityPercent = continuity,
            EyeUsableRatePercent = eyeUsableRate,
            MouthUsableRatePercent = mouthUsableRate,
            AverageEyeQualityPercent = Math.Clamp(eyeQuality, 0d, 100d),
            AverageMouthQualityPercent = Math.Clamp(mouthQuality, 0d, 100d),
            AverageOverallQualityPercent = Math.Clamp(overallQuality, 0d, 100d),
            EyeReliabilityPercent = eyeReliability,
            MouthReliabilityPercent = mouthReliability,
            CompositeReliabilityPercent = composite
        };
    }

    private static double CalculateFeatureReliability(
        double faceBoundsRate,
        double continuityPercent,
        double usableRate,
        double qualityPercent)
    {
        return Math.Clamp(
            faceBoundsRate * 0.20d
            + continuityPercent * 0.24d
            + usableRate * 0.34d
            + qualityPercent * 0.22d,
            0d,
            100d);
    }

    private static double CalculatePairContinuity(Rect previous, Rect current)
    {
        var previousCenter = Center(previous);
        var currentCenter = Center(current);
        var distance = Distance(previousCenter, currentCenter);
        var previousDiagonal = Diagonal(previous);
        var currentDiagonal = Diagonal(current);
        var referenceDiagonal = Math.Max(0.001d, (previousDiagonal + currentDiagonal) / 2d);
        var proximity = 1d - Math.Clamp(distance / (referenceDiagonal * 1.20d), 0d, 1d);
        var scaleSimilarity = LogSimilarity(
            Math.Max(0.000001d, current.Width * current.Height),
            Math.Max(0.000001d, previous.Width * previous.Height),
            toleranceFactor: 3.5d);
        return proximity * 0.72d + scaleSimilarity * 0.28d;
    }

    private static Rect? GetFaceBounds(FaceFeatureDetection featureDetection, FaceLandmarkFrame frame)
    {
        if (featureDetection.HasFace && featureDetection.FaceBox.Width > 0d && featureDetection.FaceBox.Height > 0d)
        {
            return featureDetection.FaceBox;
        }

        return Bounds(frame.FaceContour);
    }

    private static Rect? Bounds(IReadOnlyList<Point> points)
    {
        if (points.Count == 0)
        {
            return null;
        }

        var first = points[0];
        var minX = first.X;
        var maxX = first.X;
        var minY = first.Y;
        var maxY = first.Y;
        for (var index = 1; index < points.Count; index++)
        {
            var point = points[index];
            minX = Math.Min(minX, point.X);
            maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y);
            maxY = Math.Max(maxY, point.Y);
        }

        return maxX <= minX || maxY <= minY
            ? null
            : new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static Point Center(Rect rect)
    {
        return new Point(rect.Left + rect.Width / 2d, rect.Top + rect.Height / 2d);
    }

    private static double Distance(Point first, Point second)
    {
        var deltaX = first.X - second.X;
        var deltaY = first.Y - second.Y;
        return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
    }

    private static double Diagonal(Rect rect)
    {
        return Math.Sqrt(rect.Width * rect.Width + rect.Height * rect.Height);
    }

    private static double LogSimilarity(double value, double target, double toleranceFactor)
    {
        var distance = Math.Abs(Math.Log(value / target));
        return 1d - Math.Clamp(distance / Math.Log(Math.Max(1.01d, toleranceFactor)), 0d, 1d);
    }

    private static double Rate(int count, int total)
    {
        return total <= 0 ? 0d : count / (double)total * 100d;
    }

    private sealed record Sample(
        DateTime CapturedAtUtc,
        Rect? FaceBounds,
        bool EyeUsable,
        bool MouthUsable,
        double EyeQualityPercent,
        double MouthQualityPercent,
        double OverallMeasurementQualityPercent);
}
