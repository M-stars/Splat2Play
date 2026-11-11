// Splat2Play — M1/T1.6 烘焙向导窗口
// 流程:选 PLY → 调参数 → 烘焙(进度条)→ ProxyData 落盘 → 场景装配 SplatLevel + 碰撞
// SPDX-License-Identifier: MIT

using System.IO;
using GaussianSplatting.Runtime;
using Splat2Play.Runtime;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace Splat2Play.Editor
{
    public sealed class SplatProxyBakeWindow : EditorWindow
    {
        [MenuItem("Splat2Play/Splat Level Baker", priority = 0)]
        static void Open() => GetWindow<SplatProxyBakeWindow>("Splat Level Baker");

        string _plyPath = "";
        GaussianSplatRenderer _targetRenderer;
        ProxyBakeSettings _settings;
        bool _buildLevel = true;
        bool _bakeNavMesh = true;
        string _lastLog;

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "流程:① PLY 路径(或选中场景里的 GaussianSplatRenderer 自动取路径)\n" +
                "② 烘焙参数 → Bake\n" +
                "③ 自动生成 ProxyData 资产并在场景中一键装配 SplatLevel(碰撞 + NavMesh + 流送 + 性能面板)",
                MessageType.Info);

            _targetRenderer = (GaussianSplatRenderer)EditorGUILayout.ObjectField(
                "场景 splat 渲染器(可选)", _targetRenderer, typeof(GaussianSplatRenderer), true);

            EditorGUILayout.BeginHorizontal();
            _plyPath = EditorGUILayout.TextField("PLY 路径", _plyPath);
            if (GUILayout.Button("浏览", GUILayout.Width(48)))
            {
                string picked = EditorUtility.OpenFilePanel("选择 3DGS PLY", "", "ply");
                if (!string.IsNullOrEmpty(picked)) _plyPath = picked;
            }
            EditorGUILayout.EndHorizontal();

            if (_targetRenderer != null && GUILayout.Button("从渲染器读取路径"))
                _plyPath = GaussianSplatReader.ResolvePath(Application.streamingAssetsPath, _targetRenderer.m_SourcePath);

            _settings = (ProxyBakeSettings)EditorGUILayout.ObjectField(
                "烘焙参数", _settings, typeof(ProxyBakeSettings), false);
            if (_settings == null && GUILayout.Button("创建默认参数资产"))
                _settings = CreateSettingsAsset();

            _buildLevel = EditorGUILayout.Toggle("烘焙后装配 SplatLevel", _buildLevel);
            using (new EditorGUI.DisabledScope(!_buildLevel))
            {
                _bakeNavMesh = EditorGUILayout.Toggle("同时烘焙 NavMesh (M2)", _bakeNavMesh);
            }

            using (new EditorGUI.DisabledScope(!CanBake))
            {
                if (GUILayout.Button("Bake", GUILayout.Height(32)))
                    Bake();
            }

            if (!string.IsNullOrEmpty(_lastLog))
                EditorGUILayout.HelpBox(_lastLog, MessageType.None);
        }

        bool CanBake =>
            !string.IsNullOrWhiteSpace(_plyPath) && File.Exists(_plyPath)
            && _settings != null;

        void Bake()
        {
            try
            {
                var progress = new ProgressReporter(this);
                using var result = SplatProxyBaker.Bake(_plyPath, _settings, progress.Report);

                string dir = "Assets/Splat2Play/Baked";
                if (!AssetDatabase.IsValidFolder(dir)) AssetDatabase.CreateFolder("Assets/Splat2Play", "Baked");
                string baseName = Path.GetFileNameWithoutExtension(_plyPath);
                string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{dir}/{baseName}_Proxy.asset");

                var proxy = ScriptableObject.CreateInstance<ProxyData>();
                AssetDatabase.CreateAsset(proxy, assetPath);
                AssetDatabase.AddObjectToAsset(result.Mesh, proxy);
                proxy.CollisionMesh = result.Mesh;
                proxy.SceneBounds = result.SceneBounds;
                proxy.SourceSplatCount = result.SplatCount;
                proxy.TriangleCount = result.TriangleCount;
                proxy.VoxelResolution = result.VoxelResolution;
                proxy.DensityThreshold = result.DensityThreshold;
                proxy.Settings = _settings;
                proxy.PatchBoxes = new(_settings.PatchBoxes);
                proxy.BakeLog = result.Log;
                EditorUtility.SetDirty(proxy);
                AssetDatabase.SaveAssets();

                _lastLog = $"已生成 {assetPath}\n{result.Log}";

                if (_buildLevel)
                {
                    GameObject go = _targetRenderer != null ? _targetRenderer.gameObject : null;
                    if (go == null) go = new GameObject($"SplatLevel_{baseName}");
                    var renderer = go.GetComponent<GaussianSplatRenderer>();
                    if (renderer == null) renderer = go.AddComponent<GaussianSplatRenderer>();
                    if (string.IsNullOrWhiteSpace(renderer.m_SourcePath))
                        renderer.m_SourcePath = _plyPath; // ResolvePath 支持绝对路径
                    var level = go.GetComponent<SplatLevel>() ?? go.AddComponent<SplatLevel>();
                    level.SplatRenderer = renderer;
                    level.Proxy = proxy;
                    level.BuildCollision();

                    var streaming = go.GetComponent<S2PSplatStreaming>() ?? go.AddComponent<S2PSplatStreaming>();
                    streaming.Target = renderer;

                    var panel = go.GetComponent<S2PPerfPanel>() ?? go.AddComponent<S2PPerfPanel>();
                    panel.SplatRenderer = renderer;
                    panel.Proxy = proxy;

                    if (_bakeNavMesh)
                    {
                        var surface = go.GetComponent<NavMeshSurface>() ?? go.AddComponent<NavMeshSurface>();
                        surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
                        surface.collectObjects = CollectObjects.Children;
                        surface.BuildNavMesh();
                    }

                    Undo.RegisterCreatedObjectUndo(go, "Splat2Play Bake Level");
                    Selection.activeGameObject = go;
                }
            }
            catch (System.Exception e)
            {
                _lastLog = $"烘焙失败:{e.Message}";
                Debug.LogException(e);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        ProxyBakeSettings CreateSettingsAsset()
        {
            string dir = "Assets/Splat2Play/Settings";
            if (!AssetDatabase.IsValidFolder(dir)) AssetDatabase.CreateFolder("Assets/Splat2Play", "Settings");
            var asset = ScriptableObject.CreateInstance<ProxyBakeSettings>();
            string path = AssetDatabase.GenerateUniqueAssetPath($"{dir}/ProxyBakeSettings.asset");
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<ProxyBakeSettings>(path);
        }

        sealed class ProgressReporter
        {
            readonly EditorWindow _window;
            public ProgressReporter(EditorWindow w) => _window = w;
            public void Report(string stage, float t)
            {
                if (EditorUtility.DisplayCancelableProgressBar("Splat2Play 烘焙", stage, t))
                    throw new System.OperationCanceledException("用户取消烘焙");
                _window?.Repaint();
            }
        }
    }
}
