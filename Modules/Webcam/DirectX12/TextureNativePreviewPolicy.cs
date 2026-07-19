using AvatarBuilder.Modules.Webcam.Common;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public static class TextureNativePreviewPolicy
{
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(20);
    private static readonly Dictionary<string, PreviewFailure> PreviewFailures = new(StringComparer.OrdinalIgnoreCase);

    public static bool CanUseNv12UploadFallback(
        string? mediaSubtype,
        int width,
        int height,
        byte[]? nv12PreviewBytes,
        int nv12PreviewStride)
    {
        if (string.IsNullOrWhiteSpace(mediaSubtype)
            || !mediaSubtype.Contains("NV12", StringComparison.OrdinalIgnoreCase)
            || width <= 0
            || height <= 0
            || nv12PreviewBytes is not { LongLength: > 0 }
            || nv12PreviewStride < width)
        {
            return false;
        }

        var uvHeight = (height + 1) / 2;
        var requiredLength = (long)nv12PreviewStride * height + (long)nv12PreviewStride * uvHeight;
        return requiredLength > 0 && nv12PreviewBytes.LongLength >= requiredLength;
    }

    public static bool TryGetPreviewFailure(CameraDevice camera, CameraVideoMode mode, out string reason)
    {
        var key = CreatePreviewFailureKey(camera, mode);
        if (!PreviewFailures.TryGetValue(key, out var failure))
        {
            reason = string.Empty;
            return false;
        }

        if (DateTime.UtcNow - failure.RecordedAtUtc > FailureCooldown)
        {
            PreviewFailures.Remove(key);
            reason = string.Empty;
            return false;
        }

        reason = failure.Reason;
        return true;
    }

    public static void RememberPreviewFailure(CameraDevice camera, CameraVideoMode mode, string reason)
    {
        PreviewFailures[CreatePreviewFailureKey(camera, mode)] = new PreviewFailure(
            DateTime.UtcNow,
            string.IsNullOrWhiteSpace(reason)
                ? "previous native DX12 texture preview attempt failed"
                : reason);
    }

    public static void ForgetPreviewFailure(CameraDevice camera, CameraVideoMode mode)
    {
        PreviewFailures.Remove(CreatePreviewFailureKey(camera, mode));
    }

    private static string CreatePreviewFailureKey(CameraDevice camera, CameraVideoMode mode)
    {
        var cameraKey = string.IsNullOrWhiteSpace(camera.DevicePath)
            ? $"{camera.Source}|{camera.Name}"
            : $"{camera.Source}|{camera.DevicePath}";
        var modeKey = mode.IsAuto
            ? "auto"
            : $"{mode.Width}x{mode.Height}@{mode.FramesPerSecond:0.###}";
        return $"{cameraKey}|{modeKey}";
    }

    private sealed record PreviewFailure(DateTime RecordedAtUtc, string Reason);
}
