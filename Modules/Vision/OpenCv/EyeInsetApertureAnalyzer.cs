using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public static class EyeInsetApertureAnalyzer
{
    private const double MaximumPlausibleOpeningRatio = 0.95d;
    private const double MaximumRetainedOpeningRatio = 1.05d;
    private const double MinimumAutoCandidateScore = 45d;
    private const double MinimumAutoCandidateSharpnessPercent = 55d;
    private const double MinimumAutoCandidateContrastPercent = 70d;
    private const double MinimumAutoCandidateConfidence = 0.20d;

    public static EyeInsetRegion BottomRightDefaultRegion { get; } = new("bottom-right", 0.62d, 0.56d, 0.36d, 0.40d);

    public static EyeInsetRegion AutoSearchRegion { get; } = new("auto", 0d, 0d, 0d, 0d);

    public static EyeInsetRegion? SelectBestRegion(Mat frame)
    {
        if (frame.Empty())
        {
            return null;
        }

        EyeInsetRegion? bestRegion = null;
        var bestScore = 0d;
        foreach (var candidate in CreateAutoCandidateRegions())
        {
            var analysis = Analyze(frame, candidate);
            var score = ScoreAutoCandidate(analysis);
            if (score > bestScore)
            {
                bestRegion = candidate;
                bestScore = score;
            }
        }

        return bestScore >= MinimumAutoCandidateScore ? bestRegion : null;
    }

    public static EyeInsetRegion? SelectBestRegion(IEnumerable<Mat> frames)
    {
        var candidates = CreateAutoCandidateRegions().ToList();
        var scores = candidates.ToDictionary(static candidate => candidate, static _ => 0d);
        var measuredFrames = candidates.ToDictionary(static candidate => candidate, static _ => 0);
        var frameCount = 0;
        foreach (var frame in frames)
        {
            if (frame.Empty())
            {
                continue;
            }

            frameCount++;
            foreach (var candidate in candidates)
            {
                var analysis = Analyze(frame, candidate);
                scores[candidate] += ScoreAutoCandidate(analysis);
                if (analysis.HasMeasurement)
                {
                    measuredFrames[candidate]++;
                }
            }
        }

        if (frameCount == 0)
        {
            return null;
        }

        EyeInsetRegion? bestRegion = null;
        var bestScore = 0d;
        foreach (var candidate in candidates)
        {
            var measuredRate = measuredFrames[candidate] / (double)frameCount;
            var score = scores[candidate] / frameCount + measuredRate * 25d;
            if (score > bestScore)
            {
                bestRegion = candidate;
                bestScore = score;
            }
        }

        return bestScore >= MinimumAutoCandidateScore ? bestRegion : null;
    }

    public static EyeInsetApertureAnalysis AnalyzeBest(Mat frame)
    {
        if (frame.Empty())
        {
            return EyeInsetApertureAnalysis.None("auto");
        }

        var bestRegion = SelectBestRegion(frame);
        return bestRegion is null
            ? EyeInsetApertureAnalysis.None("auto")
            : Analyze(frame, bestRegion);
    }

    public static EyeInsetApertureAnalysis Analyze(Mat frame, EyeInsetRegion region)
    {
        if (frame.Empty())
        {
            return EyeInsetApertureAnalysis.None(region.Label);
        }

        using var gray = CreateGrayFrame(frame);
        var inset = region.ToPixelRect(gray.Width, gray.Height);
        if (inset.Width < 32 || inset.Height < 20)
        {
            return EyeInsetApertureAnalysis.None(region.Label);
        }

        var eyeTop = inset.Y + Math.Max(0, (int)Math.Round(inset.Height * 0.10d));
        var eyeHeight = Math.Max(8, (int)Math.Round(inset.Height * 0.78d));
        var horizontalPadding = Math.Max(2, (int)Math.Round(inset.Width * 0.035d));
        var centerGap = Math.Max(2, (int)Math.Round(inset.Width * 0.025d));
        var halfWidth = Math.Max(10, (inset.Width - horizontalPadding * 2 - centerGap) / 2);

        var leftBox = ClampRect(
            new CvRect(inset.X + horizontalPadding, eyeTop, halfWidth, eyeHeight),
            gray.Width,
            gray.Height);
        var rightBox = ClampRect(
            new CvRect(inset.X + horizontalPadding + halfWidth + centerGap, eyeTop, halfWidth, eyeHeight),
            gray.Width,
            gray.Height);

        var left = OpenCvApertureEstimator.EstimateEye(gray, leftBox);
        var right = OpenCvApertureEstimator.EstimateEye(gray, rightBox);
        var leftRatio = CalculateOpeningRatio(left);
        var rightRatio = CalculateOpeningRatio(right);
        var averageRatio = Average(leftRatio, rightRatio);
        var confidence = CalculateConfidence(left, right, leftRatio, rightRatio);
        var imageQualityAvailable = HasImageDiagnostics(left) || HasImageDiagnostics(right);
        var glarePercent = AverageDiagnostic(left, right, static estimate => estimate.GlareRatio * 100d);
        var contrastPercent = AverageDiagnostic(left, right, static estimate => estimate.ContrastScore * 100d);
        var sharpnessPercent = AverageDiagnostic(left, right, static estimate => estimate.SharpnessScore * 100d);
        var darkCoveragePercent = AverageDiagnostic(left, right, static estimate => estimate.DarkCoverageRatio * 100d);
        var measuredEyes = (leftRatio.HasValue ? 1 : 0) + (rightRatio.HasValue ? 1 : 0);
        var status = measuredEyes switch
        {
            2 => $"eye inset lock {confidence * 100d:0}%",
            1 => $"eye inset partial {confidence * 100d:0}%",
            _ => "eye inset searching"
        };

        return new EyeInsetApertureAnalysis(
            region.Label,
            true,
            measuredEyes > 0,
            inset.X / (double)gray.Width,
            inset.Y / (double)gray.Height,
            inset.Width / (double)gray.Width,
            inset.Height / (double)gray.Height,
            leftRatio,
            rightRatio,
            averageRatio,
            left.Confidence,
            right.Confidence,
            confidence,
            imageQualityAvailable,
            glarePercent,
            contrastPercent,
            sharpnessPercent,
            darkCoveragePercent,
            status);
    }

    private static Mat CreateGrayFrame(Mat frame)
    {
        var gray = new Mat();
        if (frame.Channels() == 1)
        {
            frame.CopyTo(gray);
        }
        else if (frame.Channels() == 3)
        {
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        }
        else if (frame.Channels() == 4)
        {
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGRA2GRAY);
        }
        else
        {
            throw new InvalidOperationException($"Unsupported frame channel count: {frame.Channels()}");
        }

        return gray;
    }

    private static double? CalculateOpeningRatio(ApertureEstimate estimate)
    {
        if (!estimate.HasAperture)
        {
            return null;
        }

        if (estimate.AverageOpeningRatio is double averageOpeningRatio)
        {
            return NormalizeOpeningRatio(averageOpeningRatio);
        }

        if (estimate.ApertureBox.Width <= 0 || estimate.ApertureBox.Height <= 0)
        {
            return null;
        }

        return NormalizeOpeningRatio(estimate.ApertureBox.Height / (double)estimate.ApertureBox.Width);
    }

    private static double? NormalizeOpeningRatio(double openingRatio)
    {
        if (double.IsNaN(openingRatio) || double.IsInfinity(openingRatio) || openingRatio < 0d)
        {
            return null;
        }

        if (openingRatio > MaximumRetainedOpeningRatio)
        {
            return null;
        }

        return Math.Clamp(openingRatio, 0d, MaximumPlausibleOpeningRatio);
    }

    private static double? Average(double? first, double? second)
    {
        if (first is double left && second is double right)
        {
            return (left + right) / 2d;
        }

        return first ?? second;
    }

    private static double CalculateConfidence(
        ApertureEstimate left,
        ApertureEstimate right,
        double? leftOpeningRatio,
        double? rightOpeningRatio)
    {
        var leftUsable = left.HasAperture && leftOpeningRatio.HasValue;
        var rightUsable = right.HasAperture && rightOpeningRatio.HasValue;
        if (leftUsable && rightUsable)
        {
            return Math.Clamp((left.Confidence + right.Confidence) / 2d, 0d, 1d)
                * OpeningPlausibilityMultiplier(leftOpeningRatio, rightOpeningRatio);
        }

        if (leftUsable || rightUsable)
        {
            return Math.Clamp(Math.Max(left.Confidence, right.Confidence) * 0.55d, 0d, 1d)
                * OpeningPlausibilityMultiplier(leftOpeningRatio, rightOpeningRatio);
        }

        return 0d;
    }

    private static double OpeningPlausibilityMultiplier(double? leftOpeningRatio, double? rightOpeningRatio)
    {
        var ratios = new[] { leftOpeningRatio, rightOpeningRatio }
            .OfType<double>()
            .ToList();
        if (ratios.Count == 0)
        {
            return 0d;
        }

        var largest = ratios.Max();
        if (largest <= 0.75d)
        {
            return 1d;
        }

        return Math.Clamp(1d - (largest - 0.75d) / (MaximumPlausibleOpeningRatio - 0.75d) * 0.35d, 0.55d, 1d);
    }

    private static bool HasImageDiagnostics(ApertureEstimate estimate)
    {
        return estimate.GlareRatio > 0d
            || estimate.ContrastScore > 0d
            || estimate.SharpnessScore > 0d
            || estimate.DarkCoverageRatio > 0d;
    }

    private static double AverageDiagnostic(
        ApertureEstimate left,
        ApertureEstimate right,
        Func<ApertureEstimate, double> selector)
    {
        var count = 0;
        var total = 0d;
        if (HasImageDiagnostics(left))
        {
            total += selector(left);
            count++;
        }

        if (HasImageDiagnostics(right))
        {
            total += selector(right);
            count++;
        }

        return count == 0 ? 0d : total / count;
    }

    private static IEnumerable<EyeInsetRegion> CreateAutoCandidateRegions()
    {
        yield return BottomRightDefaultRegion with { Label = "auto:bottom-right" };
        yield return new EyeInsetRegion("auto:bottom-right-large", 0.56d, 0.48d, 0.42d, 0.50d);
        yield return new EyeInsetRegion("auto:right-middle", 0.60d, 0.30d, 0.38d, 0.44d);
        yield return new EyeInsetRegion("auto:top-right", 0.60d, 0.02d, 0.38d, 0.42d);
        yield return new EyeInsetRegion("auto:bottom-left", 0.02d, 0.56d, 0.36d, 0.40d);
        yield return new EyeInsetRegion("auto:top-left", 0.02d, 0.02d, 0.36d, 0.42d);

        foreach (var width in new[] { 0.30d, 0.36d, 0.42d })
        {
            foreach (var height in new[] { 0.30d, 0.38d, 0.46d })
            {
                yield return new EyeInsetRegion(
                    $"auto:grid-br-{width:0.00}x{height:0.00}",
                    0.98d - width,
                    0.98d - height,
                    width,
                    height);
            }
        }
    }

    private static double ScoreAutoCandidate(EyeInsetApertureAnalysis analysis)
    {
        if (!analysis.HasMeasurement)
        {
            return 0d;
        }

        if (analysis.MeasurementConfidence < MinimumAutoCandidateConfidence
            || analysis.ContrastPercent < MinimumAutoCandidateContrastPercent
            || analysis.SharpnessPercent < MinimumAutoCandidateSharpnessPercent)
        {
            return 0d;
        }

        var bothEyes = analysis.LeftEyeOpeningRatio.HasValue && analysis.RightEyeOpeningRatio.HasValue ? 15d : 0d;
        var quality = Math.Clamp(analysis.ContrastPercent, 0d, 100d) * 0.14d
            + Math.Clamp(analysis.SharpnessPercent, 0d, 100d) * 0.10d
            + Math.Clamp(analysis.DarkCoveragePercent, 0d, 100d) * 0.18d;
        var defaultInsetPrior = analysis.RegionLabel.Contains("bottom-right", StringComparison.OrdinalIgnoreCase) ? 4d : 0d;
        return Math.Clamp(analysis.MeasurementConfidence, 0d, 1d) * 70d
            + bothEyes
            + quality
            + defaultInsetPrior;
    }

    private static CvRect ClampRect(CvRect rect, int width, int height)
    {
        var x = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
        var right = Math.Clamp(rect.Right, x + 1, width);
        var bottom = Math.Clamp(rect.Bottom, y + 1, height);
        return new CvRect(x, y, right - x, bottom - y);
    }
}

