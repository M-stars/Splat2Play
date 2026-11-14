// Splat2Play v3 — M11(方向 7)T11.4:3D 连通域 + XZ-PCA 自动物件检测
// 纯确定性 CPU/Burst 单线程管线(124 万点 < 120ms):
//   1. [2%,98%] 分位包络(逐轴 8192-bin 直方图)→ N³ 占用统计(α ≥ 0.2 的 splat 计数,≥3 记为实心);
//   2. 主地面高度 y_floor:下半区逐层实心体素数 H(y),取满足 H(y) ≥ 0.15·maxH 的最低层;
//   3. 候选掩码:[y_floor + ⌈FloorClearance/stepY⌉, y_floor + ⌊CeilingHeight/stepY⌋](带外实心体素清零);
//   4. 3D 6-邻域 BFS 连通分量标记(固定线性扫描顺序,确定性);
//   5. 簇级过滤:触碰水平边界(x≤1 / x≥N-2 / z≤1 / z≥N-2)或水平跨度 > 45% 场景宽 → 结构墙剔除;
//      体积/点数约束(MinPropVolumeM3..MaxPropVolumeM3, MinSplatCount);
//   6. XZ 2D PCA 主轴(米制空间):θ = ½·atan2(2Σxz, Σxx − Σzz);对 ±θ 两个候选分别求旋转系
//      min/max,取旋转包围盒面积更小者(确定性消解 PCA 符号歧义),外扩半个体素步长得 OBB。
// 输出:带 RotationEuler=(0, deg(θ), 0) 的 DynamicPropVolume 列表(Volume.center/extents 在 OBB 局部系解释)。
// SPDX-License-Identifier: MIT

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Splat2Play.Runtime
{
    /// <summary>自动物件检测参数(规格 §4.2(2) 默认值)。</summary>
    [Serializable]
    public sealed class AutoDetectOptions
    {
        public int GridResolution = 96;
        [UnityEngine.Range(0f, 1f)] public float MinOpacity = 0.2f;
        public int MinSplatsPerCell = 3;
        public float MinPropVolumeM3 = 0.015f;
        public float MaxPropVolumeM3 = 3.0f;
        public int MinSplatCount = 200;
        public float FloorClearance = 0.08f;
        public float CeilingHeight = 2.0f;
        [UnityEngine.Range(0f, 1f)] public float StructureSpanRatio = 0.45f;
    }

    /// <summary>检测结果:OBB 参数 + 统计(前 OutCount 项有效)。</summary>
    public struct AutoDetectedProp
    {
        public float3 Center;         // 扫描资产局部空间 OBB 中心(米)
        public float YawRadians;      // OBB 水平旋转角(Unity Y 轴欧拉)
        public float3 HalfExtents;    // OBB 半尺寸(旋转系,米)
        public int SplatCount;        // 簇内 splat 总数
        public int VoxelCount;        // 簇内实心体素数
        public float VolumeM3;        // OBB 体积(m³)
    }

    public static class SplatAutoPropDetector
    {
        /// <summary>单次检测簇数上限(防止病态输入爆内存;按簇序截断,确定性)。</summary>
        public const int MaxClusters = 256;

        /// <summary>
        /// 自动检测独立物件。返回长度 = 命中数的 DynamicPropVolume 数组
        /// (PropName = AutoProp{i},Volume/RotationEuler 构成 OBB,Mass=5kg,ColliderMode=CompoundBoxes)。
        /// </summary>
        public static ProxyBakeSettings.DynamicPropVolume[] DetectProps(NativeArray<SplatRawX> splats, AutoDetectOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var job = CreateJob(splats, options, Allocator.TempJob);
            try
            {
                job.handle.Complete();
                int count = job.count[0];
                var result = new ProxyBakeSettings.DynamicPropVolume[count];
                for (int i = 0; i < count; ++i)
                {
                    AutoDetectedProp p = job.props[i];
                    result[i] = new ProxyBakeSettings.DynamicPropVolume
                    {
                        PropName = $"AutoProp{i}",
                        Volume = new Bounds((Vector3)p.Center, (Vector3)(p.HalfExtents * 2f)),
                        RotationEuler = new Vector3(0f, math.degrees(p.YawRadians), 0f),
                        ColliderMode = PropColliderMode.CompoundBoxes,
                        Mass = 5f,
                        Destructible = false
                    };
                }
                return result;
            }
            finally
            {
                job.count.Dispose();
                job.props.Dispose();
            }
        }

        /// <summary>检测原始输出(验证脚本使用;调用方须 Complete 并 Dispose)。</summary>
        internal static (NativeArray<int> count, NativeArray<AutoDetectedProp> props, JobHandle handle)
            DetectPropsRaw(NativeArray<SplatRawX> splats, AutoDetectOptions options, Allocator allocator)
        {
            var job = CreateJob(splats, options, allocator);
            return (job.count, job.props, job.handle);
        }

        static (NativeArray<int> count, NativeArray<AutoDetectedProp> props, JobHandle handle)
            CreateJob(NativeArray<SplatRawX> splats, AutoDetectOptions options, Allocator allocator)
        {
            var job = new AutoDetectJob
            {
                Splats = splats,
                N = Mathf.Clamp(options.GridResolution, 16, 256),
                MinOpacity = options.MinOpacity,
                MinSplatsPerCell = options.MinSplatsPerCell,
                MinPropVolumeM3 = options.MinPropVolumeM3,
                MaxPropVolumeM3 = options.MaxPropVolumeM3,
                MinSplatCount = options.MinSplatCount,
                FloorClearance = options.FloorClearance,
                CeilingHeight = options.CeilingHeight,
                StructureSpanRatio = options.StructureSpanRatio,
                PercentileBins = 8192,
                OutCount = new NativeArray<int>(1, allocator, NativeArrayOptions.ClearMemory),
                OutProps = new NativeArray<AutoDetectedProp>(MaxClusters, allocator, NativeArrayOptions.ClearMemory)
            };
            return (job.OutCount, job.OutProps, job.Schedule());
        }
    }

    /// <summary>单线程 Burst 主作业:分位包络 → 占用 → 地面 → CCL → PCA → OBB 输出。</summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    struct AutoDetectJob : IJob
    {
        [ReadOnly] public NativeArray<SplatRawX> Splats;
        public int N;                       // 网格分辨率(每轴)
        public float MinOpacity;
        public int MinSplatsPerCell;
        public float MinPropVolumeM3;
        public float MaxPropVolumeM3;
        public int MinSplatCount;
        public float FloorClearance;
        public float CeilingHeight;
        public float StructureSpanRatio;
        public int PercentileBins;          // 分位数直方图 bin 数(每轴)
        public NativeArray<int> OutCount;   // 长度 1
        public NativeArray<AutoDetectedProp> OutProps; // MaxClusters

        struct ClusterAcc
        {
            public long VoxelCount;
            public long SplatCount;
            public double SumXm, SumZm;               // 体素中心(米)和
            public double SumXm2, SumZm2, SumXmZm;    // 米制二阶矩(PCA 用)
            public double SumYm;                      // y 中心和
            public int MinCellX, MinCellZ, MaxCellX, MaxCellZ; // AABB(格坐标,边界/跨度判定)
        }

        public void Execute()
        {
            int n = N;
            int total = n * n * n;
            int n2 = n * n;

            // —— 1a. 绝对包络 ——
            float3 mn = new float3(float.PositiveInfinity), mx = new float3(float.NegativeInfinity);
            for (int i = 0; i < Splats.Length; ++i)
            {
                float3 p = Splats[i].Position;
                mn = math.min(mn, p);
                mx = math.max(mx, p);
            }
            if (math.any(mn > mx)) return; // 空点云

            // —— 1b. [2%,98%] 分位包络(逐轴直方图,确定性)——
            float3 lo = new float3(), hi = new float3();
            {
                var hist = new NativeArray<long>(PercentileBins, Allocator.Temp, NativeArrayOptions.ClearMemory);
                for (int axis = 0; axis < 3; ++axis)
                {
                    float aMin = axis == 0 ? mn.x : axis == 1 ? mn.y : mn.z;
                    float aMax = axis == 0 ? mx.x : axis == 1 ? mx.y : mx.z;
                    float span = math.max(aMax - aMin, 1e-6f);
                    float inv = PercentileBins / span;
                    for (int b = 0; b < PercentileBins; ++b) hist[b] = 0;
                    for (int i = 0; i < Splats.Length; ++i)
                    {
                        float v = axis == 0 ? Splats[i].Position.x : axis == 1 ? Splats[i].Position.y : Splats[i].Position.z;
                        hist[math.clamp((int)((v - aMin) * inv), 0, PercentileBins - 1)]++;
                    }
                    lo[axis] = PercentileAt(hist, aMin, span, 0.02);
                    hi[axis] = PercentileAt(hist, aMin, span, 0.98);
                }
                hist.Dispose();
            }
            float3 span3 = math.max(hi - lo, new float3(1e-3f));
            hi = lo + span3;
            // 各向同性体素(最大轴跨度/N):保证"每格 ≥ MinSplatsPerCell 即实心"的占用阈值
            // 在任意场景纵横比下都有均匀的点密度语义(细高/扁平场景下 y 轴过细会使阈值失效)
            float cellSizeMax = math.cmax(span3) / n;
            float3 cellSize = new float3(cellSizeMax);
            float3 invStep = 1f / cellSize;

            // —— 2. 占用统计 ——
            var cellCounts = new NativeArray<int>(total, Allocator.Temp, NativeArrayOptions.ClearMemory);
            for (int i = 0; i < Splats.Length; ++i)
            {
                if (Splats[i].Opacity < MinOpacity) continue;
                float3 p = Splats[i].Position;
                int3 c = (int3)math.clamp(math.floor((p - lo) * invStep), 0, n - 1);
                cellCounts[c.x + n * c.y + n2 * c.z]++;
            }
            var occupied = new NativeArray<byte>(total, Allocator.Temp, NativeArrayOptions.ClearMemory);
            for (int i = 0; i < total; ++i)
                occupied[i] = cellCounts[i] >= MinSplatsPerCell ? (byte)1 : (byte)0;

            // —— 3. 主地面高度:下半区逐层实心体素数,取满足 0.15·max 的最低层 ——
            int floorCell = 0;
            {
                int half = math.max(n / 2, 1);
                var layer = new NativeArray<long>(half, Allocator.Temp, NativeArrayOptions.ClearMemory);
                for (int z = 0; z < n; ++z)
                for (int x = 0; x < n; ++x)
                {
                    int baseIdx = x + n2 * z;
                    for (int y = 0; y < half; ++y)
                        if (occupied[baseIdx + n * y] != 0) layer[y]++;
                }
                long maxH = 0;
                for (int y = 0; y < half; ++y) maxH = math.max(maxH, layer[y]);
                for (int y = 0; y < half; ++y)
                {
                    if (layer[y] >= 0.15 * maxH) { floorCell = y; break; }
                }
                layer.Dispose();
            }

            // —— 4. 候选掩码(高度带;带外实心体素清零,BFS 天然不越带)——
            int yLow = floorCell + (int)math.ceil(FloorClearance / math.max(cellSize.y, 1e-6f));
            int yHigh = floorCell + (int)math.floor(CeilingHeight / math.max(cellSize.y, 1e-6f));
            yLow = math.clamp(yLow, 0, n);
            yHigh = math.clamp(yHigh, 0, n - 1);
            for (int z = 0; z < n; ++z)
            for (int y = 0; y < n; ++y)
            {
                if (y >= yLow && y <= yHigh) continue;
                int rowBase = n * y + n2 * z;
                for (int x = 0; x < n; ++x) occupied[rowBase + x] = 0;
            }

            // —— 5. 3D 6-邻域 BFS 连通分量标记(固定线性扫描顺序,米制一/二阶矩同步累加)——
            var labels = new NativeArray<int>(total, Allocator.Temp, NativeArrayOptions.ClearMemory); // 0 = 未标记
            var queue = new NativeArray<int>(total, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            const int MaxClusters = SplatAutoPropDetector.MaxClusters;
            var acc = new NativeArray<ClusterAcc>(MaxClusters + 1, Allocator.Temp, NativeArrayOptions.ClearMemory);
            int clusterCount = 0;

            for (int z = 0; z < n; ++z)
            for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x)
            {
                int idx = x + n * y + n2 * z;
                if (occupied[idx] == 0 || labels[idx] != 0) continue;
                if (clusterCount >= MaxClusters) continue;
                int id = ++clusterCount;
                int head = 0, tail = 0;
                queue[tail++] = idx;
                labels[idx] = id;
                while (head < tail)
                {
                    int cur = queue[head++];
                    int cx = cur % n, cy = (cur / n) % n, cz = cur / n2;
                    ClusterAcc local = acc[id]; // NativeArray 索引器返回副本,累加后写回
                    Accumulate(ref local, cx, cy, cz, cellCounts[cur], cellSize);
                    acc[id] = local;
                    Enqueue(ref queue, ref tail, labels, occupied, cx + 1, cy, cz, n, n2, id);
                    Enqueue(ref queue, ref tail, labels, occupied, cx - 1, cy, cz, n, n2, id);
                    Enqueue(ref queue, ref tail, labels, occupied, cx, cy + 1, cz, n, n2, id);
                    Enqueue(ref queue, ref tail, labels, occupied, cx, cy - 1, cz, n, n2, id);
                    Enqueue(ref queue, ref tail, labels, occupied, cx, cy, cz + 1, n, n2, id);
                    Enqueue(ref queue, ref tail, labels, occupied, cx, cy, cz - 1, n, n2, id);
                }
            }

            // —— 6. 簇级过滤 + XZ 2D PCA ±θ 候选 → 旋转 OBB(第二遍扫描取旋转系 min/max,米制)——
            float sceneWidth = math.max(span3.x, span3.z);
            int outCount = 0;
            for (int id = 1; id <= clusterCount && outCount < MaxClusters; ++id)
            {
                ClusterAcc a = acc[id];
                if (a.VoxelCount < 4) continue;                       // PCA 需要至少 4 个体素
                if (a.SplatCount < MinSplatCount) continue;

                // 触碰水平边界 → 结构墙/大背景
                if (a.MinCellX <= 1 || a.MinCellZ <= 1 || a.MaxCellX >= n - 2 || a.MaxCellZ >= n - 2) continue;

                // 单簇水平跨度超过场景宽度 45% → 大背景
                float spanX = (a.MaxCellX - a.MinCellX + 1) * cellSize.x;
                float spanZ = (a.MaxCellZ - a.MinCellZ + 1) * cellSize.z;
                if (math.max(spanX, spanZ) > StructureSpanRatio * sceneWidth) continue;

                double invN = 1.0 / a.VoxelCount;
                double meanX = a.SumXm * invN, meanZ = a.SumZm * invN;
                double sxx = a.SumXm2 * invN - meanX * meanX;
                double szz = a.SumZm2 * invN - meanZ * meanZ;
                double sxz = a.SumXmZm * invN - meanX * meanZ;
                double theta = 0.5 * math.atan2(2.0 * sxz, sxx - szz);

                // 第二遍:两个候选角 {+θ, −θ} 的旋转系 min/max(体素中心,米制;R_ccw 旋转约定)
                double2 minA = new double2(double.MaxValue), maxA = new double2(double.MinValue);
                double2 minB = new double2(double.MaxValue), maxB = new double2(double.MinValue);
                double yMin = double.MaxValue, yMax = double.MinValue;
                double2 csA = new double2(math.cos(theta), math.sin(theta));   // φ = +θ
                double2 csB = new double2(math.cos(-theta), math.sin(-theta)); // φ = −θ
                for (int idx2 = 0; idx2 < total; ++idx2)
                {
                    if (labels[idx2] != id) continue;
                    int cx = idx2 % n, cy = (idx2 / n) % n, cz = idx2 / n2;
                    double xm = cx * cellSize.x, zm = cz * cellSize.z;
                    RotateExtent(csA, xm, zm, ref minA, ref maxA);
                    RotateExtent(csB, xm, zm, ref minB, ref maxB);
                    yMin = math.min(yMin, cy);
                    yMax = math.max(yMax, cy);
                }
                double areaA = (maxA.x - minA.x) * (maxA.y - minA.y);
                double areaB = (maxB.x - minB.x) * (maxB.y - minB.y);
                bool useA = areaA <= areaB; // 面积更小者 = 正确对齐角(确定性消解 PCA 符号歧义)
                double2 rMin = useA ? minA : minB;
                double2 rMax = useA ? maxA : maxB;
                double2 cs = useA ? csA : csB;
                double yaw = useA ? theta : -theta;

                // OBB 中心:第二遍扫描旋转的是网格局部坐标(米,原点 = lo),中点经 R_ccw(−yaw)
                // 回变换后须加回网格原点 lo 才是世界坐标(Y 同样是网格局部坐标,须加 lo.y)
                double2 rMid = (rMin + rMax) * 0.5;
                float3 center = new float3(
                    lo.x + (float)(cs.x * rMid.x + cs.y * rMid.y),
                    lo.y + (float)(a.SumYm * invN),
                    lo.z + (float)(-cs.y * rMid.x + cs.x * rMid.y));
                float3 halfExtents = new float3(
                    (float)((rMax.x - rMin.x) * 0.5 + cellSize.x * 0.5),
                    (float)((yMax - yMin) * cellSize.y * 0.5 + cellSize.y * 0.5),
                    (float)((rMax.y - rMin.y) * 0.5 + cellSize.z * 0.5));
                float volume = 8f * halfExtents.x * halfExtents.y * halfExtents.z;
                if (volume < MinPropVolumeM3 || volume > MaxPropVolumeM3) continue;

                OutProps[outCount++] = new AutoDetectedProp
                {
                    Center = center,
                    YawRadians = (float)yaw,
                    HalfExtents = halfExtents,
                    SplatCount = (int)a.SplatCount,
                    VoxelCount = (int)a.VoxelCount,
                    VolumeM3 = volume
                };
            }
            OutCount[0] = outCount;

            cellCounts.Dispose();
            occupied.Dispose();
            labels.Dispose();
            queue.Dispose();
            acc.Dispose();
        }

        static void Accumulate(ref ClusterAcc a, int x, int y, int z, int splats, float3 cellSize)
        {
            a.VoxelCount++;
            a.SplatCount += splats;
            double xm = x * cellSize.x, ym = y * cellSize.y, zm = z * cellSize.z;
            a.SumXm += xm;
            a.SumZm += zm;
            a.SumYm += ym;
            a.SumXm2 += xm * xm;
            a.SumZm2 += zm * zm;
            a.SumXmZm += xm * zm;
            if (a.VoxelCount == 1)
            {
                a.MinCellX = x; a.MaxCellX = x; a.MinCellZ = z; a.MaxCellZ = z;
            }
            else
            {
                a.MinCellX = math.min(a.MinCellX, x); a.MaxCellX = math.max(a.MaxCellX, x);
                a.MinCellZ = math.min(a.MinCellZ, z); a.MaxCellZ = math.max(a.MaxCellZ, z);
            }
        }

        static void Enqueue(ref NativeArray<int> queue, ref int tail, NativeArray<int> labels,
                            NativeArray<byte> occupied, int x, int y, int z, int n, int n2, int id)
        {
            if ((uint)x >= (uint)n || (uint)y >= (uint)n || (uint)z >= (uint)n) return;
            int idx = x + n * y + n2 * z;
            if (occupied[idx] == 0 || labels[idx] != 0) return;
            labels[idx] = id;
            queue[tail++] = idx;
        }

        /// <summary>体素中心 (x,z)(米)经 R_ccw(φ) 旋转(cs=(cosφ, sinφ)):xr = c·x − s·z, zr = s·x + c·z。
        /// PCA 主轴角 α 的标准公式即基于 R_ccw 约定;Unity yaw = −α 时 OBB 局部 X 对齐主轴。</summary>
        static void RotateExtent(double2 cs, double x, double z, ref double2 mn, ref double2 mx)
        {
            double xr = cs.x * x - cs.y * z;
            double zr = cs.y * x + cs.x * z;
            mn = math.min(mn, new double2(xr, zr));
            mx = math.max(mx, new double2(xr, zr));
        }

        /// <summary>直方图分位数:累计至 ratio·count 所在 bin 的下界坐标。</summary>
        static float PercentileAt(NativeArray<long> hist, float aMin, float span, double ratio)
        {
            long count = 0;
            for (int b = 0; b < hist.Length; ++b) count += hist[b];
            if (count <= 0) return aMin;
            long target = (long)(ratio * count);
            long cum = 0;
            for (int b = 0; b < hist.Length; ++b)
            {
                cum += hist[b];
                if (cum >= target)
                    return aMin + span * b / hist.Length;
            }
            return aMin + span;
        }
    }
}
