using AvatarBuilder.Modules.Vision.Common;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

internal static class MediaPipeBrowOutlineGeometry
{
    private const double GeometryEpsilon = 1e-10;

    internal static readonly int[] BrowAIndices = [70, 63, 105, 66, 107, 55, 65, 52, 53, 46];
    internal static readonly int[] BrowBIndices = [336, 296, 334, 293, 300, 285, 295, 282, 283, 276];

    public static IReadOnlyList<int> BuildClosedOutlineIndices(
        IReadOnlyList<FaceMeshLandmarkPoint> meshPoints,
        IReadOnlyList<int> candidateIndices)
    {
        if (meshPoints.Count < 3 || candidateIndices.Count < 3)
        {
            return [];
        }

        var pointsByIndex = new Dictionary<int, FaceMeshLandmarkPoint>(meshPoints.Count);
        foreach (var point in meshPoints)
        {
            if (double.IsFinite(point.X) && double.IsFinite(point.Y))
            {
                pointsByIndex[point.Index] = point;
            }
        }

        var candidates = new List<IndexedPoint>(candidateIndices.Count);
        foreach (var pointIndex in candidateIndices)
        {
            if (pointsByIndex.TryGetValue(pointIndex, out var point))
            {
                candidates.Add(new IndexedPoint(pointIndex, point.X, point.Y));
            }
        }

        candidates.Sort(static (left, right) =>
        {
            var xOrder = left.X.CompareTo(right.X);
            if (xOrder != 0)
            {
                return xOrder;
            }

            var yOrder = left.Y.CompareTo(right.Y);
            return yOrder != 0 ? yOrder : left.Index.CompareTo(right.Index);
        });
        RemoveDuplicateCoordinates(candidates);
        if (candidates.Count < 3)
        {
            return [];
        }

        var hull = new IndexedPoint[candidates.Count * 2];
        var hullCount = 0;
        foreach (var point in candidates)
        {
            AppendHullPoint(hull, ref hullCount, point);
        }

        var lowerCount = hullCount;
        for (var index = candidates.Count - 2; index >= 0; index--)
        {
            while (hullCount > lowerCount
                   && hullCount >= 2
                   && Cross(hull[hullCount - 2], hull[hullCount - 1], candidates[index]) <= GeometryEpsilon)
            {
                hullCount--;
            }

            hull[hullCount++] = candidates[index];
        }

        hullCount--;
        if (hullCount < 3)
        {
            return [];
        }

        var outline = new int[hullCount];
        for (var index = 0; index < hullCount; index++)
        {
            outline[index] = hull[index].Index;
        }

        return TryValidateClosedOutline(meshPoints, outline, closed: true, out _)
            ? outline
            : [];
    }

