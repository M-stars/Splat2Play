// Splat2Play — M1 烘焙编排器(§5.1 算法步骤 1–9 的串联实现)
// 注意:本类不引用 UnityEditor,可在 Runtime 程序集使用;资产落地由编辑器向导负责。
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using System.IO;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace Splat2Play.Runtime
{
    /// <summary>烘焙结果(Mesh 在调用方 Dispose 前必须转存/落地)。</summary>
    public sealed class ProxyBakeResult : IDisposable
    {
        public Mesh Mesh;
        public Mesh NavigationMesh;
        public Bounds SceneBounds;
        public int SplatCount;
        public int TriangleCount;
        public int NavTriangleCount;
        public int VoxelResolution;
        public float DensityThreshold;
        public System.Collections.Generic.List<Bounds> ChunkBounds = new();
        public System.Collections.Generic.List<int> ChunkSplatCounts = new();
        public string Log;   // 各阶段耗时与决策,供 benchmarks 汇总
        public bool Disposed { get; private set; }
        public void Dispose()
        {
            if (!Disposed)
            {
                if (Mesh != null) UnityEngine.Object.DestroyImmediate(Mesh);
                if (NavigationMesh != null) UnityEngine.Object.DestroyImmediate(NavigationMesh);
                Disposed = true;
            }
        }
    }

    public static class SplatProxyBaker
    {
        /// <summary>
        /// 完整烘焙:PLY/SPZ → 密度场 → 填充 → Surface Nets → 抽稀 → Mesh。
        /// mesh 只在 result 内有效,调用方负责转存为资产或 Dispose。
        /// M5:输入流自动按文件头路由 PLY / SPZ;体素化后端按 settings.Backend 选择。
        /// </summary>
        public static ProxyBakeResult Bake(Stream sourceStream, ProxyBakeSettings settings,
                                           Action<string, float> onProgress = null)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var log = new System.Text.StringBuilder();
            var sw = Stopwatch.StartNew();
            void Progress(string stage, float t)
            {
                onProgress?.Invoke(stage, t);
                log.AppendLine($"[{sw.ElapsedMilliseconds,6} ms] {stage}");
            }

            // —— 1. 解析 PLY / SPZ(M5:按魔数自动路由)——
            Progress("解析扫描资产头部", 0f);
            var splats = SplatSourceReader.ReadAll(sourceStream, out int splatCount, out SplatSourceKind kind, out long sourceBytes);
            if (splatCount == 0) throw new InvalidDataException("扫描资产中没有 splat");
            Progress($"解析 {(kind == SplatSourceKind.Spz ? "SPZ" : "PLY")} 头部完成({splatCount:N0} splats, 属性 {sourceBytes / (1024f * 1024f):F1} MB)", 0.05f);

            try
            {
                // 排除区/封闭区 → Burst 结构
                using var exclusions = ToAabbs(settings.ExclusionVolumes);
                using var seals = ToAabbs(settings.SealVolumes);

                // —— 2. 包围盒与网格 ——
                using (var mmResult = new NativeArray<float3>(2, Allocator.TempJob, NativeArrayOptions.UninitializedMemory))
                {
                    // 百分位包围盒:逐轴取 [p, 100-p] 分位,剔除天空等远距离离群 splat
                    float p = Mathf.Clamp(settings.BoundsPercentile, 0f, 10f);
                    float3 mn, mx;
                    if (p > 0.01f)
                    {
                        ComputePercentileBounds(splats, exclusions, p, out mn, out mx);
                        if (math.any(mx - mn < 1e-3f)) { mn = float3.zero; mx = float3.zero; } // 退化保护
                    }
                    else
                    {
                        var mm = new MinMaxJob { Splats = splats, Exclusions = exclusions, Result = mmResult };
                        mm.Schedule().Complete();
                        mn = mmResult[0]; mx = mmResult[1];
                    }

                    float margin = Mathf.Clamp(settings.BoundsMargin, 0f, 5f);
                    float3 pctMin = mn, pctMax = mx; // 百分位盒(不含 margin),供取景/玩法使用
                    mn -= margin; mx += margin;
                    float3 size = mx - mn;
                    float maxAxis = math.max(size.x, math.max(size.y, size.z));
                    int res = Mathf.ClosestPowerOfTwo(Mathf.Clamp(settings.VoxelResolution, 64, 512));
                    var grid = new VoxelGridInfo
                    {
                        N = res,
                        Min = mn - (new float3(maxAxis, maxAxis, maxAxis) - size) * 0.5f, // 居中,保证三轴同分辨率
                        Step = maxAxis / res
                    };
                    Progress($"体素网格 {res}³(步长 {grid.Step:F3}m, 场景 {maxAxis:F1}m, margin={margin:F2}m)", 0.12f);

                    // 提取 M4 空间分块包围盒(4×2×4 子块)
                    BuildSpatialChunks(splats, exclusions, pctMin, pctMax, out var chunkBounds, out var chunkCounts);

                // —— 3. 体素化(M5:按 Backend 路由 CPU Burst / GPU Compute)——
                var density = new NativeArray<int>(grid.Count, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                try
                {
                    // Auto 策略:支持 Compute 且 256³/512³ 且点数 ≥ 100 万时走 GPU(§2.1 T5.2)
                    bool autoGpu = settings.Backend == ProxyBakeBackend.Auto
                                   && SystemInfo.supportsComputeShaders
                                   && res >= 256 && splats.Length >= 1_000_000;
                    bool useGpu = settings.Backend == ProxyBakeBackend.GPUCompute || autoGpu;
                    bool gpuSdfDone = false;
                    if (useGpu && !SystemInfo.supportsComputeShaders)
                    {
                        log.AppendLine("[警告] GPUCompute 后端不可用(不支持 Compute Shader),回退 CPU 确定性管线");
                        useGpu = false;
                    }
                    if (useGpu)
                    {
                        var gpuSw = Stopwatch.StartNew();
                        bool sdfOnGpu = settings.UseSDF && res < 512; // 512³ 自动关 SDF 的规则与 CPU 一致
                        S2PGpuVoxelBaker.Voxelize(splats, exclusions, grid, settings.OpacityWeight, density, sdfOnGpu);
                        gpuSdfDone = sdfOnGpu;
                        log.AppendLine($"[M5] GPU Compute 体素化{(sdfOnGpu ? " + SDF 平滑" : "")} 耗时 {gpuSw.ElapsedMilliseconds} ms(含读回)");
                        Progress("GPU 体素化完成", 0.55f);
                    }
                    else
                    {
                        new VoxelizeJob
                        {
                            Splats = splats, Exclusions = exclusions, Grid = grid,
                            OpacityWeight = settings.OpacityWeight, DensityQ16 = density
                        }.Schedule(splats.Length, 256).Complete();
                        Progress("体素化完成", 0.55f);
                    }
                    using (var hres = new NativeArray<ulong>(1, Allocator.TempJob, NativeArrayOptions.ClearMemory))
                    {
                        new HashJob { Data = density, Result = hres }.Schedule().Complete();
                        log.AppendLine($"[诊断] 体素化后密度哈希 {hres[0]:X}");
                    }

                    // —— 4. 填充 ——
                    using (var stats = new NativeArray<long>(2, Allocator.TempJob, NativeArrayOptions.ClearMemory))
                    {
                        new FillJob
                        {
                            Grid = grid, ThresholdQ16 = (int)(settings.DensityThreshold * 65536f),
                            SkipInteriorFill = !settings.FillInterior,
                            SealVolumes = seals, DensityQ16 = density, Stats = stats
                        }.Schedule().Complete();
                        Progress($"洪泛填充完成(fillInterior={settings.FillInterior}, 密度和 {stats[0]}, 实心体素 {stats[1]})", 0.65f);
                        using (var hres2 = new NativeArray<ulong>(1, Allocator.TempJob, NativeArrayOptions.ClearMemory))
                        {
                            new HashJob { Data = density, Result = hres2 }.Schedule().Complete();
                            log.AppendLine($"[诊断] 填充后密度哈希 {hres2[0]:X}");
                        }
                    }

                    // —— 4.5 可选 SDF 平滑(§5.1 useSDF,512 档自动跳过以保证内存峰值 <2GB) ——
                    // M5:GPU 后端已在读回前于 GPU 上完成平滑(规格 §2.2 流程图:体素化→SDF→读回→填充)
                    bool applySdf = settings.UseSDF && res < 512 && !gpuSdfDone;
                    if (settings.UseSDF && res >= 512)
                        log.AppendLine("[警告] 512³ 分辨率下已自动跳过 SDF 平滑以控制内存峰值(<2GB)");
                    if (settings.UseSDF && gpuSdfDone)
                        log.AppendLine("[M5] SDF 平滑已在 GPU 完成(填充前执行,规格 §2.2 GPU 路径)");
                    if (applySdf)
                    {
                        var sdfFiltered = new NativeArray<int>(grid.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                        try
                        {
                            new SdfSmoothJob { Grid = grid, SrcDensityQ16 = density, DstDensityQ16 = sdfFiltered }
                                .Schedule(res, 4).Complete();
                            density.CopyFrom(sdfFiltered);
                            Progress("SDF 距离场平滑完成", 0.72f);
                        }
                        finally
                        {
                            if (sdfFiltered.IsCreated) sdfFiltered.Dispose();
                        }
                    }

                    // —— 5. Surface Nets ——
                    var vertices = new NativeList<float3>(1 << 18, Allocator.Persistent);
                    var triangles = new NativeList<int>(1 << 19, Allocator.Persistent);
                    try
                    {
                        new SurfaceNetsJob
                        {
                            Grid = grid, Threshold = settings.DensityThreshold,
                            DensityQ16 = density, Vertices = vertices, Triangles = triangles
                        }.Schedule().Complete();
                        Progress($"[SURF] n={vertices.Length} v0={(vertices.Length > 0 ? vertices[0] : float3.zero)} gridMin={grid.Min} step={grid.Step}", 0.8f);
                        {
                            int bad = 0; float maxAbs = 0f;
                            for (int i = 0; i < vertices.Length; ++i)
                            {
                                float3 pv = vertices[i];
                                if (!math.all(math.isfinite(pv))) ++bad;
                                else maxAbs = math.max(maxAbs, math.cmax(math.abs(pv)));
                            }
                            log.AppendLine($"[诊断] SURF 输出: 非有限={bad} 最大坐标={maxAbs:F1}");
                        }

                        // —— 6. 抽稀 ——
                        int decimPasses = 0;
                        var diag = new NativeArray<long>(13, Allocator.TempJob, NativeArrayOptions.ClearMemory);
                        try
                        {
                            while (triangles.Count / 3 > settings.MaxTriangles && decimPasses < 6)
                            {
                                float step = grid.Step * Mathf.Pow(2f, decimPasses + 1);
                                diag[0] = 0; diag[1] = 0; diag[2] = 0; diag[3] = 0;
                                new DecimateJob
                                {
                                    ClusterStep = step, GridMin = grid.Min,
                                    BoundMin = grid.Min - grid.Step,
                                    BoundMax = grid.Min + (grid.N + 1) * grid.Step,
                                    Vertices = vertices, Triangles = triangles, Diag = diag
                                }.Schedule().Complete();
                                ++decimPasses;
                                Progress($"抽稀第 {decimPasses} 轮(step={step:F2}m):{vertices.Length:N0} 顶点 / {triangles.Count / 3:N0} 三角形 | total={diag[0]} clusters={diag[1]} kd=({diag[4]},{diag[5]},{diag[6]}) kMin=({diag[7]},{diag[8]},{diag[9]}) kMax=({diag[10]},{diag[11]},{diag[12]}) 非有限={diag[2]} 早退={diag[3]}", 0.8f + 0.05f * decimPasses);
                                {
                                    int bad2 = 0;
                                    for (int i = 0; i < vertices.Length; ++i)
                                        if (!math.all(math.isfinite(vertices[i]))) ++bad2;
                                    log.AppendLine($"[诊断] 抽稀{decimPasses} 后顶点非有限数={bad2}");
                                }
                            }
                        }
                        finally
                        {
                            diag.Dispose();
                        }
                        if (triangles.Count / 3 > settings.MaxTriangles)
                            log.AppendLine($"[警告] 面数 {triangles.Count / 3:N0} 仍高于目标 {settings.MaxTriangles:N0}(已达抽稀上限)");

                        // —— 7. 组装碰撞 Mesh 与可选寻路粗 Mesh(§5.2) ——
                        int collTris = triangles.Length / 3;
                        var mesh = BuildMesh(vertices, triangles, grid, log, "S2P_ProxyCollision");
                        Mesh navMesh = null;
                        int navTris = collTris;
                        if (settings.BakeNavigationMesh && triangles.Length / 3 > settings.MaxNavTriangles)
                        {
                            var diagNav = new NativeArray<long>(13, Allocator.TempJob, NativeArrayOptions.ClearMemory);
                            try
                            {
                                while (triangles.Length / 3 > settings.MaxNavTriangles && decimPasses < 8)
                                {
                                    float step = grid.Step * Mathf.Pow(2f, decimPasses + 1);
                                    new DecimateJob
                                    {
                                        ClusterStep = step, GridMin = grid.Min,
                                        BoundMin = grid.Min - grid.Step,
                                        BoundMax = grid.Min + (grid.N + 1) * grid.Step,
                                        Vertices = vertices, Triangles = triangles, Diag = diagNav
                                    }.Schedule().Complete();
                                    ++decimPasses;
                                }
                            }
                            finally { diagNav.Dispose(); }
                            navTris = triangles.Length / 3;
                            navMesh = BuildMesh(vertices, triangles, grid, log, "S2P_ProxyNavigation");
                        }
                        Progress("烘焙完成", 1f);

                        return new ProxyBakeResult
                        {
                            Mesh = mesh,
                            NavigationMesh = navMesh,
                            SceneBounds = new Bounds((pctMin + pctMax) * 0.5f, pctMax - pctMin), // 百分位盒:有意义的玩法范围
                            SplatCount = splatCount,
                            TriangleCount = collTris,
                            NavTriangleCount = navTris,
                            VoxelResolution = res,
                            DensityThreshold = settings.DensityThreshold,
                            ChunkBounds = chunkBounds,
                            ChunkSplatCounts = chunkCounts,
                            Log = log.ToString()
                        };
                    }
                    finally
                    {
                        if (vertices.IsCreated) vertices.Dispose();
                        if (triangles.IsCreated) triangles.Dispose();
                    }
                }
                finally
                {
                    if (density.IsCreated) density.Dispose();
                }
                } // using positions
            }
            finally
            {
                if (splats.IsCreated) splats.Dispose();
            }
        }

        /// <summary>百分位包围盒:逐轴取 [p, 100-p] 分位(采样后排序,过滤排除区,确定性)。</summary>
        static void ComputePercentileBounds(NativeArray<SplatRaw> splats, NativeArray<AabbRange> exclusions, float percentile, out float3 mn, out float3 mx)
        {
            int sampleCount = math.min(splats.Length, 8192);
            int stride = math.max(1, splats.Length / sampleCount);
            var xs = new System.Collections.Generic.List<float>(sampleCount);
            var ys = new System.Collections.Generic.List<float>(sampleCount);
            var zs = new System.Collections.Generic.List<float>(sampleCount);
            for (int i = 0; i < splats.Length; i += stride)
            {
                float3 p = splats[i].Position;
                bool excluded = false;
                for (int e = 0; e < exclusions.Length; ++e)
                    if (exclusions[e].Contains(p)) { excluded = true; break; }
                if (excluded) continue;
                xs.Add(p.x); ys.Add(p.y); zs.Add(p.z);
            }
            if (xs.Count == 0)
            {
                mn = float3.zero;
                mx = float3.zero;
                return;
            }
            float lo = percentile, hi = 100f - percentile;
            mn = new float3(Pct(xs, lo), Pct(ys, lo), Pct(zs, lo));
            mx = new float3(Pct(xs, hi), Pct(ys, hi), Pct(zs, hi));
        }

        static float Pct(System.Collections.Generic.List<float> v, float p)
        {
            v.Sort();
            int idx = Mathf.Clamp((int)(v.Count * p / 100f), 0, v.Count - 1);
            return v[idx];
        }

        static NativeArray<AabbRange> ToAabbs(System.Collections.Generic.List<Bounds> bounds)
        {
            if (bounds == null || bounds.Count == 0)
                return new NativeArray<AabbRange>(0, Allocator.TempJob);
            var arr = new NativeArray<AabbRange>(bounds.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < bounds.Count; ++i)
            {
                Bounds b = bounds[i];
                arr[i] = new AabbRange { Min = (float3)b.min, Max = (float3)b.max };
            }
            return arr;
        }

        static void BuildSpatialChunks(NativeArray<SplatRaw> splats, NativeArray<AabbRange> exclusions,
                                       float3 bMin, float3 bMax,
                                       out System.Collections.Generic.List<Bounds> chunkBounds,
                                       out System.Collections.Generic.List<int> chunkCounts)
        {
            const int kx = 4, ky = 2, kz = 4;
            const int total = kx * ky * kz;
            chunkBounds = new System.Collections.Generic.List<Bounds>(total);
            chunkCounts = new System.Collections.Generic.List<int>(total);
            float3 span = math.max(bMax - bMin, new float3(1e-3f));
            var counts = new int[total];
            var mins = new float3[total];
            var maxs = new float3[total];
            for (int i = 0; i < total; ++i)
            {
                mins[i] = new float3(float.PositiveInfinity);
                maxs[i] = new float3(float.NegativeInfinity);
            }

            int stride = math.max(1, splats.Length / 65536); // 采样加速并按比例放大计数
            for (int i = 0; i < splats.Length; i += stride)
            {
                float3 p = splats[i].Position;
                bool excluded = false;
                for (int e = 0; e < exclusions.Length; ++e)
                    if (exclusions[e].Contains(p)) { excluded = true; break; }
                if (excluded) continue;
                float3 norm = math.saturate((p - bMin) / span);
                int ix = math.clamp((int)(norm.x * kx), 0, kx - 1);
                int iy = math.clamp((int)(norm.y * ky), 0, ky - 1);
                int iz = math.clamp((int)(norm.z * kz), 0, kz - 1);
                int ci = ix + iy * kx + iz * kx * ky;
                counts[ci] += stride;
                mins[ci] = math.min(mins[ci], p);
                maxs[ci] = math.max(maxs[ci], p);
            }

            for (int i = 0; i < total; ++i)
            {
                if (counts[i] <= 0) continue;
                Vector3 c = (Vector3)((mins[i] + maxs[i]) * 0.5f);
                Vector3 s = (Vector3)math.max(maxs[i] - mins[i], new float3(0.2f));
                chunkBounds.Add(new Bounds(c, s));
                chunkCounts.Add(math.min(counts[i], splats.Length));
            }
        }

        static Mesh BuildMesh(NativeList<float3> vertices, NativeList<int> triangles, VoxelGridInfo grid, System.Text.StringBuilder log, string meshName = "S2P_ProxyCollision")
        {
            // 最后一道防线(托管侧):丢弃非有限/超界顶点并重映射索引,保证碰撞网格永远有效
            var cleanV = new System.Collections.Generic.List<Vector3>((int)vertices.Length);
            var remap = new System.Collections.Generic.Dictionary<int, int>((int)vertices.Length);
            for (int i = 0; i < vertices.Length; ++i)
            {
                float3 p = vertices[i];
                if (!math.all(math.isfinite(p)) || math.cmax(math.abs(p)) > 1e5f) continue;
                remap[i] = cleanV.Count;
                cleanV.Add(new Vector3(p.x, p.y, p.z));
            }
            var cleanT = new System.Collections.Generic.List<int>(triangles.Count);
            int droppedTris = 0;
            for (int t = 0; t + 2 < triangles.Count; t += 3)
            {
                bool ok = true;
                for (int k = 0; k < 3; ++k)
                    if (!remap.ContainsKey(triangles[t + k])) { ok = false; break; }
                if (!ok) { ++droppedTris; continue; }
                for (int k = 0; k < 3; ++k) cleanT.Add(remap[triangles[t + k]]);
            }

            var mesh = new Mesh
            {
                name = meshName,
                indexFormat = cleanV.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            mesh.vertices = cleanV.ToArray();
            mesh.SetIndices(cleanT.ToArray(), MeshTopology.Triangles, 0, false);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            log.AppendLine($"{meshName}: {cleanV.Count:N0} 顶点 / {cleanT.Count / 3:N0} 三角形(原始 {vertices.Length:N0}/{triangles.Count / 3:N0},丢弃脏顶点 {vertices.Length - cleanV.Count:N0}、脏三角形 {droppedTris:N0}) | bounds={mesh.bounds.size:F2} center={mesh.bounds.center:F2}");
            return mesh;
        }

        /// <summary>便捷入口:从文件路径烘焙(编辑器向导调用)。</summary>
        public static ProxyBakeResult Bake(string plyPath, ProxyBakeSettings settings,
                                           Action<string, float> onProgress = null)
        {
            using var fs = new FileStream(plyPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            return Bake(fs, settings, onProgress);
        }
    }
}
