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

            new FillJob { Grid = grid, ThresholdQ16 = 32768, SealVolumes = seals, DensityQ16 = density }
                .Schedule().Complete();

            int inside = density[grid.Index(8, 8, 8)];
            Assert.AreEqual(65536, inside, "封闭腔体应被填实");

            int outside = density[grid.Index(1, 1, 1)];
            Assert.AreEqual(unchecked((int)0x80000000), outside, "外部空气应带 outside 标记且密度为 0");

            int shell = density[grid.Index(8, 8, 13)] & 0x7FFFFFFF;
            Assert.AreEqual(65536, shell, "壳体保持实心且不被标记 outside");
        }

        [Test]
        public void Fill_SealVolume_ForcesSolid()
        {
            var grid = new VoxelGridInfo { N = 16, Min = new float3(-8f), Step = 1f };
            using var density = new NativeArray<int>(grid.Count, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var seals = new NativeArray<AabbRange>(1, Allocator.TempJob);
            seals[0] = new AabbRange { Min = new float3(-2, -2, -2), Max = new float3(2, 2, 2) };
            using (seals)
            {
                new FillJob { Grid = grid, ThresholdQ16 = 32768, SealVolumes = seals, DensityQ16 = density }
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

                new DecimateJob { ClusterStep = 4f, GridMin = grid.Min, Vertices = verts, Triangles = tris }
                    .Schedule().Complete();

                Assert.Less(tris.Length / 3, trisBefore, "抽稀后三角形应减少");
                foreach (var t in tris)
                    Assert.That(t, Is.InRange(0, verts.Length - 1), "重排后索引越界");
            }
        }

        // ---------- 端到端:确定性 ----------

        [Test]
        public void Bake_IsDeterministic_ForSameInput()
        {
            // 固定种子生成 400 个 splat 的合成 PLY
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
                using (var ms1 = new MemoryStream(ply))
                using (var first = SplatProxyBaker.Bake(ms1, settings))
                using (var ms2 = new MemoryStream(ply))
                using (var second = SplatProxyBaker.Bake(ms2, settings))
                {
                    Assert.Greater(first.TriangleCount, 0);
                    Assert.AreEqual(first.TriangleCount, second.TriangleCount, "三角形数应一致");
                    var v1 = first.Mesh.vertices;
                    var v2 = second.Mesh.vertices;
                    Assert.AreEqual(v1.Length, v2.Length);
                    for (int i = 0; i < v1.Length; ++i)
                        Assert.That((v2[i] - v1[i]).magnitude, Is.LessThan(1e-4f), $"顶点 {i} 不一致(确定性失败)");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }
    }
}
