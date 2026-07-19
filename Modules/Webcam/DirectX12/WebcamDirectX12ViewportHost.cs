using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public abstract class WebcamDirectX12ViewportHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;
    private const int SsBlackRect = 0x00000004;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;

    private readonly string _childWindowFailureMessage;
    private readonly bool _useBlackBackground;
    private IntPtr _viewportHandle;
    private int _viewportPixelWidth;
    private int _viewportPixelHeight;
    private DateTimeOffset? _viewportCreatedUtc;

    protected WebcamDirectX12ViewportHost(
        string childWindowFailureMessage = "Could not create webcam DX12 preview child window.",
        bool useBlackBackground = true)
    {
        _childWindowFailureMessage = childWindowFailureMessage;
        _useBlackBackground = useBlackBackground;
    }

    public bool IsViewportCreated => _viewportHandle != IntPtr.Zero;

    public int ViewportPixelWidth => _viewportPixelWidth;

    public int ViewportPixelHeight => _viewportPixelHeight;

    public DateTimeOffset? ViewportCreatedUtc => _viewportCreatedUtc;

    public string ViewportStateDescription => IsViewportCreated
        ? $"webcam DX12 viewport {_viewportPixelWidth}x{_viewportPixelHeight}"
        : "webcam DX12 viewport not created";

    protected IntPtr ViewportHandle => _viewportHandle;

    protected int ViewportWidth => Math.Max(1, (int)ActualWidth);

    protected int ViewportHeight => Math.Max(1, (int)ActualHeight);

    protected sealed override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var width = ViewportWidth;
        var height = ViewportHeight;
        var style = WsChild | WsVisible | WsClipChildren | WsClipSiblings;
        if (_useBlackBackground)
        {
            style |= SsBlackRect;
        }

        _viewportHandle = CreateWindowEx(
            0,
            "static",
            string.Empty,
            style,
            0,
            0,
            width,
            height,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_viewportHandle == IntPtr.Zero)
        {
            var lastError = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"{_childWindowFailureMessage} Win32 error: {lastError}.");
        }

        _viewportPixelWidth = width;
        _viewportPixelHeight = height;
        _viewportCreatedUtc = DateTimeOffset.UtcNow;

        try
        {
            OnViewportCreated(_viewportHandle, width, height);
        }
        catch (Exception ex)
        {
            OnViewportCreateFailed(ex);
        }

        return new HandleRef(this, _viewportHandle);
    }

    protected sealed override void DestroyWindowCore(HandleRef hwnd)
    {
        try
        {
            OnViewportDestroying();
        }
        finally
        {
            if (hwnd.Handle != IntPtr.Zero)
            {
                DestroyWindow(hwnd.Handle);
            }

            _viewportHandle = IntPtr.Zero;
            _viewportPixelWidth = 0;
            _viewportPixelHeight = 0;
            _viewportCreatedUtc = null;
        }
    }

    protected sealed override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_viewportHandle == IntPtr.Zero)
        {
            return;
        }

        var width = ViewportWidth;
        var height = ViewportHeight;
        SetWindowPos(_viewportHandle, IntPtr.Zero, 0, 0, width, height, SwpNoZOrder | SwpNoActivate);
        _viewportPixelWidth = width;
        _viewportPixelHeight = height;

        try
        {
            OnViewportResized(width, height);
        }
        catch (Exception ex)
        {
            OnViewportResizeFailed(ex);
        }
    }

    protected abstract void OnViewportCreated(IntPtr hwnd, int width, int height);

    protected abstract void OnViewportDestroying();

    protected abstract void OnViewportResized(int width, int height);

    protected virtual void OnViewportCreateFailed(Exception ex)
    {
        throw new InvalidOperationException("Webcam DX12 viewport initialization failed.", ex);
    }

    protected virtual void OnViewportResizeFailed(Exception ex)
    {
        throw new InvalidOperationException("Webcam DX12 viewport resize failed.", ex);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr hwndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        int flags);
}

