// Splat2Play v3 — M12(方向 8)T12.1/T12.2:高斯色度 + 几何扁平度物理材质自动分类器
// 分类准则(规格 §5.2(1),逐 splat,由 f_dc 还原线性色 + 最短轴扁平度):
//   c = saturate(0.5 + 0.28209479·Fdc);M/m = 颜色最大/最小分量;S = (M−m)/max(M,1e-4);
//   w_flat = saturate(1 − sMin/max(sMid,1e-5))(与 SplatNormalMath.ExtractNormalAndFlatness 的扁平度一致)
//   1. FoliageOrCarpet(4):G 显著占优(G > 1.15·max(R,B) 且 G > 0.12),或高饱和低扁平散射体(S > 0.35 且 w < 0.22)
//   2. Wood(2):暖棕色 R > G > B,S ∈ [0.22, 0.80],M ∈ [0.12, 0.78],w ≥ 0.22
//   3. Metal(3):极高明度低饱和高扁平:S < 0.15,M > 0.68,w ≥ 0.65
//   4. StoneOrConcrete(1):中性低饱和硬表面:S < 0.22,w ≥ 0.25(且未命中 Metal)
//   5. Default(0):其余
// 空间材质场:32³(32KB)逐体素按不透明度加权投票(仅非 Default 类别)+ 空体素 1 步 6-邻域膨胀。
// SPDX-License-Identifier: MIT

