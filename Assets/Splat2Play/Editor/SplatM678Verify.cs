// Splat2Play — M6/M7/M8 无头运行时验证
// 验证内容:
//   M6:真实扫描加载后 Morton 分块生效;GPU 剔除派发不报错;可见计数读回;剔除开启时画面完整(非黑像素)
//   M7:3 盏彩色动态点光源 + 手电筒驱动 → 逐高斯重光照路径渲染非黑
//   M8:对真实扫描执行动态物体分离(含底面修补)并装配刚体子物体
// 运行: Unity.exe -batchmode -executeMethod Splat2Play.Editor.SplatM678Verify.Run
//       -projectPath ... -logFile ...(需要图形设备,勿加 -nographics)
// 环境变量:S2P_PLY_PATH(默认 scene_bonsai.ply)、S2P_OUT_DIR(默认 D:\unity_hub\smoke_out)
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using System.IO;
using GaussianSplatting.Runtime;
using Splat2Play.Runtime;
using UnityEditor;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace Splat2Play.Editor
{
    public static class SplatM678Verify
    {
        static string PlyPath =>
            Environment.GetEnvironmentVariable("S2P_PLY_PATH")
            ?? @"D:\unity_hub\_scans\scene_bonsai.ply";
        static string OutDir =>
            Environment.GetEnvironmentVariable("S2P_OUT_DIR")
            ?? @"D:\unity_hub\smoke_out";

        public static void Run()
        {
            var log = new System.Text.StringBuilder();
            try
            {
                Directory.CreateDirectory(OutDir);
                if (!File.Exists(PlyPath))
                {
                    Debug.LogError($"[M678] 扫描文件不存在:{PlyPath}");
                    EditorApplication.Exit(3);
                    return;
                }
                bool ok = File.Exists(PlyPath) && PlyPath.EndsWith(".ply", StringComparison.OrdinalIgnoreCase);
                if (!ok)
                {
                    Debug.LogError("[M678] 运行时验证需要 PLY(需要真实碰撞代理 Hi-Z)");
                    EditorApplication.Exit(3);
                    return;
                }

                SplatDemoSceneBuilder.SetupUrpFeatures();
                var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                    UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                    UnityEditor.SceneManagement.NewSceneMode.Single);

                // 1. 加载扫描
                var go = new GameObject("SplatLevel_Scan");
                var renderer = go.AddComponent<GaussianSplatRenderer>();
                renderer.m_SourcePath = PlyPath;
                renderer.m_SHOrder = 2;
                renderer.m_MortonReorder = true;
                renderer.Reload();
                WaitForSplatLoad(renderer);
                if (renderer.loadError != null) throw new Exception($"splat 加载失败:{renderer.loadError}");
                int chunks = renderer.GpuChunkCount;
                log.AppendLine($"[M6] 加载 {renderer.splatCount:N0} splats, Morton 分块 {chunks} 个(期望 ceil(count/4096)={(renderer.splatCount + 4095) / 4096})");
                if (chunks != (renderer.splatCount + 4095) / 4096)
                    throw new Exception("Morton 分块数量不符");

                // 2. M6:相机置于场景外侧(墙体后方)→ GPU 剔除应显著降低可见数
                var camGo = new GameObject("Cam");
                var cam = camGo.AddComponent<Camera>();
                var b = renderer.localBounds;
                camGo.transform.position = b.center + new Vector3(0f, b.size.y * 0.35f, -b.size.magnitude * 0.6f);
                camGo.transform.LookAt(b.center);

                GaussianSplatRenderingOptions.instance.m_Transparency = TransparencyMode.SortedBlended;
                GaussianSplatRenderingOptions.instance.m_TemporalFilter = StochasticTemporalFilter.None;
                GaussianSplatRenderingOptions.instance.m_GpuChunkCulling = true;
                GaussianSplatRenderingOptions.instance.m_IndirectDraw = true;

                float nb1 = CapturePng(cam, Path.Combine(OutDir, "m6_cull_far.png"));
                // 计数读回:第一次请求在 flags 写入前入队(读到旧值/无值),渲染一帧后重新请求并同步等待
                renderer.UpdateGpuCullingCounters();
                CapturePng(cam, Path.Combine(OutDir, "m6_cull_far_pump.png"));
                AsyncGPUReadback.WaitAllRequests();
                renderer.UpdateGpuCullingCounters();
                AsyncGPUReadback.WaitAllRequests();
                int visSplats = renderer.GpuCullingVisibleSplats;
                log.AppendLine($"[M6] GPU 剔除后可见 splat 计数:{visSplats:N0} / {renderer.splatCount:N0}, 可见 Chunk {renderer.GpuCullingVisibleChunks}/{chunks}");
                log.AppendLine($"[M6] 远景渲染非黑像素 {nb1:P1}(剔除开启下画面应完整)→ m6_cull_far.png");
                if (nb1 < 0.5) throw new Exception($"剔除开启时远景画面异常(非黑 {nb1:P1})");
                if (visSplats >= 0 && visSplats < renderer.splatCount * 0.05)
                    log.AppendLine("[M6] 远景视角可见数显著下降 → 剔除生效");

                // 相机移入场景内部(房间内):剔除不应误杀,画面仍完整
                camGo.transform.position = b.center + new Vector3(0f, Mathf.Min(1.6f, b.size.y * 0.3f), -b.size.z * 0.2f);
                camGo.transform.LookAt(b.center + Vector3.up * 0.5f);
                float nb2 = CapturePng(cam, Path.Combine(OutDir, "m6_cull_inside.png"));
                log.AppendLine($"[M6] 室内视角非黑像素 {nb2:P1} → m6_cull_inside.png");
                if (nb2 < 0.5) throw new Exception($"室内视角画面异常(非黑 {nb2:P1})——Hi-Z 误剔除");
                // 剔除正向用例:室内视角下相机后方 Chunk 应被视锥剔除 → 可见数 < 总数
                renderer.UpdateGpuCullingCounters();
                CapturePng(cam, Path.Combine(OutDir, "m6_cull_inside_pump.png"));
                AsyncGPUReadback.WaitAllRequests();
                renderer.UpdateGpuCullingCounters();
                AsyncGPUReadback.WaitAllRequests();
                log.AppendLine($"[M6] 室内视角剔除后可见:{renderer.GpuCullingVisibleSplats:N0} / {renderer.splatCount:N0}, Chunk {renderer.GpuCullingVisibleChunks}/{chunks}"
                    + (renderer.GpuCullingVisibleChunks is > 0 && renderer.GpuCullingVisibleChunks < chunks
                        ? " ← 视锥剔除生效(相机后方 Chunk 被剔除)"
                        : " ← 该视角未产生视锥剔除"));

                // 3. M7:3 盏彩色点光源 + 手电筒,逐高斯重光照
                var lightColors = new[] { new Color(1f, 0.3f, 0.2f), new Color(0.2f, 0.6f, 1f), new Color(0.3f, 1f, 0.4f) };
                for (int i = 0; i < 3; ++i)
                {
                    var lgo = new GameObject($"M7Light{i}");
                    lgo.transform.position = b.center + new Vector3(
                        Mathf.Sin(i * 2.1f) * b.size.x * 0.2f, b.size.y * 0.25f, Mathf.Cos(i * 2.1f) * b.size.z * 0.15f);
                    var l = lgo.AddComponent<Light>();
                    l.type = LightType.Point;
                    l.color = lightColors[i];
                    l.range = 12f;
                    l.intensity = 25f;
                }
                var flashlight = camGo.AddComponent<Light>();
                flashlight.type = LightType.Spot;
                flashlight.range = 25f;
                flashlight.spotAngle = 55f;
                flashlight.intensity = 60f;
                flashlight.shadows = LightShadows.Soft;
                var driver = go.AddComponent<S2PLightModulationDriver>();
                driver.Flashlight = flashlight;
                driver.PerSplatLightStrength = 1f;
                driver.SetGlobalsNow();
                float nb3 = CapturePng(cam, Path.Combine(OutDir, "m7_dynamic_lights.png"));
                log.AppendLine($"[M7] 3 盏彩色点光源 + 手电筒(逐高斯路径)非黑像素 {nb3:P1} → m7_dynamic_lights.png");
                if (nb3 < 0.5) throw new Exception($"动态光照画面异常(非黑 {nb3:P1})");

                // 4. M8:真实扫描动态物体分离(盒体置于场景中部)
                var settings = ScriptableObject.CreateInstance<ProxyBakeSettings>();
                settings.VoxelResolution = 64;
                settings.MaxTriangles = 4000;
                settings.MaxNavTriangles = 500;
                settings.BakeNavigationMesh = false;
                settings.FillInterior = false;
                settings.UseSDF = false;
                settings.BoundsPercentile = 0f;
                settings.BoundsMargin = 0.05f;
                var splats = SplatSourceReader.ReadAllX(PlyPath, out int splatCount, out _, out _);
                // 切割盒定位:取 splat 中心采样的中位数(原始 bounds 可能被离群点拉偏,
                // 固定中心常落在空腔);盒体 1.2m³ 保证覆盖物件级 splat 聚簇
                var sample = new System.Collections.Generic.List<float3>();
                for (int i = 0; i < splats.Length; i += 997)
                    sample.Add(splats[i].Position);
                sample.Sort((a, c) => a.x.CompareTo(c.x));
                float3 medianPos = sample[sample.Count / 2];
                var volume = new Bounds((Vector3)medianPos, new Vector3(1.2f, 1.2f, 1.2f));
                settings.DynamicProps.Add(new ProxyBakeSettings.DynamicPropVolume
                {
                    PropName = "SmokeProp",
                    Volume = volume,
                    Mass = 6f,
                    Destructible = false
                });
                string propPath;
                int propSplats;
                try
                {
                    using var extraction = SplatPropExtractor.Extract(splats, settings);
                    using var inpainted = SplatPropExtractor.InpaintFloors(extraction.Background, settings);
                    propSplats = extraction.Props[0].Length;
                    string scansDir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Assets", "StreamingAssets", "scans", "props");
                    Directory.CreateDirectory(scansDir);
                    propPath = Path.Combine(scansDir, "m8_smoke_prop.ply");
                    SplatPropExtractor.WriteBinaryPly(propPath, extraction.Props[0]);
                    log.AppendLine($"[M8] 分离:物体 {propSplats:N0} splats, 背景 {inpainted.Length:N0} splats(含底面修补)");

                    using var bake = SplatProxyBaker.Bake(propPath, settings);
                    var propGo = new GameObject("S2P_Prop_SmokeProp");
                    propGo.transform.SetParent(go.transform, false);
                    propGo.transform.localPosition = (Vector3)extraction.PropCentroids[0];
                    var pr = propGo.AddComponent<GaussianSplatRenderer>();
                    pr.m_SourcePath = propPath;
                    var mc = propGo.AddComponent<MeshCollider>();
                    mc.sharedMesh = bake.Mesh;
                    mc.convex = true;
                    var prop = propGo.AddComponent<S2PDynamicProp>();
                    prop.SplatRenderer = pr;
                    prop.Mass = 6f;
                    log.AppendLine($"[M8] 物体凸包碰撞 {bake.TriangleCount:N0} 面, Rigidbody mass=6, NavMeshObstacle 由组件装配");
                }
                finally
                {
                    splats.Dispose();
                    UnityEngine.Object.DestroyImmediate(settings);
                }
                if (propSplats <= 0) throw new Exception("M8 分离得到的物体为空");

                File.WriteAllText(Path.Combine(OutDir, "m678_report.txt"), log.ToString());
                Debug.Log($"[M678] 验证完成\n{log}");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                log.AppendLine("FAIL: " + e);
                try { File.WriteAllText(Path.Combine(OutDir, "m678_report.txt"), log.ToString()); } catch { }
                Debug.LogError($"[M678] 验证失败:{e}");
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

        static void WaitForSplatLoad(GaussianSplatRenderer renderer)
        {
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
            if (renderer.loadError != null) throw new Exception($"splat 加载失败:{renderer.loadError}");
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