    public static bool TryValidateClosedOutline(
        IReadOnlyList<FaceMeshLandmarkPoint> meshPoints,
        IReadOnlyList<int> outlineIndices,
        bool closed,
        out string failureReason)
    {
        if (!closed)
        {
            failureReason = "The eyebrow outline is open.";
            return false;
        }

        if (outlineIndices.Count < 3 || outlineIndices.Distinct().Count() != outlineIndices.Count)
        {
            failureReason = "The eyebrow outline needs at least three unique vertices.";
            return false;
        }

        var pointsByIndex = new Dictionary<int, FaceMeshLandmarkPoint>(meshPoints.Count);
        foreach (var point in meshPoints)
        {
            pointsByIndex[point.Index] = point;
        }

        var polygon = new IndexedPoint[outlineIndices.Count];
        for (var index = 0; index < outlineIndices.Count; index++)
        {
            if (!pointsByIndex.TryGetValue(outlineIndices[index], out var point)
                || !double.IsFinite(point.X)
                || !double.IsFinite(point.Y))
            {
                failureReason = $"Eyebrow vertex {outlineIndices[index]} is missing or invalid.";
                return false;
            }

            polygon[index] = new IndexedPoint(point.Index, point.X, point.Y);
            var previous = polygon[(index + polygon.Length - 1) % polygon.Length];
            if (index > 0 && SamePoint(previous, polygon[index]))
            {
                failureReason = "The eyebrow outline contains a zero-length edge.";
                return false;
            }
        }

        if (SamePoint(polygon[^1], polygon[0]))
        {
            failureReason = "The eyebrow outline contains a zero-length closing edge.";
            return false;
        }

        for (var firstEdge = 0; firstEdge < polygon.Length; firstEdge++)
        {
            var firstNext = (firstEdge + 1) % polygon.Length;
            for (var secondEdge = firstEdge + 1; secondEdge < polygon.Length; secondEdge++)
            {
                var secondNext = (secondEdge + 1) % polygon.Length;
                if (firstEdge == secondEdge
                    || firstNext == secondEdge
                    || secondNext == firstEdge)
                {
                    continue;
                }

                if (SegmentsIntersect(
                        polygon[firstEdge],
                        polygon[firstNext],
                        polygon[secondEdge],
                        polygon[secondNext]))
                {
                    failureReason = $"Eyebrow edges {firstEdge} and {secondEdge} cross.";
                    return false;
                }
            }
        }

        var signedAreaTwice = 0d;
        for (var index = 0; index < polygon.Length; index++)
        {
            var next = polygon[(index + 1) % polygon.Length];
            signedAreaTwice += polygon[index].X * next.Y - next.X * polygon[index].Y;
        }

        if (Math.Abs(signedAreaTwice) <= GeometryEpsilon)
        {
            failureReason = "The eyebrow outline has no enclosed area.";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static void RemoveDuplicateCoordinates(List<IndexedPoint> points)
    {
        var writeIndex = 1;
        for (var readIndex = 1; readIndex < points.Count; readIndex++)
        {
            if (!SamePoint(points[readIndex], points[writeIndex - 1]))
            {
                points[writeIndex++] = points[readIndex];
            }
        }

        if (writeIndex < points.Count)
        {
            points.RemoveRange(writeIndex, points.Count - writeIndex);
        }
    }

    private static void AppendHullPoint(IndexedPoint[] hull, ref int count, IndexedPoint point)
    {
        while (count >= 2 && Cross(hull[count - 2], hull[count - 1], point) <= GeometryEpsilon)
        {
            count--;
        }

        hull[count++] = point;
    }

    private static double Cross(IndexedPoint origin, IndexedPoint a, IndexedPoint b)
    {
        return (a.X - origin.X) * (b.Y - origin.Y)
            - (a.Y - origin.Y) * (b.X - origin.X);
    }

    private static bool SegmentsIntersect(IndexedPoint a, IndexedPoint b, IndexedPoint c, IndexedPoint d)
    {
        var abC = Cross(a, b, c);
        var abD = Cross(a, b, d);
        var cdA = Cross(c, d, a);
        var cdB = Cross(c, d, b);

        if (((abC > GeometryEpsilon && abD < -GeometryEpsilon)
             || (abC < -GeometryEpsilon && abD > GeometryEpsilon))
            && ((cdA > GeometryEpsilon && cdB < -GeometryEpsilon)
                || (cdA < -GeometryEpsilon && cdB > GeometryEpsilon)))
        {
            return true;
        }

        return (Math.Abs(abC) <= GeometryEpsilon && IsOnSegment(a, b, c))
            || (Math.Abs(abD) <= GeometryEpsilon && IsOnSegment(a, b, d))
            || (Math.Abs(cdA) <= GeometryEpsilon && IsOnSegment(c, d, a))
            || (Math.Abs(cdB) <= GeometryEpsilon && IsOnSegment(c, d, b));
    }

    private static bool IsOnSegment(IndexedPoint start, IndexedPoint end, IndexedPoint point)
    {
        return point.X >= Math.Min(start.X, end.X) - GeometryEpsilon
            && point.X <= Math.Max(start.X, end.X) + GeometryEpsilon
            && point.Y >= Math.Min(start.Y, end.Y) - GeometryEpsilon
            && point.Y <= Math.Max(start.Y, end.Y) + GeometryEpsilon;
    }

    private static bool SamePoint(IndexedPoint left, IndexedPoint right)
    {
        return Math.Abs(left.X - right.X) <= GeometryEpsilon
            && Math.Abs(left.Y - right.Y) <= GeometryEpsilon;
    }

    private readonly record struct IndexedPoint(int Index, double X, double Y);
}
