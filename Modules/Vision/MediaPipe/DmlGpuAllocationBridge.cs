using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.ML.OnnxRuntime;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

/// <summary>
/// Wraps an ID3D12Resource buffer as memory owned by the ONNX Runtime DirectML
/// execution provider. ONNX Runtime's managed API can then construct an
/// OrtValue directly over the GPU allocation without staging through the CPU.
/// </summary>
internal static class DmlGpuAllocationBridge
{
	private const uint OrtApiVersion = 24;

	private const int GetErrorMessageApiIndex = 2;

	private const int ReleaseStatusApiIndex = 93;

	private const int GetExecutionProviderApiIndex = 195;

	private const int CreateGpuAllocationApiIndex = 2;

	private const int FreeGpuAllocationApiIndex = 3;

	private const int AppendExecutionProviderDml1ApiIndex = 1;

	private static readonly Guid IDmlDevice =
		new("6dbd6437-96fd-423f-a98c-ae5e7c2a573f");

	private static readonly Lazy<Api> DirectMlApi = new(CreateApi, LazyThreadSafetyMode.ExecutionAndPublication);

	public static DmlGpuAllocation Create(nint d3d12Resource)
	{
		if (d3d12Resource == IntPtr.Zero)
		{
			throw new ArgumentException("A D3D12 resource is required.", nameof(d3d12Resource));
		}

		Api api = DirectMlApi.Value;
		nint status = api.CreateGpuAllocation(d3d12Resource, out nint allocation);
		ThrowIfFailed(api, status, "DirectML could not wrap the GPU tensor buffer");
		if (allocation == IntPtr.Zero)
		{
			throw new InvalidOperationException("DirectML returned an empty GPU allocation.");
		}
		return new DmlGpuAllocation(allocation, api.FreeGpuAllocation);
	}

	public static void AppendExecutionProvider(
		SessionOptions options,
		nint d3d12Device,
		nint commandQueue)
	{
		ArgumentNullException.ThrowIfNull(options);
		if (d3d12Device == IntPtr.Zero || commandQueue == IntPtr.Zero)
		{
			throw new ArgumentException(
				"A D3D12 device and compute command queue are required.");
		}

		nint dmlDevice = IntPtr.Zero;
		int result = DMLCreateDevice(
			d3d12Device,
			0u,
			in IDmlDevice,
			out dmlDevice);
		Marshal.ThrowExceptionForHR(result);
		try
		{
			Api api = DirectMlApi.Value;
			nint status = api.AppendExecutionProviderDml1(
				GetSessionOptionsHandle(options),
				dmlDevice,
				commandQueue);
			ThrowIfFailed(
				api,
				status,
				"DirectML could not bind ONNX Runtime to the camera GPU device");
		}
		finally
		{
			if (dmlDevice != IntPtr.Zero)
			{
				Marshal.Release(dmlDevice);
			}
		}
	}

	private static Api CreateApi()
	{
		nint apiBase = OrtGetApiBase();
		if (apiBase == IntPtr.Zero)
		{
			throw new InvalidOperationException("ONNX Runtime did not expose OrtApiBase.");
		}

		GetApiDelegate getApi = Marshal.GetDelegateForFunctionPointer<GetApiDelegate>(
			Marshal.ReadIntPtr(apiBase));
		nint ortApi = getApi(OrtApiVersion);
		if (ortApi == IntPtr.Zero)
		{
			throw new InvalidOperationException(
				$"ONNX Runtime does not support API version {OrtApiVersion}.");
		}

		GetExecutionProviderApiDelegate getProviderApi =
			ReadDelegate<GetExecutionProviderApiDelegate>(ortApi, GetExecutionProviderApiIndex);
		byte[] providerName = Encoding.ASCII.GetBytes("DML\0");
		nint providerApi;
		unsafe
		{
			fixed (byte* providerNamePointer = providerName)
			{
				nint status = getProviderApi((nint)providerNamePointer, OrtApiVersion, out providerApi);
				if (status != IntPtr.Zero)
				{
					GetErrorMessageDelegate getMessage =
						ReadDelegate<GetErrorMessageDelegate>(ortApi, GetErrorMessageApiIndex);
					ReleaseStatusDelegate releaseStatus =
						ReadDelegate<ReleaseStatusDelegate>(ortApi, ReleaseStatusApiIndex);
					string message = Marshal.PtrToStringUTF8(getMessage(status))
						?? "unknown ONNX Runtime error";
					releaseStatus(status);
					throw new InvalidOperationException(
						"ONNX Runtime did not expose its DirectML provider API: " + message);
				}
			}
		}

		if (providerApi == IntPtr.Zero)
		{
			throw new InvalidOperationException("ONNX Runtime returned an empty DirectML provider API.");
		}

		return new Api(
			ReadDelegate<AppendExecutionProviderDml1Delegate>(
				providerApi,
				AppendExecutionProviderDml1ApiIndex),
			ReadDelegate<CreateGpuAllocationDelegate>(providerApi, CreateGpuAllocationApiIndex),
			ReadDelegate<FreeGpuAllocationDelegate>(providerApi, FreeGpuAllocationApiIndex),
			ReadDelegate<GetErrorMessageDelegate>(ortApi, GetErrorMessageApiIndex),
			ReadDelegate<ReleaseStatusDelegate>(ortApi, ReleaseStatusApiIndex));
	}

