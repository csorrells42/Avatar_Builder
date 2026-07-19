using AvatarBuilder.Modules.Webcam.Common;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace AvatarBuilder.Modules.Webcam.DirectShow;

public sealed class DirectShowCameraControlService
{
    private const int CameraControlFlagAuto = 0x1;
    private const int CameraControlFlagManual = 0x2;
    private static readonly Guid SystemDeviceEnumClsid = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
    private static readonly Guid VideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
    private static readonly IReadOnlyList<(int Id, string Name)> CameraProperties =
    [
        (0, "Pan"),
        (1, "Tilt"),
        (2, "Roll"),
        (3, "Zoom"),
        (4, "Exposure"),
        (5, "Iris"),
        (6, "Focus")
    ];
    private static readonly IReadOnlyList<(int Id, string Name)> VideoProcAmpProperties =
    [
        (0, "Brightness"),
        (1, "Contrast"),
        (2, "Hue"),
        (3, "Saturation"),
        (4, "Sharpness"),
        (5, "Gamma"),
        (6, "Color Enable"),
        (7, "White Balance"),
        (8, "Backlight"),
        (9, "Gain")
    ];

    public IReadOnlyList<CameraControlItem> GetControls(CameraDevice camera)
    {
        return WithCameraFilter(camera, filter =>
        {
            var controls = new List<CameraControlItem>();
            if (filter is IAMCameraControl cameraControl)
            {
                foreach (var property in CameraProperties)
                {
                    if (TryReadCameraControl(cameraControl, property.Id, property.Name, out var item))
                    {
                        controls.Add(item);
                    }
                }
            }

            if (filter is IAMVideoProcAmp videoProcAmp)
            {
                foreach (var property in VideoProcAmpProperties)
                {
                    if (TryReadVideoProcAmpControl(videoProcAmp, property.Id, property.Name, out var item))
                    {
                        controls.Add(item);
                    }
                }
            }

            return controls;
        }) ?? [];
    }

    public bool SetControl(CameraDevice camera, CameraControlItem control, int value, bool isAuto)
    {
        return WithCameraFilter(camera, filter =>
        {
            var flags = isAuto ? CameraControlFlagAuto : CameraControlFlagManual;
            if (control.Kind == CameraControlKind.Camera && filter is IAMCameraControl cameraControl)
            {
                return cameraControl.Set(control.PropertyId, value, flags) == 0;
            }

            if (control.Kind == CameraControlKind.VideoProcAmp && filter is IAMVideoProcAmp videoProcAmp)
            {
                return videoProcAmp.Set(control.PropertyId, value, flags) == 0;
            }

            return false;
        });
    }

    private static bool TryReadCameraControl(IAMCameraControl cameraControl, int propertyId, string name, out CameraControlItem item)
    {
        item = null!;
        if (cameraControl.GetRange(propertyId, out var min, out var max, out var step, out var defaultValue, out var capsFlags) != 0)
        {
            return false;
        }

        if (cameraControl.Get(propertyId, out var value, out var flags) != 0)
        {
            value = defaultValue;
            flags = capsFlags;
        }

        item = new CameraControlItem(
            CameraControlKind.Camera,
            propertyId,
            name,
            min,
            max,
            step,
            defaultValue,
            value,
            (flags & CameraControlFlagAuto) != 0,
            (capsFlags & CameraControlFlagAuto) != 0);
        return true;
    }

    private static bool TryReadVideoProcAmpControl(IAMVideoProcAmp videoProcAmp, int propertyId, string name, out CameraControlItem item)
    {
        item = null!;
        if (videoProcAmp.GetRange(propertyId, out var min, out var max, out var step, out var defaultValue, out var capsFlags) != 0)
        {
            return false;
        }

        if (videoProcAmp.Get(propertyId, out var value, out var flags) != 0)
        {
            value = defaultValue;
            flags = capsFlags;
        }

        item = new CameraControlItem(
            CameraControlKind.VideoProcAmp,
            propertyId,
            name,
            min,
            max,
            step,
            defaultValue,
            value,
            (flags & CameraControlFlagAuto) != 0,
            (capsFlags & CameraControlFlagAuto) != 0);
        return true;
    }

    private static T? WithCameraFilter<T>(CameraDevice camera, Func<object, T> action)
    {
        object? systemDeviceEnum = null;
        IEnumMoniker? enumMoniker = null;
        object? filter = null;

        try
        {
            var enumType = Type.GetTypeFromCLSID(SystemDeviceEnumClsid, throwOnError: true)!;
            systemDeviceEnum = Activator.CreateInstance(enumType);
            if (systemDeviceEnum is not ICreateDevEnum createDevEnum)
            {
                return default;
            }

            var category = VideoInputDeviceCategory;
            if (createDevEnum.CreateClassEnumerator(ref category, out enumMoniker, 0) != 0 || enumMoniker is null)
            {
                return default;
            }

            var monikers = new IMoniker[1];
            while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
            {
                var moniker = monikers[0];
                try
                {
                    var name = ReadProperty(moniker, "FriendlyName");
                    var path = ReadProperty(moniker, "DevicePath") ?? GetDisplayName(moniker);
                    if (!CameraMatches(camera, name, path))
                    {
                        continue;
                    }

                    var baseFilterId = typeof(IBaseFilter).GUID;
                    moniker.BindToObject(null!, null, ref baseFilterId, out filter);
                    return filter is null ? default : action(filter);
                }
                finally
                {
                    Marshal.ReleaseComObject(moniker);
                }
            }

            return default;
        }
        catch
        {
            return default;
        }
        finally
        {
            if (filter is not null)
            {
                Marshal.ReleaseComObject(filter);
            }

            if (enumMoniker is not null)
            {
                Marshal.ReleaseComObject(enumMoniker);
            }

            if (systemDeviceEnum is not null)
            {
                Marshal.ReleaseComObject(systemDeviceEnum);
            }
        }
    }

    private static bool CameraMatches(CameraDevice camera, string? name, string? path)
    {
        return camera.EnumerateSourceDevices().Any(sourceDevice =>
            string.Equals(path, sourceDevice.DevicePath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, sourceDevice.Name, StringComparison.OrdinalIgnoreCase));
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
        catch
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
        catch
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

    [ComImport]
    [Guid("56A86895-0AD4-11CE-B03A-0020AF0BA770")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBaseFilter
    {
    }

    [ComImport]
    [Guid("C6E13370-30AC-11d0-A18C-00A0C9118956")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAMCameraControl
    {
        [PreserveSig]
        int GetRange(int property, out int min, out int max, out int steppingDelta, out int defaultValue, out int capsFlags);

        [PreserveSig]
        int Set(int property, int value, int flags);

        [PreserveSig]
        int Get(int property, out int value, out int flags);
    }

    [ComImport]
    [Guid("C6E13360-30AC-11d0-A18C-00A0C9118956")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAMVideoProcAmp
    {
        [PreserveSig]
        int GetRange(int property, out int min, out int max, out int steppingDelta, out int defaultValue, out int capsFlags);

        [PreserveSig]
        int Set(int property, int value, int flags);

        [PreserveSig]
        int Get(int property, out int value, out int flags);
    }
}
