namespace AvatarBuilder.Modules.Vision.Common;

public sealed class FaceMeshLandmarkPoint
{
    public int Index { get; init; }

    // A point owns position only. A/B/C orientation belongs to the containing
    // frame, pose bucket, or future local surface patch.
    public double X { get; init; }

    public double Y { get; init; }

    public double Z { get; init; }
}
