using System.Runtime.InteropServices;
using AvatarBuilder.Modules.Webcam.MediaFoundation;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

internal interface ITextureNativeDeviceManager : IDisposable
{
    IMFDXGIDeviceManager Manager { get; }

    string ModeName { get; }

    Guid TextureResourceId { get; }

    IntPtr DuplicateNativeD3D12Device();
}

internal sealed class Direct3D12DeviceManager : ITextureNativeDeviceManager
{
    private const int D3D_FEATURE_LEVEL_12_0 = 0xc000;
    private static readonly Guid ID3D12Device = new("189819f1-1db6-4b57-be54-1821339b85f7");
    public static readonly Guid ID3D12Resource = new("696442be-a72e-4059-bc79-5b5c98040fad");

    private IntPtr _device;
    private IMFDXGIDeviceManager? _manager;

    private Direct3D12DeviceManager(IntPtr device, IMFDXGIDeviceManager manager, int mode)
    {
        _device = device;
        _manager = manager;
        Mode = mode;
    }

    public IMFDXGIDeviceManager Manager => _manager
        ?? throw new ObjectDisposedException(nameof(Direct3D12DeviceManager));

    public int Mode { get; }

    public string ModeName => Mode switch
    {
        2 => "D3D12",
        1 => "D3D11",
        _ => $"mode {Mode}"
    };

    public Guid TextureResourceId => ID3D12Resource;

    public IntPtr DuplicateNativeD3D12Device()
    {
        var device = _device;
        if (device == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        Marshal.AddRef(device);
        return device;
    }

    public static Direct3D12DeviceManager Create()
    {
        MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.D3D12CreateDevice(
            IntPtr.Zero,
            D3D_FEATURE_LEVEL_12_0,
            ID3D12Device,
            out var device));

        try
        {
            MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateDXGIDeviceManager(
                out var resetToken,
                out var manager));
            MediaFoundationInterop.ThrowIfFailed(manager.ResetDevice(device, resetToken));
            MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFGetDXGIDeviceManageMode(
                manager,
                out var mode));
            if (mode != 2)
            {
                throw new InvalidOperationException($"Media Foundation created a DXGI device manager in {FormatMode(mode)} mode instead of D3D12.");
            }

            return new Direct3D12DeviceManager(device, manager, mode);
        }
        catch
        {
            if (device != IntPtr.Zero)
            {
                Marshal.Release(device);
            }

            throw;
        }
    }

    private static string FormatMode(int mode)
    {
        return mode switch
        {
            2 => "D3D12",
            1 => "D3D11",
            _ => $"unknown {mode}"
        };
    }

    public void Dispose()
    {
        MediaFoundationInterop.ReleaseComObject(_manager);
        _manager = null;

        if (_device != IntPtr.Zero)
        {
            Marshal.Release(_device);
            _device = IntPtr.Zero;
        }
    }
}
