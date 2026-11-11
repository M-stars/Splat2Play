// SPDX-License-Identifier: MIT
// M6(方向 1)T6.1:Morton 码(Z-Order Curve)空间重排 + GPU Chunk 构建。
// 把空间相邻的高斯在显存缓冲区中排到连续位置(提升 GPU L2 缓存命中率),
// 并按 kChunkSize=4096 划分 SplatGpuChunk,预计算每个 Chunk 的紧凑局部 AABB。
//
// 位置说明:规格书原计划把本文件放在 Assets/Splat2Play/Runtime/,但它必须被
// GaussianSplatRenderer.CreateResources(本程序集)直接调用,而程序集依赖方向是
// Splat2Play.Runtime → GaussianSplatting,故随 CreateResources 一并落在第三方程序集内,
// 通过 public API 暴露给 Splat2Play 层(单元测试与性能面板)。

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>Morton 分块常量:每 Chunk 4096 个连续 splat(索引 >> 12 即 Chunk 号)。</summary>
    public static class SplatGpuChunking
    {
        public const int kChunkSize = 4096;

        /// <summary>GPU 端 Chunk 结构(32 字节,与 S2PGpuCulling.compute 中的 HLSL 定义一致)。</summary>
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct GpuChunk
        {
            public float3 boundsMin;
            public uint startSplatIdx;
            public float3 boundsMax;
            public uint splatCount;
        }
    }

    /// <summary>30-bit 3D Morton 码(每轴 10 bit)。</summary>
    public static class SplatMorton
    {
        public static uint ExpandBits10(uint v)
        {
            v = (v * 0x00010001u) & 0xFF0000FFu;
            v = (v * 0x00000101u) & 0x0F00F00Fu;
            v = (v * 0x00000011u) & 0xC30C30C3u;
            v = (v * 0x00000005u) & 0x49249249u;
            return v;
        }

        /// <summary>normPos ∈ [0,1]³ → 30-bit Morton 码(xyz 交织)。</summary>
        public static uint Morton3D(float3 normPos)
        {
            uint x = (uint)math.clamp(normPos.x * 1023f, 0f, 1023f);
            uint y = (uint)math.clamp(normPos.y * 1023f, 0f, 1023f);
            uint z = (uint)math.clamp(normPos.z * 1023f, 0f, 1023f);
            return (ExpandBits10(x) << 2) | (ExpandBits10(y) << 1) | ExpandBits10(z);
        }
    }

    /// <summary>
    /// 加载完成后的数据重排编排器:
    /// Morton 键计算 → 4 趟 8-bit LSD 稳定基数排序(Burst,确定性)→ 四数组双射置换 → 分块 AABB。
    /// 重排是纯双射,不改变渲染语义(逐帧排序与混合均与存储顺序无关)。
    /// </summary>
    public static class SplatMortonReorder
    {
        /// <summary>
        /// 就地重排 data 的 positions/geometry/colors/sh 并构建分块。
        /// 返回每个 Chunk 的局部 AABB(长度 = ceil(count/4096));失败时返回 null 且不改动数据。
        /// </summary>
        public static Bounds[] ReorderAndBuildChunks(GaussianSplatData data)
        {
            int count = data.count;
            if (count <= 0) return null;

            // 归一化:零尺寸(全部同点)时用极小分母避免除零
            float3 boundsMin = data.bounds.min;
            float3 invSpan = 1f / math.max(data.bounds.size, new float3(1e-6f));

            var keys = new NativeArray<uint>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var order = new NativeArray<int>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var scratchOrder = new NativeArray<int>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var histogram = new NativeArray<int>(256, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            try
            {
                new MortonKeyJob { BoundsMin = boundsMin, InvSpan = invSpan, Positions = data.positions, Keys = keys }
                    .Schedule(count, 8192).Complete();

                new RadixSortJob
                    { Keys = keys, Order = order, ScratchOrder = scratchOrder, Histogram = histogram }
                    .Schedule().Complete();

                // 双射置换四个数组
                int shBytesPerSplat = GaussianUtils.GetSHDataSize(data.shCoefficients);
                var newPositions = new NativeArray<uint>(count * 3, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                var newGeometry = new NativeArray<uint>(count * 3, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                var newColors = new NativeArray<uint>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                var newSh = new NativeArray<uint>(
                    data.shCoefficients == 0 ? 1 : count * (shBytesPerSplat / 4),
                    Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                try
                {
                    new PermuteJob
                    {
                        Order = order,
                        SrcPositions = data.positions, SrcGeometry = data.geometry, SrcColors = data.colors,
                        DstPositions = newPositions, DstGeometry = newGeometry, DstColors = newColors
                    }.Schedule(count, 4096).Complete();
                    if (data.shCoefficients > 0)
                    {
                        new PermuteShJob
                        {
                            Order = order, SrcSh = data.sh, DstSh = newSh,
                            WordsPerSplat = shBytesPerSplat / 4
                        }.Schedule(count, 4096).Complete();
                    }
                    else
                    {
                        newSh[0] = 0;
                    }

                    // 交换进 data(旧数组释放)
                    data.positions.Dispose(); data.positions = newPositions;
                    data.geometry.Dispose(); data.geometry = newGeometry;
                    data.colors.Dispose(); data.colors = newColors;
                    data.sh.Dispose(); data.sh = newSh;
                }
                catch
                {
                    newPositions.Dispose(); newGeometry.Dispose(); newColors.Dispose(); newSh.Dispose();
                    throw;
                }

                // 分块 AABB(局部坐标)
                int chunkCount = (count + SplatGpuChunking.kChunkSize - 1) / SplatGpuChunking.kChunkSize;
                var chunkMm = new NativeArray<float3>(chunkCount * 2, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                try
                {
                    new ChunkBoundsJob
                    {
                        Positions = data.positions, SplatCount = count,
                        ChunkSize = SplatGpuChunking.kChunkSize, MinMax = chunkMm
                    }.Schedule(chunkCount, 1).Complete();

                    var result = new Bounds[chunkCount];
                    for (int c = 0; c < chunkCount; ++c)
                    {
                        float3 mn = chunkMm[c * 2], mx = chunkMm[c * 2 + 1];
                        var center = (Vector3)((mn + mx) * 0.5f);
                        var size = (Vector3)math.max(mx - mn, new float3(1e-4f));
                        result[c] = new Bounds(center, size);
                    }
                    return result;
                }
                finally { chunkMm.Dispose(); }
            }
            finally
            {
                keys.Dispose(); order.Dispose(); scratchOrder.Dispose(); histogram.Dispose();
            }
        }
    }

    /// <summary>逐 splat 计算 30-bit Morton 键(位置归一化到 [0,1]³)。</summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    struct MortonKeyJob : IJobParallelFor
    {
        public float3 BoundsMin;
        public float3 InvSpan;
        [ReadOnly] public NativeArray<uint> Positions;
        [WriteOnly] public NativeArray<uint> Keys;

        public void Execute(int i)
        {
            float3 p = new float3(
                math.asfloat(Positions[i * 3]), math.asfloat(Positions[i * 3 + 1]), math.asfloat(Positions[i * 3 + 2]));
            Keys[i] = SplatMorton.Morton3D((p - BoundsMin) * InvSpan);
        }
    }

    /// <summary>
    /// 4 趟 8-bit LSD 稳定基数排序(单线程 Burst,构造性确定)。
    /// 输出 Order[k] = 排序位次 k 的原始 splat 索引;键相同的 splat 保持原始相对顺序(分块确定性关键)。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    struct RadixSortJob : IJob
    {
        [ReadOnly] public NativeArray<uint> Keys;
        public NativeArray<int> Order;        // 输入被覆盖为排序结果
        public NativeArray<int> ScratchOrder;
        public NativeArray<int> Histogram;    // 长度 256

        public void Execute()
        {
            int n = Keys.Length;
            for (int i = 0; i < n; ++i) Order[i] = i;
            for (int pass = 0; pass < 4; ++pass)
            {
                int shift = pass * 8;
                for (int b = 0; b < 256; ++b) Histogram[b] = 0;
                for (int i = 0; i < n; ++i)
                    ++Histogram[(int)((Keys[Order[i]] >> shift) & 0xFF)];
                int sum = 0;
                for (int b = 0; b < 256; ++b) { int c = Histogram[b]; Histogram[b] = sum; sum += c; }
                for (int i = 0; i < n; ++i)
                {
                    int idx = Order[i];
                    ScratchOrder[Histogram[(int)((Keys[idx] >> shift) & 0xFF)]++] = idx;
                }
                for (int i = 0; i < n; ++i)
                {
                    int t = Order[i]; Order[i] = ScratchOrder[i]; ScratchOrder[i] = t;
                }
            }
        }
    }

    /// <summary>positions/geometry/colors 双射置换(每 splat 3/3/1 个 uint)。</summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    struct PermuteJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> Order;
        [ReadOnly] public NativeArray<uint> SrcPositions, SrcGeometry, SrcColors;
        [NativeDisableParallelForRestriction] public NativeArray<uint> DstPositions, DstGeometry, DstColors;

        public void Execute(int k)
        {
            int src = Order[k] * 3, dst = k * 3;
            DstPositions[dst] = SrcPositions[src];
            DstPositions[dst + 1] = SrcPositions[src + 1];
            DstPositions[dst + 2] = SrcPositions[src + 2];
            DstGeometry[dst] = SrcGeometry[src];
            DstGeometry[dst + 1] = SrcGeometry[src + 1];
            DstGeometry[dst + 2] = SrcGeometry[src + 2];
            DstColors[k] = SrcColors[Order[k]];
        }
    }

    /// <summary>SH 数据双射置换(每 splat WordsPerSplat 个 uint,打包含 fp16 范围头)。</summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    struct PermuteShJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> Order;
        [ReadOnly] public NativeArray<uint> SrcSh;
        [NativeDisableParallelForRestriction] public NativeArray<uint> DstSh;
        public int WordsPerSplat;

        public void Execute(int k)
        {
            int src = Order[k] * WordsPerSplat, dst = k * WordsPerSplat;
            for (int w = 0; w < WordsPerSplat; ++w)
                DstSh[dst + w] = SrcSh[src + w];
        }
    }

    /// <summary>逐 Chunk 紧凑 AABB(局部坐标;MinMax[c*2]=min,MinMax[c*2+1]=max)。</summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    struct ChunkBoundsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<uint> Positions;
        public int SplatCount;
        public int ChunkSize;
        [NativeDisableParallelForRestriction] public NativeArray<float3> MinMax;

        public void Execute(int c)
        {
            int lo = c * ChunkSize;
            int hi = math.min(lo + ChunkSize, SplatCount);
            float3 mn = new float3(float.PositiveInfinity), mx = new float3(float.NegativeInfinity);
            for (int i = lo; i < hi; ++i)
            {
                float3 p = new float3(
                    math.asfloat(Positions[i * 3]), math.asfloat(Positions[i * 3 + 1]), math.asfloat(Positions[i * 3 + 2]));
                mn = math.min(mn, p);
                mx = math.max(mx, p);
            }
            MinMax[c * 2] = mn;
            MinMax[c * 2 + 1] = mx;
        }
    }
}
