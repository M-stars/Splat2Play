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

        [Tooltip("乘数下限(<1 表示帧率充裕时放宽切换距离,更晚进入 LOD)")]
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
            var (nextMul, lod1, lod2) = StepBudget(
                LodDistanceMultiplier, _smoothFrameMs, TargetFrameBudgetMs,
                Time.unscaledDeltaTime, Responsiveness, MinMultiplier, MaxMultiplier,
                _baseLod1, _baseLod2);
            LodDistanceMultiplier = nextMul;
            _options.m_Lod1Distance = lod1;
            _options.m_Lod2Distance = lod2;
        }

        /// <summary>M10 纯函数步进:帧耗时超预算(smoothFrameMs &gt; targetBudgetMs)时乘数上升、LOD 切换距离缩短(负反馈)。</summary>
        public static (float nextMultiplier, float lod1Distance, float lod2Distance) StepBudget(
            float currentMultiplier, float smoothFrameMs, float targetBudgetMs,
            float dt, float responsiveness, float minMul, float maxMul,
            float baseLod1, float baseLod2)
        {
            float ratio = smoothFrameMs / Mathf.Max(targetBudgetMs, 0.01f);
            float step = (ratio - 1f) * responsiveness * dt * (ratio > 1f ? 1f : 0.5f);
            float next = Mathf.Clamp(currentMultiplier + step, minMul, maxMul);
            float inv = 1f / Mathf.Max(next, 0.01f);
            return (next, baseLod1 * inv, baseLod2 * inv);
        }

        void Apply(float m)
        {
            // m > 1(超预算)时缩短切换距离以提前进入 LOD1/LOD2;m < 1(帧率充裕)时放宽切换距离
            float inv = 1f / Mathf.Max(m, 0.01f);
            _options.m_Lod1Distance = _baseLod1 * inv;
            _options.m_Lod2Distance = _baseLod2 * inv;
        }
    }
}
