// Splat2Play v3 — M10(方向 4)T10.4:LOD 帧预算自适应控制器
// 按实测帧耗时与目标预算(TargetFrameBudgetMs)的偏差,平滑缩放 GaussianSplatRenderingOptions
// 的 LOD 距离(m_Lod1Distance / m_Lod2Distance):帧超预算 → 乘数增大 → 更近的 Chunk 更早进入
// 步进档(光栅化量下降);帧余量充足 → 乘数回落至 1(恢复原始距离)。
// 乘数作用于距离而非补偿强度:距离越远越多的 Chunk 落入 LOD1/2,视觉质量渐进退化、无突变。
// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEngine;

namespace Splat2Play.Runtime
{
    [DisallowMultipleComponent]
    public sealed class S2PLodBudgetController : MonoBehaviour
    {
        [Tooltip("是否启用自适应(关闭时乘数恒为 1,LOD 距离恢复基线)")]
        public bool AdaptEnabled = true;

        [Tooltip("目标帧预算(毫秒);与 GaussianSplatRenderingOptions.m_TargetFrameBudgetMs 同步使用")]
        public float TargetFrameBudgetMs = 8f;

        [Tooltip("乘数下限(<1 表示比用户设定更早进入 LOD,用于低功耗设备兜底)")]
        public float MinMultiplier = 0.6f;

        [Tooltip("乘数上限(过高会整体糊化;默认 3.5)")]
        public float MaxMultiplier = 3.5f;

        [Tooltip("自适应速度:每超预算 100% 时乘数每秒上升的量(回落速度为其一半)")]
        public float Responsiveness = 1.2f;

        [Tooltip("当前距离乘数(1 = 用户设定距离;>1 = 更早进入 LOD)")]
        public float LodDistanceMultiplier { get; private set; } = 1f;

        GaussianSplatRenderingOptions _options;
        float _baseLod1 = -1f, _baseLod2 = -1f;
        float _smoothFrameMs;

        void OnEnable()
        {
            _options = GaussianSplatRenderingOptions.instance;
            _baseLod1 = _options.m_Lod1Distance;
            _baseLod2 = _options.m_Lod2Distance;
            if (_options.m_TargetFrameBudgetMs > 0f)
                TargetFrameBudgetMs = _options.m_TargetFrameBudgetMs;
            _smoothFrameMs = TargetFrameBudgetMs;
        }

        void OnDisable()
        {
            if (_options != null && _baseLod1 > 0f)
            {
                _options.m_Lod1Distance = _baseLod1;
                _options.m_Lod2Distance = _baseLod2;
            }
        }

        void Update()
        {
            if (_options == null) return;
            if (TargetFrameBudgetMs <= 0.01f || _baseLod1 <= 0f)
                return;

            _smoothFrameMs = Mathf.Lerp(_smoothFrameMs, Time.unscaledDeltaTime * 1000f, 0.06f);

            if (!AdaptEnabled)
            {
                LodDistanceMultiplier = 1f;
                Apply(1f);
                return;
            }

            // 比例偏差 → 乘数积分:超预算 100% 时每秒 +Responsiveness;余量充足时以一半速度回落
            float ratio = _smoothFrameMs / TargetFrameBudgetMs;
            float step = (ratio - 1f) * Responsiveness * Time.unscaledDeltaTime * (ratio > 1f ? 1f : 0.5f);
            LodDistanceMultiplier = Mathf.Clamp(LodDistanceMultiplier + step, MinMultiplier, MaxMultiplier);
            Apply(LodDistanceMultiplier);
        }

        void Apply(float m)
        {
            _options.m_Lod1Distance = _baseLod1 * m;
            _options.m_Lod2Distance = _baseLod2 * m;
        }
    }
}
