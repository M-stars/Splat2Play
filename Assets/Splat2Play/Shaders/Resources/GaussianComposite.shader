// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/Composite"
{
CGINCLUDE
#pragma require compute
#pragma use_dxc
#include "UnityCG.cginc"

struct v2f
{
    float4 vertex : SV_POSITION;
};

v2f vert (uint vtxID : SV_VertexID)
{
    v2f o;
    float2 quadPos = float2(vtxID&1, (vtxID>>1)&1) * 4.0 - 1.0;
	o.vertex = float4(quadPos, 1, 1);
    return o;
}

Texture2D _GaussianSplatRT;
Texture2D _GaussianSplatDepthRT;
Texture2D _S2PSceneDepthTexture;
SamplerState sampler_LinearClamp;

float4x4 _SplatInvViewProjection;
float2 _SplatRenderSize;
float4 _CameraTargetTexture_TexelSize;
int _S2PHasSplatDepth;

// —— S2P M3:近似动态光照(手电筒) ——
int _S2PLightEnabled; // 0=Off, 1=ShadowAndNormal, 2=NormalOnly_PathC
float3 _S2PLightPos;
float3 _S2PLightDir;
float3 _S2PLightColor;
float _S2PLightRange;
float _S2PLightCos;    // cos(半锥角)
int _S2PLightSlice;    // 附加光阴影切片索引
float _S2PBias;
float _S2PIntensity;
float _S2PAmbient;
float _S2PNormalStrength;
float _S2PModulationRadius;
int _S2PChunkBiasCount;
float4 _S2PChunkMinBias[8]; // xyz=min, w=biasOffset
float4 _S2PChunkMax[8];     // xyz=max
float4x4 _AdditionalLightsWorldToShadow[256];
Texture2D _AdditionalLightsShadowmapTexture;

// —— S2P M7:额外动态光源(逐像素回退路径;逐高斯路径启用时为 0,避免双重光照)——
float4 _S2PGpuLightA[8];  // xyz=worldPos, w=1/range²
float4 _S2PGpuLightB[8];  // xyz=forward, w=cos(半锥角);<-0.999 为点光源
float4 _S2PGpuLightC[8];  // rgb=linearColor*intensity, w=shininess
int _S2PDynamicLightCount;
float _S2PPerSplatLightStrength;
float _S2PSplatNormalWeight;

float SampleS2PDepth(int2 xy)
{
    int3 px = int3(xy, 0);
    float d = _S2PHasSplatDepth != 0 ? _GaussianSplatDepthRT.Load(px).r : 0.0;
    if (d <= 1e-6)
        d = _S2PSceneDepthTexture.Load(px).r;
    return d;
}

float3 ReconstructS2PWorld(float2 pixelXY, float depth, float2 size, float projectionY)
{
    float2 clipXY = pixelXY / size * 2.0 - 1.0;
    clipXY.y *= projectionY;
    float4 world4 = mul(_SplatInvViewProjection, float4(clipXY, depth, 1.0));
    return world4.xyz / max(abs(world4.w), 1e-6);
}

half4 frag (v2f i) : SV_Target
{
    half4 col = _GaussianSplatRT.Load(int3(i.vertex.xy, 0));
    col.rgb = GammaToLinearSpace(col.a != 0 ? col.rgb/col.a : col.rgb);
    if (_S2PLightEnabled != 0 && col.a > 0.001)
    {
        int2 pxi = int2(i.vertex.xy);
        float depth = SampleS2PDepth(pxi);
        if (depth > 1e-6)
        {
            float2 size = max(_SplatRenderSize.xy, float2(1.0, 1.0));
            float projectionY = _CameraTargetTexture_TexelSize.z == 1.0 ? 1.0 : -1.0;
            float3 world = ReconstructS2PWorld(i.vertex.xy, depth, size, projectionY);
            float3 toPixel = world - _S2PLightPos;
            float dist = length(toPixel);
            float maxRad = _S2PModulationRadius > 0.0 ? _S2PModulationRadius : 35.0;
            if (dist <= maxRad)
            {
                float3 ldir = dist > 1e-4 ? toPixel / dist : _S2PLightDir;
                float cone = smoothstep(_S2PLightCos, _S2PLightCos + 0.08, dot(ldir, _S2PLightDir));
                float atten = saturate(1.0 - dist / max(_S2PLightRange, 1.0));
                atten *= atten;

                // 屏幕空间深度差分重建代理法线(§5.3;M3 手电筒调制与 M7 附加光回退路径共用)
                float3 ssNormal = float3(0.0, 1.0, 0.0);
                bool hasSSNormal = false;
                float nlFactor = 1.0;
                if (_S2PNormalStrength > 0.001 || (_S2PDynamicLightCount > 0 && _S2PPerSplatLightStrength < 0.001))
                {
                    float dRight = SampleS2PDepth(pxi + int2(1, 0));
                    float dUp    = SampleS2PDepth(pxi + int2(0, 1));
                    if (dRight > 1e-6 && dUp > 1e-6 && abs(dRight - depth) < 0.02 && abs(dUp - depth) < 0.02)
                    {
                        float3 wRight = ReconstructS2PWorld(i.vertex.xy + float2(1.0, 0.0), dRight, size, projectionY);
                        float3 wUp    = ReconstructS2PWorld(i.vertex.xy + float2(0.0, 1.0), dUp,    size, projectionY);
                        ssNormal = normalize(cross(wUp - world, wRight - world));
                        hasSSNormal = true;
                        float ndotl = abs(dot(ssNormal, -ldir));
                        nlFactor = lerp(1.0, 0.45 + 0.55 * ndotl, _S2PNormalStrength);
                    }
                }

                float lit = 1.0;
                if (_S2PLightEnabled == 1) // ShadowAndNormal (路径 A/B)
                {
                    float effectiveBias = _S2PBias;
                    [unroll]
                    for (int cb = 0; cb < 8; ++cb)
                    {
                        if (cb >= _S2PChunkBiasCount) break;
                        if (all(world >= _S2PChunkMinBias[cb].xyz) && all(world <= _S2PChunkMax[cb].xyz))
                        {
                            effectiveBias += _S2PChunkMinBias[cb].w;
                            break;
                        }
                    }

                    float4 shadowCoord = mul(_AdditionalLightsWorldToShadow[_S2PLightSlice], float4(world, 1.0));
                    if (shadowCoord.w > 1e-4)
                    {
                        float3 sc = shadowCoord.xyz / shadowCoord.w;
                        if (all(sc.xy > 0.001) && all(sc.xy < 0.999) && sc.z > 0.0 && sc.z < 1.0)
                        {
                            float mapZ = _AdditionalLightsShadowmapTexture.SampleLevel(sampler_LinearClamp, sc.xy, 0).r;
                            // Reversed-Z: 遮挡物离光源更近则 mapZ > sc.z
                            if (mapZ > 0.0)
                                lit = saturate((sc.z - mapZ + effectiveBias) / max(abs(effectiveBias), 1e-4));
                        }
                    }
                }

                float factor = cone * atten * lit * nlFactor * _S2PIntensity;
                float3 lightTint = any(_S2PLightColor > 0.0) ? _S2PLightColor : float3(1.0, 0.96, 0.9);
                float ambient = _S2PAmbient > 0.0 ? _S2PAmbient : 0.35;
                col.rgb = col.rgb * lerp(ambient, 1.0, saturate(cone * atten * lit)) + col.rgb * lightTint * factor;

                // —— S2P M7:额外动态光源逐像素回退路径(逐高斯路径关闭时生效,与手电筒协同)——
                if (_S2PDynamicLightCount > 0 && _S2PPerSplatLightStrength < 0.001 && hasSSNormal)
                {
                    float3 addLight = 0.0;
                    [loop]
                    for (int li = 0; li < 8; ++li)
                    {
                        if (li >= _S2PDynamicLightCount) break;
                        float3 lVec = _S2PGpuLightA[li].xyz - world;
                        float distSq = dot(lVec, lVec);
                        float dist = sqrt(distSq);
                        if (dist < 1e-5) continue;
                        float3 L = lVec / dist;
                        float tAtten = saturate(1.0 - distSq * _S2PGpuLightA[li].w);
                        float lAtten = tAtten * tAtten;
                        float cosHalf = _S2PGpuLightB[li].w;
                        float spotFactor = cosHalf > -0.999
                            ? smoothstep(cosHalf, cosHalf + 0.08, dot(L, _S2PGpuLightB[li].xyz))
                            : 1.0;
                        float ndl = max(abs(dot(ssNormal, L)), 0.0);
                        addLight += _S2PGpuLightC[li].rgb * (ndl * lAtten * spotFactor);
                    }
                    col.rgb += col.rgb * addLight;
                }
            }
        }
    }
    return float4(col.rgb, col.a);
}
ENDCG
    SubShader
    {
        ZWrite Off
        ZTest Always
        Cull Off

        // Composite splats
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
ENDCG
        }

        // Stochastic temporal: average premultiplied splat color and coverage,
        // with optional reprojection
        Pass
        {
            Blend Off
CGPROGRAM
#pragma vertex vert
#pragma fragment fragAccumulate
#pragma multi_compile_local __ SPLAT_REPROJECTED
Texture2D _SplatHistory;

#if defined(SPLAT_REPROJECTED)
SamplerState linear_clamp_sampler;
float4x4 _SplatPreviousViewProjection;
float3 _SplatCameraPosition;
uint _SplatOrthographic;

// use YCoCg for history rejection, similar to SteadySplats
float4 HistoryColor(float4 color)
{
    return float4(dot(color.rgb, float3(0.25, 0.5, 0.25)),
        (color.r - color.b) * 0.5, dot(color.rgb, float3(-0.25, 0.5, -0.25)), color.a);
}

half4 ReprojectHistory(float2 pixel, half4 current)
{
    uint width = (uint)_SplatRenderSize.x, height = (uint)_SplatRenderSize.y;
    float2 size = float2(width, height);
    float2 clipXY = pixel / size * 2.0 - 1.0;
    // match FlipProjectionIfBackbuffer in the splat vertex shader: BiRP
    // direct-to-backbuffer path flips the supplied GPU projection once more
    float projectionY = _CameraTargetTexture_TexelSize.z == 1.0 ? 1.0 : -1.0;
    clipXY.y *= projectionY;

    float depth = _GaussianSplatDepthRT.Load(int3(pixel, 0)).r;
    float4 world;
    if (current.a == 0)
    {
        // we are in the background pixel; there's no depth.
        // for perspective case, reproject rays at infinity;
        // skip reprojection for orthographic background pixels.
        if (_SplatOrthographic != 0)
            return current;
        world = mul(_SplatInvViewProjection, float4(clipXY, 0.5, 1));
        world.xyz -= _SplatCameraPosition * world.w;
        world.w = 0;
    }
    else
        world = mul(_SplatInvViewProjection, float4(clipXY, depth, 1));

    float4 previousClip = mul(_SplatPreviousViewProjection, world);
    if (previousClip.w <= 0)
        return current; // no usable history for perspective samples behind old camera
    float2 uv = previousClip.xy / previousClip.w;
    uv.y *= projectionY;
    uv = uv * 0.5 + 0.5;

    // keep bilinear footprint inside history instead of clamping edge texels
    if (any(uv < 0.5 / size) || any(uv > 1.0 - 0.5 / size))
        return current;

    half4 history = _SplatHistory.SampleLevel(linear_clamp_sampler, uv, 0);

    // motion adaptive soft history rejection similar to SteadySplats appendix 8
    // https://arxiv.org/html/2610.05576v2
    // we don't do spatial resampling bit though
    float4 mean = 0, secondMoment = 0;
    for (int y = -1; y <= 1; ++y)
    {
        for (int x = -1; x <= 1; ++x)
        {
            int2 p = clamp(int2(pixel) + int2(x, y), int2(0, 0), int2(width, height) - 1);
            float4 color = HistoryColor(_GaussianSplatRT.Load(int3(p, 0)));
            mean += color;
            secondMoment += color * color;
        }
    }
    mean /= 9.0;
    float4 deviation = sqrt(max(0, secondMoment / 9.0 - mean * mean));
    float velocity = length(uv * size - pixel);

    // confidence width from paper: 4 stddev at zero pixel motion, reduced to 1 at >=4 pixels of motion
    float gamma = lerp(4.0, 1.0, saturate(velocity / 4.0));
    float4 extent = max(gamma * deviation, float4(0.01, 0.005, 0.005, 0.01));
    float4 distance = abs(HistoryColor(history) - mean) / extent;
    float mismatch = max(max(distance.x, distance.y), max(distance.z, distance.w));
    float retention = 0.7 / max(1.0, mismatch);
    return lerp(current, history, retention);
}
#else
float _SplatFrameWeight;
#endif

half4 fragAccumulate(v2f i) : SV_Target
{
    half4 current = _GaussianSplatRT.Load(int3(i.vertex.xy, 0));
#if defined(SPLAT_REPROJECTED)
    return ReprojectHistory(i.vertex.xy, current);
#else
    // at reset weight=1, just copy without reading uninitialized history
    if (_SplatFrameWeight == 1.0)
        return current;
    half4 history = _SplatHistory.Load(int3(i.vertex.xy, 0));
    return lerp(history, current, _SplatFrameWeight);
#endif
}
ENDCG
        }
    }
}
