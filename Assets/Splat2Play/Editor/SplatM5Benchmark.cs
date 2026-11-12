// Splat2Play — M5 无头烘焙基准(CPU 确定性后端 vs GPU Compute 后端)
// 由 Unity CLI -executeMethod 驱动,对真实扫描资产对比 256³/512³ 体素化 + SDF 平滑耗时,
// 结果追加到 docs/benchmarks.md。运行示例:
//   Unity.exe -batchmode -executeMethod Splat2Play.Editor.SplatM5Benchmark.Run
//             -projectPath ... -logFile ... (需要图形设备,勿加 -nographics)
// 环境变量:S2P_PLY_PATH(扫描文件,ply/spz),默认 D:\unity_hub\_scans\scene_bonsai.ply
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using System.IO;
using Splat2Play.Runtime;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Splat2Play.Editor
{
    public static class SplatM5Benchmark
    {
        static string SourcePath =>
            Environment.GetEnvironmentVariable("S2P_PLY_PATH")
            ?? @"D:\unity_hub\_scans\scene_bonsai.ply";
        static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        public static void Run()
        {
            try
            {
                if (!File.Exists(SourcePath))
                {
                    Debug.LogError($"[M5Bench] 扫描文件不存在:{SourcePath}");
                    EditorApplication.Exit(3);
                    return;
                }

                var report = new System.Text.StringBuilder();
                report.AppendLine($"\n## M5 烘焙后端基准({DateTime.Now:yyyy-MM-dd HH:mm})");
                report.AppendLine($"\n资产:`{Path.GetFileName(SourcePath)}`({new FileInfo(SourcePath).Length / 1024f / 1024f:F1} MB)\n");
                report.AppendLine("| 分辨率 | 后端 | 纯 GPU 派发 | 体素化阶段(含读回) | 烘焙总时长 | 三角形 |");
                report.AppendLine("|---|---|---|---|---|---|");

                foreach (var res in new[] { 256, 512 })
                {
                    foreach (var backend in new[] { ProxyBakeBackend.CPUJobsDeterministic, ProxyBakeBackend.GPUCompute })
                    {
                        if (backend == ProxyBakeBackend.GPUCompute && !SystemInfo.supportsComputeShaders)
                        {
                            Debug.Log("[M5Bench] 设备不支持 Compute,跳过 GPU");
                            continue;
                        }
                        var settings = ScriptableObject.CreateInstance<ProxyBakeSettings>();
                        settings.VoxelResolution = res;
                        settings.MaxTriangles = 150000;
                        settings.Backend = backend;
                        settings.UseSDF = res < 512; // 512³ 自动关 SDF 的规则与生产一致
                        try
                        {
                            var sw = Stopwatch.StartNew();
                            long voxMs = -1;
                            using var result = SplatProxyBaker.Bake(SourcePath, settings, (stage, t) =>
                            {
                                // 从烘焙日志阶段提取体素化/SDF 阶段耗时
                                if (stage.Contains("体素化完成") || stage.Contains("GPU 体素化完成"))
                                    voxMs = sw.ElapsedMilliseconds;
                            });
                            long total = sw.ElapsedMilliseconds;
                            long dispatchMs = S2PGpuVoxelBaker.LastDispatchMs;
                            string log = result.Log;
                            foreach (var line in log.Split('\n'))
                            {
                                if (line.Contains("GPU Compute 体素化"))
                                {
                                    var m = System.Text.RegularExpressions.Regex.Match(line, @"耗时 (\d+) ms");
                                    if (m.Success) voxMs = long.Parse(m.Groups[1].Value);
                                }
                                // CPU 侧体素化阶段耗时:从日志阶段时间戳提取
                                if (line.Contains("体素化完成") && !line.Contains("GPU"))
                                {
                                    var m2 = System.Text.RegularExpressions.Regex.Match(line, @"\[\s*(\d+) ms\]");
                                    if (m2.Success) voxMs = long.Parse(m2.Groups[1].Value);
                                }
                            }
                            report.AppendLine($"| {res}³ | {(backend == ProxyBakeBackend.GPUCompute ? "GPUCompute" : "CPU 确定性")} " +
                                $"| {(backend == ProxyBakeBackend.GPUCompute && dispatchMs >= 0 ? dispatchMs + " ms" : "—")} " +
                                $"| {(voxMs >= 0 ? voxMs + " ms" : "—")} " +
                                $"| {total / 1000f:F1} s | {result.TriangleCount:N0} |");
                            Debug.Log($"[M5Bench] {res}³ {backend}: 总 {total / 1000f:F1}s, {result.TriangleCount:N0} tris\n{log}");
                        }
                        finally
                        {
                            UnityEngine.Object.DestroyImmediate(settings);
                        }
                    }
                }

                string docsDir = Path.Combine(RepoRoot, "docs");
                Directory.CreateDirectory(docsDir);
                string benchPath = Path.Combine(docsDir, "benchmarks.md");
                if (!File.Exists(benchPath)) File.WriteAllText(benchPath, "# Splat2Play — 性能基准(benchmarks)\n");
                File.AppendAllText(benchPath, report.ToString());
                Debug.Log($"[M5Bench] 完成\n{report}");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[M5Bench] 失败:{e}");
                EditorApplication.Exit(1);
            }
        }
    }
}
