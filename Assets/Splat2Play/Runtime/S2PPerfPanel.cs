// Splat2Play — T4.3 运行时性能面板(IMGUI 覆盖层 + ProfilerRecorder + benchmarks.md 写入)
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using GaussianSplatting.Runtime;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Profiling;

namespace Splat2Play.Runtime
{
    /// <summary>
    /// 运行时性能面板:
    /// - 当前可见 splat 数 / 总 splat 数 + 分块可见性 (VisibleChunkCount / TotalChunkCount)
    /// - 排序耗时 (ms) + 渲染耗时 (ms) + 物理步耗时 (ms) + 总帧耗时 (ms)
    /// - GPU 缓冲显存估算 (MB) + 代理网格三角面数(碰撞 / 导航)
    /// - F3 显隐面板 / F4 切换光照模式(Off / Path A+B / Path C) / F5 追加实测数据到 docs/benchmarks.md
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class S2PPerfPanel : MonoBehaviour
    {
        public GaussianSplatRenderer SplatRenderer;
        public ProxyData Proxy;
        public Rect Rect = new(8f, 8f, 360f, 250f);
        public bool Show = true;

        ProfilerRecorder _recSort;
        ProfilerRecorder _recDraw;
        ProfilerRecorder _recCompose;
        ProfilerRecorder _recCalcView;
        ProfilerRecorder _recPhysics;

        float _fps;
        float _frameMs;
        float _sortMs;
        float _renderMs;
        float _physicsMs;
        float _vramMb;
        int _visibleSplats;
        int _sourceSplats;
        string _text = "";
        string _toast = "";
        float _toastUntil;

        void OnEnable()
        {
            _recSort = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GaussianSplat.Sort", 15);
            _recDraw = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GaussianSplat.Draw", 15);
            _recCompose = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GaussianSplat.Compose", 15);
            _recCalcView = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GaussianSplat.CalcView", 15);
            _recPhysics = ProfilerRecorder.StartNew(ProfilerCategory.Physics, "Physics.Processing", 15);
        }

        void OnDisable()
        {
            _recSort.Dispose();
            _recDraw.Dispose();
            _recCompose.Dispose();
            _recCalcView.Dispose();
            _recPhysics.Dispose();
        }

        static float AverageMs(ProfilerRecorder rec)
        {
            if (!rec.Valid || rec.Count == 0) return 0f;
            long sum = 0;
            int n = rec.Count;
            for (int i = 0; i < n; ++i) sum += rec.GetSample(i).Value;
            return (float)(sum / (double)n * 1e-6);
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.f3Key.wasPressedThisFrame) Show = !Show;
                if (kb.f4Key.wasPressedThisFrame) CycleLightMode();
                if (kb.f5Key.wasPressedThisFrame) AppendBenchmarkSnapshot();
            }
            if (!Show) return;

            if (Proxy == null && TryGetComponent<SplatLevel>(out var lvl))
                Proxy = lvl.Proxy;

