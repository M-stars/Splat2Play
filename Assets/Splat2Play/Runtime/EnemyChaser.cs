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

        NavMeshAgent _agent;
        float _nextRepath;
        int _catchCount;

        void OnEnable()
        {
            _agent = GetComponent<NavMeshAgent>();
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
            if (dist < CatchDistance)
            {
                ++_catchCount;
                Debug.Log($"[Splat2Play] 敌人抓住玩家(第 {_catchCount} 次),位置 {transform.position:F1}");
                // 重置到出生点,便于演示连续性
                if (TryGetComponent(out NavMeshAgent a))
                {
                    a.Warp(transform.position + Vector3.up * 0.5f);
                }
            }
        }
    }
}
