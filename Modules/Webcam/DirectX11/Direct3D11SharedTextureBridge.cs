using System;
using System.Runtime.InteropServices;
using AvatarBuilder.Modules.Webcam.MediaFoundation;
using Vortice.DXGI;

namespace AvatarBuilder.Modules.Webcam.DirectX11;

internal sealed class Direct3D11SharedTextureBridge : IDisposable
{
	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate int CreateTexture2DDelegate(nint device, ref D3D11Texture2DDescription description, nint initialData, out nint texture);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate void CopyResourceDelegate(nint context, nint destinationResource, nint sourceResource);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate void FlushDelegate(nint context);

	private struct D3D11Texture2DDescription
	{
		public uint Width;

		public uint Height;

		public uint MipLevels;

		public uint ArraySize;

		public int Format;

		public DxgiSampleDescription SampleDescription;

		public int Usage;

		public uint BindFlags;

		public uint CPUAccessFlags;

		public uint MiscFlags;
	}

	private struct DxgiSampleDescription
	{
		public uint Count;

		public uint Quality;
	}

	private const int CreateTexture2DSlot = 5;

	private const int CopyResourceSlot = 47;

	private const int FlushSlot = 111;

	private const int D3D11UsageDefault = 0;

	private const int D3D11BindShaderResource = 8;

	private const int D3D11ResourceMiscSharedKeyedMutex = 256;

	private const int D3D11ResourceMiscSharedNtHandle = 2048;

	private const int DxgiFormatNv12 = 103;

	private const int DuplicateSameAccess = 2;

	private readonly nint _device;

	private readonly nint _context;

	private readonly CreateTexture2DDelegate _createTexture2D;

	private readonly CopyResourceDelegate _copyResource;

	private readonly FlushDelegate _flush;

	private nint _sharedTexture;

	private nint _sharedHandle;

	private bool _disposed;

	public Direct3D11SharedTextureBridge(nint device, nint context, int width, int height)
	{
		if (device == IntPtr.Zero)
		{
			throw new ArgumentException("D3D11 device is missing.", "device");
		}
		if (context == IntPtr.Zero)
		{
			throw new ArgumentException("D3D11 context is missing.", "context");
		}
		if (width <= 0 || height <= 0)
		{
			throw new ArgumentOutOfRangeException("width", "Shared texture dimensions must be positive.");
		}
		_device = device;
		_context = context;
		Marshal.AddRef(_device);
		Marshal.AddRef(_context);
		_createTexture2D = GetComMethod<CreateTexture2DDelegate>(_device, 5);
		_copyResource = GetComMethod<CopyResourceDelegate>(_context, 47);
		_flush = GetComMethod<FlushDelegate>(_context, 111);
		CreateSharedTexture(width, height);
	}

	public bool TryCopyToSharedHandle(nint sourceTexture, out nint duplicatedSharedHandle, out string? failureReason)
	{
		duplicatedSharedHandle = IntPtr.Zero;
		failureReason = null;
		if (_disposed)
		{
			failureReason = "D3D11 shared texture bridge is disposed.";
			return false;
		}
		if (sourceTexture == IntPtr.Zero)
		{
			failureReason = "D3D11 source texture is missing.";
			return false;
		}
		if (_sharedTexture == IntPtr.Zero || _sharedHandle == IntPtr.Zero)
		{
			failureReason = "D3D11 shared texture bridge is not initialized.";
			return false;
		}
		try
		{
			_copyResource(_context, _sharedTexture, sourceTexture);
			_flush(_context);
			if (!TryDuplicateHandle(_sharedHandle, out duplicatedSharedHandle))
			{
				failureReason = $"DuplicateHandle failed: {Marshal.GetLastWin32Error()}";
				return false;
			}
			return true;
		}
		catch (Exception ex)
		{
			if (duplicatedSharedHandle != IntPtr.Zero)
			{
				CloseHandle(duplicatedSharedHandle);
				duplicatedSharedHandle = IntPtr.Zero;
			}
			failureReason = ex.Message;
			return false;
		}
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			if (_sharedHandle != IntPtr.Zero)
			{
				CloseHandle(_sharedHandle);
				_sharedHandle = IntPtr.Zero;
			}
			if (_sharedTexture != IntPtr.Zero)
			{
				Marshal.Release(_sharedTexture);
				_sharedTexture = IntPtr.Zero;
			}
			Marshal.Release(_context);
			Marshal.Release(_device);
		}
	}

	private void CreateSharedTexture(int width, int height)
	{
		D3D11Texture2DDescription description = new D3D11Texture2DDescription
		{
			Width = (uint)width,
			Height = (uint)height,
			MipLevels = 1u,
			ArraySize = 1u,
			Format = 103,
			SampleDescription = new DxgiSampleDescription
			{
				Count = 1u,
				Quality = 0u
			},
			Usage = 0,
			BindFlags = 8u,
			CPUAccessFlags = 0u,
			MiscFlags = 2304u
		};
		MediaFoundationInterop.ThrowIfFailed(_createTexture2D(_device, ref description, IntPtr.Zero, out _sharedTexture));
		if (_sharedTexture == IntPtr.Zero)
		{
			throw new InvalidOperationException("D3D11 CreateTexture2D returned no shared bridge texture.");
		}
		nint ppv = IntPtr.Zero;
		try
		{
			Guid iid = typeof(IDXGIResource1).GUID;
			MediaFoundationInterop.ThrowIfFailed(Marshal.QueryInterface(_sharedTexture, in iid, out ppv));
			if (ppv == IntPtr.Zero)
			{
				throw new InvalidOperationException("D3D11 bridge texture did not expose IDXGIResource1.");
			}
			using IDXGIResource1 iDXGIResource = new IDXGIResource1(ppv);
			ppv = IntPtr.Zero;
			_sharedHandle = iDXGIResource.CreateSharedHandle(null, SharedResourceFlags.Read, null);
			if (_sharedHandle == IntPtr.Zero)
			{
				throw new InvalidOperationException("D3D11 bridge texture did not create a shared handle.");
			}
		}
		finally
		{
			if (ppv != IntPtr.Zero)
			{
				Marshal.Release(ppv);
			}
		}
	}

	private static TDelegate GetComMethod<TDelegate>(nint instance, int slot) where TDelegate : Delegate
	{
		return Marshal.GetDelegateForFunctionPointer<TDelegate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
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
