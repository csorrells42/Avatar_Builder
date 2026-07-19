using AvatarBuilder.Modules.Webcam.Common;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace AvatarBuilder.Modules.Webcam.DirectShow;

public static class DirectShowCameraEnumerator
{
    private static readonly Guid SystemDeviceEnumClsid = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
    private static readonly Guid VideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");

    public static IReadOnlyList<CameraDevice> GetVideoInputDevices()
    {
        var devices = new List<CameraDevice>();
        object? systemDeviceEnum = null;
        IEnumMoniker? enumMoniker = null;

        try
        {
            var enumType = Type.GetTypeFromCLSID(SystemDeviceEnumClsid, throwOnError: true)!;
            systemDeviceEnum = Activator.CreateInstance(enumType);
            if (systemDeviceEnum is not ICreateDevEnum createDevEnum)
            {
                return devices;
            }

            var category = VideoInputDeviceCategory;
            if (createDevEnum.CreateClassEnumerator(ref category, out enumMoniker, 0) != 0 || enumMoniker is null)
            {
                return devices;
            }

            var monikers = new IMoniker[1];
            var deviceNumber = 0;
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                try
                {
                    var name = ReadProperty(moniker, "FriendlyName") ?? "Camera";
                    var path = ReadProperty(moniker, "DevicePath") ?? GetDisplayName(moniker) ?? name;
                    devices.Add(new CameraDevice(deviceNumber, name, path, "DirectShow"));
                    deviceNumber++;
                }
                finally
                {
                    Marshal.ReleaseComObject(moniker);
                }
            }
        }
        catch (Exception)
        {
            return devices;
        }
        finally
        {
            if (enumMoniker is not null)
            {
                Marshal.ReleaseComObject(enumMoniker);
            }

            if (systemDeviceEnum is not null)
            {
                Marshal.ReleaseComObject(systemDeviceEnum);
            }
        }

        return devices;
    }

    private static string? ReadProperty(IMoniker moniker, string propertyName)
    {
        object? propertyBagObject = null;

        try
        {
            var bagId = typeof(IPropertyBag).GUID;
            moniker.BindToStorage(null!, null, ref bagId, out propertyBagObject);
            if (propertyBagObject is not IPropertyBag propertyBag)
            {
                return null;
            }

            propertyBag.Read(propertyName, out var value, IntPtr.Zero);
            return value as string;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (propertyBagObject is not null)
            {
                Marshal.ReleaseComObject(propertyBagObject);
            }
        }
    }

    private static string? GetDisplayName(IMoniker moniker)
    {
        try
        {
            moniker.GetDisplayName(null!, null, out var displayName);
            return displayName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [ComImport]
    [Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig]
        int CreateClassEnumerator(ref Guid deviceClass, out IEnumMoniker? enumMoniker, int flags);
    }

    [ComImport]
    [Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        void Read(
            [MarshalAs(UnmanagedType.LPWStr)] string propertyName,
            [MarshalAs(UnmanagedType.Struct)] out object? value,
            IntPtr errorLog);

        void Write(
            [MarshalAs(UnmanagedType.LPWStr)] string propertyName,
            [MarshalAs(UnmanagedType.Struct)] ref object value);
    }
}

