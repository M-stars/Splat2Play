// Splat2Play — M1 碰撞代理:烘焙设置与产物类型(§5.1 规格实现)
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Splat2Play.Runtime
{
    /// <summary>
    /// M5(方向 5)T5.3:烘焙计算后端。
    /// Auto:系统自动选择(支持 Compute 且 256³/512³ 且点数 ≥ 100 万时走 GPU,否则 CPU);
    /// CPUJobsDeterministic:现有 Burst/Q16.16 定点管线(单元测试与无 Compute 环境的确定性回退路径);
    /// GPUCompute:强制 GPU(不支持时告警并回退 CPU)。
    /// </summary>
    public enum ProxyBakeBackend
    {
        Auto = 0,
        CPUJobsDeterministic = 1,
        GPUCompute = 2
    }

    /// <summary>烘焙参数(§5.1 参数表)。ScriptableObject 以便向导窗口持久化预设。</summary>
    [CreateAssetMenu(menuName = "Splat2Play/Proxy Bake Settings", fileName = "ProxyBakeSettings")]
    public sealed class ProxyBakeSettings : ScriptableObject
    {
        [Header("后端")]
        [Tooltip("M5 烘焙后端:Auto(按硬件与规模自动)/ CPUJobsDeterministic(Burst 确定性)/ GPUCompute。")]
        public ProxyBakeBackend Backend = ProxyBakeBackend.Auto;

        [Header("体素化")]
        [Tooltip("密度场每轴体素数(64–512)。越大越精细,内存与耗时按立方增长。")]
        public int VoxelResolution = 256;
        [Tooltip("二值化密度阈值。密度为不透明度加权的高斯覆盖度(定点数已归一化到 0–1)。")]
        [Range(0.05f, 1f)] public float DensityThreshold = 0.5f;
        [Tooltip("烘焙时按 splat 不透明度缩放其贡献(过滤半透明漂浮物)。")]
        public float OpacityWeight = 1.0f;
        [Tooltip("包围盒百分位(0–10):逐轴取 [p,100-p] 分位,剔除天空等远距离离群 splat。0 = 用绝对 min/max。")]
        [Range(0f, 10f)] public float BoundsPercentile = 1f;
        [Tooltip("场景 AABB 外扩边距(米,§5.1 boundsMargin)。")]
        [Range(0f, 5f)] public float BoundsMargin = 0.5f;

        [Header("填充与 SDF 平滑")]
        [Tooltip("洪泛填充封闭内部(把扫描漏洞里的空腔标记为实心;室内房间关卡建议设为 false)。")]
        public bool FillInterior = true;
        [Tooltip("启用 3D 有向距离场(SDF)平滑核,使提取出的等值面网格更平滑(512 档自动关闭以控制内存)。")]
        public bool UseSDF = true;

        [Header("网格与导航")]
        [Tooltip("代理碰撞网格三角形上限,超出则自动加倍顶点聚类粒度。")]
        public int MaxTriangles = 150000;
        [Tooltip("同时生成供 M2 NavMesh 烘焙使用的简化平滑寻路网格(§5.2 碰撞细网格/寻路粗网格分离)。")]
        public bool BakeNavigationMesh = true;
        [Tooltip("寻路专用粗网格三角形上限。")]
        public int MaxNavTriangles = 40000;

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
            if (VoxelResolution >= 512 && UseSDF)
            {
                UseSDF = false;
                Debug.LogWarning("[Splat2Play] 512³ 分辨率下为控制内存峰值(<2GB),已自动关闭 UseSDF(§5.1 规格)。");
            }
            BoundsMargin = Mathf.Clamp(BoundsMargin, 0f, 5f);
            MaxTriangles = Mathf.Max(1000, MaxTriangles);
            MaxNavTriangles = Mathf.Clamp(MaxNavTriangles, 500, MaxTriangles);
        }
    }

    /// <summary>M1 烘焙产物(§5.1 接口)。</summary>
    [CreateAssetMenu(menuName = "Splat2Play/Proxy Data", fileName = "ProxyData")]
    public sealed class ProxyData : ScriptableObject
    {
        [Tooltip("代理碰撞网格。只读使用;由烘焙生成。")]
        public Mesh CollisionMesh;
        [Tooltip("寻路专用简化网格(M2 NavMesh 优先使用;为空时回退使用 CollisionMesh)。")]
        public Mesh NavigationMesh;
        public Bounds SceneBounds;
        public int SourceSplatCount;
        public int TriangleCount;
        public int VoxelResolution;
        public float DensityThreshold;
        public ProxyBakeSettings Settings;
        [Tooltip("补丁盒体(场景局部坐标),由 SplatLevel 实例化为 BoxCollider。")]
        public List<ProxyBakeSettings.PatchBox> PatchBoxes = new();
        [Tooltip("M4 分块剔除:非空空间子块 AABB 列表(局部坐标)。")]
        public List<Bounds> ChunkBounds = new();
        [Tooltip("M4 分块剔除:对应每个非空子块内的 splat 数量。")]
        public List<int> ChunkSplatCounts = new();
        [Tooltip("烘焙统计与决策记录(供 benchmarks 汇总)。")]
        [TextArea(3, 8)] public string BakeLog;
    }
}
