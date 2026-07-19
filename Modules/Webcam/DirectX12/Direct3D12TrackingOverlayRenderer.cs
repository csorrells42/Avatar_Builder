using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace AvatarBuilder.Modules.Webcam.DirectX12;

internal sealed unsafe class Direct3D12TrackingOverlayRenderer : IDisposable
{
    private const int MaxSegments = 4096;
    private static readonly int SegmentStride = Marshal.SizeOf<OverlaySegment>();
    private static readonly int UploadBytes = MaxSegments * SegmentStride;

    private static readonly OverlayColor FaceBoxColor = new(0.96f, 0.83f, 0.37f);
    private static readonly OverlayColor EyeBoxColor = new(0.40f, 0.91f, 0.78f);
    private static readonly OverlayColor MouthBoxColor = new(0.96f, 0.52f, 0.69f);
    private static readonly OverlayColor FaceContourColor = new(0.73f, 0.84f, 0.94f);
    private static readonly OverlayColor EyeContourColor = new(0.48f, 0.85f, 1.00f);
    private static readonly OverlayColor BrowContourColor = new(0.77f, 0.97f, 0.64f);
    private static readonly OverlayColor LipContourColor = new(1.00f, 0.75f, 0.43f);
    private static readonly OverlayColor InferredContourColor = new(0.93f, 0.68f, 0.29f);

    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _pipelineState;
    private readonly OverlayFrameResource[] _frameResources;
    private bool _disposed;

    private Direct3D12TrackingOverlayRenderer(ID3D12Device device, int frameCount)
    {
        var vertexShader = CompileShader("VSMain", "vs_5_0");
        var pixelShader = CompileShader("PSMain", "ps_5_0");
        var parameters = new[]
        {
            new RootParameter(new RootConstants(0, 0, 2), ShaderVisibility.Vertex)
        };
        var rootDescription = new RootSignatureDescription(
            RootSignatureFlags.AllowInputAssemblerInputLayout,
            parameters,
            Array.Empty<StaticSamplerDescription>());
        _rootSignature = device.CreateRootSignature(in rootDescription, RootSignatureVersion.Version1);

        var inputElements = new[]
        {
            new InputElementDescription("SEGMENT", 0, Format.R32G32B32A32_Float, 0, 0, InputClassification.PerInstanceData, 1),
            new InputElementDescription("SEGMENT", 1, Format.R32G32B32A32_Float, 16, 0, InputClassification.PerInstanceData, 1)
        };
        var pipelineDescription = new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature,
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            BlendState = BlendDescription.AlphaBlend,
            RasterizerState = RasterizerDescription.CullNone,
            DepthStencilState = DepthStencilDescription.None,
            InputLayout = new InputLayoutDescription(inputElements),
            SampleMask = uint.MaxValue,
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = [Format.B8G8R8A8_UNorm],
            SampleDescription = new SampleDescription(1, 0)
        };
        _pipelineState = device.CreateGraphicsPipelineState<ID3D12PipelineState>(pipelineDescription);

