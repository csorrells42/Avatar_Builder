namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed record PreviewTrackingOverlay
{
    public static PreviewTrackingOverlay Empty { get; } = new();

    public PreviewOverlayRect? FaceBox { get; init; }

    public PreviewOverlayRect? LeftEyeBox { get; init; }

    public PreviewOverlayRect? RightEyeBox { get; init; }

    public PreviewOverlayRect? MouthBox { get; init; }

    public PreviewOverlayPolyline? FaceContour { get; init; }

    public PreviewOverlayPolyline? JawContour { get; init; }

    public PreviewOverlayPolyline? LeftEyeContour { get; init; }

    public PreviewOverlayPolyline? RightEyeContour { get; init; }

    public PreviewOverlayPolyline? LeftBrowContour { get; init; }

    public PreviewOverlayPolyline? RightBrowContour { get; init; }

    public PreviewOverlayPolyline? OuterLipContour { get; init; }

    public PreviewOverlayPolyline? InnerLipContour { get; init; }

    public bool HasContent => FaceBox is not null
        || LeftEyeBox is not null
        || RightEyeBox is not null
        || MouthBox is not null
        || FaceContour is not null
        || JawContour is not null
        || LeftEyeContour is not null
        || RightEyeContour is not null
        || LeftBrowContour is not null
        || RightBrowContour is not null
        || OuterLipContour is not null
        || InnerLipContour is not null;
}

public sealed record PreviewOverlayPolyline(
    IReadOnlyList<PreviewOverlayPoint> Points,
    bool Closed,
    bool Inferred = false);

public readonly record struct PreviewOverlayPoint(double X, double Y)
{
    public PreviewOverlayPoint Clamp()
    {
        return new PreviewOverlayPoint(
            Math.Clamp(X, 0d, 1d),
            Math.Clamp(Y, 0d, 1d));
    }
}

public readonly record struct PreviewOverlayRect(double Left, double Top, double Right, double Bottom)
{
    public PreviewOverlayRect Clamp()
    {
        var left = Math.Clamp(Math.Min(Left, Right), 0d, 1d);
        var top = Math.Clamp(Math.Min(Top, Bottom), 0d, 1d);
        var right = Math.Clamp(Math.Max(Left, Right), 0d, 1d);
        var bottom = Math.Clamp(Math.Max(Top, Bottom), 0d, 1d);
        return new PreviewOverlayRect(left, top, right, bottom);
    }
}
