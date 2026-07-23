using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AvatarBuilder.Modules.Infrastructure;

public static class DarkWindowFrame
{
	public static void Apply(Window window)
	{
		ArgumentNullException.ThrowIfNull(window, "window");
		try
		{
			nint handle = new WindowInteropHelper(window).Handle;
			if (handle != IntPtr.Zero)
			{
				int attributeValue = 1;
				if (DwmSetWindowAttribute(handle, 20, ref attributeValue, 4) != 0)
				{
					DwmSetWindowAttribute(handle, 19, ref attributeValue, 4);
				}
			}
		}
		catch
		{
		}
	}

	[DllImport("dwmapi.dll")]
	private static extern int DwmSetWindowAttribute(nint windowHandle, int attribute, ref int attributeValue, int attributeSize);
}
