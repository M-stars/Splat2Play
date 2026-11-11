// Splat2Play — T4.3 运行时性能面板(IMGUI 覆盖层)
// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Profiling;

namespace Splat2Play.Runtime
{
    /// <summary>运行时性能面板:splat 数 / fps / 帧耗时 / 显存估算 / 代理与流送状态(F3 切换显隐)。</summary>
    [DisallowMultipleComponent]
    public sealed class S2PPerfPanel : MonoBehaviour
    {
        public GaussianSplatRenderer SplatRenderer;
        public ProxyData Proxy;
        public Rect Rect = new(8f, 8f, 320f, 175f);
        public bool Show = true;

        float _fps;
        string _text = "";

        void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f3Key.wasPressedThisFrame)
                Show = !Show;
            if (!Show) return;

            if (Proxy == null && TryGetComponent<SplatLevel>(out var lvl))
                Proxy = lvl.Proxy;

            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0.05f);
            long totalMem = Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);
            int activeCount = SplatRenderer != null && SplatRenderer.isActiveAndEnabled ? SplatRenderer.splatCount : 0;
            int sourceCount = Proxy != null && Proxy.SourceSplatCount > 0 ? Proxy.SourceSplatCount : activeCount;
            // GPU 显存估算:每 splat 静态数据约 60B + 视图/排序缓冲约 24B ≈ 84B
            float vramMb = activeCount * 84f / (1024f * 1024f);

            string splat = activeCount > 0
                ? $"{activeCount:N0} / {sourceCount:N0}"
                : $"0 / {sourceCount:N0} (已卸载/剔除)";
            string sh = SplatRenderer != null ? $"SH{SplatRenderer.m_SHOrder}" : "-";
            string proxy = Proxy != null ? $"{Proxy.TriangleCount:N0} 三角形" : "无代理";

            string streaming = "关闭";
            if (TryGetComponent<S2PSplatStreaming>(out var st) && st.enabled)
            {
                string cullDetail = st.IsOccluded ? " [代理遮挡]" : (!st.IsFrustumVisible ? " [视锥外]" : "");
                streaming = $"{st.CurrentState}{cullDetail}";
            }

            bool lightOn = TryGetComponent<S2PLightModulationDriver>(out var ld)
                           && ld.enabled && ld.Flashlight != null && ld.Flashlight.isActiveAndEnabled;

            _text =
                $"[Splat2Play 性能面板 (F3)]\n" +
                $"fps: {_fps:F0}  帧耗时: {Time.unscaledDeltaTime * 1000f:F1} ms\n" +
                $"splat: {splat} ({sh})\n" +
                $"代理: {proxy}\n" +
                $"流送/剔除: {streaming}\n" +
                $"动态光照: {(lightOn ? "开启 (ShadowsOnly+合成)" : "关闭")}\n" +
                $"显存估算: {vramMb:F1} MB | 引擎总内存: {totalMem} MB";
        }

        void OnGUI()
        {
            if (!Show) return;
            GUI.Box(Rect, GUIContent.none);
            GUI.Label(new Rect(Rect.x + 8, Rect.y + 4, Rect.width - 16, Rect.height - 8), _text);
        }
    }
}
