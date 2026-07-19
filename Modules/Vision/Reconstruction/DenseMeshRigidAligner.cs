using AvatarBuilder.Modules.Vision.Common;
using OpenCvSharp;

namespace AvatarBuilder.Modules.Vision.Reconstruction;

internal static class DenseMeshRigidAligner
{
    public static IReadOnlyList<FaceMeshLandmarkPoint> Align(
        IReadOnlyList<FaceMeshLandmarkPoint> source,
        IReadOnlyList<FaceMeshLandmarkPoint> target)
    {
        if (source.Count < 3 || target.Count < 3)
        {
            return source;
        }

        IReadOnlyList<FaceMeshLandmarkPoint> matchedSource;
        IReadOnlyList<FaceMeshLandmarkPoint> matchedTarget;
        if (HasMatchingIndexOrder(source, target))
        {
            matchedSource = source;
            matchedTarget = target;
        }
        else
        {
            var targetByIndex = target.ToDictionary(static point => point.Index);
            var sourceMatches = new List<FaceMeshLandmarkPoint>(Math.Min(source.Count, target.Count));
            var targetMatches = new List<FaceMeshLandmarkPoint>(sourceMatches.Capacity);
            foreach (var point in source)
            {
                if (targetByIndex.TryGetValue(point.Index, out var targetPoint))
                {
                    sourceMatches.Add(point);
                    targetMatches.Add(targetPoint);
                }
            }

            matchedSource = sourceMatches;
            matchedTarget = targetMatches;
        }

        if (matchedSource.Count < 3)
        {
            return source;
        }

        var sourceXTotal = 0d;
        var sourceYTotal = 0d;
        var sourceZTotal = 0d;
        var targetXTotal = 0d;
        var targetYTotal = 0d;
        var targetZTotal = 0d;
        for (var index = 0; index < matchedSource.Count; index++)
        {
            var sourcePoint = matchedSource[index];
            var targetPoint = matchedTarget[index];
            sourceXTotal += sourcePoint.X;
            sourceYTotal += sourcePoint.Y;
            sourceZTotal += sourcePoint.Z;
            targetXTotal += targetPoint.X;
            targetYTotal += targetPoint.Y;
            targetZTotal += targetPoint.Z;
        }

        var inverseCount = 1d / matchedSource.Count;
        var sourceCenter = new CenterPoint(
            sourceXTotal * inverseCount,
            sourceYTotal * inverseCount,
            sourceZTotal * inverseCount);
        var targetCenter = new CenterPoint(
            targetXTotal * inverseCount,
            targetYTotal * inverseCount,
            targetZTotal * inverseCount);
        var covariance = new double[3, 3];
        for (var index = 0; index < matchedSource.Count; index++)
        {
            var sourcePoint = matchedSource[index];
            var targetPoint = matchedTarget[index];
            var sourceX = sourcePoint.X - sourceCenter.X;
            var sourceY = sourcePoint.Y - sourceCenter.Y;
            var sourceZ = sourcePoint.Z - sourceCenter.Z;
            var targetX = targetPoint.X - targetCenter.X;
            var targetY = targetPoint.Y - targetCenter.Y;
            var targetZ = targetPoint.Z - targetCenter.Z;
            covariance[0, 0] += sourceX * targetX;
            covariance[0, 1] += sourceX * targetY;
            covariance[0, 2] += sourceX * targetZ;
            covariance[1, 0] += sourceY * targetX;
            covariance[1, 1] += sourceY * targetY;
            covariance[1, 2] += sourceY * targetZ;
            covariance[2, 0] += sourceZ * targetX;
            covariance[2, 1] += sourceZ * targetY;
            covariance[2, 2] += sourceZ * targetZ;
        }

        var rotation = CalculateRotation(covariance);
        var aligned = new List<FaceMeshLandmarkPoint>(source.Count);
        foreach (var point in source)
        {
            aligned.Add(Transform(point, sourceCenter, targetCenter, rotation));
        }

        return aligned;
    }

    private static bool HasMatchingIndexOrder(
        IReadOnlyList<FaceMeshLandmarkPoint> source,
        IReadOnlyList<FaceMeshLandmarkPoint> target)
    {
        if (source.Count != target.Count)
        {
            return false;
        }

        for (var index = 0; index < source.Count; index++)
        {
            if (source[index].Index != target[index].Index)
            {
                return false;
            }
        }

        return true;
    }

    private static double[,] CalculateRotation(double[,] covariance)
    {
        using var matrix = new Mat(3, 3, MatType.CV_64FC1);
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                matrix.Set(row, column, covariance[row, column]);
            }
        }

        using var singularValues = new Mat();
        using var u = new Mat();
        using var vt = new Mat();
        Cv2.SVDecomp(matrix, singularValues, u, vt);
        var rotation = Multiply(u, vt);
        if (Determinant(rotation) < 0d)
        {
            for (var row = 0; row < 3; row++)
            {
                u.Set(row, 2, -u.At<double>(row, 2));
            }

            rotation = Multiply(u, vt);
        }

        return rotation;
    }

    private static double[,] Multiply(Mat first, Mat second)
    {
        var result = new double[3, 3];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                for (var index = 0; index < 3; index++)
                {
                    result[row, column] += first.At<double>(row, index) * second.At<double>(index, column);
                }
            }
        }

        return result;
    }

    private static double Determinant(double[,] matrix)
    {
        return matrix[0, 0] * (matrix[1, 1] * matrix[2, 2] - matrix[1, 2] * matrix[2, 1])
            - matrix[0, 1] * (matrix[1, 0] * matrix[2, 2] - matrix[1, 2] * matrix[2, 0])
            + matrix[0, 2] * (matrix[1, 0] * matrix[2, 1] - matrix[1, 1] * matrix[2, 0]);
    }

    private static FaceMeshLandmarkPoint Transform(
        FaceMeshLandmarkPoint point,
        CenterPoint sourceCenter,
        CenterPoint targetCenter,
        double[,] rotation)
    {
        var x = point.X - sourceCenter.X;
        var y = point.Y - sourceCenter.Y;
        var z = point.Z - sourceCenter.Z;
        return new FaceMeshLandmarkPoint
        {
            Index = point.Index,
            X = x * rotation[0, 0] + y * rotation[1, 0] + z * rotation[2, 0] + targetCenter.X,
            Y = x * rotation[0, 1] + y * rotation[1, 1] + z * rotation[2, 1] + targetCenter.Y,
            Z = x * rotation[0, 2] + y * rotation[1, 2] + z * rotation[2, 2] + targetCenter.Z
        };
    }

    private readonly record struct CenterPoint(double X, double Y, double Z);
}
