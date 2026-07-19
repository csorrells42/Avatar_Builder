using AvatarBuilder.Modules.Vision.Common;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AvatarBuilder.Modules.Vision.Analysis;

public sealed class FaceCueAnalyzer : IFaceCueAnalyzer
{
    private const int BaselineTargetSamples = 30;
    private double _eyeBaseline;
    private double _leftEyeBaseline;
    private double _rightEyeBaseline;
    private double _jawBaseline;
    private double _leftJawBaseline;
    private double _rightJawBaseline;
    private double _lowerFaceCenterBaseline;
    private double _faceCenterBaseline;
    private int _baselineSamples;

    public void Reset()
    {
        _eyeBaseline = 0d;
        _leftEyeBaseline = 0d;
        _rightEyeBaseline = 0d;
        _jawBaseline = 0d;
        _leftJawBaseline = 0d;
        _rightJawBaseline = 0d;
        _lowerFaceCenterBaseline = 0d;
        _faceCenterBaseline = 0d;
        _baselineSamples = 0;
    }

    public FaceCueBaselineSnapshot ExportBaseline()
    {
        return new FaceCueBaselineSnapshot
        {
            BaselineSamples = _baselineSamples,
            EyeBaseline = _eyeBaseline,
            LeftEyeBaseline = _leftEyeBaseline,
            RightEyeBaseline = _rightEyeBaseline,
            JawBaseline = _jawBaseline,
            LeftJawBaseline = _leftJawBaseline,
            RightJawBaseline = _rightJawBaseline,
            LowerFaceCenterBaseline = _lowerFaceCenterBaseline,
            FaceCenterBaseline = _faceCenterBaseline
        };
    }

    public bool TryImportBaseline(FaceCueBaselineSnapshot? baseline)
    {
        if (baseline is null || baseline.BaselineSamples <= 0)
        {
            return false;
        }

        _baselineSamples = Math.Clamp(baseline.BaselineSamples, 0, BaselineTargetSamples);
        _eyeBaseline = baseline.EyeBaseline;
        _leftEyeBaseline = baseline.LeftEyeBaseline;
        _rightEyeBaseline = baseline.RightEyeBaseline;
        _jawBaseline = baseline.JawBaseline;
        _leftJawBaseline = baseline.LeftJawBaseline;
        _rightJawBaseline = baseline.RightJawBaseline;
        _lowerFaceCenterBaseline = baseline.LowerFaceCenterBaseline;
        _faceCenterBaseline = baseline.FaceCenterBaseline;
        return true;
    }

