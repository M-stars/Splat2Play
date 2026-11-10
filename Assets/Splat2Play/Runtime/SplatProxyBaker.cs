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
        public Bounds SceneBounds;
        public int SplatCount;
        public int TriangleCount;
        public int VoxelResolution;
        public float DensityThreshold;
        public string Log;   // 各阶段耗时与决策,供 benchmarks 汇总
        public bool Disposed { get; private set; }
        public void Dispose()
        {
            if (!Disposed)
            {
                if (Mesh != null) UnityEngine.Object.DestroyImmediate(Mesh);
                Disposed = true;
            }
        }
    }

    public static class SplatProxyBaker
    {
        /// <summary>
        /// 完整烘焙:PLY → 密度场 → 填充 → Surface Nets → 抽稀 → Mesh。
        /// mesh 只在 result 内有效,调用方负责转存为资产或 Dispose。
        /// </summary>
        public static ProxyBakeResult Bake(Stream plyStream, ProxyBakeSettings settings,
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

            // —— 1. 解析 PLY ——
            Progress("解析 PLY 头部", 0f);
            var header = PlySplatReader.ReadHeader(plyStream);
            if (header.SplatCount == 0) throw new InvalidDataException("PLY 中没有 splat");
            Progress($"解析 PLY 头部完成({header.SplatCount:N0} splats, stride={header.Stride}B)", 0.05f);

            NativeArray<SplatRaw> splats = PlySplatReader.ReadSplats(plyStream, header, out long vertexBytes);
            Progress($"读取顶点数据完成({vertexBytes / (1024f * 1024f):F1} MB)", 0.1f);

            try
            {
                // —— 2. 包围盒与网格 ——
                using (var positions = splats.ToPositions())
                {
                    var mm = new MinMaxJob { Positions = positions };
                    mm.Schedule().Complete();
                    float3 mn = mm.Min, mx = mm.Max;
                    float margin = 0.5f;
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
                    Progress($"体素网格 {res}³(步长 {grid.Step:F3}m, 场景 {maxAxis:F1}m)", 0.12f);

                    // 排除区/封闭区 → Burst 结构
                    using var exclusions = ToAabbs(settings.ExclusionVolumes);
                    using var seals = ToAabbs(settings.SealVolumes);

                // —— 3. 体素化 ——
                var density = new NativeArray<int>(grid.Count, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                try
                {
                    new VoxelizeJob
                    {
                        Splats = splats, Exclusions = exclusions, Grid = grid,
                        OpacityWeight = settings.OpacityWeight, DensityQ16 = density
                    }.Schedule(splats.Length, 256).Complete();
                    Progress("体素化完成", 0.55f);

                    // —— 4. 填充 ——
                    new FillJob
                    {
                        Grid = grid, ThresholdQ16 = (int)(settings.DensityThreshold * 65536f),
                        SealVolumes = seals, DensityQ16 = density
                    }.Schedule().Complete();
                    Progress("洪泛填充完成", 0.65f);

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
                        Progress($"网格提取完成({vertices.Length:N0} 顶点 / {triangles.Count / 3:N0} 三角形)", 0.8f);

                        // —— 6. 抽稀 ——
                        int decimPasses = 0;
                        while (triangles.Count / 3 > settings.MaxTriangles && decimPasses < 6)
                        {
                            float step = grid.Step * Mathf.Pow(2f, decimPasses + 1);
                            new DecimateJob
                            {
                                ClusterStep = step, GridMin = grid.Min,
                                Vertices = vertices, Triangles = triangles
                            }.Schedule().Complete();
                            ++decimPasses;
                            Progress($"抽稀第 {decimPasses} 轮(step={step:F2}m):{vertices.Length:N0} 顶点 / {triangles.Count / 3:N0} 三角形", 0.8f + 0.05f * decimPasses);
                        }
                        if (triangles.Count / 3 > settings.MaxTriangles)
                            log.AppendLine($"[警告] 面数 {triangles.Count / 3:N0} 仍高于目标 {settings.MaxTriangles:N0}(已达抽稀上限)");

                        // —— 7. 组装 Mesh ——
                        var mesh = BuildMesh(vertices, triangles, grid, log);
                        Progress("烘焙完成", 1f);

                        return new ProxyBakeResult
                        {
                            Mesh = mesh,
                            SceneBounds = new Bounds((mn + mx) * 0.5f, size),
                            SplatCount = header.SplatCount,
                            TriangleCount = triangles.Count / 3,
                            VoxelResolution = res,
                            DensityThreshold = settings.DensityThreshold,
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

        static NativeArray<float3> ToPositions(this NativeArray<SplatRaw> splats)
        {
            var positions = new NativeArray<float3>(splats.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < splats.Length; ++i)
                positions[i] = splats[i].Position;
            return positions; // 调用方以 using 持有并在用后释放
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

        static Mesh BuildMesh(NativeList<float3> vertices, NativeList<int> triangles, VoxelGridInfo grid, System.Text.StringBuilder log)
        {
            var mesh = new Mesh { name = "S2P_ProxyCollision" };
            int vCount = vertices.Length;
            var v = new Vector3[vCount];
            for (int i = 0; i < vCount; ++i)
            {
                float3 p = vertices[i];
                v[i] = new Vector3(p.x, p.y, p.z);
            }
            var t = new int[triangles.Length];
            for (int i = 0; i < triangles.Length; ++i) t[i] = triangles[i];
            mesh.vertices = v;
            mesh.SetIndices(t, MeshTopology.Triangles, 0, false);
            mesh.RecalculateBounds();
            log.AppendLine($"Mesh: {vCount:N0} 顶点 / {triangles.Count / 3:N0} 三角形");
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
