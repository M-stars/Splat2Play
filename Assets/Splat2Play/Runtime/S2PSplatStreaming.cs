// Splat2Play — T4.1/T4.2 资产级流送与保守代理遮挡剔除
// 按相机距离切换 Full/Reduced/Unloaded,并支持视锥剔除与基于碰撞代理的 9 点保守射线遮挡测试。
// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEngine;

namespace Splat2Play.Runtime
{
    public enum SplatStreamState { Full, Reduced, Unloaded, Culled }

    /// <summary>
    /// 挂在 SplatLevel 上:按与相机的距离及代理遮挡状态切换 Full/Reduced/Unloaded/Culled。
    /// Unloaded/Culled 时禁用 GaussianSplatRenderer(释放 GPU 缓冲,OnEnable 时自动重载)。
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
        [Tooltip("卸载/剔除前的缓冲时长,避免相机抖动导致反复加载")]
        public float HysteresisSeconds = 1.5f;

        [Header("T4.2 剔除(保守策略)")]
        [Tooltip("启用视锥剔除(多区块关卡适用)")]
        public bool EnableFrustumCulling;
        [Tooltip("启用基于代理碰撞体的 9 点保守遮挡剔除(当区块完全被其他建筑代理阻挡时卸载)")]
        public bool EnableOcclusionCulling = true;
        [Tooltip("遮挡检测物理层")]
        public LayerMask OccluderMask = ~0;

        public SplatStreamState CurrentState { get; private set; } = SplatStreamState.Full;
        public bool IsOccluded { get; private set; }
        public bool IsFrustumVisible { get; private set; } = true;

        float _pendingUnloadSince = -1f;
        readonly Plane[] _frustumPlanes = new Plane[6];

        void Update()
        {
            if (Target == null || Target.isLoading) return;
            var cam = Camera != null ? Camera : Camera.main;
            if (cam == null) return;

            Bounds localB = Target.localBounds;
            if (localB.size.sqrMagnitude < 1e-4f && TryGetComponent<SplatLevel>(out var lvl) && lvl.Proxy != null)
                localB = lvl.Proxy.SceneBounds;

            Vector3 worldCenter = transform.TransformPoint(localB.center);
            Vector3 worldSize = Vector3.Scale(localB.size, transform.lossyScale);
            Bounds worldBounds = new(worldCenter, new Vector3(Mathf.Abs(worldSize.x), Mathf.Abs(worldSize.y), Mathf.Abs(worldSize.z)));

            Vector3 camPos = cam.transform.position;
            float dist = Vector3.Distance(camPos, worldCenter);

            // 1. 视锥与保守代理遮挡测试
            IsFrustumVisible = true;
            if (EnableFrustumCulling && worldBounds.size.sqrMagnitude > 1e-3f)
            {
                GeometryUtility.CalculateFrustumPlanes(cam, _frustumPlanes);
                IsFrustumVisible = GeometryUtility.TestPlanesAABB(_frustumPlanes, worldBounds);
            }

            IsOccluded = EnableOcclusionCulling && CheckConservativeOcclusion(camPos, worldBounds);

            if (!IsFrustumVisible || IsOccluded)
            {
                if (_pendingUnloadSince < 0f) _pendingUnloadSince = Time.time;
                if (Time.time - _pendingUnloadSince >= HysteresisSeconds)
                    SetState(SplatStreamState.Culled);
                else
                    SetState(SplatStreamState.Reduced);
                return;
            }

            // 2. 距离分级流送
            if (dist <= FullDistance)
            {
                SetState(SplatStreamState.Full);
                _pendingUnloadSince = -1f;
            }
            else if (dist <= CullDistance)
            {
                SetState(SplatStreamState.Reduced);
                _pendingUnloadSince = -1f;
            }
            else
            {
                // 迟滞:需要持续超距 HysteresisSeconds 才真正卸载
                if (_pendingUnloadSince < 0f) _pendingUnloadSince = Time.time;
                if (Time.time - _pendingUnloadSince >= HysteresisSeconds)
                    SetState(SplatStreamState.Unloaded);
                else
                    SetState(SplatStreamState.Reduced);
            }
        }

        /// <summary>
        /// 保守遮挡判定:相机在当前区块包围盒外部,且相机到包围盒 9 个特征点(中心 + 8 个内缩角点)
        /// 的视线全部被非本关卡的静态碰撞体阻挡时,才返回 true。
        /// </summary>
        bool CheckConservativeOcclusion(Vector3 camPos, Bounds wb)
        {
            if (wb.size.sqrMagnitude < 1e-3f || wb.Contains(camPos)) return false;

            Vector3 c = wb.center;
            Vector3 e = wb.extents * 0.9f;
            if (!IsRayBlockedByOther(camPos, c)) return false;
            if (!IsRayBlockedByOther(camPos, c + new Vector3(-e.x, -e.y, -e.z))) return false;
            if (!IsRayBlockedByOther(camPos, c + new Vector3( e.x, -e.y, -e.z))) return false;
            if (!IsRayBlockedByOther(camPos, c + new Vector3(-e.x,  e.y, -e.z))) return false;
            if (!IsRayBlockedByOther(camPos, c + new Vector3( e.x,  e.y, -e.z))) return false;
            if (!IsRayBlockedByOther(camPos, c + new Vector3(-e.x, -e.y,  e.z))) return false;
            if (!IsRayBlockedByOther(camPos, c + new Vector3( e.x, -e.y,  e.z))) return false;
            if (!IsRayBlockedByOther(camPos, c + new Vector3(-e.x,  e.y,  e.z))) return false;
            if (!IsRayBlockedByOther(camPos, c + new Vector3( e.x,  e.y,  e.z))) return false;
            return true;
        }

        bool IsRayBlockedByOther(Vector3 from, Vector3 to)
        {
            if (Physics.Linecast(from, to, out RaycastHit hit, OccluderMask, QueryTriggerInteraction.Ignore))
                return !hit.collider.transform.IsChildOf(transform);
            return false;
        }

        void SetState(SplatStreamState state)
        {
            if (CurrentState == state) return;
            CurrentState = state;
            int maxSh = Target.sourceSHDegree > 0 ? Target.sourceSHDegree : 3;
            switch (state)
            {
                case SplatStreamState.Full:
                    if (!Target.isActiveAndEnabled) Target.enabled = true;
                    Target.m_SHOrder = maxSh;
                    break;
                case SplatStreamState.Reduced:
                    if (!Target.isActiveAndEnabled) Target.enabled = true;
                    Target.m_SHOrder = Mathf.Min(FarSHOrder, maxSh);
                    break;
                case SplatStreamState.Unloaded:
                case SplatStreamState.Culled:
                    Target.enabled = false; // OnDisable 释放 GPU 缓冲;重新启用时自动 Reload
                    break;
            }
        }
    }
}