using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Splat2Play.Runtime
{
    /// <summary>表面物理材质类型(字节编码,与 ProxyData.SurfaceGrid 一致)。</summary>
    public enum S2PSurfaceType : byte
    {
        Default = 0,
        StoneOrConcrete = 1,
        Wood = 2,
        Metal = 3,
        FoliageOrCarpet = 4
    }

    /// <summary>各表面类型的物理属性表(摩擦/弹性)与声学基频参数。</summary>
    public static class S2PSurfacePhysics
    {
        public struct SurfaceProps
        {
            public float StaticFriction;
            public float DynamicFriction;
            public float Bounciness;
            public float FootstepPitch;   // 脚步音基频相对系数
            public string MaterialName;   // PhysicsMaterial 命名
        }

        public static SurfaceProps Get(S2PSurfaceType type) => type switch
        {
            S2PSurfaceType.StoneOrConcrete => new SurfaceProps { StaticFriction = 0.85f, DynamicFriction = 0.72f, Bounciness = 0.05f, FootstepPitch = 1.25f, MaterialName = "S2P_Stone" },
            S2PSurfaceType.Wood            => new SurfaceProps { StaticFriction = 0.65f, DynamicFriction = 0.55f, Bounciness = 0.18f, FootstepPitch = 0.85f, MaterialName = "S2P_Wood" },
            S2PSurfaceType.Metal           => new SurfaceProps { StaticFriction = 0.50f, DynamicFriction = 0.42f, Bounciness = 0.35f, FootstepPitch = 1.50f, MaterialName = "S2P_Metal" },
            S2PSurfaceType.FoliageOrCarpet => new SurfaceProps { StaticFriction = 0.95f, DynamicFriction = 0.85f, Bounciness = 0.00f, FootstepPitch = 0.55f, MaterialName = "S2P_Foliage" },
            _                              => new SurfaceProps { StaticFriction = 0.70f, DynamicFriction = 0.60f, Bounciness = 0.10f, FootstepPitch = 1.00f, MaterialName = "S2P_Default" }
        };
    }

    /// <summary>32³ 空间材质场构建结果(Grid 长度 = Resolution³,索引 x + R·(y + R·z))。</summary>
    public sealed class S2PSurfaceGridResult
    {
        public int Resolution;
        public Bounds GridBounds;
        public byte[] Grid;
        public int[] TypeCounts = new int[5];
        public int OccupiedCells;
        public int DilatedCells;
    }

    public static class S2PSurfaceClassifier
    {
        public const int DefaultResolution = 32;
        public const float kShC0 = 0.28209479f;
        /// <summary>投票有效的最低不透明度(极透明 splat 对表面无意义)。</summary>
        public const float kMinVoteOpacity = 0.05f;

        /// <summary>由直流球谐分量还原线性颜色(与着色器 ShadeSH DC 项一致)。</summary>
        public static float3 SplatColor(float3 fdc) => math.saturate(0.5f + kShC0 * fdc);

        /// <summary>最短轴扁平度 ∈ [0,1](与 SplatNormalMath.ExtractNormalAndFlatness 一致:sMid 为三轴中位数)。</summary>
        public static float FlatnessOf(float3 scale)
        {
            float sMin = math.cmin(scale);
            float sMid = MiddleOf(scale);
            return math.saturate(1f - sMin / math.max(sMid, 1e-5f));
        }

        static float MiddleOf(float3 s)
        {
            float mx = math.max(s.x, math.max(s.y, s.z));
            float mn = math.min(s.x, math.min(s.y, s.z));
            return math.clamp(s.x + s.y + s.z - mx - mn, mn, mx);
        }

        /// <summary>单 splat 材质分类(规格 §5.2(1) 五级判定,顺序影响 Stone 的"未命中 Metal"语义)。</summary>
        public static S2PSurfaceType ClassifySplat(float3 fdc, float3 scale)
        {
            float3 c = SplatColor(fdc);
            float M = math.cmax(c);
            float m = math.cmin(c);
            float S = (M - m) / math.max(M, 1e-4f);
            float w = FlatnessOf(scale);

            // 1. 植被 / 草地 / 软质地毯
            if ((c.y > 1.15f * math.max(c.x, c.z) && c.y > 0.12f) || (S > 0.35f && w < 0.22f))
                return S2PSurfaceType.FoliageOrCarpet;
            // 2. 木材 / 地板 / 木制家具
            if (c.x > c.y && c.y > c.z && S >= 0.22f && S <= 0.80f && M >= 0.12f && M <= 0.78f && w >= 0.22f)
                return S2PSurfaceType.Wood;
            // 3. 金属 / 镜面硬质表面
            if (S < 0.15f && M > 0.68f && w >= 0.65f)
                return S2PSurfaceType.Metal;
            // 4. 石材 / 混凝土 / 砖墙(未命中 Metal)
            if (S < 0.22f && w >= 0.25f)
                return S2PSurfaceType.StoneOrConcrete;
            return S2PSurfaceType.Default;
        }

        /// <summary>
        /// 由 splat 点云构建 32³ 空间材质场:逐体素不透明度加权投票(非 Default 类别)+ 空体素 1 步膨胀。
        /// 网格边界取点云绝对 min/max 外扩 0.1%(越界查询由采样端 clamp)。
        /// </summary>
        public static S2PSurfaceGridResult BuildGrid(NativeArray<SplatRawX> splats, int resolution = DefaultResolution)
        {
            if (resolution < 4 || resolution > 256) throw new ArgumentOutOfRangeException(nameof(resolution));
            int r = resolution;
            long cellCount = (long)r * r * r;

            float3 mn = new float3(float.PositiveInfinity), mx = new float3(float.NegativeInfinity);
            for (int i = 0; i < splats.Length; ++i)
            {
                float3 p = splats[i].Position;
                mn = math.min(mn, p);
                mx = math.max(mx, p);
            }
            if (math.any(mn > mx)) mn = mx = float3.zero;
            float3 span = math.max(mx - mn, new float3(1e-4f)) * 1.001f; // 微膨胀使最大点可含
            var bounds = new Bounds((Vector3)((mn + (mn + span)) * 0.5f), (Vector3)span);
            float3 invCell = r / span;

            var votes = new float[cellCount * 4];   // 每格 4 个非 Default 类别的加权票
            var occupied = new bool[cellCount];
            for (int i = 0; i < splats.Length; ++i)
            {
                SplatRawX s = splats[i];
                if (s.Opacity < kMinVoteOpacity) continue;
                int3 c = (int3)math.clamp(math.floor((s.Position - mn) * invCell), 0, r - 1);
                long idx = c.x + r * (c.y + (long)r * c.z);
                S2PSurfaceType t = ClassifySplat(s.Fdc, s.Scale);
                if (t == S2PSurfaceType.Default) { occupied[idx] = true; continue; }
                votes[idx * 4 + (int)t - 1] += s.Opacity;
                occupied[idx] = true;
            }

            var grid = new byte[cellCount];
            var counts = new int[5];
            int occupiedCells = 0;
            for (long i = 0; i < cellCount; ++i)
            {
                byte best = 0;
                float bestW = 0f;
                for (int k = 0; k < 4; ++k)
                {
                    float w = votes[i * 4 + k];
                    if (w > bestW) { bestW = w; best = (byte)(k + 1); } // 平票取最低类别,确定性
                }
                grid[i] = best;
                if (occupied[i]) occupiedCells++;
            }

            // 空体素 1 步 6-邻域膨胀:碰撞网格表面顶点落入紧邻体素时也能查到材质
            int dilated = 0;
            {
                var snapshot = (byte[])grid.Clone();
                var occupiedSnap = (bool[])occupied.Clone();
                for (int z = 0; z < r; ++z)
                for (int y = 0; y < r; ++y)
                for (int x = 0; x < r; ++x)
                {
                    long idx = x + r * (y + (long)r * z);
                    if (occupiedSnap[idx]) continue;
                    int[] neighborCounts = new int[5]; // 栈上小数组;仅对空格执行
                    for (int d = 0; d < 6; ++d)
                    {
                        int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
                        int ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        int nz = z + (d == 4 ? 1 : d == 5 ? -1 : 0);
                        if ((uint)nx >= (uint)r || (uint)ny >= (uint)r || (uint)nz >= (uint)r) continue;
                        byte nb = snapshot[nx + r * (ny + (long)r * nz)];
                        if (nb != 0 && occupiedSnap[nx + r * (ny + (long)r * nz)]) neighborCounts[nb]++;
                    }
                    byte pick = 0;
                    int pickCount = 0;
                    for (int k = 1; k < 5; ++k)
                        if (neighborCounts[k] > pickCount) { pickCount = neighborCounts[k]; pick = (byte)k; }
                    if (pick != 0) { grid[idx] = pick; dilated++; }
                }
            }

            for (long i = 0; i < cellCount; ++i) counts[grid[i]]++;

            return new S2PSurfaceGridResult
            {
                Resolution = r,
                GridBounds = bounds,
                Grid = grid,
                TypeCounts = counts,
                OccupiedCells = occupiedCells,
                DilatedCells = dilated
            };
        }

        /// <summary>材质场索引(x + R·(y + R·z));越界坐标由调用端 clamp。</summary>
        public static long GridIndex(int3 c, int r) => c.x + r * (c.y + (long)r * c.z);
    }
}
