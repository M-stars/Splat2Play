// Splat2Play — 无头全链路冒烟测试(T0.2 渲染 + T1.5 真实烘焙 + T2.1 NavMesh)
// 由 Unity CLI -executeMethod 驱动,产出截图与 benchmarks 数据。
// 运行: Unity.exe -batchmode -executeMethod Splat2Play.Editor.SplatHeadlessSmokeTest.RunFull
//        -projectPath ... -logFile ... (需要图形设备,勿加 -nographics)
// 环境变量:S2P_PLY_PATH(扫描文件,ply/spz)、S2P_OUT_DIR(输出目录)
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using Debug = UnityEngine.Debug;
using System.IO;
using GaussianSplatting.Runtime;
using Splat2Play.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using Unity.AI.Navigation;

namespace Splat2Play.Editor
{
    public static class SplatHeadlessSmokeTest
    {
        static string PlyPath =>
            Environment.GetEnvironmentVariable("S2P_PLY_PATH")
            ?? @"D:\unity_hub\_scans\scene_indoor.ply";
        static string OutDir =>
            Environment.GetEnvironmentVariable("S2P_OUT_DIR")
            ?? @"D:\unity_hub\smoke_out";

        public static void RunFull()
        {
            Directory.CreateDirectory(OutDir);
            var log = new System.Text.StringBuilder();
            try
            {
                // 1. URP Feature 接线(幂等)
                SplatDemoSceneBuilder.SetupUrpFeatures();

                if (!File.Exists(PlyPath))
                {
                    Debug.LogError($"[S2P] 扫描文件不存在:{PlyPath}(等待下载完成后再跑)");
                    EditorApplication.Exit(3);
                    return;
                }
                bool isPly = PlyPath.EndsWith(".ply", StringComparison.OrdinalIgnoreCase);

                var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                    UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                    UnityEditor.SceneManagement.NewSceneMode.Single);

                // 2. 放置 splat 渲染器(绝对路径加载)
                var go = new GameObject("SplatLevel_Scan");
                var renderer = go.AddComponent<GaussianSplatRenderer>();
                renderer.m_SourcePath = PlyPath;
                renderer.m_SHOrder = 2;
                var level = go.AddComponent<SplatLevel>();
                level.SplatRenderer = renderer;

                // 3. 等待异步加载(batch 下编辑器 tick 不泵 → 反射直接驱动 UpdateLoading 状态机)
                var sw = Stopwatch.StartNew();
                renderer.Reload();
                var updateLoading = typeof(GaussianSplatRenderer).GetMethod("UpdateLoading",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (updateLoading == null) throw new Exception("反射未找到 UpdateLoading");
                int waitMs = 0;
                while (renderer.isLoading && waitMs < 180_000)
                {
                    updateLoading.Invoke(renderer, null);
                    if (renderer.loadError != null) break;
                    System.Threading.Thread.Sleep(20);
                    waitMs += 20;
                }
                if (renderer.loadError != null)
                    throw new Exception($"splat 加载失败:{renderer.loadError}");
                if (renderer.splatCount == 0)
                    throw new Exception("splat 数量为 0");
                float loadSec = sw.ElapsedMilliseconds / 1000f;
                log.AppendLine($"加载: {renderer.splatCount:N0} splats, {loadSec:F1}s, bounds={renderer.localBounds.size:F1}");
                Debug.Log($"[S2P] 加载完成 {renderer.splatCount:N0} splats");

                // 4. 相机渲染一张远景图(验证 splat 真实出现在画面里)
                // 参照方块:验证 URP 批处理下不透明网格是否正常渲染
                var refCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                refCube.name = "RefCube";
                var cubeb = renderer.localBounds;
                refCube.transform.position = cubeb.center + new Vector3(0f, cubeb.size.y * 0.05f, -cubeb.size.z * 0.28f);
                refCube.transform.localScale = Vector3.one * (cubeb.size.magnitude * 0.04f);

                var camGo = new GameObject("Cam");
                var cam = camGo.AddComponent<Camera>();
                var b = renderer.localBounds;
                camGo.transform.position = b.center + new Vector3(0f, 0f, -b.size.magnitude * 0.6f);
                camGo.transform.LookAt(b.center);
                cam.fieldOfView = 60f;
                float nonBlack = CapturePng(cam, Path.Combine(OutDir, "render_splat_far.png"));
                log.AppendLine($"渲染(远景): 非黑像素占比 {nonBlack:P1} → render_splat_far.png");
                Debug.Log($"[S2P] 非黑像素占比 {nonBlack:P1}");

                // 5. M1 真实烘焙(仅 PLY;SPZ 样例只做渲染冒烟)
                ProxyBakeResult bakeResult = null;
                float bakeSec = 0f;
                if (isPly)
                {
                    var settings = ScriptableObject.CreateInstance<ProxyBakeSettings>();
                    settings.VoxelResolution = 256;
                    settings.MaxTriangles = 150000;
                    sw.Restart();
                    bakeResult = SplatProxyBaker.Bake(PlyPath, settings,
                        (s, t) => Debug.Log($"[S2P][烘焙 {t:P0}] {s}"));
                    bakeSec = sw.ElapsedMilliseconds / 1000f;
                    log.AppendLine($"烘焙: {bakeSec:F1}s, {bakeResult.TriangleCount:N0} 三角形, {bakeResult.Mesh.vertexCount:N0} 顶点");
                    Debug.Log($"[S2P] 烘焙完成 {bakeSec:F1}s\n{bakeResult.Log}");
                }

                // 6. 装配碰撞 + NavMesh 烘焙 + 调试截图(仅有烘焙产物时)
                ProxyData proxy = null;
                bool navOk = false;
                if (bakeResult != null)
                {
                    var settings = ScriptableObject.CreateInstance<ProxyBakeSettings>();
                    settings.VoxelResolution = 256;
                    settings.MaxTriangles = 150000;
                    proxy = ScriptableObject.CreateInstance<ProxyData>();
                    proxy.CollisionMesh = bakeResult.Mesh;
                    proxy.SceneBounds = bakeResult.SceneBounds;
                    proxy.SourceSplatCount = renderer.splatCount;
                    proxy.TriangleCount = bakeResult.TriangleCount;
                    proxy.VoxelResolution = settings.VoxelResolution;
                    proxy.DensityThreshold = settings.DensityThreshold;
                    proxy.BakeLog = bakeResult.Log;
                    level.Proxy = proxy;
                    level.BuildCollision();

                    var surface = go.GetComponent<NavMeshSurface>() ?? go.AddComponent<NavMeshSurface>();
                    surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders; // 代理网格只有碰撞体,无渲染网格
                    surface.collectObjects = CollectObjects.Children;
                    surface.BuildNavMesh();
                    int triChildren = NavMesh.CalculateTriangulation().vertices.Length / 3;
                    // 对照实验:All 模式
                    surface.collectObjects = CollectObjects.All;
                    surface.BuildNavMesh();
                    int triAll = NavMesh.CalculateTriangulation().vertices.Length / 3;
                    navOk = triChildren > 0 || triAll > 0;
                    log.AppendLine($"NavMesh: Children={triChildren} All={triAll}, ok={navOk}, colliderBounds={go.GetComponentInChildren<MeshCollider>().bounds.size:F2}");

                    // 相机移到百分位盒内近景(室内场景应有可辨识画面)
                    var sb = proxy.SceneBounds;
                    camGo.transform.position = sb.center + new Vector3(0f, Mathf.Min(1.6f, sb.size.y * 0.35f), -sb.size.magnitude * 0.4f);
                    camGo.transform.LookAt(sb.center);
                    float nbClose = CapturePng(cam, Path.Combine(OutDir, "render_splat_close.png"));
                    log.AppendLine($"近景(splat): 非黑像素 {nbClose:P1} → render_splat_close.png");

                    level.ShowCollisionDebug = true;
                    level.BuildCollision();
                    renderer.enabled = false;
                    float nb2 = CapturePng(cam, Path.Combine(OutDir, "render_collision.png"));
                    renderer.enabled = true;
                    log.AppendLine($"近景(碰撞调试): 非黑像素 {nb2:P1} → render_collision.png");

                    // 几何验证(camera.Render() 在 URP 下不画不透明网格 → 用 Raycast 证明碰撞体工作)
                    var probe = sb.center + new Vector3(0f, Mathf.Min(1.6f, sb.size.y * 0.3f), 0f);
                    int hitCount = 0;
                    var dirs = new[] { Vector3.down, Vector3.up, Vector3.forward, Vector3.back, Vector3.left, Vector3.right };
                    foreach (var d in dirs)
                    {
                        if (Physics.Raycast(probe, d, out var hit, 100f))
                        {
                            ++hitCount;
                            log.AppendLine($"[碰撞验证] {d} → 命中 {hit.collider.name} 距离 {hit.distance:F2}m");
                        }
                        else log.AppendLine($"[碰撞验证] {d} → 未命中");
                    }
                    log.AppendLine($"[碰撞验证] 命中 {hitCount}/6 方向");
                }

                // 7. 写 benchmark 数据
                string benchLine = isPly
                    ? $"| {DateTime.Now:yyyy-MM-dd HH:mm} | {Path.GetFileName(PlyPath)} | {renderer.splatCount:N0} | 体素256/阈值0.5 | 加载 {loadSec:F1}s;烘焙 {bakeSec:F1}s;三角形 {(bakeResult?.TriangleCount ?? 0):N0};NavMesh {navOk};非黑像素 {nonBlack:P1} |\n"
                    : $"| {DateTime.Now:yyyy-MM-dd HH:mm} | {Path.GetFileName(PlyPath)}(SPZ 渲染冒烟) | {renderer.splatCount:N0} | — | 加载 {loadSec:F1}s;非黑像素 {nonBlack:P1} |\n";
                File.AppendAllText(@"D:\unity_hub\Splat2Play\docs\benchmarks.md", benchLine);
                File.WriteAllText(Path.Combine(OutDir, "smoke_report.txt"), log.ToString());
                Debug.Log($"[S2P] 冒烟测试完成\n{log}");

                // 8. 落盘 ProxyData 资产(供后续向导/场景复用)
                if (proxy != null)
                {
                    string assetDir = "Assets/Splat2Play/Baked";
                    if (!AssetDatabase.IsValidFolder(assetDir)) AssetDatabase.CreateFolder("Assets/Splat2Play", "Baked");
                    string assetPath = $"{assetDir}/smoke_Proxy.asset";
                    var existing = AssetDatabase.LoadAssetAtPath<ProxyData>(assetPath);
                    if (existing != null) AssetDatabase.DeleteAsset(assetPath);
                    AssetDatabase.CreateAsset(proxy, assetPath);
                    AssetDatabase.AddObjectToAsset(bakeResult.Mesh, proxy);
                    EditorUtility.SetDirty(proxy);
                    AssetDatabase.SaveAssets();
                }

                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[S2P] 冒烟失败:{e}");
                try { File.WriteAllText(Path.Combine(OutDir, "smoke_report.txt"), log + "\nFAIL: " + e); } catch { }
                EditorApplication.Exit(1);
            }
        }

        static float CapturePng(Camera cam, string pngPath)
        {
            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGBFloat);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGBAFloat, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            cam.targetTexture = null;
            RenderTexture.active = null;
            rt.Release();
            File.WriteAllBytes(pngPath, tex.EncodeToPNG());
            float nonBlack = NonBlackRatio(tex);
            UnityEngine.Object.DestroyImmediate(tex);
            return nonBlack;
        }

        static float NonBlackRatio(Texture2D tex)
        {
            var pixels = tex.GetPixels();
            int nonBlack = 0;
            foreach (var p in pixels)
                if (p.maxColorComponent > 0.02f) ++nonBlack;
            return (float)nonBlack / pixels.Length;
        }
    }
}
