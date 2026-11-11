// Splat2Play — M8(方向 3)T8.2:静态背景与动态子物体的 Splat 分离 + 底面空洞修补
// 流程:中心落在 DynamicPropVolume 内的 splat → 剥离为独立子物体点云(局部居中);
//       主背景在盒底执行底面邻域高斯修补(Floor Inpainting),防止物体推开后露出黑洞。
// 算法(§5.2(2)):
//   1. 取 Volume 底面切片 y∈[ymin-0.08, ymin+0.08],向外扩 RingBand(默认 0.20m)成环形采样框;
//   2. 统计环内背景高斯的平均高度 ȳ、平均 f_dc 颜色、平均水平尺度 σ̄;
//   3. 在盒底足迹内按步长 Δ=1.5σ̄ 铺设扁平修补高斯(σ̄, 0.01, σ̄),α=0.95,颜色 ±3% 确定性扰动。
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Text;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Splat2Play.Runtime
{
    /// <summary>分离结果(所有 NativeArray 由调用方 Dispose)。</summary>
    public sealed class PropExtractionResult : IDisposable
    {
        public NativeArray<SplatRawX> Background;   // 剔除 + 底面修补后的背景
        public NativeArray<SplatRawX>[] Props;      // 每个动态物体的点云(局部坐标,质心在原点)
        public float3[] PropCentroids;              // 各物体质心(资产局部坐标 = 子物体摆放位置)
        public string[] PropNames;
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            if (Disposed) return;
            if (Background.IsCreated) Background.Dispose();
            if (Props != null)
                foreach (var p in Props)
                    if (p.IsCreated) p.Dispose();
            Disposed = true;
        }
    }

    public static class SplatPropExtractor
    {
        /// <summary>底面切片半高(米,规格 §5.2(2))。</summary>
        public const float kFloorSliceHalfHeight = 0.08f;
        /// <summary>环形采样框外扩宽度(米)。</summary>
        public const float kRingBandWidth = 0.20f;
        /// <summary>修补高斯不透明度。</summary>
        public const float kInpaintOpacity = 0.95f;

        /// <summary>
        /// 按设置中的 DynamicProps 分离背景与动态物体。
        /// 无 DynamicProps 时直接原样返回背景(Props 为空数组)。
        /// </summary>
        public static PropExtractionResult Extract(NativeArray<SplatRawX> splats, ProxyBakeSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var volumes = settings.DynamicProps;
            var result = new PropExtractionResult();
            int propCount = volumes?.Count ?? 0;

            var assignment = new NativeArray<int>(splats.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var counts = new NativeArray<int>(math.max(1, propCount + 1), Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var sums = new NativeArray<float3>(math.max(1, propCount + 1), Allocator.TempJob, NativeArrayOptions.ClearMemory);
            bool ok = false;
            try
            {
                var volArray = new AabbRange[propCount];
                for (int v = 0; v < propCount; ++v)
                    volArray[v] = new AabbRange { Min = (float3)volumes[v].Volume.min, Max = (float3)volumes[v].Volume.max };
                using var volBuf = new NativeArray<AabbRange>(volArray, Allocator.TempJob);

                // 1. 归属标记(并行;每线程只写自己的槽位)
                new AssignJob { Splats = splats, Volumes = volBuf, Assignment = assignment }
                    .Schedule(splats.Length, 4096).Complete();

                // 2. 分组计数与质心(单线程 Burst,构造性确定)
                new GroupSumJob { Splats = splats, Assignment = assignment, Counts = counts, Sums = sums }
                    .Schedule().Complete();

                result.PropNames = new string[propCount];
                result.PropCentroids = new float3[propCount];
                for (int v = 0; v < propCount; ++v)
                {
                    result.PropNames[v] = string.IsNullOrEmpty(volumes[v].PropName) ? $"Prop{v}" : volumes[v].PropName;
                    int c = counts[v];
                    result.PropCentroids[v] = c > 0 ? sums[v] / c : (float3)volumes[v].Volume.center;
                }

                // 3. 分组写出(prop 局部居中;背景 = 剩余 splats)
                result.Props = new NativeArray<SplatRawX>[propCount];
                bool anyProp = false;
                for (int v = 0; v < propCount; ++v)
                    if (counts[v] > 0) { anyProp = true; break; }

                if (!anyProp)
                {
                    result.Background = new NativeArray<SplatRawX>(splats.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    NativeArray<SplatRawX>.Copy(splats, result.Background, splats.Length);
                }
                else
                {
                    for (int v = 0; v < propCount; ++v)
                    {
                        int c = counts[v];
                        if (c <= 0) { result.Props[v] = new NativeArray<SplatRawX>(0, Allocator.Persistent); continue; }
                        var prop = new NativeArray<SplatRawX>(c, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                        FillGroup(splats, assignment, v, prop, result.PropCentroids[v]);
                        result.Props[v] = prop;
                    }

                    int bgCount = counts[propCount];
                    var bg = new NativeArray<SplatRawX>(bgCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    FillGroup(splats, assignment, propCount, bg, float3.zero);
                    result.Background = bg;
                }
                ok = true;
                return result;
            }
            finally
            {
                if (!ok) result.Dispose();
                assignment.Dispose();
                counts.Dispose();
                sums.Dispose();
            }
        }

        static void FillGroup(NativeArray<SplatRawX> splats, NativeArray<int> assignment, int group,
                              NativeArray<SplatRawX> dst, float3 offset)
        {
            // 单线程顺序填充(确定性;相对分离总耗时占比小)
            int w = 0;
            for (int i = 0; i < splats.Length && w < dst.Length; ++i)
            {
                if (assignment[i] != group) continue;
                SplatRawX s = splats[i];
                s.Position -= offset;
                dst[w++] = s;
            }
        }

        /// <summary>
        /// T8.2 底面修补:对每个切割盒,从背景 splats 采样环形框统计,在盒底足迹内铺设修补高斯,
        /// 追加到 background 之后(返回新数组;原数组不释放,由调用方决定去留)。
        /// </summary>
        public static NativeArray<SplatRawX> InpaintFloors(NativeArray<SplatRawX> background,
                                                           ProxyBakeSettings settings, int propStartIndex = 0)
        {
            var volumes = settings.DynamicProps;
            int propCount = volumes?.Count ?? 0;
            if (propCount == 0) return background;

            var statsList = new InpaintStats[propCount];
            long addedTotal = 0;
            for (int v = 0; v < propCount; ++v)
            {
                float3 volMin = (float3)volumes[v].Volume.min, volMax = (float3)volumes[v].Volume.max;
                var stats = SampleRingStats(background, volMin, volMax);
                statsList[v] = stats;
                addedTotal += StepsFor(volMax.x - volMin.x, stats.Step) * StepsFor(volMax.z - volMin.z, stats.Step);
            }
            if (addedTotal == 0) return background;

            var merged = new NativeArray<SplatRawX>(background.Length + (int)addedTotal, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            try
            {
                NativeArray<SplatRawX>.Copy(background, merged, background.Length);
                int w = background.Length;
                for (int v = 0; v < propCount; ++v)
                {
                    var stats = statsList[v];
                    float3 volMin = (float3)volumes[v].Volume.min, volMax = (float3)volumes[v].Volume.max;
                    float3 scale = new float3(stats.MeanScale, 0.01f, stats.MeanScale);
                    for (float z = volMin.z + stats.Step * 0.5f; z < volMax.z; z += stats.Step)
                    for (float x = volMin.x + stats.Step * 0.5f; x < volMax.x; x += stats.Step)
                    {
                        // ±3% 确定性伪随机颜色扰动(整数哈希,无 RNG 状态依赖)
                        uint hash = math.asuint(x * 73856093f) ^ math.asuint(z * 19349663f) ^ (uint)(propStartIndex + v) * 83492791u;
                        float jitter = ((hash >> 8) & 0xFFFF) / 65535f - 0.5f; // [-0.5, 0.5]
                        float3 fdc = stats.MeanFdc * (1f + 0.06f * jitter);
                        merged[w++] = new SplatRawX
                        {
                            Position = new float3(x, stats.MeanY, z),
                            Scale = scale,
                            Rotation = new float4(0f, 0f, 0f, 1f),
                            Opacity = kInpaintOpacity,
                            Fdc = fdc
                        };
                    }
                }
            }
            catch
            {
                merged.Dispose();
                throw;
            }
            return merged;
        }

        struct InpaintStats
        {
            public float MeanY;
            public float3 MeanFdc;
            public float MeanScale;
            public float Step;
        }

        /// <summary>修补网格某轴的步数(与铺设循环逐位一致,保证分配大小精确)。</summary>
        static int StepsFor(float span, float step)
        {
            if (span <= 0f || step <= 0f) return 0;
            return math.max(0, (int)math.ceil(span / step - 0.5f));
        }

        /// <summary>统计环形采样框(底面切片 ±0.08m,足迹外扩 0.20m 减足迹本身)内的背景高斯均值。</summary>
        static InpaintStats SampleRingStats(NativeArray<SplatRawX> background, float3 volMin, float3 volMax)
        {
            double sumY = 0, sumScale = 0;
            double3 sumFdc = 0;
            int n = 0;
            float yLo = volMin.y - kFloorSliceHalfHeight, yHi = volMin.y + kFloorSliceHalfHeight;
            for (int i = 0; i < background.Length; ++i)
            {
                float3 p = background[i].Position;
                if (p.y < yLo || p.y > yHi) continue;
                bool inFootprintX = p.x >= volMin.x && p.x <= volMax.x;
                bool inFootprintZ = p.z >= volMin.z && p.z <= volMax.z;
                if (inFootprintX && inFootprintZ) continue; // 只采样足迹外的环带
                bool inRingX = p.x >= volMin.x - kRingBandWidth && p.x <= volMax.x + kRingBandWidth;
                bool inRingZ = p.z >= volMin.z - kRingBandWidth && p.z <= volMax.z + kRingBandWidth;
                if (!(inRingX && inRingZ)) continue;
                sumY += p.y;
                sumFdc += background[i].Fdc;
                sumScale += math.cmax(background[i].Scale); // 水平平均尺度用最大分量近似
                ++n;
            }
            var stats = new InpaintStats { MeanY = volMin.y, MeanFdc = new float3(0.4f, 0.4f, 0.4f), MeanScale = 0.05f };
            if (n > 0)
            {
                stats.MeanY = (float)(sumY / n);
                stats.MeanFdc = (float3)(sumFdc / n);
                stats.MeanScale = math.clamp((float)(sumScale / n), 0.01f, 0.5f);
            }
            stats.Step = math.max(1.5f * stats.MeanScale, 0.01f);
            return stats;
        }

        /// <summary>
        /// 写出 binary_little_endian 3DGS PLY(x/y/z, f_dc_0..2, opacity, scale_0..2, rot_0..3)。
        /// rot_0 = w(与 PlySplatReader/PLYFileReader 的约定一致)。
        /// </summary>
        public static void WriteBinaryPly(string path, NativeArray<SplatRawX> splats)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var header = new StringBuilder(256);
            header.Append("ply\nformat binary_little_endian 1.0\n");
            header.Append($"element vertex {splats.Length}\n");
            foreach (var n in new[] { "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" })
                header.Append($"property float {n}\n");
            header.Append("end_header\n");
            byte[] hb = Encoding.ASCII.GetBytes(header.ToString());
            fs.Write(hb, 0, hb.Length);

            const int floatsPerSplat = 14;
            var row = new float[floatsPerSplat];
            var bytes = new byte[floatsPerSplat * 4];
            for (int i = 0; i < splats.Length; ++i)
            {
                SplatRawX s = splats[i];
                float op = math.clamp(s.Opacity, 1e-4f, 1f - 1e-4f);
                row[0] = s.Position.x; row[1] = s.Position.y; row[2] = s.Position.z;
                row[3] = s.Fdc.x; row[4] = s.Fdc.y; row[5] = s.Fdc.z;
                row[6] = math.log(op / (1f - op)); // sigmoid 反激活,与读取端对称
                row[7] = math.log(math.max(s.Scale.x, 1e-8f));
                row[8] = math.log(math.max(s.Scale.y, 1e-8f));
                row[9] = math.log(math.max(s.Scale.z, 1e-8f));
                row[10] = s.Rotation.w; // rot_0 = w
                row[11] = s.Rotation.x;
                row[12] = s.Rotation.y;
                row[13] = s.Rotation.z;
                Buffer.BlockCopy(row, 0, bytes, 0, bytes.Length);
                fs.Write(bytes, 0, bytes.Length);
            }
        }

        /// <summary>归属标记:中心点在盒内 → 物体索引;否则 = propCount(背景)。</summary>
        [BurstCompile(FloatMode = FloatMode.Fast)]
        struct AssignJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<SplatRawX> Splats;
            [ReadOnly] public NativeArray<AabbRange> Volumes;
            [NativeDisableParallelForRestriction] public NativeArray<int> Assignment;

            public void Execute(int i)
            {
                float3 p = Splats[i].Position;
                for (int v = 0; v < Volumes.Length; ++v)
                {
                    if (Volumes[v].Contains(p))
                    {
                        Assignment[i] = v;
                        return;
                    }
                }
                Assignment[i] = Volumes.Length; // 背景
            }
        }

        /// <summary>分组计数与质心和(单线程 Burst,构造性确定)。</summary>
        [BurstCompile(FloatMode = FloatMode.Fast)]
        struct GroupSumJob : IJob
        {
            [ReadOnly] public NativeArray<SplatRawX> Splats;
            [ReadOnly] public NativeArray<int> Assignment;
            public NativeArray<int> Counts;   // 长度 = propCount + 1
            public NativeArray<float3> Sums;  // 长度 = propCount + 1(背景组求和不使用)

            public void Execute()
            {
                int bgGroup = Counts.Length - 1;
                for (int i = 0; i < Splats.Length; ++i)
                {
                    int g = Assignment[i];
                    if (g >= bgGroup) continue; // 背景组不需要质心
                    Counts[g]++;
                    Sums[g] += Splats[i].Position;
                }
                Counts[bgGroup] = Splats.Length;
                for (int g = 0; g < bgGroup; ++g)
                    Counts[bgGroup] -= Counts[g];
            }
        }
    }
}
