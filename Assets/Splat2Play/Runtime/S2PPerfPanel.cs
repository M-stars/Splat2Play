// Splat2Play — T4.3 运行时性能面板(IMGUI 覆盖层)
// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.Profiling;

namespace Splat2Play.Runtime
{
    /// <summary>运行时性能面板:splat 数 / fps / 帧耗时 / 内存 / 代理与流送状态。</summary>
    [DisallowMultipleComponent]
    public sealed class S2PPerfPanel : MonoBehaviour
    {
        public GaussianSplatRenderer SplatRenderer;
        public ProxyData Proxy;
        public Rect Rect = new(8f, 8f, 300f, 150f);
        public bool Show = true;

        float _fps;
        string _text = "";

        void Update()
        {
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0.05f);
            long totalMem = Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);
            string splat = SplatRenderer != null && SplatRenderer.isActiveAndEnabled
                ? $"{SplatRenderer.splatCount:N0}"
                : "已流送卸载";
            string sh = SplatRenderer != null ? $"SH{SplatRenderer.m_SHOrder}" : "-";
            string proxy = Proxy != null ? $"{Proxy.TriangleCount:N0} 三角形" : "无代理";
            string streaming = "关闭";
            var st = GetComponent<S2PSplatStreaming>();
            if (st != null && st.enabled) streaming = st.CurrentState.ToString();
            _text =
                $"[Splat2Play 性能]\n" +
                $"fps: {_fps:F0}  帧: {Time.unscaledDeltaTime * 1000f:F1} ms\n" +
                $"splat: {splat} ({sh})\n" +
                $"代理: {proxy}\n" +
                $"流送: {streaming}\n" +
                $"托管外内存: {totalMem} MB";
        }

        void OnGUI()
        {
            if (!Show) return;
            GUI.Box(Rect, GUIContent.none);
            GUI.Label(new Rect(Rect.x + 8, Rect.y + 4, Rect.width - 16, Rect.height - 8), _text);
        }
    }
}
