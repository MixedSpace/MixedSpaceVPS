#if UNITY_EDITOR

using System;
using System.Collections;
using System.Linq;
using MixedSpace.ServiceContracts;
using MixedSpace.REST;
using MixedSpace.XR;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace MixedSpace.EditorTools
{
    // 独立的地图获取、缓存和绑定编辑流程。
    // Independent editor workflow for acquiring, caching and binding map data.
    /// <summary>
    /// 地图导入、云端绑定、对齐和点云预览的编辑窗口；网络和文件操作不放入运行时组件 Inspector。
    /// Dedicated authoring surface for map import, cloud binding, alignment and
    /// point-cloud previews. Network and file operations do not live in a runtime
    /// component inspector.
    /// </summary>
    public sealed class MixedSpaceMapWorkspaceWindow : EditorWindow
    {
        [SerializeField] private MixedSpaceMapBinding m_Target;
        [SerializeField] private TextAsset m_LocalMapAsset;
        [SerializeField] private int m_CloudMapId = -1;
        [SerializeField] private bool m_CacheBinaryInProject;
        [SerializeField] private bool m_DownloadPointCloud = true;

        private Vector2 m_Scroll;
        private bool m_IsBusy;
        private string m_Status = "Select or create a Map Binding.";
        private MessageType m_StatusType = MessageType.Info;

        [MenuItem("MixedSpaceSDK/Maps/Map Workspace")]
        public static void OpenFromMenu()
        {
            Open(Selection.activeGameObject?.GetComponent<MixedSpaceMapBinding>());
        }

        public static void Open(MixedSpaceMapBinding binding)
        {
            var window = GetWindow<MixedSpaceMapWorkspaceWindow>();
            window.titleContent = new GUIContent("Map Workspace");
            window.minSize = new Vector2(430f, 540f);
            if (binding != null)
                window.m_Target = binding;
            window.Show();
            window.Repaint();
        }

        private void OnSelectionChange()
        {
            MixedSpaceMapBinding selected = Selection.activeGameObject?.GetComponent<MixedSpaceMapBinding>();
            if (selected == null)
                return;

            m_Target = selected;
            Repaint();
        }

        private void OnGUI()
        {
            DrawHeader();
            using (var scroll = new EditorGUILayout.ScrollViewScope(m_Scroll))
            {
                m_Scroll = scroll.scrollPosition;
                DrawTargetSection();
                EditorGUILayout.Space(10f);
                DrawLocalAssetSection();
                EditorGUILayout.Space(10f);
                DrawCloudSection();
                EditorGUILayout.Space(10f);
                DrawPointCloudSection();
                EditorGUILayout.Space(10f);
                DrawAlignmentSection();
            }

            EditorGUILayout.Space(6f);
            EditorGUILayout.HelpBox(m_Status, m_StatusType);
        }

        private void DrawHeader()
        {
            Rect rect = EditorGUILayout.GetControlRect(false, 52f);
            EditorGUI.DrawRect(rect, new Color(0.07f, 0.13f, 0.20f, 1f));
            var titleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                normal = { textColor = new Color(0.55f, 0.88f, 1f) }
            };
            var subtitleStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(0.72f, 0.76f, 0.80f) }
            };
            EditorGUI.LabelField(new Rect(rect.x + 12f, rect.y + 7f, rect.width - 24f, 22f), "MIXEDSPACE MAP WORKSPACE", titleStyle);
            EditorGUI.LabelField(new Rect(rect.x + 12f, rect.y + 30f, rect.width - 24f, 16f), "Import · Bind · Preview · Align", subtitleStyle);
        }

        private void DrawTargetSection()
        {
            EditorGUILayout.LabelField("Target Binding", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(m_IsBusy))
            {
                m_Target = (MixedSpaceMapBinding)EditorGUILayout.ObjectField(
                    "Binding",
                    m_Target,
                    typeof(MixedSpaceMapBinding),
                    true);

                if (m_Target == null && GUILayout.Button("Create Binding Under Selected World"))
                    CreateBinding();
            }

            if (m_Target == null)
                return;

            MixedSpaceMapDefinition definition = m_Target.Definition;
            string identity = !definition.IsReady
                ? "Not bound"
                : MixedSpaceMapWorkspaceService.HasGeneratedLocalMapKey(definition)
                    ? $"Local asset · {definition.Label}"
                    : $"{definition.Key} · {definition.Label}";
            EditorGUILayout.LabelField("State", identity);

            if (m_Target.GetComponentInParent<MixedSpaceWorldRoot>(true) == null)
            {
                EditorGUILayout.HelpBox(
                    "The target must be below a MixedSpace World Root.",
                    MessageType.Error);
            }
        }

        private void DrawLocalAssetSection()
        {
            EditorGUILayout.LabelField("Local Map Asset", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Bind an existing MixedSpace .bytes asset for offline on-device localization.",
                MessageType.None);

            using (new EditorGUI.DisabledScope(m_IsBusy || m_Target == null))
            {
                m_LocalMapAsset = (TextAsset)EditorGUILayout.ObjectField(
                    "Map Asset",
                    m_LocalMapAsset,
                    typeof(TextAsset),
                    false);
                if (GUILayout.Button("Bind Local Asset", GUILayout.Height(26f)))
                    BindLocalAsset();
            }
        }

        private void DrawCloudSection()
        {
            EditorGUILayout.LabelField("Cloud Map", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Fetch metadata by map ID. On-device mode can cache the binary in the project or download it at runtime; cloud mode stores only the map identity.",
                MessageType.None);

            using (new EditorGUI.DisabledScope(m_IsBusy || m_Target == null))
            {
                m_CloudMapId = EditorGUILayout.IntField("Map ID", m_CloudMapId);
                EditorGUILayout.HelpBox("Localization mode is controlled by the SDK Run Plan. Binary caching only prepares data for local use.", MessageType.Info);
                    m_CacheBinaryInProject = EditorGUILayout.Toggle("Cache .bytes In Project", m_CacheBinaryInProject);
                m_DownloadPointCloud = EditorGUILayout.Toggle("Download Point Cloud", m_DownloadPointCloud);

                if (GUILayout.Button("Download And Bind", GUILayout.Height(28f)))
                    BeginCloudBinding();
            }
        }

        private void DrawPointCloudSection()
        {
            EditorGUILayout.LabelField("Point Cloud Preview", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(m_IsBusy || m_Target == null))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Import Local .ply"))
                        ImportLocalPointCloud();
                    if (GUILayout.Button("Download From Cloud"))
                        BeginPointCloudDownload();
                }

                if (m_Target != null && m_Target.Preview != null)
                {
                    if (GUILayout.Button("Select Point Cloud View"))
                        Selection.activeGameObject = m_Target.Preview.gameObject;
                }
            }
        }

        private void DrawAlignmentSection()
        {
            EditorGUILayout.LabelField("World Alignment", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(m_IsBusy || m_Target == null ||
                                               !m_Target.Definition.IsReady ||
                                               MixedSpaceMapWorkspaceService.HasGeneratedLocalMapKey(m_Target.Definition)))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Load"))
                    Run(LoadAlignment(), "Loading alignment…");
                if (GUILayout.Button("Save"))
                    Run(SaveAlignment(), "Saving alignment…");
                if (GUILayout.Button("Reset"))
                    Run(ResetAlignment(), "Resetting alignment…");
            }
        }

        private void CreateBinding()
        {
            Transform parent = Selection.activeTransform;
            MixedSpaceWorldRoot world = parent?.GetComponentInParent<MixedSpaceWorldRoot>(true);
            if (world == null)
            {
                var worldObject = new GameObject("MixedSpace World");
                Undo.RegisterCreatedObjectUndo(worldObject, "Create MixedSpace world");
                world = Undo.AddComponent<MixedSpaceWorldRoot>(worldObject);
                parent = world.transform;
            }
            else
            {
                parent = world.transform;
            }

            var bindingObject = new GameObject("Map Binding");
            Undo.RegisterCreatedObjectUndo(bindingObject, "Create MixedSpace map binding");
            bindingObject.transform.SetParent(parent, false);
            m_Target = Undo.AddComponent<MixedSpaceMapBinding>(bindingObject);
            Selection.activeGameObject = bindingObject;
            SetStatus("Created a new Map Binding.", MessageType.Info);
        }

        private void BindLocalAsset()
        {
            if (!ValidateTarget() || m_LocalMapAsset == null)
            {
                SetStatus("Select a target and a local .bytes asset.", MessageType.Warning);
                return;
            }

            string path = AssetDatabase.GetAssetPath(m_LocalMapAsset);
            if (!path.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("The selected asset must use the .bytes extension.", MessageType.Error);
                return;
            }

            if (!MixedSpaceMapWorkspaceService.TryReadLocalMap(
                    m_LocalMapAsset,
                    out int mapId,
                    out string displayName,
                    out MixedSpaceCloudMapDescriptor descriptor,
                    out bool hasMetadata,
                    out string error))
            {
                SetStatus(error, MessageType.Error);
                return;
            }

            foreach (GameObject root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            foreach (MixedSpaceMapBinding other in root.GetComponentsInChildren<MixedSpaceMapBinding>(true))
            {
                if (other != m_Target && other.Definition.IsReady && other.Definition.Key == mapId)
                {
                    SetStatus($"Map key {mapId} is already used by another binding in this scene.", MessageType.Error);
                    return;
                }
            }

            Undo.RecordObject(m_Target, "Bind local MixedSpace map");
            if (hasMetadata)
            {
                m_Target.DefineFromCloud(
                    descriptor,
                    MixedSpaceProviderIds.OnDevice,
                    m_LocalMapAsset,
                    BuildContentPolicy(MixedSpaceMapContentLocation.EmbeddedAsset),
                    renameObject: true);
            }
            else
            {
                m_Target.Define(
                    mapId,
                    displayName,
                    MixedSpaceProviderIds.OnDevice,
                    m_LocalMapAsset,
                    MixedSpaceMapBinding.IdentityAlignment(),
                    default,
                    0,
                    BuildContentPolicy(MixedSpaceMapContentLocation.EmbeddedAsset),
                    renameObject: true);
            }
            SaveTarget();
            SetStatus($"Bound local map '{displayName}' for on-device localization.", MessageType.Info);
        }

        private void BeginCloudBinding()
        {
            if (!ValidateTarget() || m_CloudMapId <= 0)
            {
                SetStatus("Select a target and enter a positive cloud map ID.", MessageType.Warning);
                return;
            }

            MixedSpaceSDK sdk = MixedSpaceMapWorkspaceService.RequireSceneRuntime();
            if (sdk == null)
            {
                SetStatus("A MixedSpaceSDK instance is required in the open scene.", MessageType.Error);
                return;
            }

            sdk.NetworkEnvironment.ValidateToken(sdk.DeveloperToken, valid =>
            {
                if (!valid)
                {
                    SetStatus("Developer token validation failed.", MessageType.Error);
                    return;
                }
                Run(DownloadAndBind(), "Downloading map metadata…");
            });
        }

        private IEnumerator DownloadAndBind()
        {
            bool metadataReceived = false;
            SDKMapMetadataGetResult metadata = default;
            yield return MixedSpaceMapWorkspaceService.DownloadMapMetadata(
                m_CloudMapId,
                result =>
                {
                    metadata = result;
                    metadataReceived = true;
                });

            if (!metadataReceived)
            {
                Fail("Map metadata download failed.");
                yield break;
            }

            string providerKey = MixedSpaceProviderIds.OnDevice;
            TextAsset embeddedData = null;
            MixedSpaceMapContentPolicy contentPolicy =
                MixedSpaceMapContentPolicy.ProviderManaged();

            if (m_CacheBinaryInProject)
            {
                yield return MixedSpaceMapWorkspaceService.DownloadMapFile(
                    metadata.id,
                    metadata.name,
                    ( asset) => embeddedData = asset);
                if (embeddedData == null)
                {
                    Fail("Map binary download failed.");
                    yield break;
                }
                contentPolicy = BuildContentPolicy(
                    MixedSpaceMapContentLocation.EmbeddedAsset);
            }
            else
            {
                contentPolicy = BuildContentPolicy(
                    MixedSpaceMapContentLocation.RemoteBinary);
            }

            Undo.RecordObject(m_Target, "Bind MixedSpace cloud map");
            m_Target.DefineFromCloud(
                MixedSpaceCloudMapRecordAdapter.ToDomain(metadata),
                providerKey,
                embeddedData,
                contentPolicy,
                renameObject: true);

            if (m_DownloadPointCloud)
                yield return DownloadPointCloud();

            SaveTarget();
            Complete($"Bound cloud map {m_Target.Definition.Key} to {providerKey}.");
        }

        private void BeginPointCloudDownload()
        {
            if (!ValidateBoundTarget())
                return;
            Run(DownloadPointCloud(), "Downloading point cloud…");
        }

        private IEnumerator DownloadPointCloud()
        {
            bool downloaded = false;
            yield return MixedSpaceMapWorkspaceService.DownloadPointCloud(
                m_Target.Definition.Key,
                m_Target.Definition.Label,
                (_, path) =>
                {
                    EnsurePointCloudView();
                    m_Target.Preview.PresentFile(path);
                    downloaded = true;
                });

            if (!downloaded)
                Fail("Point cloud download failed.");
        }

        private void ImportLocalPointCloud()
        {
            if (!ValidateTarget())
                return;

            string path = EditorUtility.OpenFilePanel("Import MixedSpace point cloud", string.Empty, "ply");
            if (string.IsNullOrEmpty(path))
                return;

            Undo.RegisterFullObjectHierarchyUndo(m_Target.gameObject, "Import point cloud preview");
            EnsurePointCloudView();
            m_Target.Preview.PresentFile(path);
            SaveTarget();
            SetStatus("Imported local point cloud preview.", MessageType.Info);
        }

        private IEnumerator LoadAlignment()
        {
            bool received = false;
            yield return MixedSpaceMapWorkspaceService.DownloadMapMetadata(
                m_Target.Definition.Key,
                metadata =>
                {
                    Undo.RecordObject(m_Target, "Load MixedSpace alignment");
                    Undo.RecordObject(m_Target.transform, "Load MixedSpace alignment");
                    MixedSpaceMapDefinition current = m_Target.Definition;
                    m_Target.DefineFromCloud(
                        MixedSpaceCloudMapRecordAdapter.ToDomain(metadata),
                        current.ProviderKey,
                        current.EmbeddedData,
                        current.ContentPolicy,
                        renameObject: false);
                    m_Target.ApplyRelationToTransform();
                    received = true;
                });

            if (!received)
            {
                Fail("Alignment download failed.");
                yield break;
            }
            SaveTarget();
            Complete("Loaded alignment from cloud metadata.");
        }

        private IEnumerator SaveAlignment()
        {
            MixedSpaceSDK sdk = MixedSpaceMapWorkspaceService.RequireSceneRuntime();
            Vector3 position = m_Target.transform.localPosition;
            Quaternion rotation = m_Target.transform.localRotation;
            Vector3 scale = m_Target.transform.localScale;
            float uniformScale = (scale.x + scale.y + scale.z) / 3f;
            Matrix4x4 rightHanded = Matrix4x4.TRS(position, rotation, scale);
            rightHanded = MixedSpaceCoordinateFrame.ReflectXAxis(rightHanded);
            position = rightHanded.GetColumn(3);
            rotation = rightHanded.rotation;

            var requestData = new SDKMapAlignmentSetRequest
            {
                token = sdk.DeveloperToken,
                id = m_Target.Definition.Key,
                tx = position.x,
                ty = position.y,
                tz = position.z,
                qx = rotation.x,
                qy = rotation.y,
                qz = rotation.z,
                qw = rotation.w,
                scale = uniformScale
            };

            using (UnityWebRequest request = CreatePostRequest(SDKMapAlignmentSetRequest.endpoint, JsonUtility.ToJson(requestData)))
            {
                yield return request.SendWebRequest();
                if (!Succeeded(request))
                {
                    Fail($"Alignment save failed: {request.error}");
                    yield break;
                }
            }

            yield return LoadAlignment();
        }

        private IEnumerator ResetAlignment()
        {
            MixedSpaceSDK sdk = MixedSpaceMapWorkspaceService.RequireSceneRuntime();
            var requestData = new SDKMapAlignmentResetRequest
            {
                token = sdk.DeveloperToken,
                id = m_Target.Definition.Key
            };

            using (UnityWebRequest request = CreatePostRequest(SDKMapAlignmentResetRequest.endpoint, JsonUtility.ToJson(requestData)))
            {
                yield return request.SendWebRequest();
                if (!Succeeded(request))
                {
                    Fail($"Alignment reset failed: {request.error}");
                    yield break;
                }
            }

            yield return LoadAlignment();
        }

        private static UnityWebRequest CreatePostRequest(string endpoint, string json)
        {
            string uri = string.Format(
                MixedSpaceCloudClient.UrlFormat,
                MixedSpaceMapWorkspaceService.RequireSceneRuntime().NetworkEnvironment.LocalizationServer,
                endpoint);
            UnityWebRequest request = UnityWebRequest.Put(uri, json);
            request.method = UnityWebRequest.kHttpVerbPOST;
            request.useHttpContinue = false;
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "application/json");
            return request;
        }

        private static bool Succeeded(UnityWebRequest request)
        {
#if UNITY_2020_1_OR_NEWER
            return request.result == UnityWebRequest.Result.Success;
#else
            return !request.isNetworkError && !request.isHttpError;
#endif
        }

        private static MixedSpaceMapContentPolicy BuildContentPolicy(
            MixedSpaceMapContentLocation location)
        {
            return new MixedSpaceMapContentPolicy(location);
        }

        private void EnsurePointCloudView()
        {
            if (m_Target.Preview == null)
                MixedSpacePointCloudPresenter.Show(m_Target);
        }

        private bool ValidateTarget()
        {
            if (m_Target == null)
                return false;
            if (m_Target.GetComponentInParent<MixedSpaceWorldRoot>(true) != null)
                return true;

            SetStatus("Map Binding is not below a MixedSpace World Root.", MessageType.Error);
            return false;
        }

        private bool ValidateBoundTarget()
        {
            if (ValidateTarget() && m_Target.Definition.IsReady && m_Target.Definition.Key > 0)
            {
                if (MixedSpaceMapWorkspaceService.HasGeneratedLocalMapKey(m_Target.Definition))
                {
                    SetStatus("This local-only map has no cloud map ID.", MessageType.Warning);
                    return false;
                }
                return true;
            }
            SetStatus("Bind a valid map before using this operation.", MessageType.Warning);
            return false;
        }

        private void SaveTarget()
        {
            if (m_Target == null)
                return;
            EditorUtility.SetDirty(m_Target);
            if (m_Target.Preview != null)
                EditorUtility.SetDirty(m_Target.Preview);
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        private void Run(IEnumerator operation, string status)
        {
            if (m_IsBusy)
                return;
            m_IsBusy = true;
            SetStatus(status, MessageType.Info);
            EditorCoroutineUtility.StartCoroutine(RunGuarded(operation), this);
        }

        private IEnumerator RunGuarded(IEnumerator operation)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!operation.MoveNext())
                        break;
                    current = operation.Current;
                }
                catch (Exception exception)
                {
                    Fail(exception.Message);
                    MixedSpaceLogger.LogError(exception.ToString());
                    yield break;
                }
                yield return current;
            }

            m_IsBusy = false;
            Repaint();
        }

        private void Complete(string message)
        {
            m_IsBusy = false;
            SetStatus(message, MessageType.Info);
        }

        private void Fail(string message)
        {
            m_IsBusy = false;
            SetStatus(message, MessageType.Error);
        }

        private void SetStatus(string message, MessageType type)
        {
            m_Status = message;
            m_StatusType = type;
            Repaint();
        }
    }
}

#endif
