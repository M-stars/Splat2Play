// Splat2Play — M3:近似动态光照驱动器
// 每帧(或手动)把手电筒参数写入全局,供 S2P fork 的合成 shader 采样:
//   阴影 = URP 为该 SpotLight 渲染的真实阴影图(附加光切片)
//   近似性:遮挡判定来自代理网格之外的 URP 阴影图 —— 实际是真实阴影而非代理近似,
//   splat 管线内自阴影仍属近似(见技术博客)。
// SPDX-License-Identifier: MIT

using UnityEngine;

namespace Splat2Play.Runtime
{
    [ExecuteInEditMode]
    [DisallowMultipleComponent]
    public sealed class S2PLightModulationDriver : MonoBehaviour
    {
        public Light Flashlight;
        [Range(0f, 3f)] public float Intensity = 1.6f;
        [Tooltip("阴影深度比较偏移(反转 z 单位)")]
        public float Bias = 0.002f;
        [Tooltip("附加光阴影切片索引(单灯场景 = 0)")]
        public int ShadowSlice;

        public void SetGlobalsNow()
        {
            if (Flashlight == null || !Flashlight.isActiveAndEnabled) return;
            var t = Flashlight.transform;
            Shader.SetGlobalVector("_S2PLightPos", t.position);
            Shader.SetGlobalVector("_S2PLightDir", t.forward);
            Shader.SetGlobalFloat("_S2PLightRange", Mathf.Max(1f, Flashlight.range));
            Shader.SetGlobalFloat("_S2PLightCos", Mathf.Cos(Flashlight.spotAngle * 0.5f * Mathf.Deg2Rad));
            Shader.SetGlobalInt("_S2PLightSlice", ShadowSlice);
            Shader.SetGlobalFloat("_S2PBias", Bias);
            Shader.SetGlobalFloat("_S2PIntensity", Intensity);
        }

        void LateUpdate()
        {
            if (isActiveAndEnabled) SetGlobalsNow();
        }

        void OnEnable() => SetGlobalsNow();
    }
}
