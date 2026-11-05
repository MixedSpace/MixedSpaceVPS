#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace MixedSpace.EditorTools
{
    /// <summary>
    /// 仅编辑场景连线；策略和回调在各自的所有者上配置。
    /// Scene wiring only; policies and callbacks are edited on their owners.
    /// </summary>
    [CustomEditor(typeof(MixedSpaceSDK))]
    public sealed class MixedSpaceSDKEditor : UnityEditor.Editor
    {
        private bool m_ShowPorts;
        private string m_VerificationMessage;
        private MessageType m_VerificationType;
        private string m_VerifiedToken;
        private int m_VerifiedServer;


        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            bool verifyRequested = false;
            EditorGUILayout.LabelField("MixedSpace · Scene Entry", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Run Plan owns runtime policies. Platform Profile owns camera settings. Application Binding owns UI and callbacks.", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                Field("m_RunPlan", "Run Plan");
                Field("m_PlatformAdapter", "Platform Adapter");
                Field("m_ApplicationBinding", "Application Binding");
                DrawServerSettings();
                var token = serializedObject.FindProperty("m_DeveloperToken");
                using (new EditorGUILayout.HorizontalScope())
                {
                    token.stringValue = EditorGUILayout.TextField("Developer Token", token.stringValue);
                    using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(token.stringValue)))
                        verifyRequested = GUILayout.Button(new GUIContent("验证", "验证当前 Token 并导出离线授权缓存"), GUILayout.Width(50));
                }
                if (token.stringValue != m_VerifiedToken ||
                    serializedObject.FindProperty("m_XrLocServer").intValue != m_VerifiedServer)
                    m_VerificationMessage = null;
                if (!string.IsNullOrEmpty(m_VerificationMessage))
                    EditorGUILayout.HelpBox(m_VerificationMessage, m_VerificationType);
                m_ShowPorts = EditorGUILayout.Foldout(m_ShowPorts, "Scene extensions", true);
                if (m_ShowPorts)
                {
                    Field("m_ScenePorts.m_LocalizationViews", "Localization Views");
                    Field("m_ScenePorts.m_AlignmentSolverOverride", "Alignment Override");
                    Field("m_ScenePorts.m_ExtensionProviders", "Extension Providers");
                }
                if (serializedObject.FindProperty("m_RunPlan").objectReferenceValue == null ||
                    serializedObject.FindProperty("m_ApplicationBinding").objectReferenceValue == null ||
                    serializedObject.FindProperty("m_PlatformAdapter").objectReferenceValue == null)
                    EditorGUILayout.HelpBox("Assign Run Plan, Application Binding and Platform Adapter before startup.", MessageType.Error);
            }
            serializedObject.ApplyModifiedProperties();
            // Apply the edited token/server first so the button never verifies a stale Inspector value.
            if (verifyRequested) VerifyDeveloperToken((MixedSpaceSDK)target);
            if (Application.isPlaying)
                EditorGUILayout.LabelField("Runtime", ((MixedSpaceSDK)target).RuntimeState.ToString());
        }

        private void VerifyDeveloperToken(MixedSpaceSDK sdk)
        {
            m_VerifiedToken = sdk.DeveloperToken;
            m_VerifiedServer = sdk.XrLocServer;
            bool tokenVerified = false;
            try
            {
                EditorUtility.DisplayProgressBar("Token 验证", "正在验证当前 Token…", 0.5f);
                int serverStatus = MixedSpaceXrLocNative.SetServer(sdk.XrLocServer);
                if (serverStatus != 0)
                    MixedSpaceLogger.LogWarning($"XrLoc server selection rejected: status={serverStatus}");
                int status = MixedSpaceXrLocNative.VerifyDeveloperToken(sdk.DeveloperToken);
                if (status != 0)
                {
                    m_VerificationType = MessageType.Error;
                    m_VerificationMessage = $"Token 验证失败（code={status}）。请检查 Token、网络和服务器环境。";
                    MixedSpaceLogger.LogError($"Native token verification failed: status={status}");
                    return;
                }
                tokenVerified = true;
                EditorUtility.DisplayProgressBar("Token 验证", "验证成功，正在导出离线授权缓存…", 0.8f);
                string destination = MixedSpaceAuthCacheExporter.ExportToStreamingAssets(sdk.DeveloperToken);
                AssetDatabase.Refresh();
                m_VerificationType = MessageType.Info;
                m_VerificationMessage = "Token 验证成功（code=0），离线授权缓存已导出。\n" + destination;
            }
            catch (System.Exception error)
            {
                m_VerificationType = MessageType.Error;
                m_VerificationMessage = (tokenVerified ? "Token 验证成功，但缓存导出失败：" : "Token 验证异常：") +
                    error.GetType().Name + "。请查看 Console。";
                MixedSpaceLogger.LogError((tokenVerified ? "Authorization cache export failed (" : "Token verification failed (") + error.GetType().Name + ").");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                Repaint();
            }
        }

        private void DrawServerSettings()
        {
            var server = serializedObject.FindProperty("m_XrLocServer");
            var map = serializedObject.FindProperty("m_ServerMapLoad");
            var localize = serializedObject.FindProperty("m_ServerAddressLocalize");
            EditorGUI.BeginChangeCheck();
            int selected = EditorGUILayout.IntPopup("Environment", server.intValue,
                new[] { "Production (0)", "Test (1)" }, new[] { 0, 1 });
            if (EditorGUI.EndChangeCheck())
            {
                server.intValue = selected;
                map.stringValue = MixedSpaceNetworkEnvironment.ResolveServer(null, selected, false);
                localize.stringValue = MixedSpaceNetworkEnvironment.ResolveServer(null, selected, true);
            }
            if (server.intValue == 0 || server.intValue == 1)
            {
                map.stringValue = MixedSpaceNetworkEnvironment.ResolveServer(map.stringValue, server.intValue, false);
                localize.stringValue = MixedSpaceNetworkEnvironment.ResolveServer(localize.stringValue, server.intValue, true);
            }
            Field("m_ServerMapLoad", "Map Server");
            Field("m_ServerAddressLocalize", "Localize Server");
            EditorGUILayout.HelpBox("Switching environment resets both URLs to its defaults. You can edit them afterwards.", MessageType.Info);
        }

        private void Field(string path, string label) =>
            EditorGUILayout.PropertyField(serializedObject.FindProperty(path), new GUIContent(label), true);
    }
}
#endif
