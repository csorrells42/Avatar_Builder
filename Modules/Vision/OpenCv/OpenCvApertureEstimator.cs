using AvatarBuilder.Modules.Vision.Analysis;
using AvatarBuilder.Modules.Vision.Common;
using OpenCvSharp;
using CvRect = OpenCvSharp.Rect;
using WpfPoint = System.Windows.Point;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public static class OpenCvApertureEstimator
{
    public static ApertureEstimate EstimateEye(Mat gray, CvRect eyeBox)
    {
        return EstimateDarkAperture(
            gray,
            eyeBox,
            innerXFraction: 0.14d,
            innerYFraction: 0.18d,
            minimumRowCoverage: 0.075d,
            minimumColumnCoverage: 0.06d,
            verticalPaddingFraction: 0.18d,
            horizontalPaddingFraction: 0.04d,
            maximumOpeningFraction: 0.72d);
    }

    public static ApertureEstimate EstimateMouth(Mat gray, CvRect mouthBox)
    {
        return EstimateDarkAperture(
            gray,
            mouthBox,
            innerXFraction: 0.10d,
            innerYFraction: 0.20d,
            minimumRowCoverage: 0.09d,
            minimumColumnCoverage: 0.08d,
            verticalPaddingFraction: 0.18d,
            horizontalPaddingFraction: 0.08d,
            maximumOpeningFraction: 0.80d);
    }

    public static ApertureEstimate FromBox(CvRect box, double heightFraction, double confidence)
    {
        var centerX = box.X + box.Width / 2d;
        var centerY = box.Y + box.Height / 2d;
        var halfWidth = box.Width * 0.48d;
        var halfHeight = box.Height * heightFraction / 2d;
        return new ApertureEstimate(
            true,
            new CvRect(
                (int)Math.Round(centerX - halfWidth),
                (int)Math.Round(centerY - halfHeight),
                Math.Max(1, (int)Math.Round(halfWidth * 2d)),
                Math.Max(1, (int)Math.Round(halfHeight * 2d))),
            CreateOvalContour(centerX, centerY, halfWidth, halfHeight),
            confidence);
    }

    private static ApertureEstimate EstimateDarkAperture(
        Mat gray,
        CvRect featureBox,
        double innerXFraction,
        double innerYFraction,
        double minimumRowCoverage,
        double minimumColumnCoverage,
        double verticalPaddingFraction,
        double horizontalPaddingFraction,
        double maximumOpeningFraction)
    {
        if (gray.Empty() || gray.Width <= 0 || gray.Height <= 0 || gray.Channels() != 1)
        {
            return ApertureEstimate.None;
        }

        var roi = ClampRect(featureBox, gray.Width, gray.Height);
        if (roi.Width < 8 || roi.Height < 6)
        {
            return ApertureEstimate.None;
        }

        using var view = new Mat(gray, roi).Clone();
        if (view.Empty() || view.Width < 8 || view.Height < 6)
        {
            return ApertureEstimate.None;
        }

        using var equalized = new Mat();
        using var darkMask = new Mat();
        var imageQuality = AnalyzeImageQuality(view);
        Cv2.EqualizeHist(view, equalized);
        using var smoothed = SmoothGray3x3(equalized);
        Cv2.Threshold(smoothed, darkMask, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        Cv2.MorphologyEx(darkMask, darkMask, MorphTypes.Open, kernel);
        Cv2.MorphologyEx(darkMask, darkMask, MorphTypes.Close, kernel);
        RemoveLikelyGlassesFrameArtifacts(darkMask);

        var innerLeft = Math.Clamp((int)Math.Round(roi.Width * innerXFraction), 0, Math.Max(0, roi.Width - 1));
        var innerRight = Math.Clamp((int)Math.Round(roi.Width * (1d - innerXFraction)), innerLeft + 1, roi.Width);
        var innerTop = Math.Clamp((int)Math.Round(roi.Height * innerYFraction), 0, Math.Max(0, roi.Height - 1));
        var innerBottom = Math.Clamp((int)Math.Round(roi.Height * (1d - innerYFraction)), innerTop + 1, roi.Height);

        if (TryEstimateCenterWeightedAperture(
            darkMask,
            roi,
            innerLeft,
            innerRight,
            innerTop,
            innerBottom,
            verticalPaddingFraction,
            horizontalPaddingFraction,
            imageQuality,
            out var centerEstimate))
        {
            return centerEstimate;
        }

        if (TryEstimateCentralComponent(
            darkMask,
            roi,
            innerLeft,
            innerRight,
            innerTop,
            innerBottom,
            verticalPaddingFraction,
            horizontalPaddingFraction,
            maximumOpeningFraction,
            imageQuality,
            out var componentEstimate))
        {
            return componentEstimate;
        }

        var rowSpan = FindProjectionSpan(
            darkMask,
            innerLeft,
            innerRight,
            innerTop,
            innerBottom,
            scanRows: true,
            minimumCoverage: minimumRowCoverage);
        var columnSpan = FindProjectionSpan(
            darkMask,
            innerLeft,
            innerRight,
            innerTop,
            innerBottom,
            scanRows: false,
            minimumCoverage: minimumColumnCoverage);

        if (rowSpan is null || columnSpan is null)
        {
            return ApertureEstimate.FromDiagnostics(imageQuality);
        }

        var left = columnSpan.Value.Start;
        var right = columnSpan.Value.End;
        var top = rowSpan.Value.Start;
        var bottom = rowSpan.Value.End;
        var width = Math.Max(1, right - left + 1);
        var height = Math.Max(1, bottom - top + 1);
        if (height / (double)Math.Max(1, roi.Height) > maximumOpeningFraction)
        {
            var center = top + height / 2d;
            height = Math.Max(1, (int)Math.Round(roi.Height * maximumOpeningFraction));
            top = Math.Clamp((int)Math.Round(center - height / 2d), 0, Math.Max(0, roi.Height - height));
            bottom = top + height - 1;
        }

        var mass = CountNonZero(darkMask, innerLeft, innerRight, innerTop, innerBottom);
        var area = Math.Max(1, (innerRight - innerLeft) * (innerBottom - innerTop));
        var massRatio = mass / (double)area;
        var coverageScore = Math.Clamp(massRatio / 0.18d, 0d, 1d);
        var spanScore = Math.Clamp(height / (double)Math.Max(1, roi.Height) / 0.34d, 0d, 1d);
        var confidence = AdjustConfidenceForImageQuality(
            Math.Clamp(coverageScore * 0.58d + spanScore * 0.42d, 0.05d, 0.88d),
            imageQuality);

        return CreateProfileAwareEstimate(
            darkMask,
            roi,
            left,
            right,
            top,
            bottom,
            verticalPaddingFraction,
            horizontalPaddingFraction,
            confidence,
            imageQuality.GlareRatio,
            imageQuality.ContrastScore,
            imageQuality.SharpnessScore,
            massRatio);
    }

    private static bool TryEstimateCenterWeightedAperture(
        Mat mask,
        CvRect roi,
        int innerLeft,
        int innerRight,
        int innerTop,
        int innerBottom,
        double verticalPaddingFraction,
        double horizontalPaddingFraction,
        ApertureImageQuality imageQuality,
        out ApertureEstimate estimate)
    {
        estimate = ApertureEstimate.None;
        var targetX = roi.Width / 2d;
        var targetY = roi.Height / 2d;
        var searchTop = Math.Max(innerTop, (int)Math.Round(targetY - roi.Height * 0.22d));
        var searchBottom = Math.Min(innerBottom, (int)Math.Round(targetY + roi.Height * 0.22d));
        if (searchBottom <= searchTop)
        {
            return false;
        }

        var rowCounts = new int[innerBottom - innerTop];
        var bestRow = -1;
        var bestRowCount = 0;
        for (var y = innerTop; y < innerBottom; y++)
        {
            var count = CountNonZero(mask, innerLeft, innerRight, y, y + 1);
            rowCounts[y - innerTop] = count;
            if (y >= searchTop && y < searchBottom && count > bestRowCount)
            {
                bestRow = y;
                bestRowCount = count;
            }
        }

        if (bestRow < 0 || bestRowCount < Math.Max(3, (innerRight - innerLeft) * 0.08d))
        {
            return false;
        }

        var rowThreshold = Math.Max(1, (int)Math.Round(bestRowCount * 0.34d));
        var top = bestRow;
        while (top > innerTop && rowCounts[top - 1 - innerTop] >= rowThreshold)
        {
            top--;
        }

        var bottom = bestRow;
        while (bottom < innerBottom - 1 && rowCounts[bottom + 1 - innerTop] >= rowThreshold)
        {
            bottom++;
        }

        var height = Math.Max(1, bottom - top + 1);
        if (height > roi.Height * 0.55d)
        {
            return false;
        }

        var columnCounts = new int[innerRight - innerLeft];
        var columnThreshold = Math.Max(1, (int)Math.Round(height * 0.16d));
        for (var x = innerLeft; x < innerRight; x++)
        {
            columnCounts[x - innerLeft] = CountNonZero(mask, x, x + 1, top, bottom + 1);
        }

        var centerColumn = Math.Clamp((int)Math.Round(targetX), innerLeft, innerRight - 1);
        var left = centerColumn;
        while (left > innerLeft && columnCounts[left - 1 - innerLeft] >= columnThreshold)
        {
            left--;
        }

        var right = centerColumn;
        while (right < innerRight - 1 && columnCounts[right + 1 - innerLeft] >= columnThreshold)
        {
            right++;
        }

        if (right - left < Math.Max(4, roi.Width * 0.12d))
        {
            return false;
        }

        var width = Math.Max(1, right - left + 1);
        var horizontalPadding = Math.Max(1d, width * horizontalPaddingFraction);
        var centerGlobalX = roi.X + left + width / 2d;
        var halfWidth = Math.Max(2d, width / 2d + horizontalPadding);
        var confidence = AdjustConfidenceForImageQuality(Math.Clamp(
            Math.Min(1d, bestRowCount / Math.Max(1d, innerRight - innerLeft) * 2d) * 0.55d
            + Math.Min(1d, height / Math.Max(1d, roi.Height * 0.34d)) * 0.45d,
            0.08d,
            0.90d),
            imageQuality);
        var darkCoverage = CountNonZero(mask, left, right + 1, top, bottom + 1) / (double)Math.Max(1, width * height);

        estimate = CreateProfileAwareEstimate(
            mask,
            roi,
            left,
            right,
            top,
            bottom,
            verticalPaddingFraction,
            horizontalPaddingFraction,
            confidence,
            imageQuality.GlareRatio,
            imageQuality.ContrastScore,
            imageQuality.SharpnessScore,
            darkCoverage);
        return true;
    }

    private static void RemoveLikelyGlassesFrameArtifacts(Mat mask)
    {
        var width = mask.Width;
        var height = mask.Height;
        var targetY = height / 2d;
        for (var y = 0; y < height; y++)
        {
            var count = CountNonZero(mask, 0, width, y, y + 1);
            var isPeripheral = Math.Abs(y - targetY) > height * 0.18d;
            if (isPeripheral && count >= width * 0.45d)
            {
                for (var x = 0; x < width; x++)
                {
                    mask.Set(y, x, (byte)0);
                }
            }
        }

        for (var x = 0; x < width; x++)
        {
            var count = CountNonZero(mask, x, x + 1, 0, height);
            if (count >= height * 0.68d)
            {
                for (var y = 0; y < height; y++)
                {
                    mask.Set(y, x, (byte)0);
                }
            }
        }
    }

    private static bool TryEstimateCentralComponent(
        Mat mask,
        CvRect roi,
        int innerLeft,
        int innerRight,
        int innerTop,
        int innerBottom,
        double verticalPaddingFraction,
        double horizontalPaddingFraction,
        double maximumOpeningFraction,
        ApertureImageQuality imageQuality,
        out ApertureEstimate estimate)
    {
        estimate = ApertureEstimate.None;
        Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var bestScore = 0d;
        CvRect bestRect = default;
        var targetX = roi.Width / 2d;
        var targetY = roi.Height / 2d;

        foreach (var contour in contours)
        {
            var rect = Cv2.BoundingRect(contour);
            if (rect.Width < Math.Max(4, roi.Width * 0.12d)
                || rect.Height < 1
                || rect.X + rect.Width / 2d < innerLeft
                || rect.X + rect.Width / 2d > innerRight
                || rect.Y + rect.Height / 2d < innerTop
                || rect.Y + rect.Height / 2d > innerBottom)
            {
                continue;
            }

            var isLongFrameLine = rect.Width > roi.Width * 0.72d && rect.Height < Math.Max(3d, roi.Height * 0.09d);
            var isNarrowFrameBridge = rect.Width < roi.Width * 0.10d && rect.Height > roi.Height * 0.35d;
            if (isLongFrameLine || isNarrowFrameBridge)
            {
                continue;
            }

            if (rect.Height / (double)Math.Max(1, roi.Height) > maximumOpeningFraction)
            {
                continue;
            }

            var centerX = rect.X + rect.Width / 2d;
            var centerY = rect.Y + rect.Height / 2d;
            var centerScore = Math.Clamp(1d - Math.Abs(centerX - targetX) / Math.Max(1d, roi.Width * 0.50d), 0d, 1d)
                * Math.Clamp(1d - Math.Abs(centerY - targetY) / Math.Max(1d, roi.Height * 0.50d), 0d, 1d);
            var area = Math.Max(1d, Cv2.ContourArea(contour));
            var score = area * Math.Max(0.08d, centerScore);
            if (score > bestScore)
            {
                bestScore = score;
                bestRect = rect;
            }
        }

        if (bestScore <= 0d)
        {
            return false;
        }

        var areaRatio = Math.Clamp(bestScore / Math.Max(1d, roi.Width * roi.Height * 0.12d), 0d, 1d);
        var spanScore = Math.Clamp(bestRect.Height / (double)Math.Max(1, roi.Height) / 0.34d, 0d, 1d);
        var confidence = AdjustConfidenceForImageQuality(
            Math.Clamp(areaRatio * 0.50d + spanScore * 0.50d, 0.08d, 0.90d),
            imageQuality);
        var darkCoverage = CountNonZero(mask, bestRect.X, bestRect.Right, bestRect.Y, bestRect.Bottom)
            / (double)Math.Max(1, bestRect.Width * bestRect.Height);

        estimate = CreateProfileAwareEstimate(
            mask,
            roi,
            bestRect.Left,
            bestRect.Right - 1,
            bestRect.Top,
            bestRect.Bottom - 1,
            verticalPaddingFraction,
            horizontalPaddingFraction,
            confidence,
            imageQuality.GlareRatio,
            imageQuality.ContrastScore,
            imageQuality.SharpnessScore,
            darkCoverage);
        return true;
    }

    private static ApertureEstimate CreateProfileAwareEstimate(
        Mat mask,
        CvRect roi,
        int left,
        int right,
        int top,
        int bottom,
        double verticalPaddingFraction,
        double horizontalPaddingFraction,
        double confidence,
        double glareRatio,
        double contrastScore,
        double sharpnessScore,
        double darkCoverage)
    {
        var width = Math.Max(1, right - left + 1);
        var boundingHeight = Math.Max(1, bottom - top + 1);
        var profile = MeasureColumnProfile(mask, left, right + 1, top, bottom + 1);
        var measuredHeight = profile.SampleCount >= Math.Max(3, width * 0.18d)
            ? BlendProfileHeight(boundingHeight, profile.MedianHeight)
            : boundingHeight;
        var centerX = roi.X + left + width / 2d;
        var centerY = roi.Y + (profile.SampleCount > 0 ? profile.CenterY : top + boundingHeight / 2d);
        var verticalPadding = Math.Max(1d, measuredHeight * verticalPaddingFraction);
        var horizontalPadding = Math.Max(1d, width * horizontalPaddingFraction);
        var halfWidth = Math.Max(2d, width / 2d + horizontalPadding);
        var halfHeight = Math.Max(1d, measuredHeight / 2d + verticalPadding);
        var apertureWidth = Math.Max(1d, halfWidth * 2d);
        var apertureHeight = Math.Max(1d, halfHeight * 2d);
        var openingRatio = Math.Clamp(apertureHeight / apertureWidth, 0d, 2d);

        return new ApertureEstimate(
            true,
            new CvRect(
                Math.Max(0, (int)Math.Round(centerX - halfWidth)),
                Math.Max(0, (int)Math.Round(centerY - halfHeight)),
                Math.Max(1, (int)Math.Round(apertureWidth)),
                Math.Max(1, (int)Math.Round(apertureHeight))),
            CreateOvalContour(centerX, centerY, halfWidth, halfHeight),
            confidence,
            glareRatio,
            contrastScore,
            sharpnessScore,
            darkCoverage,
            openingRatio,
            profile.SampleCount,
            profile.CoverageRatio);
    }

    private static double BlendProfileHeight(int boundingHeight, double profileMedianHeight)
    {
        if (profileMedianHeight <= 0d)
        {
            return boundingHeight;
        }

        var blended = boundingHeight * 0.35d + profileMedianHeight * 0.65d;
        return Math.Clamp(blended, Math.Max(1d, profileMedianHeight * 0.80d), boundingHeight);
    }

    private static ApertureColumnProfile MeasureColumnProfile(Mat mask, int left, int right, int top, int bottom)
    {
        left = Math.Clamp(left, 0, Math.Max(0, mask.Width - 1));
        right = Math.Clamp(right, left + 1, mask.Width);
        top = Math.Clamp(top, 0, Math.Max(0, mask.Height - 1));
        bottom = Math.Clamp(bottom, top + 1, mask.Height);

        var heights = new List<int>();
        var centers = new List<double>();
        var maximumAcceptedHeight = Math.Max(2, (int)Math.Round((bottom - top) * 0.92d));
        for (var x = left; x < right; x++)
        {
            var first = -1;
            var last = -1;
            var darkPixels = 0;
            var currentRunStart = -1;
            var currentRunLength = 0;
            var bestRunStart = -1;
            var bestRunLength = 0;
            for (var y = top; y < bottom; y++)
            {
                if (mask.At<byte>(y, x) == 0)
                {
                    if (currentRunLength > bestRunLength)
                    {
                        bestRunStart = currentRunStart;
                        bestRunLength = currentRunLength;
                    }

                    currentRunStart = -1;
                    currentRunLength = 0;
                    continue;
                }

                first = first < 0 ? y : first;
                last = y;
                darkPixels++;
                currentRunStart = currentRunStart < 0 ? y : currentRunStart;
                currentRunLength++;
            }

            if (currentRunLength > bestRunLength)
            {
                bestRunStart = currentRunStart;
                bestRunLength = currentRunLength;
            }

            if (first < 0)
            {
                continue;
            }

            var span = last - first + 1;
            if (span > maximumAcceptedHeight && darkPixels < span * 0.28d)
            {
                continue;
            }

            var runIsMoreSpecific = bestRunLength > 0
                && (bestRunLength <= span * 0.78d || darkPixels <= span * 0.72d);
            var measuredHeight = runIsMoreSpecific ? bestRunLength : span;
            var measuredCenter = runIsMoreSpecific && bestRunStart >= 0
                ? bestRunStart + bestRunLength / 2d
                : (first + last) / 2d;
            heights.Add(measuredHeight);
            centers.Add(measuredCenter);
        }

        if (heights.Count == 0)
        {
            return ApertureColumnProfile.None;
        }

        heights.Sort();
        var median = heights[heights.Count / 2];
        var trim = heights.Count >= 8 ? Math.Max(1, heights.Count / 10) : 0;
        var trimmed = heights.Skip(trim).Take(Math.Max(1, heights.Count - trim * 2)).ToList();
        var average = trimmed.Average();
        var centerY = centers.Average();
        return new ApertureColumnProfile(
            average,
            median,
            heights.Count,
            heights.Count / (double)Math.Max(1, right - left),
            centerY);
    }

    private static (int Start, int End)? FindProjectionSpan(
        Mat mask,
        int innerLeft,
        int innerRight,
        int innerTop,
        int innerBottom,
        bool scanRows,
        double minimumCoverage)
    {
        var limit = scanRows ? innerBottom : innerRight;
        var countLength = scanRows ? innerRight - innerLeft : innerBottom - innerTop;
        var threshold = Math.Max(1, (int)Math.Round(countLength * minimumCoverage));
        var bestStart = -1;
        var bestEnd = -1;
        var currentStart = -1;
        var currentEnd = -1;

        for (var index = scanRows ? innerTop : innerLeft; index < limit; index++)
        {
            var count = scanRows
                ? CountNonZero(mask, innerLeft, innerRight, index, index + 1)
                : CountNonZero(mask, index, index + 1, innerTop, innerBottom);

            if (count >= threshold)
            {
                currentStart = currentStart < 0 ? index : currentStart;
                currentEnd = index;
                continue;
            }

            if (currentStart >= 0 && currentEnd - currentStart > bestEnd - bestStart)
            {
                bestStart = currentStart;
                bestEnd = currentEnd;
            }

            currentStart = -1;
            currentEnd = -1;
        }

        if (currentStart >= 0 && currentEnd - currentStart > bestEnd - bestStart)
        {
            bestStart = currentStart;
            bestEnd = currentEnd;
        }

        return bestStart < 0 ? null : (bestStart, bestEnd);
    }

    private static int CountNonZero(Mat mask, int left, int right, int top, int bottom)
    {
        var count = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                if (mask.At<byte>(y, x) != 0)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static ApertureImageQuality AnalyzeImageQuality(Mat grayRoi)
    {
        if (grayRoi.Empty() || grayRoi.Width <= 0 || grayRoi.Height <= 0)
        {
            return ApertureImageQuality.None;
        }

        Cv2.MeanStdDev(grayRoi, out _, out var intensityStdDev);
        using var laplacian = new Mat();
        Cv2.Laplacian(grayRoi, laplacian, MatType.CV_64F);
        Cv2.MeanStdDev(laplacian, out _, out var laplacianStdDev);

        var area = Math.Max(1, grayRoi.Width * grayRoi.Height);
        var glareRatio = CountPixelsAtLeast(grayRoi, 238) / (double)area;
        var contrastScore = Math.Clamp(intensityStdDev.Val0 / 55d, 0d, 1d);
        var laplacianVariance = laplacianStdDev.Val0 * laplacianStdDev.Val0;
        var sharpnessScore = Math.Clamp(laplacianVariance / 4500d, 0d, 1d);
        return new ApertureImageQuality(glareRatio, contrastScore, sharpnessScore);
    }

    private static double AdjustConfidenceForImageQuality(double confidence, ApertureImageQuality imageQuality)
    {
        var glarePenalty = Math.Clamp((imageQuality.GlareRatio - 0.035d) / 0.18d, 0d, 0.55d);
        var contrastBoost = (imageQuality.ContrastScore - 0.50d) * 0.16d;
        var sharpnessBoost = (imageQuality.SharpnessScore - 0.45d) * 0.14d;
        var multiplier = Math.Clamp(1d + contrastBoost + sharpnessBoost - glarePenalty, 0.46d, 1.08d);
        return Math.Clamp(confidence * multiplier, 0.03d, 0.92d);
    }

    private static int CountPixelsAtLeast(Mat gray, byte threshold)
    {
        var count = 0;
        var height = gray.Height;
        var width = gray.Width;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (gray.At<byte>(y, x) >= threshold)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static Mat SmoothGray3x3(Mat gray)
    {
        var rows = gray.Rows;
        var cols = gray.Cols;
        var output = new Mat(rows, cols, MatType.CV_8UC1);
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < cols; x++)
            {
                var sum = 0;
                var samples = 0;
                for (var yy = Math.Max(0, y - 1); yy <= Math.Min(rows - 1, y + 1); yy++)
                {
                    for (var xx = Math.Max(0, x - 1); xx <= Math.Min(cols - 1, x + 1); xx++)
                    {
                        sum += gray.At<byte>(yy, xx);
                        samples++;
                    }
                }

                output.Set(y, x, (byte)Math.Clamp((int)Math.Round(sum / (double)Math.Max(1, samples)), 0, 255));
            }
        }

        return output;
    }

    private static IReadOnlyList<WpfPoint> CreateOvalContour(double centerX, double centerY, double halfWidth, double halfHeight)
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

    private static CvRect ClampRect(CvRect rect, int width, int height)
    {
        var x = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
        var right = Math.Clamp(rect.Right, x + 1, width);
        var bottom = Math.Clamp(rect.Bottom, y + 1, height);
        return new CvRect(x, y, right - x, bottom - y);
    }
}

public sealed record ApertureEstimate(
    bool HasAperture,
    CvRect ApertureBox,
    IReadOnlyList<WpfPoint> Contour,
    double Confidence,
    double GlareRatio = 0d,
    double ContrastScore = 0d,
    double SharpnessScore = 0d,
    double DarkCoverageRatio = 0d,
    double? AverageOpeningRatio = null,
    int ProfileSampleCount = 0,
    double ProfileCoverageRatio = 0d)
{
    public static ApertureEstimate None { get; } = new(false, default, [], 0d);

    public static ApertureEstimate FromDiagnostics(ApertureImageQuality imageQuality)
    {
        return new ApertureEstimate(
            false,
            default,
            [],
            0d,
            imageQuality.GlareRatio,
            imageQuality.ContrastScore,
            imageQuality.SharpnessScore,
            0d);
    }
}

public sealed record ApertureColumnProfile(
    double AverageHeight,
    double MedianHeight,
    int SampleCount,
    double CoverageRatio,
    double CenterY)
{
    public static ApertureColumnProfile None { get; } = new(0d, 0d, 0, 0d, 0d);
}

public sealed record ApertureImageQuality(
    double GlareRatio,
    double ContrastScore,
    double SharpnessScore)
{
    public static ApertureImageQuality None { get; } = new(0d, 0d, 0d);
}
