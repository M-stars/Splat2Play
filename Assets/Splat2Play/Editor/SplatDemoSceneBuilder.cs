// Splat2Play — T0.2/T2.3 Demo 场景构建器 + URP RendererFeature 接线(编辑器工具)
// SPDX-License-Identifier: MIT

using System;
using GaussianSplatting.Runtime;
using Splat2Play.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering.Universal;
using Unity.AI.Navigation;

namespace Splat2Play.Editor
{
    public static class SplatDemoSceneBuilder
    {
        /// <summary>把 GaussianSplatURPFeature 挂到工程所有 URP Renderer 资产上(幂等)。
        /// 该类型在 GaussianSplatting 程序集中为 internal,故经反射创建。</summary>
        [MenuItem("Splat2Play/Setup URP Renderer Features", priority = 10)]
        public static void SetupUrpFeatures()
        {
            Type featureType = typeof(GaussianSplatRenderer).Assembly.GetType(
                "GaussianSplatting.Runtime.GaussianSplatURPFeature");
            if (featureType == null)
            {
                Debug.LogError("[Splat2Play] 未找到 GaussianSplatURPFeature 类型(URP 宏未生效?)");
                return;
            }
            string[] guids = AssetDatabase.FindAssets("t:UniversalRendererData");
            int wired = 0;
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                if (rendererData == null) continue;

                bool exists = false;
                foreach (var f in rendererData.rendererFeatures)
                    if (f != null && f.GetType() == featureType) { exists = true; break; }
                if (!exists)
                {
                    var feature = (ScriptableRendererFeature)ScriptableObject.CreateInstance(featureType);
                    feature.name = "GaussianSplatURPFeature";
                    AssetDatabase.AddObjectToAsset(feature, rendererData);
                    rendererData.rendererFeatures.Add(feature);
                    EditorUtility.SetDirty(rendererData);
                    ++wired;
                    Debug.Log($"[Splat2Play] 已为 {path} 添加 GaussianSplatURPFeature");
                }
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[Splat2Play] URP Renderer Feature 接线完成,新增 {wired} 处");
        }

        /// <summary>构建可玩 Demo 场景(SplatLevel + 玩家 + 敌人 + NavMesh)。</summary>
        [MenuItem("Splat2Play/Build Demo Scene", priority = 11)]
        public static void BuildDemoScene()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.DefaultGameObjects,
                UnityEditor.SceneManagement.NewSceneMode.Single);

            // 移除模板默认相机/平行光(玩家自带相机;手电筒为点光源)
            var defaultCam = GameObject.Find("Main Camera");
            if (defaultCam != null) UnityEngine.Object.DestroyImmediate(defaultCam);
            var defaultLight = GameObject.Find("Directional Light");
            if (defaultLight != null) UnityEngine.Object.DestroyImmediate(defaultLight);

            // —— SplatLevel 根节点 ——
            var levelGo = new GameObject("SplatLevel_Scan");
            var renderer = levelGo.AddComponent<GaussianSplatRenderer>();
            renderer.m_SourcePath = "scans/demo.ply"; // 占位;烘焙向导会写入实际路径
            renderer.m_SHOrder = 2;                    // 首版性能保守值
            var level = levelGo.AddComponent<SplatLevel>();
            level.SplatRenderer = renderer;
            levelGo.AddComponent<NavMeshSurface>();    // T2.1:烘焙源=代理网格(装配后手动 Bake)

            // —— 玩家 ——
            var player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            player.name = "Player";
            player.tag = "Player";
            player.layer = 0;
            player.transform.position = Vector3.up * 1.2f;
            UnityEngine.Object.DestroyImmediate(player.GetComponent<Collider>()); // CharacterController 替代
            var cc = player.AddComponent<CharacterController>();
            cc.height = 1.8f; cc.radius = 0.3f; cc.center = new Vector3(0f, 0.9f, 0f);
            player.AddComponent<SplatFPSController>();

            var head = new GameObject("Head");
            head.transform.SetParent(player.transform, false);
            head.transform.localPosition = new Vector3(0f, 0.7f, 0f);
            var cam = head.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            cam.tag = "MainCamera";
            player.GetComponent<SplatFPSController>().CameraPivot = head.transform;

            var flashlight = head.AddComponent<Light>();
            flashlight.type = LightType.Spot;
            flashlight.spotAngle = 55f;
            flashlight.range = 25f;
            flashlight.intensity = 60f; // Unity 6 物理光照单位
            flashlight.shadows = LightShadows.Soft;
            flashlight.color = new Color(1f, 0.96f, 0.9f);
            flashlight.name = "Flashlight";

            // —— 敌人 ——
            var enemy = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            enemy.name = "Enemy";
            UnityEngine.Object.DestroyImmediate(enemy.GetComponent<Collider>());
            enemy.transform.position = Vector3.up * 1.2f + Vector3.forward * 6f;
            var mr = enemy.GetComponent<MeshRenderer>();
            var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "S2P_Enemy" };
            mat.SetColor("_BaseColor", Color.red);
            mr.sharedMaterial = mat;
            var agent = enemy.AddComponent<NavMeshAgent>();
            agent.radius = 0.3f; agent.height = 1.8f; agent.speed = 3.5f; agent.angularSpeed = 360f;
            enemy.AddComponent<EnemyChaser>();

            // 保存
            const string scenePath = "Assets/Scenes/SplatDemo.unity";
            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, scenePath);
            Debug.Log($"[Splat2Play] Demo 场景已保存:{scenePath}\n下一步:烘焙代理 → SplatLevel.BuildCollision → NavMeshSurface 烘焙");
        }
    }
}
