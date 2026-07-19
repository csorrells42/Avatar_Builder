namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed record Direct3D12PreviewDiagnostics(
    string PreviewPath,
    string DeviceDescription,
    string FrameFormat,
    int Width,
    int Height,
    double SourceFramesPerSecond,
    long SubmittedFrames,
    long RenderedFrames,
    long DroppedFrames,
    double RenderFramesPerSecond,
    bool DenoiseEnabled,
    double DenoiseStrength,
    bool ColorPolishEnabled,
    string RecordingMode,
    string? FallbackReason,
    long LastFrameNumber,
    DateTimeOffset? LastFrameUtc)
{
    public static Direct3D12PreviewDiagnostics Empty { get; } = new(
        "DX12 preview path pending",
        "DX12 preview not initialized",
        "none",
        0,
        0,
        0d,
        0,
        0,
        0,
        0d,
        false,
        0d,
        false,
        "not recording",
        null,
        0,
        null);

    public string FormatStatusLine()
    {
        var size = Width > 0 && Height > 0 ? $"{Width}x{Height}" : "no frame";
        var sourceFps = SourceFramesPerSecond > 0d ? $"{SourceFramesPerSecond:0.#} source fps" : "source fps unknown";
        var fallback = string.IsNullOrWhiteSpace(FallbackReason) ? string.Empty : $"; fallback: {FallbackReason}";
        var color = ColorPolishEnabled ? "color on" : "color off";
        var denoise = DenoiseEnabled ? $"denoise {DenoiseStrength:0.0}" : "denoise off";
        return $"{PreviewPath}; {FrameFormat}; {size}; {sourceFps}; render {RenderFramesPerSecond:0.#} fps; submitted {SubmittedFrames}; rendered {RenderedFrames}; dropped {DroppedFrames}; {denoise}; {color}; {RecordingMode}{fallback}";
    }
}

