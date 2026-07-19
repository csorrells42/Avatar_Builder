using System.Runtime.InteropServices;
using AvatarBuilder.Modules.Webcam.DirectX12;
using AvatarBuilder.Modules.Webcam.MediaFoundation;

namespace AvatarBuilder.Modules.Webcam.DirectX11;

internal sealed class Direct3D11DeviceManager : ITextureNativeDeviceManager
{
    public static readonly Guid ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const int D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const int D3D11_CREATE_DEVICE_VIDEO_SUPPORT = 0x800;
    private const int D3D11_SDK_VERSION = 7;
    private const int D3D_FEATURE_LEVEL_11_1 = 0xb100;
    private const int D3D_FEATURE_LEVEL_11_0 = 0xb000;

    private IntPtr _device;
    private IntPtr _context;
    private IMFDXGIDeviceManager? _manager;

    private Direct3D11DeviceManager(IntPtr device, IntPtr context, IMFDXGIDeviceManager manager, int mode)
    {
        _device = device;
        _context = context;
        _manager = manager;
        Mode = mode;
    }

    public IMFDXGIDeviceManager Manager => _manager
        ?? throw new ObjectDisposedException(nameof(Direct3D11DeviceManager));

    public int Mode { get; }

    public string ModeName => Mode switch
    {
        2 => "D3D12",
        1 => "D3D11",
        _ => $"mode {Mode}"
    };

    public Guid TextureResourceId => ID3D11Texture2D;

    public IntPtr DuplicateNativeD3D12Device() => IntPtr.Zero;

    public static Direct3D11DeviceManager Create()
    {
        var featureLevels = new[] { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
        MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.D3D11CreateDevice(
            IntPtr.Zero,
            D3D_DRIVER_TYPE_HARDWARE,
            IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
            featureLevels,
            featureLevels.Length,
            D3D11_SDK_VERSION,
            out var device,
            out _,
            out var context));

        try
        {
            MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFCreateDXGIDeviceManager(
                out var resetToken,
                out var manager));
            MediaFoundationInterop.ThrowIfFailed(manager.ResetDevice(device, resetToken));
            MediaFoundationInterop.ThrowIfFailed(MediaFoundationInterop.MFGetDXGIDeviceManageMode(
                manager,
                out var mode));
            return new Direct3D11DeviceManager(device, context, manager, mode);
        }
        catch
        {
            if (context != IntPtr.Zero)
            {
                Marshal.Release(context);
            }

            if (device != IntPtr.Zero)
            {
                Marshal.Release(device);
            }

            throw;
        }
    }

    public void Dispose()
    {
        MediaFoundationInterop.ReleaseComObject(_manager);
        _manager = null;

        if (_context != IntPtr.Zero)
        {
            Marshal.Release(_context);
            _context = IntPtr.Zero;
        }

        if (_device != IntPtr.Zero)
        {
            Marshal.Release(_device);
            _device = IntPtr.Zero;
        }
    }
}
