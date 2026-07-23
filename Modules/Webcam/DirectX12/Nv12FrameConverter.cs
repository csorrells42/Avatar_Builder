using System;
using System.Runtime.InteropServices;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

internal static class Nv12FrameConverter
{
	public static byte[]? ConvertToBgra(nint source, int sourceLength, int width, int height, out int bgraStride)
	{
		bgraStride = width * 4;
		if (width <= 0 || height <= 0)
		{
			return null;
		}
		int num = Math.Max(width, sourceLength * 2 / Math.Max(1, height * 3));
		int num2 = num * height + num * ((height + 1) / 2);
		if (sourceLength < num2)
		{
			return null;
		}
		byte[] array = new byte[num2];
		Marshal.Copy(source, array, 0, num2);
		return ConvertToBgra(array, num, width, height, out bgraStride);
	}

	public static byte[]? ConvertToBgra(byte[] nv12, int nv12Stride, int width, int height, out int bgraStride)
	{
		int outputWidth;
		int outputHeight;
		return ConvertToBgra(nv12, nv12Stride, width, height, width, out outputWidth, out outputHeight, out bgraStride);
	}

	public static byte[]? ConvertToBgra(byte[] nv12, int nv12Stride, int width, int height, int maximumWidth, out int outputWidth, out int outputHeight, out int bgraStride)
	{
		bgraStride = width * 4;
		outputWidth = width;
		outputHeight = height;
		if (width <= 0 || height <= 0 || nv12Stride < width)
		{
			return null;
		}
		int num = nv12Stride * height + nv12Stride * ((height + 1) / 2);
		if (nv12.Length < num)
		{
			return null;
		}
		if (maximumWidth > 0 && width > maximumWidth)
		{
			double num2 = (double)maximumWidth / (double)width;
			outputWidth = maximumWidth;
			outputHeight = Math.Max(1, (int)Math.Round((double)height * num2));
		}
		bgraStride = outputWidth * 4;
		byte[] array = new byte[bgraStride * outputHeight];
		int num3 = nv12Stride * height;
		for (int i = 0; i < outputHeight; i++)
		{
			int num4 = Math.Min(height - 1, (int)(((double)i + 0.5) * (double)height / (double)outputHeight));
			int num5 = num4 * nv12Stride;
			int num6 = num3 + num4 / 2 * nv12Stride;
			int num7 = i * bgraStride;
			for (int j = 0; j < outputWidth; j++)
			{
				int num8 = Math.Min(width - 1, (int)(((double)j + 0.5) * (double)width / (double)outputWidth));
				byte b = nv12[num5 + num8];
				int num9 = num6 + (num8 & -2);
				byte b2 = nv12[num9];
				byte num10 = nv12[num9 + 1];
				int num11 = b - 16;
				int num12 = b2 - 128;
				int num13 = num10 - 128;
				byte b3 = ClampToByte(298 * num11 + 409 * num13 + 128 >> 8);
				byte b4 = ClampToByte(298 * num11 - 100 * num12 - 208 * num13 + 128 >> 8);
				byte b5 = ClampToByte(298 * num11 + 516 * num12 + 128 >> 8);
				int num14 = num7 + j * 4;
				array[num14] = b5;
				array[num14 + 1] = b4;
				array[num14 + 2] = b3;
				array[num14 + 3] = byte.MaxValue;
			}
		}
		return array;
	}

	private static byte ClampToByte(int value)
	{
		return (byte)Math.Clamp(value, 0, 255);
	}
}
