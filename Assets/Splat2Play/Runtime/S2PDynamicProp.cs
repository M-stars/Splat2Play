// Splat2Play — M8(方向 3)T8.2/T8.3:动态 Splat 刚体组件
// 由 SplatPropExtractor 装配到动态子物体上:Rigidbody + 凸包 MeshCollider(编辑器烘焙)
// + NavMeshObstacle(carving = true,被推倒后 AI 自动绕行)。
// 为什么 GaussianSplatRenderer 原生支持刚体运动:GaussianSplatRenderSystem 每帧对每个
// 渲染器分别传入各自的 localToWorldMatrix 计算 ViewData,然后合并进全局排序缓冲统一排序,
// 因此子物体在扫描房间里翻滚时能与背景进行精确的前后深度混合并投射 M3 动态阴影。
// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.AI;

namespace Splat2Play.Runtime
{
    [DisallowMultipleComponent]
    public sealed class S2PDynamicProp : MonoBehaviour
    {
        [Tooltip("本物件的 splat 渲染器(与刚体同节点)")]
        public GaussianSplatRenderer SplatRenderer;

        [Tooltip("刚体质量(kg),由烘焙设置的 DynamicPropVolume.Mass 填入")]
        public float Mass = 5f;

        [Tooltip("是否可被击碎(预留)")]
        public bool Destructible;

        Rigidbody _rb;
        NavMeshObstacle _obstacle;

        public Rigidbody Body => _rb;
        public bool IsHeld { get; internal set; }

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            if (_rb == null) _rb = gameObject.AddComponent<Rigidbody>();
            _rb.mass = Mass;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _rb.sleepThreshold = 0.08f;

            // NavMeshObstacle:carving 让追击 AI 在物件倒下/被推动后自动绕行
            _obstacle = GetComponent<NavMeshObstacle>();
            if (_obstacle == null) _obstacle = gameObject.AddComponent<NavMeshObstacle>();
            _obstacle.shape = NavMeshObstacleShape.Box;
            _obstacle.carving = true;
            _obstacle.carveOnlyStationary = true;
            _obstacle.carvingMoveThreshold = 0.3f;
            var col = GetComponentInChildren<Collider>();
            if (col != null)
            {
                var b = col.bounds.size;
                _obstacle.size = new Vector3(Mathf.Max(0.3f, b.x), Mathf.Max(0.3f, b.y), Mathf.Max(0.3f, b.z));
            }
        }

        void OnEnable() => SplatRenderer?.EnsureRegistered();

        /// <summary>抓取/释放时切换刚体状态(抓取时降低阻尼便于拖动)。</summary>
        public void SetHeld(bool held)
        {
            IsHeld = held;
            if (_rb == null) return;
            _rb.useGravity = !held;
            _rb.linearDamping = held ? 6f : 0.05f;
            _rb.angularDamping = held ? 6f : 0.05f;
        }
    }
}
