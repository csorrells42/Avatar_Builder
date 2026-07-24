using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using AvatarBuilder.Modules.Webcam.Common;
using Vortice;
using Vortice.D3DCompiler;
using Vortice.DXGI;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

public sealed class Direct3D12PreviewHost : WebcamDirectX12ViewportHost
{
	private sealed record AcceptedCameraFrame(CameraFrame Frame, VideoFrameColorSettings ColorSettings, bool DenoiseEnabled, double DenoiseStrength, long FrameNumber) : IDisposable
	{
		public byte[] BgraBytes => Frame.BgraBytes;

		public int Width => Frame.Width;

		public int Height => Frame.Height;

		public int Stride => Frame.Stride;

		public byte[]? Nv12Bytes => Frame.Nv12Bytes;

		public int Nv12Stride => Frame.Nv12Stride;

		public string FrameFormat => Frame.Format;

		public void Dispose()
		{
			Frame.Dispose();
		}
	}

	private sealed record AcceptedTextureFrame(TextureNativeFrameLease Frame, VideoFrameColorSettings ColorSettings, bool DenoiseEnabled, double DenoiseStrength, string? SharedBridgeFailureReason, TexturePreviewRead PreviewRead) : IDisposable
	{
		public void Dispose()
		{
			Frame.Dispose();
		}
	}

	private sealed class Direct3D12SwapChainRenderer : IDisposable
	{
		private sealed class FrameResource : IDisposable
		{
			public ID3D12CommandAllocator CommandAllocator { get; }

			public ID3D12Resource? CameraUploadBuffer { get; private set; }

			public nint CameraUploadPointer { get; private set; }

			public ID3D12Resource? BgraColorSettingsBuffer { get; private set; }

			public nint BgraColorSettingsPointer { get; private set; }

			public ID3D12Resource? Nv12YUploadBuffer { get; private set; }

			public nint Nv12YUploadPointer { get; private set; }

			public ID3D12Resource? Nv12UvUploadBuffer { get; private set; }

			public nint Nv12UvUploadPointer { get; private set; }

			public ulong FenceValue { get; set; }

			public FrameResource(ID3D12CommandAllocator commandAllocator)
			{
				CommandAllocator = commandAllocator;
			}

			public void CreateCameraUploadBuffer(ID3D12Device device, ulong uploadBytes)
			{
				ReleaseCameraUploadBuffer();
				CameraUploadBuffer = CreateMappedUploadBuffer(device, uploadBytes, out var mappedPointer);
				CameraUploadPointer = mappedPointer;
			}

			public void CreateBgraColorSettingsBuffer(ID3D12Device device, ulong uploadBytes)
			{
				ReleaseBgraColorSettingsBuffer();
				BgraColorSettingsBuffer = CreateMappedUploadBuffer(device, uploadBytes, out var mappedPointer);
				BgraColorSettingsPointer = mappedPointer;
			}

			public void CreateNv12UploadBuffers(ID3D12Device device, ulong yUploadBytes, ulong uvUploadBytes)
			{
				ReleaseNv12UploadBuffers();
				Nv12YUploadBuffer = CreateMappedUploadBuffer(device, yUploadBytes, out var mappedPointer);
				Nv12YUploadPointer = mappedPointer;
				Nv12UvUploadBuffer = CreateMappedUploadBuffer(device, uvUploadBytes, out var mappedPointer2);
				Nv12UvUploadPointer = mappedPointer2;
			}

			public void ReleaseCameraUploadBuffer()
			{
				if ((object)CameraUploadBuffer != null)
				{
					CameraUploadBuffer.Unmap(0u);
					CameraUploadBuffer.Dispose();
					CameraUploadBuffer = null;
				}
				CameraUploadPointer = IntPtr.Zero;
			}

			public void ReleaseBgraColorSettingsBuffer()
			{
				if ((object)BgraColorSettingsBuffer != null)
				{
					BgraColorSettingsBuffer.Unmap(0u);
					BgraColorSettingsBuffer.Dispose();
					BgraColorSettingsBuffer = null;
				}
				BgraColorSettingsPointer = IntPtr.Zero;
			}

			public void ReleaseNv12UploadBuffers()
			{
				if ((object)Nv12YUploadBuffer != null)
				{
					Nv12YUploadBuffer.Unmap(0u);
					Nv12YUploadBuffer.Dispose();
					Nv12YUploadBuffer = null;
				}
				if ((object)Nv12UvUploadBuffer != null)
				{
					Nv12UvUploadBuffer.Unmap(0u);
					Nv12UvUploadBuffer.Dispose();
					Nv12UvUploadBuffer = null;
				}
				Nv12YUploadPointer = IntPtr.Zero;
				Nv12UvUploadPointer = IntPtr.Zero;
			}

			public void Dispose()
			{
				ReleaseCameraUploadBuffer();
				ReleaseBgraColorSettingsBuffer();
				ReleaseNv12UploadBuffers();
				CommandAllocator.Dispose();
			}

			private unsafe static ID3D12Resource CreateMappedUploadBuffer(ID3D12Device device, ulong uploadBytes, out nint mappedPointer)
			{
				ID3D12Resource iD3D12Resource = device.CreateCommittedResource<ID3D12Resource>(new HeapProperties(HeapType.Upload), HeapFlags.None, ResourceDescription.Buffer(uploadBytes, ResourceFlags.None, 0uL), ResourceStates.GenericRead);
				void* ptr = null;
				iD3D12Resource.Map(0u, null, &ptr).CheckError();
				mappedPointer = (nint)ptr;
				return iD3D12Resource;
			}
		}

		private const int FrameCount = 3;

		private const int D3D12DefaultShader4ComponentMappingValue = 5768;

		private const int BgraColorSettingsDescriptorStart = 3;

		private const int BgraColorSettingsBufferBytes = 256;

		private readonly ID3D12Device _device;

		private readonly ID3D12CommandQueue _commandQueue;

		private readonly IDXGIFactory4 _factory;

		private readonly ID3D12GraphicsCommandList _commandList;

		private readonly ID3D12Fence _fence;

		private readonly AutoResetEvent _fenceEvent = new AutoResetEvent(initialState: false);

		private readonly ID3D12DescriptorHeap _rtvHeap;

		private readonly ID3D12DescriptorHeap _srvHeap;

		private readonly int _rtvDescriptorSize;

		private readonly int _srvDescriptorSize;

		private readonly ID3D12Resource?[] _renderTargets = new ID3D12Resource[3];

		private ID3D12RootSignature? _previewRootSignature;

		private ID3D12PipelineState? _previewPipelineState;

		private ID3D12RootSignature? _nv12PreviewRootSignature;

		private ID3D12PipelineState? _nv12PreviewPipelineState;

		private Direct2DTrackingOverlayRenderer? _trackingOverlayRenderer;

		private readonly FrameResource[] _frameResources = new FrameResource[3];

		private ID3D12Resource? _cameraTexture;

		private PlacedSubresourceFootPrint _cameraTextureFootprint;

		private ResourceStates _cameraTextureState;

		private ID3D12Resource? _nv12YTexture;

		private ID3D12Resource? _nv12UvTexture;

		private PlacedSubresourceFootPrint _nv12YFootprint;

		private PlacedSubresourceFootPrint _nv12UvFootprint;

		private ResourceStates _nv12YTextureState;

		private ResourceStates _nv12UvTextureState;

		private IDXGISwapChain3 _swapChain;

		private ulong _fenceValue;

		private ID3D12Fence? _d3d11ProducerFence;

		private nint _d3d11ProducerFenceHandle;

		private long _lastSubmittedFenceValue;

		private int _cameraTextureWidth;

		private int _cameraTextureHeight;

		private int _nv12TextureWidth;

		private int _nv12TextureHeight;

		private int _viewportWidth;

		private int _viewportHeight;

		private int _presentationRefreshRequested;

		private bool _disposed;

		private bool _shaderPreviewUnavailable;

		private bool _nv12PreviewUnavailable;

		private string? _nv12PreviewFailureReason;

		private bool _nativeTexturePreviewUnavailable;

		private string? _nativeTexturePreviewFailureReason;

		private bool _sharedD3D11BridgePreviewUnavailable;

		private string? _sharedD3D11BridgePreviewFailureReason;

		private readonly bool _usesSharedCaptureDevice;

		private const string PreviewShaderSource = "Texture2D<float4> CameraFrame : register(t0);\r\nSamplerState CameraSampler : register(s0);\r\n\r\ncbuffer ColorSettings : register(b0)\r\n{\r\n    float ExposureOffset;\r\n    float Contrast;\r\n    float Saturation;\r\n    float Warmth;\r\n    float DenoiseAmount;\r\n    float DenoiseEdgeThreshold;\n    float TexelWidth;\n    float TexelHeight;\n};\n\r\nstruct VertexOutput\r\n{\r\n    float4 Position : SV_POSITION;\r\n    float2 TexCoord : TEXCOORD0;\r\n};\r\n\r\nVertexOutput VSMain(uint vertexId : SV_VertexID)\r\n{\r\n    float2 positions[3] =\r\n    {\r\n        float2(-1.0, -1.0),\r\n        float2(-1.0, 3.0),\r\n        float2(3.0, -1.0)\r\n    };\r\n    float2 texCoords[3] =\r\n    {\r\n        float2(0.0, 1.0),\r\n        float2(0.0, -1.0),\r\n        float2(2.0, 1.0)\r\n    };\r\n\r\n    VertexOutput output;\r\n    output.Position = float4(positions[vertexId], 0.0, 1.0);\r\n    output.TexCoord = texCoords[vertexId];\r\n    return output;\r\n}\r\n\r\nfloat3 ApplyColorPolish(float3 rgb)\r\n{\r\n    float3 rgb255 = rgb * 255.0;\r\n    rgb255.r = ((rgb255.r + ExposureOffset + Warmth - 128.0) * Contrast) + 128.0;\r\n    rgb255.g = ((rgb255.g + ExposureOffset - 128.0) * Contrast) + 128.0;\r\n    rgb255.b = ((rgb255.b + ExposureOffset - Warmth - 128.0) * Contrast) + 128.0;\r\n\r\n    float luma = dot(rgb255, float3(0.2126, 0.7152, 0.0722));\r\n    rgb255 = luma + (rgb255 - luma) * Saturation;\r\n    return saturate(rgb255 / 255.0);\r\n}\r\n\r\nfloat Luma(float3 rgb)\r\n{\r\n    return dot(rgb, float3(0.2126, 0.7152, 0.0722));\r\n}\r\n\r\nfloat EdgeAwareWeight(float sampleY, float centerY)\r\n{\r\n    return saturate(1.0 - abs(sampleY - centerY) / max(DenoiseEdgeThreshold, 0.0001));\r\n}\r\n\r\nfloat3 ApplyBgraDenoise(float3 centerRgb, float2 texCoord)\r\n{\r\n    if (DenoiseAmount <= 0.001)\r\n    {\r\n        return centerRgb;\r\n    }\r\n\r\n    float2 xOffset = float2(TexelWidth, 0.0);\r\n    float2 yOffset = float2(0.0, TexelHeight);\r\n    float3 leftRgb = CameraFrame.Sample(CameraSampler, texCoord - xOffset).rgb;\r\n    float3 rightRgb = CameraFrame.Sample(CameraSampler, texCoord + xOffset).rgb;\r\n    float3 upRgb = CameraFrame.Sample(CameraSampler, texCoord - yOffset).rgb;\r\n    float3 downRgb = CameraFrame.Sample(CameraSampler, texCoord + yOffset).rgb;\r\n\r\n    float centerY = Luma(centerRgb);\r\n    float centerWeight = 2.0;\r\n    float leftWeight = EdgeAwareWeight(Luma(leftRgb), centerY);\r\n    float rightWeight = EdgeAwareWeight(Luma(rightRgb), centerY);\r\n    float upWeight = EdgeAwareWeight(Luma(upRgb), centerY);\r\n    float downWeight = EdgeAwareWeight(Luma(downRgb), centerY);\r\n    float totalWeight = centerWeight + leftWeight + rightWeight + upWeight + downWeight;\r\n    float3 smoothedRgb = (\r\n        centerRgb * centerWeight\r\n        + leftRgb * leftWeight\r\n        + rightRgb * rightWeight\r\n        + upRgb * upWeight\r\n        + downRgb * downWeight) / max(totalWeight, 0.0001);\r\n    return lerp(centerRgb, smoothedRgb, DenoiseAmount);\r\n}\r\n\r\nfloat4 PSMain(VertexOutput input) : SV_TARGET\r\n{\r\n    float4 color = CameraFrame.Sample(CameraSampler, input.TexCoord);\r\n    float3 denoised = ApplyBgraDenoise(color.rgb, input.TexCoord);\r\n    return float4(ApplyColorPolish(denoised), 1.0);\r\n}";

