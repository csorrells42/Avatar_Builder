using System.Runtime.InteropServices;
using System.Text;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

internal static class MediaFoundationInterop
{
    public const int MF_VERSION = 0x00020070;
    public const int MFSTARTUP_FULL = 0;
    public const int MF_SOURCE_READER_ANY_STREAM = unchecked((int)0xfffffffe);
    public const int MF_SOURCE_READER_MEDIASOURCE = unchecked((int)0xffffffff);
    public const int MF_SOURCE_READER_FIRST_VIDEO_STREAM = unchecked((int)0xfffffffc);
    public const int MF_SOURCE_READERF_ENDOFSTREAM = 0x00000002;
    public const int MF_E_NO_MORE_TYPES = unchecked((int)0xc00d36b9);
    public const int MFVideoInterlace_Progressive = 2;
    public const long TicksPerSecond = 10_000_000L;

    public static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    public static bool Failed(int result) => result < 0;

    public static long PackRatio(int numerator, int denominator)
    {
        return ((long)numerator << 32) | (uint)Math.Max(1, denominator);
    }

    public static void ReleaseComObject(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.ReleaseComObject(instance);
        }
    }

    public static string? GetAllocatedString(IMFAttributes attributes, Guid key)
    {
        var result = attributes.GetAllocatedString(key, out var valuePointer, out var length);
        if (Failed(result) || valuePointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(valuePointer, length);
        }
        finally
        {
            CoTaskMemFree(valuePointer);
        }
    }

    public static string? GetString(IMFAttributes attributes, Guid key)
    {
        if (Failed(attributes.GetStringLength(key, out var length)))
        {
            return null;
        }

        var builder = new StringBuilder(length + 1);
        return Failed(attributes.GetString(key, builder, length + 1, out _))
            ? null
            : builder.ToString();
    }

    public static bool TryGetFrameSize(IMFAttributes attributes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (Failed(attributes.GetUINT64(MediaFoundationGuids.MF_MT_FRAME_SIZE, out var packed)))
        {
            return false;
        }

        width = (int)(packed >> 32);
        height = (int)(packed & 0xffffffff);
        return width > 0 && height > 0;
    }

    public static bool TryGetFrameRate(IMFAttributes attributes, out double framesPerSecond)
    {
        framesPerSecond = 0;
        if (Failed(attributes.GetUINT64(MediaFoundationGuids.MF_MT_FRAME_RATE, out var packed)))
        {
            return false;
        }

        var numerator = (int)(packed >> 32);
        var denominator = (int)(packed & 0xffffffff);
        if (numerator <= 0 || denominator <= 0)
        {
            return false;
        }

        framesPerSecond = numerator / (double)denominator;
        return framesPerSecond > 0;
    }

    public static string FormatSubtype(Guid subtype)
    {
        return subtype == MediaFoundationGuids.MFVideoFormat_RGB32 ? "rgb32"
            : subtype == MediaFoundationGuids.MFVideoFormat_NV12 ? "nv12"
            : subtype == MediaFoundationGuids.MFVideoFormat_P010 ? "p010"
            : subtype == MediaFoundationGuids.MFVideoFormat_H264 ? "h264"
            : TryFormatFourCc(subtype, out var fourCc) ? fourCc
            : subtype.ToString("N")[..8];
    }

    private static bool TryFormatFourCc(Guid subtype, out string value)
    {
        value = string.Empty;
        var guidBytes = subtype.ToByteArray();
        var data2 = BitConverter.ToUInt16(guidBytes, 4);
        var data3 = BitConverter.ToUInt16(guidBytes, 6);
        if (data2 != 0 || data3 != 0x0010)
        {
            return false;
        }

        var bytes = guidBytes[..4];
        if (bytes.Any(character => character < 32 || character > 126))
        {
            return false;
        }

        value = Encoding.ASCII.GetString(bytes).Trim().ToLowerInvariant();
        return !string.IsNullOrWhiteSpace(value);
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IMFMediaType mediaType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateDXGIDeviceManager(out int resetToken, out IMFDXGIDeviceManager deviceManager);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFGetDXGIDeviceManageMode(
        [MarshalAs(UnmanagedType.IUnknown)] object deviceManager,
        out int mode);

    [DllImport("mf.dll", ExactSpelling = true)]
    public static extern int MFEnumDeviceSources(
        IMFAttributes attributes,
        out IntPtr activateArray,
        out int count);

    [DllImport("mf.dll", ExactSpelling = true)]
    public static extern int MFCreateDeviceSource(
        IMFAttributes attributes,
        [MarshalAs(UnmanagedType.IUnknown)] out object? mediaSource);

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    public static extern int MFCreateSourceReaderFromMediaSource(
        [MarshalAs(UnmanagedType.IUnknown)] object mediaSource,
        IMFAttributes? attributes,
        out IMFSourceReader reader);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MFCreateSourceReaderFromURL(
        string sourceUrl,
        IMFAttributes? attributes,
        out IMFSourceReader reader);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MFCreateSinkWriterFromURL(
        string outputUrl,
        IntPtr byteStream,
        IMFAttributes? attributes,
        out IMFSinkWriter sinkWriter);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoTaskMemFree(IntPtr pointer);

    [DllImport("d3d12.dll", ExactSpelling = true)]
    public static extern int D3D12CreateDevice(
        IntPtr adapter,
        int minimumFeatureLevel,
        in Guid riid,
        out IntPtr device);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    public static extern int D3D11CreateDevice(
        IntPtr adapter,
        int driverType,
        IntPtr software,
        int flags,
        int[]? featureLevels,
        int featureLevelsCount,
        int sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr immediateContext);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
internal interface IMFAttributes
{
    [PreserveSig] int GetItem(in Guid guidKey, IntPtr value);
    [PreserveSig] int GetItemType(in Guid guidKey, out int type);
    [PreserveSig] int CompareItem(in Guid guidKey, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] int Compare(IMFAttributes? theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] int GetUINT32(in Guid guidKey, out int value);
    [PreserveSig] int GetUINT64(in Guid guidKey, out long value);
    [PreserveSig] int GetDouble(in Guid guidKey, out double value);
    [PreserveSig] int GetGUID(in Guid guidKey, out Guid value);
    [PreserveSig] int GetStringLength(in Guid guidKey, out int length);
    [PreserveSig] int GetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int bufferSize, out int length);
    [PreserveSig] int GetAllocatedString(in Guid guidKey, out IntPtr value, out int length);
    [PreserveSig] int GetBlobSize(in Guid guidKey, out int blobSize);
    [PreserveSig] int GetBlob(in Guid guidKey, IntPtr buffer, int bufferSize, out int blobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out IntPtr buffer, out int size);
    [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? value);
    [PreserveSig] int SetItem(in Guid guidKey, IntPtr value);
    [PreserveSig] int DeleteItem(in Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid guidKey, int value);
    [PreserveSig] int SetUINT64(in Guid guidKey, long value);
    [PreserveSig] int SetDouble(in Guid guidKey, double value);
    [PreserveSig] int SetGUID(in Guid guidKey, in Guid value);
    [PreserveSig] int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(in Guid guidKey, IntPtr buffer, int bufferSize);
    [PreserveSig] int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object? value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int items);
    [PreserveSig] int GetItemByIndex(int index, out Guid guidKey, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
internal interface IMFMediaType : IMFAttributes
{
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67")]
internal interface IMFActivate : IMFAttributes
{
    [PreserveSig] int ActivateObject(in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? objectInstance);
    [PreserveSig] int ShutdownObject();
    [PreserveSig] int DetachObject();
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
internal interface IMFSample
{
    [PreserveSig] int GetItem(in Guid guidKey, IntPtr value);
    [PreserveSig] int GetItemType(in Guid guidKey, out int type);
    [PreserveSig] int CompareItem(in Guid guidKey, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] int Compare(IMFAttributes? theirs, int matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] int GetUINT32(in Guid guidKey, out int value);
    [PreserveSig] int GetUINT64(in Guid guidKey, out long value);
    [PreserveSig] int GetDouble(in Guid guidKey, out double value);
    [PreserveSig] int GetGUID(in Guid guidKey, out Guid value);
    [PreserveSig] int GetStringLength(in Guid guidKey, out int length);
    [PreserveSig] int GetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int bufferSize, out int length);
    [PreserveSig] int GetAllocatedString(in Guid guidKey, out IntPtr value, out int length);
    [PreserveSig] int GetBlobSize(in Guid guidKey, out int blobSize);
    [PreserveSig] int GetBlob(in Guid guidKey, IntPtr buffer, int bufferSize, out int blobSize);
    [PreserveSig] int GetAllocatedBlob(in Guid guidKey, out IntPtr buffer, out int size);
    [PreserveSig] int GetUnknown(in Guid guidKey, in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? value);
    [PreserveSig] int SetItem(in Guid guidKey, IntPtr value);
    [PreserveSig] int DeleteItem(in Guid guidKey);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid guidKey, int value);
    [PreserveSig] int SetUINT64(in Guid guidKey, long value);
    [PreserveSig] int SetDouble(in Guid guidKey, double value);
    [PreserveSig] int SetGUID(in Guid guidKey, in Guid value);
    [PreserveSig] int SetString(in Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(in Guid guidKey, IntPtr buffer, int bufferSize);
    [PreserveSig] int SetUnknown(in Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object? value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out int items);
    [PreserveSig] int GetItemByIndex(int index, out Guid guidKey, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);
    [PreserveSig] int GetSampleFlags(out int sampleFlags);
    [PreserveSig] int SetSampleFlags(int sampleFlags);
    [PreserveSig] int GetSampleTime(out long sampleTime);
    [PreserveSig] int SetSampleTime(long sampleTime);
    [PreserveSig] int GetSampleDuration(out long sampleDuration);
    [PreserveSig] int SetSampleDuration(long sampleDuration);
    [PreserveSig] int GetBufferCount(out int bufferCount);
    [PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    [PreserveSig] int RemoveBufferByIndex(int index);
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out int totalLength);
    [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("045FA593-8799-42b8-BC8D-8968C6453507")]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out int currentLength);
    [PreserveSig] int SetCurrentLength(int currentLength);
    [PreserveSig] int GetMaxLength(out int maxLength);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("e7174cfa-1c9e-48b1-8866-626226bfc258")]
internal interface IMFDXGIBuffer
{
    [PreserveSig] int GetResource(in Guid riid, out IntPtr resource);
    [PreserveSig] int GetSubresourceIndex(out int subresource);
    [PreserveSig] int GetUnknown(in Guid guid, in Guid riid, out IntPtr unknown);
    [PreserveSig] int SetUnknown(in Guid guid, [MarshalAs(UnmanagedType.IUnknown)] object? unknown);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993")]
internal interface IMFSourceReader
{
    [PreserveSig] int GetStreamSelection(int streamIndex, [MarshalAs(UnmanagedType.Bool)] out bool selected);
    [PreserveSig] int SetStreamSelection(int streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
    [PreserveSig] int GetNativeMediaType(int streamIndex, int mediaTypeIndex, out IMFMediaType mediaType);
    [PreserveSig] int GetCurrentMediaType(int streamIndex, out IMFMediaType mediaType);
    [PreserveSig] int SetCurrentMediaType(int streamIndex, IntPtr reserved, IMFMediaType mediaType);
    [PreserveSig] int SetCurrentPosition(in Guid timeFormat, IntPtr position);
    [PreserveSig] int ReadSample(
        int streamIndex,
        int controlFlags,
        out int actualStreamIndex,
        out int streamFlags,
        out long timestamp,
        [MarshalAs(UnmanagedType.IUnknown)] out object? sample);
    [PreserveSig] int Flush(int streamIndex);
    [PreserveSig] int GetServiceForStream(int streamIndex, in Guid service, in Guid riid, out IntPtr value);
    [PreserveSig] int GetPresentationAttribute(int streamIndex, in Guid attribute, IntPtr value);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d")]
internal interface IMFSinkWriter
{
    [PreserveSig] int AddStream(IMFMediaType targetMediaType, out int streamIndex);
    [PreserveSig] int SetInputMediaType(int streamIndex, IMFMediaType inputMediaType, IMFAttributes? encodingParameters);
    [PreserveSig] int BeginWriting();
    [PreserveSig] int WriteSample(int streamIndex, IMFSample sample);
    [PreserveSig] int SendStreamTick(int streamIndex, long timestamp);
    [PreserveSig] int PlaceMarker(int streamIndex, IntPtr context);
    [PreserveSig] int NotifyEndOfSegment(int streamIndex);
    [PreserveSig] int Flush(int streamIndex);
    [PreserveSig] int Finalize_();
    [PreserveSig] int GetServiceForStream(int streamIndex, in Guid service, in Guid riid, out IntPtr value);
    [PreserveSig] int GetStatistics(int streamIndex, IntPtr statistics);
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("eb533d5d-2db6-40f8-97a9-494692014f07")]
internal interface IMFDXGIDeviceManager
{
    [PreserveSig] int CloseDeviceHandle(IntPtr deviceHandle);
    [PreserveSig] int GetVideoService(IntPtr deviceHandle, in Guid riid, out IntPtr service);
    [PreserveSig] int LockDevice(IntPtr deviceHandle, in Guid riid, out IntPtr device, [MarshalAs(UnmanagedType.Bool)] bool block);
    [PreserveSig] int OpenDeviceHandle(out IntPtr deviceHandle);
    [PreserveSig] int ResetDevice(IntPtr device, int resetToken);
    [PreserveSig] int TestDevice(IntPtr deviceHandle);
    [PreserveSig] int UnlockDevice(IntPtr deviceHandle, [MarshalAs(UnmanagedType.Bool)] bool saveState);
}
