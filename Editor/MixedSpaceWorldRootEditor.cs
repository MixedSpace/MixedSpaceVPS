#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using MixedSpace.XR;

namespace MixedSpace.EditorTools
{
    /// <summary>
    /// 编辑场景世界根节点和输出策略；运行时策略参数需重新打开对应 World 后生效。
    /// Edits world roots and output policies; reopen the corresponding World for runtime policy
    /// changes to take effect.
    /// </summary>
    [CustomEditor(typeof(MixedSpaceWorldRoot))]
    public sealed class MixedSpaceWorldRootEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.LabelField("MIXEDSPACE WORLD", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Place Map Bindings and world-locked content below this coordinate root. History is owned by the World runtime, not by components.", MessageType.Info);
            if (Application.isPlaying)
                EditorGUILayout.HelpBox("Reopen the World to apply output policy changes.", MessageType.None);
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("m_ProcessAlignment"),
                new GUIContent("Use Output Policy"));
            using (new EditorGUI.DisabledScope(Application.isPlaying))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_OutputSettings"),
                    new GUIContent("Fallback Pose Output"), true);
            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
