// Splat2Play — M1 烘焙 Burst 任务集:体素化 → 洪泛填充 → Surface Nets → 顶点聚类抽稀
// 设计决策(偏离 §5.1 原文并记录):
//  1) 密度用定点整数(Q16.16)原子累加:整数加法与顺序无关 → 并行下确定性成立(验收要求);
//  2) 不做显式 SDF/JFA:Surface Nets 直接在"填充后的密度场"上以阈值插值取面,
//     减少一层内存与算法风险(原 useSDF 参数移除,已在文档记录);
//  3) 洪泛填充用单线程 BFS(密度值最高位复用为 outside 标记),确定性最好;
//  4) Burst 不支持闭包:所有局部函数必须是 static 且无捕获。
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using System.Threading;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Splat2Play.Runtime
{
    /// <summary>烘焙期间共享的网格常量。</summary>
    public struct VoxelGridInfo
    {
        public int N;            // 每轴体素数
        public float3 Min;       // 网格最小角(体素中心为 min + (i+0.5)*step)
        public float Step;       // 体素尺寸
        public int Count => N * N * N;
        public int Index(int x, int y, int z) => x + y * N + z * N * N;
        public float3 VoxelCenter(int x, int y, int z) => Min + new float3(x + 0.5f, y + 0.5f, z + 0.5f) * Step;
    }

    /// <summary>坐标范围包围盒(排除区/封闭区,数量通常 <64)。</summary>
    public struct AabbRange
    {
        public float3 Min, Max;
        public bool Contains(float3 p) => math.all(p >= Min & p <= Max);
    }


    /// <summary>诊断用:FNV-1a 哈希(抓"和相同但置换"的非确定性)。</summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    public struct HashJob : IJob
    {
        [ReadOnly] public NativeArray<int> Data;
        public NativeArray<ulong> Result; // [0]=hash

        public void Execute()
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < Data.Length; ++i)
            {
                uint v = (uint)Data[i];
                for (int b = 0; b < 4; ++b)
                {
                    h ^= (v >> (b * 8)) & 0xFFUL;
                    h *= 1099511628211UL;
                }
            }
            Result[0] = h;
        }
    }

    /// <summary>计算 splat 位置包围盒。注意:IJob.Schedule 会拷贝结构体,
    /// 输出必须落在 NativeArray(NativeContainer)里才能带回来。</summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    public struct MinMaxJob : IJob
    {
        [ReadOnly] public NativeArray<float3> Positions;
        [ReadOnly] public NativeArray<AabbRange> Exclusions;
        public NativeArray<float3> Result; // [0]=min [1]=max(仅统计不在排除区内的 splat)

        public void Execute()
        {
            float3 mn = new(float.PositiveInfinity), mx = new(float.NegativeInfinity);
            bool any = false;
            for (int i = 0; i < Positions.Length; ++i)
            {
                float3 p = Positions[i];
                if (Exclusions.Length > 0)
                {
                    bool excluded = false;
                    for (int e = 0; e < Exclusions.Length; ++e)
                        if (Exclusions[e].Contains(p)) { excluded = true; break; }
                    if (excluded) continue;
                }
                mn = math.min(mn, p);
                mx = math.max(mx, p);
                any = true;
            }
            if (!any) { mn = float3.zero; mx = float3.zero; }
            Result[0] = mn;
            Result[1] = mx;
        }
    }

    /// <summary>
    /// T1.2 密度场体素化:每 splat 向其 3σ 世界 AABB 覆盖的体素累加
    /// opacity·exp(-d²/2),d 为马氏距离。定点整数原子累加(顺序无关 → 确定性)。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    public struct VoxelizeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<SplatRaw> Splats;
        [ReadOnly] public NativeArray<AabbRange> Exclusions; // 可为空
        public VoxelGridInfo Grid;
        public float OpacityWeight;
        [NativeDisableParallelForRestriction] public NativeArray<int> DensityQ16; // 65536 = 密度 1.0

        // 3σ 截断:d² > 9 的贡献 < 1.2%·opacity,忽略
        const float kMaxD2 = 9f;

        public unsafe void Execute(int i)
        {
            SplatRaw s = Splats[i];
            for (int e = 0; e < Exclusions.Length; ++e)
                if (Exclusions[e].Contains(s.Position)) return;

            float op = s.Opacity * OpacityWeight;
            if (op < 0.005f) return; // 几乎透明的 splat 不贡献

            float3 scale = math.max(s.Scale, 1e-5f);
            float3x3 r = QuaternionToMatrix(s.Rotation);
            // 椭球沿世界各轴的支撑半宽 = Σ_k |R[k][i]|·σ_i(r[k] 为第 k 列)
            float3 half = (math.abs(r[0]) * scale.x + math.abs(r[1]) * scale.y + math.abs(r[2]) * scale.z) * 3f;

            float3 lo = (s.Position - half - Grid.Min) / Grid.Step;
            float3 hi = (s.Position + half - Grid.Min) / Grid.Step;
            // 整个 3σ AABB 在网格外(如天空离群点)→ 跳过,避免钳位后堆积到边角体素
            if (math.any(lo > new float3(Grid.N - 1)) || math.any(hi < float3.zero)) return;
            int3 iLo = math.clamp((int3)math.floor(lo), 0, Grid.N - 1);
            int3 iHi = math.clamp((int3)math.floor(hi), 0, Grid.N - 1);

            float3 invSigma = 1f / scale;
            float3x3 rInv = math.transpose(r);
            // T 的第 i 行 = R 的第 i 行 × (1/σ_i);q_i = (R row_i · d)/σ_i
            float3x3 t = new float3x3(rInv[0] * invSigma.x, rInv[1] * invSigma.y, rInv[2] * invSigma.z);

            int* density = (int*)DensityQ16.GetUnsafePtr();
            for (int z = iLo.z; z <= iHi.z; ++z)
            for (int y = iLo.y; y <= iHi.y; ++y)
            for (int x = iLo.x; x <= iHi.x; ++x)
            {
                float3 d = Grid.VoxelCenter(x, y, z) - s.Position;
                float3 q = math.mul(t, d);
                float d2 = math.dot(q, q);
                if (d2 > kMaxD2) continue;
                float w = op * math.exp(-d2 * 0.5f);
                int add = (int)(w * 65536f);
                if (add <= 0) continue;
                Interlocked.Add(ref density[Grid.Index(x, y, z)], add);
            }
        }


        static float3x3 QuaternionToMatrix(float4 q)
        {
            // xyzw 四元数 → 行主序旋转矩阵
            float x = q.x, y = q.y, z = q.z, w = q.w;
            return new float3x3(
                new float3(1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)),
                new float3(2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)),
                new float3(2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)));
        }
    }

    /// <summary>
    /// T1.3 二值化 + 洪泛填充 + 封闭区(单线程 IJob,确定性)。
    /// 从边界空气体素 BFS 标记 outside(复用最高位);未标记的"空气"即封闭腔体 → 置实心。
    /// 封闭区(SealVolumes)在填充前强制置实心。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    public struct FillJob : IJob
    {
        public VoxelGridInfo Grid;
        public int ThresholdQ16;
        [ReadOnly] public NativeArray<AabbRange> SealVolumes;
        public NativeArray<int> DensityQ16; // 最高位复用为 outside 标记(输出保留该位)
        public NativeArray<long> Stats;     // [0]=密度和 [1]=实心体素数(诊断用,NativeContainer 才能跨 Schedule 带回)

        const int kOutsideBit = unchecked((int)0x80000000);

        public void Execute()
        {
            int n = Grid.N;
            int count = Grid.Count;

            // 封闭区强制实心
            for (int s = 0; s < SealVolumes.Length; ++s)
            {
                AabbRange a = SealVolumes[s];
                int3 lo = math.clamp((int3)math.floor((a.Min - Grid.Min) / Grid.Step), 0, n - 1);
                int3 hi = math.clamp((int3)math.floor((a.Max - Grid.Min) / Grid.Step), 0, n - 1);
                for (int z = lo.z; z <= hi.z; ++z)
                for (int y = lo.y; y <= hi.y; ++y)
                for (int x = lo.x; x <= hi.x; ++x)
                    DensityQ16[Grid.Index(x, y, z)] = 65536;
            }

            // BFS:从边界空气体素扩散
            var queue = new NativeList<int>(1 << 16, Allocator.Temp);
            try
            {
                for (int z = 0; z < n; ++z)
                for (int y = 0; y < n; ++y)
                {
                    TrySeed(DensityQ16, queue, Grid.Index(0, y, z), ThresholdQ16);
                    TrySeed(DensityQ16, queue, Grid.Index(n - 1, y, z), ThresholdQ16);
                }
                for (int z = 0; z < n; ++z)
                for (int x = 0; x < n; ++x)
                {
                    TrySeed(DensityQ16, queue, Grid.Index(x, 0, z), ThresholdQ16);
                    TrySeed(DensityQ16, queue, Grid.Index(x, n - 1, z), ThresholdQ16);
                }
                for (int y = 0; y < n; ++y)
                for (int x = 0; x < n; ++x)
                {
                    TrySeed(DensityQ16, queue, Grid.Index(x, y, 0), ThresholdQ16);
                    TrySeed(DensityQ16, queue, Grid.Index(x, y, n - 1), ThresholdQ16);
                }

                while (queue.Length > 0)
                {
                    int idx = queue[queue.Length - 1];
                    queue.RemoveAt(queue.Length - 1);
                    int x = idx % n, y = (idx / n) % n, z = idx / (n * n);
                    if (x > 0) TrySeed(DensityQ16, queue, idx - 1, ThresholdQ16);
                    if (x < n - 1) TrySeed(DensityQ16, queue, idx + 1, ThresholdQ16);
                    if (y > 0) TrySeed(DensityQ16, queue, idx - n, ThresholdQ16);
                    if (y < n - 1) TrySeed(DensityQ16, queue, idx + n, ThresholdQ16);
                    if (z > 0) TrySeed(DensityQ16, queue, idx - n * n, ThresholdQ16);
                    if (z < n - 1) TrySeed(DensityQ16, queue, idx + n * n, ThresholdQ16);
                }

                // 封闭腔体 → 置实心(把密度补到 1.0);顺带统计校验和
                long sum = 0, solid = 0;
                for (int i = 0; i < count; ++i)
                {
                    int v = DensityQ16[i];
                    if ((v & kOutsideBit) == 0 && (v & 0x7FFFFFFF) < ThresholdQ16)
                        DensityQ16[i] = 65536;
                    int d = DensityQ16[i] & 0x7FFFFFFF;
                    sum += d;
                    if (d >= ThresholdQ16) ++solid;
                }
                if (Stats.IsCreated) { Stats[0] = sum; Stats[1] = solid; }
            }
            finally
            {
                if (queue.IsCreated) queue.Dispose();
            }
        }

        static void TrySeed(NativeArray<int> density, NativeList<int> queue, int idx, int thr)
        {
            int v = density[idx];
            if ((v & kOutsideBit) == 0 && (v & 0x7FFFFFFF) < thr)
            {
                density[idx] = v | kOutsideBit;
                queue.Add(idx);
            }
        }
    }

    /// <summary>
    /// T1.4 Surface Nets(朴素版):在密度场(填充后)上按阈值插值取面。
    /// 单线程 IJob;Burst 不支持闭包,边交点累加用 static 局部函数 + ref 参数。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    public struct SurfaceNetsJob : IJob
    {
        public VoxelGridInfo Grid;
        public float Threshold;
        [ReadOnly] public NativeArray<int> DensityQ16;
        public NativeList<float3> Vertices;
        public NativeList<int> Triangles;

        public void Execute()
        {
            int n = Grid.N;
            int n2 = n * n;
            float thr = Threshold;
            var cellToVertex = new NativeParallelHashMap<int, int>(1 << 18, Allocator.Temp);
            var surfaceCells = new NativeList<int>(1 << 18, Allocator.Temp);
            try
            {
                // Pass 1: 为每个跨阈值的 cell 放置一个顶点(12 边交点均值)
                for (int z = 0; z < n - 1; ++z)
                for (int y = 0; y < n - 1; ++y)
                for (int x = 0; x < n - 1; ++x)
                {
                    int idx = Grid.Index(x, y, z);
                    float c000 = Field(idx), c100 = Field(idx + 1);
                    float c010 = Field(idx + n), c110 = Field(idx + n + 1);
                    float c001 = Field(idx + n2), c101 = Field(idx + n2 + 1);
                    float c011 = Field(idx + n2 + n), c111 = Field(idx + n2 + n + 1);

                    bool s000 = c000 >= thr;
                    if (s000 == (c100 >= thr) && s000 == (c010 >= thr) && s000 == (c110 >= thr) &&
                        s000 == (c001 >= thr) && s000 == (c101 >= thr) && s000 == (c011 >= thr) &&
                        s000 == (c111 >= thr))
                        continue; // 无表面

                    float step = Grid.Step;
                    float3 p000 = Grid.VoxelCenter(x, y, z);
                    float3 sum = float3.zero;
                    int crossings = 0;

                    Edge(c000, c100, p000, p000 + new float3(step, 0, 0), thr, ref sum, ref crossings);
                    Edge(c010, c110, p000 + new float3(0, step, 0), p000 + new float3(step, step, 0), thr, ref sum, ref crossings);
                    Edge(c001, c101, p000 + new float3(0, 0, step), p000 + new float3(step, 0, step), thr, ref sum, ref crossings);
                    Edge(c011, c111, p000 + new float3(0, step, step), p000 + new float3(step, step, step), thr, ref sum, ref crossings);
                    Edge(c000, c010, p000, p000 + new float3(0, step, 0), thr, ref sum, ref crossings);
                    Edge(c100, c110, p000 + new float3(step, 0, 0), p000 + new float3(step, step, 0), thr, ref sum, ref crossings);
                    Edge(c001, c011, p000 + new float3(0, 0, step), p000 + new float3(0, step, step), thr, ref sum, ref crossings);
                    Edge(c101, c111, p000 + new float3(step, 0, step), p000 + new float3(step, step, step), thr, ref sum, ref crossings);
                    Edge(c000, c001, p000, p000 + new float3(0, 0, step), thr, ref sum, ref crossings);
                    Edge(c010, c011, p000 + new float3(0, step, 0), p000 + new float3(0, step, step), thr, ref sum, ref crossings);
                    Edge(c100, c101, p000 + new float3(step, 0, 0), p000 + new float3(step, 0, step), thr, ref sum, ref crossings);
                    Edge(c110, c111, p000 + new float3(step, step, 0), p000 + new float3(step, step, step), thr, ref sum, ref crossings);

                    if (crossings > 0)
                    {
                        cellToVertex.Add(idx, Vertices.Length);
                        Vertices.Add(sum / crossings);
                        surfaceCells.Add(idx);
                    }
                }

                // Pass 2: 沿三轴连接相邻 surface cell,输出四边形(两三角形)
                int stepX = 1, stepY = n, stepZ = n2;
                for (int s = 0; s < surfaceCells.Length; ++s)
                {
                    int idx = surfaceCells[s];
                    for (int axis = 0; axis < 3; ++axis)
                    {
                        int stepA = axis == 0 ? stepX : axis == 1 ? stepY : stepZ;
                        int stepB = axis == 0 ? stepY : axis == 1 ? stepZ : stepX;
                        if (!cellToVertex.TryGetValue(idx, out int v0)) continue;
                        if (!cellToVertex.TryGetValue(idx + stepA, out int v3)) continue;
                        if (!cellToVertex.TryGetValue(idx + stepB, out int v1)) continue;
                        if (!cellToVertex.TryGetValue(idx + stepA + stepB, out int v2)) continue;
                        Triangles.Add(v0); Triangles.Add(v1); Triangles.Add(v2);
                        Triangles.Add(v0); Triangles.Add(v2); Triangles.Add(v3);
                    }
                }
            }
            finally
            {
                if (cellToVertex.IsCreated) cellToVertex.Dispose();
                if (surfaceCells.IsCreated) surfaceCells.Dispose();
            }
        }

        float Field(int idx) => DensityQ16[idx] & 0x7FFFFFFF; // 忽略 outside 位

        static void Edge(float a, float b, float3 pa, float3 pb, float thr, ref float3 sum, ref int crossings)
        {
            if ((a >= thr) != (b >= thr))
            {
                // t 钳位到 [0,1]:a/b 贴着阈值时 b-a 可能趋零,未钳位的 t 会爆到天文字坐标
                float t = math.clamp((thr - a) / (b - a), 0f, 1f);
                sum += pa + (pb - pa) * t;
                ++crossings;
            }
        }
    }

    /// <summary>
    /// T1.5 顶点聚类抽稀:量化顶点到聚类网格,桶数组直接寻址(无哈希表,构造性确定),
    /// 簇内取均值,剔除退化三角形。可反复加倍 step 直到面数达标。
    /// </summary>
    [BurstCompile(FloatMode = FloatMode.Fast)]
    public struct DecimateJob : IJob
    {
        public float ClusterStep;          // 世界尺寸
        public float3 GridMin;             // 量化基准(保证确定性)
        public float3 BoundMin, BoundMax;  // 顶点理论范围(体素网格外扩一格)
        public NativeList<float3> Vertices;
        public NativeList<int> Triangles;
        public NativeArray<long> Diag;     // [0]=total [1]=clusterCount [2]=非有限顶点数 [3]=是否早退

        public void Execute()
        {
            int oldCount = Vertices.Length;
            if (oldCount == 0) return;
            int nonFinite = 0;
            for (int i = 0; i < oldCount; ++i)
                if (!math.all(math.isfinite(Vertices[i]))) ++nonFinite;

            // 键空间:量化网格范围
            int3 kMin = new(int.MaxValue), kMax = new(int.MinValue);
            var keys = new NativeArray<int3>(oldCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            try
            {
                // 饱和钳位:有限但巨大的坐标(上游数值异常)会把 int3 键打满,使 kMax-kMin 溢出
                for (int i = 0; i < oldCount; ++i)
                {
                    float3 f = (Vertices[i] - GridMin) / ClusterStep;
                    f = math.clamp(f, -1e6f, 1e6f);
                    int3 k = (int3)math.floor(f);
                    keys[i] = k;
                }
                // 键范围 = 体素网格的理论键范围(顶点是体素中心插值,必然落在网格内;
                // 用常数硬钳位,不依赖任何数据采样 —— 构造性确定且抗上游数值异常)
                {
                    float3 kLoF = (BoundMin - GridMin) / ClusterStep;
                    float3 kHiF = (BoundMax - GridMin) / ClusterStep;
                    kMin = (int3)math.floor(math.clamp(kLoF, -1000f, 1000f));
                    kMax = (int3)math.floor(math.clamp(kHiF, -1000f, 1000f));
                }
                for (int i = 0; i < oldCount; ++i)
                    keys[i] = math.clamp(keys[i], kMin, kMax);
                int3 kd = kMax - kMin + 1;
                long total = (long)kd.x * kd.y * kd.z;
                if (Diag.IsCreated)
                {
                    Diag[0] = total; Diag[2] = nonFinite;
                    Diag[4] = kd.x; Diag[5] = kd.y; Diag[6] = kd.z;
                    Diag[7] = kMin.x; Diag[8] = kMin.y; Diag[9] = kMin.z;
                    Diag[10] = kMax.x; Diag[11] = kMax.y; Diag[12] = kMax.z;
                }
                if (total > 1 << 26) // 6700 万桶上限,超出说明聚类粒度异常
                {
                    if (Diag.IsCreated) Diag[3] = 1;
                    return; // 跳过本次抽稀(保守:保留上一轮结果)
                }

                int bucketCount = (int)total;
                var bucketIndex = new NativeArray<int>(bucketCount, Allocator.Temp, NativeArrayOptions.ClearMemory); // -1 表示空,ClearMemory 用 int0 → 用哨兵
                for (int i = 0; i < bucketCount; ++i) bucketIndex[i] = -1;
                var bucketSum = new NativeArray<float3>(bucketCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                var bucketCountArr = new NativeArray<int>(bucketCount, Allocator.Temp, NativeArrayOptions.ClearMemory);
                try
                {
                    int clusterCount = 0;
                    var oldToNew = new NativeArray<int>(oldCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                    try
                    {
                        // 构造性防御:非有限/超界顶点直接丢弃(-1),保证输出网格永远干净
                        for (int i = 0; i < oldCount; ++i)
                        {
                            float3 p = Vertices[i];
                            if (!math.all(math.isfinite(p)) || math.any(math.abs(p) > 1e4f))
                            {
                                oldToNew[i] = -1;
                                continue;
                            }
                            int3 k = keys[i] - kMin;
                            int bi = k.x + k.y * kd.x + k.z * kd.x * kd.y;
                            int ci = bucketIndex[bi];
                            if (ci < 0)
                            {   // 首触:bucketSum 是 UninitializedMemory,直接赋值而非 +=(否则读到脏内存 → NaN)
                                bucketIndex[bi] = ci = clusterCount++;
                                bucketSum[ci] = p;
                                bucketCountArr[ci] = 1;
                                oldToNew[i] = ci;
                            }
                            else
                            {
                                oldToNew[i] = ci;
                                bucketSum[ci] += p;
                                bucketCountArr[ci]++;
                            }
                        }

                        if (Diag.IsCreated) Diag[1] = clusterCount;
                        var newVerts = new NativeList<float3>(clusterCount, Allocator.Temp);
                        var newTris = new NativeList<int>(Triangles.Length, Allocator.Temp);
                        try
                        {
                            for (int c = 0; c < clusterCount; ++c)
                                newVerts.Add(bucketSum[c] / math.max(1, bucketCountArr[c]));
                            for (int t = 0; t + 2 < Triangles.Length; t += 3)
                            {
                                int i0 = oldToNew[Triangles[t]];
                                int i1 = oldToNew[Triangles[t + 1]];
                                int i2 = oldToNew[Triangles[t + 2]];
                                if (i0 < 0 || i1 < 0 || i2 < 0) continue; // 引用了被丢弃的脏顶点
                                if (i0 == i1 || i1 == i2 || i0 == i2) continue; // 聚类合并产生的退化三角形
                                newTris.Add(i0); newTris.Add(i1); newTris.Add(i2);
                            }

                            Vertices.Clear();
                            for (int i = 0; i < newVerts.Length; ++i) Vertices.Add(newVerts[i]);
                            Triangles.Clear();
                            for (int i = 0; i < newTris.Length; ++i) Triangles.Add(newTris[i]);
                        }
                        finally
                        {
                            if (newVerts.IsCreated) newVerts.Dispose();
                            if (newTris.IsCreated) newTris.Dispose();
                        }
                    }
                    finally
                    {
                        if (oldToNew.IsCreated) oldToNew.Dispose();
                    }
                }
                finally
                {
                    if (bucketIndex.IsCreated) bucketIndex.Dispose();
                    if (bucketSum.IsCreated) bucketSum.Dispose();
                    if (bucketCountArr.IsCreated) bucketCountArr.Dispose();
                }
            }
            finally
            {
                if (keys.IsCreated) keys.Dispose();
            }
        }
        static void SortInts(NativeArray<int> a, int n)
        {
            for (int i = 1; i < n; ++i)
            {
                int v = a[i];
                int j = i - 1;
                while (j >= 0 && a[j] > v) { a[j + 1] = a[j]; --j; }
                a[j + 1] = v;
            }
        }

    }
}
