namespace EpisodeMonitor.Video;

public sealed class CameraDevice
{
    public CameraDevice(int deviceNumber, string name, string devicePath)
    {
        DeviceNumber = deviceNumber;
        Name = name;
        DevicePath = devicePath;
    }

    public int DeviceNumber { get; }

    public string Name { get; }

    public string DevicePath { get; }

    public override string ToString() => Name;
}

