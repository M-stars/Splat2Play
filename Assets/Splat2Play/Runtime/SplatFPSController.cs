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

        CharacterController _cc;
        float _yaw, _pitch;
        float _verticalVel;

        void OnEnable()
        {
            _cc = GetComponent<CharacterController>();
            var euler = transform.eulerAngles;
            _yaw = euler.y;
            _pitch = CameraPivot != null ? CameraPivot.localEulerAngles.x : 0f;
            if (_pitch > 180f) _pitch -= 360f;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        void OnDisable()
        {
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
        }
    }
}