            _frameMs = Mathf.Lerp(_frameMs, Time.unscaledDeltaTime * 1000f, 0.08f);
            _fps = Mathf.Lerp(_fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f), 0.05f);
            _sortMs = AverageMs(_recSort);
            _renderMs = AverageMs(_recCalcView) + AverageMs(_recDraw) + AverageMs(_recCompose);
            _physicsMs = AverageMs(_recPhysics);

            long totalMem = Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);
            int activeCount = SplatRenderer != null && SplatRenderer.isActiveAndEnabled ? SplatRenderer.splatCount : 0;
            _sourceSplats = Proxy != null && Proxy.SourceSplatCount > 0 ? Proxy.SourceSplatCount : activeCount;
            _visibleSplats = activeCount;

            // M6:GPU 分块剔除的精确可见计数(异步读回,延迟一帧)
            string gpuCullStr = "";
            if (SplatRenderer != null && SplatRenderer.GpuCullingActive)
            {
                int vis = SplatRenderer.GpuCullingVisibleSplats;
                if (vis >= 0)
                {
                    _visibleSplats = vis; // 真实的 GPU 剔除后参与排序/光栅化的精确 splat 数
                    gpuCullStr = SplatRenderer.GpuCullingVisibleChunks >= 0
                        ? $" [GPU块 {SplatRenderer.GpuCullingVisibleChunks}/{SplatRenderer.GpuChunkCount}]"
                        : "";
                }
            }

            string streaming = "关闭";
            if (TryGetComponent<S2PSplatStreaming>(out var st) && st.enabled)
            {
                if (activeCount > 0 && st.TotalChunkCount > 1)
                    _visibleSplats = st.VisibleSplatEstimate;
                string cullDetail = st.IsOccluded ? " [代理遮挡]" : (!st.IsFrustumVisible ? " [视锥外]" : "");
                string chunkStr = st.TotalChunkCount > 1 ? $" 块:{st.VisibleChunkCount}/{st.TotalChunkCount}" : "";
                streaming = $"{st.CurrentState}{cullDetail}{chunkStr}";
            }

            // GPU 显存估算:每 splat 静态数据约 60B + 视图/排序缓冲约 28B ≈ 88B
            _vramMb = activeCount * 88f / (1024f * 1024f);

            string splat = activeCount > 0
                ? $"{_visibleSplats:N0} / {_sourceSplats:N0}{gpuCullStr}"
                : $"0 / {_sourceSplats:N0} (已卸载/剔除)";
            string sh = SplatRenderer != null ? $"SH{SplatRenderer.m_SHOrder}" : "-";
            int navTris = Proxy != null && Proxy.NavigationMesh != null ? Proxy.NavigationMesh.triangles.Length / 3 : 0;
            string proxy = Proxy != null
                ? (navTris > 0 ? $"碰撞 {Proxy.TriangleCount:N0} | 导航 {navTris:N0} 面" : $"{Proxy.TriangleCount:N0} 三角形")
                : "无代理";

            string lightState = "无驱动";
            if (TryGetComponent<S2PLightModulationDriver>(out var ld))
            {
                bool spotOn = ld.enabled && ld.Flashlight != null && ld.Flashlight.isActiveAndEnabled && ld.Mode != S2PLightMode.Off;
                lightState = spotOn ? $"{ld.Mode} (Bias={ld.Bias:F4})" : $"关闭 ({ld.Mode})";
            }

            _text =
                $"[Splat2Play 性能面板 (F3显隐 | F4光照 | F5记录)]\n" +
                $"FPS: {_fps:F0}  总帧耗时: {_frameMs:F2} ms\n" +
                $"排序: {_sortMs:F2} ms | 渲染: {_renderMs:F2} ms | 物理: {_physicsMs:F2} ms\n" +
                $"可见/总 splat: {splat} ({sh})\n" +
                $"代理网格: {proxy}\n" +
                $"流送/剔除: {streaming}\n" +
                $"动态光照: {lightState}\n" +
                $"GPU缓冲估算: {_vramMb:F1} MB | 引擎内存: {totalMem} MB";
        }

        void CycleLightMode()
        {
            if (!TryGetComponent<S2PLightModulationDriver>(out var ld)) return;
            ld.Mode = ld.Mode switch
            {
                S2PLightMode.Off => S2PLightMode.ShadowAndNormal,
                S2PLightMode.ShadowAndNormal => S2PLightMode.NormalOnly_PathC,
                _ => S2PLightMode.Off
            };
            ShowToast($"已切换光照模式: {ld.Mode}");
        }

        public void AppendBenchmarkSnapshot()
        {
            try
            {
                string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                string path = Path.Combine(root, "docs", "benchmarks.md");
                if (!File.Exists(path)) return;

                string sh = SplatRenderer != null ? $"SH{SplatRenderer.m_SHOrder}" : "-";
                string lightMode = TryGetComponent<S2PLightModulationDriver>(out var ld) ? ld.Mode.ToString() : "N/A";
                string streamState = TryGetComponent<S2PSplatStreaming>(out var st) && st.enabled ? st.CurrentState.ToString() : "Off";
                int tris = Proxy != null ? Proxy.TriangleCount : 0;
                string line =
                    $"\n- **实测快照 ({DateTime.Now:yyyy-MM-dd HH:mm:ss})**: " +
                    $"splat=`{_visibleSplats:N0}/{_sourceSplats:N0}` ({sh}) | " +
                    $"FPS=`{_fps:F1}` (`{_frameMs:F2} ms`, Sort=`{_sortMs:F2} ms`, Render=`{_renderMs:F2} ms`, Phys=`{_physicsMs:F2} ms`) | " +
                    $"GPU=`{_vramMb:F1} MB` | 代理=`{tris:N0} tris` | 流送=`{streamState}` | 光照=`{lightMode}`";
                File.AppendAllText(path, line);
                ShowToast("已追加实测快照到 docs/benchmarks.md");
            }
            catch (Exception e)
            {
                ShowToast($"写入失败: {e.Message}");
            }
        }

        void ShowToast(string msg)
        {
            _toast = msg;
            _toastUntil = Time.unscaledTime + 2.5f;
        }

        void OnGUI()
        {
            if (!Show) return;
            GUI.Box(Rect, GUIContent.none);
            GUI.Label(new Rect(Rect.x + 8, Rect.y + 4, Rect.width - 16, Rect.height - 34), _text);

            float btnY = Rect.y + Rect.height - 28f;
            if (GUI.Button(new Rect(Rect.x + 8, btnY, 165, 22), "切换光照模式 (F4)"))
                CycleLightMode();
            if (GUI.Button(new Rect(Rect.x + 180, btnY, 170, 22), "记录到 benchmarks.md (F5)"))
                AppendBenchmarkSnapshot();

            if (Time.unscaledTime < _toastUntil && !string.IsNullOrEmpty(_toast))
            {
                GUI.Box(new Rect(Rect.x, Rect.yMax + 4f, Rect.width, 24f), _toast);
            }
        }
    }
}
