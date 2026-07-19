using AvatarBuilder.Modules.Vision.Common;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AvatarBuilder.Modules.Vision.Analysis;

public static class FaceCueAutoLayoutEstimator
{
    public static FaceCueGuideLayout Estimate(BitmapSource bitmap, FaceCueGuideLayout current)
    {
        var gray = CreateGrayPixels(bitmap, out var width, out var height, out var stride);
        var bestLayout = current;
        var bestScore = ScoreLayout(gray, stride, width, height, current, current);

        foreach (var candidate in CreateCandidates(current))
        {
            var score = ScoreLayout(gray, stride, width, height, candidate, current);
            if (score > bestScore)
            {
                bestScore = score;
                bestLayout = candidate;
            }
        }

        return new FaceCueGuideLayout(
            Lerp(current.CenterXPercent, bestLayout.CenterXPercent, 0.28d),
            Lerp(current.CenterYPercent, bestLayout.CenterYPercent, 0.28d),
            Lerp(current.HeightPercent, bestLayout.HeightPercent, 0.18d));
    }

    private static IEnumerable<FaceCueGuideLayout> CreateCandidates(FaceCueGuideLayout current)
    {
        for (var dx = -12d; dx <= 12d; dx += 3d)
        {
            for (var dy = -12d; dy <= 12d; dy += 3d)
            {
                for (var ds = -10d; ds <= 10d; ds += 5d)
                {
                    yield return new FaceCueGuideLayout(
                        current.CenterXPercent + dx,
                        current.CenterYPercent + dy,
                        current.HeightPercent + ds);
                }
            }
        }
    }

    private static double ScoreLayout(byte[] pixels, int stride, int width, int height, FaceCueGuideLayout candidate, FaceCueGuideLayout current)
    {
        var faceRegion = candidate.ToPixelRegion(width, height, candidate.Face);
        var eyeRegion = candidate.ToPixelRegion(width, height, candidate.Eyes);
        var jawRegion = candidate.ToPixelRegion(width, height, candidate.Jaw);

        var quality = CalculateQuality(pixels, stride, faceRegion);
        var eyeContrast = CalculateVerticalContrast(pixels, stride, eyeRegion);
        var jawDetail = CalculateEdgeAndDarknessScore(pixels, stride, jawRegion);
        var distancePenalty =
            Math.Abs(candidate.CenterXPercent - current.CenterXPercent) * 0.012d
            + Math.Abs(candidate.CenterYPercent - current.CenterYPercent) * 0.012d
            + Math.Abs(candidate.HeightPercent - current.HeightPercent) * 0.008d;

        return quality * 0.42d + eyeContrast * 0.34d + jawDetail * 0.24d - distancePenalty;
    }

    private static byte[] CreateGrayPixels(BitmapSource bitmap, out int width, out int height, out int stride)
    {
        var scale = Math.Min(1d, 120d / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
        var scaled = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        var converted = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);
        width = converted.PixelWidth;
        height = converted.PixelHeight;
        stride = Math.Max(1, (width * converted.Format.BitsPerPixel + 7) / 8);
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static double CalculateQuality(byte[] pixels, int stride, Int32Rect region)
    {
        double sum = 0d;
        double sumSquared = 0d;
        var count = 0;
        for (var y = region.Y; y < region.Y + region.Height; y++)
        {
            var row = y * stride;
            for (var x = region.X; x < region.X + region.Width; x++)
            {
                var value = pixels[row + x] / 255d;
                sum += value;
                sumSquared += value * value;
                count++;
            }
        }

        if (count == 0)
        {
            return 0d;
        }

        var mean = sum / count;
        var variance = Math.Max(0d, sumSquared / count - mean * mean);
        var brightness = 1d - Math.Clamp(Math.Abs(mean - 0.52d) / 0.52d, 0d, 1d);
        var contrast = Math.Clamp(Math.Sqrt(variance) / 0.22d, 0d, 1d);
        return brightness * 0.55d + contrast * 0.45d;
    }

    private static double CalculateVerticalContrast(byte[] pixels, int stride, Int32Rect region)
    {
        long total = 0;
        var count = 0;
        for (var y = region.Y + 1; y < region.Y + region.Height; y++)
        {
            var row = y * stride;
            var previousRow = (y - 1) * stride;
            for (var x = region.X; x < region.X + region.Width; x++)
            {
                total += Math.Abs(pixels[row + x] - pixels[previousRow + x]);
                count++;
            }
        }

        return count == 0 ? 0d : total / (double)(count * 255d);
    }

    private static double CalculateEdgeAndDarknessScore(byte[] pixels, int stride, Int32Rect region)
    {
        long edgeTotal = 0;
        long darknessTotal = 0;
        var count = 0;
        for (var y = region.Y + 1; y < region.Y + region.Height; y++)
        {
            var row = y * stride;
            var previousRow = (y - 1) * stride;
            for (var x = region.X + 1; x < region.X + region.Width; x++)
            {
                var value = pixels[row + x];
                edgeTotal += Math.Abs(value - pixels[row + x - 1]);
                edgeTotal += Math.Abs(value - pixels[previousRow + x]);
                darknessTotal += 255 - value;
                count++;
            }
        }

        if (count == 0)
        {
            return 0d;
        }

        var edge = edgeTotal / (double)(count * 510d);
        var darkness = darknessTotal / (double)(count * 255d);
        return edge * 0.65d + darkness * 0.35d;
    }

    private static double Lerp(double from, double to, double amount)
    {
        return from + (to - from) * amount;
    }
}
