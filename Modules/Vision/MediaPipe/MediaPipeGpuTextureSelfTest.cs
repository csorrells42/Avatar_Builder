using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AvatarBuilder.Modules.Vision.Common;
using AvatarBuilder.Modules.Vision.Identity;
using AvatarBuilder.Modules.Webcam.DirectX12;
using OpenCvSharp;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using static Vortice.Direct3D12.D3D12;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

public sealed record MediaPipeGpuTextureSelfTestResult(
	bool Succeeded,
	string Detail);

public static class MediaPipeGpuTextureSelfTest
{
	public static MediaPipeGpuTextureSelfTestResult RunFrame(
		TextureNativeFrameLease frame)
	{
		try
		{
			MediaPipeDirectMlModelEnvironment models =
				MediaPipeSidecarPythonEnvironment.DetectDirectMlModels();
			if (!models.IsReady)
			{
				return new MediaPipeGpuTextureSelfTestResult(
					false,
					models.Status);
			}

			using MediaPipeDirectMlTextureTracker tracker = new(
				frame,
				models.DetectorModelPath,
				models.LandmarkerModelPath);
			FaceLandmarkTrackingResult? result = null;
			var samples = new double[4];
			for (int index = 0; index < samples.Length; index++)
			{
				long started = Stopwatch.GetTimestamp();
				result = tracker.Detect(
					frame,
					DateTime.UtcNow.AddMilliseconds(index));
				samples[index] =
					Stopwatch.GetElapsedTime(started).TotalMilliseconds;
			}

			double averageMilliseconds = 0d;
			for (int index = 1; index < samples.Length; index++)
			{
				averageMilliseconds += samples[index];
			}
			averageMilliseconds /= samples.Length - 1;
			int densePoints =
				result?.LandmarkFrame.DenseMeshPoints.Count ?? 0;
			int poseValues =
				result?.LandmarkFrame.FacialTransformationMatrix.Count ?? 0;
			bool succeeded =
				result?.HasFace == true
				&& densePoints == 478
				&& poseValues == 16;
			return new MediaPipeGpuTextureSelfTestResult(
				succeeded,
				$"{result?.BackendStatus ?? "no result"}; " +
				$"{densePoints} dense points; {poseValues} pose values; " +
				"steady live texture inference " +
				$"{averageMilliseconds:0.00} ms.");
		}
		catch (Exception ex)
		{
			return new MediaPipeGpuTextureSelfTestResult(
				false,
				ex.ToString());
		}
	}

