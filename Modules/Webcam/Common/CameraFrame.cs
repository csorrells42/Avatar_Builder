namespace AvatarBuilder.Modules.Webcam.Common;

public sealed class CameraFrame
{
    public CameraFrame(byte[] bgraBytes, int width, int height, int stride)
        : this(bgraBytes, width, height, stride, null, 0, "bgra32")
    {
    }

    public CameraFrame(
        byte[] bgraBytes,
        int width,
        int height,
        int stride,
        byte[]? nv12Bytes,
        int nv12Stride,
        string format)
    {
        BgraBytes = bgraBytes;
        Width = width;
        Height = height;
        Stride = stride;
        Nv12Bytes = nv12Bytes;
        Nv12Stride = nv12Stride;
        Format = format;
    }

    public byte[] BgraBytes { get; }

    public byte[]? Nv12Bytes { get; }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public int Nv12Stride { get; }

    public string Format { get; }

    public bool HasBgra => BgraBytes.Length > 0 && Stride > 0;

    public bool HasNv12 => Nv12Bytes is { Length: > 0 } && Nv12Stride > 0;
}

