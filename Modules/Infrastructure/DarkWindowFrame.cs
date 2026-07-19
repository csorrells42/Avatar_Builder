using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AvatarBuilder.Modules.Infrastructure;

public static class DarkWindowFrame
{
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        try
        {
            var windowHandle = new WindowInteropHelper(window).Handle;
            if (windowHandle == IntPtr.Zero)
            {
                return;
            }

            var enabled = 1;
            if (DwmSetWindowAttribute(windowHandle, 20, ref enabled, sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(windowHandle, 19, ref enabled, sizeof(int));
            }
        }
        catch
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
