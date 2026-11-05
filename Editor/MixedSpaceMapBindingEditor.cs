#if UNITY_EDITOR

using MixedSpace.XR;
using UnityEditor;
using UnityEngine;

namespace MixedSpace.EditorTools
{
    /// <summary>
    /// 地图声明的只读 Inspector，地图编辑通过 Map Workspace 完成。
    /// Read-only Inspector for map declarations; editing is handled by Map Workspace.
    /// </summary>
    [CustomEditor(typeof(MixedSpaceMapBinding))]
    public sealed class MixedSpaceMapBindingEditor : UnityEditor.Editor
    {
        private SerializedProperty m_AccessLevel;
        private SerializedProperty m_WorldRelation;
        private SerializedProperty m_GeoOrigin;
        private bool m_ShowSpatialData;

        private void OnEnable()
        {
            m_AccessLevel = serializedObject.FindProperty("m_AccessLevel");
            m_WorldRelation = serializedObject.FindProperty("m_WorldRelation");
            m_GeoOrigin = serializedObject.FindProperty("m_GeoOrigin");
        }

        public override void OnInspectorGUI()
        {
            if (BuildPipeline.isBuildingPlayer)
                return;

            serializedObject.Update();
            var binding = (MixedSpaceMapBinding)target;
            MixedSpaceMapDefinition definition = binding.Definition;

            DrawStatus(definition);
            if (!binding.GetComponentInParent<MixedSpaceWorldRoot>(true))
            {
                EditorGUILayout.HelpBox(
                    "Map Binding must be placed below a MixedSpace World Root.",
                    MessageType.Error);
            }

            EditorGUILayout.LabelField("Declaration", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.IntField("Map Key", definition.Key);
                EditorGUILayout.TextField("Label", definition.Label);
                EditorGUILayout.ObjectField(
                    "Embedded Data",
                    definition.EmbeddedData,
                    typeof(TextAsset),
                    false);
                EditorGUILayout.EnumPopup(
                    "Content Location",
                    definition.ContentPolicy.Location);
                EditorGUILayout.Toggle(
                    "Runtime Preview",
                    definition.ContentPolicy.PresentPreviewAtRuntime);
            }

            DrawPreview(binding);
            DrawSpatialData();

            EditorGUILayout.Space(10f);
            if (GUILayout.Button("Open Map Workspace", GUILayout.Height(30f)))
                MixedSpaceMapWorkspaceWindow.Open(binding);

            serializedObject.ApplyModifiedProperties();
        }

        private static void DrawStatus(MixedSpaceMapDefinition definition)
        {
            Rect rect = EditorGUILayout.GetControlRect(false, 36f);
            EditorGUI.DrawRect(
                rect,
                definition.IsReady
                    ? new Color(0.12f, 0.48f, 0.32f, 0.28f)
                    : new Color(0.75f, 0.39f, 0.08f, 0.28f));

            string title = definition.IsReady
                ? $"MAP {definition.Key}  ·  {definition.Label}"
                : "MIXEDSPACE MAP DECLARATION";
            EditorGUI.LabelField(
                new Rect(rect.x + 10f, rect.y + 3f, rect.width - 20f, 16f),
                title,
                EditorStyles.boldLabel);
            EditorGUI.LabelField(
                new Rect(rect.x + 10f, rect.y + 19f, rect.width - 20f, 14f),
                definition.IsReady ? "READY" : "NOT CONFIGURED",
                EditorStyles.miniLabel);
            EditorGUILayout.Space(4f);
        }

        private static void DrawPreview(MixedSpaceMapBinding binding)
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Point Cloud Preview", EditorStyles.boldLabel);
            if (!binding.Preview)
            {
                EditorGUILayout.LabelField("No preview attached", EditorStyles.miniLabel);
                return;
            }

            if (GUILayout.Button("Select Preview"))
                Selection.activeGameObject = binding.Preview.gameObject;
        }

        private void DrawSpatialData()
        {
            EditorGUILayout.Space(6f);
            m_ShowSpatialData = EditorGUILayout.Foldout(
                m_ShowSpatialData,
                "Spatial Data",
                true);
            if (!m_ShowSpatialData)
                return;

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(m_AccessLevel);
                EditorGUILayout.PropertyField(m_GeoOrigin, true);
                EditorGUILayout.PropertyField(m_WorldRelation, true);
            }
        }
    }
}

#endif
