namespace AvatarBuilder.Modules.Webcam.Common;

public sealed class VideoFrameDenoiser
{
    private byte[]? _previousFrame;

    public void Reset()
    {
        _previousFrame = null;
    }

    public void Apply(byte[] current, double strength)
    {
        ApplyTemporalDenoise(current, strength, ref _previousFrame);
    }

    public static void ApplyTemporalDenoise(byte[] current, double strength, ref byte[]? previousFrame)
    {
        var previous = previousFrame;
        if (previous is null || previous.Length != current.Length)
        {
            previousFrame = (byte[])current.Clone();
            return;
        }

        var previousWeight = (int)Math.Round(Math.Clamp(strength / 12d, 0.05d, 0.62d) * 256d);
        var currentWeight = 256 - previousWeight;
        for (var i = 0; i < current.Length; i += 4)
        {
            current[i] = Blend(current[i], previous[i], currentWeight, previousWeight);
            current[i + 1] = Blend(current[i + 1], previous[i + 1], currentWeight, previousWeight);
            current[i + 2] = Blend(current[i + 2], previous[i + 2], currentWeight, previousWeight);
            current[i + 3] = 255;
        }

        Buffer.BlockCopy(current, 0, previous, 0, current.Length);
    }

    private static byte Blend(byte current, byte previous, int currentWeight, int previousWeight)
    {
        return (byte)((current * currentWeight + previous * previousWeight + 128) >> 8);
    }
}
