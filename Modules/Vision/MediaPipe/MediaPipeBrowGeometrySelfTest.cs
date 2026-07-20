using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

public static class MediaPipeBrowGeometrySelfTest
{
    public static MediaPipeBrowGeometrySelfTestResult Run()
    {
        try
        {
            VerifyGeneratedOutline("left", CreateBrow(mirror: false));
            VerifyGeneratedOutline("right", CreateBrow(mirror: true));

            var square = CreatePoints((0d, 0d), (1d, 0d), (1d, 1d), (0d, 1d));
            Require(
                !MediaPipeBrowOutlineGeometry.TryValidateClosedOutline(square, [0, 1, 2, 3], closed: false, out _),
                "An open eyebrow outline passed validation.");

            var bowTie = CreatePoints((0d, 0d), (1d, 1d), (0d, 1d), (1d, 0d));
            Require(
                !MediaPipeBrowOutlineGeometry.TryValidateClosedOutline(bowTie, [0, 1, 2, 3], closed: true, out var reason)
                && reason.Contains("cross", StringComparison.OrdinalIgnoreCase),
                "A self-crossing eyebrow outline passed validation.");

            return new MediaPipeBrowGeometrySelfTestResult(
                true,
                "PASS: mirrored brow hulls are closed and simple; open and crossing outlines are rejected.");
        }
        catch (Exception ex)
        {
            return new MediaPipeBrowGeometrySelfTestResult(false, $"FAIL: {ex.Message}");
        }
    }

    private static void VerifyGeneratedOutline(string name, IReadOnlyList<FaceMeshLandmarkPoint> points)
    {
        var candidates = points.Select(static point => point.Index).ToArray();
        var outline = MediaPipeBrowOutlineGeometry.BuildClosedOutlineIndices(points, candidates);
        Require(outline.Count >= 3, $"The {name} brow did not produce a polygon.");
        Require(
            MediaPipeBrowOutlineGeometry.TryValidateClosedOutline(points, outline, closed: true, out var reason),
            $"The {name} brow failed validation: {reason}");
    }

    private static IReadOnlyList<FaceMeshLandmarkPoint> CreateBrow(bool mirror)
    {
        var source = new (double X, double Y)[]
        {
            (0.05d, 0.58d), (0.18d, 0.28d), (0.38d, 0.10d), (0.62d, 0.08d), (0.88d, 0.30d),
            (0.92d, 0.52d), (0.68d, 0.43d), (0.45d, 0.40d), (0.24d, 0.48d), (0.10d, 0.62d)
        };
        return source
            .Select((point, index) => new FaceMeshLandmarkPoint
            {
                Index = index,
                X = mirror ? 1d - point.X : point.X,
                Y = point.Y
            })
            .ToArray();
    }

    private static IReadOnlyList<FaceMeshLandmarkPoint> CreatePoints(params (double X, double Y)[] points)
    {
        return points
            .Select((point, index) => new FaceMeshLandmarkPoint { Index = index, X = point.X, Y = point.Y })
            .ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

public sealed record MediaPipeBrowGeometrySelfTestResult(bool Succeeded, string Detail);
