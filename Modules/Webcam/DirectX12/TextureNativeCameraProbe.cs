using System.Runtime.InteropServices;
using AvatarBuilder.Modules.Webcam.Common;
using AvatarBuilder.Modules.Webcam.DirectX11;
using AvatarBuilder.Modules.Webcam.MediaFoundation;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed record TextureNativeCameraProbeResult(
    bool D3D12ManagerReady,
    string D3D12Mode,
    string MediaSubtype,
    int Width,
    int Height,
    double FramesPerSecond,
    int SamplesRead,
    int DxgiBackedSamples,
    int D3D12ResourceSamples,
    string Status);

public static class TextureNativeCameraProbe
{
    public static Task<TextureNativeCameraProbeResult> RunAsync(
        string cameraName,
        CameraVideoMode? mode,
        int requestedSamples,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => Run(cameraName, mode, Math.Clamp(requestedSamples, 1, 240), cancellationToken),
            cancellationToken);
    }

    private static TextureNativeCameraProbeResult Run(
        string cameraName,
        CameraVideoMode? mode,
        int requestedSamples,
        CancellationToken cancellationToken)
    {
        using var _ = MediaFoundationCameraDeviceFactory.Startup();
        var attempts = new[]
        {
            new TextureProbeAttempt("advanced nv12", true, MediaFoundationGuids.MFVideoFormat_NV12, true),
            new TextureProbeAttempt("basic nv12", false, MediaFoundationGuids.MFVideoFormat_NV12, true),
            new TextureProbeAttempt("advanced p010", true, MediaFoundationGuids.MFVideoFormat_P010, true),
            new TextureProbeAttempt("basic p010", false, MediaFoundationGuids.MFVideoFormat_P010, true),
            new TextureProbeAttempt("advanced native", true, null, false),
            new TextureProbeAttempt("basic native", false, null, false)
        };
        var failedAttempts = new List<string>();
        var productionAttempt = attempts.Take(1).ToArray();
        var exploratoryAttempts = attempts.Skip(1).ToArray();

        using (var d3d12 = Direct3D12DeviceManager.Create())
        {
            if (TryRunAttempts(
                cameraName,
                mode,
                requestedSamples,
                cancellationToken,
                d3d12.Manager,
                d3d12.ModeName,
                Direct3D12DeviceManager.ID3D12Resource,
                "D3D12",
                productionAttempt,
                failedAttempts,
                "D3D12",
                out var result))
            {
                return result;
            }
        }

        using (var d3d11 = Direct3D11DeviceManager.Create())
        {
            if (TryRunAttempts(
                cameraName,
                mode,
                requestedSamples,
                cancellationToken,
                d3d11.Manager,
                d3d11.ModeName,
                Direct3D11DeviceManager.ID3D11Texture2D,
                "D3D11 texture bridge",
                productionAttempt,
                failedAttempts,
                "D3D11 bridge",
                out var result))
            {
                return result;
            }
        }

        using (var d3d12 = Direct3D12DeviceManager.Create())
        {
            if (TryRunAttempts(
                cameraName,
                mode,
                requestedSamples,
                cancellationToken,
                d3d12.Manager,
                d3d12.ModeName,
                Direct3D12DeviceManager.ID3D12Resource,
                "D3D12",
                exploratoryAttempts,
                failedAttempts,
                "D3D12",
                out var result))
            {
                return result;
            }
        }

        using (var d3d11 = Direct3D11DeviceManager.Create())
        {
            if (TryRunAttempts(
                cameraName,
                mode,
                requestedSamples,
                cancellationToken,
                d3d11.Manager,
                d3d11.ModeName,
                Direct3D11DeviceManager.ID3D11Texture2D,
                "D3D11 texture bridge",
                exploratoryAttempts,
                failedAttempts,
                "D3D11 bridge",
                out var result))
            {
                return result;
            }
        }

        return new TextureNativeCameraProbeResult(
            D3D12ManagerReady: true,
            D3D12Mode: "none",
            MediaSubtype: "unknown",
            Width: mode?.Width ?? 0,
            Height: mode?.Height ?? 0,
            FramesPerSecond: mode?.FramesPerSecond ?? 0d,
            SamplesRead: 0,
            DxgiBackedSamples: 0,
            D3D12ResourceSamples: 0,
            Status: string.Join(" | ", failedAttempts));
    }

    private static bool TryRunAttempts(
        string cameraName,
        CameraVideoMode? mode,
        int requestedSamples,
        CancellationToken cancellationToken,
        IMFDXGIDeviceManager deviceManager,
        string deviceMode,
        Guid resourceId,
        string resourceLabel,
        IEnumerable<TextureProbeAttempt> attempts,
        List<string> failedAttempts,
        string attemptGroupName,
        out TextureNativeCameraProbeResult successfulResult)
    {
        foreach (var attempt in attempts)
        {
            var result = TryRunAttempt(
                cameraName,
                mode,
                requestedSamples,
                cancellationToken,
                deviceManager,
                deviceMode,
                resourceId,
                resourceLabel,
                attempt);
            if (result.D3D12ResourceSamples > 0)
            {
                successfulResult = result with { Status = $"{result.Status} Attempt: {attempt.Name}." };
                return true;
            }

            failedAttempts.Add($"{attemptGroupName} {attempt.Name}: {result.Status}");
        }

        successfulResult = default!;
        return false;
    }

    private static TextureNativeCameraProbeResult TryRunAttempt(
        string cameraName,
        CameraVideoMode? mode,
        int requestedSamples,
        CancellationToken cancellationToken,
        IMFDXGIDeviceManager deviceManager,
        string deviceMode,
        Guid resourceId,
        string resourceLabel,
        TextureProbeAttempt attempt)
    {
        object? mediaSource = null;
        IMFSourceReader? reader = null;
        try
        {
            reader = MediaFoundationCameraDeviceFactory.CreateTextureSourceReader(
                cameraName,
                mode,
                deviceManager,
                out mediaSource,
                attempt.EnableAdvancedVideoProcessing,
                attempt.PreferredSubtype,
                attempt.ConfigureMediaType);

            var (width, height, fps, subtype) = ReadCurrentFormat(reader, mode);
            var samplesRead = 0;
            var dxgiBackedSamples = 0;
            var d3d12ResourceSamples = 0;

            while (samplesRead < requestedSamples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = reader.ReadSample(
                    MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
                    0,
                    out var actualStreamIndex,
                    out var streamFlags,
                    out var timestamp,
                    out var sampleObject);

                if (MediaFoundationInterop.Failed(result))
                {
                    return new TextureNativeCameraProbeResult(
                        D3D12ManagerReady: true,
                        D3D12Mode: deviceMode,
                        MediaSubtype: MediaFoundationInterop.FormatSubtype(subtype),
                        Width: width,
                        Height: height,
                        FramesPerSecond: fps,
                        SamplesRead: samplesRead,
                        DxgiBackedSamples: dxgiBackedSamples,
                        D3D12ResourceSamples: d3d12ResourceSamples,
                        Status: $"ReadSample failed on the texture-native path: 0x{result:X8}");
                }
                if ((streamFlags & MediaFoundationInterop.MF_SOURCE_READERF_ENDOFSTREAM) != 0)
                {
                    break;
                }

                if (sampleObject is not IMFSample sample)
                {
                    MediaFoundationInterop.ReleaseComObject(sampleObject);
                    continue;
                }

                try
                {
                    samplesRead++;
                    if (TryInspectDxgiSample(sample, resourceId, out var hasTextureResource))
                    {
                        dxgiBackedSamples++;
                        if (hasTextureResource)
                        {
                            d3d12ResourceSamples++;
                        }
                    }
                }
                finally
                {
                    MediaFoundationInterop.ReleaseComObject(sampleObject);
                }
            }

            var status = d3d12ResourceSamples > 0
                ? $"Texture-native {resourceLabel} samples are available."
                : dxgiBackedSamples > 0
                    ? $"DXGI samples are available, but the resource did not expose {resourceLabel}."
                    : "Samples were read, but they were not DXGI-backed.";

            return new TextureNativeCameraProbeResult(
                D3D12ManagerReady: true,
                D3D12Mode: deviceMode,
                MediaSubtype: MediaFoundationInterop.FormatSubtype(subtype),
                Width: width,
                Height: height,
                FramesPerSecond: fps,
                SamplesRead: samplesRead,
                DxgiBackedSamples: dxgiBackedSamples,
                D3D12ResourceSamples: d3d12ResourceSamples,
                Status: status);
        }
        catch (Exception ex)
        {
            return new TextureNativeCameraProbeResult(
                D3D12ManagerReady: true,
                D3D12Mode: deviceMode,
                MediaSubtype: "unknown",
                Width: mode?.Width ?? 0,
                Height: mode?.Height ?? 0,
                FramesPerSecond: mode?.FramesPerSecond ?? 0d,
                SamplesRead: 0,
                DxgiBackedSamples: 0,
                D3D12ResourceSamples: 0,
                Status: ex.Message);
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(reader);
            MediaFoundationInterop.ReleaseComObject(mediaSource);
        }
    }

    private static (int Width, int Height, double Fps, Guid Subtype) ReadCurrentFormat(
        IMFSourceReader reader,
        CameraVideoMode? requestedMode)
    {
        var width = requestedMode?.Width ?? 0;
        var height = requestedMode?.Height ?? 0;
        var fps = requestedMode?.FramesPerSecond ?? 0d;
        var subtype = Guid.Empty;

        var result = reader.GetCurrentMediaType(
            MediaFoundationInterop.MF_SOURCE_READER_FIRST_VIDEO_STREAM,
            out var currentType);
        if (MediaFoundationInterop.Failed(result))
        {
            return (width, height, fps, subtype);
        }

        try
        {
            if (MediaFoundationInterop.TryGetFrameSize(currentType, out var activeWidth, out var activeHeight))
            {
                width = activeWidth;
                height = activeHeight;
            }

            if (MediaFoundationInterop.TryGetFrameRate(currentType, out var activeFps))
            {
                fps = activeFps;
            }

            currentType.GetGUID(MediaFoundationGuids.MF_MT_SUBTYPE, out subtype);
            return (width, height, fps, subtype);
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(currentType);
        }
    }

    private static bool TryInspectDxgiSample(IMFSample sample, Guid resourceId, out bool hasTextureResource)
    {
        hasTextureResource = false;
        IMFMediaBuffer? buffer = null;
        try
        {
            var bufferResult = sample.GetBufferByIndex(0, out buffer);
            if (MediaFoundationInterop.Failed(bufferResult) || buffer is null)
            {
                return false;
            }

            var dxgiBuffer = QueryDxgiBuffer(buffer);
            if (dxgiBuffer is null)
            {
                return false;
            }

            try
            {
                var resourceResult = dxgiBuffer.GetResource(
                    resourceId,
                    out var resource);
                if (!MediaFoundationInterop.Failed(resourceResult) && resource != IntPtr.Zero)
                {
                    Marshal.Release(resource);
                    hasTextureResource = true;
                }

                return true;
            }
            finally
            {
                MediaFoundationInterop.ReleaseComObject(dxgiBuffer);
            }
        }
        finally
        {
            MediaFoundationInterop.ReleaseComObject(buffer);
        }
    }

    private static IMFDXGIBuffer? QueryDxgiBuffer(IMFMediaBuffer buffer)
    {
        var unknown = IntPtr.Zero;
        var dxgiBufferPointer = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(buffer);
            var dxgiBufferId = typeof(IMFDXGIBuffer).GUID;
            if (Marshal.QueryInterface(unknown, in dxgiBufferId, out dxgiBufferPointer) < 0
                || dxgiBufferPointer == IntPtr.Zero)
            {
                return null;
            }

            return (IMFDXGIBuffer)Marshal.GetObjectForIUnknown(dxgiBufferPointer);
        }
        finally
        {
            if (dxgiBufferPointer != IntPtr.Zero)
            {
                Marshal.Release(dxgiBufferPointer);
            }

            if (unknown != IntPtr.Zero)
            {
                Marshal.Release(unknown);
            }
        }
    }

    private sealed record TextureProbeAttempt(
        string Name,
        bool EnableAdvancedVideoProcessing,
        Guid? PreferredSubtype,
        bool ConfigureMediaType);
}
