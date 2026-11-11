// Splat2Play — M5(方向 5)T5.1:统一物理量读取器
// PLY(数百 MB)与 SPZ(体积小 ~10×)按文件头魔数自动路由,
// 输出统一的 NativeArray<SplatRaw>(Position/Scale/归一化 Rotation/Opacity),
// 供 M1 碰撞代理烘焙管线直接消费。SPZ 解码数学严格对齐
// GaussianSplatPrepareDataJob.DecodeSpz:
//   Position: v1 = half3;v2–v4 = 24-bit 定点(符号扩展 × 1/2^fractionalBits)
//   Opacity:  byte/255 —— SPZ 中已是激活后的 [0,1],不得再做 Sigmoid!
//   Scale:    σ = exp(byte/16 - 10)
//   Rotation: v1–v2 = xyz/127.5-1 + w=sqrt(max(0,1-|xyz|²));v3–v4 = smallest-three 打包
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using GaussianSplatting.Runtime;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Splat2Play.Runtime
{
    /// <summary>扫描资产来源格式。</summary>
    public enum SplatSourceKind { Unknown = 0, Ply = 1, Spz = 2 }

    /// <summary>
    /// 统一入口:任何受支持的 3DGS 资产流 → SplatRaw 数组。
    /// 返回的 NativeArray 由调用方负责 Dispose。
    /// </summary>
    public static class SplatSourceReader
    {
        /// <summary>嗅探流类型:PLY("ply")/ SPZ("NGSP" 或 gzip 魔数)。失败返回 Unknown。</summary>
        public static SplatSourceKind DetectKind(Stream stream)
        {
            long start = stream.Position;
            try
            {
                int a = stream.ReadByte(), b = stream.ReadByte();
                if (a == 'p' && b == 'l') return SplatSourceKind.Ply;
                if ((a == 'N' && b == 'G') || (a == 0x1f && b == 0x8b)) return SplatSourceKind.Spz;
                return SplatSourceKind.Unknown;
            }
            finally { stream.Position = start; }
        }

        /// <summary>
        /// 读取全部 splat 并转换为物理量。返回数组由调用方 Dispose;
        /// splatCount = splat 数,sourceBytes = 原始顶点/属性字节数(诊断用)。
        /// </summary>
        public static NativeArray<SplatRaw> ReadAll(Stream stream, out int splatCount,
                                                    out SplatSourceKind kind, out long sourceBytes)
        {
            kind = DetectKind(stream);
            switch (kind)
            {
                case SplatSourceKind.Ply:
                {
                    var header = PlySplatReader.ReadHeader(stream);
                    if (header.SplatCount == 0) throw new InvalidDataException("PLY 中没有 splat");
                    splatCount = header.SplatCount;
                    var splats = PlySplatReader.ReadSplats(stream, header, out long vertexBytes);
                    sourceBytes = vertexBytes;
                    return splats;
                }
                case SplatSourceKind.Spz:
                {
                    using var spz = GaussianSplatRawExtractor.Read(stream);
                    if (spz.SplatCount == 0) throw new InvalidDataException("SPZ 中没有 splat");
                    splatCount = spz.SplatCount;
                    sourceBytes = spz.Data.Length;
                    return DecodeSpzToSplatRaw(spz);
                }
                default:
                    throw new InvalidDataException("无法识别的扫描资产格式(期望 binary PLY 或 SPZ)");
            }
        }

        /// <summary>便捷入口:从文件路径读取(自动按内容路由,与扩展名无关)。</summary>
        public static NativeArray<SplatRaw> ReadAll(string path, out int splatCount,
                                                    out SplatSourceKind kind, out long sourceBytes)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            return ReadAll(fs, out splatCount, out kind, out sourceBytes);
        }

        /// <summary>
        /// M8:读取全部 splat(含 f_dc 基色 SH0 系数),供动态物体剥离与底面修补往返 PLY。
        /// PLY 直接读 f_dc;SPZ 由颜色流解码:fdc = SH0ToColor⁻¹((c/255-0.5)/0.15 过 SH0)。
        /// </summary>
        public static NativeArray<SplatRawX> ReadAllX(Stream stream, out int splatCount,
                                                      out SplatSourceKind kind, out long sourceBytes)
        {
            kind = DetectKind(stream);
            switch (kind)
            {
                case SplatSourceKind.Ply:
                {
                    var header = PlySplatReader.ReadHeader(stream);
                    if (header.SplatCount == 0) throw new InvalidDataException("PLY 中没有 splat");
                    splatCount = header.SplatCount;
                    var splats = PlySplatReader.ReadSplatsX(stream, header, out long vertexBytes);
                    sourceBytes = vertexBytes;
                    return splats;
                }
                case SplatSourceKind.Spz:
                {
                    using var spz = GaussianSplatRawExtractor.Read(stream);
                    if (spz.SplatCount == 0) throw new InvalidDataException("SPZ 中没有 splat");
                    splatCount = spz.SplatCount;
                    sourceBytes = spz.Data.Length;
                    return DecodeSpzToSplatRawX(spz);
                }
                default:
                    throw new InvalidDataException("无法识别的扫描资产格式(期望 binary PLY 或 SPZ)");
            }
        }

        /// <summary>便捷入口(文件路径版,含颜色)。</summary>
        public static NativeArray<SplatRawX> ReadAllX(string path, out int splatCount,
                                                      out SplatSourceKind kind, out long sourceBytes)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            return ReadAllX(fs, out splatCount, out kind, out sourceBytes);
        }

        /// <summary>SPZ 解压属性流 → SplatRawX(含 f_dc 基色,Burst 并行,确定性)。</summary>
        static NativeArray<SplatRawX> DecodeSpzToSplatRawX(in SpzRawData spz)
        {
            var result = new NativeArray<SplatRawX>(spz.SplatCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            bool ok = false;
            try
            {
                new SpzToSplatRawXJob
                {
                    Input = spz.Data,
                    SplatCount = spz.SplatCount,
                    Version = spz.Version,
                    FractionalBits = spz.FractionalBits,
                    Out = result
                }.Schedule(spz.SplatCount, 4096).Complete();
                ok = true;
                return result;
            }
            finally
            {
                if (!ok) result.Dispose();
            }
        }

        /// <summary>SPZ 解压属性流 → SplatRaw(Burst 并行,确定性)。</summary>
        static NativeArray<SplatRaw> DecodeSpzToSplatRaw(in SpzRawData spz)
        {
            var result = new NativeArray<SplatRaw>(spz.SplatCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            bool ok = false;
            try
            {
                new SpzToSplatRawJob
                {
                    Input = spz.Data,
                    SplatCount = spz.SplatCount,
                    Version = spz.Version,
                    FractionalBits = spz.FractionalBits,
                    ShCoefficients = spz.ShCoefficients,
                    Out = result
                }.Schedule(spz.SplatCount, 4096).Complete();
                ok = true;
                return result;
            }
            finally
            {
                if (!ok) result.Dispose();
            }
        }

        /// <summary>
        /// 单 splat SPZ 解码(与 DecodeSpz 逐条对齐)。
        /// 布局:[positions 6|9N][alpha N][colors 3N][scale 3N][rot 3|4N][SH 3·sh·N]。
        /// </summary>
        [BurstCompile(FloatMode = FloatMode.Fast)]
        unsafe struct SpzToSplatRawJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> Input;
            public int SplatCount;
            public int Version;          // 1–4
            public int FractionalBits;   // v2–v4 定点小数位
            public int ShCoefficients;   // 仅用于校验布局(解码不需要 SH)
            [NativeDisableParallelForRestriction] public NativeArray<SplatRaw> Out;

            public void Execute(int i)
            {
                int positionBytes = Version == 1 ? 6 : 9;
                byte* basePtr = (byte*)Input.GetUnsafeReadOnlyPtr();

                // —— Position ——
                byte* p = basePtr + i * positionBytes;
                float3 position;
                if (Version == 1)
                {
                    position = new float3(
                        math.f16tof32(*(ushort*)p),
                        math.f16tof32(*(ushort*)(p + 2)),
                        math.f16tof32(*(ushort*)(p + 4)));
                }
                else
                {
                    float fraction = math.rcp(1 << FractionalBits);
                    position = new float3(Fixed24(p, fraction), Fixed24(p + 3, fraction), Fixed24(p + 6, fraction));
                }

                // —— Opacity(已是激活后的 alpha,不做 Sigmoid)——
                byte* alpha = basePtr + (long)SplatCount * positionBytes;
                float opacity = alpha[i] / 255f;

                // —— Scale ——
                byte* scl = alpha + (long)SplatCount * 4 + i * 3;
                float3 scale = math.exp(new float3(scl[0], scl[1], scl[2]) / 16f - 10f);

                // —— Rotation(统一转换为 xyzw 单位四元数)——
                int rotationBytes = Version < 3 ? 3 : 4;
                byte* rot = alpha + (long)SplatCount * 7 + i * rotationBytes;
                float4 rotation = float4.zero;
                if (Version < 3)
                {
                    rotation.xyz = new float3(rot[0], rot[1], rot[2]) / 127.5f - 1f;
                    rotation.w = math.sqrt(math.max(0f, 1f - math.lengthsq(rotation.xyz)));
                }
                else
                {
                    uint packed = rot[0] | ((uint)rot[1] << 8) | ((uint)rot[2] << 16) | ((uint)rot[3] << 24);
                    rotation = DecodeSmallestThree(packed);
                }
                float len = math.length(rotation);
                rotation = len > 1e-8f ? rotation / len : new float4(0f, 0f, 0f, 1f);

                Out[i] = new SplatRaw
                {
                    Position = position,
                    Scale = scale,
                    Rotation = rotation,
                    Opacity = opacity
                };
            }

            static float Fixed24(byte* p, float scale)
            {
                int v = p[0] | (p[1] << 8) | (p[2] << 16);
                return ((v << 8) >> 8) * scale; // 24-bit 符号扩展到 int32
            }

            /// <summary>SPZ v3/v4 smallest-three 四元数解包(与 GaussianSplatting.hlsl 的 HLSL 版逐位对齐)。</summary>
            static float4 DecodeSmallestThree(uint packed)
            {
                uint index = packed >> 30;
                uint3 components = new uint3(packed >> 20, packed >> 10, packed) & 1023;
                float4 q = float4.zero;
                q.xyz = (float3)(components & 511) * (0.7071067811865475f / 511f);
                q.xyz *= math.select(new float3(1f), new float3(-1f), (components & 512) != 0);
                q.w = math.sqrt(math.max(0f, 1f - math.dot(q.xyz, q.xyz)));
                if (index == 0) q = q.wxyz;
                if (index == 1) q = q.xwyz;
                if (index == 2) q = q.xywz;
                return q;
            }
        }

        /// <summary>带 f_dc 基色的 SPZ 解码(M8):布局与 SpzToSplatRawJob 一致,另解颜色流。</summary>
        [BurstCompile(FloatMode = FloatMode.Fast)]
        unsafe struct SpzToSplatRawXJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> Input;
            public int SplatCount;
            public int Version;
            public int FractionalBits;
            [NativeDisableParallelForRestriction] public NativeArray<SplatRawX> Out;

            public void Execute(int i)
            {
                int positionBytes = Version == 1 ? 6 : 9;
                byte* basePtr = (byte*)Input.GetUnsafeReadOnlyPtr();

                byte* p = basePtr + i * positionBytes;
                float3 position;
                if (Version == 1)
                {
                    position = new float3(
                        math.f16tof32(*(ushort*)p),
                        math.f16tof32(*(ushort*)(p + 2)),
                        math.f16tof32(*(ushort*)(p + 4)));
                }
                else
                {
                    float fraction = math.rcp(1 << FractionalBits);
                    position = new float3(Fixed24(p, fraction), Fixed24(p + 3, fraction), Fixed24(p + 6, fraction));
                }

                byte* alpha = basePtr + (long)SplatCount * positionBytes;
                float opacity = alpha[i] / 255f;

                // 颜色流:c ∈ [0,255] → ((c/255-0.5)/0.15) 过 SH0(×0.2820948+0.5)——此处保留
                // SH0 系数形式(f_dc),与 PLY 的 f_dc 语义一致供往返写盘
                byte* col = alpha + (long)SplatCount + i * 3;
                float3 fdc = ((new float3(col[0], col[1], col[2]) / 255f - 0.5f) / 0.15f);

                byte* scl = alpha + (long)SplatCount * 4 + i * 3;
                float3 scale = math.exp(new float3(scl[0], scl[1], scl[2]) / 16f - 10f);

                int rotationBytes = Version < 3 ? 3 : 4;
                byte* rot = alpha + (long)SplatCount * 7 + i * rotationBytes;
                float4 rotation = float4.zero;
                if (Version < 3)
                {
                    rotation.xyz = new float3(rot[0], rot[1], rot[2]) / 127.5f - 1f;
                    rotation.w = math.sqrt(math.max(0f, 1f - math.lengthsq(rotation.xyz)));
                }
                else
                {
                    uint packed = rot[0] | ((uint)rot[1] << 8) | ((uint)rot[2] << 16) | ((uint)rot[3] << 24);
                    rotation = DecodeSmallestThree(packed);
                }
                float len = math.length(rotation);
                rotation = len > 1e-8f ? rotation / len : new float4(0f, 0f, 0f, 1f);

                Out[i] = new SplatRawX
                {
                    Position = position,
                    Scale = scale,
                    Rotation = rotation,
                    Opacity = opacity,
                    Fdc = fdc
                };
            }

            static float Fixed24(byte* p, float scale)
            {
                int v = p[0] | (p[1] << 8) | (p[2] << 16);
                return ((v << 8) >> 8) * scale;
            }

            static float4 DecodeSmallestThree(uint packed)
            {
                uint index = packed >> 30;
                uint3 components = new uint3(packed >> 20, packed >> 10, packed) & 1023;
                float4 q = float4.zero;
                q.xyz = (float3)(components & 511) * (0.7071067811865475f / 511f);
                q.xyz *= math.select(new float3(1f), new float3(-1f), (components & 512) != 0);
                q.w = math.sqrt(math.max(0f, 1f - math.dot(q.xyz, q.xyz)));
                if (index == 0) q = q.wxyz;
                if (index == 1) q = q.xwyz;
                if (index == 2) q = q.xywz;
                return q;
            }
        }
    }
}
