namespace AvatarBuilder.Modules.Webcam.Common;

public static class CameraDeviceCatalog
{
    public static IReadOnlyList<CameraDevice> MergeDevices(
        IReadOnlyList<CameraDevice> mediaFoundationDevices,
        IReadOnlyList<CameraDevice> directShowDevices)
    {
        var devices = new List<CameraDevice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mediaFoundationByPhysicalKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in mediaFoundationDevices)
        {
            if (!seen.Add(CreateExactKey(candidate)))
            {
                continue;
            }

            devices.Add(candidate);
            var physicalKey = TryCreatePhysicalDeviceKey(candidate);
            if (!string.IsNullOrWhiteSpace(physicalKey))
            {
                mediaFoundationByPhysicalKey.TryAdd(physicalKey, devices.Count - 1);
            }
        }

        foreach (var candidate in directShowDevices)
        {
            if (!seen.Add(CreateExactKey(candidate)))
            {
                continue;
            }

            var physicalKey = TryCreatePhysicalDeviceKey(candidate);
            if (!string.IsNullOrWhiteSpace(physicalKey)
                && mediaFoundationByPhysicalKey.TryGetValue(physicalKey, out var primaryIndex))
            {
                var primary = devices[primaryIndex];
                if (primary.FallbackDevice is null)
                {
                    devices[primaryIndex] = primary.WithFallback(candidate);
                }

                continue;
            }

            devices.Add(candidate);
        }

        return devices;
    }

    public static string? TryCreatePhysicalDeviceKey(CameraDevice camera)
    {
        var path = camera.DevicePath.Trim();
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith("@device:sw:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = path.Replace('/', '\\').ToLowerInvariant();
        var interfaceStart = normalized.IndexOf("#{", StringComparison.Ordinal);
        if (interfaceStart > 0)
        {
            normalized = normalized[..interfaceStart];
        }

        return normalized.Contains('#', StringComparison.Ordinal)
            ? normalized
            : null;
    }

    private static string CreateExactKey(CameraDevice camera)
    {
        return string.IsNullOrWhiteSpace(camera.DevicePath)
            ? $"name:{camera.Name}|source:{camera.Source}"
            : $"path:{camera.DevicePath}|source:{camera.Source}";
    }
}
