// Splat2Play — M7(方向 2)T7.1:椭球最短轴法线提取的 C# 等价实现
// 与 GaussianSplatting.hlsl 中 ExtractSplatNormalAndFlatness 逐行对齐,
// 供单元测试验证 HLSL 逻辑(扁平圆盘 → 法线 (0,1,0)、球状 → flatness=0)与 CPU 侧复用。
// SPDX-License-Identifier: MIT

using Unity.Mathematics;

namespace Splat2Play.Runtime
{
    public static class SplatNormalMath
    {
        /// <summary>
        /// 从高斯四元数 rot(xyzw)与尺度 scale 提取世界空间法线与扁平度置信度。
        /// worldNormal 朝向 worldViewDir 一侧(双面一致);flatness ∈ [0,1]。
        /// objToWorldIT 为对象→世界的逆转置矩阵(法线变换),等距变换时传旋转矩阵即可。
        /// </summary>
        public static void ExtractNormalAndFlatness(float4 rot, float3 scale, float3x3 objToWorldIT,
                                                    float3 worldViewDir,
                                                    out float3 worldNormal, out float flatness)
        {
            float3x3 r = CalcMatrixFromRotationScale(rot, new float3(1f, 1f, 1f));
            float3 localAxis;
            float sMin, sMid;
            if (scale.x <= scale.y && scale.x <= scale.z)
            {
                localAxis = r.c0;      // 与 HLSL float3(R._m00, R._m10, R._m20) 同为第 0 列
                sMin = scale.x; sMid = math.min(scale.y, scale.z);
            }
            else if (scale.y <= scale.x && scale.y <= scale.z)
            {
                localAxis = r.c1;
                sMin = scale.y; sMid = math.min(scale.x, scale.z);
            }
            else
            {
                localAxis = r.c2;
                sMin = scale.z; sMid = math.min(scale.x, scale.y);
            }

            worldNormal = math.normalizesafe(math.mul(objToWorldIT, localAxis), new float3(0f, 1f, 0f));
            // 朝向视线侧翻转(双面一致)
            if (math.dot(worldNormal, worldViewDir) < 0f)
                worldNormal = -worldNormal;

            flatness = math.saturate(1f - sMin / math.max(sMid, 1e-5f));
        }

        /// <summary>与 GaussianSplatting.hlsl 的 CalcMatrixFromRotationScale 一致(标量行主序构造,含单位化)。</summary>
        public static float3x3 CalcMatrixFromRotationScale(float4 rot, float3 scale)
        {
            var ms = new float3x3(
                scale.x, 0f, 0f,
                0f, scale.y, 0f,
                0f, 0f, scale.z);
            float x = rot.x, y = rot.y, z = rot.z, w = rot.w;
            var mr = new float3x3(
                1f - 2f * (y * y + z * z), 2f * (x * y - w * z), 2f * (x * z + w * y),
                2f * (x * y + w * z), 1f - 2f * (x * x + z * z), 2f * (y * z - w * x),
                2f * (x * z - w * y), 2f * (y * z + w * x), 1f - 2f * (x * x + y * y));
            return math.mul(mr, ms);
        }
    }
}
