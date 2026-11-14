// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public enum TransparencyMode
    {
        SortedBlended,
        Stochastic,
    }

    public enum RenderMode
    {
        Splats,
        DebugPoints,
    }

    public enum StochasticTemporalFilter
    {
        None,
        SimpleAccumulation,
        Reprojected,
    }

#if UNITY_6000_5_OR_NEWER
    [Unity.Scripting.LifecycleManagement.NoAutoStaticsCleanup]
#endif
    [ExecuteInEditMode] // so that Awake is called in edit mode
    [DefaultExecutionOrder(-100)]
    public class GaussianSplatRenderingOptions : MonoBehaviour
    {
        public static GaussianSplatRenderingOptions instance
        {
            get
            {
                if (ms_Instance == null)
                    ms_Instance = FindAnyObjectByType<GaussianSplatRenderingOptions>();
                if (ms_Instance == null)
                {
                    var go = new GameObject($"{nameof(GaussianSplatRenderingOptions)} (Defaults)")
                    {
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    ms_Instance = go.AddComponent<GaussianSplatRenderingOptions>();
                }
                ms_Instance.EnsureResources();
                return ms_Instance;
            }
        }
        static GaussianSplatRenderingOptions ms_Instance;

        [Header("Options")]
        [Tooltip("Gaussian splat transparency rendering algorithm")]
        public TransparencyMode m_Transparency = TransparencyMode.SortedBlended;
        [Tooltip("Stochastic temporal filtering")]
        public StochasticTemporalFilter m_TemporalFilter = StochasticTemporalFilter.Reprojected;

        [Header("Debugging Tweaks")]
        public RenderMode m_RenderMode = RenderMode.Splats;
        [Range(1.0f,15.0f)] public float m_PointDisplaySize = 3.0f;
        [Tooltip("Show only Spherical Harmonics contribution, using gray color")]
        public bool m_SHOnly;

        [Header("S2P M6 GPU Culling")]
        [Tooltip("GPU 分块剔除:视锥 + Hi-Z 深度遮挡(Morton 分块,需 m_MortonReorder 开启)")]
        public bool m_GpuChunkCulling = true;
        [Tooltip("剔除后通过 DrawProceduralIndirect 只绘制可见实例(排序模式下精确跳过被剔除 splat)")]
        public bool m_IndirectDraw = true;

        [Header("S2P M10 Chunk HLOD")]
        [Tooltip("Chunk 级 3 档 HLOD:基于 Morton 局部性的零额外显存步进抽样(LOD0 全量 / LOD1 1/4 / LOD2 1/16),需 m_GpuChunkCulling 开启")]
        public bool m_EnableChunkLod = false;
        [Tooltip("LOD1 步进(1/4 抽样)起始距离(世界空间米;分块距离超过该值进入 LOD1)")]
        public float m_Lod1Distance = 12f;
        [Tooltip("LOD2 步进(1/16 抽样)起始距离(世界空间米)")]
        public float m_Lod2Distance = 35f;
        [Tooltip("帧预算(毫秒):S2PLodBudgetController 据此自适应缩放 LOD 距离(0 = 关闭自适应)")]
        public float m_TargetFrameBudgetMs = 8f;
        [Tooltip("尺度/不透明度能量守恒补偿强度(0 = 不补偿,1 = 按经验系数 γscale=1.65/2.60、γα=1.35/1.75 全额补偿)")]
        [Range(0f, 1f)] public float m_LodScaleCompensation = 1f;

        [Header("S2P M13 Depth Export")]
        [Tooltip("SortedBlended 模式下导出透射率加权有效表面深度并写回相机深度(供 DoF/SSAO/屏幕空间特效与后续透明物体使用)")]
        public bool m_WriteSplatDepth = true;
        [Tooltip("有效表面深度判定阈值:累积 α ≥ 该阈值的像素才取 splat 深度,否则保留场景深度")]
        [Range(0.1f, 0.9f)] public float m_SplatDepthAlphaThreshold = 0.5f;

        internal StochasticTemporalFilter temporalFilter => m_Transparency == TransparencyMode.Stochastic && m_RenderMode == RenderMode.Splats
            ? m_TemporalFilter : StochasticTemporalFilter.None;

        internal bool needSorting => m_Transparency == TransparencyMode.SortedBlended && m_RenderMode == RenderMode.Splats;

        internal bool resourcesFound { get; private set; }
        bool resourcesLoadAttempted;
        internal Shader shaderSplats { get; private set; }
        internal Shader shaderComposite { get; private set; }
        internal Shader shaderDebugPoints { get; private set; }
        internal ComputeShader csUtilities { get; private set; }

        void Awake()
        {
            if (ms_Instance != null && ms_Instance != this && ms_Instance.gameObject.hideFlags == HideFlags.HideAndDontSave)
                DestroyImmediate(ms_Instance.gameObject);
            ms_Instance = this;
            EnsureResources();
        }

        void EnsureResources()
        {
            if (resourcesLoadAttempted)
                return;
            resourcesLoadAttempted = true;

            shaderSplats = Resources.Load<Shader>("GaussianSplats");
            shaderComposite = Resources.Load<Shader>("GaussianComposite");
            shaderDebugPoints = Resources.Load<Shader>("GaussianDebugRenderPoints");
            csUtilities = Resources.Load<ComputeShader>("GaussianSplatUtilities");

            resourcesFound = shaderSplats != null && shaderComposite != null && shaderDebugPoints != null &&
                             csUtilities != null && SystemInfo.supportsComputeShaders;
        }

        void OnDestroy()
        {
            if (ms_Instance == this) ms_Instance = null;
        }
    }
}
