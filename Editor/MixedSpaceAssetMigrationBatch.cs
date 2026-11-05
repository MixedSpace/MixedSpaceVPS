#if UNITY_EDITOR && MIXEDSPACE_INTERNAL_TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace MixedSpace.EditorTools
{
    /// <summary>
    /// 无界面的资产清单检查；不会进入播放模式或启动 SDK 服务。
    /// Headless asset inventory. Never enters Play Mode or starts SDK services.
    /// </summary>
    public static class MixedSpaceAssetMigrationBatch
    {
        internal const string PackageRoot = "Packages/com.ningfeng.mixedspace";
        internal const string BasePrefab = PackageRoot + "/Runtime/Resources/Prefabs/MixedSpaceSDK.prefab";
        internal const string ScriptPath = PackageRoot + "/Runtime/Scripts/MixedSpaceSDK.cs";

        internal static string[] FindProjectAssets(string filter) =>
            AssetDatabase.FindAssets(filter, new[] { "Assets" })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => !path.StartsWith("Assets/__MixedSpaceMigrationStage_", StringComparison.Ordinal))
                .Where(path => AssetDatabase.GetDependencies(path, true)
                    .Any(dependency => dependency == BasePrefab || dependency == ScriptPath))
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();

        /// <summary>
        /// 离线配置示例的已知引用，保留已有非空引用。保存前备份源场景，保存后重新打开验证；不启动服务，不修改地图、凭据或 UnityEvent。
        /// Offline sample reference configuration. Only known sample endpoints
        /// are edited; existing non-null references are preserved. Every source
        /// scene is backed up before saving, and each saved scene is reopened.
        /// Does not start SDK services or change maps, credentials or UnityEvents.
        /// </summary>
        public static void BindSampleRuntimeOwners()
        {
            string stage = "Assets/__MixedSpaceSampleBindings_" + Guid.NewGuid().ToString("N");
            try
            {
                string records = "ProjectSettings/MixedSpaceMigrationRecords/sample-bindings-" +
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                Directory.CreateDirectory(records);
                var results = new List<string>();
                var paths = FindProjectAssets("t:Scene").ToList();
                string packageScene = Path.GetFullPath(Path.Combine(
                    Application.dataPath, "../../MixedSpaceMobilePackage/Samples~/Scenes/SampleScene.unity"));
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(stage));
                string stagedScene = stage + "/SampleScene.unity";
                File.Copy(packageScene, stagedScene, false);
                AssetDatabase.ImportAsset(stagedScene, ImportAssetOptions.ForceSynchronousImport);
                paths.Add(stagedScene);
                for (int index = 0; index < paths.Count; index++)
                {
                    string path = paths[index];
                    string source = path == stagedScene ? packageScene : path;
                    File.Copy(source, Path.Combine(records, index + "-" + Path.GetFileName(source)), false);
                    var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                    int edits = BindSceneSampleOwners(scene, false);
                    if (edits > 0 && !EditorSceneManager.SaveScene(scene))
                        throw new InvalidOperationException("Could not save sample bindings: " + path);
                    scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                    BindSceneSampleOwners(scene, true);
                    if (path == stagedScene && edits > 0) File.Copy(stagedScene, packageScene, true);
                    results.Add("PASS " + (path == stagedScene ? "Package SampleScene" : path) + " assigned=" + edits);
                }
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(stage);
                File.WriteAllLines(Path.Combine(records, "result.txt"), results);
                foreach (string result in results) Debug.Log(result);
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                // 失败时保留备份。临时暂存场景不是源资产，场景卸载后才能删除暂存目录。
                // Keep backups on failure. The temporary stage is never a source asset and can be removed once no scene is
                // loaded from it.
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                AssetDatabase.DeleteAsset(stage);
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static int BindSceneSampleOwners(UnityEngine.SceneManagement.Scene scene, bool verifyOnly)
        {
            var names = new HashSet<string> {
                "MixedSpace.Samples.CaptureResolutionDropdown", "CustomLocalization",
                "MixedSpace.Samples.MapListController", "MixedSpace.Samples.LocalizationSettingsPanel",
                "MixedSpace.Samples.Util.LocalizationStatus", "MixedSpace.Samples.Util.PoseIndicator",
                "MixedSpace.Samples.Mapping.RealtimeCaptureManager" };
            var components = scene.GetRootGameObjects().SelectMany(root =>
                root.GetComponentsInChildren<MonoBehaviour>(true)).Where(item => item != null).ToArray();
            var owners = components.OfType<MixedSpaceSDK>().ToArray();
            var endpoints = components.Where(item => names.Contains(item.GetType().FullName)).ToArray();
            if (owners.Length != 1)
                throw new InvalidOperationException("Sample scene needs exactly one unambiguous SDK owner: " + scene.path);
            var worlds = components.OfType<MixedSpace.XR.MixedSpaceWorldRoot>().ToArray();
            int edits = 0;
            foreach (var endpoint in endpoints)
            {
                var serialized = new SerializedObject(endpoint);
                foreach (string field in new[] { "m_Sdk", endpoint.GetType().Name == "LocalizationSettingsPanel" ? "m_World" : "m_Space" })
                {
                    var property = serialized.FindProperty(field);
                    if (property == null) { if (field == "m_Sdk") throw new InvalidOperationException("Missing sample owner slot."); else continue; }
                    if (property.objectReferenceValue != null) continue;
                    if (verifyOnly) throw new InvalidOperationException("Unbound sample endpoint: " + endpoint.GetType().Name + "." + field);
                    if (field == "m_Sdk") property.objectReferenceValue = owners[0];
                    else if (worlds.Length == 1) property.objectReferenceValue = worlds[0];
                    else throw new InvalidOperationException("Select an explicit World for " + endpoint.GetType().Name);
                    ++edits;
                }
                if (!verifyOnly) serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            return edits;
        }

        public static void Inspect()
        {
            try
            {
                var lines = new List<string> { "MixedSpace asset inventory (read-only)", DateTime.UtcNow.ToString("O") };
                foreach (string path in FindProjectAssets("t:Scene"))
                {
                    var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                    BindSceneSampleOwners(scene, true);
                    var sdks = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<MixedSpaceSDK>(true)).ToArray();
                    lines.Add("SCENE " + path + " SDK=" + sdks.Length + " run-plans=" + sdks.Count(sdk => sdk.RunPlan != null));
                    foreach (var sdk in sdks)
                    {
                        // 仅读取序列化和策略，不触发服务组装。
                        // Force only serialization/policy reads, never service composition.
                        var settings = sdk.RuntimeConfiguration;
                        lines.Add("  policies-readable=" + (settings != null) + " platform=" + (sdk.Platform != null));
                    }
                }
                foreach (string path in FindProjectAssets("t:Prefab")) lines.Add("PREFAB " + path);
                lines.Add("BASE " + BasePrefab);
                Directory.CreateDirectory("ProjectSettings/MixedSpaceMigrationRecords");
                File.WriteAllLines("ProjectSettings/MixedSpaceMigrationRecords/asset-inventory.txt", lines);
                foreach (string line in lines) Debug.Log(line);
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// 在 Unity 内运行指定的 NUnit 架构契约验证，不进入播放模式。单独编译的测试程序集放在 Library，避免启动时被清理；不是完整的 Unity Test Runner 测试集。
        /// Runs the schema validation NUnit methods inside Unity without
        /// entering Play Mode. The separately compiled test assembly is supplied
        /// under Library because Unity removes Temp on the next editor startup.
        /// This is a targeted contract run, not the full Unity Test Runner suite.
        /// </summary>
        public static async void RunIntegrationContracts()
        {
            try
            {
                string nunit = Directory.GetFiles("Library/PackageCache", "nunit.framework.dll", SearchOption.AllDirectories).Single();
                System.Reflection.Assembly.LoadFrom(Path.GetFullPath(nunit));
                var assembly = System.Reflection.Assembly.LoadFrom(Path.GetFullPath(
                    "Library/MixedSpaceMigrationContracts/MixedSpace.Core.Tests.dll"));
                var type = assembly.GetType("MixedSpace.Tests.RuntimeArchitectureTests", true);
                var instance = Activator.CreateInstance(type);
                string[] contracts =
                {
                    "SdkPrefab_UsesCodeComposedRuntime",
                    "RunPlan_SnapshotsAreDetachedAndPreserveAuthoringValues",
                    "RunPlan_EditorReadsDoNotMutateSceneData",
                    "ApplicationBinding_FacadeForwardsPersistentEventTargets",
                    "RequiredOwners_PreservePoliciesScenePortsAndCallbacks",
                    "RequiredOwners_MissingOwnersRejectFallbackWithoutConstructingServices",
                    "CoordinateReflection_PreservesShippedMatrixAndQuaternionBehavior",
                    "RetiredComponentCarriers_AreAbsentFromRuntimeAssembly",
                    "MapContent_ExplicitPolicyOwnsPayloadWithoutLegacyReader",
                    "CloudClients_UseTheirOwnEnvironmentAndTransport",
                    "HttpTransport_StreamsResponseAndOwnsRequest"
                };
                var results = new List<string>();
                foreach (string contract in contracts)
                {
                    try
                    {
                        if (type.GetMethod(contract).Invoke(instance, null) is System.Threading.Tasks.Task task)
                            await task;
                        results.Add("PASS " + contract);
                    }
                    catch (Exception exception)
                    {
                        results.Add("FAIL " + contract);
                        Debug.LogException(exception.InnerException ?? exception);
                    }
                }
                Directory.CreateDirectory("ProjectSettings/MixedSpaceMigrationRecords");
                // 额外测试集检查 Unity 原生对象和几何，不启动定位、网络或相机。
                // Additional suites exercise native Unity objects and geometry, but never start localization, network services
                // or a camera.
                foreach (string suite in new[] { "WorldOutputUnityTests", "PlatformProfileUnityTests", "CloudWireUnityTests", "CameraArchitectureUnityTests", "SampleNavigationUnityTests" })
                {
                    var suiteType = assembly.GetType("MixedSpace.Tests." + suite, true);
                    var suiteInstance = Activator.CreateInstance(suiteType);
                    foreach (var method in suiteType.GetMethods().Where(method =>
                        method.GetCustomAttributes(false).Any(attribute =>
                            attribute.GetType().FullName == "NUnit.Framework.TestAttribute")))
                    {
                        try
                        {
                            if (method.Invoke(suiteInstance, null) is System.Threading.Tasks.Task task)
                                await task;
                            results.Add("PASS " + suite + "." + method.Name);
                        }
                        catch (Exception exception)
                        {
                            results.Add("FAIL " + suite + "." + method.Name);
                            Debug.LogException(exception.InnerException ?? exception);
                        }
                    }
                }
                File.WriteAllLines("ProjectSettings/MixedSpaceMigrationRecords/unity-migration-contracts.txt", results);
                foreach (string result in results) Debug.Log(result);
                if (results.Any(result => result.StartsWith("FAIL", StringComparison.Ordinal)))
                    EditorApplication.Exit(1);
                else Inspect();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }
    }
}
#endif
