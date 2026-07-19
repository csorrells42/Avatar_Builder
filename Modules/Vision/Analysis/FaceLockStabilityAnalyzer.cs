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

        var samples = _samples.ToList();
        var faceBoundsSamples = samples.Where(static sample => sample.FaceBounds.HasValue).ToList();
        var faceBoundsRate = Rate(faceBoundsSamples.Count, samples.Count);
        var continuity = CalculateContinuityPercent(faceBoundsSamples);
        var eyeUsableRate = Rate(samples.Count(static sample => sample.EyeUsable), samples.Count);
        var mouthUsableRate = Rate(samples.Count(static sample => sample.MouthUsable), samples.Count);
        var eyeQuality = samples.Average(static sample => sample.EyeQualityPercent);
        var mouthQuality = samples.Average(static sample => sample.MouthQualityPercent);
        var overallQuality = samples.Average(static sample => sample.OverallMeasurementQualityPercent);
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
            SampleCount = samples.Count,
            WindowSeconds = (samples[^1].CapturedAtUtc - samples[0].CapturedAtUtc).TotalSeconds,
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

    private static double CalculateContinuityPercent(IReadOnlyList<Sample> samples)
    {
        if (samples.Count < 2)
        {
            return samples.Count == 1 ? 50d : 0d;
        }

        var scores = new List<double>();
        for (var index = 1; index < samples.Count; index++)
        {
            var previous = samples[index - 1].FaceBounds!.Value;
            var current = samples[index].FaceBounds!.Value;
            scores.Add(CalculatePairContinuity(previous, current));
        }

        return Math.Clamp(scores.Average() * 100d, 0d, 100d);
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

        var minX = points.Min(static point => point.X);
        var maxX = points.Max(static point => point.X);
        var minY = points.Min(static point => point.Y);
        var maxY = points.Max(static point => point.Y);
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
        return Math.Sqrt(Math.Pow(first.X - second.X, 2d) + Math.Pow(first.Y - second.Y, 2d));
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
