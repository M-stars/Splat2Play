// Splat2Play — T4.1 资产级流送:按相机距离启用/卸载 splat 渲染器,远距降 SH 阶数
// v0 简化:以"资产级"为流送粒度(§5.4 降级预案),chunk 级剔除列为后续工作。
// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEngine;

namespace Splat2Play.Runtime
{
    public enum SplatStreamState { Full, Reduced, Unloaded }

    /// <summary>
    /// 挂在 SplatLevel 上:按与相机的距离切换 Full/Reduced/Unloaded。
    /// Unloaded 时禁用 GaussianSplatRenderer(释放 GPU 缓冲,OnEnable 时自动重载)。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class S2PSplatStreaming : MonoBehaviour
    {
        public GaussianSplatRenderer Target;
        public Camera Camera;
        [Tooltip("此距离内使用完整 SH3")]
        public float FullDistance = 20f;
        [Tooltip("超过此距离卸载(禁用渲染器)")]
        public float CullDistance = 80f;
        [Tooltip("Full 与 Cull 之间使用的 SH 阶数")]
        public int FarSHOrder = 1;
        [Tooltip("卸载前的缓冲时长,避免相机抖动导致反复加载")]
        public float HysteresisSeconds = 1.5f;

        public SplatStreamState CurrentState { get; private set; } = SplatStreamState.Full;
        float _reducedSince = -1f;

        void Update()
        {
            if (Target == null) return;
            var cam = Camera != null ? Camera : Camera.main;
            if (cam == null) return;

            Vector3 center = transform.TransformPoint(Target.localBounds.center);
            float dist = Vector3.Distance(cam.transform.position, center);

            if (dist <= FullDistance)
            {
                SetState(SplatStreamState.Full);
                _reducedSince = -1f;
            }
            else if (dist <= CullDistance)
            {
                SetState(SplatStreamState.Reduced);
            }
            else
            {
                // 迟滞:需要持续超距 HysteresisSeconds 才真正卸载
                if (_reducedSince < 0f) _reducedSince = Time.time;
                if (Time.time - _reducedSince >= HysteresisSeconds)
                    SetState(SplatStreamState.Unloaded);
                else
                    SetState(SplatStreamState.Reduced);
            }
        }

        void SetState(SplatStreamState state)
        {
            if (CurrentState == state) return;
            CurrentState = state;
            switch (state)
            {
                case SplatStreamState.Full:
                    if (!Target.isActiveAndEnabled) Target.enabled = true;
                    Target.m_SHOrder = Target.sourceSHDegree;
                    break;
                case SplatStreamState.Reduced:
                    if (!Target.isActiveAndEnabled) Target.enabled = true;
                    Target.m_SHOrder = Mathf.Min(FarSHOrder, Target.sourceSHDegree);
                    break;
                case SplatStreamState.Unloaded:
                    Target.enabled = false; // OnDisable 释放 GPU 缓冲;重新启用时自动 Reload
                    break;
            }
        }
    }
}
