// Splat2Play v3 — M11(方向 7)T11.5:凹体多凸包组合碰撞生成
// 针对凹形物体(桌底空腔、L 型沙发),单一 convex MeshCollider 会把空腔封死;
// 本构建器把物体重建为 ≤ maxBoxes(默认 8)个非重叠 BoxCollider 的组合:
//   1. 在局部点云包围盒上划分 gridRes × gridRes × gridRes(默认 12³)占用体素网格(≥1 个 splat 中心即实心);
//   2. Greedy 3D Box Merging:按固定线性顺序扫描第一个未消费的实心体素,
//      依次沿 +X → +Y → +Z 扩展出最大的全实心长方体并标记消费(确定性);
//   3. 长方体总数 > maxBoxes 时,按体积降序保留前 maxBoxes-1 个主长方体,
//      剩余零散实心体素合并为第 maxBoxes 个包络盒(注意:包络盒可能覆盖凹腔,属规格取舍)。
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Splat2Play.Runtime
{
    public static class SplatCompoundColliderBuilder
    {
        public const int DefaultMaxBoxes = 8;
        public const int DefaultGridRes = 12;

        /// <summary>
        /// 由物件点云(物件局部坐标,质心在原点)生成组合碰撞盒列表(局部坐标)。
        /// 返回盒数 ≤ maxBoxes;空点云返回空列表。
        /// </summary>
        public static List<Bounds> BuildBoxes(NativeArray<SplatRawX> propSplats, int maxBoxes = DefaultMaxBoxes, int gridRes = DefaultGridRes)
        {
            var result = new List<Bounds>();
            int n = Mathf.Clamp(gridRes, 4, 64);
            maxBoxes = Mathf.Max(1, maxBoxes);
            int count = propSplats.Length;
            if (count == 0) return result;

            float3 mn = new float3(float.PositiveInfinity), mx = new float3(float.NegativeInfinity);
            for (int i = 0; i < count; ++i)
            {
                float3 p = propSplats[i].Position;
                mn = math.min(mn, p);
                mx = math.max(mx, p);
            }
            float3 size = math.max(mx - mn, new float3(1e-4f));
            float3 cell = size / n;
            float3 invCell = n / size;

            // 占用栅格
            var occ = new bool[n * n * n];
            for (int i = 0; i < count; ++i)
            {
                int3 c = (int3)math.clamp(math.floor((propSplats[i].Position - mn) * invCell), 0, n - 1);
                occ[c.x + n * (c.y + n * c.z)] = true;
            }

            // Greedy 3D Box Merging(+X → +Y → +Z,固定扫描顺序,确定性)
            var consumed = new bool[n * n * n];
            var boxes = new List<CellBox>(); // min/max 格坐标
            for (int z = 0; z < n; ++z)
            for (int y = 0; y < n; ++y)
            for (int x = 0; x < n; ++x)
            {
                int idx = x + n * (y + n * z);
                if (!occ[idx] || consumed[idx]) continue;

                int x1 = x;
                while (x1 + 1 < n && occ[x1 + 1 + n * (y + n * z)] && !consumed[x1 + 1 + n * (y + n * z)]) x1++;

                int y1 = y;
                bool extendY = true;
                while (extendY && y1 + 1 < n)
                {
                    for (int xi = x; xi <= x1; ++xi)
                        if (!occ[xi + n * (y1 + 1 + n * z)] || consumed[xi + n * (y1 + 1 + n * z)]) { extendY = false; break; }
                    if (extendY) y1++;
                }

                int z1 = z;
                bool extendZ = true;
                while (extendZ && z1 + 1 < n)
                {
                    for (int yi = y; yi <= y1 && extendZ; ++yi)
                    for (int xi = x; xi <= x1; ++xi)
                        if (!occ[xi + n * (yi + n * (z1 + 1))] || consumed[xi + n * (yi + n * (z1 + 1))]) { extendZ = false; break; }
                    if (extendZ) z1++;
                }

                for (int zi = z; zi <= z1; ++zi)
                for (int yi = y; yi <= y1; ++yi)
                for (int xi = x; xi <= x1; ++xi)
                    consumed[xi + n * (yi + n * zi)] = true;

                boxes.Add(new CellBox { Min = new int3(x, y, z), Max = new int3(x1, y1, z1) });
            }

            if (boxes.Count == 0) return result;

            // 超额处理:体积降序保留主长方体,零散合并为包络盒(排序键含格坐标,稳定确定性)
            if (boxes.Count > maxBoxes)
            {
                boxes.Sort((a, b) =>
                {
                    long va = VolumeOf(a), vb = VolumeOf(b);
                    if (va != vb) return vb.CompareTo(va);
                    if (a.Min.x != b.Min.x) return a.Min.x.CompareTo(b.Min.x);
                    if (a.Min.y != b.Min.y) return a.Min.y.CompareTo(b.Min.y);
                    if (a.Min.z != b.Min.z) return a.Min.z.CompareTo(b.Min.z);
                    return 0;
                });
                var kept = boxes.GetRange(0, maxBoxes - 1);
                var rest = boxes.GetRange(maxBoxes - 1, boxes.Count - (maxBoxes - 1));
                int3 envMin = rest[0].Min, envMax = rest[0].Max;
                foreach (var b in rest)
                {
                    envMin = math.min(envMin, b.Min);
                    envMax = math.max(envMax, b.Max);
                }
                kept.Add(new CellBox { Min = envMin, Max = envMax });
                boxes = kept;
            }

            foreach (var b in boxes)
            {
                float3 bmin = mn + (float3)b.Min * cell;
                float3 bmax = mn + ((float3)b.Max + 1f) * cell;
                // 1mm 外扩:补偿 mn + idx·cell 的浮点累积误差,保证栅格量化后的原始点
                // 恒被覆盖(相邻盒间 1mm 重叠对物理无副作用)
                float3 pad = new float3(1e-3f);
                bmin -= pad;
                bmax += pad;
                result.Add(new Bounds((Vector3)((bmin + bmax) * 0.5f), (Vector3)math.max(bmax - bmin, new float3(1e-4f))));
            }
            return result;

            long VolumeOf(CellBox b) => (long)(b.Max.x - b.Min.x + 1) * (b.Max.y - b.Min.y + 1) * (b.Max.z - b.Min.z + 1);
        }

        /// <summary>
        /// 把组合碰撞盒挂到目标 GameObject(物件局部空间):单一子节点承载 N 个 BoxCollider。
        /// 返回生成的 BoxCollider 数量。
        /// </summary>
        public static int AttachCompoundColliders(GameObject target, List<Bounds> localBoxes)
        {
            if (target == null || localBoxes == null || localBoxes.Count == 0) return 0;
            var root = target.transform.Find("S2P_CompoundColliders");
            if (root != null)
            {
                if (Application.isPlaying) Object.Destroy(root.gameObject);
                else Object.DestroyImmediate(root.gameObject);
            }
            var go = new GameObject("S2P_CompoundColliders");
            go.transform.SetParent(target.transform, false);
            for (int i = 0; i < localBoxes.Count; ++i)
            {
                var col = go.AddComponent<BoxCollider>();
                col.center = localBoxes[i].center;
                col.size = localBoxes[i].size;
            }
            return localBoxes.Count;
        }

        /// <summary>格坐标长方体(Min/Max 均为含端点的闭区间)。</summary>
        private struct CellBox
        {
            public int3 Min;
            public int3 Max;
        }
    }
}
