// Splat2Play — T2.2 第一人称控制器(Input System 直接轮询,不依赖模板动作资产)
// SPDX-License-Identifier: MIT

using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Splat2Play.Runtime
{
    [RequireComponent(typeof(CharacterController))]
    public sealed class SplatFPSController : MonoBehaviour
    {
        public float MoveSpeed = 4.5f;
        public float SprintMultiplier = 1.8f;
        public float MouseSensitivity = 0.12f;
        public float PitchLimit = 85f;
        public Transform CameraPivot; // 通常为主相机子节点

        [Header("M8 交互(动态 Splat 刚体)")]
        [Tooltip("左键射击对动态物件施加的冲量(N·s)")]
        public float ShotImpulse = 6f;
        [Tooltip("射击射线最大距离(米)")]
        public float ShotRange = 25f;
        [Tooltip("E 键抓取距离(米)")]
        public float GrabDistance = 3f;
        [Tooltip("抓取时物件保持在相机前的距离(米)")]
        public float HoldDistance = 1.6f;
        [Tooltip("投掷初速度(m/s)")]
        public float ThrowSpeed = 8f;

        [Header("M12 材质音效(S2PSurfaceMaterialMap 驱动)")]
        [Tooltip("脚步触发水平移动距离(米)")]
        public float FootstepDistance = 0.85f;
        [Range(0f, 1f)] public float FootstepVolume = 0.45f;
        [Range(0f, 1f)] public float ShotHitVolume = 0.8f;

        CharacterController _cc;
        float _yaw, _pitch;
        float _verticalVel;
        S2PDynamicProp _heldProp;
        Rigidbody _heldBody;
        AudioSource _audioSrc;
        float _stepAccum;
        Vector3 _prevPos;

        void OnEnable()
        {
            _cc = GetComponent<CharacterController>();
            var euler = transform.eulerAngles;
            _yaw = euler.y;
            _pitch = CameraPivot != null ? CameraPivot.localEulerAngles.x : 0f;
            if (_pitch > 180f) _pitch -= 360f;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            _audioSrc = GetComponent<AudioSource>();
            if (_audioSrc == null) _audioSrc = gameObject.AddComponent<AudioSource>();
            _audioSrc.spatialBlend = 0f; // 自身脚步用 2D 播放,避免自定位衰减
            _prevPos = transform.position;
        }

        void OnDisable()
        {
            ReleaseHeld();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        void Update()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            // 视角
            float2 delta = (float2)mouse.delta.ReadValue();
            _yaw += delta.x * MouseSensitivity;
            _pitch = Mathf.Clamp(_pitch - delta.y * MouseSensitivity, -PitchLimit, PitchLimit);
            transform.rotation = Quaternion.Euler(0f, _yaw, 0f);
            if (CameraPivot != null)
                CameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);

            // 移动
            Vector2 move = Vector2.zero;
            if (kb.wKey.isPressed) move.y += 1f;
            if (kb.sKey.isPressed) move.y -= 1f;
            if (kb.aKey.isPressed) move.x -= 1f;
            if (kb.dKey.isPressed) move.x += 1f;
            move.Normalize();
            float speed = MoveSpeed * (kb.leftShiftKey.isPressed ? SprintMultiplier : 1f);

            Vector3 velocity = (transform.right * move.x + transform.forward * move.y) * speed;
            // 简易重力(碰撞代理由 M1 提供)
            if (_cc.isGrounded) _verticalVel = -1f;
            else _verticalVel += Physics.gravity.y * Time.deltaTime;
            velocity.y = _verticalVel;

            _cc.Move(velocity * Time.deltaTime);

            // —— M12 T12.4:按脚下材质播放脚步声(移动距离积累触发,音高随材质与确定性抖动)——
            if (_cc.isGrounded)
            {
                Vector3 horizontal = transform.position - _prevPos;
                horizontal.y = 0f;
                _stepAccum += horizontal.magnitude;
                if (_stepAccum >= FootstepDistance && horizontal.sqrMagnitude > 1e-8f)
                {
                    _stepAccum = 0f;
                    PlayFootstep();
                }
            }
            _prevPos = transform.position;

            // —— M8 T8.3:射线交互 ——
            if (mouse.leftButton.wasPressedThisFrame)
                Shoot();
            if (kb.eKey.wasPressedThisFrame)
            {
                if (_heldProp != null) ThrowHeld();
                else TryGrab();
            }
            if (_heldProp != null)
                UpdateHeldPosition();
        }

        /// <summary>左键:向准星方向发射物理射线,命中动态 Splat 刚体则施加冲量击飞/推倒;命中点按材质播放命中声。</summary>
        void Shoot()
        {
            Transform origin = CameraPivot != null ? CameraPivot : transform;
            Vector3 dir = origin.forward;
            if (Physics.Raycast(origin.position, dir, out var hit, ShotRange))
            {
                if (hit.rigidbody != null
                    && hit.rigidbody.TryGetComponent<S2PDynamicProp>(out var prop))
                {
                    hit.rigidbody.AddForceAtPosition(dir * ShotImpulse, hit.point, ForceMode.Impulse);
                    // 轻微扭矩让物件翻滚更自然
                    hit.rigidbody.AddTorque(Vector3.Cross(dir, Vector3.up) * ShotImpulse * 0.15f, ForceMode.Impulse);
                }
                // M12:命中点材质音效(石材清脆 / 木质低频 / 金属高频 / 草地沙沙)
                S2PProceduralAudio.PlayAt(S2PSurfaceMaterialMap.QueryWorld(hit.point), hit.point, ShotHitVolume);
            }
        }

        /// <summary>M12:脚下材质脚步声(音高 = 材质基频 × 确定性抖动)。</summary>
        void PlayFootstep()
        {
            var surf = QuerySurfaceBelow();
            var props = S2PSurfacePhysics.Get(surf);
            uint jitter = (uint)Time.frameCount * 2654435761u;
            float pitch = props.FootstepPitch * (0.92f + 0.16f * ((jitter >> 24) & 15u) / 15f);
            var clip = S2PProceduralAudio.GetClip(surf);
            if (clip != null && _audioSrc != null)
            {
                _audioSrc.pitch = pitch;
                _audioSrc.PlayOneShot(clip, FootstepVolume);
            }
        }

        /// <summary>M12:脚下表面材质查询(向下 1.6m 射线 + 材质场 O(1) 查询;未命中回退玩家位置)。</summary>
        S2PSurfaceType QuerySurfaceBelow()
        {
            Vector3 probe = transform.position + Vector3.down * 0.4f;
            Vector3 queryPos = transform.position;
            if (Physics.Raycast(probe, Vector3.down, out var hit, 1.6f))
                queryPos = hit.point;
            return S2PSurfaceMaterialMap.QueryWorld(queryPos);
        }

        void TryGrab()
        {
            Transform origin = CameraPivot != null ? CameraPivot : transform;
            if (Physics.Raycast(origin.position, origin.forward, out var hit, GrabDistance))
            {
                var prop = hit.rigidbody != null ? hit.rigidbody.GetComponent<S2PDynamicProp>() : hit.collider.GetComponentInParent<S2PDynamicProp>();
                if (prop == null) return;
                _heldProp = prop;
                _heldBody = prop.Body;
                if (_heldBody != null)
                {
                    _heldBody.detectCollisions = true;
                    prop.SetHeld(true);
                }
            }
        }

        void UpdateHeldPosition()
        {
            if (_heldBody == null) { ReleaseHeld(); return; }
            Transform origin = CameraPivot != null ? CameraPivot : transform;
            Vector3 target = origin.position + origin.forward * HoldDistance;
            Vector3 delta = target - _heldBody.position;
            // 速度驱动跟随:足够快可以贴着胸前移动,同时保留物理碰撞(可阻挡敌人)
            Vector3 vel = delta * 12f;
            float maxSpeed = 18f;
            if (vel.magnitude > maxSpeed) vel = vel.normalized * maxSpeed;
            _heldBody.linearVelocity = vel;
            _heldBody.angularVelocity = Vector3.Slerp(_heldBody.angularVelocity, Vector3.zero, 0.15f);
        }

        void ThrowHeld()
        {
            if (_heldBody == null) { ReleaseHeld(); return; }
            Transform origin = CameraPivot != null ? CameraPivot : transform;
            _heldProp.SetHeld(false);
            _heldBody.linearVelocity = origin.forward * ThrowSpeed + Vector3.up * 2f;
            ReleaseHeld();
        }

        void ReleaseHeld()
        {
            if (_heldProp != null) _heldProp.SetHeld(false);
            _heldProp = null;
            _heldBody = null;
        }
    }
}
