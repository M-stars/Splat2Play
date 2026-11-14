// Splat2Play v3 — M12(方向 8)T12.3:运行时 O(1) 表面材质查询与 PhysicsMaterial 绑定
// 由 SplatLevel(烘焙装配)自动挂载:绑定 ProxyData.SurfaceGrid 后,
//   QuerySurface(worldPos) 经 InverseTransformPoint + 栅格索引 O(1) 无 GC 返回 S2PSurfaceType;
//   GetPhysicsMaterial(type) 惰性创建 5 种 Unity 6 PhysicsMaterial(摩擦/弹性按 S2PSurfacePhysics 表)。
// Primary 静态注册表供 SplatFPSController(脚步/命中)与 S2PDynamicProp(刚体碰撞)全局查询。
// SPDX-License-Identifier: MIT

using System;
using Unity.Mathematics;
using UnityEngine;

namespace Splat2Play.Runtime
{
    [DisallowMultipleComponent]
    public sealed class S2PSurfaceMaterialMap : MonoBehaviour
    {
        public int Resolution = S2PSurfaceClassifier.DefaultResolution;
        public Bounds GridBounds;
        [Tooltip("Resolution³ 个 byte(S2PSurfaceType),索引 x + R·(y + R·z)")]
        public byte[] Grid = Array.Empty<byte>();

        /// <summary>当前激活的材质场(关卡级单例;SplatFPSController/S2PDynamicProp 全局查询用)。</summary>
        public static S2PSurfaceMaterialMap Primary { get; private set; }

        void OnEnable() => Primary = this;
        void OnDisable() { if (Primary == this) Primary = null; }

        /// <summary>从烘焙产物 ProxyData 绑定材质场(SurfaceGrid 为空时不绑定,保持 Default 查询)。</summary>
        public void BindFromProxy(ProxyData proxy)
        {
            if (proxy == null || proxy.SurfaceGrid == null || proxy.SurfaceGrid.Count == 0)
            {
                Grid = Array.Empty<byte>();
                return;
            }
            Resolution = Mathf.Max(4, proxy.SurfaceGridResolution);
            GridBounds = proxy.SurfaceGridBounds;
            Grid = proxy.SurfaceGrid.ToArray();
        }

        /// <summary>
        /// O(1) 无 GC 查询世界坐标处的表面材质。
        /// 越界/未绑定一律返回 Default;局部坐标经 InverseTransformPoint 支持关卡整体变换。
        /// </summary>
        public S2PSurfaceType QuerySurface(Vector3 worldPos)
        {
            int r = Resolution;
            byte[] grid = Grid;
            if (r < 4 || grid == null || grid.Length != (long)r * r * r)
                return S2PSurfaceType.Default;
            float3 local = transform.InverseTransformPoint(worldPos);
            float3 rel = (local - (float3)GridBounds.min) / math.max((float3)GridBounds.size, new float3(1e-4f));
            int3 c = (int3)math.clamp(math.floor(rel * r), 0, r - 1);
            return (S2PSurfaceType)grid[c.x + r * (c.y + (long)r * c.z)];
        }

        /// <summary>查询世界坐标处表面材质的便捷入口(无激活材质场时返回 Default)。</summary>
        public static S2PSurfaceType QueryWorld(Vector3 worldPos)
        {
            var map = Primary;
            return map != null ? map.QuerySurface(worldPos) : S2PSurfaceType.Default;
        }

        static readonly PhysicsMaterial[] s_Materials = new PhysicsMaterial[5];

        /// <summary>惰性创建并缓存各表面类型的 Unity 6 PhysicsMaterial(运行时资产,不入库)。</summary>
        public static PhysicsMaterial GetPhysicsMaterial(S2PSurfaceType type)
        {
            int i = (int)type;
            if (i < 0 || i >= s_Materials.Length) return null;
            if (s_Materials[i] != null) return s_Materials[i];
            var props = S2PSurfacePhysics.Get(type);
            var mat = new PhysicsMaterial(props.MaterialName)
            {
                staticFriction = props.StaticFriction,
                dynamicFriction = props.DynamicFriction,
                bounciness = props.Bounciness,
                frictionCombine = PhysicsMaterialCombine.Average,
                bounceCombine = PhysicsMaterialCombine.Average,
                hideFlags = HideFlags.DontSave
            };
            s_Materials[i] = mat;
            return mat;
        }

        /// <summary>把材质场对应的 PhysicsMaterial 应用到碰撞体(材质场查询命中时)。</summary>
        public static void ApplyMaterialTo(Collider collider, Vector3 worldPos)
        {
            if (collider == null) return;
            collider.sharedMaterial = GetPhysicsMaterial(QueryWorld(worldPos));
        }
    }
}
