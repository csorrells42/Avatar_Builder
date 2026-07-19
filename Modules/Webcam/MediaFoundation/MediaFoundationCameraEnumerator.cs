using AvatarBuilder.Modules.Webcam.Common;
namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

public static class MediaFoundationCameraEnumerator
{
    public static IReadOnlyList<CameraDevice> GetVideoInputDevices()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        using var _ = MediaFoundationCameraDeviceFactory.Startup();
        var activates = MediaFoundationCameraDeviceFactory.EnumerateVideoActivates();
        var devices = new List<CameraDevice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var activate in activates)
        {
            try
            {
                var name = MediaFoundationInterop.GetAllocatedString(
                    activate,
                    MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
                var symbolicLink = MediaFoundationInterop.GetAllocatedString(
                    activate,
                    MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var key = string.IsNullOrWhiteSpace(symbolicLink) ? name : symbolicLink;
                if (seen.Add(key))
                {
                    devices.Add(new CameraDevice(devices.Count, name, symbolicLink ?? string.Empty, "Media Foundation"));
                }
            }
            finally
            {
                MediaFoundationInterop.ReleaseComObject(activate);
            }
        }

        return devices;
    }
}