    public FaceCueAnalysis Analyze(BitmapSource bitmap, FaceCueGuideLayout layout)
    {
        var gray = CreateGrayPixels(bitmap, out var width, out var height, out var stride);
        var eyeRegion = layout.ToPixelRegion(width, height, layout.Eyes);
        var leftEyeRegion = layout.ToPixelRegion(width, height, layout.LeftEye);
        var rightEyeRegion = layout.ToPixelRegion(width, height, layout.RightEye);
        var jawRegion = layout.ToPixelRegion(width, height, layout.Jaw);
        var leftJawRegion = layout.ToPixelRegion(width, height, layout.LeftJaw);
        var rightJawRegion = layout.ToPixelRegion(width, height, layout.RightJaw);
        var faceRegion = layout.ToPixelRegion(width, height, layout.Face);

        var eyeScore = CalculateVerticalContrast(gray, stride, eyeRegion);
        var leftEyeScore = CalculateVerticalContrast(gray, stride, leftEyeRegion);
        var rightEyeScore = CalculateVerticalContrast(gray, stride, rightEyeRegion);
        var jawScore = CalculateEdgeAndDarknessScore(gray, stride, jawRegion);
        var leftJawScore = CalculateEdgeAndDarknessScore(gray, stride, leftJawRegion);
        var rightJawScore = CalculateEdgeAndDarknessScore(gray, stride, rightJawRegion);
        var lowerFaceCenter = CalculateVerticalCenterOfEnergy(gray, stride, jawRegion);
        var faceCenter = CalculateHorizontalCenterOfEnergy(gray, stride, faceRegion);
        var quality = CalculateQualityPercent(gray, stride, faceRegion);

        if (_baselineSamples < BaselineTargetSamples)
        {
            _baselineSamples++;
            var weight = 1d / _baselineSamples;
            _eyeBaseline = Lerp(_eyeBaseline, eyeScore, weight);
            _leftEyeBaseline = Lerp(_leftEyeBaseline, leftEyeScore, weight);
            _rightEyeBaseline = Lerp(_rightEyeBaseline, rightEyeScore, weight);
            _jawBaseline = Lerp(_jawBaseline, jawScore, weight);
            _leftJawBaseline = Lerp(_leftJawBaseline, leftJawScore, weight);
            _rightJawBaseline = Lerp(_rightJawBaseline, rightJawScore, weight);
            _lowerFaceCenterBaseline = Lerp(_lowerFaceCenterBaseline, lowerFaceCenter, weight);
            _faceCenterBaseline = Lerp(_faceCenterBaseline, faceCenter, weight);

            return new FaceCueAnalysis
            {
                BaselineReady = false,
                BaselineSamples = _baselineSamples,
                QualityPercent = quality,
                CompositeCuePercent = 0d,
                EyeOpennessPercent = 100d,
                EyeDropPercent = 0d,
                EyeAsymmetryPercent = 0d,
                JawChangePercent = 0d
            };
        }

        var eyeOpenness = _eyeBaseline <= 0.0001d ? 100d : Math.Clamp(eyeScore / _eyeBaseline * 100d, 0d, 180d);
        var eyeDrop = Math.Clamp(100d - eyeOpenness, 0d, 100d);
        var leftEyeOpenness = _leftEyeBaseline <= 0.0001d ? 100d : Math.Clamp(leftEyeScore / _leftEyeBaseline * 100d, 0d, 180d);
        var rightEyeOpenness = _rightEyeBaseline <= 0.0001d ? 100d : Math.Clamp(rightEyeScore / _rightEyeBaseline * 100d, 0d, 180d);
        var eyeAsymmetry = Math.Clamp(Math.Abs(leftEyeOpenness - rightEyeOpenness), 0d, 100d);
        var jawChange = _jawBaseline <= 0.0001d ? 0d : Math.Clamp(Math.Abs(jawScore - _jawBaseline) / _jawBaseline * 100d, 0d, 200d);
        var leftJawChange = _leftJawBaseline <= 0.0001d ? 0d : Math.Clamp(Math.Abs(leftJawScore - _leftJawBaseline) / _leftJawBaseline * 100d, 0d, 200d);
        var rightJawChange = _rightJawBaseline <= 0.0001d ? 0d : Math.Clamp(Math.Abs(rightJawScore - _rightJawBaseline) / _rightJawBaseline * 100d, 0d, 200d);
        var jawAsymmetry = Math.Clamp(Math.Abs(leftJawChange - rightJawChange), 0d, 200d);
        var lowerFaceDrop = Math.Clamp(Math.Abs(lowerFaceCenter - _lowerFaceCenterBaseline) * 100d, 0d, 100d);
        var headDrift = Math.Clamp(Math.Abs(faceCenter - _faceCenterBaseline) * 100d, 0d, 100d);
        var compositeCue = CalculateCompositeCue(eyeDrop, eyeAsymmetry, jawChange, jawAsymmetry, lowerFaceDrop, headDrift, quality);

        // Keep a gentle baseline drift so lighting changes do not permanently poison the cue model.
        if (compositeCue < 20d && quality >= 50d)
        {
            _eyeBaseline = Lerp(_eyeBaseline, eyeScore, 0.015d);
            _leftEyeBaseline = Lerp(_leftEyeBaseline, leftEyeScore, 0.015d);
            _rightEyeBaseline = Lerp(_rightEyeBaseline, rightEyeScore, 0.015d);
            _jawBaseline = Lerp(_jawBaseline, jawScore, 0.015d);
            _leftJawBaseline = Lerp(_leftJawBaseline, leftJawScore, 0.015d);
            _rightJawBaseline = Lerp(_rightJawBaseline, rightJawScore, 0.015d);
            _lowerFaceCenterBaseline = Lerp(_lowerFaceCenterBaseline, lowerFaceCenter, 0.015d);
            _faceCenterBaseline = Lerp(_faceCenterBaseline, faceCenter, 0.015d);
        }

        return new FaceCueAnalysis
        {
            BaselineReady = true,
            BaselineSamples = _baselineSamples,
            QualityPercent = quality,
            CompositeCuePercent = compositeCue,
            EyeOpennessPercent = eyeOpenness,
            EyeDropPercent = eyeDrop,
            EyeAsymmetryPercent = eyeAsymmetry,
            JawChangePercent = jawChange,
            JawAsymmetryPercent = jawAsymmetry,
            LowerFaceDropPercent = lowerFaceDrop,
            HeadDriftPercent = headDrift
        };
    }