	public static MediaPipeGpuTextureSelfTestResult RunBgra(
		byte[] bgra,
		int width,
		int height,
		int stride)
	{
		if (width <= 0
			|| height <= 0
			|| (width & 1) != 0
			|| (height & 1) != 0
			|| stride < width * 4
			|| bgra.Length < stride * height)
		{
			return new MediaPipeGpuTextureSelfTestResult(
				false,
				"GPU texture self-test requires an even-sized BGRA frame.");
		}

		try
		{
			MediaPipeDirectMlModelEnvironment models =
				MediaPipeSidecarPythonEnvironment.DetectDirectMlModels();
			if (!models.IsReady)
			{
				return new MediaPipeGpuTextureSelfTestResult(
					false,
					models.Status);
			}

			(byte[] luma, byte[] chroma) =
				ConvertBgraToNv12(bgra, width, height, stride);
			using Nv12TextureUpload upload =
				new(width, height, luma, chroma);
			using TextureNativeFrameLease frame =
				upload.CreateFrameLease();
			long identityReadbackStarted = Stopwatch.GetTimestamp();
			using var identityReader =
				new D3D12Nv12IdentityFrameReader(frame);
			using Mat identityBgr = identityReader.ReadBgr(frame);
			double identityReadbackMilliseconds =
				Stopwatch.GetElapsedTime(
					identityReadbackStarted).TotalMilliseconds;
			Scalar identityMean = Cv2.Mean(identityBgr);
			bool identityReadbackValid =
				identityBgr.Width == width
				&& identityBgr.Height == height
				&& identityBgr.Channels() == 3
				&& identityMean.Val0
					+ identityMean.Val1
					+ identityMean.Val2 > 1d;
			long started = Stopwatch.GetTimestamp();
			using MediaPipeDirectMlTextureTracker tracker = new(
				frame,
				models.DetectorModelPath,
				models.LandmarkerModelPath);
			double startupMilliseconds =
				Stopwatch.GetElapsedTime(started).TotalMilliseconds;

			var samples = new double[4];
			FaceLandmarkTrackingResult? result = null;
			for (int index = 0; index < samples.Length; index++)
			{
				long inferenceStarted = Stopwatch.GetTimestamp();
				result = tracker.Detect(
					frame,
					DateTime.UtcNow.AddMilliseconds(index));
				samples[index] =
					Stopwatch.GetElapsedTime(inferenceStarted).TotalMilliseconds;
			}

			double averageMilliseconds = 0d;
			for (int index = 1; index < samples.Length; index++)
			{
				averageMilliseconds += samples[index];
			}
			averageMilliseconds /= samples.Length - 1;
			int densePoints =
				result?.LandmarkFrame.DenseMeshPoints.Count ?? 0;
			int poseValues =
				result?.LandmarkFrame.FacialTransformationMatrix.Count ?? 0;
			bool succeeded =
				result?.HasFace == true
				&& densePoints == 478
				&& poseValues == 16
				&& identityReadbackValid;
			return new MediaPipeGpuTextureSelfTestResult(
				succeeded,
				$"{result?.BackendStatus ?? "no result"}; " +
				$"{densePoints} dense points; {poseValues} pose values; " +
				"DirectML texture session startup " +
				$"{startupMilliseconds:0.00} ms; steady texture inference " +
				$"{averageMilliseconds:0.00} ms " +
				$"({1000d / Math.Max(0.001d, averageMilliseconds):0.0} fps); " +
				$"identity NV12 texture readback " +
				$"{identityReadbackMilliseconds:0.00} ms; " +
				"CPU-to-GPU test upload excluded.");
		}
		catch (Exception ex)
		{
			return new MediaPipeGpuTextureSelfTestResult(
				false,
				ex.ToString());
		}
	}

	private static (byte[] Luma, byte[] Chroma) ConvertBgraToNv12(
		byte[] bgra,
		int width,
		int height,
		int stride)
	{
		byte[] luma = new byte[checked(width * height)];
		byte[] chroma = new byte[checked(width * height / 2)];
		for (int y = 0; y < height; y++)
		{
			int sourceRow = y * stride;
			int lumaRow = y * width;
			for (int x = 0; x < width; x++)
			{
				int source = sourceRow + x * 4;
				float blue = bgra[source];
				float green = bgra[source + 1];
				float red = bgra[source + 2];
				luma[lumaRow + x] = ClampByte(
					16f +
					0.182586f * red +
					0.614231f * green +
					0.062007f * blue);
			}
		}

		for (int y = 0; y < height; y += 2)
		{
			int chromaRow = y / 2 * width;
			for (int x = 0; x < width; x += 2)
			{
				float red = 0f;
				float green = 0f;
				float blue = 0f;
				for (int offsetY = 0; offsetY < 2; offsetY++)
				{
					for (int offsetX = 0; offsetX < 2; offsetX++)
					{
						int source =
							(y + offsetY) * stride +
							(x + offsetX) * 4;
						blue += bgra[source];
						green += bgra[source + 1];
						red += bgra[source + 2];
					}
				}
				red *= 0.25f;
				green *= 0.25f;
				blue *= 0.25f;
				chroma[chromaRow + x] = ClampByte(
					128f -
					0.100644f * red -
					0.338572f * green +
					0.439216f * blue);
				chroma[chromaRow + x + 1] = ClampByte(
					128f +
					0.439216f * red -
					0.398942f * green -
					0.040274f * blue);
			}
		}
		return (luma, chroma);
	}

	private static byte ClampByte(float value)
	{
		return (byte)Math.Clamp(
			(int)MathF.Round(value),
			byte.MinValue,
			byte.MaxValue);
	}

	private sealed class Nv12TextureUpload : IDisposable
	{
		private readonly ID3D12Device _device;

		private readonly ID3D12Resource _texture;

		private readonly int _width;

		private readonly int _height;

