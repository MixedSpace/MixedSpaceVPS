#if UNITY_EDITOR && MIXEDSPACE_INTERNAL_TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MixedSpace.XR;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MixedSpace.EditorTools
{
    /// <summary>
    /// 保存当前格式资产并重新打开验证结构；缺少所有者或脚本时中止。不进入播放模式，不调用定位或网络服务。
    /// Saves current-schema assets and verifies them after reopening; missing owners or scripts stop verification. Does not enter Play Mode or invoke localization or network services.
    /// </summary>
    public static class MixedSpaceCurrentSchemaVerification
    {
        private const string Package = "Packages/com.njyjxr.mixedspace";
        private static readonly HashSet<string> ExistingIssues = new HashSet<string>();

        public static void SaveAndVerify()
        {
            try
            {
                if (!Application.isBatchMode)
                    throw new InvalidOperationException("Run this verification in a closed-project batch session.");
                var records = new List<string> { "Current schema verification " + DateTime.UtcNow.ToString("O") };
                string[] scripts = {
                    Package + "/Runtime/Scripts/MixedSpaceSDK.cs",
                    Package + "/Runtime/Scripts/XR/MixedSpaceWorldRoot.cs",
                    Package + "/Runtime/Scripts/XR/MixedSpaceMapBinding.cs",
                    Package + "/Runtime/Scripts/XR/MixedSpacePointCloudView.cs",
                    Package + "/Runtime/Scripts/XR/DataProcessing/MixedSpaceMapTransitionView.cs",
                    Package + "/Runtime/Scripts/XR/DataProcessing/MixedSpaceGeographicPoseView.cs"
                };
                string[] Find(string filter) => AssetDatabase.FindAssets(filter, new[] { "Assets" })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Where(path => !path.StartsWith("Assets/__MixedSpace", StringComparison.Ordinal))
                    .Where(path => AssetDatabase.GetDependencies(path, true).Intersect(scripts).Any())
                    .OrderBy(path => path, StringComparer.Ordinal).ToArray();
                var prefabs = new[] { MixedSpaceAssetMigrationBatch.BasePrefab }.Concat(Find("t:Prefab")).Distinct();
                foreach (string path in prefabs)
                {
                    var root = PrefabUtility.LoadPrefabContents(path);
                    string before;
                    try
                    {
                        before = Capture(new[] { root });
                        PrefabUtility.SaveAsPrefabAsset(root, path, out bool saved);
                        if (!saved) throw new IOException("Could not save prefab: " + path);
                    }
                    finally { PrefabUtility.UnloadPrefabContents(root); }
                    root = PrefabUtility.LoadPrefabContents(path);
                    try { RequireEqual(before, Capture(new[] { root }), path); }
                    finally { PrefabUtility.UnloadPrefabContents(root); }
                    records.Add("PASS prefab save/reopen " + path);
                }
                foreach (string path in Find("t:Scene")) VerifyScene(path, records);

                // Unity 不能直接打开 Samples~ 场景；只暂存当前场景，保留原始 meta/GUID，只清理本次暂存内容。
                // Unity cannot open a Samples~ scene directly. Stage only this scene, preserve the published meta/GUID, and
                // remove only our stage.
                string folder = "Assets/__MixedSpaceSchemaStage_" + Guid.NewGuid().ToString("N");
                string folderGuid = AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
                if (string.IsNullOrEmpty(folderGuid)) throw new IOException("Could not create staging folder.");
                string staged = folder + "/Sample.unity";
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(Package);
                string source = Path.Combine(package.resolvedPath, "Samples~/Scenes/SampleScene.unity");
                File.Copy(source, staged, false);
                AssetDatabase.ImportAsset(staged, ImportAssetOptions.ForceSynchronousImport);
                VerifyScene(staged, records);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                File.Copy(staged, source, true);
                if (!AssetDatabase.DeleteAsset(folder)) throw new IOException("Could not remove owned staging folder.");
                AssetDatabase.SaveAssets();
                records.AddRange(ExistingIssues.OrderBy(value => value, StringComparer.Ordinal));
                Directory.CreateDirectory("ProjectSettings/MixedSpaceMigrationRecords");
                File.WriteAllLines("ProjectSettings/MixedSpaceMigrationRecords/current-schema-verification.txt", records);
                foreach (string record in records) Debug.Log(record);
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static void VerifyScene(string path, List<string> records)
        {
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            string before = Capture(scene.GetRootGameObjects());
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Could not save scene: " + path);
            scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            RequireEqual(before, Capture(scene.GetRootGameObjects()), path);
            records.Add("PASS scene values/references save/reopen " + path);
        }

        private static string Capture(GameObject[] roots)
        {
            var state = new List<string>();
            foreach (var root in roots)
            {
                foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                {
                    int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject);
                    if (missing == 0) continue;
                    if (!IsRecordedMissingLightScript(transform.gameObject, missing))
                        throw new InvalidOperationException("Unreviewed missing script at " + Hierarchy(transform));
                    state.Add("existing-missing-script:" + Hierarchy(transform));
                    ExistingIssues.Add("KNOWN EXISTING missing Directional Light script preserved: " + transform.gameObject.scene.path);
                }
                foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (component == null || component.GetType().Namespace?.StartsWith("MixedSpace", StringComparison.Ordinal) != true)
                        continue;
                    if (component is MixedSpaceSDK sdk)
                    {
                        if (sdk.RunPlan == null || sdk.Platform == null || sdk.ApplicationSignals == null)
                            throw new InvalidOperationException("SDK owners are incomplete.");
                        state.Add(JsonUtility.ToJson(sdk.RunPlan.CreateRuntimeSettings()));
                        _ = sdk.RuntimeConfiguration;
                    }
                    if (component is MixedSpaceMapBinding map) _ = map.Definition;
                    if (component.GetType().Name == "MixedSpaceARFoundationAdapter")
                    {
                        var profile = new SerializedObject(component).FindProperty("m_Profile").objectReferenceValue;
                        if (profile == null) throw new InvalidOperationException("AR Foundation Profile is missing.");
                        var data = new SerializedObject(profile);
                        foreach (var entry in new[] { ("m_ProbeLimit", 10), ("m_ProbeDelayMilliseconds", 100),
                            ("m_Android", 2), ("m_Ios", 0), ("m_Format", 0) })
                            if (data.FindProperty(entry.Item1).intValue != entry.Item2)
                                throw new InvalidOperationException("Platform baseline changed: " + entry.Item1);
                        state.Add(EditorJsonUtility.ToJson(profile));
                    }
                    // 重新打开后实例 ID 会变化，应转换为持久资产 ID 或确定的场景层级标识。
                    // Instance IDs change on reopen. Normalize object references to persistent asset IDs or deterministic scene
                    // hierarchy identities.
                    string json = Regex.Replace(EditorJsonUtility.ToJson(component),
                        "\"instanceID\":\\s*(-?\\d+)", match =>
                            "\"reference\":\"" + Identify(EditorUtility.InstanceIDToObject(int.Parse(match.Groups[1].Value))) + "\"");
                    state.Add(Identify(component) + json);
                }
            }
            state.Sort(StringComparer.Ordinal);
            using var hash = SHA256.Create();
            // 审查输出不写入序列化值或凭据。
            // Never write serialized values or credentials into audit output.
            return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", state))));
        }

        private static string Identify(Object value)
        {
            if (value == null) return "null";
            if (AssetDatabase.Contains(value)) return GlobalObjectId.GetGlobalObjectIdSlow(value).ToString();
            if (value is Component component)
                return Hierarchy(component.transform) + "/" + component.GetType().FullName + "/" +
                    Array.IndexOf(component.gameObject.GetComponents<Component>(), component);
            if (value is GameObject gameObject) return Hierarchy(gameObject.transform);
            return value.GetType().FullName + "/" + value.name;
        }

        private static bool IsRecordedMissingLightScript(GameObject gameObject, int count)
        {
            // 仅允许此示例对象缺少指定脚本；其他脚本缺失仍会导致失败。
            // The following missing-script identifier is explicitly allowed for this sample object; unrelated missing
            // scripts still fail.
            const string guid = "b3fc08d7569d8b045bbe0943da6c434a";
            string path = gameObject.scene.path;
            if (count != 1 || gameObject.name != "Directional Light" || !File.Exists(path)) return false;
            ulong id = GlobalObjectId.GetGlobalObjectIdSlow(gameObject).targetObjectId;
            return Regex.Split(File.ReadAllText(path), "(?m)^--- ")
                .Any(document => document.StartsWith("!u!114 ", StringComparison.Ordinal) &&
                    document.Contains("m_GameObject: {fileID: " + id + "}") &&
                    document.Contains("guid: " + guid + ","));
        }

        private static string Hierarchy(Transform transform) =>
            (transform.parent != null ? Hierarchy(transform.parent) : "") + "/" + transform.GetSiblingIndex();

        private static void RequireEqual(string before, string after, string path)
        {
            if (before != after) throw new InvalidOperationException("Serialized values or references changed on reopen: " + path);
        }
    }
}
#endif
