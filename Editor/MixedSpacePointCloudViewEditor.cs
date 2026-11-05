#if UNITY_EDITOR

using MixedSpace.XR;
using UnityEditor;
using UnityEngine;

namespace MixedSpace.EditorTools
{
    /// <summary>
    /// 仅提供渲染控制；文件和网络加载通过 Map Workspace 完成。
    /// Rendering controls only. File and network loading live in Map Workspace.
    /// </summary>
    [CustomEditor(typeof(MixedSpacePointCloudView))]
    public sealed class MixedSpacePointCloudViewEditor : UnityEditor.Editor
    {
        private SerializedProperty m_RenderMode;
        private SerializedProperty m_PointColor;
        private SerializedProperty m_PointSize;
        private SerializedProperty m_PerspectivePoints;

        private void OnEnable()
        {
            m_RenderMode = serializedObject.FindProperty("m_RenderMode");
            m_PointColor = serializedObject.FindProperty("m_PointColor");
            m_PointSize = serializedObject.FindProperty("m_PointSize");
            m_PerspectivePoints = serializedObject.FindProperty("m_PerspectivePoints");
        }

        public override void OnInspectorGUI()
        {
            if (BuildPipeline.isBuildingPlayer)
                return;

            serializedObject.Update();
            var view = (MixedSpacePointCloudView)target;

            EditorGUILayout.LabelField("POINT CLOUD VIEW", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "This component only renders a preview. Importing and downloading are managed by Map Workspace.",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.IntField("Map Key", view.MapKey);

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(m_RenderMode, new GUIContent("Visibility"));
            EditorGUILayout.PropertyField(m_PointColor, new GUIContent("Point Color"));
            EditorGUILayout.PropertyField(m_PointSize, new GUIContent("Point Size"));
            EditorGUILayout.PropertyField(m_PerspectivePoints, new GUIContent("Perspective Points"));
            bool appearanceChanged = EditorGUI.EndChangeCheck();

            serializedObject.ApplyModifiedProperties();
            if (appearanceChanged)
            {
                view.RefreshAppearance();
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField(
                view.HasPointCloud ? $"Loaded · {view.Mesh?.vertexCount ?? 0} points" : "No point cloud loaded",
                EditorStyles.miniLabel);

            if (GUILayout.Button("Open Map Workspace"))
                MixedSpaceMapWorkspaceWindow.Open(
                    view.GetComponentInParent<MixedSpaceMapBinding>());

            using (new EditorGUI.DisabledScope(!view.HasPointCloud))
            {
                if (GUILayout.Button("Clear Preview"))
                {
                    Undo.RegisterFullObjectHierarchyUndo(view.gameObject, "Clear point cloud preview");
                    view.ReleasePreview();
                    EditorUtility.SetDirty(view);
                }
            }

        }
    }
}

#endif
