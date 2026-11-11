// Splat2Play — SplatLevel:把 splat 渲染器与游戏接入件(碰撞/补丁)装配成一个关卡组件
// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace Splat2Play.Runtime
{
    /// <summary>
    /// 挂在关卡根节点:负责按 ProxyData 装配碰撞层(网格碰撞体 + 补丁盒体 + 可选近似阴影投射)。
    /// 场景结构约定:
    ///   SplatLevel (本组件)
    ///   ├── GaussianSplatRenderer (m_SourcePath 指向 StreamingAssets 下的扫描文件)
    ///   ├── Collision (子节点:MeshCollider + 补丁 BoxCollider,由本组件生成)
    ///   └── NavMeshSurface (M2,可由向导添加)
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SplatLevel : MonoBehaviour
    {
        [Tooltip("扫描场景渲染器(与 SplatLevel 同节点或子节点)")]
        public GaussianSplatRenderer SplatRenderer;

        [Tooltip("M1 烘焙产物")]
        public ProxyData Proxy;

        [Tooltip("碰撞层是否可见调试网格")]
        public bool ShowCollisionDebug;

        [Tooltip("M3:代理网格是否以 ShadowsOnly 模式投射近似动态阴影")]
        public bool CastApproximateShadows = true;

        const string kCollisionChild = "S2P_Collision";

        /// <summary>按 ProxyData 装配/重建碰撞子节点。编辑器与运行时均可调用。</summary>
        public void BuildCollision()
        {
            if (Proxy == null || Proxy.CollisionMesh == null)
            {
                Debug.LogWarning("[Splat2Play] ProxyData 或碰撞网格为空,跳过碰撞装配", this);
                return;
            }

            var old = transform.Find(kCollisionChild);
            if (old != null)
            {
                if (Application.isPlaying) Destroy(old.gameObject);
                else DestroyImmediate(old.gameObject);
            }

            var collisionGo = new GameObject(kCollisionChild);
            collisionGo.transform.SetParent(transform, false);
            collisionGo.layer = gameObject.layer;
            collisionGo.isStatic = true;

            var mc = collisionGo.AddComponent<MeshCollider>();
            mc.sharedMesh = Proxy.CollisionMesh;

            foreach (var patch in Proxy.PatchBoxes)
            {
                var boxGo = new GameObject("S2P_PatchBox");
                boxGo.transform.SetParent(collisionGo.transform, false);
                boxGo.transform.localPosition = patch.Center;
                boxGo.transform.localRotation = Quaternion.Euler(patch.RotationEuler);
                boxGo.transform.localScale = Vector3.one;
                var box = boxGo.AddComponent<BoxCollider>();
                box.size = patch.Size;
            }

            if (ShowCollisionDebug || CastApproximateShadows)
            {
                var mf = collisionGo.AddComponent<MeshFilter>();
                mf.sharedMesh = Proxy.CollisionMesh;
                var mr = collisionGo.AddComponent<MeshRenderer>();
                var shader = Shader.Find("Universal Render Pipeline/Lit")
                             ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                             ?? Shader.Find("Universal Render Pipeline/Unlit");
                var mat = new Material(shader)
                {
                    name = "S2P_CollisionDebug",
                    hideFlags = HideFlags.DontSaveInEditor | HideFlags.DontSaveInBuild
                };
                mat.SetColor("_BaseColor", new Color(0f, 1f, 0.4f, 0.35f));
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = ShowCollisionDebug
                    ? (CastApproximateShadows ? ShadowCastingMode.On : ShadowCastingMode.Off)
                    : ShadowCastingMode.ShadowsOnly;
            }

            S2PLightProxyRegistry.RegisterShadowMesh(Proxy.CollisionMesh);
        }
    }
}
