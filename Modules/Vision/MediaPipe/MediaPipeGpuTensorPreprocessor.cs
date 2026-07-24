using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using AvatarBuilder.Modules.Webcam.DirectX12;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using static Vortice.Direct3D12.D3D12;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

internal readonly record struct MediaPipeGpuRoi(
	float CenterX,
	float CenterY,
	float Size,
	float Angle);

internal readonly record struct MediaPipeDetectorTransform(
	float Scale,
	float Left,
	float Top,
	int FrameWidth,
	int FrameHeight);

/// <summary>
/// Converts an NV12 camera texture directly into the float NHWC tensors used by
/// the MediaPipe detector and 478-point landmarker. The source texture and
/// output tensors remain GPU-resident.
/// </summary>
internal sealed class MediaPipeGpuTensorPreprocessor : IDisposable
{
	public const int DetectorSize = 128;

	public const int LandmarkSize = 256;

	private const int TensorChannels = 3;

	private const int RootConstantCount = 16;

	private static readonly TimeSpan GpuWaitTimeout =
		TimeSpan.FromMilliseconds(500);

	private const string ShaderSource = """
		Texture2D<float> CameraLuma : register(t0);
		Texture2D<float2> CameraChroma : register(t1);
		RWStructuredBuffer<float> OutputTensor : register(u0);
		SamplerState CameraSampler : register(s0);

		cbuffer PreprocessSettings : register(b0)
		{
		    float2 FrameSize;
		    float2 OutputSize;
		    float2 LetterboxOffset;
		    float2 LetterboxSize;
		    float2 RoiCenter;
		    float2 RoiRight;
		    float2 RoiDown;
		    float RoiSize;
		    float SignedNormalization;
		};

		float3 SampleRgb(float2 sourcePixel)
		{
		    if (sourcePixel.x < 0.0 || sourcePixel.y < 0.0
		        || sourcePixel.x >= FrameSize.x || sourcePixel.y >= FrameSize.y)
		    {
		        return float3(0.0, 0.0, 0.0);
		    }

		    float2 uvCoordinate = (sourcePixel + 0.5) / FrameSize;
		    float rawY = CameraLuma.SampleLevel(CameraSampler, uvCoordinate, 0);
		    float y = saturate((rawY - (16.0 / 255.0)) * (255.0 / 219.0));
		    float2 uv = CameraChroma.SampleLevel(CameraSampler, uvCoordinate, 0) - float2(0.5, 0.5);
		    return saturate(float3(
		        y + 1.5748 * uv.y,
		        y - 0.1873 * uv.x - 0.4681 * uv.y,
		        y + 1.8556 * uv.x));
		}

		[numthreads(8, 8, 1)]
		void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
		{
		    if (dispatchThreadId.x >= (uint)OutputSize.x
		        || dispatchThreadId.y >= (uint)OutputSize.y)
		    {
		        return;
		    }

		    float2 outputPixel = float2(dispatchThreadId.xy);
		    float3 rgb;
		    if (SignedNormalization > 0.5)
		    {
		        float2 letterboxPixel = outputPixel - LetterboxOffset;
		        bool inside = letterboxPixel.x >= 0.0 && letterboxPixel.y >= 0.0
		            && letterboxPixel.x < LetterboxSize.x
		            && letterboxPixel.y < LetterboxSize.y;
		        if (inside)
		        {
		            float2 sourcePixel =
		                ((letterboxPixel + 0.5) / LetterboxSize) * FrameSize - 0.5;
		            rgb = SampleRgb(sourcePixel);
		        }
		        else
		        {
		            rgb = float3(0.0, 0.0, 0.0);
		        }
		        rgb = rgb * 2.0 - 1.0;
		    }
		    else
		    {
		        float2 local =
		            (outputPixel / (OutputSize - 1.0) - 0.5) * RoiSize;
		        float2 sourcePixel =
		            RoiCenter + RoiRight * local.x + RoiDown * local.y;
		        rgb = SampleRgb(sourcePixel);
		    }

		    uint tensorIndex =
		        (dispatchThreadId.y * (uint)OutputSize.x + dispatchThreadId.x) * 3;
		    OutputTensor[tensorIndex] = rgb.r;
		    OutputTensor[tensorIndex + 1] = rgb.g;
		    OutputTensor[tensorIndex + 2] = rgb.b;
		}
		""";

