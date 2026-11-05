#if UNITY_EDITOR

using System;
using System.Collections;
using System.IO;
using MixedSpace.ServiceContracts;
using MixedSpace.REST;
using MixedSpace.XR;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace MixedSpace.EditorTools
{
    /// <summary>
    /// 仅供编辑器使用的地图下载和持久化辅助逻辑，与运行时仓库分离，避免移动端程序集依赖文件编辑和 AssetDatabase。
    /// Editor-only map asset download and persistence helpers.
    /// Keeping these operations outside the runtime map repository prevents file
    /// and AssetDatabase concerns from leaking into mobile platform assemblies.
    /// </summary>
    internal static class MixedSpaceMapWorkspaceService
    {
        /// <summary>
        /// 仅在编辑阶段解析活动场景中的所有者，不填充全局运行时状态，也不隐式选择其他场景的对象。
        /// Authoring-only resolution, scoped to the active scene. Never populates
        /// runtime global state or silently selects an owner in another scene.
        /// </summary>
        internal static MixedSpaceSDK RequireSceneRuntime()
        {
            var owners = new System.Collections.Generic.List<MixedSpaceSDK>();
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            foreach (var root in scene.GetRootGameObjects())
                owners.AddRange(root.GetComponentsInChildren<MixedSpaceSDK>(true));
            if (owners.Count != 1)
                throw new InvalidOperationException("Map Workspace requires exactly one SDK in the active scene.");
            return owners[0];
        }

        public static string GetDirectoryPath(string inputPath = "", bool assetsRoot = false)
        {
            string root = assetsRoot ? "Assets/" : Application.dataPath;
            string defaultPath = Path.Combine(root, MixedSpaceMapWorkspaceService.RequireSceneRuntime().CacheDirectory);
            return string.IsNullOrEmpty(inputPath) ? defaultPath : Path.Combine(root, inputPath);
        }

        /// <summary>
        /// 读取本地地图资产供编辑器使用的标识及可选缓存元数据；运行时组件不检查 AssetDatabase 路径。
        /// Reads the editor-facing identity and optional cached metadata for a local
        /// map asset. Runtime scene components never inspect AssetDatabase paths.
        /// </summary>
        public static bool TryReadLocalMap(
            TextAsset mapAsset,
            out int mapId,
            out string displayName,
            out MixedSpaceCloudMapDescriptor descriptor,
            out bool hasMetadata,
            out string error)
        {
            mapId = -1;
            displayName = string.Empty;
            descriptor = null;
            hasMetadata = false;
            error = string.Empty;

            if (mapAsset == null)
            {
                error = "Select a local .bytes asset.";
                return false;
            }

            string assetName = mapAsset.name;
            string assetPath = AssetDatabase.GetAssetPath(mapAsset);
            string fullMapPath = ResolveAssetFullPath(assetPath);
            if (string.IsNullOrEmpty(fullMapPath))
            {
                error = $"Could not resolve the project path for '{assetName}'.";
                return false;
            }

            int separator = assetName.IndexOf('-');
            string idToken = separator > 0 ? assetName.Substring(0, separator) : assetName;
            bool hasNumericPrefix = int.TryParse(idToken, out int parsedId) && parsedId > 0;
            displayName = hasNumericPrefix && separator >= 0 && separator < assetName.Length - 1
                ? assetName.Substring(separator + 1)
                : assetName;
            // 绑定本地资产不会创建云端身份；正整数键仅用于原生地图仓库的稳定索引。
            // Bind Local Asset never establishes a cloud identity. The positive key is only a stable index for the native
            // map store.
            mapId = CreateLocalMapKey(assetPath);

            string metadataPath = Path.Combine(
                Path.GetDirectoryName(fullMapPath) ?? string.Empty,
                $"{assetName}-metadata.json");
            if (!File.Exists(metadataPath))
                return true;

            try
            {
                SDKMapMetadataGetResult transport =
                    JsonUtility.FromJson<SDKMapMetadataGetResult>(
                        File.ReadAllText(metadataPath));
                descriptor = MixedSpaceCloudMapRecordAdapter.ToDomain(
                    transport,
                    mapId,
                    displayName);
                hasMetadata = true;
                return true;
            }
            catch (Exception exception)
            {
                error = $"Cached metadata for '{assetName}' is invalid: {exception.Message}";
                return false;
            }
        }

        // 无云端身份的地图也需要正整数键；使用资产 GUID，使重命名不改变键。
        // Native map lookup needs a positive integer even when the asset has no cloud identity. The asset GUID makes
        // this key stable across renames.
        private static int CreateLocalMapKey(string assetPath)
        {
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid))
                throw new InvalidOperationException($"No Unity asset GUID for '{assetPath}'.");

            uint hash = 2166136261;
            foreach (char character in guid)
            {
                hash ^= character;
                hash = unchecked(hash * 16777619);
            }
            return (int)(0x40000000u | (hash & 0x3fffffffu));
        }

        internal static bool HasGeneratedLocalMapKey(MixedSpaceMapDefinition definition)
        {
            if (definition?.EmbeddedData == null)
                return false;
            string path = AssetDatabase.GetAssetPath(definition.EmbeddedData);
            return !string.IsNullOrEmpty(path) &&
                   definition.Key == CreateLocalMapKey(path);
        }

        public static IEnumerator DownloadMapMetadata(
            int mapId,
            Action<SDKMapMetadataGetResult> resultCallback,
            string jsonWritePath = "")
        {
            string targetFullPath = GetDirectoryPath(jsonWritePath);
            if (!EnsureDirectory(targetFullPath))
                yield break;

            SDKMapMetadataGetRequest requestData = new SDKMapMetadataGetRequest
            {
                token = MixedSpaceMapWorkspaceService.RequireSceneRuntime().DeveloperToken,
                id = mapId
            };

            if (string.IsNullOrEmpty(requestData.token))
                MixedSpaceLogger.LogWarning("Trying to download map data without developer token.");

            string json = JsonUtility.ToJson(requestData);
     
            using UnityWebRequest request = CreatePostRequest(
                SDKMapMetadataGetRequest.endpoint,
                json);
            yield return request.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
            if (request.result != UnityWebRequest.Result.Success)
#else
            if (request.isNetworkError || request.isHttpError)
#endif
            {
                MixedSpaceLogger.LogError($"Map metadata download failed: HTTP {request.responseCode}.");
                yield break;
            }

            SDKMapMetadataGetResult result =
                JsonUtility.FromJson<SDKMapMetadataGetResult>(request.downloadHandler.text);
            if (result.error != "none")
                yield break;

            resultCallback?.Invoke(result);
            string fileName = $"{result.id}-{result.name}-metadata.json";
            WriteJson(Path.Combine(targetFullPath, fileName), request.downloadHandler.text);
            ImportAsset(Path.Combine(GetDirectoryPath(jsonWritePath, true), fileName));
        }

        public static IEnumerator DownloadMapFile(
            int mapId,
            string mapName,
            Action<TextAsset> resultCallback,
            string bytesWritePath = "")
        {
            string targetFullPath = GetDirectoryPath(bytesWritePath);
            if (!EnsureDirectory(targetFullPath))
                yield break;

            using UnityWebRequest request = CreateGetRequest(SDKMapDownloadRequest.endpoint,mapId,MixedSpaceMapWorkspaceService.RequireSceneRuntime().DeveloperToken);
            
            yield return request.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
            if (request.result != UnityWebRequest.Result.Success)
#else
            if (request.isNetworkError || request.isHttpError)
#endif
            {
                MixedSpaceLogger.LogError($"Map binary download failed: HTTP {request.responseCode}.");
                yield break;
            }


            string fileName = $"{mapId}-{mapName}.bytes";
            WriteBytes(Path.Combine(targetFullPath, fileName), request.downloadHandler.data);
            string assetPath = Path.Combine(GetDirectoryPath(bytesWritePath, true), fileName);
            ImportAsset(assetPath);
            TextAsset mapFile = AssetDatabase.LoadAssetAtPath<TextAsset>(NormalizeAssetPath(assetPath));
            resultCallback?.Invoke(mapFile);
        }

        public static IEnumerator DownloadPointCloud(
            int mapId,
            string mapName,
            Action<SDKSparseDownloadResult, string> resultCallback,
            string plyWritePath = "")
        {
            string targetFullPath = GetDirectoryPath(plyWritePath);
            if (!EnsureDirectory(targetFullPath))
                yield break;

            string uri = string.Format(
                MixedSpaceCloudClient.UrlFormat,
                MixedSpaceMapWorkspaceService.RequireSceneRuntime().NetworkEnvironment.LocalizationServer,
                SDKSparseDownloadRequest.endpoint);
            uri += $"?token={UnityWebRequest.EscapeURL(MixedSpaceMapWorkspaceService.RequireSceneRuntime().DeveloperToken)}&id={mapId}";

            using UnityWebRequest request = UnityWebRequest.Get(uri);
            yield return request.SendWebRequest();

#if UNITY_2020_1_OR_NEWER
            if (request.result != UnityWebRequest.Result.Success)
#else
            if (request.isNetworkError || request.isHttpError)
#endif
            {
                MixedSpaceLogger.LogError(request.error);
                yield break;
            }

            SDKSparseDownloadResult result = new SDKSparseDownloadResult
            {
                data = request.downloadHandler.data,
                error = request.error,
                isSuccess = request.error == null
            };
            if (!result.isSuccess)
                yield break;

            string fileName = $"{mapId}-{mapName}-sparse.ply";
            string filePath = Path.Combine(targetFullPath, fileName);
            if (!File.Exists(filePath))
                File.WriteAllBytes(filePath, result.data);

            resultCallback?.Invoke(result, filePath);
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
        private static UnityWebRequest CreateGetRequest(string endpoint,int mapId,string token)
        {
            
            string uri = string.Format(
                MixedSpaceCloudClient.UrlFormat,
                MixedSpaceMapWorkspaceService.RequireSceneRuntime().NetworkEnvironment.LocalizationServer,
                endpoint)+$"?id={mapId}&token={token}";
            UnityWebRequest request = UnityWebRequest.Get(uri);
            request.useHttpContinue = false;
            return request;
        }


        private static bool EnsureDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                MixedSpaceLogger.LogError("Requested path is null or empty.");
                return false;
            }

            try
            {
                string fullPath = Path.GetFullPath(path);
                if (!Directory.Exists(fullPath))
                    Directory.CreateDirectory(fullPath);
                return true;
            }
            catch (Exception exception)
            {
                MixedSpaceLogger.LogError($"Requested path is invalid: {path}. {exception.Message}");
                return false;
            }
        }

        private static void WriteJson(string filePath, string data)
        {
            if (!File.Exists(filePath))
                File.WriteAllText(filePath, data);
        }

        private static void WriteBytes(string filePath, string base64Data)
        {
            if (!File.Exists(filePath))
                File.WriteAllBytes(filePath, Convert.FromBase64String(base64Data));
        }

        private static void WriteBytes(string filePath, byte[] data)
        {
            if (!File.Exists(filePath))
                File.WriteAllBytes(filePath, (data));
        }

        private static void ImportAsset(string assetPath)
        {
            string normalizedPath = NormalizeAssetPath(assetPath);
            AssetDatabase.Refresh();
            AssetDatabase.ImportAsset(normalizedPath);
        }

        private static string NormalizeAssetPath(string path)
        {
            return path.Replace('\\', '/');
        }

        private static string ResolveAssetFullPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            string normalizedPath = NormalizeAssetPath(assetPath);
            if (normalizedPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                UnityEditor.PackageManager.PackageInfo package =
                    UnityEditor.PackageManager.PackageInfo.FindForAssetPath(normalizedPath);
                if (package == null)
                    return null;

                string relativePath = normalizedPath
                    .Substring(package.assetPath.Length)
                    .TrimStart('/');
                return Path.GetFullPath(Path.Combine(package.resolvedPath, relativePath));
            }

            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            return string.IsNullOrEmpty(projectRoot)
                ? null
                : Path.GetFullPath(Path.Combine(projectRoot, normalizedPath));
        }
    }
}

#endif