        _frameResources = new OverlayFrameResource[Math.Max(1, frameCount)];
        for (var index = 0; index < _frameResources.Length; index++)
        {
            _frameResources[index] = new OverlayFrameResource(device, UploadBytes, (uint)SegmentStride);
        }
    }

    public static Direct3D12TrackingOverlayRenderer? TryCreate(ID3D12Device device, int frameCount)
    {
        try
        {
            return new Direct3D12TrackingOverlayRenderer(device, frameCount);
        }
        catch
        {
            return null;
        }
    }

    public void Draw(
        ID3D12GraphicsCommandList commandList,
        int frameIndex,
        int width,
        int height,
        PreviewTrackingOverlay overlay)
    {
        if (_disposed
            || !overlay.HasContent
            || (uint)frameIndex >= (uint)_frameResources.Length
            || width <= 1
            || height <= 1)
        {
            return;
        }

        var segments = (OverlaySegment*)_frameResources[frameIndex].UploadPointer;
        var count = 0;
        AppendRectangle(segments, ref count, overlay.FaceBox, FaceBoxColor, 4f);
        AppendRectangle(segments, ref count, overlay.LeftEyeBox, EyeBoxColor, 3f);
        AppendRectangle(segments, ref count, overlay.RightEyeBox, EyeBoxColor, 3f);
        AppendRectangle(segments, ref count, overlay.MouthBox, MouthBoxColor, 3f);
        AppendPolyline(segments, ref count, overlay.FaceContour, FaceContourColor, 2f);
        AppendPolyline(segments, ref count, overlay.JawContour, FaceContourColor, 3f);
        AppendPolyline(segments, ref count, overlay.LeftEyeContour, EyeContourColor, 4f);
        AppendPolyline(segments, ref count, overlay.RightEyeContour, EyeContourColor, 4f);
        AppendPolyline(segments, ref count, overlay.LeftBrowContour, BrowContourColor, 3f);
        AppendPolyline(segments, ref count, overlay.RightBrowContour, BrowContourColor, 3f);
        AppendPolyline(segments, ref count, overlay.OuterLipContour, LipContourColor, 4f);
        AppendPolyline(segments, ref count, overlay.InnerLipContour, LipContourColor, 3f);
        if (count <= 0)
        {
            return;
        }

        commandList.SetGraphicsRootSignature(_rootSignature);
        commandList.SetPipelineState(_pipelineState);
        commandList.SetGraphicsRoot32BitConstant(0, BitConverter.SingleToUInt32Bits(Math.Max(1f, width * 0.5f)), 0);
        commandList.SetGraphicsRoot32BitConstant(0, BitConverter.SingleToUInt32Bits(Math.Max(1f, height * 0.5f)), 1);
        commandList.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commandList.IASetVertexBuffers(0, [_frameResources[frameIndex].VertexBufferView]);
        commandList.DrawInstanced(6, (uint)count, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var frameResource in _frameResources)
        {
            frameResource.Dispose();
        }

        _pipelineState.Dispose();
        _rootSignature.Dispose();
    }

    private static void AppendRectangle(
        OverlaySegment* destination,
        ref int count,
        PreviewOverlayRect? region,
        OverlayColor color,
        float thickness)
    {
        if (region is not PreviewOverlayRect source
            || !IsFinite(source.Left)
            || !IsFinite(source.Top)
            || !IsFinite(source.Right)
            || !IsFinite(source.Bottom))
        {
            return;
        }

        var normalized = source.Clamp();
        AppendSegment(destination, ref count, normalized.Left, normalized.Top, normalized.Right, normalized.Top, color, thickness);
        AppendSegment(destination, ref count, normalized.Right, normalized.Top, normalized.Right, normalized.Bottom, color, thickness);
        AppendSegment(destination, ref count, normalized.Right, normalized.Bottom, normalized.Left, normalized.Bottom, color, thickness);
        AppendSegment(destination, ref count, normalized.Left, normalized.Bottom, normalized.Left, normalized.Top, color, thickness);
    }

    private static void AppendPolyline(
        OverlaySegment* destination,
        ref int count,
        PreviewOverlayPolyline? polyline,
        OverlayColor normalColor,
        float thickness)
    {
        if (polyline is null || polyline.Points.Count < 2)
        {
            return;
        }

        var color = polyline.Inferred ? InferredContourColor : normalColor;
        for (var index = 1; index < polyline.Points.Count && count < MaxSegments; index++)
        {
            AppendSegment(destination, ref count, polyline.Points[index - 1], polyline.Points[index], color, thickness);
        }

        if (polyline.Closed && count < MaxSegments)
        {
            AppendSegment(destination, ref count, polyline.Points[^1], polyline.Points[0], color, thickness);
        }
    }

    private static void AppendSegment(
        OverlaySegment* destination,
        ref int count,
        PreviewOverlayPoint from,
        PreviewOverlayPoint to,
        OverlayColor color,
        float thickness)
    {
        if (!TryNormalize(from, out var fromX, out var fromY)
            || !TryNormalize(to, out var toX, out var toY))
        {
            return;
        }

        AppendSegment(destination, ref count, fromX, fromY, toX, toY, color, thickness);
    }

    private static void AppendSegment(
        OverlaySegment* destination,
        ref int count,
        double fromX,
        double fromY,
        double toX,
        double toY,
        OverlayColor color,
        float thickness)
    {
        if (count >= MaxSegments)
        {
            return;
        }

        destination[count++] = new OverlaySegment(
            (float)Math.Clamp(fromX, 0d, 1d),
            (float)Math.Clamp(fromY, 0d, 1d),
            (float)Math.Clamp(toX, 0d, 1d),
            (float)Math.Clamp(toY, 0d, 1d),
            Math.Clamp(thickness, 1f, 8f),
            color.R,
            color.G,
            color.B);
    }

    private static bool TryNormalize(PreviewOverlayPoint point, out double x, out double y)
    {
        if (!IsFinite(point.X) || !IsFinite(point.Y))
        {
            x = 0d;
            y = 0d;
            return false;
        }

        x = Math.Clamp(point.X, 0d, 1d);
        y = Math.Clamp(point.Y, 0d, 1d);
        return true;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static byte[] CompileShader(string entryPoint, string profile)
    {
        return Compiler.Compile(
                OverlayShaderSource,
                entryPoint,
                "AvatarBuilderTrackingOverlay.hlsl",
                profile,
                ShaderFlags.OptimizationLevel3,
                EffectFlags.None)
            .ToArray();
    }

    private const string OverlayShaderSource = """
        struct VertexInput
        {
            float4 Segment0 : SEGMENT0;
            float4 Segment1 : SEGMENT1;
        };

        struct VertexOutput
        {
            float4 Position : SV_POSITION;
            float4 Color : COLOR;
            float LineSide : TEXCOORD0;
        };

        cbuffer OverlayViewport : register(b0)
        {
            float2 ViewportHalfSize;
        };

        VertexOutput VSMain(VertexInput input, uint vertexId : SV_VertexID)
        {
            float2 p1 = float2(input.Segment0.x * 2.0 - 1.0, 1.0 - input.Segment0.y * 2.0);
            float2 p2 = float2(input.Segment0.z * 2.0 - 1.0, 1.0 - input.Segment0.w * 2.0);
            float2 viewportHalfSize = max(ViewportHalfSize, float2(1.0, 1.0));
            float2 deltaPixels = (p2 - p1) * viewportHalfSize;
            float lengthPixels = max(length(deltaPixels), 0.001);
            float2 normal = float2(-deltaPixels.y, deltaPixels.x) / lengthPixels;
            float2 offset = normal * max(input.Segment1.x * 0.5, 0.5) / viewportHalfSize;

            float2 position = p1 + offset;
            float lineSide = 1.0;
            if (vertexId == 1 || vertexId == 4)
            {
                position = p1 - offset;
                lineSide = -1.0;
            }
            else if (vertexId == 2 || vertexId == 3)
            {
                position = p2 + offset;
            }
            else if (vertexId == 5)
            {
                position = p2 - offset;
                lineSide = -1.0;
            }

            VertexOutput output;
            output.Position = float4(position, 0.0, 1.0);
            output.Color = float4(input.Segment1.yzw, 0.96);
            output.LineSide = lineSide;
            return output;
        }

        float4 PSMain(VertexOutput input) : SV_TARGET
        {
            float4 color = input.Color;
            color.a *= 1.0 - smoothstep(0.72, 1.0, abs(input.LineSide));
            return color;
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct OverlaySegment
    {
        public OverlaySegment(
            float fromX,
            float fromY,
            float toX,
            float toY,
            float thickness,
            float red,
            float green,
            float blue)
        {
            FromX = fromX;
            FromY = fromY;
            ToX = toX;
            ToY = toY;
            Thickness = thickness;
            Red = red;
            Green = green;
            Blue = blue;
        }

        public readonly float FromX;
        public readonly float FromY;
        public readonly float ToX;
        public readonly float ToY;
        public readonly float Thickness;
        public readonly float Red;
        public readonly float Green;
        public readonly float Blue;
    }

    private readonly record struct OverlayColor(float R, float G, float B);

    private sealed class OverlayFrameResource : IDisposable
    {
        public OverlayFrameResource(ID3D12Device device, int uploadBytes, uint stride)
        {
            UploadBuffer = device.CreateCommittedResource<ID3D12Resource>(
                new HeapProperties(HeapType.Upload),
                HeapFlags.None,
                ResourceDescription.Buffer((ulong)uploadBytes, ResourceFlags.None, 0),
                ResourceStates.GenericRead);
            void* mapped = null;
            UploadBuffer.Map(0, null, &mapped).CheckError();
            UploadPointer = (IntPtr)mapped;
            VertexBufferView = new VertexBufferView(UploadBuffer.GPUVirtualAddress, (uint)uploadBytes, stride);
        }

        public ID3D12Resource UploadBuffer { get; }

        public IntPtr UploadPointer { get; }

        public VertexBufferView VertexBufferView { get; }

        public void Dispose()
        {
            UploadBuffer.Unmap(0, null);
            UploadBuffer.Dispose();
        }
    }
}