	private static nint GetSessionOptionsHandle(SessionOptions options)
	{
		const BindingFlags flags =
			BindingFlags.Instance |
			BindingFlags.Public |
			BindingFlags.NonPublic;
		Type type = options.GetType();
		object? value =
			type.GetProperty("Handle", flags)?.GetValue(options)
			?? type.GetProperty("NativeHandle", flags)?.GetValue(options)
			?? type.GetField("_handle", flags)?.GetValue(options);
		if (value is IntPtr pointer && pointer != IntPtr.Zero)
		{
			return pointer;
		}
		if (value is SafeHandle safeHandle && !safeHandle.IsInvalid)
		{
			return safeHandle.DangerousGetHandle();
		}
		throw new InvalidOperationException(
			"ONNX Runtime did not expose the native session-options handle.");
	}

	private static TDelegate ReadDelegate<TDelegate>(nint table, int index)
		where TDelegate : Delegate
	{
		nint function = Marshal.ReadIntPtr(table, checked(index * IntPtr.Size));
		if (function == IntPtr.Zero)
		{
			throw new InvalidOperationException(
				$"ONNX Runtime API function {index} is unavailable.");
		}
		return Marshal.GetDelegateForFunctionPointer<TDelegate>(function);
	}

	private static void ThrowIfFailed(Api api, nint status, string operation)
	{
		if (status == IntPtr.Zero)
		{
			return;
		}
		string message = Marshal.PtrToStringUTF8(api.GetErrorMessage(status))
			?? "unknown ONNX Runtime error";
		api.ReleaseStatus(status);
		throw new InvalidOperationException(operation + ": " + message);
	}

	[DllImport("onnxruntime", CallingConvention = CallingConvention.StdCall)]
	private static extern nint OrtGetApiBase();

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate nint GetApiDelegate(uint version);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate nint GetExecutionProviderApiDelegate(
		nint providerName,
		uint version,
		out nint providerApi);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate nint GetErrorMessageDelegate(nint status);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate void ReleaseStatusDelegate(nint status);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate nint CreateGpuAllocationDelegate(
		nint d3d12Resource,
		out nint dmlAllocation);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	private delegate nint AppendExecutionProviderDml1Delegate(
		nint sessionOptions,
		nint dmlDevice,
		nint commandQueue);

	[UnmanagedFunctionPointer(CallingConvention.StdCall)]
	internal delegate nint FreeGpuAllocationDelegate(nint dmlAllocation);

	private sealed record Api(
		AppendExecutionProviderDml1Delegate AppendExecutionProviderDml1,
		CreateGpuAllocationDelegate CreateGpuAllocation,
		FreeGpuAllocationDelegate FreeGpuAllocation,
		GetErrorMessageDelegate GetErrorMessage,
		ReleaseStatusDelegate ReleaseStatus);

	[DllImport("DirectML", CallingConvention = CallingConvention.StdCall)]
	private static extern int DMLCreateDevice(
		nint d3d12Device,
		uint flags,
		in Guid interfaceId,
		out nint dmlDevice);
}

internal sealed class DmlGpuAllocation : IDisposable
{
	private nint _allocation;

	private readonly DmlGpuAllocationBridge.FreeGpuAllocationDelegate _free;

	public nint Pointer => Volatile.Read(ref _allocation);

	internal DmlGpuAllocation(
		nint allocation,
		DmlGpuAllocationBridge.FreeGpuAllocationDelegate free)
	{
		_allocation = allocation;
		_free = free;
	}

	public void Dispose()
	{
		nint allocation = Interlocked.Exchange(ref _allocation, IntPtr.Zero);
		if (allocation == IntPtr.Zero)
		{
			return;
		}
		_ = _free(allocation);
	}
}
