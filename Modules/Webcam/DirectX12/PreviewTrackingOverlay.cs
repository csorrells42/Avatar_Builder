namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed record PreviewTrackingOverlay(
    PreviewOverlayRect? FaceBox,
    PreviewOverlayRect? LeftEyeBox,
    PreviewOverlayRect? RightEyeBox,
    PreviewOverlayRect? MouthBox)
{
    public static PreviewTrackingOverlay Empty { get; } = new(null, null, null, null);

    public bool HasRegions => FaceBox is not null
        || LeftEyeBox is not null
        || RightEyeBox is not null
        || MouthBox is not null;
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
