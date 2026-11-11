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
        bool _showSceneHandles = true;
        string _lastLog;

        void OnEnable() => SceneView.duringSceneGui += OnSceneGUI;
        void OnDisable() => SceneView.duringSceneGui -= OnSceneGUI;

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "流程:① PLY/SPZ 路径(或选中场景里的 GaussianSplatRenderer 自动取路径)\n" +
                "② 烘焙参数 + 后端选择(CPU 确定性 / GPU Compute)+ T1.7 场景盒体可视化微调 → Bake\n" +
                "③ 自动生成 ProxyData 资产并在场景中一键装配 SplatLevel(碰撞 + NavMesh + 流送 + 性能面板)",
                MessageType.Info);

            _targetRenderer = (GaussianSplatRenderer)EditorGUILayout.ObjectField(
                "场景 splat 渲染器(可选)", _targetRenderer, typeof(GaussianSplatRenderer), true);

            EditorGUILayout.BeginHorizontal();
            _plyPath = EditorGUILayout.TextField("扫描资产路径", _plyPath);
            if (GUILayout.Button("浏览", GUILayout.Width(48)))
            {
                // M5:同时支持未压缩 .ply 与压缩 .spz(体积小 ~10×)
                string picked = EditorUtility.OpenFilePanel("选择 3DGS PLY / SPZ", "", "ply,spz");
                if (!string.IsNullOrEmpty(picked)) _plyPath = picked;
            }
            EditorGUILayout.EndHorizontal();

            if (_targetRenderer != null && GUILayout.Button("从渲染器读取路径"))
                _plyPath = GaussianSplatReader.ResolvePath(Application.streamingAssetsPath, _targetRenderer.m_SourcePath);

            _settings = (ProxyBakeSettings)EditorGUILayout.ObjectField(
                "烘焙参数", _settings, typeof(ProxyBakeSettings), false);
            if (_settings == null && GUILayout.Button("创建默认参数资产"))
                _settings = CreateSettingsAsset();

            if (_settings != null)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("T1.7 人工补丁与范围控制 (SceneView 可视化编辑)", EditorStyles.boldLabel);
                _showSceneHandles = EditorGUILayout.Toggle("Scene 视图显示盒体手柄", _showSceneHandles);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("+ 排除盒 (红)")) AddVolumeInSceneView(0);
                if (GUILayout.Button("+ 封闭盒 (青)")) AddVolumeInSceneView(1);
                if (GUILayout.Button("+ 补丁盒 (绿)")) AddVolumeInSceneView(2);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField(
                    $"当前: 排除区 {_settings.ExclusionVolumes.Count} | 封闭区 {_settings.SealVolumes.Count} | 补丁盒 {_settings.PatchBoxes.Count}",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(4);
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

        void AddVolumeInSceneView(int kind)
        {
            if (_settings == null) return;
            Undo.RecordObject(_settings, "Add Splat2Play Volume");
            Vector3 center = Vector3.zero;
            if (SceneView.lastActiveSceneView != null && SceneView.lastActiveSceneView.camera != null)
            {
                var camT = SceneView.lastActiveSceneView.camera.transform;
                center = camT.position + camT.forward * 3f;
                if (_targetRenderer != null)
                    center = _targetRenderer.transform.InverseTransformPoint(center);
            }
            if (kind == 0) _settings.ExclusionVolumes.Add(new Bounds(center, Vector3.one * 2f));
            else if (kind == 1) _settings.SealVolumes.Add(new Bounds(center, new Vector3(1.5f, 2.2f, 0.4f)));
            else if (kind == 2) _settings.PatchBoxes.Add(new ProxyBakeSettings.PatchBox { Center = center, Size = new Vector3(2f, 0.2f, 2f), RotationEuler = Vector3.zero });
            EditorUtility.SetDirty(_settings);
            SceneView.RepaintAll();
        }

        void OnSceneGUI(SceneView sv)
        {
            if (!_showSceneHandles || _settings == null) return;
            Matrix4x4 localToWorld = _targetRenderer != null ? _targetRenderer.transform.localToWorldMatrix : Matrix4x4.identity;
            using (new Handles.DrawingScope(localToWorld))
            {
                // 1. ExclusionVolumes (红色)
                Handles.color = new Color(1f, 0.25f, 0.25f, 0.9f);
                for (int i = 0; i < _settings.ExclusionVolumes.Count; ++i)
                {
                    Bounds b = _settings.ExclusionVolumes[i];
                    Handles.DrawWireCube(b.center, b.size);
                    Handles.Label(b.center + Vector3.up * (b.extents.y + 0.1f), $"Exclude[{i}]");
                    EditorGUI.BeginChangeCheck();
                    Vector3 newCenter = Handles.PositionHandle(b.center, Quaternion.identity);
                    Vector3 newSize = Handles.ScaleHandle(b.size, newCenter, Quaternion.identity, HandleUtility.GetHandleSize(newCenter) * 0.8f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(_settings, "Edit Exclusion Volume");
                        _settings.ExclusionVolumes[i] = new Bounds(newCenter, Vector3.Max(newSize, Vector3.one * 0.05f));
                        EditorUtility.SetDirty(_settings);
                    }
                }

                // 2. SealVolumes (青色)
                Handles.color = new Color(0.2f, 0.9f, 1f, 0.9f);
                for (int i = 0; i < _settings.SealVolumes.Count; ++i)
                {
                    Bounds b = _settings.SealVolumes[i];
                    Handles.DrawWireCube(b.center, b.size);
                    Handles.Label(b.center + Vector3.up * (b.extents.y + 0.1f), $"Seal[{i}]");
                    EditorGUI.BeginChangeCheck();
                    Vector3 newCenter = Handles.PositionHandle(b.center, Quaternion.identity);
                    Vector3 newSize = Handles.ScaleHandle(b.size, newCenter, Quaternion.identity, HandleUtility.GetHandleSize(newCenter) * 0.8f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(_settings, "Edit Seal Volume");
                        _settings.SealVolumes[i] = new Bounds(newCenter, Vector3.Max(newSize, Vector3.one * 0.05f));
                        EditorUtility.SetDirty(_settings);
                    }
                }

                // 3. PatchBoxes (绿色)
                Handles.color = new Color(0.2f, 1f, 0.4f, 0.9f);
                for (int i = 0; i < _settings.PatchBoxes.Count; ++i)
                {
                    ProxyBakeSettings.PatchBox p = _settings.PatchBoxes[i];
                    Quaternion rot = Quaternion.Euler(p.RotationEuler);
                    using (new Handles.DrawingScope(localToWorld * Matrix4x4.TRS(p.Center, rot, Vector3.one)))
                    {
                        Handles.DrawWireCube(Vector3.zero, p.Size);
                    }
                    Handles.Label(p.Center + Vector3.up * (p.Size.y * 0.5f + 0.1f), $"Patch[{i}]");
                    EditorGUI.BeginChangeCheck();
                    Vector3 newCenter = Handles.PositionHandle(p.Center, rot);
                    Vector3 newSize = Handles.ScaleHandle(p.Size, newCenter, rot, HandleUtility.GetHandleSize(newCenter) * 0.8f);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(_settings, "Edit PatchBox");
                        p.Center = newCenter;
                        p.Size = Vector3.Max(newSize, Vector3.one * 0.05f);
                        _settings.PatchBoxes[i] = p;
                        EditorUtility.SetDirty(_settings);
                    }
                }

            }
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
                if (result.NavigationMesh != null)
                {
                    AssetDatabase.AddObjectToAsset(result.NavigationMesh, proxy);
                    proxy.NavigationMesh = result.NavigationMesh;
                }
                proxy.SceneBounds = result.SceneBounds;
                proxy.SourceSplatCount = result.SplatCount;
                proxy.TriangleCount = result.TriangleCount;
                proxy.VoxelResolution = result.VoxelResolution;
                proxy.DensityThreshold = result.DensityThreshold;
                proxy.Settings = _settings;
                proxy.PatchBoxes = new(_settings.PatchBoxes);
                proxy.ChunkBounds = result.ChunkBounds != null ? new(result.ChunkBounds) : new();
                proxy.ChunkSplatCounts = result.ChunkSplatCounts != null ? new(result.ChunkSplatCounts) : new();
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
                        level.SwapToNavigationMesh(true);
                        try
                        {
                            surface.BuildNavMesh();
                        }
                        finally
                        {
                            level.SwapToNavigationMesh(false);
                        }
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
