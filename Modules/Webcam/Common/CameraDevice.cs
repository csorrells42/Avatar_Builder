namespace AvatarBuilder.Modules.Webcam.Common;

public sealed class CameraDevice
{
    public CameraDevice(
        int deviceNumber,
        string name,
        string devicePath,
        string source = "",
        CameraDevice? fallbackDevice = null)
    {
        DeviceNumber = deviceNumber;
        Name = name;
        DevicePath = devicePath;
        Source = source;
        FallbackDevice = fallbackDevice;
    }

    public int DeviceNumber { get; }

    public string Name { get; }

    public string DevicePath { get; }

    public string Source { get; }

    public CameraDevice? FallbackDevice { get; }

    public bool HasFallbackDevice => FallbackDevice is not null;

    public string DisplayName => HasFallbackDevice || string.IsNullOrWhiteSpace(Source)
        ? Name
        : $"{Name} ({Source})";

    public CameraDevice WithFallback(CameraDevice fallbackDevice)
    {
        return new CameraDevice(DeviceNumber, Name, DevicePath, Source, fallbackDevice);
    }

    public IEnumerable<CameraDevice> EnumerateSourceDevices()
    {
        yield return this;
        if (FallbackDevice is not null)
        {
            yield return FallbackDevice;
        }
    }

    public CameraDevice DirectShowDeviceOrSelf()
    {
        return EnumerateSourceDevices().FirstOrDefault(static device =>
            string.Equals(device.Source, "DirectShow", StringComparison.OrdinalIgnoreCase)) ?? this;
    }

    public override string ToString() => DisplayName;
}

