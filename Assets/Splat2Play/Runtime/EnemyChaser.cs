// Splat2Play — T2.3 敌人追击 AI(NavMeshAgent 沿扫描场景真实地形寻路)
// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.AI;

namespace Splat2Play.Runtime
{
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class EnemyChaser : MonoBehaviour
    {
        public Transform Target;
        public float CatchDistance = 1.5f;
        public float RepathInterval = 0.25f;
        [Tooltip("抓捕视线遮挡检测层(防止隔薄墙穿墙抓捕)")]
        public LayerMask ObstacleMask = ~0;

        NavMeshAgent _agent;
        Vector3 _spawnPosition;
        float _nextRepath;
        int _catchCount;

        void OnEnable()
        {
            _agent = GetComponent<NavMeshAgent>();
            _spawnPosition = transform.position;
            if (Target == null)
            {
                var player = GameObject.FindGameObjectWithTag("Player");
                if (player != null) Target = player.transform;
            }
        }

        void Update()
        {
            if (Target == null || !_agent.enabled || !_agent.isOnNavMesh) return;

            if (Time.time >= _nextRepath)
            {
                _agent.SetDestination(Target.position);
                _nextRepath = Time.time + RepathInterval;
            }

            float dist = Vector3.Distance(transform.position, Target.position);
            if (dist < CatchDistance && HasLineOfSight())
            {
                ++_catchCount;
                Debug.Log($"[Splat2Play] 敌人抓住玩家(第 {_catchCount} 次),位置 {transform.position:F1}");
                // 重置回初始出生点,便于演示连续性
                _agent.Warp(_spawnPosition);
            }
        }

        /// <summary>视线检查:排除自身与目标碰撞体后,若中间被墙体/代理网格阻挡则不判定为抓捕。</summary>
        bool HasLineOfSight()
        {
            Vector3 from = transform.position + Vector3.up * 0.8f;
            Vector3 to = Target.position + Vector3.up * 0.8f;
            if (Physics.Linecast(from, to, out RaycastHit hit, ObstacleMask, QueryTriggerInteraction.Ignore))
            {
                if (hit.transform == transform || hit.transform.IsChildOf(transform)) return true;
                if (hit.transform == Target || hit.transform.IsChildOf(Target)) return true;
                return false;
            }
            return true;
        }
    }
}