		private const string Nv12PreviewShaderSource = "Texture2D<float> CameraLuma : register(t0);\r\nTexture2D<float2> CameraChroma : register(t1);\r\nSamplerState CameraSampler : register(s0);\r\n\r\ncbuffer Nv12PreviewSettings : register(b0)\r\n{\r\n    float ExposureOffset;\r\n    float Contrast;\r\n    float Saturation;\r\n    float Warmth;\r\n    float DenoiseAmount;\r\n    float DenoiseEdgeThreshold;\n    float TexelWidth;\n    float TexelHeight;\n    float SwapChromaChannels;\n};\n\r\nstruct VertexOutput\r\n{\r\n    float4 Position : SV_POSITION;\r\n    float2 TexCoord : TEXCOORD0;\r\n};\r\n\r\nVertexOutput VSMain(uint vertexId : SV_VertexID)\r\n{\r\n    float2 positions[3] =\r\n    {\r\n        float2(-1.0, -1.0),\r\n        float2(-1.0, 3.0),\r\n        float2(3.0, -1.0)\r\n    };\r\n    float2 texCoords[3] =\r\n    {\r\n        float2(0.0, 1.0),\r\n        float2(0.0, -1.0),\r\n        float2(2.0, 1.0)\r\n    };\r\n\r\n    VertexOutput output;\r\n    output.Position = float4(positions[vertexId], 0.0, 1.0);\r\n    output.TexCoord = texCoords[vertexId];\r\n    return output;\r\n}\r\n\r\nfloat NormalizeLuma(float rawY)\r\n{\r\n    return saturate((rawY - (16.0 / 255.0)) * (255.0 / 219.0));\r\n}\r\n\r\nfloat EdgeAwareWeight(float sampleY, float centerY)\r\n{\r\n    return saturate(1.0 - abs(sampleY - centerY) / max(DenoiseEdgeThreshold, 0.0001));\r\n}\r\n\r\nfloat SampleNormalizedLuma(float2 texCoord)\r\n{\r\n    return NormalizeLuma(CameraLuma.Sample(CameraSampler, texCoord));\r\n}\r\n\r\nfloat ApplyLumaDenoise(float centerY, float2 texCoord)\r\n{\r\n    if (DenoiseAmount <= 0.001)\r\n    {\r\n        return centerY;\r\n    }\r\n\r\n    float2 xOffset = float2(TexelWidth, 0.0);\r\n    float2 yOffset = float2(0.0, TexelHeight);\r\n    float leftY = SampleNormalizedLuma(texCoord - xOffset);\r\n    float rightY = SampleNormalizedLuma(texCoord + xOffset);\r\n    float upY = SampleNormalizedLuma(texCoord - yOffset);\r\n    float downY = SampleNormalizedLuma(texCoord + yOffset);\r\n\r\n    float centerWeight = 2.0;\r\n    float leftWeight = EdgeAwareWeight(leftY, centerY);\r\n    float rightWeight = EdgeAwareWeight(rightY, centerY);\r\n    float upWeight = EdgeAwareWeight(upY, centerY);\r\n    float downWeight = EdgeAwareWeight(downY, centerY);\r\n    float totalWeight = centerWeight + leftWeight + rightWeight + upWeight + downWeight;\r\n    float smoothedY = (\r\n        centerY * centerWeight\r\n        + leftY * leftWeight\r\n        + rightY * rightWeight\r\n        + upY * upWeight\r\n        + downY * downWeight) / max(totalWeight, 0.0001);\r\n    return lerp(centerY, smoothedY, DenoiseAmount);\r\n}\r\n\r\nfloat3 ApplyColorPolish(float3 rgb)\r\n{\r\n    float3 rgb255 = rgb * 255.0;\r\n    rgb255.r = ((rgb255.r + ExposureOffset + Warmth - 128.0) * Contrast) + 128.0;\r\n    rgb255.g = ((rgb255.g + ExposureOffset - 128.0) * Contrast) + 128.0;\r\n    rgb255.b = ((rgb255.b + ExposureOffset - Warmth - 128.0) * Contrast) + 128.0;\r\n\r\n    float luma = dot(rgb255, float3(0.2126, 0.7152, 0.0722));\r\n    rgb255 = luma + (rgb255 - luma) * Saturation;\r\n    return saturate(rgb255 / 255.0);\r\n}\r\n\r\nfloat4 PSMain(VertexOutput input) : SV_TARGET\r\n{\r\n    float y = NormalizeLuma(CameraLuma.Sample(CameraSampler, input.TexCoord));\n    y = ApplyLumaDenoise(y, input.TexCoord);\n    float2 uv = CameraChroma.Sample(CameraSampler, input.TexCoord) - float2(0.5, 0.5);\n    uv = SwapChromaChannels > 0.5 ? uv.yx : uv;\n    float3 rgb = float3(\n        y + 1.5748 * uv.y,\r\n        y - 0.1873 * uv.x - 0.4681 * uv.y,\r\n        y + 1.8556 * uv.x);\r\n    return float4(ApplyColorPolish(saturate(rgb)), 1.0);\r\n}";

		public string DeviceDescription
		{
			get
			{
				if (!_usesSharedCaptureDevice)
				{
					return "Direct3D 12 / DXGI flip model";
				}
				return "Direct3D 12 / DXGI flip model on shared capture device";
			}
		}

		public string LastNv12PreviewFailureReason => _nv12PreviewFailureReason ?? "no NV12 failure detail";

		public ulong LastSubmittedFenceValue =>
			checked((ulong)Math.Max(
				0L,
				Volatile.Read(ref _lastSubmittedFenceValue)));