	private readonly ID3D12Device _device;

	private readonly ID3D12CommandQueue _commandQueue;

	private readonly CommandContext _detectorCommands;

	private readonly CommandContext _landmarkCommands;

	private readonly ID3D12Fence _fence;

	private ID3D12Fence? _d3d11ProducerFence;

	private nint _d3d11ProducerFenceHandle;

	private ulong _lastD3D11ProducerFenceValue;

	private readonly AutoResetEvent _fenceEvent = new(initialState: false);

	private readonly ID3D12DescriptorHeap _descriptorHeap;

	private readonly int _descriptorSize;

	private readonly ID3D12RootSignature _rootSignature;

	private readonly ID3D12PipelineState _pipelineState;

	private readonly OrtMemoryInfo _dmlMemoryInfo;

	private readonly GpuTensor _detectorTensor;

	private readonly GpuTensor _landmarkTensor;

	private readonly Dictionary<nint, ID3D12Resource> _sharedFrameResources = [];

	private readonly Queue<RetiredResourceBatch> _retiredSharedFrameResources = [];

	private ulong _fenceValue;

	private bool _disposed;

	public OrtValue DetectorTensor => _detectorTensor.Value;

	public OrtValue LandmarkTensor => _landmarkTensor.Value;

	public nint DevicePointer => _device.NativePointer;

	public nint CommandQueuePointer => _commandQueue.NativePointer;

