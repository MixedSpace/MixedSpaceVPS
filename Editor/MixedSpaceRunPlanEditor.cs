#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace MixedSpace.EditorTools
{
    [CustomEditor(typeof(MixedSpaceRunPlan))]
    public sealed class MixedSpaceRunPlanEditor : UnityEditor.Editor
    {
        private bool m_Advanced;
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            if (serializedObject.FindProperty("m_LocalizationMode").intValue == 0)
                serializedObject.FindProperty("m_LocalizationMode").intValue = 1;
            EditorGUILayout.LabelField("MixedSpace · Run Plan", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Shared authoring asset. Changes apply to the next runtime instance, not a running session. Credentials and scene targets are stored separately.", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                EditorGUILayout.LabelField("Startup and resource budget", EditorStyles.boldLabel);
                Draw("m_StartAutomatically", "Start Automatically");
                Draw("m_FrameRate", "Target Frame Rate");
                Draw("m_LimitCapture", "Limit Capture Resolution");
                Draw("m_Diagnostics", "Diagnostics");
                Draw("m_WritePoseLogs", "Write Pose Logs + Images");
                Draw("m_PoseFusionRefineThresholdDist", "Pose Fusion Refine Distance (m)");
                Draw("m_LocalizationMode", "Localization Mode");
                Draw("m_Scheduling", "Scheduling Policy");
                Draw("m_Quality", "Tracking Quality");
                m_Advanced = EditorGUILayout.Foldout(m_Advanced, "Solver Settings", true);
                if (m_Advanced)
                {
                    bool cloud = serializedObject.FindProperty("m_LocalizationMode").intValue ==
                        (int)MixedSpaceLocalizationMode.Cloud;
                    EditorGUILayout.LabelField(cloud ? "Cloud Solver" : "On Device Solver", EditorStyles.boldLabel);
                    DrawSolver(cloud ? "m_RemoteSearch" : "m_DeviceSearch");
                }
            }
            serializedObject.ApplyModifiedProperties();
        }
        // 求解器参数与提供器激活相互独立，激活由 SDK 管理。
        // Solver tuning is independent from provider activation, which is SDK-owned.
        private void DrawSolver(string field)
        {
            var property = serializedObject.FindProperty(field).Copy();
            var end = property.GetEndProperty();
            bool enterChildren = true;
            while (property.NextVisible(enterChildren) && !SerializedProperty.EqualContents(property, end))
            {
                enterChildren = false;
                if (property.name == "m_ActivationMode") continue;
                EditorGUILayout.PropertyField(property, true);
            }
        }

        private void Draw(string field, string label) =>
            EditorGUILayout.PropertyField(serializedObject.FindProperty(field), new GUIContent(label), true);
    }
}
#endif