    private static byte[] CreateGrayPixels(BitmapSource bitmap, out int width, out int height, out int stride)
    {
        var scale = Math.Min(1d, 160d / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
        var scaled = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
        var converted = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);
        width = converted.PixelWidth;
        height = converted.PixelHeight;
        stride = Math.Max(1, (width * converted.Format.BitsPerPixel + 7) / 8);
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);
        return pixels;
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

    private static double CalculateVerticalCenterOfEnergy(byte[] pixels, int stride, Int32Rect region)
    {
        double weighted = 0d;
        double total = 0d;
        for (var y = region.Y; y < region.Y + region.Height; y++)
        {
            var row = y * stride;
            for (var x = region.X; x < region.X + region.Width; x++)
            {
                var energy = 255 - pixels[row + x];
                weighted += energy * ((y - region.Y) / (double)Math.Max(1, region.Height - 1));
                total += energy;
            }
        }

        return total <= 0.0001d ? 0.5d : weighted / total;
    }

    private static double CalculateHorizontalCenterOfEnergy(byte[] pixels, int stride, Int32Rect region)
    {
        double weighted = 0d;
        double total = 0d;
        for (var y = region.Y; y < region.Y + region.Height; y++)
        {
            var row = y * stride;
            for (var x = region.X; x < region.X + region.Width; x++)
            {
                var energy = 255 - pixels[row + x];
                weighted += energy * ((x - region.X) / (double)Math.Max(1, region.Width - 1));
                total += energy;
            }
        }

        return total <= 0.0001d ? 0.5d : weighted / total;
    }

    private static double CalculateQualityPercent(byte[] pixels, int stride, Int32Rect region)
    {
        double sum = 0d;
        double sumSquared = 0d;
        var count = 0;

        for (var y = region.Y; y < region.Y + region.Height; y++)
        {
            var row = y * stride;
            for (var x = region.X; x < region.X + region.Width; x++)
            {
                var normalized = pixels[row + x] / 255d;
                sum += normalized;
                sumSquared += normalized * normalized;
                count++;
            }
        }

        if (count == 0)
        {
            return 0d;
        }

        var mean = sum / count;
        var variance = Math.Max(0d, sumSquared / count - mean * mean);
        var contrast = Math.Sqrt(variance);
        var brightnessScore = 1d - Math.Clamp(Math.Abs(mean - 0.52d) / 0.52d, 0d, 1d);
        var contrastScore = Math.Clamp(contrast / 0.22d, 0d, 1d);
        return Math.Clamp((brightnessScore * 0.55d + contrastScore * 0.45d) * 100d, 0d, 100d);
    }

    private static double CalculateCompositeCue(
        double eyeDrop,
        double eyeAsymmetry,
        double jawChange,
        double jawAsymmetry,
        double lowerFaceDrop,
        double headDrift,
        double quality)
    {
        var raw = eyeDrop * 0.56d
            + eyeAsymmetry * 0.10d
            + jawChange * 0.14d
            + jawAsymmetry * 0.10d
            + lowerFaceDrop * 0.06d
            + headDrift * 0.04d;
        var qualityPenalty = quality < 45d ? 0.65d : 1d;
        return Math.Clamp(raw * qualityPenalty, 0d, 100d);
    }

    private static double Lerp(double from, double to, double amount)
    {
        return from + (to - from) * amount;
    }
}
