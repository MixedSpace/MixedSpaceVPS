using MixedSpace.XR;
using UnityEditor;
using UnityEngine;

namespace MixedSpace.Editor
{
    /// <summary>
    /// 将集成覆盖选项与普通相机配置界面分开。
    /// Keep integration overrides out of the normal camera configuration view.
    /// </summary>
    [CustomEditor(typeof(MixedSpaceRokidProfile))]
    internal sealed class MixedSpaceRokidProfileEditor : UnityEditor.Editor
    {
        private bool m_Advanced;
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox("相机参数由 Rokid 提供。定位使用去畸变函数返回的输出内参。", MessageType.Info);
            var calibration = serializedObject.FindProperty("m_Calibration");
            if (calibration.enumValueIndex == 0)
                EditorGUILayout.HelpBox("当前校正内参契约尚未确认，请由接入开发人员确认高级设置。", MessageType.Warning);
            m_Advanced = EditorGUILayout.Foldout(m_Advanced, "高级接入设置", true);
            if (m_Advanced)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_StartupTimeoutMilliseconds"), new GUIContent("启动超时（毫秒）"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_MaxFrameAgeMilliseconds"), new GUIContent("最大帧龄（毫秒）"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_ImageOrientationEuler"), new GUIContent("图像方向修正"));
                EditorGUILayout.PropertyField(calibration, new GUIContent("校正内参策略"));
                if (calibration.enumValueIndex == (int)MixedSpaceXrLocFisheyeProcessor.CalibrationMode.ExplicitOutputIntrinsics)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("m_OutputIntrinsics"), new GUIContent("参考输出内参"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("m_CalibrationResolution"), new GUIContent("参考内参对应分辨率"));
                }
            }
            serializedObject.ApplyModifiedProperties();
        }
    }
}
