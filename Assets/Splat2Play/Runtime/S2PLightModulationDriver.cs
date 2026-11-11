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

        const int kMaxChunkBiases = 8;
        readonly Vector4[] _chunkMinBias = new Vector4[kMaxChunkBiases];
        readonly Vector4[] _chunkMax = new Vector4[kMaxChunkBiases];

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
        }
    }
}
