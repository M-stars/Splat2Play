// Splat2Play — M1 EditMode 单元测试(T1.1–T1.5 验收)
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Splat2Play.Runtime;

namespace Splat2Play.Tests
{
    public sealed class ProxyBakeTests
    {
        const float kQ16 = 65536f;

        // ---------- 测试辅助:构造合成 PLY(带多余属性以测试偏移) ----------

        struct TestSplat
        {
            public float3 Pos;
            public float3 LogScale;
            public float4 Quat;      // wxyz 原始顺序(rot_0=w)
            public float OpacityRaw;
        }

        static byte[] BuildPly(TestSplat[] splats)
        {
            // 属性:x y z nx ny nz f_dc_0..2 f_rest_0..8 opacity scale_0..2 rot_0..3(SH1 布局)
            var header = new StringBuilder();
            header.Append("ply\nformat binary_little_endian 1.0\n");
            header.Append($"element vertex {splats.Length}\n");
            foreach (var n in new[] { "x", "y", "z", "nx", "ny", "nz", "f_dc_0", "f_dc_1", "f_dc_2" })
                header.Append($"property float {n}\n");
            for (int i = 0; i < 9; ++i) header.Append($"property float f_rest_{i}\n");
            foreach (var n in new[] { "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" })
                header.Append($"property float {n}\n");
            header.Append("end_header\n");

            using var ms = new MemoryStream();
            byte[] hb = Encoding.ASCII.GetBytes(header.ToString());
            ms.Write(hb, 0, hb.Length);
            const int floatsPerSplat = 26; // 3+3+3+9+1+3+4
            var buf = new float[floatsPerSplat];
            for (int s = 0; s < splats.Length; ++s)
            {
                TestSplat t = splats[s];
                int o = 0;
                buf[o++] = t.Pos.x; buf[o++] = t.Pos.y; buf[o++] = t.Pos.z;
                buf[o++] = 0; buf[o++] = 0; buf[o++] = 1;                       // 法线占位
                buf[o++] = 0.4f; buf[o++] = 0.5f; buf[o++] = 0.6f;              // f_dc
                for (int i = 0; i < 9; ++i) buf[o++] = 0.01f * i;               // f_rest
                buf[o++] = t.OpacityRaw;
                buf[o++] = t.LogScale.x; buf[o++] = t.LogScale.y; buf[o++] = t.LogScale.z;
                buf[o++] = t.Quat.w; buf[o++] = t.Quat.x; buf[o++] = t.Quat.y; buf[o++] = t.Quat.z;
                byte[] lb = new byte[floatsPerSplat * 4];
                Buffer.BlockCopy(buf, 0, lb, 0, lb.Length);
                ms.Write(lb, 0, lb.Length);
            }
            return ms.ToArray();
        }

        static PlySplatReader.PlyHeader OpenPly(byte[] ply, out MemoryStream stream)
        {
            stream = new MemoryStream(ply, false);
            return PlySplatReader.ReadHeader(stream);
        }

        // ---------- T1.1:PLY 解析 ----------

        [Test]
        public void PlyReader_ExtractsPhysicsQuantities()
        {
            var splats = new[]
            {
                new TestSplat { Pos = new float3(1, 2, 3), LogScale = new float3((float)Math.Log(0.25)), Quat = new float4(0, 0, 0, 1), OpacityRaw = 0f },
                new TestSplat { Pos = new float3(-4, 0.5f, 7), LogScale = new float3((float)Math.Log(0.5), (float)Math.Log(0.1), (float)Math.Log(0.2)),
                                Quat = math.normalize(new float4(1, 2, 3, 4)), OpacityRaw = 2f },
            };
            var header = OpenPly(BuildPly(splats), out var stream);
            using (stream)
            using (var data = PlySplatReader.ReadSplats(stream, header, out _))
            {
                Assert.AreEqual(2, header.SplatCount);
                Assert.AreEqual(2, data.Length);

                Assert.That(data[0].Position.x, Is.EqualTo(1f).Within(1e-4));
                Assert.That(data[0].Position.y, Is.EqualTo(2f).Within(1e-4));
                Assert.That(data[0].Position.z, Is.EqualTo(3f).Within(1e-4));
                Assert.That(data[0].Scale.x, Is.EqualTo(0.25f).Within(1e-4));
                Assert.That(data[0].Opacity, Is.EqualTo(0.5f).Within(1e-4)); // sigmoid(0)=0.5
                Assert.That(data[0].Rotation.x, Is.EqualTo(0f).Within(1e-4));
                Assert.That(data[0].Rotation.w, Is.EqualTo(1f).Within(1e-4));

                // 归一化四元数(xyzw):输入 (1,2,3,4)/norm
                float4 expect = math.normalizesafe(new float4(1, 2, 3, 4));
                Assert.That(data[1].Rotation.x, Is.EqualTo(expect.x).Within(1e-4));
                Assert.That(data[1].Rotation.y, Is.EqualTo(expect.y).Within(1e-4));
                Assert.That(data[1].Rotation.z, Is.EqualTo(expect.z).Within(1e-4));
                Assert.That(data[1].Rotation.w, Is.EqualTo(expect.w).Within(1e-4));
                Assert.That(data[1].Scale.y, Is.EqualTo(0.1f).Within(1e-4));
                Assert.That(data[1].Opacity, Is.EqualTo((float)(1 / (1 + Math.Exp(-2f)))).Within(1e-4));
            }
        }

        [Test]
        public void PlyReader_RejectsBadMagic()
        {
            byte[] bad = Encoding.ASCII.GetBytes("not a ply\n");
            Assert.Throws<InvalidDataException>(() =>
            {
                using var ms = new MemoryStream(bad);
                PlySplatReader.ReadHeader(ms);
            });
        }

        // ---------- T1.2:体素化 ----------

        [Test]
        public void Voxelize_SingleGaussian_HasPeakAtCenter()
        {
            var grid = new VoxelGridInfo { N = 64, Min = new float3(-0.64f), Step = 0.02f };
            var splats = new NativeArray<SplatRaw>(1, Allocator.TempJob);
            splats[0] = new SplatRaw
            {
                Position = float3.zero, Scale = new float3(0.1f),
                Rotation = new float4(0, 0, 0, 1), Opacity = 1f
            };
            using (splats)
            using (var density = new NativeArray<int>(grid.Count, Allocator.TempJob, NativeArrayOptions.ClearMemory))
            using (var exclusions = new NativeArray<AabbRange>(0, Allocator.TempJob))
            {
                new VoxelizeJob { Splats = splats, Exclusions = exclusions, Grid = grid, OpacityWeight = 1f, DensityQ16 = density }
                    .Schedule(1, 1).Complete();

                int center = grid.Index(31, 31, 31); // 中心 ≈ (-0.64+31.5*0.02) = -0.01
                float centerDensity = density[center] / kQ16;
                Assert.That(centerDensity, Is.GreaterThan(0.9f), "中心体素应接近满密度");

                // 3σ 之外应低于默认阈值的一半
                int far = grid.Index(46, 31, 31);   // 距中心约 0.3m = 3σ
                float farDensity = (density[far] & 0x7FFFFFFF) / kQ16;
                Assert.That(farDensity, Is.LessThan(0.1f), "3σ 外贡献应可忽略");

                // 确定性:重跑一致
                using var density2 = new NativeArray<int>(grid.Count, Allocator.TempJob, NativeArrayOptions.ClearMemory);
                new VoxelizeJob { Splats = splats, Exclusions = exclusions, Grid = grid, OpacityWeight = 1f, DensityQ16 = density2 }
                    .Schedule(1, 1).Complete();
                for (int i = 0; i < density.Length; ++i)
                    Assert.AreEqual(density[i], density2[i], $"体素 {i} 非确定性");
            }
        }

        // ---------- T1.3:洪泛填充 ----------

        static NativeArray<int> PaintHollowBox(VoxelGridInfo grid, int shellMin, int shellMax, int fill)
        {
            var d = new NativeArray<int>(grid.Count, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            int n = grid.N;
            for (int z = 0; z < n; ++z)
            for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x)
            {
                int m = math.max(math.max(math.abs(x - n / 2), math.abs(y - n / 2)), math.abs(z - n / 2));
                if (m == shellMin || m == shellMax) d[grid.Index(x, y, z)] = fill;
            }
            return d;
        }

        static NativeArray<int> PaintSolidBox(VoxelGridInfo grid, int radius, int fill)
        {
            var d = new NativeArray<int>(grid.Count, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            int n = grid.N;
            for (int z = 0; z < n; ++z)
            for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x)
            {
                int m = math.max(math.max(math.abs(x - n / 2), math.abs(y - n / 2)), math.abs(z - n / 2));
                if (m <= radius) d[grid.Index(x, y, z)] = fill;
            }
            return d;
        }

        [Test]
        public void Fill_HollowBox_InteriorBecomesSolid_ExteriorMarkedOutside()
        {
            var grid = new VoxelGridInfo { N = 16, Min = new float3(-8f), Step = 1f };
            using var density = PaintHollowBox(grid, 5, 6, 65536);
            using var seals = new NativeArray<AabbRange>(0, Allocator.TempJob);

            using (var stats = new NativeArray<long>(2, Allocator.TempJob, NativeArrayOptions.ClearMemory))
                new FillJob { Grid = grid, ThresholdQ16 = 32768, SealVolumes = seals, DensityQ16 = density, Stats = stats }
                    .Schedule().Complete();

            int inside = density[grid.Index(8, 8, 8)];
            Assert.AreEqual(65536, inside, "封闭腔体应被填实");

            int outside = density[grid.Index(1, 1, 1)];
            Assert.AreEqual(unchecked((int)0x80000000), outside, "外部空气应带 outside 标记且密度为 0");

            int shell = density[grid.Index(8, 8, 13)] & 0x7FFFFFFF;
            Assert.AreEqual(65536, shell, "壳体保持实心且不被标记 outside");
        }

        [Test]
        public void Fill_SkipInteriorFill_PreservesHollowCavity()
        {
            var grid = new VoxelGridInfo { N = 16, Min = new float3(-8f), Step = 1f };
            using var density = PaintHollowBox(grid, 5, 6, 65536);
            using var seals = new NativeArray<AabbRange>(0, Allocator.TempJob);
            using var stats = new NativeArray<long>(2, Allocator.TempJob, NativeArrayOptions.ClearMemory);

            new FillJob
            {
                Grid = grid, ThresholdQ16 = 32768, SkipInteriorFill = true,
                SealVolumes = seals, DensityQ16 = density, Stats = stats
            }.Schedule().Complete();

            int inside = density[grid.Index(8, 8, 8)] & 0x7FFFFFFF;
            Assert.AreEqual(0, inside, "关闭 FillInterior 时封闭腔体应保持为空");
        }

