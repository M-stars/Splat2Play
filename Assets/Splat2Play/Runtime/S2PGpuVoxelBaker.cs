// Splat2Play — M5(方向 5)T5.2/T5.3:GPU Compute 烘焙后端驱动
// 在 GPU 上完成 Q16.16 密度场体素化与 3×3×3 SDF 平滑(InterlockedAdd 定点累加 → 确定性),
// 同步读回后继续走 CPU 的 FillJob + SurfaceNetsJob(与现有管线共享同一套确定性后半段)。
// 本类仅做缓冲编组与派发,数学全部在 S2PVoxelize.compute 中,与 CPU 逐位对齐。
// SPDX-License-Identifier: MIT

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Splat2Play.Runtime
{
    public static class S2PGpuVoxelBaker
    {
        /// <summary>当前图形设备是否具备 GPU Compute 烘焙能力。</summary>
        public static bool IsSupported => SystemInfo.supportsComputeShaders;

        /// <summary>最近一次 Voxelize 的纯 GPU 派发耗时(不含上传/读回,供基准对照)。</summary>
        public static long LastDispatchMs { get; private set; } = -1;

        const int kKernelClear = 0;
        const int kKernelVoxelize = 1;
        const int kKernelSdfSmooth = 2;

        /// <summary>上传给 compute 的 splat 布局(48 字节,与 HLSL struct SplatGpuIn 一致)。</summary>
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct SplatGpuIn
        {
            public float3 Position;
            public float3 Scale;
            public float4 Rotation;
            public float Opacity;
            public float Pad;
        }

        /// <summary>上传给 compute 的排除区布局(32 字节,与 HLSL struct AabbGpu 一致)。</summary>
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct AabbGpu
        {
            public Vector4 MinBox;
            public Vector4 MaxBox;
        }

        static ComputeShader LoadShader() => Resources.Load<ComputeShader>("S2PVoxelize");

        /// <summary>
        /// GPU 体素化(+可选 SDF 平滑)并同步读回密度场,输出写入 densityQ16Out。
        /// GPU 不可用时抛 InvalidOperationException(调用方负责回退 CPU)。
        /// 注意:GPU 路径的 SDF 在填充前执行(§2.2 规格流程图:体素化→SDF→读回→填充),
        /// 与 CPU 路径(体素化→填充→SDF)的阶段顺序不同,属规格既定行为。
        /// </summary>
        public static void Voxelize(NativeArray<SplatRaw> splats, NativeArray<AabbRange> exclusions,
                                    in VoxelGridInfo grid, float opacityWeight,
                                    NativeArray<int> densityQ16Out, bool sdfSmooth)
        {
            ComputeShader cs = LoadShader();
            if (cs == null) throw new InvalidOperationException("未找到 S2PVoxelize.compute(Resources)");
            if (!IsSupported) throw new InvalidOperationException("当前设备不支持 Compute Shader");

            int count = grid.Count;
            using var splatBuf = ToGpuSplats(splats);
            using var exclBuf = ToGpuAabbs(exclusions);
            using var densityBuf = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, count, 4);

            SetGridUniforms(cs, grid);

            // 1. 清零(每组 64 线程,组内循环步进覆盖 4096 体素/组)
            cs.SetBuffer(kKernelClear, "_DensityQ16", densityBuf);
            cs.Dispatch(kKernelClear, Mathf.CeilToInt(count / 4096f), 1, 1);

            // 2. 体素化(InterlockedAdd 定点整数累加,与线程执行顺序无关 → 确定性)
            cs.SetInt("_SplatCount", splats.Length);
            cs.SetInt("_ExclusionCount", exclusions.Length);
            cs.SetFloat("_OpacityWeight", opacityWeight);
            cs.SetBuffer(kKernelVoxelize, "_Splats", splatBuf);
            cs.SetBuffer(kKernelVoxelize, "_Exclusions", exclBuf);
            cs.SetBuffer(kKernelVoxelize, "_DensityQ16", densityBuf);
            // 分批派发:单批 GPU 工作量限制在 TDR(~2s)安全范围内,批间以 4B 读回强制排空队列
            // (1.24M splats × 512³ 的体素写入可达百亿级,一次性派发实测触发 D3D12 设备移除)
            var dispatchSw = System.Diagnostics.Stopwatch.StartNew();
            using var flushBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4);
            var flushTmp = new int[1];
            const int kBatch = 100_000; // 单批 GPU 工作量 ~0.5G 体素写入,远低于 TDR 窗口
            int dispatched = 0;
            while (dispatched < splats.Length)
            {
                int batch = math.min(kBatch, splats.Length - dispatched);
                cs.SetInt("_SplatCount", batch);
                cs.SetInt("_SplatDispatchOffset", dispatched);
                cs.Dispatch(kKernelVoxelize, Mathf.CeilToInt(batch / 256f), 1, 1);
                dispatched += batch;
                // 批间同步:1 元素缓冲 GetData 强制排空 GPU 队列,防止单帧累计超过 TDR(~2s)
                flushBuf.SetData(flushTmp);
                flushBuf.GetData(flushTmp);
            }
            dispatchSw.Stop();
            LastDispatchMs = dispatchSw.ElapsedMilliseconds;

            // 3. 可选 SDF 平滑(GPU 双缓冲)后读回
            if (sdfSmooth)
            {
                using var srcBuf = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopyDestination, count, 4);
                using var dstBuf = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, count, 4);
                cs.SetBuffer(kKernelSdfSmooth, "_SdfSrc", srcBuf);
                cs.SetBuffer(kKernelSdfSmooth, "_SdfDst", dstBuf);
                Graphics.CopyBuffer(densityBuf, srcBuf);
                DispatchSdf(cs, grid);
                ReadbackSync(dstBuf, densityQ16Out, count);
            }
            else
            {
                ReadbackSync(densityBuf, densityQ16Out, count);
            }
        }

        /// <summary>
        /// 同步分块读回:512³ 密度场达 512MB,单次异步读回在部分驱动上触发设备移除,
        /// 故按 64MB 分块同步 GetData(编辑器烘焙路径,同步停顿可接受)。
        /// </summary>
        static void ReadbackSync(GraphicsBuffer buffer, NativeArray<int> target, int count)
        {
            const int kChunkElements = 16 * 1024 * 1024; // 64MB
            var chunk = new int[math.min(kChunkElements, count)];
            try
            {
                for (int offset = 0; offset < count; offset += chunk.Length)
                {
                    int elements = math.min(chunk.Length, count - offset);
                    buffer.GetData(chunk, 0, offset, elements);
                    NativeArray<int>.Copy(chunk, 0, target, offset, elements);
                }
            }
            finally { }
        }

        /// <summary>
        /// GPU 3×3×3 [1,2,1]³/64 SDF 平滑(就地覆盖输入密度场)。
        /// 纯整数位移运算,与 CPU SdfSmoothJob 位精确一致(独立暴露供单元测试比对)。
        /// </summary>
        public static void SdfSmooth(in VoxelGridInfo grid, NativeArray<int> densityQ16)
        {
            ComputeShader cs = LoadShader();
            if (cs == null) throw new InvalidOperationException("未找到 S2PVoxelize.compute(Resources)");
            if (!IsSupported) throw new InvalidOperationException("当前设备不支持 Compute Shader");

            int count = grid.Count;
            SetGridUniforms(cs, grid);
            using var srcBuf = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopyDestination | GraphicsBuffer.Target.CopySource, count, 4);
            using var dstBuf = new GraphicsBuffer(GraphicsBuffer.Target.Raw, count, 4);
            srcBuf.SetData(densityQ16, 0, 0, count);
            cs.SetBuffer(kKernelSdfSmooth, "_SdfSrc", srcBuf);
            cs.SetBuffer(kKernelSdfSmooth, "_SdfDst", dstBuf);
            DispatchSdf(cs, grid);
            ReadbackSync(dstBuf, densityQ16, count);
        }

        static void SetGridUniforms(ComputeShader cs, in VoxelGridInfo grid)
        {
            cs.SetInt("_GridN", grid.N);
            cs.SetFloat("_GridStep", grid.Step);
            cs.SetVector("_GridMin", new Vector4(grid.Min.x, grid.Min.y, grid.Min.z, 0f));
        }

        static void DispatchSdf(ComputeShader cs, in VoxelGridInfo grid)
        {
            cs.Dispatch(kKernelSdfSmooth,
                Mathf.CeilToInt(grid.N / 8f), Mathf.CeilToInt(grid.N / 8f), Mathf.CeilToInt(grid.N / 4f));
        }

        static GraphicsBuffer ToGpuSplats(NativeArray<SplatRaw> splats)
        {
            var gpu = new NativeArray<SplatGpuIn>(splats.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                new ConvertJob { In = splats, Out = gpu }.Schedule(splats.Length, 8192).Complete();
                var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splats.Length, System.Runtime.InteropServices.Marshal.SizeOf<SplatGpuIn>());
                buffer.SetData(gpu, 0, 0, splats.Length);
                return buffer;
            }
            finally { gpu.Dispose(); }
        }

        static GraphicsBuffer ToGpuAabbs(NativeArray<AabbRange> exclusions)
        {
            // 空数组用 1 元素占位(核内 _ExclusionCount=0 不会读取)
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, exclusions.Length), System.Runtime.InteropServices.Marshal.SizeOf<AabbGpu>());
            if (exclusions.Length == 0)
            {
                buffer.SetData(new AabbGpu[1]);
                return buffer;
            }
            var arr = new AabbGpu[exclusions.Length];
            for (int i = 0; i < exclusions.Length; ++i)
            {
                arr[i] = new AabbGpu
                {
                    MinBox = new Vector4(exclusions[i].Min.x, exclusions[i].Min.y, exclusions[i].Min.z, 0f),
                    MaxBox = new Vector4(exclusions[i].Max.x, exclusions[i].Max.y, exclusions[i].Max.z, 0f)
                };
            }
            buffer.SetData(arr);
            return buffer;
        }

        [BurstCompile(FloatMode = FloatMode.Fast)]
        struct ConvertJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<SplatRaw> In;
            [NativeDisableParallelForRestriction] public NativeArray<SplatGpuIn> Out;

            public void Execute(int i)
            {
                SplatRaw s = In[i];
                Out[i] = new SplatGpuIn
                {
                    Position = s.Position,
                    Scale = s.Scale,
                    Rotation = s.Rotation,
                    Opacity = s.Opacity,
                    Pad = 0f
                };
            }
        }
    }
}