		public Direct3D12SwapChainRenderer(nint hwnd, int width, int height, nint nativeD3D12Device = 0)
		{
			_viewportWidth = width;
			_viewportHeight = height;
			if (nativeD3D12Device != IntPtr.Zero)
			{
				_device = new ID3D12Device(nativeD3D12Device);
				_usesSharedCaptureDevice = true;
			}
			else
			{
				_device = D3D12.D3D12CreateDevice<ID3D12Device>(null, FeatureLevel.Level_12_0);
			}
			_commandQueue = _device.CreateCommandQueue<ID3D12CommandQueue>(new CommandQueueDescription(CommandListType.Direct));
			_factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(debug: false);
			SwapChainDescription1 desc = new SwapChainDescription1
			{
				Width = (uint)width,
				Height = (uint)height,
				Format = Format.B8G8R8A8_UNorm,
				Stereo = false,
				SampleDescription = new SampleDescription(1u, 0u),
				BufferUsage = Usage.RenderTargetOutput,
				BufferCount = 3u,
				Scaling = Scaling.Stretch,
				SwapEffect = SwapEffect.FlipDiscard,
				AlphaMode = AlphaMode.Ignore,
				Flags = SwapChainFlags.None
			};
			using IDXGISwapChain1 iDXGISwapChain = _factory.CreateSwapChainForHwnd(_commandQueue, hwnd, desc);
			_swapChain = iDXGISwapChain.QueryInterface<IDXGISwapChain3>();
			_factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAltEnter);
			_rtvHeap = _device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 3u));
			_srvHeap = _device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 6u, DescriptorHeapFlags.ShaderVisible));
			_rtvDescriptorSize = (int)_device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
			_srvDescriptorSize = (int)_device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
			CreateRenderTargetViews();
			TryCreatePreviewShaderPipeline();
			TryCreateNv12PreviewShaderPipeline();
			_trackingOverlayRenderer = Direct2DTrackingOverlayRenderer.TryCreate(_device, _commandQueue, 3);
			TryAttachTrackingOverlayRenderer();
			for (int i = 0; i < 3; i++)
			{
				_frameResources[i] = new FrameResource(_device.CreateCommandAllocator<ID3D12CommandAllocator>(CommandListType.Direct));
				_frameResources[i].CreateBgraColorSettingsBuffer(_device, 256uL);
				_device.CreateConstantBufferView(new ConstantBufferViewDescription
				{
					BufferLocation = _frameResources[i].BgraColorSettingsBuffer.GPUVirtualAddress,
					SizeInBytes = 256u
				}, GetSrvCpuHandle(3 + i));
			}
			_commandList = _device.CreateCommandList<ID3D12GraphicsCommandList>(0u, CommandListType.Direct, _frameResources[0].CommandAllocator);
			_commandList.Close();
			_fence = _device.CreateFence<ID3D12Fence>(0uL);
		}

		public void RenderProofFrame(long frameNumber)
		{
			if (!_disposed)
			{
				int frameIndex;
				FrameResource frameResource = BeginFrame(out frameIndex);
				ID3D12Resource? resource = _renderTargets[frameIndex] ?? throw new InvalidOperationException("DX12 render target is not ready.");
				ResourceBarrier resourceBarrier = ResourceBarrier.BarrierTransition(resource, ResourceStates.Common, ResourceStates.RenderTarget);
				ID3D12GraphicsCommandList commandList = _commandList;
				ResourceBarrier reference = resourceBarrier;
				commandList.ResourceBarrier(new Span<ResourceBarrier>(ref reference));
				CpuDescriptorHandle rtvHandle = GetRtvHandle(frameIndex);
				float num = (float)((double)(frameNumber % 120) / 120.0);
				_commandList.OMSetRenderTargets(rtvHandle);
				_commandList.ClearRenderTargetView(rtvHandle, new Color4(0.02f + num * 0.08f, 0.08f, 0.12f + num * 0.18f), 0u, null);
				ResourceBarrier resourceBarrier2 = ResourceBarrier.BarrierTransition(resource, ResourceStates.RenderTarget, ResourceStates.Common);
				ID3D12GraphicsCommandList commandList2 = _commandList;
				ResourceBarrier reference2 = resourceBarrier2;
				commandList2.ResourceBarrier(new Span<ResourceBarrier>(ref reference2));
				ExecuteAndPresent(frameResource);
			}
		}

		public void RenderBgraFrame(byte[] bgraBytes, int width, int height, int stride, long frameNumber, VideoFrameColorSettings colorSettings = default(VideoFrameColorSettings), bool denoiseEnabled = false, double denoiseStrength = 0.0, PreviewTrackingOverlay? trackingOverlay = null)
		{
			if (!_disposed && bgraBytes.Length >= stride * height)
			{
				PreviewTrackingOverlay trackingOverlay2 = trackingOverlay ?? PreviewTrackingOverlay.Empty;
				if (!TryRenderBgraFrameWithShader(bgraBytes, width, height, stride, colorSettings, denoiseEnabled, denoiseStrength, trackingOverlay2))
				{
					RenderBgraFrameToBackBuffer(bgraBytes, width, height, stride, trackingOverlay2);
				}
			}
		}

		public bool RenderNv12Frame(byte[] nv12Bytes, int width, int height, int stride, long frameNumber, VideoFrameColorSettings colorSettings, bool denoiseEnabled, double denoiseStrength, PreviewTrackingOverlay trackingOverlay, bool swapChromaChannels = false)
		{
			int num = (height + 1) / 2;
			if (_disposed || stride < width || nv12Bytes.Length < stride * height + stride * num)
			{
				_nv12PreviewFailureReason = (_disposed ? "renderer disposed" : $"invalid NV12 payload: stride {stride}, width {width}, bytes {nv12Bytes.Length}, expected {stride * height + stride * num}");
				return false;
			}
			bool num2 = TryRenderNv12FrameWithShader(nv12Bytes, width, height, stride, colorSettings, denoiseEnabled, denoiseStrength, trackingOverlay, swapChromaChannels);
			if (num2)
			{
				_nv12PreviewFailureReason = null;
			}
			return num2;
		}

		public bool RenderNativeTextureFrame(TextureNativeFrameLease frame, VideoFrameColorSettings colorSettings, bool denoiseEnabled, double denoiseStrength, PreviewTrackingOverlay trackingOverlay, out string? failureReason)
		{
			failureReason = null;
			if (_disposed)
			{
				failureReason = "renderer disposed";
				return false;
			}
			if (!_usesSharedCaptureDevice)
			{
				failureReason = "presenter is not using the capture D3D12 device";
				return false;
			}
			if (_nativeTexturePreviewUnavailable)
			{
				failureReason = _nativeTexturePreviewFailureReason ?? "direct texture rendering disabled after an earlier failure";
				return false;
			}
			if ((object)_nv12PreviewRootSignature == null || (object)_nv12PreviewPipelineState == null)
			{
				failureReason = "NV12 shader pipeline unavailable";
				return false;
			}
			if (frame.Resource == IntPtr.Zero)
			{
				failureReason = "frame texture resource is missing";
				return false;
			}
			if (!frame.MediaSubtype.Contains("NV12", StringComparison.OrdinalIgnoreCase))
			{
				failureReason = "media subtype " + frame.MediaSubtype + " is not NV12";
				return false;
			}
			try
			{
				Marshal.AddRef(frame.Resource);
				using ID3D12Resource cameraResource = new ID3D12Resource(frame.Resource);
				RenderNativeNv12Resource(cameraResource, frame.Width, frame.Height, colorSettings, denoiseEnabled, denoiseStrength, trackingOverlay);
				_nativeTexturePreviewFailureReason = null;
				return true;
			}
			catch (Exception ex)
			{
				_nativeTexturePreviewUnavailable = true;
				_nativeTexturePreviewFailureReason = ex.Message;
				failureReason = ex.Message;
				return false;
			}
		}

		public bool RenderSharedD3D11BridgeFrame(TextureNativeFrameLease frame, VideoFrameColorSettings colorSettings, bool denoiseEnabled, double denoiseStrength, PreviewTrackingOverlay trackingOverlay, out string? failureReason)
		{
			failureReason = null;
			if (_disposed)
			{
				failureReason = "renderer disposed";
				return false;
			}
			if (_sharedD3D11BridgePreviewUnavailable)
			{
				failureReason = _sharedD3D11BridgePreviewFailureReason ?? "D3D11 bridge texture rendering disabled after an earlier failure";
				return false;
			}
			if ((object)_nv12PreviewRootSignature == null || (object)_nv12PreviewPipelineState == null)
			{
				failureReason = "NV12 shader pipeline unavailable";
				return false;
			}
			if (frame.D3D12SharedTextureHandle == IntPtr.Zero)
			{
				failureReason = "D3D11 bridge shared texture handle is missing";
				return false;
			}
			if (!frame.MediaSubtype.Contains("NV12", StringComparison.OrdinalIgnoreCase))
			{
				failureReason = "media subtype " + frame.MediaSubtype + " is not NV12";
				return false;
			}
			try
			{
				using ID3D12Resource cameraResource = _device.OpenSharedHandle<ID3D12Resource>(frame.D3D12SharedTextureHandle);
				WaitForD3D11Producer(frame);
				RenderNativeNv12Resource(cameraResource, frame.Width, frame.Height, colorSettings, denoiseEnabled, denoiseStrength, trackingOverlay);
				WaitForGpu();
				_sharedD3D11BridgePreviewFailureReason = null;
				return true;
			}
			catch (Exception ex)
			{
				_sharedD3D11BridgePreviewUnavailable = true;
				_sharedD3D11BridgePreviewFailureReason = ex.Message;
				failureReason = ex.Message;
				return false;
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
			if (_d3d11ProducerFence == null
				|| _d3d11ProducerFenceHandle != producerFenceHandle)
			{
				_d3d11ProducerFence?.Dispose();
				_d3d11ProducerFence =
					_device.OpenSharedHandle<ID3D12Fence>(producerFenceHandle);
				_d3d11ProducerFenceHandle = producerFenceHandle;
			}
			_commandQueue.Wait(_d3d11ProducerFence, producerFenceValue);
		}

		private void RenderNativeNv12Resource(ID3D12Resource cameraResource, int width, int height, VideoFrameColorSettings colorSettings, bool denoiseEnabled, double denoiseStrength, PreviewTrackingOverlay trackingOverlay)
		{
			ShaderResourceViewDescription value = new ShaderResourceViewDescription
			{
				Format = Format.R8_UNorm,
				ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
				Shader4ComponentMapping = 5768u,
				Texture2D = new Texture2DShaderResourceView
				{
					MipLevels = 1u,
					PlaneSlice = 0u
				}
			};
			ShaderResourceViewDescription value2 = new ShaderResourceViewDescription
			{
				Format = Format.R8G8_UNorm,
				ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
				Shader4ComponentMapping = 5768u,
				Texture2D = new Texture2DShaderResourceView
				{
					MipLevels = 1u,
					PlaneSlice = 1u
				}
			};
			_device.CreateShaderResourceView(cameraResource, value, GetSrvCpuHandle(1));
			_device.CreateShaderResourceView(cameraResource, value2, GetSrvCpuHandle(2));
			int frameIndex;
			FrameResource frameResource = BeginFrame(out frameIndex);
			ID3D12Resource resource = _renderTargets[frameIndex] ?? throw new InvalidOperationException("DX12 render target is not ready.");
			ResourceBarrier resourceBarrier = ResourceBarrier.BarrierTransition(cameraResource, ResourceStates.Common, ResourceStates.PixelShaderResource);
			ResourceBarrier resourceBarrier2 = ResourceBarrier.BarrierTransition(resource, ResourceStates.Common, ResourceStates.RenderTarget);
			ID3D12GraphicsCommandList commandList = _commandList;
			InlineArray2<ResourceBarrier> buffer = default(InlineArray2<ResourceBarrier>);
			buffer[0] = resourceBarrier;
			buffer[1] = resourceBarrier2;
			commandList.ResourceBarrier(buffer);
			CpuDescriptorHandle rtvHandle = GetRtvHandle(frameIndex);
			_commandList.SetGraphicsRootSignature(_nv12PreviewRootSignature);
			_commandList.SetPipelineState(_nv12PreviewPipelineState);
			_commandList.SetDescriptorHeaps(new ReadOnlySpan<ID3D12DescriptorHeap>(_srvHeap));
			_commandList.SetGraphicsRootDescriptorTable(0u, GetSrvGpuHandle(1));
			SetNv12ShaderConstants(width, height, colorSettings, denoiseEnabled, denoiseStrength);
			Viewport viewport = new Viewport(0f, 0f, _viewportWidth, _viewportHeight);
			RawRect rawRect = new RawRect(0, 0, _viewportWidth, _viewportHeight);
			_commandList.RSSetViewports(viewport);
			_commandList.RSSetScissorRects(rawRect);
			_commandList.OMSetRenderTargets(rtvHandle);
			_commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
			_commandList.DrawInstanced(3u, 1u, 0u, 0u);
			bool flag = _trackingOverlayRenderer?.PrepareDraw(frameIndex, trackingOverlay, _viewportWidth, _viewportHeight) ?? false;
			ResourceBarrier resourceBarrier3 = ResourceBarrier.BarrierTransition(resource, ResourceStates.RenderTarget, ResourceStates.Common);
			ResourceBarrier resourceBarrier4 = ResourceBarrier.BarrierTransition(cameraResource, ResourceStates.PixelShaderResource, ResourceStates.Common);
			_commandList.ResourceBarrier((!flag) ? new ResourceBarrier[2] { resourceBarrier3, resourceBarrier4 } : new ResourceBarrier[1] { resourceBarrier4 });
			ExecuteAndPresent(frameResource, frameIndex, trackingOverlay, flag);
		}

		private bool TryRenderNv12FrameWithShader(byte[] nv12Bytes, int width, int height, int stride, VideoFrameColorSettings colorSettings, bool denoiseEnabled, double denoiseStrength, PreviewTrackingOverlay trackingOverlay, bool swapChromaChannels)
		{
			if (_nv12PreviewUnavailable || (object)_nv12PreviewRootSignature == null || (object)_nv12PreviewPipelineState == null)
			{
				_nv12PreviewFailureReason = (_nv12PreviewUnavailable ? (_nv12PreviewFailureReason ?? "NV12 preview disabled after earlier failure") : "NV12 shader pipeline unavailable");
				return false;
			}
			try
			{
				EnsureNv12Textures(width, height);
				ID3D12Resource nv12YTexture = _nv12YTexture;
				ID3D12Resource nv12UvTexture = _nv12UvTexture;
				int frameIndex;
				FrameResource frameResource = BeginFrame(out frameIndex);
				ID3D12Resource nv12YUploadBuffer = frameResource.Nv12YUploadBuffer;
				ID3D12Resource nv12UvUploadBuffer = frameResource.Nv12UvUploadBuffer;
				if ((object)nv12YTexture == null || (object)nv12UvTexture == null || (object)nv12YUploadBuffer == null || (object)nv12UvUploadBuffer == null)
				{
					return false;
				}
				CopyNv12FrameToUploadBuffers(frameResource, nv12Bytes, width, height, stride);
				ID3D12Resource resource = _renderTargets[frameIndex] ?? throw new InvalidOperationException("DX12 render target is not ready.");
				if (_nv12YTextureState != ResourceStates.CopyDest)
				{
					ResourceBarrier resourceBarrier = ResourceBarrier.BarrierTransition(nv12YTexture, _nv12YTextureState, ResourceStates.CopyDest);
					ID3D12GraphicsCommandList commandList = _commandList;
					ResourceBarrier reference = resourceBarrier;
					commandList.ResourceBarrier(new Span<ResourceBarrier>(ref reference));
					_nv12YTextureState = ResourceStates.CopyDest;
				}
				if (_nv12UvTextureState != ResourceStates.CopyDest)
				{
					ResourceBarrier resourceBarrier2 = ResourceBarrier.BarrierTransition(nv12UvTexture, _nv12UvTextureState, ResourceStates.CopyDest);
					ID3D12GraphicsCommandList commandList2 = _commandList;
					ResourceBarrier reference2 = resourceBarrier2;
					commandList2.ResourceBarrier(new Span<ResourceBarrier>(ref reference2));
					_nv12UvTextureState = ResourceStates.CopyDest;
				}
				_commandList.CopyTextureRegion(new TextureCopyLocation(nv12YTexture), 0u, 0u, 0u, new TextureCopyLocation(nv12YUploadBuffer, _nv12YFootprint));
				_commandList.CopyTextureRegion(new TextureCopyLocation(nv12UvTexture), 0u, 0u, 0u, new TextureCopyLocation(nv12UvUploadBuffer, _nv12UvFootprint));
				ResourceBarrier resourceBarrier3 = ResourceBarrier.BarrierTransition(nv12YTexture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
				ResourceBarrier resourceBarrier4 = ResourceBarrier.BarrierTransition(nv12UvTexture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
				ID3D12GraphicsCommandList commandList3 = _commandList;
				InlineArray2<ResourceBarrier> buffer = default(InlineArray2<ResourceBarrier>);
				buffer[0] = resourceBarrier3;
				buffer[1] = resourceBarrier4;
				commandList3.ResourceBarrier(buffer);
				_nv12YTextureState = ResourceStates.PixelShaderResource;
				_nv12UvTextureState = ResourceStates.PixelShaderResource;
				ResourceBarrier resourceBarrier5 = ResourceBarrier.BarrierTransition(resource, ResourceStates.Common, ResourceStates.RenderTarget);
				ID3D12GraphicsCommandList commandList4 = _commandList;
				ResourceBarrier reference3 = resourceBarrier5;
				commandList4.ResourceBarrier(new Span<ResourceBarrier>(ref reference3));
				CpuDescriptorHandle rtvHandle = GetRtvHandle(frameIndex);
				_commandList.SetGraphicsRootSignature(_nv12PreviewRootSignature);
				_commandList.SetPipelineState(_nv12PreviewPipelineState);
				_commandList.SetDescriptorHeaps(new ReadOnlySpan<ID3D12DescriptorHeap>(_srvHeap));
				_commandList.SetGraphicsRootDescriptorTable(0u, GetSrvGpuHandle(1));
				SetNv12ShaderConstants(width, height, colorSettings, denoiseEnabled, denoiseStrength, swapChromaChannels);
				Viewport viewport = new Viewport(0f, 0f, _viewportWidth, _viewportHeight);
				RawRect rawRect = new RawRect(0, 0, _viewportWidth, _viewportHeight);
				_commandList.RSSetViewports(viewport);
				_commandList.RSSetScissorRects(rawRect);
				_commandList.OMSetRenderTargets(rtvHandle);
				_commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
				_commandList.DrawInstanced(3u, 1u, 0u, 0u);
				bool flag = _trackingOverlayRenderer?.PrepareDraw(frameIndex, trackingOverlay, _viewportWidth, _viewportHeight) ?? false;
				ResourceBarrier resourceBarrier6 = ResourceBarrier.BarrierTransition(resource, ResourceStates.RenderTarget, ResourceStates.Common);
				if (!flag)
				{
					ID3D12GraphicsCommandList commandList5 = _commandList;
					ResourceBarrier reference4 = resourceBarrier6;
					commandList5.ResourceBarrier(new Span<ResourceBarrier>(ref reference4));
				}
				ExecuteAndPresent(frameResource, frameIndex, trackingOverlay, flag);
				return true;
			}
			catch (Exception ex)
			{
				_nv12PreviewUnavailable = true;
				_nv12PreviewFailureReason = ex.Message;
				return false;
			}
		}

		private bool TryRenderBgraFrameWithShader(byte[] bgraBytes, int width, int height, int stride, VideoFrameColorSettings colorSettings, bool denoiseEnabled, double denoiseStrength, PreviewTrackingOverlay trackingOverlay)
		{
			if (_shaderPreviewUnavailable || (object)_previewRootSignature == null || (object)_previewPipelineState == null)
			{
				return false;
			}
			try
			{
				EnsureCameraTexture(width, height);
				ID3D12Resource cameraTexture = _cameraTexture;
				int frameIndex;
				FrameResource frameResource = BeginFrame(out frameIndex);
				ID3D12Resource cameraUploadBuffer = frameResource.CameraUploadBuffer;
				if ((object)cameraTexture == null || (object)cameraUploadBuffer == null)
				{
					return false;
				}
				CopyBgraFrameToUploadBuffer(frameResource, bgraBytes, width, height, stride);
				WriteBgraColorSettings(frameResource, colorSettings, denoiseEnabled, denoiseStrength, width, height);
				ID3D12Resource resource = _renderTargets[frameIndex] ?? throw new InvalidOperationException("DX12 render target is not ready.");
				if (_cameraTextureState != ResourceStates.CopyDest)
				{
					ResourceBarrier resourceBarrier = ResourceBarrier.BarrierTransition(cameraTexture, _cameraTextureState, ResourceStates.CopyDest);
					ID3D12GraphicsCommandList commandList = _commandList;
					ResourceBarrier reference = resourceBarrier;
					commandList.ResourceBarrier(new Span<ResourceBarrier>(ref reference));
					_cameraTextureState = ResourceStates.CopyDest;
				}
				_commandList.CopyTextureRegion(new TextureCopyLocation(cameraTexture), 0u, 0u, 0u, new TextureCopyLocation(cameraUploadBuffer, _cameraTextureFootprint));
				ResourceBarrier resourceBarrier2 = ResourceBarrier.BarrierTransition(cameraTexture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
				ID3D12GraphicsCommandList commandList2 = _commandList;
				ResourceBarrier reference2 = resourceBarrier2;
				commandList2.ResourceBarrier(new Span<ResourceBarrier>(ref reference2));
				_cameraTextureState = ResourceStates.PixelShaderResource;
				ResourceBarrier resourceBarrier3 = ResourceBarrier.BarrierTransition(resource, ResourceStates.Common, ResourceStates.RenderTarget);
				ID3D12GraphicsCommandList commandList3 = _commandList;
				ResourceBarrier reference3 = resourceBarrier3;
				commandList3.ResourceBarrier(new Span<ResourceBarrier>(ref reference3));
				CpuDescriptorHandle rtvHandle = GetRtvHandle(frameIndex);
				_commandList.SetGraphicsRootSignature(_previewRootSignature);
				_commandList.SetPipelineState(_previewPipelineState);
				_commandList.SetDescriptorHeaps(new ReadOnlySpan<ID3D12DescriptorHeap>(_srvHeap));
				_commandList.SetGraphicsRootDescriptorTable(0u, _srvHeap.GetGPUDescriptorHandleForHeapStart());
				_commandList.SetGraphicsRootDescriptorTable(1u, GetSrvGpuHandle(3 + frameIndex));
				Viewport viewport = new Viewport(0f, 0f, _viewportWidth, _viewportHeight);
				RawRect rawRect = new RawRect(0, 0, _viewportWidth, _viewportHeight);
				_commandList.RSSetViewports(viewport);
				_commandList.RSSetScissorRects(rawRect);
				_commandList.OMSetRenderTargets(rtvHandle);
				_commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
				_commandList.DrawInstanced(3u, 1u, 0u, 0u);
				bool flag = _trackingOverlayRenderer?.PrepareDraw(frameIndex, trackingOverlay, _viewportWidth, _viewportHeight) ?? false;
				ResourceBarrier resourceBarrier4 = ResourceBarrier.BarrierTransition(resource, ResourceStates.RenderTarget, ResourceStates.Common);
				if (!flag)
				{
					ID3D12GraphicsCommandList commandList4 = _commandList;
					ResourceBarrier reference4 = resourceBarrier4;
					commandList4.ResourceBarrier(new Span<ResourceBarrier>(ref reference4));
				}
				ExecuteAndPresent(frameResource, frameIndex, trackingOverlay, flag);
				return true;
			}
			catch
			{
				_shaderPreviewUnavailable = true;
				return false;
			}
		}

		private unsafe void RenderBgraFrameToBackBuffer(byte[] bgraBytes, int width, int height, int stride, PreviewTrackingOverlay trackingOverlay)
		{
			if (width == _viewportWidth && height == _viewportHeight)
			{
				int frameIndex;
				FrameResource frameResource = BeginFrame(out frameIndex);
				ID3D12Resource iD3D12Resource = _renderTargets[frameIndex] ?? throw new InvalidOperationException("DX12 render target is not ready.");
				ResourceBarrier resourceBarrier = ResourceBarrier.BarrierTransition(iD3D12Resource, ResourceStates.Common, ResourceStates.Common);
				ID3D12GraphicsCommandList commandList = _commandList;
				ResourceBarrier reference = resourceBarrier;
				commandList.ResourceBarrier(new Span<ResourceBarrier>(ref reference));
				_commandList.Close();
				_commandQueue.ExecuteCommandList(_commandList);
				WaitForGpu();
				fixed (byte* srcData = bgraBytes)
				{
					iD3D12Resource.WriteToSubresource(0u, null, (nint)srcData, (uint)stride, (uint)(stride * height));
				}
				frameResource.CommandAllocator.Reset();
				_commandList.Reset(frameResource.CommandAllocator);
				ResourceBarrier resourceBarrier2 = ResourceBarrier.BarrierTransition(iD3D12Resource, ResourceStates.Common, ResourceStates.RenderTarget);
				ID3D12GraphicsCommandList commandList2 = _commandList;
				ResourceBarrier reference2 = resourceBarrier2;
				commandList2.ResourceBarrier(new Span<ResourceBarrier>(ref reference2));
				CpuDescriptorHandle rtvHandle = GetRtvHandle(frameIndex);
				Viewport viewport = new Viewport(0f, 0f, _viewportWidth, _viewportHeight);
				RawRect rawRect = new RawRect(0, 0, _viewportWidth, _viewportHeight);
				_commandList.RSSetViewports(viewport);
				_commandList.RSSetScissorRects(rawRect);
				_commandList.OMSetRenderTargets(rtvHandle);
				bool flag = _trackingOverlayRenderer?.PrepareDraw(frameIndex, trackingOverlay, _viewportWidth, _viewportHeight) ?? false;
				ResourceBarrier resourceBarrier3 = ResourceBarrier.BarrierTransition(iD3D12Resource, ResourceStates.RenderTarget, ResourceStates.Common);
				if (!flag)
				{
					ID3D12GraphicsCommandList commandList3 = _commandList;
					ResourceBarrier reference3 = resourceBarrier3;
					commandList3.ResourceBarrier(new Span<ResourceBarrier>(ref reference3));
				}
				ExecuteAndPresent(frameResource, frameIndex, trackingOverlay, flag);
			}
		}

		private void EnsureCameraTexture(int width, int height)
		{
			if ((object)_cameraTexture == null || !CameraUploadBuffersReady() || _cameraTextureWidth != width || _cameraTextureHeight != height)
			{
				WaitForGpu();
				_cameraTexture?.Dispose();
				ReleaseCameraUploadBuffers();
				_cameraTexture = null;
				_cameraTextureWidth = width;
				_cameraTextureHeight = height;
				ResourceDescription resourceDescription = new ResourceDescription(ResourceDimension.Texture2D, 0uL, (ulong)width, (uint)height, 1, 1, Format.B8G8R8A8_UNorm, 1u, 0u, TextureLayout.Unknown, ResourceFlags.None);
				_cameraTexture = _device.CreateCommittedResource<ID3D12Resource>(new HeapProperties(HeapType.Default), HeapFlags.None, resourceDescription, ResourceStates.CopyDest);
				_cameraTextureState = ResourceStates.CopyDest;
				PlacedSubresourceFootPrint[] array = new PlacedSubresourceFootPrint[1];
				uint[] numRows = new uint[1];
				ulong[] rowSizeInBytes = new ulong[1];
				_device.GetCopyableFootprints(resourceDescription, 0u, 1u, 0uL, array, numRows, rowSizeInBytes, out var totalBytes);
				_cameraTextureFootprint = array[0];
				FrameResource[] frameResources = _frameResources;
				for (int i = 0; i < frameResources.Length; i++)
				{
					frameResources[i].CreateCameraUploadBuffer(_device, totalBytes);
				}
				ShaderResourceViewDescription value = new ShaderResourceViewDescription
				{
					Format = Format.B8G8R8A8_UNorm,
					ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
					Shader4ComponentMapping = 5768u,
					Texture2D = new Texture2DShaderResourceView
					{
						MipLevels = 1u
					}
				};
				_device.CreateShaderResourceView(_cameraTexture, value, _srvHeap.GetCPUDescriptorHandleForHeapStart());
			}
		}

		private void EnsureNv12Textures(int width, int height)
		{
			if ((object)_nv12YTexture == null || (object)_nv12UvTexture == null || !Nv12UploadBuffersReady() || _nv12TextureWidth != width || _nv12TextureHeight != height)
			{
				WaitForGpu();
				_nv12YTexture?.Dispose();
				_nv12UvTexture?.Dispose();
				ReleaseNv12UploadBuffers();
				_nv12YTexture = null;
				_nv12UvTexture = null;
				_nv12TextureWidth = width;
				_nv12TextureHeight = height;
				ResourceDescription description = new ResourceDescription(ResourceDimension.Texture2D, 0uL, (ulong)width, (uint)height, 1, 1, Format.R8_UNorm, 1u, 0u, TextureLayout.Unknown, ResourceFlags.None);
				ResourceDescription description2 = new ResourceDescription(ResourceDimension.Texture2D, 0uL, (ulong)Math.Max(1, width / 2), (uint)Math.Max(1, height / 2), 1, 1, Format.R8G8_UNorm, 1u, 0u, TextureLayout.Unknown, ResourceFlags.None);
				_nv12YTexture = _device.CreateCommittedResource<ID3D12Resource>(new HeapProperties(HeapType.Default), HeapFlags.None, description, ResourceStates.CopyDest);
				_nv12UvTexture = _device.CreateCommittedResource<ID3D12Resource>(new HeapProperties(HeapType.Default), HeapFlags.None, description2, ResourceStates.CopyDest);
				_nv12YTextureState = ResourceStates.CopyDest;
				_nv12UvTextureState = ResourceStates.CopyDest;
				_nv12YFootprint = GetTextureFootprint(description, out var uploadBytes);
				_nv12UvFootprint = GetTextureFootprint(description2, out var uploadBytes2);
				FrameResource[] frameResources = _frameResources;
				for (int i = 0; i < frameResources.Length; i++)
				{
					frameResources[i].CreateNv12UploadBuffers(_device, uploadBytes, uploadBytes2);
				}
				ShaderResourceViewDescription value = new ShaderResourceViewDescription
				{
					Format = Format.R8_UNorm,
					ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
					Shader4ComponentMapping = 5768u,
					Texture2D = new Texture2DShaderResourceView
					{
						MipLevels = 1u
					}
				};
				ShaderResourceViewDescription value2 = new ShaderResourceViewDescription
				{
					Format = Format.R8G8_UNorm,
					ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
					Shader4ComponentMapping = 5768u,
					Texture2D = new Texture2DShaderResourceView
					{
						MipLevels = 1u
					}
				};
				_device.CreateShaderResourceView(_nv12YTexture, value, GetSrvCpuHandle(1));
				_device.CreateShaderResourceView(_nv12UvTexture, value2, GetSrvCpuHandle(2));
			}
		}

		private PlacedSubresourceFootPrint GetTextureFootprint(ResourceDescription description, out ulong uploadBytes)
		{
			PlacedSubresourceFootPrint[] array = new PlacedSubresourceFootPrint[1];
			uint[] numRows = new uint[1];
			ulong[] rowSizeInBytes = new ulong[1];
			_device.GetCopyableFootprints(description, 0u, 1u, 0uL, array, numRows, rowSizeInBytes, out uploadBytes);
			return array[0];
		}

		private bool CameraUploadBuffersReady()
		{
			FrameResource[] frameResources = _frameResources;
			foreach (FrameResource frameResource in frameResources)
			{
				if ((object)frameResource.CameraUploadBuffer == null || frameResource.CameraUploadPointer == IntPtr.Zero)
				{
					return false;
				}
			}
			return true;
		}

		private bool Nv12UploadBuffersReady()
		{
			FrameResource[] frameResources = _frameResources;
			foreach (FrameResource frameResource in frameResources)
			{
				if ((object)frameResource.Nv12YUploadBuffer == null || (object)frameResource.Nv12UvUploadBuffer == null || frameResource.Nv12YUploadPointer == IntPtr.Zero || frameResource.Nv12UvUploadPointer == IntPtr.Zero)
				{
					return false;
				}
			}
			return true;
		}

		private void ReleaseCameraUploadBuffers()
		{
			FrameResource[] frameResources = _frameResources;
			for (int i = 0; i < frameResources.Length; i++)
			{
				frameResources[i].ReleaseCameraUploadBuffer();
			}
		}

		private void ReleaseNv12UploadBuffers()
		{
			FrameResource[] frameResources = _frameResources;
			for (int i = 0; i < frameResources.Length; i++)
			{
				frameResources[i].ReleaseNv12UploadBuffers();
			}
		}

		private unsafe void CopyBgraFrameToUploadBuffer(FrameResource frameResource, byte[] bgraBytes, int width, int height, int stride)
		{
			byte* cameraUploadPointer = (byte*)frameResource.CameraUploadPointer;
			if (cameraUploadPointer == null)
			{
				throw new InvalidOperationException("DX12 BGRA upload buffer is not mapped.");
			}
			fixed (byte* ptr = bgraBytes)
			{
				int num = width * 4;
				byte* ptr2 = cameraUploadPointer + (nint)_cameraTextureFootprint.Offset;
				nint num2 = (nint)_cameraTextureFootprint.Footprint.RowPitch;
				for (int i = 0; i < height; i++)
				{
					Buffer.MemoryCopy(ptr + i * stride, ptr2 + i * num2, num2, num);
				}
			}
		}

		private unsafe void WriteBgraColorSettings(FrameResource frameResource, VideoFrameColorSettings settings, bool denoiseEnabled, double denoiseStrength, int width, int height)
		{
			float* bgraColorSettingsPointer = (float*)frameResource.BgraColorSettingsPointer;
			if (bgraColorSettingsPointer == null)
			{
				throw new InvalidOperationException("DX12 BGRA color settings buffer is not mapped.");
			}
			bool hasVisibleAdjustments = settings.HasVisibleAdjustments;
			*bgraColorSettingsPointer = (hasVisibleAdjustments ? ((float)(Math.Clamp(settings.Exposure, -30.0, 30.0) * 2.2)) : 0f);
			bgraColorSettingsPointer[1] = (hasVisibleAdjustments ? ((float)(1.0 + Math.Clamp(settings.Contrast, -40.0, 40.0) / 100.0)) : 1f);
			bgraColorSettingsPointer[2] = (hasVisibleAdjustments ? ((float)(1.0 + Math.Clamp(settings.Saturation, -40.0, 40.0) / 100.0)) : 1f);
			bgraColorSettingsPointer[3] = (hasVisibleAdjustments ? ((float)(Math.Clamp(settings.Warmth, -40.0, 40.0) * 0.9)) : 0f);
			float num = (float)Math.Clamp(denoiseStrength, 0.5, 5.0);
			bgraColorSettingsPointer[4] = (denoiseEnabled ? Math.Clamp(0.06f + num * 0.08f, 0.1f, 0.42f) : 0f);
			bgraColorSettingsPointer[5] = (denoiseEnabled ? Math.Clamp(0.018f + num * 0.006f, 0.024f, 0.052f) : 0f);
			bgraColorSettingsPointer[6] = 1f / (float)Math.Max(1, width);
			bgraColorSettingsPointer[7] = 1f / (float)Math.Max(1, height);
		}

		private unsafe void CopyNv12FrameToUploadBuffers(FrameResource frameResource, byte[] nv12Bytes, int width, int height, int stride)
		{
			int num = Math.Max(1, height / 2);
			int num2 = stride * height;
			byte* nv12YUploadPointer = (byte*)frameResource.Nv12YUploadPointer;
			byte* nv12UvUploadPointer = (byte*)frameResource.Nv12UvUploadPointer;
			if (nv12YUploadPointer == null || nv12UvUploadPointer == null)
			{
				throw new InvalidOperationException("DX12 NV12 upload buffers are not mapped.");
			}
			fixed (byte* ptr = nv12Bytes)
			{
				byte* ptr2 = nv12YUploadPointer + (nint)_nv12YFootprint.Offset;
				nint num3 = (nint)_nv12YFootprint.Footprint.RowPitch;
				for (int i = 0; i < height; i++)
				{
					Buffer.MemoryCopy(ptr + i * stride, ptr2 + i * num3, num3, width);
				}
				byte* ptr3 = nv12UvUploadPointer + (nint)_nv12UvFootprint.Offset;
				nint num4 = (nint)_nv12UvFootprint.Footprint.RowPitch;
				for (int j = 0; j < num; j++)
				{
					Buffer.MemoryCopy(ptr + num2 + j * stride, ptr3 + j * num4, num4, width);
				}
			}
		}

		private void SetNv12ShaderConstants(int width, int height, VideoFrameColorSettings colorSettings, bool denoiseEnabled, double denoiseStrength, bool swapChromaChannels = false)
		{
			bool hasVisibleAdjustments = colorSettings.HasVisibleAdjustments;
			_commandList.SetGraphicsRoot32BitConstant(1u, BitConverter.SingleToUInt32Bits(hasVisibleAdjustments ? ((float)(Math.Clamp(colorSettings.Exposure, -30.0, 30.0) * 2.2)) : 0f), 0u);
			_commandList.SetGraphicsRoot32BitConstant(1u, BitConverter.SingleToUInt32Bits(hasVisibleAdjustments ? ((float)(1.0 + Math.Clamp(colorSettings.Contrast, -40.0, 40.0) / 100.0)) : 1f), 1u);
			_commandList.SetGraphicsRoot32BitConstant(1u, BitConverter.SingleToUInt32Bits(hasVisibleAdjustments ? ((float)(1.0 + Math.Clamp(colorSettings.Saturation, -40.0, 40.0) / 100.0)) : 1f), 2u);
			_commandList.SetGraphicsRoot32BitConstant(1u, BitConverter.SingleToUInt32Bits(hasVisibleAdjustments ? ((float)(Math.Clamp(colorSettings.Warmth, -40.0, 40.0) * 0.9)) : 0f), 3u);
			float num = (float)Math.Clamp(denoiseStrength, 0.5, 5.0);
			float value = (denoiseEnabled ? Math.Clamp(0.08f + num * 0.11f, 0.14f, 0.58f) : 0f);
			float value2 = (denoiseEnabled ? Math.Clamp(0.018f + num * 0.006f, 0.024f, 0.052f) : 0f);
			_commandList.SetGraphicsRoot32BitConstant(1u, BitConverter.SingleToUInt32Bits(value), 4u);
			_commandList.SetGraphicsRoot32BitConstant(1u, BitConverter.SingleToUInt32Bits(value2), 5u);
			_commandList.SetGraphicsRoot32BitConstant(1u, BitConverter.SingleToUInt32Bits(1f / (float)Math.Max(1, width)), 6u);
			_commandList.SetGraphicsRoot32BitConstant(1u, BitConverter.SingleToUInt32Bits(1f / (float)Math.Max(1, height)), 7u);
			_commandList.SetGraphicsRoot32BitConstant(1u, BitConverter.SingleToUInt32Bits(swapChromaChannels ? 1f : 0f), 8u);
		}

		private void TryCreatePreviewShaderPipeline()
		{
			try
			{
				byte[] array = CompileShader("VSMain", "vs_5_0");
				byte[] array2 = CompileShader("PSMain", "ps_5_0");
				DescriptorRange[] ranges = new DescriptorRange[1]
				{
					new DescriptorRange(DescriptorRangeType.ShaderResourceView, 1u, 0u)
				};
				DescriptorRange[] ranges2 = new DescriptorRange[1]
				{
					new DescriptorRange(DescriptorRangeType.ConstantBufferView, 1u, 0u)
				};
				RootParameter[] parameters = new RootParameter[2]
				{
					new RootParameter(new RootDescriptorTable(ranges), ShaderVisibility.Pixel),
					new RootParameter(new RootDescriptorTable(ranges2), ShaderVisibility.Pixel)
				};
				StaticSamplerDescription[] samplers = new StaticSamplerDescription[1]
				{
					new StaticSamplerDescription(0u, Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp, 0f, 0u, ComparisonFunction.Never, StaticBorderColor.TransparentBlack, 0f, float.MaxValue, ShaderVisibility.Pixel)
				};
				RootSignatureDescription description = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout, parameters, samplers);
				_previewRootSignature = _device.CreateRootSignature(in description, RootSignatureVersion.Version1);
				GraphicsPipelineStateDescription graphicsPipelineStateDescription = new GraphicsPipelineStateDescription();
				graphicsPipelineStateDescription.RootSignature = _previewRootSignature;
				graphicsPipelineStateDescription.VertexShader = array;
				graphicsPipelineStateDescription.PixelShader = array2;
				graphicsPipelineStateDescription.BlendState = BlendDescription.Opaque;
				graphicsPipelineStateDescription.RasterizerState = RasterizerDescription.CullNone;
				graphicsPipelineStateDescription.DepthStencilState = DepthStencilDescription.None;
				graphicsPipelineStateDescription.SampleMask = uint.MaxValue;
				graphicsPipelineStateDescription.PrimitiveTopologyType = PrimitiveTopologyType.Triangle;
				graphicsPipelineStateDescription.RenderTargetFormats = new Format[1] { Format.B8G8R8A8_UNorm };
				graphicsPipelineStateDescription.SampleDescription = new SampleDescription(1u, 0u);
				GraphicsPipelineStateDescription description2 = graphicsPipelineStateDescription;
				_previewPipelineState = _device.CreateGraphicsPipelineState<ID3D12PipelineState>(description2);
			}
			catch
			{
				_shaderPreviewUnavailable = true;
			}
		}

		private void TryCreateNv12PreviewShaderPipeline()
		{
			try
			{
				byte[] array = CompileShader("Texture2D<float> CameraLuma : register(t0);\r\nTexture2D<float2> CameraChroma : register(t1);\r\nSamplerState CameraSampler : register(s0);\r\n\r\ncbuffer Nv12PreviewSettings : register(b0)\r\n{\r\n    float ExposureOffset;\r\n    float Contrast;\r\n    float Saturation;\r\n    float Warmth;\r\n    float DenoiseAmount;\r\n    float DenoiseEdgeThreshold;\n    float TexelWidth;\n    float TexelHeight;\n    float SwapChromaChannels;\n};\n\r\nstruct VertexOutput\r\n{\r\n    float4 Position : SV_POSITION;\r\n    float2 TexCoord : TEXCOORD0;\r\n};\r\n\r\nVertexOutput VSMain(uint vertexId : SV_VertexID)\r\n{\r\n    float2 positions[3] =\r\n    {\r\n        float2(-1.0, -1.0),\r\n        float2(-1.0, 3.0),\r\n        float2(3.0, -1.0)\r\n    };\r\n    float2 texCoords[3] =\r\n    {\r\n        float2(0.0, 1.0),\r\n        float2(0.0, -1.0),\r\n        float2(2.0, 1.0)\r\n    };\r\n\r\n    VertexOutput output;\r\n    output.Position = float4(positions[vertexId], 0.0, 1.0);\r\n    output.TexCoord = texCoords[vertexId];\r\n    return output;\r\n}\r\n\r\nfloat NormalizeLuma(float rawY)\r\n{\r\n    return saturate((rawY - (16.0 / 255.0)) * (255.0 / 219.0));\r\n}\r\n\r\nfloat EdgeAwareWeight(float sampleY, float centerY)\r\n{\r\n    return saturate(1.0 - abs(sampleY - centerY) / max(DenoiseEdgeThreshold, 0.0001));\r\n}\r\n\r\nfloat SampleNormalizedLuma(float2 texCoord)\r\n{\r\n    return NormalizeLuma(CameraLuma.Sample(CameraSampler, texCoord));\r\n}\r\n\r\nfloat ApplyLumaDenoise(float centerY, float2 texCoord)\r\n{\r\n    if (DenoiseAmount <= 0.001)\r\n    {\r\n        return centerY;\r\n    }\r\n\r\n    float2 xOffset = float2(TexelWidth, 0.0);\r\n    float2 yOffset = float2(0.0, TexelHeight);\r\n    float leftY = SampleNormalizedLuma(texCoord - xOffset);\r\n    float rightY = SampleNormalizedLuma(texCoord + xOffset);\r\n    float upY = SampleNormalizedLuma(texCoord - yOffset);\r\n    float downY = SampleNormalizedLuma(texCoord + yOffset);\r\n\r\n    float centerWeight = 2.0;\r\n    float leftWeight = EdgeAwareWeight(leftY, centerY);\r\n    float rightWeight = EdgeAwareWeight(rightY, centerY);\r\n    float upWeight = EdgeAwareWeight(upY, centerY);\r\n    float downWeight = EdgeAwareWeight(downY, centerY);\r\n    float totalWeight = centerWeight + leftWeight + rightWeight + upWeight + downWeight;\r\n    float smoothedY = (\r\n        centerY * centerWeight\r\n        + leftY * leftWeight\r\n        + rightY * rightWeight\r\n        + upY * upWeight\r\n        + downY * downWeight) / max(totalWeight, 0.0001);\r\n    return lerp(centerY, smoothedY, DenoiseAmount);\r\n}\r\n\r\nfloat3 ApplyColorPolish(float3 rgb)\r\n{\r\n    float3 rgb255 = rgb * 255.0;\r\n    rgb255.r = ((rgb255.r + ExposureOffset + Warmth - 128.0) * Contrast) + 128.0;\r\n    rgb255.g = ((rgb255.g + ExposureOffset - 128.0) * Contrast) + 128.0;\r\n    rgb255.b = ((rgb255.b + ExposureOffset - Warmth - 128.0) * Contrast) + 128.0;\r\n\r\n    float luma = dot(rgb255, float3(0.2126, 0.7152, 0.0722));\r\n    rgb255 = luma + (rgb255 - luma) * Saturation;\r\n    return saturate(rgb255 / 255.0);\r\n}\r\n\r\nfloat4 PSMain(VertexOutput input) : SV_TARGET\r\n{\r\n    float y = NormalizeLuma(CameraLuma.Sample(CameraSampler, input.TexCoord));\n    y = ApplyLumaDenoise(y, input.TexCoord);\n    float2 uv = CameraChroma.Sample(CameraSampler, input.TexCoord) - float2(0.5, 0.5);\n    uv = SwapChromaChannels > 0.5 ? uv.yx : uv;\n    float3 rgb = float3(\n        y + 1.5748 * uv.y,\r\n        y - 0.1873 * uv.x - 0.4681 * uv.y,\r\n        y + 1.8556 * uv.x);\r\n    return float4(ApplyColorPolish(saturate(rgb)), 1.0);\r\n}", "VSMain", "vs_5_0");
				byte[] array2 = CompileShader("Texture2D<float> CameraLuma : register(t0);\r\nTexture2D<float2> CameraChroma : register(t1);\r\nSamplerState CameraSampler : register(s0);\r\n\r\ncbuffer Nv12PreviewSettings : register(b0)\r\n{\r\n    float ExposureOffset;\r\n    float Contrast;\r\n    float Saturation;\r\n    float Warmth;\r\n    float DenoiseAmount;\r\n    float DenoiseEdgeThreshold;\n    float TexelWidth;\n    float TexelHeight;\n    float SwapChromaChannels;\n};\n\r\nstruct VertexOutput\r\n{\r\n    float4 Position : SV_POSITION;\r\n    float2 TexCoord : TEXCOORD0;\r\n};\r\n\r\nVertexOutput VSMain(uint vertexId : SV_VertexID)\r\n{\r\n    float2 positions[3] =\r\n    {\r\n        float2(-1.0, -1.0),\r\n        float2(-1.0, 3.0),\r\n        float2(3.0, -1.0)\r\n    };\r\n    float2 texCoords[3] =\r\n    {\r\n        float2(0.0, 1.0),\r\n        float2(0.0, -1.0),\r\n        float2(2.0, 1.0)\r\n    };\r\n\r\n    VertexOutput output;\r\n    output.Position = float4(positions[vertexId], 0.0, 1.0);\r\n    output.TexCoord = texCoords[vertexId];\r\n    return output;\r\n}\r\n\r\nfloat NormalizeLuma(float rawY)\r\n{\r\n    return saturate((rawY - (16.0 / 255.0)) * (255.0 / 219.0));\r\n}\r\n\r\nfloat EdgeAwareWeight(float sampleY, float centerY)\r\n{\r\n    return saturate(1.0 - abs(sampleY - centerY) / max(DenoiseEdgeThreshold, 0.0001));\r\n}\r\n\r\nfloat SampleNormalizedLuma(float2 texCoord)\r\n{\r\n    return NormalizeLuma(CameraLuma.Sample(CameraSampler, texCoord));\r\n}\r\n\r\nfloat ApplyLumaDenoise(float centerY, float2 texCoord)\r\n{\r\n    if (DenoiseAmount <= 0.001)\r\n    {\r\n        return centerY;\r\n    }\r\n\r\n    float2 xOffset = float2(TexelWidth, 0.0);\r\n    float2 yOffset = float2(0.0, TexelHeight);\r\n    float leftY = SampleNormalizedLuma(texCoord - xOffset);\r\n    float rightY = SampleNormalizedLuma(texCoord + xOffset);\r\n    float upY = SampleNormalizedLuma(texCoord - yOffset);\r\n    float downY = SampleNormalizedLuma(texCoord + yOffset);\r\n\r\n    float centerWeight = 2.0;\r\n    float leftWeight = EdgeAwareWeight(leftY, centerY);\r\n    float rightWeight = EdgeAwareWeight(rightY, centerY);\r\n    float upWeight = EdgeAwareWeight(upY, centerY);\r\n    float downWeight = EdgeAwareWeight(downY, centerY);\r\n    float totalWeight = centerWeight + leftWeight + rightWeight + upWeight + downWeight;\r\n    float smoothedY = (\r\n        centerY * centerWeight\r\n        + leftY * leftWeight\r\n        + rightY * rightWeight\r\n        + upY * upWeight\r\n        + downY * downWeight) / max(totalWeight, 0.0001);\r\n    return lerp(centerY, smoothedY, DenoiseAmount);\r\n}\r\n\r\nfloat3 ApplyColorPolish(float3 rgb)\r\n{\r\n    float3 rgb255 = rgb * 255.0;\r\n    rgb255.r = ((rgb255.r + ExposureOffset + Warmth - 128.0) * Contrast) + 128.0;\r\n    rgb255.g = ((rgb255.g + ExposureOffset - 128.0) * Contrast) + 128.0;\r\n    rgb255.b = ((rgb255.b + ExposureOffset - Warmth - 128.0) * Contrast) + 128.0;\r\n\r\n    float luma = dot(rgb255, float3(0.2126, 0.7152, 0.0722));\r\n    rgb255 = luma + (rgb255 - luma) * Saturation;\r\n    return saturate(rgb255 / 255.0);\r\n}\r\n\r\nfloat4 PSMain(VertexOutput input) : SV_TARGET\r\n{\r\n    float y = NormalizeLuma(CameraLuma.Sample(CameraSampler, input.TexCoord));\n    y = ApplyLumaDenoise(y, input.TexCoord);\n    float2 uv = CameraChroma.Sample(CameraSampler, input.TexCoord) - float2(0.5, 0.5);\n    uv = SwapChromaChannels > 0.5 ? uv.yx : uv;\n    float3 rgb = float3(\n        y + 1.5748 * uv.y,\r\n        y - 0.1873 * uv.x - 0.4681 * uv.y,\r\n        y + 1.8556 * uv.x);\r\n    return float4(ApplyColorPolish(saturate(rgb)), 1.0);\r\n}", "PSMain", "ps_5_0");
				DescriptorRange[] ranges = new DescriptorRange[1]
				{
					new DescriptorRange(DescriptorRangeType.ShaderResourceView, 2u, 0u)
				};
				RootParameter[] parameters = new RootParameter[2]
				{
					new RootParameter(new RootDescriptorTable(ranges), ShaderVisibility.Pixel),
					new RootParameter(new RootConstants(0u, 0u, 9u), ShaderVisibility.Pixel)
				};
				StaticSamplerDescription[] samplers = new StaticSamplerDescription[1]
				{
					new StaticSamplerDescription(0u, Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp, 0f, 0u, ComparisonFunction.Never, StaticBorderColor.TransparentBlack, 0f, float.MaxValue, ShaderVisibility.Pixel)
				};
				RootSignatureDescription description = new RootSignatureDescription(RootSignatureFlags.AllowInputAssemblerInputLayout, parameters, samplers);
				_nv12PreviewRootSignature = _device.CreateRootSignature(in description, RootSignatureVersion.Version1);
				GraphicsPipelineStateDescription graphicsPipelineStateDescription = new GraphicsPipelineStateDescription();
				graphicsPipelineStateDescription.RootSignature = _nv12PreviewRootSignature;
				graphicsPipelineStateDescription.VertexShader = array;
				graphicsPipelineStateDescription.PixelShader = array2;
				graphicsPipelineStateDescription.BlendState = BlendDescription.Opaque;
				graphicsPipelineStateDescription.RasterizerState = RasterizerDescription.CullNone;
				graphicsPipelineStateDescription.DepthStencilState = DepthStencilDescription.None;
				graphicsPipelineStateDescription.SampleMask = uint.MaxValue;
				graphicsPipelineStateDescription.PrimitiveTopologyType = PrimitiveTopologyType.Triangle;
				graphicsPipelineStateDescription.RenderTargetFormats = new Format[1] { Format.B8G8R8A8_UNorm };
				graphicsPipelineStateDescription.SampleDescription = new SampleDescription(1u, 0u);
				GraphicsPipelineStateDescription description2 = graphicsPipelineStateDescription;
				_nv12PreviewPipelineState = _device.CreateGraphicsPipelineState<ID3D12PipelineState>(description2);
			}
			catch (Exception ex)
			{
				_nv12PreviewUnavailable = true;
				_nv12PreviewFailureReason = "NV12 shader pipeline creation failed: " + ex.Message;
			}
		}

		private static byte[] CompileShader(string entryPoint, string profile)
		{
			return CompileShader("Texture2D<float4> CameraFrame : register(t0);\r\nSamplerState CameraSampler : register(s0);\r\n\r\ncbuffer ColorSettings : register(b0)\r\n{\r\n    float ExposureOffset;\r\n    float Contrast;\r\n    float Saturation;\r\n    float Warmth;\r\n    float DenoiseAmount;\r\n    float DenoiseEdgeThreshold;\n    float TexelWidth;\n    float TexelHeight;\n};\n\r\nstruct VertexOutput\r\n{\r\n    float4 Position : SV_POSITION;\r\n    float2 TexCoord : TEXCOORD0;\r\n};\r\n\r\nVertexOutput VSMain(uint vertexId : SV_VertexID)\r\n{\r\n    float2 positions[3] =\r\n    {\r\n        float2(-1.0, -1.0),\r\n        float2(-1.0, 3.0),\r\n        float2(3.0, -1.0)\r\n    };\r\n    float2 texCoords[3] =\r\n    {\r\n        float2(0.0, 1.0),\r\n        float2(0.0, -1.0),\r\n        float2(2.0, 1.0)\r\n    };\r\n\r\n    VertexOutput output;\r\n    output.Position = float4(positions[vertexId], 0.0, 1.0);\r\n    output.TexCoord = texCoords[vertexId];\r\n    return output;\r\n}\r\n\r\nfloat3 ApplyColorPolish(float3 rgb)\r\n{\r\n    float3 rgb255 = rgb * 255.0;\r\n    rgb255.r = ((rgb255.r + ExposureOffset + Warmth - 128.0) * Contrast) + 128.0;\r\n    rgb255.g = ((rgb255.g + ExposureOffset - 128.0) * Contrast) + 128.0;\r\n    rgb255.b = ((rgb255.b + ExposureOffset - Warmth - 128.0) * Contrast) + 128.0;\r\n\r\n    float luma = dot(rgb255, float3(0.2126, 0.7152, 0.0722));\r\n    rgb255 = luma + (rgb255 - luma) * Saturation;\r\n    return saturate(rgb255 / 255.0);\r\n}\r\n\r\nfloat Luma(float3 rgb)\r\n{\r\n    return dot(rgb, float3(0.2126, 0.7152, 0.0722));\r\n}\r\n\r\nfloat EdgeAwareWeight(float sampleY, float centerY)\r\n{\r\n    return saturate(1.0 - abs(sampleY - centerY) / max(DenoiseEdgeThreshold, 0.0001));\r\n}\r\n\r\nfloat3 ApplyBgraDenoise(float3 centerRgb, float2 texCoord)\r\n{\r\n    if (DenoiseAmount <= 0.001)\r\n    {\r\n        return centerRgb;\r\n    }\r\n\r\n    float2 xOffset = float2(TexelWidth, 0.0);\r\n    float2 yOffset = float2(0.0, TexelHeight);\r\n    float3 leftRgb = CameraFrame.Sample(CameraSampler, texCoord - xOffset).rgb;\r\n    float3 rightRgb = CameraFrame.Sample(CameraSampler, texCoord + xOffset).rgb;\r\n    float3 upRgb = CameraFrame.Sample(CameraSampler, texCoord - yOffset).rgb;\r\n    float3 downRgb = CameraFrame.Sample(CameraSampler, texCoord + yOffset).rgb;\r\n\r\n    float centerY = Luma(centerRgb);\r\n    float centerWeight = 2.0;\r\n    float leftWeight = EdgeAwareWeight(Luma(leftRgb), centerY);\r\n    float rightWeight = EdgeAwareWeight(Luma(rightRgb), centerY);\r\n    float upWeight = EdgeAwareWeight(Luma(upRgb), centerY);\r\n    float downWeight = EdgeAwareWeight(Luma(downRgb), centerY);\r\n    float totalWeight = centerWeight + leftWeight + rightWeight + upWeight + downWeight;\r\n    float3 smoothedRgb = (\r\n        centerRgb * centerWeight\r\n        + leftRgb * leftWeight\r\n        + rightRgb * rightWeight\r\n        + upRgb * upWeight\r\n        + downRgb * downWeight) / max(totalWeight, 0.0001);\r\n    return lerp(centerRgb, smoothedRgb, DenoiseAmount);\r\n}\r\n\r\nfloat4 PSMain(VertexOutput input) : SV_TARGET\r\n{\r\n    float4 color = CameraFrame.Sample(CameraSampler, input.TexCoord);\r\n    float3 denoised = ApplyBgraDenoise(color.rgb, input.TexCoord);\r\n    return float4(ApplyColorPolish(denoised), 1.0);\r\n}", entryPoint, profile);
		}

		private static byte[] CompileShader(string shaderSource, string entryPoint, string profile)
		{
			return Compiler.Compile(shaderSource, entryPoint, "AvatarBuilderPreview.hlsl", profile, ShaderFlags.OptimizationLevel3).ToArray();
		}

		public void Resize(int width, int height)
		{
			if (!_disposed && width > 0 && height > 0 && (width != _viewportWidth || height != _viewportHeight))
			{
				WaitForGpu();
				_trackingOverlayRenderer?.ReleaseBackBuffers();
				ReleaseRenderTargets();
				_swapChain.ResizeBuffers(3u, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
				_viewportWidth = width;
				_viewportHeight = height;
				CreateRenderTargetViews();
				TryAttachTrackingOverlayRenderer();
			}
		}

		public void RequestPresentationRefresh()
		{
			if (!_disposed)
			{
				Interlocked.Exchange(ref _presentationRefreshRequested, 1);
			}
		}

		public void Dispose()
		{
			if (!_disposed)
			{
				try
				{
					WaitForGpu();
				}
				catch (TimeoutException)
				{
					// A wedged GPU must never hold the application shutdown or
					// camera recovery path. Leave these native objects to process
					// teardown rather than releasing resources still owned by a
					// non-responsive command queue.
					_disposed = true;
					return;
				}
				_disposed = true;
				_trackingOverlayRenderer?.Dispose();
				_trackingOverlayRenderer = null;
				ReleaseRenderTargets();
				_swapChain.Dispose();
				_rtvHeap.Dispose();
				_srvHeap.Dispose();
				_cameraTexture?.Dispose();
				_nv12YTexture?.Dispose();
				_nv12UvTexture?.Dispose();
				_previewPipelineState?.Dispose();
				_previewRootSignature?.Dispose();
				_nv12PreviewPipelineState?.Dispose();
				_nv12PreviewRootSignature?.Dispose();
				_d3d11ProducerFence?.Dispose();
				_d3d11ProducerFence = null;
				_d3d11ProducerFenceHandle = IntPtr.Zero;
				_fence.Dispose();
				_fenceEvent.Dispose();
				_commandList.Dispose();
				FrameResource[] frameResources = _frameResources;
				for (int i = 0; i < frameResources.Length; i++)
				{
					frameResources[i].Dispose();
				}
				_factory.Dispose();
				_commandQueue.Dispose();
				_device.Dispose();
			}
		}

		private void CreateRenderTargetViews()
		{
			CpuDescriptorHandle cPUDescriptorHandleForHeapStart = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
			for (int i = 0; i < 3; i++)
			{
				_renderTargets[i] = _swapChain.GetBuffer<ID3D12Resource>((uint)i);
				_device.CreateRenderTargetView(_renderTargets[i], null, cPUDescriptorHandleForHeapStart);
				cPUDescriptorHandleForHeapStart += _rtvDescriptorSize;
			}
		}

		private void TryAttachTrackingOverlayRenderer()
		{
			if (_trackingOverlayRenderer == null)
			{
				return;
			}
			try
			{
				_trackingOverlayRenderer.AttachBackBuffers(_renderTargets);
			}
			catch
			{
				_trackingOverlayRenderer.Dispose();
				_trackingOverlayRenderer = null;
			}
		}

		private CpuDescriptorHandle GetRtvHandle(int frameIndex)
		{
			return _rtvHeap.GetCPUDescriptorHandleForHeapStart() + frameIndex * _rtvDescriptorSize;
		}

		private CpuDescriptorHandle GetSrvCpuHandle(int descriptorIndex)
		{
			return _srvHeap.GetCPUDescriptorHandleForHeapStart() + descriptorIndex * _srvDescriptorSize;
		}

		private GpuDescriptorHandle GetSrvGpuHandle(int descriptorIndex)
		{
			return _srvHeap.GetGPUDescriptorHandleForHeapStart() + descriptorIndex * _srvDescriptorSize;
		}

		private void ReleaseRenderTargets()
		{
			for (int i = 0; i < _renderTargets.Length; i++)
			{
				_renderTargets[i]?.Dispose();
				_renderTargets[i] = null;
			}
		}

		private FrameResource BeginFrame(out int frameIndex)
		{
			RefreshPresentationResourcesIfRequested();
			frameIndex = (int)_swapChain.CurrentBackBufferIndex;
			FrameResource frameResource = _frameResources[frameIndex];
			WaitForFrameResource(frameResource);
			frameResource.CommandAllocator.Reset();
			_commandList.Reset(frameResource.CommandAllocator);
			return frameResource;
		}

		private void RefreshPresentationResourcesIfRequested()
		{
			if (Interlocked.Exchange(ref _presentationRefreshRequested, 0) != 0 && _trackingOverlayRenderer != null)
			{
				_trackingOverlayRenderer.ResetOverlayCache();
			}
		}

		private void ExecuteAndPresent(FrameResource frameResource)
		{
			_commandList.Close();
			_commandQueue.ExecuteCommandList(_commandList);
			_swapChain.Present(0u, PresentFlags.None);
			SignalFrameSubmitted(frameResource);
		}

		private void ExecuteAndPresent(FrameResource frameResource, int frameIndex, PreviewTrackingOverlay trackingOverlay, bool useDirect2DOverlay)
		{
			_commandList.Close();
			_commandQueue.ExecuteCommandList(_commandList);
			if (useDirect2DOverlay)
			{
				_trackingOverlayRenderer.Draw(frameIndex, _viewportWidth, _viewportHeight, trackingOverlay);
			}
			_swapChain.Present(0u, PresentFlags.None);
			SignalFrameSubmitted(frameResource);
		}

		private void WaitForGpu()
		{
			if (!_disposed)
			{
				_fenceValue++;
				_commandQueue.Signal(_fence, _fenceValue);
				if (_fence.CompletedValue >= _fenceValue)
				{
					ClearFrameFenceValues();
					return;
				}
				_fence.SetEventOnCompletion(_fenceValue, _fenceEvent);
				if (!_fenceEvent.WaitOne(GpuOperationTimeout))
				{
					throw new TimeoutException(
						"DX12 preview GPU did not become idle within " +
						$"{GpuOperationTimeout.TotalSeconds:0.#} seconds.");
				}
				ClearFrameFenceValues();
			}
		}

		private void SignalFrameSubmitted(FrameResource frameResource)
		{
			if (!_disposed)
			{
				_fenceValue++;
				_commandQueue.Signal(_fence, _fenceValue);
				frameResource.FenceValue = _fenceValue;
				Volatile.Write(
					ref _lastSubmittedFenceValue,
					checked((long)_fenceValue));
			}
		}

		public bool WaitForFence(ulong fenceValue, TimeSpan timeout)
		{
			if (_disposed
				|| fenceValue == 0uL
				|| _fence.CompletedValue >= fenceValue)
			{
				return true;
			}
			long started = Stopwatch.GetTimestamp();
			while (!_disposed
				&& _fence.CompletedValue < fenceValue
				&& Stopwatch.GetElapsedTime(started) < timeout)
			{
				Thread.Sleep(1);
			}
			return _disposed || _fence.CompletedValue >= fenceValue;
		}

		private void WaitForFrameResource(FrameResource frameResource)
		{
			if (!_disposed && frameResource.FenceValue != 0L)
			{
				if (_fence.CompletedValue < frameResource.FenceValue)
				{
					_fence.SetEventOnCompletion(frameResource.FenceValue, _fenceEvent);
					if (!_fenceEvent.WaitOne(GpuOperationTimeout))
					{
						throw new TimeoutException(
							"DX12 preview GPU did not release a frame resource within " +
							$"{GpuOperationTimeout.TotalSeconds:0.#} seconds.");
					}
				}
				frameResource.FenceValue = 0uL;
			}
		}

		private void ClearFrameFenceValues()
		{
			FrameResource[] frameResources = _frameResources;
			for (int i = 0; i < frameResources.Length; i++)
			{
				frameResources[i].FenceValue = 0uL;
			}
		}
	}

	private static readonly TimeSpan RendererDisposeLockTimeout = TimeSpan.FromMilliseconds(250L);

	private static readonly TimeSpan GpuOperationTimeout = TimeSpan.FromSeconds(2L);

	private static readonly TimeSpan TextureSubmissionTimeout = TimeSpan.FromSeconds(2L);

	private static readonly TimeSpan RenderWorkerStopTimeout = TimeSpan.FromMilliseconds(500L);

	private nint _nativeD3D12Device;

	private readonly object _rendererLock = new object();

	private readonly object _renderWorkerLock = new object();

	private readonly object _renderThrottleLock = new object();

	private readonly AutoResetEvent _renderFrameReady = new AutoResetEvent(initialState: false);

	private readonly ConcurrentDictionary<long, TexturePreviewRead>
		_texturePreviewReads = new();

	private Direct3D12SwapChainRenderer? _renderer;

	private Thread? _renderThread;

	private AcceptedCameraFrame? _acceptedCameraFrame;

	private AcceptedTextureFrame? _acceptedTextureFrame;

	private string _previewPathDescription = "DX12 preview path pending";

	private readonly object _diagnosticsLock = new object();

	private Direct3D12PreviewDiagnostics _diagnostics = Direct3D12PreviewDiagnostics.Empty;

	private long _diagnosticsFpsWindowStartTimestamp = Stopwatch.GetTimestamp();

	private long _diagnosticsFpsWindowStartRenderedFrames;

	private long _submittedFrames;

	private long _renderedFrames;

	private long _droppedFrames;

	private long _lastRenderedFrameTimestamp;

	private double _renderFramesPerSecond;

	private string _recordingMode = "not recording";

	private PreviewTrackingOverlay _trackingOverlay = PreviewTrackingOverlay.Empty;

	private long _lastAcceptedRenderFrameTimestamp;

	private long _lastDiagnosticsPublishedTimestamp;

	private double _maxRenderFramesPerSecond;

	private int _renderingSuspended;

	private int _renderFrameBusy;

	private bool _renderWorkerStopping;

	private bool _disposed;

	public bool IsReady => _renderer != null;

	public string DeviceDescription => _renderer?.DeviceDescription ?? "DX12 preview not initialized";

	public string PreviewPathDescription => _previewPathDescription;

	public string RecordingMode => Volatile.Read(in _recordingMode);

	public long SubmittedFrames => Interlocked.Read(ref _submittedFrames);

	public long RenderedFrames => Interlocked.Read(ref _renderedFrames);

	public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

	public long LastRenderedFrameTimestamp =>
		Volatile.Read(ref _lastRenderedFrameTimestamp);

	public double RenderFramesPerSecond => Volatile.Read(ref _renderFramesPerSecond);

	public Direct3D12PreviewDiagnostics Diagnostics
	{
		get
		{
			lock (_diagnosticsLock)
			{
				return _diagnostics;
			}
		}
	}

	private bool IsRenderingSuspended => Volatile.Read(in _renderingSuspended) != 0;

	public event EventHandler<string>? StatusChanged;

	public event EventHandler<Direct3D12PreviewDiagnostics>? DiagnosticsChanged;

	public Direct3D12PreviewHost(nint nativeD3D12Device = 0)
		: base("Could not create DX12 preview child window.")
	{
		_nativeD3D12Device = nativeD3D12Device;
		_renderThread = new Thread(RenderWorkerLoop)
		{
			IsBackground = true,
			Name = "Avatar Builder DX12 preview",
			Priority = ThreadPriority.AboveNormal
		};
		_renderThread.Start();
	}

	public void SetRecordingMode(string recordingMode)
	{
		Volatile.Write(ref _recordingMode, string.IsNullOrWhiteSpace(recordingMode) ? "not recording" : recordingMode.Trim());
	}

	public void LimitRenderRate(double maxFramesPerSecond)
	{
		lock (_renderThrottleLock)
		{
			_maxRenderFramesPerSecond = ((maxFramesPerSecond <= 0.0) ? 0.0 : Math.Clamp(maxFramesPerSecond, 1.0, 120.0));
			_lastAcceptedRenderFrameTimestamp = 0L;
		}
	}

	private sealed class TexturePreviewRead : IDisposable
	{
		private readonly ManualResetEventSlim _submitted = new(false);

		private long _fenceValue;

		private int _published;

		private int _discarded;

		private int _disposed;

		public void Publish(ulong fenceValue)
		{
			if (Interlocked.Exchange(ref _published, 1) != 0)
			{
				return;
			}
			Volatile.Write(ref _fenceValue, checked((long)fenceValue));
			try
			{
				_submitted.Set();
			}
			catch (ObjectDisposedException)
			{
				return;
			}
			if (Volatile.Read(ref _discarded) != 0)
			{
				Dispose();
			}
		}

		public void Discard()
		{
			if (Interlocked.Exchange(ref _discarded, 1) == 0
				&& Volatile.Read(ref _published) != 0)
			{
				Dispose();
			}
		}

		public bool TryWaitForSubmission(
			TimeSpan timeout,
			out ulong fenceValue)
		{
			fenceValue = 0uL;
			try
			{
				if (!_submitted.Wait(timeout))
				{
					return false;
				}
			}
			catch (ObjectDisposedException)
			{
				return false;
			}
			fenceValue = checked((ulong)Math.Max(
				0L,
				Volatile.Read(ref _fenceValue)));
			return true;
		}

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0)
			{
				_submitted.Dispose();
			}
		}
	}

	public void RenderBgraFrame(CameraFrame frame, long frameNumber, VideoFrameColorSettings colorSettings = default(VideoFrameColorSettings), bool denoiseEnabled = false, double denoiseStrength = 0.0)
	{
		if (_disposed || IsRenderingSuspended)
		{
			return;
		}
		RecordSubmittedFrame();
		if (!ShouldAcceptRenderFrame())
		{
			RecordDroppedFrame();
			return;
		}
		if (Interlocked.CompareExchange(ref _renderFrameBusy, 1, 0) != 0)
		{
			RecordDroppedFrame();
			return;
		}
		AcceptedCameraFrame acceptedCameraFrame = null;
		bool flag = false;
		try
		{
			acceptedCameraFrame = new AcceptedCameraFrame(frame.Duplicate(), colorSettings, denoiseEnabled, denoiseStrength, frameNumber);
			lock (_renderWorkerLock)
			{
				if (_renderWorkerStopping || (object)_acceptedCameraFrame != null || (object)_acceptedTextureFrame != null)
				{
					RecordDroppedFrame();
					return;
				}
				_acceptedCameraFrame = acceptedCameraFrame;
				acceptedCameraFrame = null;
				flag = true;
			}
			_renderFrameReady.Set();
		}
		catch (ObjectDisposedException)
		{
			RecordDroppedFrame();
			CancelAcceptedRenderHandoff();
			flag = false;
		}
		catch
		{
			RecordDroppedFrame();
		}
		finally
		{
			acceptedCameraFrame?.Dispose();
			if (!flag)
			{
				Interlocked.Exchange(ref _renderFrameBusy, 0);
			}
		}
	}

	public void RenderProofFrame(TextureNativeFrameInfo frame)
	{
		if (_renderer == null || IsRenderingSuspended)
		{
			return;
		}
		try
		{
			lock (_rendererLock)
			{
				_renderer.RenderProofFrame(frame.FrameNumber);
			}
		}
		catch (Exception ex)
		{
			this.StatusChanged?.Invoke(this, "DX12 preview render failed: " + ex.Message);
		}
	}

	public void RenderTextureFrame(TextureNativeFrameLease frame, bool denoiseEnabled, double denoiseStrength, VideoFrameColorSettings colorSettings = default(VideoFrameColorSettings))
	{
		RecordSubmittedFrame();
		if (_disposed || IsRenderingSuspended || !ShouldAcceptRenderFrame())
		{
			RecordDroppedFrame();
			return;
		}
		if (Interlocked.CompareExchange(ref _renderFrameBusy, 1, 0) != 0)
		{
			RecordDroppedFrame();
			return;
		}
		bool flag = false;
		bool flag2 = string.Equals(frame.DeviceMode, "D3D12", StringComparison.OrdinalIgnoreCase);
		bool flag3 = TextureNativePreviewPolicy.ShouldPreferNv12UploadFallback(frame.MediaSubtype, frame.Width, frame.Height, frame.Nv12PreviewBytes, frame.Nv12PreviewStride);
		string? failureReason = !flag2
			&& !flag3
			&& frame.D3D12SharedTextureHandle == IntPtr.Zero
				? "D3D11 bridge shared texture handle missing"
				: null;
		TextureNativeFrameLease textureNativeFrameLease = null;
		TexturePreviewRead? previewRead = null;
		try
		{
			textureNativeFrameLease = frame.Duplicate();
			if (textureNativeFrameLease == null)
			{
				RecordDroppedFrame();
				return;
			}
			lock (_renderWorkerLock)
			{
				if (_renderWorkerStopping || (object)_acceptedCameraFrame != null || (object)_acceptedTextureFrame != null)
				{
					RecordDroppedFrame();
					return;
				}
				previewRead = new TexturePreviewRead();
				if (_texturePreviewReads.TryRemove(
					frame.FrameNumber,
					out TexturePreviewRead? previousRead))
				{
					previousRead.Dispose();
				}
				_texturePreviewReads[frame.FrameNumber] = previewRead;
				_acceptedTextureFrame = new AcceptedTextureFrame(
					textureNativeFrameLease,
					colorSettings,
					denoiseEnabled,
					denoiseStrength,
					failureReason,
					previewRead);
				textureNativeFrameLease = null;
				previewRead = null;
				flag = true;
			}
			_renderFrameReady.Set();
		}
		catch (ObjectDisposedException)
		{
			RecordDroppedFrame();
			CancelAcceptedRenderHandoff();
			flag = false;
		}
		finally
		{
			previewRead?.Dispose();
			textureNativeFrameLease?.Dispose();
			if (!flag)
			{
				Interlocked.Exchange(ref _renderFrameBusy, 0);
			}
		}
	}

	public void UpdateTrackingOverlay(PreviewTrackingOverlay? overlay)
	{
		Volatile.Write(ref _trackingOverlay, overlay ?? PreviewTrackingOverlay.Empty);
	}

	private PreviewTrackingOverlay GetFreshTrackingOverlay()
	{
		PreviewTrackingOverlay overlay = Volatile.Read(in _trackingOverlay);
		return overlay.IsFresh ? overlay : PreviewTrackingOverlay.Empty;
	}

	public void ResumeRendering()
	{
		if (!_disposed)
		{
			lock (_renderThrottleLock)
			{
				_lastAcceptedRenderFrameTimestamp = 0L;
			}
			Interlocked.Exchange(ref _renderingSuspended, 0);
			Volatile.Read(in _renderer)?.RequestPresentationRefresh();
			_renderFrameReady.Set();
		}
	}

	public void SuspendRendering()
	{
		if (_disposed)
		{
			return;
		}
		Interlocked.Exchange(ref _renderingSuspended, 1);
		lock (_renderWorkerLock)
		{
			bool num = (object)_acceptedCameraFrame != null || (object)_acceptedTextureFrame != null;
			_acceptedCameraFrame?.Dispose();
			_acceptedCameraFrame = null;
			_acceptedTextureFrame?.PreviewRead.Publish(0uL);
			_acceptedTextureFrame?.Dispose();
			_acceptedTextureFrame = null;
			if (num)
			{
				Interlocked.Exchange(ref _renderFrameBusy, 0);
			}
		}
		_renderFrameReady.Set();
		if (Monitor.TryEnter(_rendererLock, TimeSpan.FromMilliseconds(100L)))
		{
			Monitor.Exit(_rendererLock);
		}
	}

	private void RenderTextureFrameCore(AcceptedTextureFrame acceptedFrame)
	{
		TextureNativeFrameLease frame = acceptedFrame.Frame;
		if (IsRenderingSuspended)
		{
			RecordDroppedFrame();
			acceptedFrame.PreviewRead.Publish(0uL);
			return;
		}
		ulong submittedFenceValue = 0uL;
		try
		{
			lock (_rendererLock)
			{
				if (_renderer == null || IsRenderingSuspended)
				{
					RecordDroppedFrame();
					return;
				}
				string failureReason = null;
				if (frame.IsValid && string.Equals(frame.DeviceMode, "D3D12", StringComparison.OrdinalIgnoreCase) && _renderer.RenderNativeTextureFrame(frame, acceptedFrame.ColorSettings, acceptedFrame.DenoiseEnabled, acceptedFrame.DenoiseStrength, GetFreshTrackingOverlay(), out failureReason))
				{
					ReportPreviewPath("direct DX12 texture");
					RecordRenderedFrame("direct DX12 texture", frame.MediaSubtype, frame.Width, frame.Height, frame.FramesPerSecond, acceptedFrame.DenoiseEnabled, acceptedFrame.DenoiseStrength, acceptedFrame.ColorSettings, RecordingMode, null, frame.FrameNumber);
					submittedFenceValue =
						_renderer.LastSubmittedFenceValue;
					return;
				}
				bool preferNv12Upload =
					TextureNativePreviewPolicy.ShouldPreferNv12UploadFallback(
						frame.MediaSubtype,
						frame.Width,
						frame.Height,
						frame.Nv12PreviewBytes,
						frame.Nv12PreviewStride);
				string? sharedBridgeFailureReason = null;
				if (!string.Equals(
						frame.DeviceMode,
						"D3D12",
						StringComparison.OrdinalIgnoreCase)
					&& !preferNv12Upload
					&& frame.D3D12SharedTextureHandle != IntPtr.Zero
					&& _renderer.RenderSharedD3D11BridgeFrame(
						frame,
						acceptedFrame.ColorSettings,
						acceptedFrame.DenoiseEnabled,
						acceptedFrame.DenoiseStrength,
						GetFreshTrackingOverlay(),
						out sharedBridgeFailureReason))
				{
					ReportPreviewPath(
						"DX12 D3D11 bridge texture preview");
					RecordRenderedFrame(
						"DX12 D3D11 bridge texture preview",
						frame.MediaSubtype,
						frame.Width,
						frame.Height,
						frame.FramesPerSecond,
						acceptedFrame.DenoiseEnabled,
						acceptedFrame.DenoiseStrength,
						acceptedFrame.ColorSettings,
						RecordingMode,
						null,
						frame.FrameNumber);
					submittedFenceValue =
						_renderer.LastSubmittedFenceValue;
					return;
				}
				if (!string.IsNullOrWhiteSpace(
					sharedBridgeFailureReason))
				{
					failureReason = sharedBridgeFailureReason;
				}
				string text = CombineTextureFailureReasons(failureReason, acceptedFrame.SharedBridgeFailureReason);
				if (!TryRenderNv12TextureUpload(_renderer, frame, text, acceptedFrame.ColorSettings, acceptedFrame.DenoiseEnabled, acceptedFrame.DenoiseStrength, GetFreshTrackingOverlay()))
				{
					_renderer.RenderProofFrame(frame.FrameNumber);
					ReportPreviewPath(FormatUploadFallbackPath("DX12 proof-frame fallback", text));
					RecordDroppedFrame();
				}
			}
		}
		catch (Exception ex)
		{
			RecordDroppedFrame();
			this.StatusChanged?.Invoke(this, "DX12 camera frame upload failed: " + ex.Message);
		}
		finally
		{
			acceptedFrame.PreviewRead.Publish(submittedFenceValue);
		}
	}

	private void ReportPreviewPath(string description)
	{
		if (!string.Equals(_previewPathDescription, description, StringComparison.Ordinal))
		{
			_previewPathDescription = description;
			this.StatusChanged?.Invoke(this, "DX12 preview path: " + description);
		}
	}

	private void RenderWorkerLoop()
	{
		try
		{
			while (true)
			{
				_renderFrameReady.WaitOne();
				AcceptedTextureFrame acceptedTextureFrame;
				AcceptedCameraFrame acceptedCameraFrame;
				lock (_renderWorkerLock)
				{
					if (_renderWorkerStopping)
					{
						break;
					}
					acceptedTextureFrame = _acceptedTextureFrame;
					_acceptedTextureFrame = null;
					acceptedCameraFrame = (((object)acceptedTextureFrame == null) ? _acceptedCameraFrame : null);
					_acceptedCameraFrame = null;
				}
				try
				{
					if ((object)acceptedTextureFrame != null)
					{
						using (acceptedTextureFrame)
						{
							RenderTextureFrameCore(acceptedTextureFrame);
						}
					}
					else
					{
						if ((object)acceptedCameraFrame == null)
						{
							continue;
						}
						using (acceptedCameraFrame)
						{
							try
							{
								lock (_rendererLock)
								{
									if (_renderer != null && !IsRenderingSuspended)
									{
										byte[] nv12Bytes = acceptedCameraFrame.Nv12Bytes;
										if (nv12Bytes == null || nv12Bytes.Length <= 0 || acceptedCameraFrame.Nv12Stride <= 0)
										{
											goto IL_0216;
										}
										if (!_renderer.RenderNv12Frame(nv12Bytes, acceptedCameraFrame.Width, acceptedCameraFrame.Height, acceptedCameraFrame.Nv12Stride, acceptedCameraFrame.FrameNumber, acceptedCameraFrame.ColorSettings, acceptedCameraFrame.DenoiseEnabled, acceptedCameraFrame.DenoiseStrength, GetFreshTrackingOverlay(), acceptedCameraFrame.FrameFormat == "nv12-ffmpeg"))
										{
											this.StatusChanged?.Invoke(this, $"DX12 NV12 preview renderer refused {acceptedCameraFrame.Width}x{acceptedCameraFrame.Height}, stride {acceptedCameraFrame.Nv12Stride}, bytes {nv12Bytes.Length}: {_renderer.LastNv12PreviewFailureReason}");
											goto IL_0216;
										}
										ReportPreviewPath("DX12 NV12 upload preview");
										RecordRenderedFrame("DX12 NV12 upload preview", acceptedCameraFrame.FrameFormat, acceptedCameraFrame.Width, acceptedCameraFrame.Height, 0.0, acceptedCameraFrame.DenoiseEnabled, acceptedCameraFrame.DenoiseStrength, acceptedCameraFrame.ColorSettings, RecordingMode, null, acceptedCameraFrame.FrameNumber);
									}
									goto end_IL_0085;
									IL_0216:
									if (acceptedCameraFrame.BgraBytes.Length == 0 || acceptedCameraFrame.Stride <= 0)
									{
										this.StatusChanged?.Invoke(this, "DX12 preview skipped: frame had no renderable BGRA or NV12 payload.");
										continue;
									}
									_renderer.RenderBgraFrame(acceptedCameraFrame.BgraBytes, acceptedCameraFrame.Width, acceptedCameraFrame.Height, acceptedCameraFrame.Stride, acceptedCameraFrame.FrameNumber, acceptedCameraFrame.ColorSettings, acceptedCameraFrame.DenoiseEnabled, acceptedCameraFrame.DenoiseStrength, GetFreshTrackingOverlay());
									goto IL_0296;
									end_IL_0085:;
								}
								goto end_IL_007c;
								IL_0296:
								ReportPreviewPath("DX12 BGRA upload preview");
								RecordRenderedFrame("DX12 BGRA upload preview", acceptedCameraFrame.FrameFormat, acceptedCameraFrame.Width, acceptedCameraFrame.Height, 0.0, acceptedCameraFrame.DenoiseEnabled, acceptedCameraFrame.DenoiseStrength, acceptedCameraFrame.ColorSettings, RecordingMode, null, acceptedCameraFrame.FrameNumber);
								end_IL_007c:;
							}
							catch (Exception ex)
							{
								RecordDroppedFrame();
								this.StatusChanged?.Invoke(this, "DX12 BGRA preview upload failed: " + ex.Message);
							}
						}
						continue;
					}
				}
				finally
				{
					Interlocked.Exchange(ref _renderFrameBusy, 0);
				}
			}
		}
		finally
		{
			DisposeRenderer();
		}
	}

	private bool StopRenderWorker()
	{
		lock (_renderWorkerLock)
		{
			_renderWorkerStopping = true;
			bool num = (object)_acceptedCameraFrame != null || (object)_acceptedTextureFrame != null;
			_acceptedCameraFrame?.Dispose();
			_acceptedCameraFrame = null;
			_acceptedTextureFrame?.PreviewRead.Publish(0uL);
			_acceptedTextureFrame?.Dispose();
			_acceptedTextureFrame = null;
			if (num)
			{
				Interlocked.Exchange(ref _renderFrameBusy, 0);
			}
		}
		_renderFrameReady.Set();
		Thread renderThread = _renderThread;
		bool stopped = true;
		if (renderThread != null && renderThread != Thread.CurrentThread)
		{
			stopped = renderThread.Join(RenderWorkerStopTimeout);
		}
		if (stopped)
		{
			_renderThread = null;
			_renderFrameReady.Dispose();
		}
		return stopped;
	}

	private void CancelAcceptedRenderHandoff()
	{
		lock (_renderWorkerLock)
		{
			_acceptedCameraFrame?.Dispose();
			_acceptedCameraFrame = null;
			_acceptedTextureFrame?.PreviewRead.Publish(0uL);
			_acceptedTextureFrame?.Dispose();
			_acceptedTextureFrame = null;
		}
	}

	public bool WaitForTextureFrameRead(long frameNumber)
	{
		if (!_texturePreviewReads.TryRemove(
			frameNumber,
			out TexturePreviewRead? previewRead))
		{
			return true;
		}
		bool submitted = false;
		try
		{
			if (!previewRead.TryWaitForSubmission(
				TextureSubmissionTimeout,
				out ulong fenceValue))
			{
				previewRead.Discard();
				return false;
			}
			submitted = true;
			if (fenceValue == 0uL)
			{
				return true;
			}
			Direct3D12SwapChainRenderer? renderer =
				Volatile.Read(ref _renderer);
			return renderer == null
				|| renderer.WaitForFence(
					fenceValue,
					GpuOperationTimeout);
		}
		finally
		{
			if (submitted)
			{
				previewRead.Dispose();
			}
		}
	}

	public void DiscardTextureFrameRead(long frameNumber)
	{
		if (_texturePreviewReads.TryRemove(
			frameNumber,
			out TexturePreviewRead? previewRead))
		{
			previewRead.Discard();
		}
	}

	private void RecordSubmittedFrame()
	{
		Interlocked.Increment(ref _submittedFrames);
	}

	private bool ShouldAcceptRenderFrame()
	{
		lock (_renderThrottleLock)
		{
			if (_maxRenderFramesPerSecond <= 0.0)
			{
				return true;
			}
			long timestamp = Stopwatch.GetTimestamp();
			long minimumInterval = Math.Max(1L, (long)(Stopwatch.Frequency / _maxRenderFramesPerSecond));
			if (timestamp - _lastAcceptedRenderFrameTimestamp < minimumInterval)
			{
				return false;
			}
			_lastAcceptedRenderFrameTimestamp = timestamp;
			return true;
		}
	}

	private void RecordDroppedFrame()
	{
		Interlocked.Increment(ref _droppedFrames);
	}

	private void RecordRenderedFrame(string previewPath, string frameFormat, int width, int height, double sourceFramesPerSecond, bool denoiseEnabled, double denoiseStrength, VideoFrameColorSettings colorSettings, string recordingMode, string? fallbackReason, long frameNumber)
	{
		long num = Interlocked.Increment(ref _renderedFrames);
		long timestamp = Stopwatch.GetTimestamp();
		Volatile.Write(ref _lastRenderedFrameTimestamp, timestamp);
		long previousTimestamp = Volatile.Read(ref _lastDiagnosticsPublishedTimestamp);
		if (timestamp - previousTimestamp < Stopwatch.Frequency * 2L
			|| Interlocked.CompareExchange(ref _lastDiagnosticsPublishedTimestamp, timestamp, previousTimestamp) != previousTimestamp)
		{
			return;
		}
		long submittedFrames = Interlocked.Read(in _submittedFrames);
		long droppedFrames = Interlocked.Read(in _droppedFrames);
		DateTimeOffset utcNow = DateTimeOffset.UtcNow;
		Direct3D12PreviewDiagnostics e;
		lock (_diagnosticsLock)
		{
			double elapsedSeconds = Math.Max(
				0.001,
				(double)(timestamp - _diagnosticsFpsWindowStartTimestamp) / Stopwatch.Frequency);
			long num2 = Math.Max(0L, num - _diagnosticsFpsWindowStartRenderedFrames);
			_renderFramesPerSecond = num2 / elapsedSeconds;
			_diagnosticsFpsWindowStartTimestamp = timestamp;
			_diagnosticsFpsWindowStartRenderedFrames = num;
			e = (_diagnostics = new Direct3D12PreviewDiagnostics(previewPath, DeviceDescription, string.IsNullOrWhiteSpace(frameFormat) ? "unknown" : frameFormat, width, height, sourceFramesPerSecond, submittedFrames, num, droppedFrames, _renderFramesPerSecond, denoiseEnabled, denoiseStrength, colorSettings.HasVisibleAdjustments, recordingMode, fallbackReason, frameNumber, utcNow));
		}
		this.DiagnosticsChanged?.Invoke(this, e);
	}

	private static string FormatUploadFallbackPath(string path, string? directTextureFailureReason)
	{
		if (string.IsNullOrWhiteSpace(directTextureFailureReason))
		{
			return path;
		}
		string text = directTextureFailureReason.Trim();
		if (text.Length > 160)
		{
			text = text.Substring(0, 157) + "...";
		}
		return path + "; texture unavailable: " + text;
	}

	private static string? CombineTextureFailureReasons(string? directTextureFailureReason, string? sharedBridgeFailureReason)
	{
		string text = (string.IsNullOrWhiteSpace(directTextureFailureReason) ? null : ("direct: " + directTextureFailureReason.Trim()));
		string text2 = (string.IsNullOrWhiteSpace(sharedBridgeFailureReason) ? null : ("bridge: " + sharedBridgeFailureReason.Trim()));
		if (text == null)
		{
			if (text2 != null)
			{
				return text2;
			}
			return null;
		}
		if (text2 != null)
		{
			return text + "; " + text2;
		}
		return text;
	}

	private bool TryRenderNv12TextureUpload(Direct3D12SwapChainRenderer renderer, TextureNativeFrameLease frame, string? textureFailureReason, VideoFrameColorSettings colorSettings, bool denoiseEnabled, double denoiseStrength, PreviewTrackingOverlay trackingOverlay)
	{
		byte[] nv12PreviewBytes = frame.Nv12PreviewBytes;
		if (nv12PreviewBytes == null || nv12PreviewBytes.Length <= 0 || frame.Nv12PreviewStride <= 0)
		{
			return false;
		}
		if (!renderer.RenderNv12Frame(nv12PreviewBytes, frame.Width, frame.Height, frame.Nv12PreviewStride, frame.FrameNumber, colorSettings, denoiseEnabled, denoiseStrength, trackingOverlay))
		{
			return false;
		}
		string text = FormatUploadFallbackPath("DX12 NV12 texture upload", textureFailureReason);
		ReportPreviewPath(text);
		RecordRenderedFrame(text, "NV12 texture upload", frame.Width, frame.Height, frame.FramesPerSecond, denoiseEnabled, denoiseStrength, colorSettings, RecordingMode, textureFailureReason, frame.FrameNumber);
		return true;
	}

	protected override void OnViewportCreated(nint hwnd, int width, int height)
	{
		nint num = Interlocked.Exchange(ref _nativeD3D12Device, IntPtr.Zero);
		Direct3D12SwapChainRenderer direct3D12SwapChainRenderer;
		try
		{
			lock (_rendererLock)
			{
				direct3D12SwapChainRenderer = (_renderer = new Direct3D12SwapChainRenderer(hwnd, width, height, num));
			}
			num = IntPtr.Zero;
		}
		finally
		{
			if (num != IntPtr.Zero)
			{
				Marshal.Release(num);
			}
		}
		this.StatusChanged?.Invoke(this, direct3D12SwapChainRenderer.DeviceDescription + " preview surface ready.");
	}

	protected override void OnViewportCreateFailed(Exception ex)
	{
		this.StatusChanged?.Invoke(this, "DX12 preview surface unavailable: " + ex.Message);
	}

	protected override void OnViewportDestroying()
	{
		TryDisposeRenderer("window destroy");
	}

	protected override void OnViewportResized(int width, int height)
	{
		lock (_rendererLock)
		{
			_renderer?.Resize(width, height);
		}
	}

	protected override void OnViewportResizeFailed(Exception ex)
	{
		this.StatusChanged?.Invoke(this, "DX12 preview resize failed: " + ex.Message);
	}

	public new void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
			StopRenderWorker();
			nint num = Interlocked.Exchange(ref _nativeD3D12Device, IntPtr.Zero);
			if (num != IntPtr.Zero)
			{
				Marshal.Release(num);
			}
			base.Dispose();
		}
	}

	private void DisposeRenderer()
	{
		lock (_rendererLock)
		{
			DisposeRendererCore();
		}
	}

	private void TryDisposeRenderer(string context)
	{
		if (!Monitor.TryEnter(_rendererLock, RendererDisposeLockTimeout))
		{
			this.StatusChanged?.Invoke(this, "DX12 preview " + context + " deferred because the renderer is busy.");
			return;
		}
		try
		{
			DisposeRendererCore();
		}
		finally
		{
			Monitor.Exit(_rendererLock);
		}
	}

	private void DisposeRendererCore()
	{
		_renderer?.Dispose();
		_renderer = null;
	}
}