public sealed record EyeInsetRegion(
    string Label,
    double Left,
    double Top,
    double Width,
    double Height)
{
    public CvRect ToPixelRect(int frameWidth, int frameHeight)
    {
        var left = Math.Clamp(Left, 0d, 0.98d);
        var top = Math.Clamp(Top, 0d, 0.98d);
        var width = Math.Clamp(Width, 0.01d, 1d - left);
        var height = Math.Clamp(Height, 0.01d, 1d - top);
        return new CvRect(
            (int)Math.Round(left * frameWidth),
            (int)Math.Round(top * frameHeight),
            Math.Max(1, (int)Math.Round(width * frameWidth)),
            Math.Max(1, (int)Math.Round(height * frameHeight)));
    }
}

public sealed record EyeInsetApertureAnalysis(
    string RegionLabel,
    bool HasRegion,
    bool HasMeasurement,
    double RegionLeft,
    double RegionTop,
    double RegionWidth,
    double RegionHeight,
    double? LeftEyeOpeningRatio,
    double? RightEyeOpeningRatio,
    double? AverageEyeOpeningRatio,
    double LeftEyeConfidence,
    double RightEyeConfidence,
    double MeasurementConfidence,
    bool ImageQualityAvailable,
    double GlarePercent,
    double ContrastPercent,
    double SharpnessPercent,
    double DarkCoveragePercent,
    string Status)
{
    public static EyeInsetApertureAnalysis None(string label) => new(
        label,
        false,
        false,
        0d,
        0d,
        0d,
        0d,
        null,
        null,
        null,
        0d,
        0d,
        0d,
        false,
        0d,
        0d,
        0d,
        0d,
        "eye inset unavailable");
}
