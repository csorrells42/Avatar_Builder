using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed class TextureNativeFrameLease : IDisposable
{
	private nint _resource;

	private nint _d3d12SharedTextureHandle;

	private PooledFrameBuffer? _nv12PreviewBuffer;

	public nint Resource => _resource;

	public int Subresource { get; }

	public int Width { get; }

	public int Height { get; }

	public double FramesPerSecond { get; }

	public string DeviceMode { get; }

	public string MediaSubtype { get; }

	public long FrameNumber { get; }

	public nint D3D12SharedTextureHandle => _d3d12SharedTextureHandle;

	public byte[]? Nv12PreviewBytes => _nv12PreviewBuffer?.Bytes;

	public int Nv12PreviewStride { get; }

	public bool IsValid
	{
		get
		{
			if (_resource == IntPtr.Zero)
			{
				return _nv12PreviewBuffer != null;
			}
			return true;
		}
	}

	internal TextureNativeFrameLease(nint resource, int subresource, int width, int height, double framesPerSecond, string deviceMode, string mediaSubtype, long frameNumber, nint d3d12SharedTextureHandle = 0, PooledFrameBuffer? nv12PreviewBuffer = null, int nv12PreviewStride = 0)
	{
		_resource = resource;
		Subresource = subresource;
		Width = width;
		Height = height;
		FramesPerSecond = framesPerSecond;
		DeviceMode = deviceMode;
		MediaSubtype = mediaSubtype;
		FrameNumber = frameNumber;
		_d3d12SharedTextureHandle = d3d12SharedTextureHandle;
		_nv12PreviewBuffer = nv12PreviewBuffer;
		Nv12PreviewStride = nv12PreviewStride;
	}

	public TextureNativeFrameLease? Duplicate()
	{
		nint resource = _resource;
		PooledFrameBuffer nv12PreviewBuffer = _nv12PreviewBuffer;
		if (resource == IntPtr.Zero && nv12PreviewBuffer == null)
		{
			return null;
		}
		if (resource != IntPtr.Zero)
		{
			Marshal.AddRef(resource);
		}
		nint duplicatedHandle = IntPtr.Zero;
		PooledFrameBuffer pooledFrameBuffer = null;
		try
		{
			if (D3D12SharedTextureHandle != IntPtr.Zero && !TryDuplicateHandle(D3D12SharedTextureHandle, out duplicatedHandle))
			{
				if (resource != IntPtr.Zero)
				{
					Marshal.Release(resource);
				}
				return null;
			}
			pooledFrameBuffer = nv12PreviewBuffer?.AddReference();
		}
		catch
		{
			pooledFrameBuffer?.Dispose();
			if (duplicatedHandle != IntPtr.Zero)
			{
				CloseHandle(duplicatedHandle);
			}
			if (resource != IntPtr.Zero)
			{
				Marshal.Release(resource);
			}
			throw;
		}
		return new TextureNativeFrameLease(resource, Subresource, Width, Height, FramesPerSecond, DeviceMode, MediaSubtype, FrameNumber, duplicatedHandle, pooledFrameBuffer, Nv12PreviewStride);
	}

	public TextureNativeFrameLease? DuplicatePreviewData()
	{
		PooledFrameBuffer nv12PreviewBuffer = _nv12PreviewBuffer;
		if (nv12PreviewBuffer == null)
		{
			return null;
		}
		return new TextureNativeFrameLease(IntPtr.Zero, Subresource, Width, Height, FramesPerSecond, DeviceMode, MediaSubtype, FrameNumber, IntPtr.Zero, nv12PreviewBuffer.AddReference(), Nv12PreviewStride);
	}

	public void Dispose()
	{
		nint num = Interlocked.Exchange(ref _resource, IntPtr.Zero);
		if (num != IntPtr.Zero)
		{
			Marshal.Release(num);
		}
		CloseOwnedSharedTextureHandle(Interlocked.Exchange(ref _d3d12SharedTextureHandle, IntPtr.Zero));
		Interlocked.Exchange(ref _nv12PreviewBuffer, null)?.Dispose();
	}

	internal static void CloseOwnedSharedTextureHandle(nint handle)
	{
		if (handle != IntPtr.Zero)
		{
			CloseHandle(handle);
		}
	}

	private static bool TryDuplicateHandle(nint sourceHandle, out nint duplicatedHandle)
	{
		nint currentProcess = GetCurrentProcess();
		return DuplicateHandle(currentProcess, sourceHandle, currentProcess, out duplicatedHandle, 0u, inheritHandle: false, 2u);
	}

	[DllImport("kernel32.dll")]
	private static extern nint GetCurrentProcess();

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool DuplicateHandle(nint sourceProcessHandle, nint sourceHandle, nint targetProcessHandle, out nint targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(nint handle);
}
