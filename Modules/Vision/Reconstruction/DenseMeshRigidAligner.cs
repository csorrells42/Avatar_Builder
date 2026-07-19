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

        var targetByIndex = target.ToDictionary(static point => point.Index);
        var pairs = source
            .Where(point => targetByIndex.ContainsKey(point.Index))
            .Select(point => (Source: point, Target: targetByIndex[point.Index]))
            .ToList();
        if (pairs.Count < 3)
        {
            return source;
        }

        var sourceCenter = new CenterPoint(
            pairs.Average(static pair => pair.Source.X),
            pairs.Average(static pair => pair.Source.Y),
            pairs.Average(static pair => pair.Source.Z));
        var targetCenter = new CenterPoint(
            pairs.Average(static pair => pair.Target.X),
            pairs.Average(static pair => pair.Target.Y),
            pairs.Average(static pair => pair.Target.Z));
        var covariance = new double[3, 3];
        foreach (var pair in pairs)
        {
            var sourcePoint = new[]
            {
                pair.Source.X - sourceCenter.X,
                pair.Source.Y - sourceCenter.Y,
                pair.Source.Z - sourceCenter.Z
            };
            var targetPoint = new[]
            {
                pair.Target.X - targetCenter.X,
                pair.Target.Y - targetCenter.Y,
                pair.Target.Z - targetCenter.Z
            };
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    covariance[row, column] += sourcePoint[row] * targetPoint[column];
                }
            }
        }

        var rotation = CalculateRotation(covariance);
        return source.Select(point => Transform(point, sourceCenter, targetCenter, rotation)).ToList();
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