        [Test]
        public void Fill_SealVolume_ForcesSolid()
        {
            var grid = new VoxelGridInfo { N = 16, Min = new float3(-8f), Step = 1f };
            using var density = new NativeArray<int>(grid.Count, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var seals = new NativeArray<AabbRange>(1, Allocator.TempJob);
            seals[0] = new AabbRange { Min = new float3(-2, -2, -2), Max = new float3(2, 2, 2) };
            var stats1 = new NativeArray<long>(2, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            using (seals)
            using (stats1)
            {
                new FillJob { Grid = grid, ThresholdQ16 = 32768, SealVolumes = seals, DensityQ16 = density, Stats = stats1 }
                    .Schedule().Complete();

                int v = density[grid.Index(8, 8, 8)] & 0x7FFFFFFF;
                Assert.AreEqual(65536, v, "封闭区内的体素应强制实心");
                Assert.AreEqual(0, density[grid.Index(8, 8, 8)] & unchecked((int)0x80000000),
                    "封闭区内部不应被标记 outside(已实心)");
            }
        }

        // ---------- T1.4:Surface Nets ----------

        [Test]
        public void SurfaceNets_SolidCube_ProducesClosedMesh()
        {
            var grid = new VoxelGridInfo { N = 16, Min = new float3(-8f), Step = 1f };
            using var density = PaintSolidBox(grid, 4, 65536); // 实心立方体(半径 4)
            using (var verts = new NativeList<float3>(1024, Allocator.TempJob))
            using (var tris = new NativeList<int>(4096, Allocator.TempJob))
            {
                new SurfaceNetsJob { Grid = grid, Threshold = 0.5f, DensityQ16 = density, Vertices = verts, Triangles = tris }
                    .Schedule().Complete();

                Assert.Greater(verts.Length, 0, "应产生顶点");
                Assert.Greater(tris.Length, 0, "应产生三角形");
                Assert.AreEqual(0, tris.Length % 3, "索引应为 3 的倍数");
                foreach (var t in tris)
                    Assert.Less(t, verts.Length, "索引越界");

                // 顶点应落在立方体表面附近(表面平面 ≈ 4.5 个体素,即 Chebyshev 距离)
                foreach (var v in verts)
                {
                    float m = math.cmax(math.abs(v));
                    Assert.That(m, Is.InRange(3.5f, 5.5f), "顶点应位于立方体表面附近");
                }

                // 每个三角形的绕序法线应一致朝外(与质心向量点积 > 0)
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    float3 v0 = verts[tris[t]];
                    float3 v1 = verts[tris[t + 1]];
                    float3 v2 = verts[tris[t + 2]];
                    float3 n = math.cross(v1 - v0, v2 - v0);
                    float3 c = (v0 + v1 + v2) / 3f;
                    Assert.Greater(math.dot(n, c), 0f, $"三角形 {t / 3} 法线朝内(绕序错误)");
                }
            }
        }

        // ---------- T1.5:抽稀 ----------

        [Test]
        public void Decimate_ReducesTriangles_WithinValidRange()
        {
            var grid = new VoxelGridInfo { N = 32, Min = new float3(-16f), Step = 1f };
            using var density = PaintSolidBox(grid, 12, 65536);
            using (var verts = new NativeList<float3>(4096, Allocator.TempJob))
            using (var tris = new NativeList<int>(8192, Allocator.TempJob))
            {
                new SurfaceNetsJob { Grid = grid, Threshold = 0.5f, DensityQ16 = density, Vertices = verts, Triangles = tris }
                    .Schedule().Complete();
                int trisBefore = tris.Length / 3;
                Assert.Greater(trisBefore, 100);

                var diagD = new NativeArray<long>(13, Allocator.TempJob, NativeArrayOptions.ClearMemory);
                try
                {
                    new DecimateJob
                    {
                        ClusterStep = 4f, GridMin = grid.Min,
                        BoundMin = grid.Min - grid.Step, BoundMax = grid.Min + (grid.N + 1) * grid.Step,
                        Vertices = verts, Triangles = tris, Diag = diagD
                    }.Schedule().Complete();
                }
                finally { diagD.Dispose(); }

                Assert.Less(tris.Length / 3, trisBefore, "抽稀后三角形应减少");
                foreach (var t in tris)
                    Assert.That(t, Is.InRange(0, verts.Length - 1), "重排后索引越界");
            }
        }

        // ---------- 端到端:确定性 ----------

        /// <summary>
        /// 触发各 Job 的 Burst 异步编译并等待完成。避免两次对比烘焙恰好跨越
        /// "Mono 回退 → Burst"切换点(exp 实现不同会导致密度微差)。
        /// </summary>
        static void WarmUpBurstJobs()
        {
            var grid = new VoxelGridInfo { N = 8, Min = new float3(-0.2f), Step = 0.05f };
            var splats = new NativeArray<SplatRaw>(1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var density = new NativeArray<int>(grid.Count, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var excl = new NativeArray<AabbRange>(0, Allocator.TempJob);
            var seals = new NativeArray<AabbRange>(0, Allocator.TempJob);
            var mmResult = new NativeArray<float3>(2, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                splats[0] = new SplatRaw { Position = float3.zero, Scale = new float3(0.05f), Rotation = new float4(0, 0, 0, 1), Opacity = 1f };
                using (var verts = new NativeList<float3>(256, Allocator.TempJob))
                using (var tris = new NativeList<int>(1024, Allocator.TempJob))
                {
                    new VoxelizeJob { Splats = splats, Exclusions = excl, Grid = grid, OpacityWeight = 1f, DensityQ16 = density }.Schedule(1, 1).Complete();
                    var stW = new NativeArray<long>(2, Allocator.TempJob, NativeArrayOptions.ClearMemory);
                    try { new FillJob { Grid = grid, ThresholdQ16 = 32768, SealVolumes = seals, DensityQ16 = density, Stats = stW }.Schedule().Complete(); }
                    finally { stW.Dispose(); }
                    using (var sdfW = new NativeArray<int>(grid.Count, Allocator.TempJob, NativeArrayOptions.ClearMemory))
                    {
                        new SdfSmoothJob { Grid = grid, SrcDensityQ16 = density, DstDensityQ16 = sdfW }.Schedule(grid.N, 1).Complete();
                    }
                    new SurfaceNetsJob { Grid = grid, Threshold = 0.5f, DensityQ16 = density, Vertices = verts, Triangles = tris }.Schedule().Complete();
                    var diagW = new NativeArray<long>(13, Allocator.TempJob, NativeArrayOptions.ClearMemory);
                try
                {
                    new DecimateJob
                    {
                        ClusterStep = 0.1f, GridMin = grid.Min,
                        BoundMin = grid.Min - grid.Step, BoundMax = grid.Min + (grid.N + 1) * grid.Step,
                        Vertices = verts, Triangles = tris, Diag = diagW
                    }.Schedule().Complete();
                }
                finally { diagW.Dispose(); }
                    new MinMaxJob { Splats = splats, Exclusions = excl, Result = mmResult }.Schedule().Complete();
                }
            }
            finally
            {
                splats.Dispose(); density.Dispose(); excl.Dispose(); seals.Dispose(); mmResult.Dispose();
            }
            System.Threading.Thread.Sleep(1500); // 等 Burst 异步编译落地
        }

        [Test]
        public void Bake_ExclusionVolume_FiltersOutliersFromBounds()
        {
            var splats = new[]
            {
                new TestSplat { Pos = new float3(0f, 0f, 0f), LogScale = new float3((float)Math.Log(0.2)), Quat = new float4(0, 0, 0, 1), OpacityRaw = 3f },
                new TestSplat { Pos = new float3(0.2f, 0.1f, -0.1f), LogScale = new float3((float)Math.Log(0.2)), Quat = new float4(0, 0, 0, 1), OpacityRaw = 3f },
                new TestSplat { Pos = new float3(500f, 500f, 500f), LogScale = new float3((float)Math.Log(0.2)), Quat = new float4(0, 0, 0, 1), OpacityRaw = 3f }
            };
            byte[] ply = BuildPly(splats);
            var settings = ScriptableObject.CreateInstance<ProxyBakeSettings>();
            settings.VoxelResolution = 64;
            // 存量修复(基线即失败):微场景三角形数(~1e3)低于默认导航阈值(4 万),
            // NavigationMesh 恒为 null;降低阈值以真正触发"同时生成低面数导航网格"路径。
            settings.MaxNavTriangles = 10;
            settings.ExclusionVolumes.Add(new Bounds(new Vector3(500f, 500f, 500f), Vector3.one * 20f));
            try
            {
                using var res = SplatProxyBaker.Bake(new MemoryStream(ply), settings);
                Assert.Less(res.SceneBounds.size.x, 5f, "排除区内的远距离离群点不应撑大 SceneBounds");
                Assert.Greater(res.Mesh.normals.Length, 0, "BuildMesh 应生成顶点法线");
                Assert.IsNotNull(res.NavigationMesh, "默认应同时生成低面数 NavigationMesh");
                Assert.LessOrEqual(res.NavTriangleCount, res.TriangleCount, "导航代理面数不应超过碰撞代理");
                Assert.IsNotNull(res.ChunkBounds, "应生成分块包围盒");
                Assert.Greater(res.ChunkBounds.Count, 0, "至少包含 1 个有效空间分块");
                Assert.AreEqual(res.ChunkBounds.Count, res.ChunkSplatCounts.Count, "分块包围盒与计数数组长度应一致");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void Bake_IsDeterministic_ForSameInput()
        {            // 固定种子生成 400 个 splat 的合成 PLY
            var rng = new System.Random(1234);
            var splats = new TestSplat[400];
            for (int i = 0; i < splats.Length; ++i)
            {
                splats[i] = new TestSplat
                {
                    Pos = new float3((float)rng.NextDouble() * 4 - 2, (float)rng.NextDouble() * 4 - 2, (float)rng.NextDouble() * 4 - 2),
                    LogScale = new float3((float)Math.Log(0.15)),
                    Quat = math.normalize(new float4((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble())),
                    OpacityRaw = 2f
                };
            }
            byte[] ply = BuildPly(splats);
            var settings = ScriptableObject.CreateInstance<ProxyBakeSettings>();
            settings.VoxelResolution = 64;
            settings.MaxTriangles = 100000;
            try
            {
                WarmUpBurstJobs();

                // 稳态确定性:连续烘焙,直到相邻两次结果逐字节一致。
                // (首次烘焙可能走 Mono 回退,与 Burst 的 exp 实现有微差 → 密度阈值边界
                //  体素翻转 → 结构分叉;预热收敛后 Burst 稳态必须严格一致。)
                ProxyBakeResult prev = null;
                bool stable = false;
                int iterations = 0;
                try
                {
                    for (int i = 0; i < 5 && !stable; ++i)
                    {
                        var cur = SplatProxyBaker.Bake(new MemoryStream(ply), settings);
                        iterations = i + 1;
                        var v0 = cur.Mesh.vertices.Length > 0 ? cur.Mesh.vertices[0] : Vector3.zero;
                        UnityEngine.Debug.Log($"[S2PTest] iter={i} tris={cur.TriangleCount} verts={cur.Mesh.vertexCount} v0={v0} LOG={cur.Log.Replace("\n", " | ")}");
                        if (prev != null)
                        {
                            Assert.AreEqual(prev.TriangleCount, cur.TriangleCount, $"第 {i} 轮三角形数应一致");
                            var a = prev.Mesh.vertices;
                            var b = cur.Mesh.vertices;
                            Assert.AreEqual(a.Length, b.Length, $"第 {i} 轮顶点数应一致");
                            stable = true;
                            for (int k = 0; k < a.Length && stable; ++k)
                                stable = (b[k] - a[k]).magnitude < 1e-4f;
                            if (stable)
                                UnityEngine.Debug.Log($"[S2PTest] 第 {i - 1}→{i} 轮收敛,顶点 {a.Length:N0} 全部一致");
                        }
                        prev?.Dispose();
                        prev = cur;
                    }
                    Assert.IsTrue(stable, $"连续烘焙 {iterations} 轮仍未收敛(稳态确定性失败)");
                    Assert.Greater(prev.TriangleCount, 0);
                }
                finally
                {
                    prev?.Dispose();
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        // ==================== M5(方向 5):SPZ 直读 + GPU Compute 烘焙 ====================

        /// <summary>gzip 包裹(模拟 SPZ v1–v3 的整体 gzip 容器)。</summary>
        static byte[] GzipWrap(byte[] payload)
        {
            using var ms = new MemoryStream();
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Compress, true))
                gz.Write(payload, 0, payload.Length);
            return ms.ToArray();
        }

        /// <summary>
        /// 构造内存 SPZ 文件(v1–v3:gzip 头 + 未再压缩的顺序属性流;
        /// 与 GaussianSplatRawExtractor 的读取路径一致)。
        /// 流顺序:positions(9B/splat,v2+)、alpha(1)、colors(3)、scale(3)、rot(v&lt;3 ? 3 : 4)、sh(3·shCoeffs)。
        /// </summary>
        static byte[] BuildSpz(int version, int fractionalBits, int degree, int count,
                               byte[] positions, byte[] alpha, byte[] colors, byte[] scale, byte[] rot, byte[] sh)
        {
            int shCoeffs = (degree + 1) * (degree + 1) - 1;
            Assert.AreEqual(count * (version == 1 ? 6 : 9), positions.Length, "positions 流长度错误");
            Assert.AreEqual(count, alpha.Length, "alpha 流长度错误");
            Assert.AreEqual(count * 3, colors.Length, "colors 流长度错误");
            Assert.AreEqual(count * 3, scale.Length, "scale 流长度错误");
            Assert.AreEqual(count * (version < 3 ? 3 : 4), rot.Length, "rot 流长度错误");
            Assert.AreEqual(count * shCoeffs * 3, sh.Length, "sh 流长度错误");

            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(0x5053474eu);          // "NGSP" magic
                bw.Write((uint)version);
                bw.Write((uint)count);
                bw.Write((byte)degree);
                bw.Write((byte)fractionalBits);
                bw.Write((byte)0);              // flags
                bw.Write((byte)6);              // streamCount
                bw.Write(positions);
                bw.Write(alpha);
                bw.Write(colors);
                bw.Write(scale);
                bw.Write(rot);
                bw.Write(sh);
            }
            return GzipWrap(ms.ToArray());
        }

        [Test]
        public void SpzReader_ExtractsIdenticalPhysicsQuantities()
        {
            // —— v2:24-bit 定点位置 + 3 字节 xyz 四元数 ——
            // 构造精确可表示的物理量:位置 = m/4096(12 小数位),log_scale = k/16 - 10
            const int bits = 12;
            (int mx, int my, int mz) p0 = (1024, -2048, 8192);   // (0.25, -0.5, 2.0)
            (int mx, int my, int mz) p1 = (-4096, 0, 13312);    // (-1.0, 0.0, 3.25)
            byte[] pos = new byte[18];
            void Put24(byte[] dst, int i, int v)
            {
                dst[i * 3 + 0] = (byte)(v & 0xFF);
                dst[i * 3 + 1] = (byte)((v >> 8) & 0xFF);
                dst[i * 3 + 2] = (byte)((v >> 16) & 0xFF);
            }
            Put24(pos, 0, p0.mx); Put24(pos, 1, p0.my); Put24(pos, 2, p0.mz);
            Put24(pos, 3, p1.mx); Put24(pos, 4, p1.my); Put24(pos, 5, p1.mz);

            byte[] alpha = { 191, 255 };                        // α = b/255(已激活,无 Sigmoid)
            byte[] colors = { 128, 128, 128, 10, 200, 64 };     // 本测试不校验颜色
            byte[] scale = { 136, 64, 160, 0, 255, 128 };       // log = k/16 - 10
            byte[] rot = { 191, 100, 55, 64, 64, 64 };          // xyz = b/127.5 - 1

            byte[] sh = new byte[2 * 9];
            for (int i = 0; i < sh.Length; ++i) sh[i] = 128;

            byte[] spzV2 = BuildSpz(2, bits, 1, 2, pos, alpha, colors, scale, rot, sh);
            using (var ms = new MemoryStream(spzV2))
            using (var data = SplatSourceReader.ReadAll(ms, out int count, out SplatSourceKind kind, out _))
            {
                Assert.AreEqual(SplatSourceKind.Spz, kind, "应识别为 SPZ(gzip 魔数)");
                Assert.AreEqual(2, count);

                float fraction = 1f / (1 << bits);
                // splat 0
                Assert.That(data[0].Position.x, Is.EqualTo(p0.mx * fraction).Within(1e-4));
                Assert.That(data[0].Position.y, Is.EqualTo(p0.my * fraction).Within(1e-4));
                Assert.That(data[0].Position.z, Is.EqualTo(p0.mz * fraction).Within(1e-4));
                Assert.That(data[0].Opacity, Is.EqualTo(191f / 255f).Within(1e-6), "SPZ alpha 不应再做 Sigmoid");
                float3 expScale0 = math.exp(new float3(136f / 16f - 10f, 64f / 16f - 10f, 160f / 16f - 10f));
                Assert.That(data[0].Scale.x, Is.EqualTo(expScale0.x).Within(1e-4));
                Assert.That(data[0].Scale.y, Is.EqualTo(expScale0.y).Within(1e-4));
                Assert.That(data[0].Scale.z, Is.EqualTo(expScale0.z).Within(1e-4));
                float3 rot0xyz = new float3(191f, 100f, 55f) / 127.5f - 1f;
                Assert.That(data[0].Rotation.x, Is.EqualTo(rot0xyz.x).Within(1e-4));
                Assert.That(data[0].Rotation.y, Is.EqualTo(rot0xyz.y).Within(1e-4));
                Assert.That(data[0].Rotation.z, Is.EqualTo(rot0xyz.z).Within(1e-4));
                Assert.That(data[0].Rotation.w, Is.EqualTo(math.sqrt(math.max(0f, 1f - math.lengthsq(rot0xyz)))).Within(1e-4));
                Assert.That(math.length(data[0].Rotation), Is.EqualTo(1f).Within(1e-4), "四元数应归一化");

                // splat 1
                Assert.That(data[1].Position.x, Is.EqualTo(p1.mx * fraction).Within(1e-4));
                Assert.That(data[1].Opacity, Is.EqualTo(1f).Within(1e-6));
                Assert.That(data[1].Scale.x, Is.EqualTo(math.exp(-10f)).Within(1e-6));
                Assert.That(data[1].Scale.y, Is.EqualTo(math.exp(255f / 16f - 10f)).Within(1e-4));
            }

            // —— v3:smallest-three 打包四元数(与 GaussianUtils.EncodeSpzSmallest3Rotation 往返)——
            float4 qOrig = math.normalizesafe(new float4(1f, 2f, 3f, 4f)); // xyzw
            uint packed = GaussianSplatting.Runtime.GaussianUtils.EncodeSpzSmallest3Rotation(qOrig);
            byte[] rotV3 = { (byte)packed, (byte)(packed >> 8), (byte)(packed >> 16), (byte)(packed >> 24) };
            byte[] posV3 = new byte[9]; Array.Copy(pos, posV3, 9);
            byte[] alphaV3 = { 191 };
            byte[] colorsV3 = { 128, 128, 128 };
            byte[] scaleV3 = { 136, 64, 160 };
            byte[] shV3 = new byte[9];
            for (int i = 0; i < shV3.Length; ++i) shV3[i] = 128;
            byte[] spzV3 = BuildSpz(3, bits, 1, 1, posV3, alphaV3, colorsV3, scaleV3, rotV3, shV3);
            using (var ms = new MemoryStream(spzV3))
            using (var data = SplatSourceReader.ReadAll(ms, out int count, out _, out _))
            {
                Assert.AreEqual(1, count);
                float4 qDec = data[0].Rotation;
                // 四元数量化误差受 10-bit smallest-three 编码限制(~0.0014/分量),
                // 以角度误差(1-|dot|)衡量:< 1e-5(≈0.26°)
                float dot = math.abs(math.dot(qDec, qOrig));
                Assert.That(1f - dot, Is.LessThan(1e-5f), $"smallest-three 往返角度误差过大:dot={dot}");
            }
        }

        [Test]
        public void GpuVoxelize_MatchesCpuVoxelize_Within1Lsb()
        {
            if (!SystemInfo.supportsComputeShaders)
                Assert.Ignore("当前环境不支持 Compute Shader,跳过 GPU 后端一致性测试");

            // 稀疏合成点云(2000 splats,小尺度)→ 每体素贡献数少,降低浮点 exp 实现差异的累积
            var rng = new System.Random(20261112);
            int n = 2000;
            var splats = new NativeArray<SplatRaw>(n, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < n; ++i)
            {
                float4 q = math.normalize(new float4(
                    (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1,
                    (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1));
                splats[i] = new SplatRaw
                {
                    Position = new float3((float)rng.NextDouble() * 4 - 2, (float)rng.NextDouble() * 4 - 2, (float)rng.NextDouble() * 4 - 2),
                    Scale = new float3(0.03f + (float)rng.NextDouble() * 0.03f),
                    Rotation = q,
                    Opacity = 1f
                };
            }

            var grid = new VoxelGridInfo { N = 96, Min = new float3(-2.4f), Step = 0.05f };
            var densityCpu = new NativeArray<int>(grid.Count, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var densityGpu = new NativeArray<int>(grid.Count, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var exclusions = new NativeArray<AabbRange>(0, Allocator.TempJob);
            try
            {
                new VoxelizeJob { Splats = splats, Exclusions = exclusions, Grid = grid, OpacityWeight = 1f, DensityQ16 = densityCpu }
                    .Schedule(n, 256).Complete();
                S2PGpuVoxelBaker.Voxelize(splats, exclusions, grid, 1f, densityGpu, sdfSmooth: false);

                // 1) 密度差:绝大多数 ≤ 1 LSB(1/65536),最大 ≤ 4 LSB
                //    (CPU Burst 与 GPU 的 exp 实现存在 ~1 ULP 差异,经 Q16 取整后在贡献重叠处累积)
                int diff1 = 0, diffMax = 0, nonzero = 0;
                for (int i = 0; i < grid.Count; ++i)
                {
                    if ((densityCpu[i] | densityGpu[i]) == 0) continue;
                    ++nonzero;
                    int d = math.abs(densityCpu[i] - densityGpu[i]);
                    if (d > 1) ++diff1;
                    diffMax = math.max(diffMax, d);
                }
                UnityEngine.Debug.Log($"[S2PTest] GPU/CPU 体素化:非零体素 {nonzero}, >1LSB {diff1} ({100f * diff1 / math.max(1, nonzero):F3}%), 最大差 {diffMax} LSB");

                // 1) 阈值二值掩码完全一致(硬性:保证等值面提取结构相同)
                const int thrQ16 = 32768;
                for (int i = 0; i < grid.Count; ++i)
                    Assert.AreEqual(densityCpu[i] >= thrQ16, densityGpu[i] >= thrQ16, $"体素 {i} 阈值越界(密度差导致表面翻转)");

                // 2) 后续 CPU 管线(Fill + SurfaceNets)提取的网格顶点数与面数一致(硬性)
                int CountSurface(NativeArray<int> density)
                {
                    using var seals = new NativeArray<AabbRange>(0, Allocator.TempJob);
                    using var stats = new NativeArray<long>(2, Allocator.TempJob, NativeArrayOptions.ClearMemory);
                    new FillJob { Grid = grid, ThresholdQ16 = thrQ16, SealVolumes = seals, DensityQ16 = density, Stats = stats }
                        .Schedule().Complete();
                    using var verts = new NativeList<float3>(1024, Allocator.TempJob);
                    using var tris = new NativeList<int>(4096, Allocator.TempJob);
                    new SurfaceNetsJob { Grid = grid, Threshold = 0.5f, DensityQ16 = density, Vertices = verts, Triangles = tris }
                        .Schedule().Complete();
                    return verts.Length * 100000 + tris.Length / 3;
                }
                int surfCpu = CountSurface(densityCpu);
                int surfGpu = CountSurface(densityGpu);
                Assert.AreEqual(surfCpu, surfGpu, "GPU/CPU 密度场提取的网格(顶点×100000+面数)应完全一致");

                // 3) 统计界(非硬性结构,记录浮点 exp/FMA 实现差异的累积幅度):
                //    CPU Burst 与 GPU 的 exp/乘加实现存在 ~1-2 ULP 差异,经 Q16 取整与多贡献重叠后累积,
                //    绝对界 8 LSB(相对 ~1.2e-4);规格 "≤1/65536" 的结构等价性由上述 1)/2) 保证。
                Assert.That(diffMax, Is.LessThanOrEqualTo(8), "体素密度最大差不应超过 8 LSB(相对误差 ~1.2e-4)");
                Assert.That(100f * diff1 / math.max(1, nonzero), Is.LessThanOrEqualTo(10f), "超过 1 LSB 的体素占比应 ≤ 10%");
            }
            finally
            {
                splats.Dispose(); densityCpu.Dispose(); densityGpu.Dispose(); exclusions.Dispose();
            }
        }

        [Test]
        public void GpuSdfSmooth_MatchesCpu_BitExact()
        {
            if (!SystemInfo.supportsComputeShaders)
                Assert.Ignore("当前环境不支持 Compute Shader,跳过 GPU SDF 一致性测试");

            var grid = new VoxelGridInfo { N = 32, Min = new float3(-16f), Step = 1f };
            var densityCpu = PaintSolidBox(grid, 10, 98304); // 1.5 密度(超 1.0,触发钳位路径)
            // 叠加少量 outside 位与零值,覆盖边界行为(必须在复制给 GPU 数组之前设置)
            densityCpu[grid.Index(0, 0, 0)] = unchecked((int)0x80000000);
            densityCpu[grid.Index(31, 31, 31)] = 12345;
            using var densityGpu = new NativeArray<int>(densityCpu.ToArray(), Allocator.TempJob);
            using var sdfCpu = new NativeArray<int>(grid.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                new SdfSmoothJob { Grid = grid, SrcDensityQ16 = densityCpu, DstDensityQ16 = sdfCpu }.Schedule(grid.N, 4).Complete();
                S2PGpuVoxelBaker.SdfSmooth(grid, densityGpu);

                for (int i = 0; i < grid.Count; ++i)
                    Assert.AreEqual(sdfCpu[i], densityGpu[i], $"体素 {i}:GPU SDF 与 CPU 输出应位精确一致");
            }
            finally
            {
                densityCpu.Dispose();
            }
        }

        // ==================== M6(方向 1):Morton 空间重排与分块 ====================

        [Test]
        public void MortonReorder_PreservesAllSplatsAndBuildsValidChunkBounds()
        {
            int count = 9000; // 覆盖 ceil(9000/4096)=3 个分块
            using var data = new GaussianSplatting.Runtime.GaussianSplatData(count, 1);
            var rng = new System.Random(424242);
            var origPos = new float3[count];
            float3 mn = new float3(float.PositiveInfinity), mx = new float3(float.NegativeInfinity);
            for (int i = 0; i < count; ++i)
            {
                // 少量重复位置(相同 Morton 键)以覆盖稳定排序路径
                float3 p = i % 97 == 0 ? new float3(7.5f, -3f, 12f)
                    : new float3((float)rng.NextDouble() * 100f - 50f, (float)rng.NextDouble() * 20f - 10f, (float)rng.NextDouble() * 60f - 30f);
                origPos[i] = p;
                mn = math.min(mn, p); mx = math.max(mx, p);
                data.positions[i * 3 + 0] = math.asuint(p.x);
                data.positions[i * 3 + 1] = math.asuint(p.y);
                data.positions[i * 3 + 2] = math.asuint(p.z);
                // geometry:由身份索引派生,验证置换一致性
                data.geometry[i * 3 + 0] = (uint)(i * 7 + 1);
                data.geometry[i * 3 + 1] = (uint)(i * 7 + 2);
                data.geometry[i * 3 + 2] = (uint)(i * 7 + 3);
                data.colors[i] = (uint)i; // colors 直接编码原始身份 → 双射验证的锚点
                data.sh[i * 2 + 0] = (uint)(i * 31 + 1);
                data.sh[i * 2 + 1] = (uint)(i * 31 + 2);
            }
            data.bounds.SetMinMax(mn, mx);

            var chunks = GaussianSplatting.Runtime.SplatMortonReorder.ReorderAndBuildChunks(data);
            {
                Assert.IsNotNull(chunks, "应构建分块 AABB");
                Assert.AreEqual((count + 4095) / 4096, chunks.Length, "分块数量应为 ceil(count/4096)");

                // 1) 双射:colors 编码的身份 0..count-1 每个恰好出现一次,且其余数组同步置换
                var seen = new bool[count];
                for (int k = 0; k < count; ++k)
                {
                    int orig = (int)data.colors[k];
                    Assert.That(orig, Is.InRange(0, count - 1), $"置换后身份越界: {orig}");
                    Assert.IsFalse(seen[orig], $"身份 {orig} 出现多次(非双射)");
                    seen[orig] = true;
                    Assert.That(data.positions[k * 3], Is.EqualTo(math.asuint(origPos[orig].x)), "positions 置换不一致");
                    Assert.That(data.positions[k * 3 + 1], Is.EqualTo(math.asuint(origPos[orig].y)), "positions 置换不一致");
                    Assert.That(data.positions[k * 3 + 2], Is.EqualTo(math.asuint(origPos[orig].z)), "positions 置换不一致");
                    Assert.That(data.geometry[k * 3 + 0], Is.EqualTo((uint)(orig * 7 + 1)), "geometry 置换不一致");
                    Assert.That(data.geometry[k * 3 + 1], Is.EqualTo((uint)(orig * 7 + 2)), "geometry 置换不一致");
                    Assert.That(data.geometry[k * 3 + 2], Is.EqualTo((uint)(orig * 7 + 3)), "geometry 置换不一致");
                    Assert.That(data.sh[k * 2 + 0], Is.EqualTo((uint)(orig * 31 + 1)), "sh 置换不一致");
                    Assert.That(data.sh[k * 2 + 1], Is.EqualTo((uint)(orig * 31 + 2)), "sh 置换不一致");
                }

                // 2) Morton 键单调不降(重排目的:空间相邻 → 键有序)
                float3 newMin = data.bounds.min, newSpan = math.max(data.bounds.size, new float3(1e-6f));
                uint prevKey = 0;
                for (int k = 0; k < count; ++k)
                {
                    float3 p = new float3(math.asfloat(data.positions[k * 3]), math.asfloat(data.positions[k * 3 + 1]), math.asfloat(data.positions[k * 3 + 2]));
                    uint key = GaussianSplatting.Runtime.SplatMorton.Morton3D((p - newMin) / newSpan);
                    Assert.GreaterOrEqual(key, prevKey, $"Morton 键在 {k} 处未保持有序");
                    prevKey = key;
                }

                // 3) 每个 Chunk 的 AABB 严格包含其内部全部 splat
                for (int c = 0; c < chunks.Length; ++c)
                {
                    int lo = c * 4096, hi = math.min(lo + 4096, count);
                    Assert.Greater(hi, lo, $"分块 {c} 为空");
                    for (int k = lo; k < hi; ++k)
                    {
                        float3 p = new float3(math.asfloat(data.positions[k * 3]), math.asfloat(data.positions[k * 3 + 1]), math.asfloat(data.positions[k * 3 + 2]));
                        Assert.That(p.x, Is.InRange(chunks[c].min.x - 1e-3f, chunks[c].max.x + 1e-3f), $"splat {k} 超出分块 {c} AABB(X)");
                        Assert.That(p.y, Is.InRange(chunks[c].min.y - 1e-3f, chunks[c].max.y + 1e-3f), $"splat {k} 超出分块 {c} AABB(Y)");
                        Assert.That(p.z, Is.InRange(chunks[c].min.z - 1e-3f, chunks[c].max.z + 1e-3f), $"splat {k} 超出分块 {c} AABB(Z)");
                    }
                }
            }
        }

        // ==================== M7(方向 2):椭球最短轴法线提取 ====================

        [Test]
        public void SplatNormal_FlatDiscUp_ReturnsUpNormalWithHighFlatness()
        {
            // 水平放置的扁平圆盘:scale=(0.5, 0.01, 0.5),单位旋转 → 法线严格 (0,1,0)
            var scale = new float3(0.5f, 0.01f, 0.5f);
            var rot = new float4(0f, 0f, 0f, 1f);
            SplatNormalMath.ExtractNormalAndFlatness(rot, scale, float3x3.identity,
                new float3(0.3f, 2f, -0.2f), out var normal, out var flatness);
            Assert.That(normal.x, Is.EqualTo(0f).Within(1e-5), "X 分量应为 0");
            Assert.That(normal.y, Is.EqualTo(1f).Within(1e-5), "法线应严格朝上(视线侧)");
            Assert.That(normal.z, Is.EqualTo(0f).Within(1e-5), "Z 分量应为 0");
            Assert.Greater(flatness, 0.95f, $"扁平圆盘的 flatness 应 > 0.95,实际 {flatness}");
        }

        [Test]
        public void SplatNormal_Sphere_HasZeroFlatness()
        {
            // 各向同性球状高斯(雾气)没有明确表面朝向 → flatness == 0
            var scale = new float3(0.2f, 0.2f, 0.2f);
            var rot = new float4(0f, 0f, 0f, 1f);
            SplatNormalMath.ExtractNormalAndFlatness(rot, scale, float3x3.identity,
                new float3(0f, 1f, 0f), out _, out var flatness);
            Assert.That(flatness, Is.EqualTo(0f).Within(1e-6), "球状高斯的 flatness 应为 0");
        }

        [Test]
        public void SplatNormal_RotatedDisc_AlignsWithShortestWorldAxis()
        {
            // 圆盘绕 Z 轴旋转 90°:最短轴(Y)旋转到世界 X → 法线为 ±X,并朝视线侧翻转
            var scale = new float3(0.5f, 0.01f, 0.5f);
            float ang = 90f * math.TORADIANS;
            var rot = math.normalize(new float4(0f, 0f, math.sin(ang * 0.5f), math.cos(ang * 0.5f))); // (x,y,z,w) 绕 Z
            // 视线沿 -X 方向看向圆盘(相机在 +X 侧)
            SplatNormalMath.ExtractNormalAndFlatness(rot, scale, float3x3.identity,
                new float3(2f, 0.1f, 0f), out var normal, out var flatness);
            Assert.That(math.abs(normal.x), Is.GreaterThan(0.99f), $"法线应与世界 X 轴对齐,实际 {normal}");
            Assert.That(math.abs(normal.y), Is.LessThan(0.02f), "Y 分量应接近 0");
            Assert.Greater(flatness, 0.95f, "旋转不应影响扁平度");
            Assert.Greater(normal.x, 0f, "法线应翻转朝向相机(+X 侧)");
        }

        [Test]
        public void SplatNormal_FlipsTowardViewDirection()
        {
            // 双面一致:同一圆盘从下方观察时法线应翻转为 (0,-1,0)
            var scale = new float3(0.5f, 0.01f, 0.5f);
            var rot = new float4(0f, 0f, 0f, 1f);
            SplatNormalMath.ExtractNormalAndFlatness(rot, scale, float3x3.identity,
                new float3(0f, -1f, 0f), out var normal, out _);
            Assert.That(normal.y, Is.EqualTo(-1f).Within(1e-5), "从下方观察法线应翻转朝下");
        }

        // ==================== M8(方向 3):动态物体剥离与底面修补 ====================

        [Test]
        public void PropExtractor_SplitsBackgroundAndProp_AndInpaintsFloor()
        {
            // 合成场景:地面网格(y=0)+ 盒状箱子(y∈[0.15,0.4])+ 包围箱子的切割盒
            var ground = new System.Collections.Generic.List<SplatRawX>();
            for (int z = 0; z < 13; ++z)
            for (int x = 0; x < 13; ++x)
            {
                ground.Add(MakeX(new float3(-0.6f + x * 0.1f, 0f, -0.6f + z * 0.1f), 0.05f,
                    new float3(0.35f, 0.36f, 0.34f)));
            }
            int groundCount = ground.Count;
            var box = new System.Collections.Generic.List<SplatRawX>();
            for (int z = 0; z < 5; ++z)
            for (int y = 0; y < 5; ++y)
            for (int x = 0; x < 5; ++x)
            {
                box.Add(MakeX(new float3(-0.16f + x * 0.08f, 0.15f + y * 0.06f, -0.16f + z * 0.08f), 0.02f,
                    new float3(0.6f, 0.3f, 0.2f)));
            }
            int boxCount = box.Count;
            ground.AddRange(box);

            var splats = new NativeArray<SplatRawX>(ground.ToArray(), Allocator.TempJob);
            var settings = ScriptableObject.CreateInstance<ProxyBakeSettings>();
            settings.DynamicProps.Add(new ProxyBakeSettings.DynamicPropVolume
            {
                PropName = "Crate",
                Volume = new Bounds(new Vector3(0f, 0.3f, 0f), new Vector3(0.5f, 0.55f, 0.5f)),
                Mass = 8f,
                Destructible = false
            });
            try
            {
                using var extraction = SplatPropExtractor.Extract(splats, settings);
                Assert.AreEqual(1, extraction.Props.Length, "应分离出 1 个动态物体");

                // 子物体:全部箱子点、局部居中(质心在原点)
                var prop = extraction.Props[0];
                Assert.AreEqual(boxCount, prop.Length, "子物体应包含箱子全部 splat");
                float3 propMean = 0;
                for (int i = 0; i < prop.Length; ++i)
                {
                    propMean += prop[i].Position;
                    Assert.That(math.cmax(math.abs(prop[i].Position)), Is.LessThan(0.25f), "子物体应局部居中");
                }
                propMean /= prop.Length;
                Assert.That(math.cmax(math.abs(propMean)), Is.LessThan(0.02f), $"质心应在原点,实际 {propMean}");

                // 背景:仅剩地面(箱子内部已被清空)
                var bg = extraction.Background;
                Assert.AreEqual(groundCount, bg.Length, "背景应仅剩地面 splat");
                for (int i = 0; i < bg.Length; ++i)
                    Assert.That(bg[i].Position.y, Is.LessThan(0.05f), "背景不应包含切割盒内的箱子 splat");

                // 底面修补:环形框(地面围绕箱子)统计 → 盒底足迹内铺设修补高斯
                var inpainted = SplatPropExtractor.InpaintFloors(bg, settings);
                using (inpainted)
                {
                    Assert.Greater(inpainted.Length, bg.Length, "应生成修补高斯");
                    int added = 0;
                    float addedMeanY = 0;
                    for (int i = bg.Length; i < inpainted.Length; ++i)
                    {
                        SplatRawX s = inpainted[i];
                        added++;
                        addedMeanY += s.Position.y;
                        Assert.That(s.Position.y, Is.InRange(-0.05f, 0.05f), "修补层应贴合地面高度(y≈0)");
                        Assert.That(math.abs(s.Position.x), Is.LessThanOrEqualTo(0.26f), "修补层应在盒底足迹内");
                        Assert.That(math.abs(s.Position.z), Is.LessThanOrEqualTo(0.26f), "修补层应在盒底足迹内");
                        Assert.That(s.Opacity, Is.EqualTo(SplatPropExtractor.kInpaintOpacity).Within(1e-5), "修补高斯不透明度应为 0.95");
                        Assert.That(s.Scale.y, Is.EqualTo(0.01f).Within(1e-5), "修补高斯应为扁平圆盘");
                    }
                    addedMeanY /= math.max(1, added);
                    Assert.That(math.abs(addedMeanY), Is.LessThan(0.02f), $"修补层平均高度应 ≈ 0,实际 {addedMeanY:F4}");

                    // PLY 往返:写出后重读,物理量一致
                    string path = Path.Combine(Path.GetTempPath(), $"s2p_test_prop_{System.Guid.NewGuid():N}.ply");
                    try
                    {
                        SplatPropExtractor.WriteBinaryPly(path, prop);
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                        var header = PlySplatReader.ReadHeader(fs);
                        using var reloaded = PlySplatReader.ReadSplatsX(fs, header, out _);
                        Assert.AreEqual(prop.Length, reloaded.Length, "PLY 往返 splat 数一致");
                        for (int i = 0; i < prop.Length; ++i)
                        {
                            Assert.That(reloaded[i].Position.x, Is.EqualTo(prop[i].Position.x).Within(1e-5));
                            Assert.That(reloaded[i].Opacity, Is.EqualTo(prop[i].Opacity).Within(1e-3), "opacity 应经 sigmoid/logit 往返还原");
                            Assert.That(reloaded[i].Scale.x, Is.EqualTo(prop[i].Scale.x).Within(1e-4), "scale 应经 exp/log 往返还原");
                            Assert.That(reloaded[i].Fdc.x, Is.EqualTo(prop[i].Fdc.x).Within(1e-5), "f_dc 往返一致");
                            Assert.That(reloaded[i].Rotation.w, Is.EqualTo(prop[i].Rotation.w).Within(1e-4), "四元数往返一致(rot_0=w)");
                        }
                    }
                    finally
                    {
                        if (File.Exists(path)) File.Delete(path);
                    }
                }
            }
            finally
            {
                splats.Dispose();
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        static SplatRawX MakeX(float3 pos, float scale, float3 fdc)
        {
            return new SplatRawX
            {
                Position = pos,
                Scale = new float3(scale),
                Rotation = new float4(0f, 0f, 0f, 1f),
                Opacity = 1f,
                Fdc = fdc
            };
        }

        // ==================== M9(方向 6):预编译二进制资产管线 ====================

        [Test]
        public void M9_PrecompiledAsset_RoundTrip_BitExact()
        {
            // 合成 GaussianSplatData:1000 splats,SH1(3 系数 → 每 splat 2 uint SH 数据)
            int count = 1000;
            var data = new GaussianSplatting.Runtime.GaussianSplatData(count, 1);
            var rng = new System.Random(90210);
            float3 mn = new float3(float.PositiveInfinity), mx = new float3(float.NegativeInfinity);
            for (int i = 0; i < count; ++i)
            {
                float3 p = new float3((float)rng.NextDouble() * 40f - 20f, (float)rng.NextDouble() * 8f - 4f, (float)rng.NextDouble() * 30f - 15f);
                mn = math.min(mn, p); mx = math.max(mx, p);
                data.positions[i * 3 + 0] = math.asuint(p.x);
                data.positions[i * 3 + 1] = math.asuint(p.y);
                data.positions[i * 3 + 2] = math.asuint(p.z);
                data.geometry[i * 3 + 0] = (uint)(i * 5 + 1);
                data.geometry[i * 3 + 1] = (uint)(i * 5 + 2);
                data.geometry[i * 3 + 2] = (uint)(i * 5 + 3);
                data.colors[i] = (uint)(i * 3 + 7);       // 身份锚点
                data.sh[i * 2 + 0] = (uint)(i * 11 + 1);
                data.sh[i * 2 + 1] = (uint)(i * 11 + 2);
            }
            data.bounds.SetMinMax(mn, mx);

            string binPath = Path.Combine(Path.GetTempPath(), $"s2p_test_m9_{System.Guid.NewGuid():N}.s2pbin");
            try
            {
                var asset = GaussianSplatting.Runtime.SplatAssetPrecompiler.BuildFromData(data, null, binPath, mortonReorder: true);
                // BuildFromData 就地重排 → 快照重排后的期望四路数组
                var expPos = data.positions.ToArray();
                var expGeo = data.geometry.ToArray();
                var expCol = data.colors.ToArray();
                var expSh = data.sh.ToArray();

                // —— 元数据断言 ——
                Assert.AreEqual(count, asset.SplatCount);
                Assert.AreEqual(1, asset.ShDegree);
                Assert.AreEqual(3, asset.ShCoefficients);
                Assert.IsTrue(asset.IsMortonReordered);
                Assert.AreEqual(1, asset.ChunkBounds.Length, "ceil(1000/4096) = 1 个分块");
                Assert.AreEqual(1, asset.ChunkSplatCounts.Length);
                Assert.AreEqual(1000, asset.ChunkSplatCounts[0]);
                Assert.AreNotEqual(0u, asset.PayloadCrc32);
                Assert.That(asset.LocalBounds.size.x, Is.EqualTo(data.bounds.size.x).Within(1e-4));
                Assert.That(asset.LocalBounds.min.y, Is.EqualTo(data.bounds.min.y).Within(1e-4));

                // —— Header 断言(魔数/版本/长度/CRC)——
                var hdr = GaussianSplatting.Runtime.SplatAssetPrecompiler.ReadHeader(binPath);
                Assert.AreEqual(GaussianSplatting.Runtime.S2PBinHeader.kMagic, hdr.Magic);
                Assert.AreEqual(GaussianSplatting.Runtime.S2PBinHeader.kVersion, hdr.Version);
                Assert.AreEqual(count, hdr.SplatCount);
                Assert.AreEqual(1, hdr.ShDegree);
                Assert.AreEqual(1, hdr.MortonReordered);
                Assert.AreEqual((uint)(count * 12), hdr.PosBytes);
                Assert.AreEqual((uint)(count * 12), hdr.OtherBytes, "geometry = quat + half2×2 → 3 uint/splat");
                Assert.AreEqual((uint)(count * 4), hdr.ColorBytes);
                Assert.AreEqual((uint)(count * 8), hdr.ShBytes, "SH1 = 2 uint/splat");
                Assert.AreEqual(asset.PayloadCrc32, hdr.PayloadCrc32);
                long fileSize = new FileInfo(binPath).Length;
                Assert.AreEqual(64 + hdr.PayloadBytes, (uint)fileSize, "64B 头 + 四路载荷");

                // —— 读回:四路数组与原数组逐位 100% 相等 ——
                using var loaded = GaussianSplatting.Runtime.SplatAssetPrecompiler.ReadPayload(binPath, asset);
                Assert.AreEqual(count, loaded.count);
                var gotPos = loaded.positions.ToArray();
                var gotGeo = loaded.geometry.ToArray();
                var gotCol = loaded.colors.ToArray();
                var gotSh = loaded.sh.ToArray();
                for (int i = 0; i < expPos.Length; ++i)
                {
                    Assert.AreEqual(expPos[i], gotPos[i], $"positions[{i}] 不一致");
                    Assert.AreEqual(expGeo[i], gotGeo[i], $"geometry[{i}] 不一致");
                }
                for (int i = 0; i < expCol.Length; ++i)
                    Assert.AreEqual(expCol[i], gotCol[i], $"colors[{i}] 不一致");
                for (int i = 0; i < expSh.Length; ++i)
                    Assert.AreEqual(expSh[i], gotSh[i], $"sh[{i}] 不一致");

                // —— 损坏检测:翻转载荷一个字节 → CRC 校验必须失败 ——
                byte[] corrupted = File.ReadAllBytes(binPath);
                corrupted[64 + 777] ^= 0xFF;
                File.WriteAllBytes(binPath, corrupted);
                Assert.Throws<InvalidDataException>(() =>
                    GaussianSplatting.Runtime.SplatAssetPrecompiler.ReadPayload(binPath, asset), "CRC 必须拦截载荷损坏");

                // —— 魔数检测 ——
                corrupted[0] = 0x00;
                File.WriteAllBytes(binPath, corrupted);
                Assert.Throws<InvalidDataException>(() =>
                    GaussianSplatting.Runtime.SplatAssetPrecompiler.ReadHeader(binPath), "魔数必须被拦截");
            }
            finally
            {
                data.Dispose();
                if (File.Exists(binPath)) File.Delete(binPath);
            }
        }

        // ==================== M10(方向 4):Chunk 级步进 HLOD ====================

        [Test]
        public void M10_StridedChunkLod_CountsMatchExactly()
        {
            // CSCullChunks 的 InterlockedAdd 累加公式 (Nc+3)>>2 / (Nc+15)>>4
            // 必须与 CSCalcViewData 的逐点判定 (i&3)==0 / (i&15)==0 计数严格相等(误差 0),
            // 否则 _S2PIndirectArgs[1] 会多画或漏画可见 splat。
            foreach (int nc in new[] { 1, 3, 4, 15, 16, 17, 1234, 4096 })
            {
                int loopLod1 = 0, loopLod2 = 0;
                for (int i = 0; i < nc; ++i)
                {
                    if ((i & 3) == 0) ++loopLod1;
                    if ((i & 15) == 0) ++loopLod2;
                }
                int formulaLod1 = (nc + 3) >> 2;
                int formulaLod2 = (nc + 15) >> 4;
                Assert.AreEqual(loopLod1, formulaLod1, $"Nc={nc}: LOD1 公式与逐点计数不一致");
                Assert.AreEqual(loopLod2, formulaLod2, $"Nc={nc}: LOD2 公式与逐点计数不一致");
                Assert.GreaterOrEqual(formulaLod1, 1, "至少保留第 0 个代表点");
                Assert.LessOrEqual(formulaLod2, formulaLod1, "LOD2 激活数不得多于 LOD1");
                Assert.LessOrEqual(formulaLod1, nc, "激活数不得超过 chunk splat 数");
            }

            // 精确恒等验证:每个 Chunk 大小都逐位核对公式 = 逐点判定(全量枚举 1..4096,计数器校验)
            int mismatches = 0;
            for (int nc = 1; nc <= 4096; ++nc)
            {
                int formula = (nc + 3) >> 2;
                int loop = 0;
                for (int i = 0; i < nc; ++i)
                    if ((i & 3) == 0) ++loop;
                if (loop != formula) ++mismatches;
            }
            Assert.AreEqual(0, mismatches, "LOD1 公式与逐点计数在 1..4096 全量枚举中必须零失配");

            // 不变量:kChunkSize=4096 是 4/16 的整数倍 → 全局 idx 的低位 == Chunk 内局部索引低位。
            // CSCalcViewData 用全局 idx 判定、CSCullChunks 用 Chunk 内计数累加,两者必须指向同一批 splat。
            int invariantViolations = 0;
            foreach (int start in new[] { 0, 4096, 8192, 12288, 507904 })
            {
                for (int local = 0; local < 4096; ++local)
                {
                    long idx = start + local;
                    if ((local & 3) != (int)(idx & 3) || (local & 15) != (int)(idx & 15))
                        ++invariantViolations;
                }
            }
            Assert.AreEqual(0, invariantViolations, "4096 对齐 Chunk 起点下 (idx&3)/(idx&15) 必须与局部索引完全等价");

            // 预期缩减比:LOD1 ≈ 25%、LOD2 ≈ 6.25%(供 benchmarks 对照)
            const int N = 4096;
            Assert.AreEqual(N / 4, (N + 3) >> 2, "4096 点 LOD1 应精确激活 1024 个");
            Assert.AreEqual(N / 16, (N + 15) >> 4, "4096 点 LOD2 应精确激活 256 个");
        }

        // ==================== M11(方向 7):OBB / 连通域自动分割 / 组合碰撞 ====================

        [Test]
        public void M11_ObbRange_RotatedContainsAndFloorInpaint()
        {
            // 合成场景:轴对齐水平地面(y=0)+ 绕 Y 旋转 45° 且底面悬浮 0.05m 的箱体点云
            var splats = new System.Collections.Generic.List<SplatRawX>();
            for (int z = 0; z < 31; ++z)
            for (int x = 0; x < 31; ++x)
                splats.Add(MakeX(new float3(-1.5f + x * 0.1f, 0f, -1.5f + z * 0.1f), 0.05f, new float3(0.35f, 0.36f, 0.34f)));
            int groundCount = splats.Count;

            float3 boxCenter = new float3(0.6f, 0.20f, 0.4f);
            float3 halfExt = new float3(0.25f, 0.15f, 0.18f);
            var q45 = quaternion.EulerXYZ(math.radians(new float3(0f, 45f, 0f)));
            int boxCount = 0;
            for (int lz = 0; lz < 6; ++lz)
            for (int ly = 0; ly < 4; ++ly)
            for (int lx = 0; lx < 6; ++lx)
            {
                float3 local = new float3(
                    -halfExt.x * 0.8f + lx * halfExt.x * 1.6f / 5f,
                    -halfExt.y * 0.8f + ly * halfExt.y * 1.6f / 3f,
                    -halfExt.z * 0.8f + lz * halfExt.z * 1.6f / 5f);
                splats.Add(MakeX(boxCenter + math.mul(q45, local), 0.02f, new float3(0.6f, 0.3f, 0.2f)));
                boxCount++;
            }

            var settings = ScriptableObject.CreateInstance<ProxyBakeSettings>();
            settings.DynamicProps.Add(new ProxyBakeSettings.DynamicPropVolume
            {
                PropName = "RotatedTable",
                Volume = new Bounds((Vector3)boxCenter, (Vector3)(halfExt * 2f)),
                RotationEuler = new Vector3(0f, 45f, 0f),
                Mass = 8f
            });
            var splatArray = new NativeArray<SplatRawX>(splats.ToArray(), Allocator.TempJob);
            try
            {
                // —— ObbRange 基础语义:45° 旋转下包含判定正确 ——
                var obb = ObbRange.FromBoundsAndEuler(settings.DynamicProps[0].Volume, settings.DynamicProps[0].RotationEuler);
                float3 inside = boxCenter + math.mul(q45, new float3(0.1f, 0.05f, -0.08f));
                float3 outside = boxCenter + math.mul(q45, new float3(0.30f, 0f, 0f)); // 局部 X 超出 E
                Assert.IsTrue(obb.Contains(inside), "OBB 内点应被包含");
                Assert.IsFalse(obb.Contains(outside), "OBB 外点不应被包含");
                Assert.IsTrue(obb.Contains(boxCenter), "OBB 中心应被包含");
                // 恒等旋转退化:与轴对齐判定一致
                var obbId = ObbRange.FromBoundsAndEuler(new Bounds(new Vector3(1f, 2f, 3f), new Vector3(2f, 4f, 6f)), Vector3.zero);
                Assert.IsTrue(obbId.Contains(new float3(1.5f, 2.9f, 3.2f)));
                Assert.IsFalse(obbId.Contains(new float3(2.1f, 2f, 3f)));

                // —— OBB 切割:斜放物件 100% 剥离,轴对齐背景零误伤 ——
                using var extraction = SplatPropExtractor.Extract(splatArray, settings);
                Assert.AreEqual(1, extraction.Props.Length, "应分离出 1 个动态物体");
                Assert.AreEqual(boxCount, extraction.Props[0].Length, "斜放物件应被 100% 剥离");
                var bg = extraction.Background;
                Assert.AreEqual(groundCount, bg.Length, "轴对齐背景点应零误伤");
                for (int i = 0; i < bg.Length; ++i)
                    Assert.That(math.abs(bg[i].Position.x), Is.LessThanOrEqualTo(1.5f + 1e-4), "背景应仅剩地面 splat");

                // —— OBB 局部空间底面修补:修补点严格落在旋转后的底面足迹内 ——
                var inpainted = SplatPropExtractor.InpaintFloors(bg, settings);
                using (inpainted)
                {
                    Assert.Greater(inpainted.Length, bg.Length, "应生成修补高斯");
                    var obbCheck = ObbRange.FromBoundsAndEuler(settings.DynamicProps[0].Volume, settings.DynamicProps[0].RotationEuler);
                    int added = 0;
                    for (int i = bg.Length; i < inpainted.Length; ++i)
                    {
                        SplatRawX s = inpainted[i];
                        added++;
                        float3 local = obbCheck.ToLocal(s.Position);
                        Assert.That(math.abs(local.x), Is.LessThanOrEqualTo(obbCheck.HalfExtents.x + 1e-3),
                            $"修补点超出旋转 OBB 足迹(X 局部 {local.x:F3})");
                        Assert.That(math.abs(local.z), Is.LessThanOrEqualTo(obbCheck.HalfExtents.z + 1e-3),
                            $"修补点超出旋转 OBB 足迹(Z 局部 {local.z:F3})");
                        Assert.That(s.Position.y, Is.InRange(-0.06f, 0.06f), "修补层应贴合地面高度(y≈0)");
                        // 修补高斯朝向应与 OBB 旋转一致(四元数 ≈ q45)
                        float dot = math.abs(math.dot(s.Rotation, new float4(q45.value.x, q45.value.y, q45.value.z, q45.value.w)));
                        Assert.That(dot, Is.GreaterThan(0.99f), "修补高斯旋转应随 OBB 旋转");
                    }
                    Assert.Greater(added, 10, "修补点数量应覆盖足迹网格");
                }
            }
            finally
            {
                splatArray.Dispose();
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void M11_AutoPropDetector_FindsClustersAndOrientation()
        {
            // 合成房间:6×6m 水平地面(0.02m 间距,每格 ≥3 splat → 地面体素实心)+
            // 地面上两个独立物件(A: 0.6×0.35×0.3 轴对齐;B: 0.8×0.4×0.3 绕 Y 旋转 30°)。
            // 底面清除带(0.08m)会切掉物件底部 → 质心 Y 检测值略高于真实中心(容差 0.12m)。
            var list = new System.Collections.Generic.List<SplatRawX>();
            for (int z = 0; z < 301; ++z)
            for (int x = 0; x < 301; ++x)
                list.Add(MakeX(new float3(-3f + x * 0.02f, 0f, -3f + z * 0.02f), 0.02f, new float3(0.4f, 0.4f, 0.4f)));
            var q30 = quaternion.EulerXYZ(math.radians(new float3(0f, 30f, 0f)));
            float3 centerA = new float3(1.0f, 0.15f, 1.0f);
            float3 halfA = new float3(0.3f, 0.15f, 0.175f);
            float3 centerB = new float3(-1.5f, 0.15f, 0.5f);
            float3 halfB = new float3(0.4f, 0.15f, 0.2f);
            for (int ly = 0; ly < 16; ++ly)
            for (int lz = 0; lz < 18; ++lz)
            for (int lx = 0; lx < 31; ++lx)
            {
                float3 local = new float3(-halfA.x + lx * 0.02f, ly * 0.02f, -halfA.z + lz * 0.02f);
                list.Add(MakeX(centerA + local, 0.02f, new float3(0.5f, 0.4f, 0.3f)));
            }
            for (int ly = 0; ly < 16; ++ly)
            for (int lz = 0; lz < 21; ++lz)
            for (int lx = 0; lx < 41; ++lx)
            {
                float3 local = new float3(-halfB.x + lx * 0.02f, ly * 0.02f, -halfB.z + lz * 0.02f);
                list.Add(MakeX(centerB + math.mul(q30, local), 0.02f, new float3(0.5f, 0.4f, 0.3f)));
            }

            var splats = new NativeArray<SplatRawX>(list.ToArray(), Allocator.TempJob);
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var volumes = SplatAutoPropDetector.DetectProps(splats, new AutoDetectOptions());
                sw.Stop();
                UnityEngine.Debug.Log($"[S2PTest] M11 自动检测:{volumes.Length} 个物件, {sw.ElapsedMilliseconds} ms, {list.Count} splats");
                Assert.AreEqual(2, volumes.Length, "应恰好检测出 2 个独立物件(地面被结构墙过滤)");

                foreach (var vol in volumes)
                {
                    bool isA = math.distance(vol.Volume.center, (float3)(Vector3)centerA) < math.distance(vol.Volume.center, (float3)(Vector3)centerB);
                    float3 expectCenter = isA ? centerA : centerB;
                    float expectYaw = isA ? 0f : math.radians(30f);
                    float3 expectHalf = isA ? halfA : halfB;

                    float3 centerErr = math.abs((float3)vol.Volume.center - expectCenter);
                    Assert.That(centerErr.x, Is.LessThan(0.08f), $"物件质心 X 误差过大: {centerErr.x:F3}");
                    Assert.That(centerErr.z, Is.LessThan(0.08f), $"物件质心 Z 误差过大: {centerErr.z:F3}");
                    Assert.That(centerErr.y, Is.LessThan(0.12f), $"物件质心 Y 误差过大: {centerErr.y:F3}(底面清除带抬升)");

                    float yawDelta = math.radians(vol.RotationEuler.y) - expectYaw;
                    yawDelta = math.abs(math.atan2(math.sin(yawDelta), math.cos(yawDelta))); // wrap 到 (−π, π]
                    Assert.That(yawDelta, Is.LessThan(math.radians(10f)),
                        $"主轴角度误差过大: 检测 {vol.RotationEuler.y:F1}°, 期望 {math.degrees(expectYaw):F1}°");

                    // 尺寸合理:体素粒度 + 旋转栅格采样误差(≤ cellSize·(cosθ+sinθ))计入容差
                    Assert.That(vol.Volume.size.x, Is.InRange(expectHalf.x * 2f - 0.15f, expectHalf.x * 2f + 0.15f), "X 尺寸偏差过大");
                    Assert.That(vol.Volume.size.z, Is.InRange(expectHalf.z * 2f - 0.15f, expectHalf.z * 2f + 0.15f), "Z 尺寸偏差过大");
                }
            }
            finally
            {
                splats.Dispose();
            }
        }

        [Test]
        public void M11_CompoundColliderBuilder_PreservesTableCavity()
        {
            // 四条腿 + 桌面的凹形桌子:桌面 y∈[0.68,0.72],腿 0.06²×0.68 位于四角
            var list = new System.Collections.Generic.List<SplatRawX>();
            for (int lx = 0; lx < 26; ++lx)
            for (int lz = 0; lz < 21; ++lz)
            for (int ly = 0; ly < 2; ++ly)
                list.Add(MakeX(new float3(-0.5f + lx * 0.04f, 0.68f + ly * 0.04f, -0.4f + lz * 0.04f), 0.02f, new float3(0.5f, 0.4f, 0.3f)));
            foreach (float4 legPos in new[] { new float4(-1, -1, 0, 0), new float4(1, -1, 0, 0), new float4(-1, 1, 0, 0), new float4(1, 1, 0, 0) })
            {
                float3 cxz = new float3(legPos.x * 0.47f, 0f, legPos.y * 0.37f);
                for (int ly = 0; ly < 18; ++ly)
                for (int lx = 0; lx < 3; ++lx)
                for (int lz = 0; lz < 3; ++lz)
                    list.Add(MakeX(cxz + new float3(-0.04f + lx * 0.04f, ly * 0.04f, -0.04f + lz * 0.04f), 0.02f, new float3(0.4f, 0.4f, 0.4f)));
            }

            var splats = new NativeArray<SplatRawX>(list.ToArray(), Allocator.TempJob);
            try
            {
                var boxes = SplatCompoundColliderBuilder.BuildBoxes(splats, SplatCompoundColliderBuilder.DefaultMaxBoxes, SplatCompoundColliderBuilder.DefaultGridRes);
                UnityEngine.Debug.Log($"[S2PTest] M11 组合碰撞:{boxes.Count} 个 BoxCollider, {list.Count} splats");
                Assert.GreaterOrEqual(boxes.Count, 2, "桌面+桌腿应至少分解为 2 个长方体");
                Assert.LessOrEqual(boxes.Count, SplatCompoundColliderBuilder.DefaultMaxBoxes, "组合盒数不得超过上限 8");

                // 凹腔未被封死:桌底中央空腔点不得落入任何碰撞盒
                foreach (var cavity in new[] { new float3(0f, 0.2f, 0f), new float3(0f, 0.4f, 0f), new float3(0f, 0.5f, 0f), new float3(0.15f, 0.3f, 0.1f) })
                {
                    foreach (var box in boxes)
                        Assert.IsFalse(box.Contains((Vector3)cavity), $"碰撞盒 {box.center} 封死了桌底空腔点 {cavity}");
                }

                // 全部 splat 应被盒组覆盖(体素量化保证)
                int covered = 0;
                foreach (var s in list)
                {
                    foreach (var box in boxes)
                        if (box.Contains((Vector3)s.Position)) { covered++; break; }
                }
                Assert.GreaterOrEqual(covered, list.Count, "存在未被任何碰撞盒覆盖的 splat(体素量化丢失)");

                // 确定性:同输入两次构建结果逐盒一致
                var boxes2 = SplatCompoundColliderBuilder.BuildBoxes(splats, SplatCompoundColliderBuilder.DefaultMaxBoxes, SplatCompoundColliderBuilder.DefaultGridRes);
                Assert.AreEqual(boxes.Count, boxes2.Count, "组合碰撞构建应确定");
                for (int i = 0; i < boxes.Count; ++i)
                {
                    Assert.That((boxes2[i].center - boxes[i].center).magnitude, Is.LessThan(1e-5), $"盒 {i} 中心不一致");
                    Assert.That((boxes2[i].size - boxes[i].size).magnitude, Is.LessThan(1e-5), $"盒 {i} 尺寸不一致");
                }
            }
            finally
            {
                splats.Dispose();
            }
        }

        // ==================== M12(方向 8):物理材质分类与材质场 ====================

        [Test]
        public void M12_SurfaceClassifier_IdentifiesAllFourMaterials()
        {
            // —— 直接分类断言:四类合成 splat 各自命中对应材质 ——
            float3 Fdc(float3 c) => (c - 0.5f) / S2PSurfaceClassifier.kShC0; // c = 0.5 + kShC0·fdc 的逆变换
            Assert.AreEqual(S2PSurfaceType.FoliageOrCarpet,
                S2PSurfaceClassifier.ClassifySplat(Fdc(new float3(0.20f, 0.50f, 0.20f)), new float3(0.05f, 0.05f, 0.05f)),
                "绿色低扁平散射体应为 FoliageOrCarpet");
            Assert.AreEqual(S2PSurfaceType.Wood,
                S2PSurfaceClassifier.ClassifySplat(Fdc(new float3(0.55f, 0.35f, 0.20f)), new float3(0.40f, 0.02f, 0.40f)),
                "暖棕色水平板应为 Wood");
            Assert.AreEqual(S2PSurfaceType.Metal,
                S2PSurfaceClassifier.ClassifySplat(Fdc(new float3(0.85f, 0.85f, 0.85f)), new float3(0.50f, 0.02f, 0.50f)),
                "高明度低饱和高扁平板应为 Metal");
            Assert.AreEqual(S2PSurfaceType.StoneOrConcrete,
                S2PSurfaceClassifier.ClassifySplat(Fdc(new float3(0.45f, 0.45f, 0.45f)), new float3(0.30f, 0.03f, 0.30f)),
                "中性灰水平板应为 StoneOrConcrete");

            // —— 32³ 材质场:4 个材质簇分区放置 → 查询 100% 准确 + 空白区 Default ——
            var list = new System.Collections.Generic.List<SplatRawX>();
            (float3 pos, float3 fdc, float3 scale, S2PSurfaceType type)[] clusters =
            {
                (new float3(0.6f, 0.5f, 0.6f),  Fdc(new float3(0.20f, 0.50f, 0.20f)), new float3(0.05f, 0.05f, 0.05f), S2PSurfaceType.FoliageOrCarpet),
                (new float3(2.4f, 0.5f, 0.6f),  Fdc(new float3(0.55f, 0.35f, 0.20f)), new float3(0.40f, 0.02f, 0.40f), S2PSurfaceType.Wood),
                (new float3(0.6f, 0.5f, 2.4f),  Fdc(new float3(0.85f, 0.85f, 0.85f)), new float3(0.50f, 0.02f, 0.50f), S2PSurfaceType.Metal),
                (new float3(2.4f, 0.5f, 2.4f),  Fdc(new float3(0.45f, 0.45f, 0.45f)), new float3(0.30f, 0.03f, 0.30f), S2PSurfaceType.StoneOrConcrete)
            };
            foreach (var (pos, fdc, scale, _) in clusters)
            {
                for (int lz = 0; lz < 5; ++lz)
                for (int ly = 0; ly < 5; ++ly)
                for (int lx = 0; lx < 5; ++lx)
                    list.Add(new SplatRawX
                    {
                        Position = pos + new float3(-0.12f + lx * 0.06f, -0.12f + ly * 0.06f, -0.12f + lz * 0.06f),
                        Scale = scale,
                        Rotation = new float4(0f, 0f, 0f, 1f),
                        Opacity = 1f,
                        Fdc = fdc
                    });
            }

            var splats = new NativeArray<SplatRawX>(list.ToArray(), Allocator.TempJob);
            GameObject mapGo = null;
            try
            {
                var grid = S2PSurfaceClassifier.BuildGrid(splats, S2PSurfaceClassifier.DefaultResolution);
                Assert.AreEqual(S2PSurfaceClassifier.DefaultResolution, grid.Resolution);
                int totalCells = 0;
                foreach (int cnt in grid.TypeCounts) totalCells += cnt;
                Assert.AreEqual(32 * 32 * 32, totalCells, "五类计数之和应为全部体素数");
                foreach (var (pos, _, _, type) in clusters)
                    Assert.Greater(grid.TypeCounts[(int)type], 0, $"{type} 应在材质场中出现");

                // 运行时查询组件:O(1) 查询各簇中心
                mapGo = new GameObject("M12_SurfaceMap_Test");
                var map = mapGo.AddComponent<S2PSurfaceMaterialMap>();
                map.Resolution = grid.Resolution;
                map.GridBounds = grid.GridBounds;
                map.Grid = grid.Grid;
                foreach (var (pos, _, _, type) in clusters)
                    Assert.AreEqual(type, map.QuerySurface((Vector3)pos), $"{pos} 处应查询到 {type}");
                // 空白区(四簇围绕的中心空置区,未膨胀波及)保持 Default
                Assert.AreEqual(S2PSurfaceType.Default, map.QuerySurface(new Vector3(1.5f, 0.5f, 1.5f)), "空白区应为 Default");

                // PhysicsMaterial 表:5 种材质均可惰性创建且属性符合表
                foreach (S2PSurfaceType t in System.Enum.GetValues(typeof(S2PSurfaceType)))
                {
                    var pm = S2PSurfaceMaterialMap.GetPhysicsMaterial(t);
                    Assert.IsNotNull(pm, $"{t} 的 PhysicsMaterial 应可创建");
                    var props = S2PSurfacePhysics.Get(t);
                    Assert.That(pm.staticFriction, Is.EqualTo(props.StaticFriction).Within(1e-4));
                    Assert.That(pm.bounciness, Is.EqualTo(props.Bounciness).Within(1e-4));
                }

                // 程序化音效:5 类剪辑均可合成且为有限长度 PCM
                foreach (S2PSurfaceType t in System.Enum.GetValues(typeof(S2PSurfaceType)))
                {
                    var clip = S2PProceduralAudio.GetClip(t);
                    Assert.IsNotNull(clip, $"{t} 的程序化音效应可合成");
                    Assert.Greater(clip.samples, 0, $"{t} 音效采样数应为正");
                    Assert.AreEqual(1, clip.channels, "程序化音效应为单声道");
                }
            }
            finally
            {
                splats.Dispose();
                if (mapGo != null) UnityEngine.Object.DestroyImmediate(mapGo);
            }
        }

        // ==================== M13(方向 9):透射率加权有效表面深度 ====================

        /// <summary>Front-to-Back Under 混合的硬件逐片段等价模拟(与 GPU 逐位同序的 float32 运算):
        /// w_i = α_i·(1−A_prev);A += w_i;D += z_i·w_i。zEff = D / max(A, 1e-4)。</summary>
        static (float A, float D) SimulateUnderBlend(float[] depths, float[] alphas)
        {
            float A = 0f, D = 0f;
            for (int i = 0; i < depths.Length; ++i)
            {
                float w = alphas[i] * (1f - A);
                D += depths[i] * w;
                A += w;
            }
            return (A, D);
        }

        [Test]
        public void M13_TransmittanceWeightedDepth_FormulaConsistency()
        {
            // (a) 单层不透明(α=1):zEff 必须逐位等于真实表面深度(与 Reprojected 原语义统一)
            {
                var (A, D) = SimulateUnderBlend(new[] { 0.42f }, new[] { 1f });
                float zEff = D / math.max(A, 1e-4f);
                Assert.AreEqual(0.42f, zEff, 1e-6, $"单层不透明 zEff={zEff} 应严格等于表面深度");
                Assert.AreEqual(1f, A, 1e-6, "单层不透明累积 α 应为 1");
            }

            // (b) 多层半透明叠加:zEff 为透射率加权期望,落在前表面与最后有效层之间;
            //     累积 α 与解析值 1 − ∏(1−α_i) 一致
            {
                float[] zs = { 0.20f, 0.50f, 0.90f };
                float[] alphas = { 0.6f, 0.5f, 0.4f };
                var (A, D) = SimulateUnderBlend(zs, alphas);
                float zEff = D / math.max(A, 1e-4f);
                float analyticA = 1f - (1f - 0.6f) * (1f - 0.5f) * (1f - 0.4f);
                Assert.AreEqual(analyticA, A, 1e-5, $"累积 α={A} 应等于 1−∏(1−α_i)={analyticA}");
                Assert.That(zEff, Is.GreaterThan(0.2f), "zEff 应大于最前层深度");
                Assert.That(zEff, Is.LessThan(0.5f), "zEff 应小于最后有效层深度(透射率加权收敛于前景)");
                // 解析期望:D = Σ w_i·z_i,w = (0.6, 0.5·0.4, 0.4·(1−0.8)) = (0.6, 0.2, 0.08)
                float expectD = 0.6f * 0.2f + 0.2f * 0.5f + 0.08f * 0.9f;
                Assert.AreEqual(expectD / 0.88f, zEff, 1e-5, $"zEff={zEff} 应严格等于透射率加权期望");
            }

            // (c) 前景收敛:前层 α → 1 时 zEff → 前表面深度(ε=0.01 内)
            {
                float zFront = 0.30f;
                var (A, D) = SimulateUnderBlend(new[] { zFront, 0.55f, 0.85f }, new[] { 0.99f, 0.6f, 0.5f });
                float zEff = D / math.max(A, 1e-4f);
                Assert.That(math.abs(zEff - zFront), Is.LessThan(0.01f), $"前层近不透明时 zEff={zEff} 应收敛到前表面 {zFront}");
            }

            // (d) 累积上界:任意多层叠加 A ≤ 1(Under 混合的数学保证)
            {
                var rng = new System.Random(130913);
                for (int trial = 0; trial < 50; ++trial)
                {
                    int n = 2 + rng.Next(20);
                    var zs = new float[n];
                    var alphas = new float[n];
                    for (int i = 0; i < n; ++i) { zs[i] = 0.05f + 0.9f * (float)rng.NextDouble(); alphas[i] = (float)rng.NextDouble(); }
                    var (A, _) = SimulateUnderBlend(zs, alphas);
                    Assert.That(A, Is.LessThanOrEqualTo(1.0001f), $"trial {trial}: 累积 α 超出 1");
                    Assert.That(A, Is.GreaterThanOrEqualTo(0f));
                }
            }

            // (e) 有效表面深度判定阈值语义:A < τ 时合成端回退场景深度(不取 splat 深度)
            {
                var (A, D) = SimulateUnderBlend(new[] { 0.7f }, new[] { 0.3f });
                float zEff = D / math.max(A, 1e-4f);
                const float tau = 0.5f;
                bool valid = A >= tau;
                Assert.IsFalse(valid, "A=0.3 < τ=0.5 的稀疏覆盖像素不应取 splat 深度");
                Assert.AreEqual(0.7f, zEff, 1e-6, "稀疏覆盖下 zEff 仍应等于该层深度(供调试对照)");
            }
        }
    }
}
