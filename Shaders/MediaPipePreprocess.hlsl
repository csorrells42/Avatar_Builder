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
    float2 uv = CameraChroma.SampleLevel(CameraSampler, uvCoordinate, 0)
        - float2(0.5, 0.5);
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