		public Nv12TextureUpload(
			int width,
			int height,
			byte[] luma,
			byte[] chroma)
		{
			_width = width;
			_height = height;
			_device = D3D12CreateDevice<ID3D12Device>(
				null,
				FeatureLevel.Level_12_0);
			ResourceDescription description = new(
				ResourceDimension.Texture2D,
				0uL,
				(ulong)width,
				(uint)height,
				1,
				1,
				Format.NV12,
				1u,
				0u,
				TextureLayout.Unknown,
				ResourceFlags.None);
			_texture =
				_device.CreateCommittedResource<ID3D12Resource>(
					new HeapProperties(HeapType.Default),
					HeapFlags.None,
					description,
					ResourceStates.CopyDest);

			PlacedSubresourceFootPrint[] footprints =
				new PlacedSubresourceFootPrint[2];
			uint[] rowCounts = new uint[2];
			ulong[] rowSizes = new ulong[2];
			_device.GetCopyableFootprints(
				description,
				0u,
				2u,
				0uL,
				footprints,
				rowCounts,
				rowSizes,
				out ulong uploadBytes);
			using ID3D12Resource upload =
				_device.CreateCommittedResource<ID3D12Resource>(
					new HeapProperties(HeapType.Upload),
					HeapFlags.None,
					ResourceDescription.Buffer(uploadBytes),
					ResourceStates.GenericRead);
			UploadPlane(
				upload,
				footprints[0],
				luma,
				width,
				height);
			UploadPlane(
				upload,
				footprints[1],
				chroma,
				width,
				height / 2);

			using ID3D12CommandQueue queue =
				_device.CreateCommandQueue<ID3D12CommandQueue>(
					new CommandQueueDescription(
						CommandListType.Direct));
			using ID3D12CommandAllocator allocator =
				_device.CreateCommandAllocator<ID3D12CommandAllocator>(
					CommandListType.Direct);
			using ID3D12GraphicsCommandList commandList =
				_device.CreateCommandList<ID3D12GraphicsCommandList>(
					0u,
					CommandListType.Direct,
					allocator);
			commandList.CopyTextureRegion(
				new TextureCopyLocation(_texture, 0u),
				0u,
				0u,
				0u,
				new TextureCopyLocation(upload, footprints[0]));
			commandList.CopyTextureRegion(
				new TextureCopyLocation(_texture, 1u),
				0u,
				0u,
				0u,
				new TextureCopyLocation(upload, footprints[1]));
			ResourceBarrier toCommon =
				ResourceBarrier.BarrierTransition(
					_texture,
					ResourceStates.CopyDest,
					ResourceStates.Common);
			commandList.ResourceBarrier(
				new Span<ResourceBarrier>(ref toCommon));
			commandList.Close();
			queue.ExecuteCommandList(commandList);
			using ID3D12Fence fence =
				_device.CreateFence<ID3D12Fence>(0uL);
			using AutoResetEvent completion =
				new(initialState: false);
			queue.Signal(fence, 1uL);
			fence.SetEventOnCompletion(1uL, completion);
			completion.WaitOne();
		}

		public TextureNativeFrameLease CreateFrameLease()
		{
			nint pointer = _texture.NativePointer;
			Marshal.AddRef(pointer);
			return new TextureNativeFrameLease(
				pointer,
				0,
				_width,
				_height,
				30d,
				"D3D12 GPU texture self-test",
				"NV12",
				1L,
				capturedAtTimestamp: Stopwatch.GetTimestamp(),
				capturedAtUtc: DateTime.UtcNow);
		}

		public void Dispose()
		{
			_texture.Dispose();
			_device.Dispose();
		}

		private static unsafe void UploadPlane(
			ID3D12Resource upload,
			PlacedSubresourceFootPrint footprint,
			byte[] source,
			int sourceStride,
			int rows)
		{
			void* mapped = null;
			upload.Map(0u, null, &mapped).CheckError();
			try
			{
				nint destination =
					(nint)mapped + checked((nint)footprint.Offset);
				int destinationStride =
					checked((int)footprint.Footprint.RowPitch);
				for (int row = 0; row < rows; row++)
				{
					Marshal.Copy(
						source,
						row * sourceStride,
						destination + row * destinationStride,
						sourceStride);
				}
			}
			finally
			{
				upload.Unmap(0u);
			}
		}
	}
}
