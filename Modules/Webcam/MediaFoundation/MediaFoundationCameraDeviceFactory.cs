using AvatarBuilder.Modules.Webcam.Common;
using System.Runtime.InteropServices;

namespace AvatarBuilder.Modules.Webcam.MediaFoundation;

internal static class MediaFoundationCameraDeviceFactory
{
    private static readonly object StartupLock = new();
    private static int _startupReferences;

    public static MediaFoundationScope Startup()
    {
        lock (StartupLock)
        {
            if (_startupReferences == 0)
            {
                MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFStartup(
                    MediaFoundationInterop.MF_VERSION,
                    MediaFoundationInterop.MFSTARTUP_FULL));
            }

            _startupReferences++;
        }

        return new MediaFoundationScope();
    }

    public static IMFSourceReader CreateModeProbeReader(CameraDevice camera, out object mediaSource)
    {
        mediaSource = null!;
        var activate = FindCameraActivate(camera)
            ?? throw new InvalidOperationException($"Media Foundation could not find camera: {camera.Name}");

        try
        {
            mediaSource = CreateMediaSource(activate, camera.Name);
            MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateAttributes(out var attributes, 1));
            try
            {
                MediaFoundationInterop.ThrowIfFailed(attributes.SetUINT32(
                    MediaFoundationGuids.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS,
                    1));
                MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateSourceReaderFromMediaSource(
                    mediaSource,
                    attributes,
                    out var reader));
                return reader;
            }
            finally
            {
                MediaFoundationInterop.ReleaseComObject(attributes);
            }
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(activate);
        }
    }

    public static IMFSourceReader CreatePreviewReader(CameraDevice camera, CameraVideoMode? mode, out object mediaSource)
    {
        mediaSource = null!;
        var activate = FindCameraActivate(camera)
            ?? throw new InvalidOperationException($"Media Foundation could not find camera: {camera.Name}");

        try
        {
            mediaSource = CreateMediaSource(activate, camera.Name);
            var attributeResult = MediaFoundationInterop.MFCreateAttributes(out var attributes, 3);
            if (MediaFoundationInterop.Failed(attributeResult))
            {
                throw new InvalidOperationException($"Media Foundation source-reader attributes failed: 0x{attributeResult:X8}");
            }

            try
            {
                MediaFoundationInterop.ThrowIfFailed(attributes.SetUINT32(
                    MediaFoundationGuids.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS,
                    1));
                MediaFoundationInterop.ThrowIfFailed(attributes.SetUINT32(
                    MediaFoundationGuids.MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING,
                    1));
                var readerResult = MediaFoundationInterop.MFCreateSourceReaderFromMediaSource(
                    mediaSource,
                    attributes,
                    out var reader);
                if (MediaFoundationInterop.Failed(readerResult))
                {
                    throw new InvalidOperationException($"Media Foundation source-reader creation failed: 0x{readerResult:X8}");
                }

                try
                {
                    ConfigurePreviewReader(reader, mode);
                    return reader;
                }
                catch
                {
                    MediaFoundationInterop.ReleaseComObject(reader);
                    throw;
                }
            }
            finally
            {
                MediaFoundationInterop.ReleaseComObject(attributes);
            }
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(activate);
        }
    }

    public static IMFSourceReader CreateTextureSourceReader(
        string cameraName,
        CameraVideoMode? mode,
        IMFDXGIDeviceManager d3dManager,
        out object mediaSource,
        bool enableAdvancedVideoProcessing = true,
        Guid? preferredSubtype = null,
        bool configureMediaType = true)
    {
        return CreateTextureSourceReader(
            new CameraDevice(-1, cameraName, string.Empty),
            mode,
            d3dManager,
            out mediaSource,
            enableAdvancedVideoProcessing,
            preferredSubtype,
            configureMediaType);
    }

    public static IMFSourceReader CreateTextureSourceReader(
        CameraDevice camera,
        CameraVideoMode? mode,
        IMFDXGIDeviceManager d3dManager,
        out object mediaSource,
        bool enableAdvancedVideoProcessing = true,
        Guid? preferredSubtype = null,
        bool configureMediaType = true)
    {
        mediaSource = null!;
        var activate = FindCameraActivate(camera)
            ?? throw new InvalidOperationException($"Media Foundation could not find camera: {camera.Name}");

        try
        {
            mediaSource = CreateMediaSource(activate, camera.Name);
            MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateAttributes(out var attributes, 5));
            try
            {
                MediaFoundationInterop.ThrowIfFailed(attributes.SetUINT32(
                    MediaFoundationGuids.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS,
                    1));
                if (enableAdvancedVideoProcessing)
                {
                    MediaFoundationInterop.ThrowIfFailed(attributes.SetUINT32(
                        MediaFoundationGuids.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING,
                        1));
                }

                MediaFoundationInterop.ThrowIfFailed(attributes.SetUnknown(
                    MediaFoundationGuids.MF_SOURCE_READER_D3D_MANAGER,
                    d3dManager));

                MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateSourceReaderFromMediaSource(
                    mediaSource,
                    attributes,
                    out var reader));

                try
                {
                    if (configureMediaType)
                    {
                        ConfigureTextureReader(reader, mode, preferredSubtype);
                    }

                    return reader;
                }
                catch
                {
                    MediaFoundationInterop.ReleaseComObject(reader);
                    throw;
                }
            }
            finally
            {
                MediaFoundationInterop.ReleaseComObject(attributes);
            }
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(activate);
        }
    }

    public static object CreateMediaSource(IMFActivate activate, string cameraName)
    {
        var mediaSourceId = new Guid("279a808d-aec7-40c8-9c6b-a6b492c78a66");
        var activateResult = activate.ActivateObject(mediaSourceId, out var activatedSource);
        if (!MediaFoundationInterop.Failed(activateResult) && activatedSource is not null)
        {
            return activatedSource;
        }

        var symbolicLink = MediaFoundationInterop.GetAllocatedString(
            activate,
            MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);
        if (string.IsNullOrWhiteSpace(symbolicLink))
        {
            throw new InvalidOperationException($"Media Foundation camera activation failed for {cameraName}: 0x{activateResult:X8}");
        }

        MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateAttributes(out var attributes, 2));
        try
        {
            MediaFoundationInterop.ThrowIfFailed(attributes.SetGUID(
                MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
                MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID));
            MediaFoundationInterop.ThrowIfFailed(attributes.SetString(
                MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK,
                symbolicLink));
            var sourceResult = MediaFoundationInterop.MFCreateDeviceSource(attributes, out var mediaSource);
            if (MediaFoundationInterop.Failed(sourceResult) || mediaSource is null)
            {
                throw new InvalidOperationException($"Media Foundation device-source creation failed for {cameraName}: 0x{sourceResult:X8}");
            }

            return mediaSource;
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(attributes);
        }
    }

    public static IReadOnlyList<IMFActivate> EnumerateVideoActivates()
    {
        MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateAttributes(out var attributes, 1));
        try
        {
            MediaFoundationInterop.ThrowIfFailed(attributes.SetGUID(
                MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
                MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID));
            MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFEnumDeviceSources(
                attributes,
                out var activateArray,
                out var count));

            try
            {
                var devices = new List<IMFActivate>();
                for (var i = 0; i < count; i++)
                {
                    var activatePointer = Marshal.ReadIntPtr(activateArray, i * IntPtr.Size);
                    if (activatePointer != IntPtr.Zero)
                    {
                        devices.Add((IMFActivate)Marshal.GetObjectForIUnknown(activatePointer));
                        Marshal.Release(activatePointer);
                    }
                }

                return devices;
            }
            finally
            {
                Marshal.FreeCoTaskMem(activateArray);
            }
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(attributes);
        }
    }

    private static IMFActivate? FindCameraActivate(CameraDevice camera)
    {
        var candidates = EnumerateVideoActivates();
        IMFActivate? fallback = null;
        var requestedPhysicalKey = CameraDeviceCatalog.TryCreatePhysicalDeviceKey(camera);

        foreach (var activate in candidates)
        {
            var friendlyName = MediaFoundationInterop.GetAllocatedString(
                activate,
                MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME);
            var symbolicLink = MediaFoundationInterop.GetAllocatedString(
                activate,
                MediaFoundationGuids.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK);

            if (!string.IsNullOrWhiteSpace(camera.DevicePath)
                && string.Equals(symbolicLink, camera.DevicePath, StringComparison.OrdinalIgnoreCase))
            {
                ReleaseAllExcept(candidates, activate);
                return activate;
            }

            if (string.Equals(friendlyName, camera.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(symbolicLink, camera.Name, StringComparison.OrdinalIgnoreCase))
            {
                ReleaseAllExcept(candidates, activate);
                return activate;
            }

            var candidatePhysicalKey = string.IsNullOrWhiteSpace(symbolicLink)
                ? null
                : CameraDeviceCatalog.TryCreatePhysicalDeviceKey(new CameraDevice(-1, friendlyName ?? "", symbolicLink, "Media Foundation"));
            if (!string.IsNullOrWhiteSpace(requestedPhysicalKey)
                && string.Equals(candidatePhysicalKey, requestedPhysicalKey, StringComparison.OrdinalIgnoreCase))
            {
                ReleaseAllExcept(candidates, activate);
                return activate;
            }

            if (fallback is null
                && (friendlyName?.Contains(camera.Name, StringComparison.OrdinalIgnoreCase) == true
                    || symbolicLink?.Contains(camera.Name, StringComparison.OrdinalIgnoreCase) == true))
            {
                fallback = activate;
            }
        }

        if (fallback is not null)
        {
            ReleaseAllExcept(candidates, fallback);
        }
        else
        {
            foreach (var candidate in candidates)
            {
                MediaFoundationInterop.ReleaseComObject(candidate);
            }
        }

        return fallback;
    }

    private static void ReleaseAllExcept(IReadOnlyList<IMFActivate> candidates, IMFActivate keep)
    {
        foreach (var candidate in candidates)
        {
            if (!ReferenceEquals(candidate, keep))
            {
                MediaFoundationInterop.ReleaseComObject(candidate);
            }
        }
    }

    private static void ConfigurePreviewReader(IMFSourceReader reader, CameraVideoMode? mode)
    {
        if (mode is { IsAuto: false, Width: > 0, Height: > 0 })
        {
            if (TrySetSelectedPreviewMediaType(reader, mode))
            {
                return;
            }

            throw new InvalidOperationException($"Media Foundation could not keep selected mode {mode.Label}; trying fallback camera path.");
        }

        if (TrySetPreviewMediaType(reader, mode, MediaFoundationGuids.MFVideoFormat_NV12, requestFrameSize: true, requestFrameRate: true)
            || TrySetPreviewMediaType(reader, mode, MediaFoundationGuids.MFVideoFormat_NV12, requestFrameSize: false, requestFrameRate: false)
            || TrySetPreviewMediaType(reader, mode, MediaFoundationGuids.MFVideoFormat_RGB32, requestFrameSize: true, requestFrameRate: true)
            || TrySetPreviewMediaType(reader, mode, MediaFoundationGuids.MFVideoFormat_RGB32, requestFrameSize: false, requestFrameRate: false))
        {
            return;
        }

        throw new InvalidOperationException("Media Foundation could not configure NV12 or RGB32 preview frames.");
    }

    private static bool TrySetSelectedPreviewMediaType(IMFSourceReader reader, CameraVideoMode mode)
    {
        var subtypes = new[]
        {
            MediaFoundationGuids.MFVideoFormat_NV12,
            MediaFoundationGuids.MFVideoFormat_RGB32
        };

        foreach (var subtype in subtypes)
        {
            if (TrySetPreviewMediaType(reader, mode, subtype, requestFrameSize: true, requestFrameRate: true)
                && CurrentMatchesRequestedResolution(reader, mode))
            {
                return true;
            }
        }

        foreach (var subtype in subtypes)
        {
            if (TrySetPreviewMediaType(reader, mode, subtype, requestFrameSize: true, requestFrameRate: false)
                && CurrentMatchesRequestedResolution(reader, mode))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TrySetPreviewMediaType(
        IMFSourceReader reader,
        CameraVideoMode? mode,
        Guid subtype,
        bool requestFrameSize,
        bool requestFrameRate)
    {
        var width = mode?.Width ?? 1280;
        var height = mode?.Height ?? 720;
        var fps = mode?.FramesPerSecond ?? 30d;
        var (fpsNumerator, fpsDenominator) = CreateFrameRateRatio(fps);

        MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateMediaType(out var mediaType));
        try
        {
            MediaFoundationInterop.ThrowIfFailed(mediaType.SetGUID(
                MediaFoundationGuids.MF_MT_MAJOR_TYPE,
                MediaFoundationGuids.MFMediaType_Video));
            MediaFoundationInterop.ThrowIfFailed(mediaType.SetGUID(
                MediaFoundationGuids.MF_MT_SUBTYPE,
                subtype));
            MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT32(
                MediaFoundationGuids.MF_MT_INTERLACE_MODE,
                MediaFoundationInterop.MFVideoInterlace_Progressive));

            if (requestFrameSize)
            {
                MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT64(
                    MediaFoundationGuids.MF_MT_FRAME_SIZE,
                    MediaFoundationInterop.PackRatio(width, height)));
            }

            if (requestFrameRate)
            {
                MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT64(
                    MediaFoundationGuids.MF_MT_FRAME_RATE,
                    MediaFoundationInterop.PackRatio(fpsNumerator, fpsDenominator)));
            }

            return !MediaFoundationInterop.Failed(reader.SetCurrentMediaType(
                MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                IntPtr.Zero,
                mediaType));
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(mediaType);
        }
    }

    private static bool CurrentMatchesRequestedResolution(IMFSourceReader reader, CameraVideoMode mode)
    {
        var result = reader.GetCurrentMediaType(
            MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
            out var currentType);
        if (MediaFoundationInterop.Failed(result))
        {
            return false;
        }

        try
        {
            return MediaFoundationInterop.TryGetFrameSize(currentType, out var width, out var height)
                && width == mode.Width
                && height == mode.Height;
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(currentType);
        }
    }

    private static void ConfigureTextureReader(IMFSourceReader reader, CameraVideoMode? mode, Guid? preferredSubtype)
    {
        Guid[] subtypes = preferredSubtype is Guid selectedSubtype
            ? [selectedSubtype]
            : [MediaFoundationGuids.MFVideoFormat_NV12, MediaFoundationGuids.MFVideoFormat_P010];

        foreach (var candidateSubtype in subtypes)
        {
            if (TrySetTextureMediaType(reader, mode, candidateSubtype, exactMode: true)
                || TrySetTextureMediaType(reader, mode, candidateSubtype, exactMode: false))
            {
                return;
            }
        }
    }

    private static bool TrySetTextureMediaType(
        IMFSourceReader reader,
        CameraVideoMode? mode,
        Guid subtype,
        bool exactMode)
    {
        var width = mode?.Width ?? 1280;
        var height = mode?.Height ?? 720;
        var fps = mode?.FramesPerSecond ?? 30d;
        var (fpsNumerator, fpsDenominator) = CreateFrameRateRatio(fps);

        MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateMediaType(out var mediaType));
        try
        {
            MediaFoundationInterop.ThrowIfFailed(mediaType.SetGUID(
                MediaFoundationGuids.MF_MT_MAJOR_TYPE,
                MediaFoundationGuids.MFMediaType_Video));
            MediaFoundationInterop.ThrowIfFailed(mediaType.SetGUID(
                MediaFoundationGuids.MF_MT_SUBTYPE,
                subtype));
            MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT32(
                MediaFoundationGuids.MF_MT_INTERLACE_MODE,
                MediaFoundationInterop.MFVideoInterlace_Progressive));

            if (exactMode)
            {
                MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT64(
                    MediaFoundationGuids.MF_MT_FRAME_SIZE,
                    MediaFoundationInterop.PackRatio(width, height)));
                MediaFoundationInterop.ThrowIfFailed(mediaType.SetUINT64(
                    MediaFoundationGuids.MF_MT_FRAME_RATE,
                    MediaFoundationInterop.PackRatio(fpsNumerator, fpsDenominator)));
            }

            return !MediaFoundationInterop.Failed(reader.SetCurrentMediaType(
                MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                IntPtr.Zero,
                mediaType));
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(mediaType);
        }
    }

    private static (int Numerator, int Denominator) CreateFrameRateRatio(double fps)
    {
        if (Math.Abs(fps - 29.97d) < 0.02d)
        {
            return (30000, 1001);
        }

        if (Math.Abs(fps - 59.94d) < 0.02d)
        {
            return (60000, 1001);
        }

        return ((int)Math.Round(Math.Clamp(fps, 1d, 240d)), 1);
    }

    public sealed class MediaFoundationScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (StartupLock)
            {
                if (_startupReferences <= 0)
                {
                    return;
                }

                _startupReferences--;
                if (_startupReferences == 0)
                {
                    MediaFoundationInterop.MFShutdown();
                }
            }
        }
    }
}