	public MediaPipeGpuTensorPreprocessor(TextureNativeFrameLease firstFrame)
	{
		_device = CreateCompatibleDevice(firstFrame);
		_commandQueue = _device.CreateCommandQueue<ID3D12CommandQueue>(
			new CommandQueueDescription(CommandListType.Compute));
		_detectorCommands = new CommandContext(_device, sourceDescriptorStart: 0);
		_landmarkCommands = new CommandContext(_device, sourceDescriptorStart: 3);
		_fence = _device.CreateFence<ID3D12Fence>(0uL);

		_descriptorHeap = _device.CreateDescriptorHeap<ID3D12DescriptorHeap>(
			new DescriptorHeapDescription(
				DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
				6u,
				DescriptorHeapFlags.ShaderVisible));
		_descriptorSize = (int)_device.GetDescriptorHandleIncrementSize(
			DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

		byte[] shader = Compiler.Compile(
			ShaderSource,
			"CSMain",
			"AvatarBuilderMediaPipeGpuPreprocess.hlsl",
			"cs_5_0",
			ShaderFlags.OptimizationLevel3).ToArray();
		DescriptorRange[] sourceRanges =
		[
			new DescriptorRange(DescriptorRangeType.ShaderResourceView, 2u, 0u)
		];
		DescriptorRange[] outputRanges =
		[
			new DescriptorRange(DescriptorRangeType.UnorderedAccessView, 1u, 0u)
		];
		RootParameter[] rootParameters =
		[
			new RootParameter(
				new RootDescriptorTable(sourceRanges),
				ShaderVisibility.All),
			new RootParameter(
				new RootDescriptorTable(outputRanges),
				ShaderVisibility.All),
			new RootParameter(
				new RootConstants(0u, 0u, RootConstantCount),
				ShaderVisibility.All)
		];
		StaticSamplerDescription[] samplers =
		[
			new StaticSamplerDescription(
				0u,
				Filter.MinMagMipLinear,
				TextureAddressMode.Clamp,
				TextureAddressMode.Clamp,
				TextureAddressMode.Clamp,
				0f,
				0u,
				ComparisonFunction.Never,
				StaticBorderColor.TransparentBlack,
				0f,
				float.MaxValue,
				ShaderVisibility.All)
		];
		RootSignatureDescription rootDescription = new(
			RootSignatureFlags.None,
			rootParameters,
			samplers);
		_rootSignature = _device.CreateRootSignature(
			in rootDescription,
			RootSignatureVersion.Version1);
		_pipelineState = _device.CreateComputePipelineState<ID3D12PipelineState>(
			new ComputePipelineStateDescription
			{
				RootSignature = _rootSignature,
				ComputeShader = shader
			});

		_dmlMemoryInfo = new OrtMemoryInfo(
			"DML",
			OrtAllocatorType.DeviceAllocator,
			0,
			OrtMemType.Default);
		_detectorTensor = CreateTensor(DetectorSize, 2);
		_landmarkTensor = CreateTensor(LandmarkSize, 5);
	}

	public bool CanProcess(TextureNativeFrameLease frame)
	{
		if (_disposed
			|| !frame.IsValid
			|| !frame.MediaSubtype.Contains("NV12", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (!IsNativeD3D12Resource(frame))
		{
			return frame.D3D12SharedTextureHandle != IntPtr.Zero;
		}

		try
		{
			using ID3D12Resource resource = WrapResource(frame.Resource);
			using ID3D12Device frameDevice = resource.GetDevice<ID3D12Device>();
			return frameDevice.NativePointer == _device.NativePointer;
		}
		catch
		{
			return false;
		}
	}

	public MediaPipeDetectorTransform PreprocessDetector(TextureNativeFrameLease frame)
	{
		float scale = Math.Min(
			(float)DetectorSize / frame.Width,
			(float)DetectorSize / frame.Height);
		int resizedWidth = Math.Max(1, (int)MathF.Round(frame.Width * scale));
		int resizedHeight = Math.Max(1, (int)MathF.Round(frame.Height * scale));
		int left = (DetectorSize - resizedWidth) / 2;
		int top = (DetectorSize - resizedHeight) / 2;
		Dispatch(
			frame,
			_detectorCommands,
			_detectorTensor,
			DetectorSize,
			[
				frame.Width,
				frame.Height,
				DetectorSize,
				DetectorSize,
				left,
				top,
				resizedWidth,
				resizedHeight,
				0f,
				0f,
				1f,
				0f,
				0f,
				1f,
				0f,
				1f
			]);
		return new MediaPipeDetectorTransform(
			scale,
			left,
			top,
			frame.Width,
			frame.Height);
	}

	public void PreprocessLandmarks(
		TextureNativeFrameLease frame,
		MediaPipeGpuRoi roi)
	{
		float cosine = MathF.Cos(roi.Angle);
		float sine = MathF.Sin(roi.Angle);
		Dispatch(
			frame,
			_landmarkCommands,
			_landmarkTensor,
			LandmarkSize,
			[
				frame.Width,
				frame.Height,
				LandmarkSize,
				LandmarkSize,
				0f,
				0f,
				0f,
				0f,
				roi.CenterX,
				roi.CenterY,
				cosine,
				sine,
				-sine,
				cosine,
				roi.Size,
				0f
			]);
	}

	public void SignalDetectorInferenceSubmitted()
	{
		SignalInferenceSubmitted(_detectorCommands);
	}

	public void SignalLandmarkInferenceSubmitted()
	{
		SignalInferenceSubmitted(_landmarkCommands);
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		try
		{
			WaitForGpu();
		}
		catch (TimeoutException)
		{
			// Releasing resources that a wedged GPU may still own is less safe
			// than abandoning this failed tracker instance to process teardown.
			return;
		}
		catch
		{
		}
		ReleaseSharedFrameResources();
		_landmarkTensor.Dispose();
		_detectorTensor.Dispose();
		_dmlMemoryInfo.Dispose();
		_pipelineState.Dispose();
		_rootSignature.Dispose();
		_descriptorHeap.Dispose();
		_d3d11ProducerFence?.Dispose();
		_d3d11ProducerFence = null;
		_d3d11ProducerFenceHandle = IntPtr.Zero;
		_fence.Dispose();
		_fenceEvent.Dispose();
		_landmarkCommands.Dispose();
		_detectorCommands.Dispose();
		_commandQueue.Dispose();
		_device.Dispose();
	}

	private GpuTensor CreateTensor(int size, int descriptorIndex)
	{
		long elementCount = checked((long)size * size * TensorChannels);
		long byteLength = checked(elementCount * sizeof(float));
		ID3D12Resource resource = _device.CreateCommittedResource<ID3D12Resource>(
			new HeapProperties(HeapType.Default),
			HeapFlags.None,
			ResourceDescription.Buffer(
				(ulong)byteLength,
				ResourceFlags.AllowUnorderedAccess,
				0uL),
			ResourceStates.Common);
		UnorderedAccessViewDescription view = new()
		{
			Format = Format.Unknown,
			ViewDimension = UnorderedAccessViewDimension.Buffer,
			Buffer = new BufferUnorderedAccessView
			{
				FirstElement = 0uL,
				NumElements = (uint)elementCount,
				StructureByteStride = sizeof(float),
				CounterOffsetInBytes = 0uL,
				Flags = BufferUnorderedAccessViewFlags.None
			}
		};
		_device.CreateUnorderedAccessView(
			resource,
			null,
			view,
			GetCpuDescriptorHandle(descriptorIndex));

		DmlGpuAllocation allocation = DmlGpuAllocationBridge.Create(resource.NativePointer);
		OrtValue value = OrtValue.CreateTensorValueWithData(
			_dmlMemoryInfo,
			TensorElementType.Float,
			[1L, size, size, TensorChannels],
			allocation.Pointer,
			byteLength);
		return new GpuTensor(resource, allocation, value, byteLength, descriptorIndex);
	}

	private void Dispatch(
		TextureNativeFrameLease frame,
		CommandContext commands,
		GpuTensor output,
		int outputSize,
		ReadOnlySpan<float> constants)
	{
		if (_disposed)
		{
			throw new ObjectDisposedException(nameof(MediaPipeGpuTensorPreprocessor));
		}
		if (constants.Length != RootConstantCount)
		{
			throw new ArgumentException(
				$"The GPU preprocessor requires {RootConstantCount} constants.",
				nameof(constants));
		}

		WaitForContext(commands);
		WaitForD3D11Producer(frame);
		ID3D12Resource cameraResource = OpenFrameResource(
			frame,
			out bool disposeCameraResource);
		try
		{
			CreateNv12SourceViews(
				cameraResource,
				commands.SourceDescriptorStart);
			commands.CommandAllocator.Reset();
			commands.CommandList.Reset(commands.CommandAllocator, _pipelineState);
		ResourceBarrier outputToWrite = ResourceBarrier.BarrierTransition(
			output.Resource,
			ResourceStates.Common,
			ResourceStates.UnorderedAccess);
			commands.CommandList.ResourceBarrier(
				new Span<ResourceBarrier>(ref outputToWrite));
			commands.CommandList.SetComputeRootSignature(_rootSignature);
			commands.CommandList.SetDescriptorHeaps(
			new ReadOnlySpan<ID3D12DescriptorHeap>(in _descriptorHeap));
			commands.CommandList.SetComputeRootDescriptorTable(
			0u,
				GetGpuDescriptorHandle(commands.SourceDescriptorStart));
			commands.CommandList.SetComputeRootDescriptorTable(
			1u,
			GetGpuDescriptorHandle(output.DescriptorIndex));
			for (int index = 0; index < constants.Length; index++)
			{
				commands.CommandList.SetComputeRoot32BitConstant(
				2u,
				BitConverter.SingleToUInt32Bits(constants[index]),
				(uint)index);
			}
			commands.CommandList.Dispatch(
			(uint)((outputSize + 7) / 8),
			(uint)((outputSize + 7) / 8),
			1u);
			ResourceBarrier unorderedAccess =
				ResourceBarrier.BarrierUnorderedAccessView(output.Resource);
			commands.CommandList.ResourceBarrier(
				new Span<ResourceBarrier>(ref unorderedAccess));
			ResourceBarrier outputToCommon = ResourceBarrier.BarrierTransition(
			output.Resource,
			ResourceStates.UnorderedAccess,
			ResourceStates.Common);
			commands.CommandList.ResourceBarrier(
				new Span<ResourceBarrier>(ref outputToCommon));
			commands.CommandList.Close();
			_commandQueue.ExecuteCommandList(commands.CommandList);
		}
		finally
		{
			if (disposeCameraResource)
			{
				cameraResource.Dispose();
			}
		}
	}

	private void WaitForD3D11Producer(TextureNativeFrameLease frame)
	{
		nint producerFenceHandle = frame.D3D11ProducerFenceHandle;
		ulong producerFenceValue = frame.D3D11ProducerFenceValue;
		if (producerFenceHandle == IntPtr.Zero || producerFenceValue == 0uL)
		{
			return;
		}
		bool generationChanged = _d3d11ProducerFence is null
			|| _d3d11ProducerFenceHandle != producerFenceHandle
			|| producerFenceValue < _lastD3D11ProducerFenceValue;
		if (generationChanged)
		{
			RetireSharedFrameResources();
			_d3d11ProducerFence?.Dispose();
			_d3d11ProducerFence =
				_device.OpenSharedHandle<ID3D12Fence>(producerFenceHandle);
			_d3d11ProducerFenceHandle = producerFenceHandle;
		}
		_lastD3D11ProducerFenceValue = producerFenceValue;
		_commandQueue.Wait(_d3d11ProducerFence, producerFenceValue);
		ReleaseCompletedSharedFrameResources();
	}

	private void CreateNv12SourceViews(
		ID3D12Resource cameraResource,
		int sourceDescriptorStart)
	{
		_device.CreateShaderResourceView(
			cameraResource,
			new ShaderResourceViewDescription
			{
				Format = Format.R8_UNorm,
				ViewDimension =
					Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
				Shader4ComponentMapping = 5768u,
				Texture2D = new Texture2DShaderResourceView
				{
					MipLevels = 1u,
					PlaneSlice = 0u
				}
			},
			GetCpuDescriptorHandle(sourceDescriptorStart));
		_device.CreateShaderResourceView(
			cameraResource,
			new ShaderResourceViewDescription
			{
				Format = Format.R8G8_UNorm,
				ViewDimension =
					Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
				Shader4ComponentMapping = 5768u,
				Texture2D = new Texture2DShaderResourceView
				{
					MipLevels = 1u,
					PlaneSlice = 1u
				}
			},
			GetCpuDescriptorHandle(sourceDescriptorStart + 1));
	}

	private ID3D12Resource OpenFrameResource(
		TextureNativeFrameLease frame,
		out bool disposeResource)
	{
		if (!frame.MediaSubtype.Contains("NV12", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException(
				$"GPU tracking requires an NV12 texture, not {frame.MediaSubtype}.");
		}
		if (IsNativeD3D12Resource(frame))
		{
			disposeResource = true;
			return WrapResource(frame.Resource);
		}
		if (frame.D3D12SharedTextureHandle != IntPtr.Zero)
		{
			disposeResource = false;
			if (!_sharedFrameResources.TryGetValue(
				frame.D3D12SharedTextureHandle,
				out ID3D12Resource? resource))
			{
				resource = _device.OpenSharedHandle<ID3D12Resource>(
					frame.D3D12SharedTextureHandle);
				_sharedFrameResources.Add(
					frame.D3D12SharedTextureHandle,
					resource);
			}
			return resource;
		}
		disposeResource = false;
		throw new InvalidOperationException(
			"The camera frame does not carry a GPU texture.");
	}

	private static ID3D12Resource WrapResource(nint resource)
	{
		Marshal.AddRef(resource);
		return new ID3D12Resource(resource);
	}

	private static ID3D12Device CreateCompatibleDevice(
		TextureNativeFrameLease frame)
	{
		if (IsNativeD3D12Resource(frame))
		{
			using ID3D12Resource resource = WrapResource(frame.Resource);
			return resource.GetDevice<ID3D12Device>();
		}
		if (frame.D3D12SharedTextureHandle != IntPtr.Zero)
		{
			return D3D12CreateDevice<ID3D12Device>(
				null,
				FeatureLevel.Level_12_0);
		}
		throw new InvalidOperationException(
			"The first GPU tracking frame does not carry a D3D12 texture.");
	}

	private static bool IsNativeD3D12Resource(TextureNativeFrameLease frame)
	{
		return frame.Resource != IntPtr.Zero
			&& frame.DeviceMode.StartsWith(
				"D3D12",
				StringComparison.OrdinalIgnoreCase);
	}

	private CpuDescriptorHandle GetCpuDescriptorHandle(int index)
	{
		return _descriptorHeap.GetCPUDescriptorHandleForHeapStart()
			+ index * _descriptorSize;
	}

	private GpuDescriptorHandle GetGpuDescriptorHandle(int index)
	{
		return _descriptorHeap.GetGPUDescriptorHandleForHeapStart()
			+ index * _descriptorSize;
	}

	private void WaitForGpu()
	{
		ulong fenceValue = SignalQueue();
		WaitForFence(fenceValue, "shut down");
	}

	private void SignalInferenceSubmitted(CommandContext commands)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		commands.ReadyFenceValue = SignalQueue();
	}

	private ulong SignalQueue()
	{
		_fenceValue++;
		_commandQueue.Signal(_fence, _fenceValue);
		return _fenceValue;
	}

	private void WaitForContext(CommandContext commands)
	{
		ulong fenceValue = commands.ReadyFenceValue;
		if (fenceValue == 0uL)
		{
			return;
		}
		WaitForFence(fenceValue, "reuse a MediaPipe GPU tensor");
		commands.ReadyFenceValue = 0uL;
	}

	private void WaitForFence(ulong fenceValue, string operation)
	{
		if (_fence.CompletedValue >= fenceValue)
		{
			return;
		}
		_fence.SetEventOnCompletion(fenceValue, _fenceEvent);
		if (!_fenceEvent.WaitOne(GpuWaitTimeout))
		{
			throw new TimeoutException(
				"MediaPipe GPU did not become ready to " +
				$"{operation} within {GpuWaitTimeout.TotalMilliseconds:0} ms.");
		}
	}

	private void RetireSharedFrameResources()
	{
		if (_sharedFrameResources.Count == 0)
		{
			return;
		}
		ulong readyFenceValue = Math.Max(
			_detectorCommands.ReadyFenceValue,
			_landmarkCommands.ReadyFenceValue);
		_retiredSharedFrameResources.Enqueue(
			new RetiredResourceBatch(
				readyFenceValue,
				[.. _sharedFrameResources.Values]));
		_sharedFrameResources.Clear();
	}

	private void ReleaseCompletedSharedFrameResources()
	{
		while (_retiredSharedFrameResources.TryPeek(out RetiredResourceBatch? batch)
			&& (batch.FenceValue == 0uL
				|| _fence.CompletedValue >= batch.FenceValue))
		{
			_retiredSharedFrameResources.Dequeue();
			batch.Dispose();
		}
	}

	private void ReleaseSharedFrameResources()
	{
		foreach (ID3D12Resource resource in _sharedFrameResources.Values)
		{
			resource.Dispose();
		}
		_sharedFrameResources.Clear();
		while (_retiredSharedFrameResources.TryDequeue(
			out RetiredResourceBatch? batch))
		{
			batch.Dispose();
		}
	}

	private sealed class CommandContext : IDisposable
	{
		public ID3D12CommandAllocator CommandAllocator { get; }

		public ID3D12GraphicsCommandList CommandList { get; }

		public int SourceDescriptorStart { get; }

		public ulong ReadyFenceValue { get; set; }

		public CommandContext(ID3D12Device device, int sourceDescriptorStart)
		{
			SourceDescriptorStart = sourceDescriptorStart;
			CommandAllocator =
				device.CreateCommandAllocator<ID3D12CommandAllocator>(
					CommandListType.Compute);
			CommandList =
				device.CreateCommandList<ID3D12GraphicsCommandList>(
					0u,
					CommandListType.Compute,
					CommandAllocator);
			CommandList.Close();
		}

		public void Dispose()
		{
			CommandList.Dispose();
			CommandAllocator.Dispose();
		}
	}

	private sealed class RetiredResourceBatch : IDisposable
	{
		public ulong FenceValue { get; }

		private readonly ID3D12Resource[] _resources;

		public RetiredResourceBatch(
			ulong fenceValue,
			ID3D12Resource[] resources)
		{
			FenceValue = fenceValue;
			_resources = resources;
		}

		public void Dispose()
		{
			foreach (ID3D12Resource resource in _resources)
			{
				resource.Dispose();
			}
		}
	}

	private sealed class GpuTensor : IDisposable
	{
		public ID3D12Resource Resource { get; }

		public DmlGpuAllocation Allocation { get; }

		public OrtValue Value { get; }

		public long ByteLength { get; }

		public int DescriptorIndex { get; }

		public GpuTensor(
			ID3D12Resource resource,
			DmlGpuAllocation allocation,
			OrtValue value,
			long byteLength,
			int descriptorIndex)
		{
			Resource = resource;
			Allocation = allocation;
			Value = value;
			ByteLength = byteLength;
			DescriptorIndex = descriptorIndex;
		}

		public void Dispose()
		{
			Value.Dispose();
			Allocation.Dispose();
			Resource.Dispose();
		}
	}
}
