using AvatarBuilder.Modules.Vision.Common;
using System.Windows;

namespace AvatarBuilder.Modules.Vision.Analysis;

public static class ContourOpeningEstimator
{
    public static double? CalculateOpeningRatio(IReadOnlyList<Point> contour)
    {
        return CalculateOpeningRatio(contour, preferPairedAverage: false);
    }

    public static double? CalculateOpeningRatio(IReadOnlyList<Point> contour, bool preferPairedAverage)
    {
        if (contour.Count < 4)
        {
            return null;
        }

        return preferPairedAverage
            ? CalculatePairedAverageOpeningRatio(contour)
                ?? CalculateAxisOpeningRatio(contour)
                ?? CalculateAxisAlignedOpeningRatio(contour)
            : CalculateAxisOpeningRatio(contour)
            ?? CalculateAxisAlignedOpeningRatio(contour);
    }

    public static double? CalculateAxisAlignedOpeningRatio(IReadOnlyList<Point> contour)
    {
        if (contour.Count < 4)
        {
            return null;
        }

        var first = contour[0];
        var minX = first.X;
        var maxX = first.X;
        var minY = first.Y;
        var maxY = first.Y;
        for (var index = 1; index < contour.Count; index++)
        {
            var point = contour[index];
            minX = Math.Min(minX, point.X);
            maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y);
            maxY = Math.Max(maxY, point.Y);
        }

        var width = maxX - minX;
        var height = maxY - minY;
        return width <= 0.0001d || height <= 0.0001d
            ? null
            : Math.Clamp(height / width, 0d, 2d);
    }

    private static double? CalculatePairedAverageOpeningRatio(IReadOnlyList<Point> contour)
    {
        var first = contour[0];
        var oppositeIndex = contour.Count / 2;
        var opposite = contour[oppositeIndex];
        var axis = CreateAxis(first, opposite);
        if (axis is not { } featureAxis)
        {
            return null;
        }

        var maximumPairCount = contour.Count / 2;
        Span<double> distances = maximumPairCount <= 64
            ? stackalloc double[maximumPairCount]
            : new double[maximumPairCount];
        var distanceCount = 0;
        for (var upperIndex = 1; upperIndex < oppositeIndex; upperIndex++)
        {
            var lowerIndex = contour.Count - upperIndex;
            if (lowerIndex <= upperIndex || lowerIndex >= contour.Count)
            {
                continue;
            }

            distances[distanceCount++] = Math.Abs(
                ProjectAcross(contour[upperIndex], featureAxis)
                - ProjectAcross(contour[lowerIndex], featureAxis));
        }

        if (distanceCount == 0)
        {
            return null;
        }

        var width = CalculateAxisWidth(contour, featureAxis);
        if (width <= 0.0001d)
        {
            return null;
        }

        var ordered = distances[..distanceCount];
        ordered.Sort();
        var trim = distanceCount >= 5 ? 1 : 0;
        var includedCount = distanceCount - trim * 2;
        var gapSum = 0d;
        for (var index = trim; index < distanceCount - trim; index++)
        {
            gapSum += ordered[index];
        }

        var averageGap = gapSum / includedCount;
        return Math.Clamp(averageGap / width, 0d, 2d);
    }

    private static double? CalculateAxisOpeningRatio(IReadOnlyList<Point> contour)
    {
        var first = contour[0];
        var opposite = contour[contour.Count / 2];
        var axis = CreateAxis(first, opposite);
        if (axis is not { } featureAxis)
        {
            return null;
        }

        var minAlong = double.PositiveInfinity;
        var maxAlong = double.NegativeInfinity;
        var minAcross = double.PositiveInfinity;
        var maxAcross = double.NegativeInfinity;

        foreach (var point in contour)
        {
            var along = ProjectAlong(point, featureAxis);
            var across = ProjectAcross(point, featureAxis);
            minAlong = Math.Min(minAlong, along);
            maxAlong = Math.Max(maxAlong, along);
            minAcross = Math.Min(minAcross, across);
            maxAcross = Math.Max(maxAcross, across);
        }

        var width = maxAlong - minAlong;
        var height = maxAcross - minAcross;
        return width <= 0.0001d || height <= 0.0001d
            ? null
            : Math.Clamp(height / width, 0d, 2d);
    }

    private static double CalculateAxisWidth(IReadOnlyList<Point> contour, ContourAxis axis)
    {
        var minAlong = double.PositiveInfinity;
        var maxAlong = double.NegativeInfinity;
        foreach (var point in contour)
        {
            var along = ProjectAlong(point, axis);
            minAlong = Math.Min(minAlong, along);
            maxAlong = Math.Max(maxAlong, along);
        }

        return maxAlong - minAlong;
    }

    private static ContourAxis? CreateAxis(Point first, Point opposite)
    {
        var axisX = opposite.X - first.X;
        var axisY = opposite.Y - first.Y;
        var axisLength = Math.Sqrt(axisX * axisX + axisY * axisY);
        if (axisLength <= 0.0001d)
        {
            return null;
        }

        axisX /= axisLength;
        axisY /= axisLength;
        return new ContourAxis(axisX, axisY, -axisY, axisX);
    }

    private static double ProjectAlong(Point point, ContourAxis axis)
    {
        return point.X * axis.AxisX + point.Y * axis.AxisY;
    }

    private static double ProjectAcross(Point point, ContourAxis axis)
    {
        return point.X * axis.CrossX + point.Y * axis.CrossY;
    }

    private readonly record struct ContourAxis(double AxisX, double AxisY, double CrossX, double CrossY);
}
