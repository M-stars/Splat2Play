// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;
using RenderMode = GaussianSplatting.Runtime.RenderMode;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatRenderingOptions))]
    [CanEditMultipleObjects]
    public class GaussianSplatRenderingOptionsEditor : UnityEditor.Editor
    {
        SerializedProperty m_PropScript;
        SerializedProperty m_PropTransparency;
        SerializedProperty m_PropTemporalFilter;
        SerializedProperty m_PropRenderMode;
        SerializedProperty m_PropPointSize;
        SerializedProperty m_PropSHOnly;
        SerializedProperty m_PropGpuChunkCulling;
        SerializedProperty m_PropIndirectDraw;
        SerializedProperty m_PropEnableChunkLod;
        SerializedProperty m_PropLod1Distance;
        SerializedProperty m_PropLod2Distance;
        SerializedProperty m_PropTargetFrameBudgetMs;
        SerializedProperty m_PropLodScaleCompensation;

        void OnEnable()
        {
            m_PropScript = serializedObject.FindProperty("m_Script");
            m_PropTransparency = serializedObject.FindProperty("m_Transparency");
            m_PropTemporalFilter = serializedObject.FindProperty("m_TemporalFilter");
            m_PropRenderMode = serializedObject.FindProperty("m_RenderMode");
            m_PropPointSize = serializedObject.FindProperty("m_PointDisplaySize");
            m_PropSHOnly = serializedObject.FindProperty("m_SHOnly");
            m_PropGpuChunkCulling = serializedObject.FindProperty("m_GpuChunkCulling");
            m_PropIndirectDraw = serializedObject.FindProperty("m_IndirectDraw");
            m_PropEnableChunkLod = serializedObject.FindProperty("m_EnableChunkLod");
            m_PropLod1Distance = serializedObject.FindProperty("m_Lod1Distance");
            m_PropLod2Distance = serializedObject.FindProperty("m_Lod2Distance");
            m_PropTargetFrameBudgetMs = serializedObject.FindProperty("m_TargetFrameBudgetMs");
            m_PropLodScaleCompensation = serializedObject.FindProperty("m_LodScaleCompensation");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(m_PropScript);
            EditorGUILayout.PropertyField(m_PropTransparency);
            if (!m_PropTransparency.hasMultipleDifferentValues && m_PropTransparency.intValue == (int)TransparencyMode.Stochastic)
                EditorGUILayout.PropertyField(m_PropTemporalFilter);
            EditorGUILayout.PropertyField(m_PropRenderMode);
            if (!m_PropRenderMode.hasMultipleDifferentValues && m_PropRenderMode.intValue == (int)RenderMode.DebugPoints)
                EditorGUILayout.PropertyField(m_PropPointSize);
            EditorGUILayout.PropertyField(m_PropSHOnly);

            EditorGUILayout.Space();
            GUILayout.Label("S2P M6 GPU Culling / M10 Chunk HLOD", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropGpuChunkCulling);
            EditorGUILayout.PropertyField(m_PropIndirectDraw);
            EditorGUILayout.PropertyField(m_PropEnableChunkLod);
            if (!m_PropEnableChunkLod.hasMultipleDifferentValues && m_PropEnableChunkLod.boolValue)
            {
                EditorGUILayout.PropertyField(m_PropLod1Distance);
                EditorGUILayout.PropertyField(m_PropLod2Distance);
                EditorGUILayout.PropertyField(m_PropTargetFrameBudgetMs);
                EditorGUILayout.PropertyField(m_PropLodScaleCompensation);
            }
            serializedObject.ApplyModifiedProperties();
        }
    }
}
