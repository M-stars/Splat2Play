// Splat2Play — M3:光照代理注册表(渲染器 Feature 与场景解耦的静态桥)
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

namespace Splat2Play.Runtime
{
    /// <summary>
    /// M3 光照调制的数据源注册表:代理网格(遮挡体)+ 手电筒(SpotLight)。
    /// 由 SplatLevel / 场景装配注册;S2PLightModulationFeature 每帧读取。
    /// </summary>
    public static class S2PLightProxyRegistry
    {
        static readonly List<Mesh> s_ShadowMeshes = new();
        static readonly List<Light> s_SpotLights = new();

        public static IReadOnlyList<Mesh> ShadowMeshes => s_ShadowMeshes;
        public static IReadOnlyList<Light> SpotLights => s_SpotLights;

        public static void RegisterShadowMesh(Mesh mesh)
        {
            if (mesh != null && !s_ShadowMeshes.Contains(mesh))
                s_ShadowMeshes.Add(mesh);
        }

        public static void UnregisterShadowMesh(Mesh mesh) => s_ShadowMeshes.Remove(mesh);

        public static void RegisterSpotLight(Light light)
        {
            if (light != null && light.type == LightType.Spot && !s_SpotLights.Contains(light))
                s_SpotLights.Add(light);
        }

        public static void UnregisterSpotLight(Light light) => s_SpotLights.Remove(light);

        public static void Clear()
        {
            s_ShadowMeshes.Clear();
            s_SpotLights.Clear();
        }
    }
}
