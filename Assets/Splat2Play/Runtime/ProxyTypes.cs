// Splat2Play — M1 碰撞代理:烘焙设置与产物类型(§5.1 规格实现)
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Splat2Play.Runtime
{
    /// <summary>烘焙参数(§5.1 参数表)。ScriptableObject 以便向导窗口持久化预设。</summary>
    [CreateAssetMenu(menuName = "Splat2Play/Proxy Bake Settings", fileName = "ProxyBakeSettings")]
    public sealed class ProxyBakeSettings : ScriptableObject
    {
        [Header("体素化")]
        [Tooltip("密度场每轴体素数(64–512)。越大越精细,内存与耗时按立方增长。")]
        public int VoxelResolution = 256;
        [Tooltip("二值化密度阈值。密度为不透明度加权的高斯覆盖度(定点数已归一化到 0–1)。")]
        [Range(0.05f, 1f)] public float DensityThreshold = 0.5f;
        [Tooltip("烘焙时按 splat 不透明度缩放其贡献(过滤半透明漂浮物)。")]
        public float OpacityWeight = 1.0f;
        [Tooltip("包围盒百分位(0–10):逐轴取 [p,100-p] 分位,剔除天空等远距离离群 splat。0 = 用绝对 min/max。")]
        [Range(0f, 10f)] public float BoundsPercentile = 1f;

        [Header("填充与封闭")]
        [Tooltip("洪泛填充封闭内部(把扫描漏洞里的空腔标记为实心)。")]
        public bool FillInterior = true;

        [Header("网格")]
        [Tooltip("代理网格三角形上限,超出则自动加倍顶点聚类粒度。")]
        public int MaxTriangles = 150000;

        [Header("边界情况(T1.7)")]
        [Tooltip("排除区:处于这些 AABB 内的 splat 不参与体素化(剔除漂浮噪点)。")]
        public List<Bounds> ExclusionVolumes = new();
        [Tooltip("封闭区:这些 AABB 强制视为实心(补扫描漏洞的封闭空间)。")]
        public List<Bounds> SealVolumes = new();
        [Tooltip("补丁盒体:不参与体素化,直接作为额外 BoxCollider 追加(细长结构兜底)。")]
        public List<PatchBox> PatchBoxes = new();

        [Serializable]
        public struct PatchBox
        {
            public Vector3 Center;
            public Vector3 Size;
            public Vector3 RotationEuler;
        }

        void OnValidate()
        {
            VoxelResolution = Mathf.Clamp(VoxelResolution, 64, 512);
            MaxTriangles = Mathf.Max(1000, MaxTriangles);
        }
    }

    /// <summary>M1 烘焙产物(§5.1 接口)。</summary>
    [CreateAssetMenu(menuName = "Splat2Play/Proxy Data", fileName = "ProxyData")]
    public sealed class ProxyData : ScriptableObject
    {
        [Tooltip("代理碰撞网格。只读使用;由烘焙生成。")]
        public Mesh CollisionMesh;
        public Bounds SceneBounds;
        public int SourceSplatCount;
        public int TriangleCount;
        public int VoxelResolution;
        public float DensityThreshold;
        public ProxyBakeSettings Settings;
        [Tooltip("补丁盒体(场景局部坐标),由 SplatLevel 实例化为 BoxCollider。")]
        public List<ProxyBakeSettings.PatchBox> PatchBoxes = new();
        [Tooltip("烘焙统计与决策记录(供 benchmarks 汇总)。")]
        [TextArea(3, 8)] public string BakeLog;
    }
}
