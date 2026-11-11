// Splat2Play — M3:近似动态光照驱动器
// 每帧(或手动)把手电筒参数写入全局,供 S2P fork 的合成 shader 采样:
//   阴影 = URP 为该 SpotLight 渲染的真实阴影图(附加光切片)
//   近似性:遮挡判定来自代理网格之外的 URP 阴影图 —— 实际是真实阴影而非代理近似,
//   splat 管线内自阴影仍属近似(见技术博客)。
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Splat2Play.Runtime
{
    public enum S2PLightMode
    {
        Off = 0,
        ShadowAndNormal = 1,
        NormalOnly_PathC = 2
    }

    [ExecuteInEditMode]
    [DisallowMultipleComponent]
    public sealed class S2PLightModulationDriver : MonoBehaviour
    {
        [Serializable]
        public struct ChunkBiasOverride
        {
            [Tooltip("Chunk 世界或局部空间包围盒")]
            public Bounds Bounds;
            [Tooltip("该 Chunk 叠加的额外阴影深度偏移")]
            public float BiasOffset;
            [Tooltip("Bounds 是否为当前节点局部坐标")]
            public bool IsLocalSpace;
        }

        public S2PLightMode Mode = S2PLightMode.ShadowAndNormal;
        public Light Flashlight;
        [Range(0f, 3f)] public float Intensity = 1.6f;
        [Range(0f, 1f)] [Tooltip("手电筒光锥外/阴影区的环境保留亮度比例(越小明暗反差越强)")]
        public float AmbientFloor = 0.35f;
        [Range(0f, 1f)] [Tooltip("基于屏幕空间深度重建法线的 N·L 漫反射调制强度(§5.3)")]
        public float NormalStrength = 0.35f;
        [Tooltip("调制作用半径(米,§5.3):超出此距离的远景像素跳过光照与阴影计算以节省带宽")]
        [Range(2f, 200f)] public float ModulationRadius = 35f;
        [Tooltip("全局阴影深度比较偏移(反转 z 单位)")]
        public float Bias = 0.002f;
        [Tooltip("附加光阴影切片索引(单灯场景 = 0)")]
        public int ShadowSlice;
        [Tooltip("逐 Chunk 深度偏移微调列表(最多前 8 个生效,§5.3 T3.4)")]
        public List<ChunkBiasOverride> ChunkBiases = new();

        [Header("M7 多光源逐高斯动态重光照")]
        [Tooltip("额外动态点光源/聚光灯(上限 8;为空时自动收集场景中开启的灯,排除主手电筒与方向光)")]
        public List<Light> AdditionalDynamicLights = new();
        [Range(0f, 2f)] [Tooltip("逐高斯动态重光照总强度(0 = 关闭逐高斯路径,走合成器逐像素回退路径)")]
        public float PerSplatLightStrength = 1f;
        [Range(0f, 1f)] [Tooltip("最短轴法线项权重:1 = 完全按椭球法线着色,0 = 退化为无方向环境项")]
        public float SplatNormalWeight = 0.9f;
        [Range(1f, 128f)] [Tooltip("Blinn-Phong 基准 shininess(随高斯扁平度自适应缩放)")]
        public float SpecularPower = 32f;

        /// <summary>注入 GPU 的单盏动态光源(48 字节,3×float4)。</summary>
        [Serializable]
        public struct S2PGpuLight
        {
            public Vector4 PosAndInvRangeSq;     // xyz=worldPos, w=1/range²
            public Vector4 DirAndCosHalfAngle;   // xyz=forward(单位), w=cos(spotAngle*0.5);<-0.999 表示点光源
            public Vector4 ColorAndSpecPower;    // rgb=linearColor*intensity, w=shininess(如 16..64)
        }

        const int kMaxLights = 8;
        const int kMaxChunkBiases = 8;
        readonly Vector4[] _chunkMinBias = new Vector4[kMaxChunkBiases];
        readonly Vector4[] _chunkMax = new Vector4[kMaxChunkBiases];
        readonly Vector4[] _lightA = new Vector4[kMaxLights];
        readonly Vector4[] _lightB = new Vector4[kMaxLights];
        readonly Vector4[] _lightC = new Vector4[kMaxLights];
        Light[] _autoLightsCache = Array.Empty<Light>();
        float _nextLightCollectTime;

        public void SetGlobalsNow()
        {
            if (Mode == S2PLightMode.Off || Flashlight == null || !Flashlight.isActiveAndEnabled)
            {
                Shader.SetGlobalInt("_S2PLightEnabled", 0);
                return;
            }
            S2PLightProxyRegistry.RegisterSpotLight(Flashlight);
            var t = Flashlight.transform;
            Shader.SetGlobalInt("_S2PLightEnabled", (int)Mode);
            Shader.SetGlobalVector("_S2PLightPos", t.position);
            Shader.SetGlobalVector("_S2PLightDir", t.forward);
            Shader.SetGlobalVector("_S2PLightColor", (Vector4)Flashlight.color);
            Shader.SetGlobalFloat("_S2PLightRange", Mathf.Max(1f, Flashlight.range));
            Shader.SetGlobalFloat("_S2PLightCos", Mathf.Cos(Flashlight.spotAngle * 0.5f * Mathf.Deg2Rad));
            Shader.SetGlobalInt("_S2PLightSlice", ShadowSlice);
            Shader.SetGlobalFloat("_S2PBias", Bias);
            Shader.SetGlobalFloat("_S2PIntensity", Intensity);
            Shader.SetGlobalFloat("_S2PAmbient", AmbientFloor);
            Shader.SetGlobalFloat("_S2PNormalStrength", NormalStrength);
            Shader.SetGlobalFloat("_S2PModulationRadius", Mathf.Max(1f, ModulationRadius));

            int count = ChunkBiases != null ? Mathf.Min(ChunkBiases.Count, kMaxChunkBiases) : 0;
            for (int i = 0; i < count; ++i)
            {
                var cb = ChunkBiases[i];
                Vector3 c = cb.IsLocalSpace ? transform.TransformPoint(cb.Bounds.center) : cb.Bounds.center;
                Vector3 s = cb.IsLocalSpace ? Vector3.Scale(cb.Bounds.size, transform.lossyScale) : cb.Bounds.size;
                Vector3 ext = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)) * 0.5f;
                Vector3 mn = c - ext;
                Vector3 mx = c + ext;
                _chunkMinBias[i] = new Vector4(mn.x, mn.y, mn.z, cb.BiasOffset);
                _chunkMax[i] = new Vector4(mx.x, mx.y, mx.z, 0f);
            }
            Shader.SetGlobalInt("_S2PChunkBiasCount", count);
            if (count > 0)
            {
                Shader.SetGlobalVectorArray("_S2PChunkMinBias", _chunkMinBias);
                Shader.SetGlobalVectorArray("_S2PChunkMax", _chunkMax);
            }

            PackDynamicLights();
        }

        /// <summary>
        /// M7 T7.2:收集并打包额外动态光源(上限 8),通过 Shader.SetGlobalVectorArray
        /// 注入 CSCalcViewData(逐高斯路径)与 GaussianComposite(逐像素回退路径)。
        /// 主手电筒不参与(保留 M3 屏幕空间阴影路径,避免双重光照);方向光属于环境 SH 也不参与。
        /// </summary>
        void PackDynamicLights()
        {
            IEnumerable<Light> source;
            if (AdditionalDynamicLights != null && AdditionalDynamicLights.Count > 0)
            {
                source = AdditionalDynamicLights;
            }
            else
            {
                // 自动收集:0.5s 节流,避免每帧 FindObjectsByType 开销
                if (Time.unscaledTime >= _nextLightCollectTime)
                {
                    _autoLightsCache = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
                    _nextLightCollectTime = Time.unscaledTime + 0.5f;
                }
                source = _autoLightsCache;
            }

            int n = 0;
            foreach (var l in source)
            {
                if (n >= kMaxLights) break;
                if (l == null || !l.isActiveAndEnabled) continue;
                if (l == Flashlight) continue;
                if (l.type != LightType.Point && l.type != LightType.Spot) continue;
                var t = l.transform;
                float range = Mathf.Max(0.05f, l.range);
                Color c = l.color.linear * Mathf.Max(0f, l.intensity);
                _lightA[n] = new Vector4(t.position.x, t.position.y, t.position.z, 1f / (range * range));
                _lightB[n] = new Vector4(t.forward.x, t.forward.y, t.forward.z,
                    l.type == LightType.Spot ? Mathf.Cos(l.spotAngle * 0.5f * Mathf.Deg2Rad) : -1f);
                _lightC[n] = new Vector4(c.r, c.g, c.b, Mathf.Clamp(SpecularPower, 1f, 128f));
                ++n;
            }
            Shader.SetGlobalInt("_S2PDynamicLightCount", n);
            Shader.SetGlobalFloat("_S2PPerSplatLightStrength", PerSplatLightStrength);
            Shader.SetGlobalFloat("_S2PSplatNormalWeight", SplatNormalWeight);
            if (n > 0)
            {
                Shader.SetGlobalVectorArray("_S2PGpuLightA", _lightA);
                Shader.SetGlobalVectorArray("_S2PGpuLightB", _lightB);
                Shader.SetGlobalVectorArray("_S2PGpuLightC", _lightC);
            }
        }

        void LateUpdate()
        {
            if (isActiveAndEnabled) SetGlobalsNow();
        }

        void OnEnable() => SetGlobalsNow();

        void OnDisable()
        {
            if (Flashlight != null) S2PLightProxyRegistry.UnregisterSpotLight(Flashlight);
            Shader.SetGlobalInt("_S2PLightEnabled", 0);
            Shader.SetGlobalInt("_S2PDynamicLightCount", 0);
        }
    }
}
