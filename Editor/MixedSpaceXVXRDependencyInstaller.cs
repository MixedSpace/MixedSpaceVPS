#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace MixedSpace.Editor
{
    /// <summary>
    /// 导入随包依赖快照，不覆盖已经安装的资源。
    /// Imports the bundled dependency snapshot without overwriting installed resources.
    /// </summary>
    internal static class MixedSpaceXVXRDependencyInstaller
    {
        internal const string PackageName = "com.ningfeng.mixedspace";
        private const string ArchiveName = "XVXRDependencies.zip";

        internal enum DependencyGroup { MRTK, TextMeshPro, XVisio }

        private static DependencyGroup GroupFor(string path)
        {
            if (path.StartsWith("Assets/MRTK/", StringComparison.Ordinal)) return DependencyGroup.MRTK;
            if (path.StartsWith("Assets/TextMesh Pro/", StringComparison.Ordinal)) return DependencyGroup.TextMeshPro;
            return DependencyGroup.XVisio;
        }

        internal static List<DependencyGroup> MissingGroups()
        {
            Manifest manifest = ReadManifest();
            string project = Directory.GetParent(Application.dataPath).FullName;
            var installed = IndexGuids();
            var missing = new HashSet<DependencyGroup>();
            foreach (FileRecord file in manifest.files)
                if (!installed.TryGetValue(file.guid, out string path) ||
                    (path.StartsWith("Assets/", StringComparison.Ordinal) && !File.Exists(Path.Combine(project, path))))
                    missing.Add(GroupFor(file.path));
            var groups = new List<DependencyGroup>();
            foreach (DependencyGroup group in new[] { DependencyGroup.MRTK, DependencyGroup.TextMeshPro, DependencyGroup.XVisio })
                if (missing.Contains(group)) groups.Add(group);
            return groups;
        }

        // 这些字段由 JsonUtility 从随包清单填充。
        // These fields are populated by JsonUtility from the bundled manifest.
#pragma warning disable CS0649
        [Serializable] internal sealed class FileRecord
        {
            public string path;
            public string guid;
            public string sha256;
            public string metaSha256;
        }

        [Serializable] internal sealed class DirectoryRecord
        {
            public string path;
            public string guid;
            public string metaSha256;
        }

        [Serializable] internal sealed class GuidRecord
        {
            public string guid;
            public string path;
        }

        [Serializable] internal sealed class Manifest
        {
            public int schema;
            public string sourceUnity;
            public string mrtkVersion;
            public string tmpVersion;
            public FileRecord[] files;
            public DirectoryRecord[] directories;
            public GuidRecord[] requiredGuids;
        }
#pragma warning restore CS0649

        internal static string PackageRoot()
        {
            foreach (UnityEditor.PackageManager.PackageInfo package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
                if (package.name == PackageName) return package.resolvedPath;
            throw new InvalidOperationException("MixedSpace package is not registered in Package Manager.");
        }

        internal static Manifest ReadManifest()
        {
            string file = Path.Combine(PackageRoot(), "Dependencies~", "XVXRDependencies.json");
            Manifest manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(file));
            if (manifest == null || manifest.schema != 1 || manifest.files == null || manifest.directories == null || manifest.requiredGuids == null)
                throw new InvalidDataException("Invalid XVXR dependency manifest.");
            return manifest;
        }

        internal static int ImportMissing() => ImportMissing(null);

        internal static int ImportMissing(DependencyGroup? group)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlaying)
                throw new InvalidOperationException("Wait for Unity import/compilation to finish and exit Play Mode before importing dependencies.");
            Manifest manifest = ReadManifest();
            if (group.HasValue && !Enum.IsDefined(typeof(DependencyGroup), group.Value))
                throw new ArgumentOutOfRangeException(nameof(group));
            if (group == DependencyGroup.XVisio && MissingGroups().Contains(DependencyGroup.MRTK))
                throw new InvalidOperationException("请先导入 MRTK。");
            var selected = new List<FileRecord>();
            var selectedDirectories = new HashSet<string>(StringComparer.Ordinal);
            foreach (FileRecord file in manifest.files)
                if (!group.HasValue || GroupFor(file.path) == group.Value)
                {
                    selected.Add(file);
                    string parent = file.path.Substring(0, file.path.LastIndexOf('/'));
                    while (parent.StartsWith("Assets/", StringComparison.Ordinal))
                    {
                        selectedDirectories.Add(parent);
                        parent = parent.Substring(0, parent.LastIndexOf('/'));
                    }
                }
            string project = Directory.GetParent(Application.dataPath).FullName;
            string archiveFile = Path.Combine(PackageRoot(), "Dependencies~", ArchiveName);
            var writes = new List<KeyValuePair<string, byte[]>>();
            var directories = new List<string>();
            var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var guidIndex = IndexGuids();
            var pluginIndex = IndexPlugins();
            var createdFiles = new List<string>();
            var createdDirectories = new List<string>();
            int resources = 0;

            using (var stream = File.OpenRead(archiveFile))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    ValidatePath(project, entry.FullName);
                    if (entries.ContainsKey(entry.FullName)) throw new InvalidDataException("Duplicate ZIP entry: " + entry.FullName);
                    entries.Add(entry.FullName, entry);
                }
                foreach (DirectoryRecord record in manifest.directories)
                {
                    ValidateGuid(record.guid);
                    string target = ValidatePath(project, record.path);
                    byte[] metadata = ReadVerified(entries, record.path + ".meta", record.metaSha256);
                    if (!knownPaths.Add(record.path + ".meta")) throw new InvalidDataException("Duplicate dependency path: " + record.path);
                    VerifyMetadata(metadata, record.guid, record.path);
                    if (group.HasValue && !selectedDirectories.Contains(record.path)) continue;
                    if (guidIndex.ContainsKey(record.guid)) continue;
                    if (File.Exists(target)) throw new InvalidOperationException("A file occupies a dependency folder: " + record.path);
                    string meta = target + ".meta";
                    if (File.Exists(meta))
                    {
                        // 已有文件夹是容器，应保留项目自己的 GUID；资源引用 GUID 在下方单独验证。
                        // Existing folders are containers; preserve their project-specific GUID. Referenced resource GUIDs are checked
                        // separately below.
                        continue;
                    }
                    directories.Add(target);
                    writes.Add(new KeyValuePair<string, byte[]>(meta, metadata));
                }
                foreach (FileRecord record in manifest.files)
                {
                    ValidateGuid(record.guid);
                    string target = ValidatePath(project, record.path);
                    byte[] content = ReadVerified(entries, record.path, record.sha256);
                    byte[] metadata = ReadVerified(entries, record.path + ".meta", record.metaSha256);
                    if (!knownPaths.Add(record.path) || !knownPaths.Add(record.path + ".meta")) throw new InvalidDataException("Duplicate dependency path: " + record.path);
                    VerifyMetadata(metadata, record.guid, record.path);
                    if (group.HasValue && GroupFor(record.path) != group.Value) continue;
                    if (guidIndex.TryGetValue(record.guid, out string installed))
                    {
                        if (Directory.Exists(Path.Combine(project, installed))) throw new InvalidOperationException("Expected a resource, found a folder: " + installed);
                        continue;
                    }
                    if (File.Exists(target) || File.Exists(target + ".meta") || Directory.Exists(target))
                    {
                        if (File.Exists(target) && File.Exists(target + ".meta") && GuidFromFile(target + ".meta") == record.guid) continue;
                        throw new InvalidOperationException("Dependency path/GUID conflict: " + record.path);
                    }
                    string extension = Path.GetExtension(record.path).ToLowerInvariant();
                    if ((extension == ".aar" || extension == ".so" || extension == ".dll") && pluginIndex.TryGetValue(Path.GetFileName(record.path), out string conflict))
                        throw new InvalidOperationException("A plugin with this filename is already installed: " + conflict);
                    writes.Add(new KeyValuePair<string, byte[]>(target, content));
                    writes.Add(new KeyValuePair<string, byte[]>(target + ".meta", metadata));
                    resources++;
                }
                if (entries.Count != knownPaths.Count) throw new InvalidDataException("The dependency ZIP contains entries not covered by its manifest.");
            }
            CheckTypeConflicts(new Manifest { files = selected.ToArray() }, guidIndex);
            if (writes.Count == 0) return 0;
            if (EditorUtility.DisplayCancelableProgressBar("XVXR dependencies", "Validated package. Importing missing resources...", 0f))
            {
                EditorUtility.ClearProgressBar();
                return 0;
            }
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string directory in directories) CreateDirectory(directory, project, createdDirectories);
                for (int i = 0; i < writes.Count; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("XVXR dependencies", Path.GetFileName(writes[i].Key), (float)i / writes.Count))
                        throw new OperationCanceledException("Dependency import cancelled; newly created files were removed.");
                    CreateDirectory(Path.GetDirectoryName(writes[i].Key), project, createdDirectories);
                    // 使用 CreateNew，避免覆盖期间由其他导入器或编辑器创建的文件。
                    // CreateNew prevents an intervening import/editor from being overwritten.
                    using (var file = new FileStream(writes[i].Key, FileMode.CreateNew, FileAccess.Write))
                    {
                        createdFiles.Add(writes[i].Key);
                        file.Write(writes[i].Value, 0, writes[i].Value.Length);
                    }
                }
            }
            catch
            {
                for (int i = createdFiles.Count - 1; i >= 0; i--) File.Delete(createdFiles[i]);
                for (int i = createdDirectories.Count - 1; i >= 0; i--)
                    if (Directory.Exists(createdDirectories[i]) && Directory.GetFileSystemEntries(createdDirectories[i]).Length == 0) Directory.Delete(createdDirectories[i]);
                throw;
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }
            return resources + directories.Count;
        }

        private static Dictionary<string, string> IndexGuids()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string path in AssetDatabase.GetAllAssetPaths())
            {
                string guid = AssetDatabase.AssetPathToGUID(path);
                if (!string.IsNullOrEmpty(guid) && (!path.StartsWith("Assets/", StringComparison.Ordinal) || File.Exists(path) || Directory.Exists(path))) result[guid] = path;
            }
            return result;
        }

        private static Dictionary<string, string> IndexPlugins()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (PluginImporter importer in PluginImporter.GetAllImporters()) result[Path.GetFileName(importer.assetPath)] = importer.assetPath;
            return result;
        }

        private static void CheckTypeConflicts(Manifest manifest, Dictionary<string, string> installed)
        {
            foreach (FileRecord record in manifest.files)
            {
                if (Path.GetExtension(record.path) != ".cs" || installed.ContainsKey(record.guid)) continue;
                string name = Path.GetFileNameWithoutExtension(record.path);
                if (record.path.StartsWith("Assets/XvXR/", StringComparison.Ordinal) || name == "API" || name == "SixDofControl" || name == "CreatMeshControl" || name == "NowXslamSurface")
                {
                    foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type type = assembly.GetType(name, false) ?? assembly.GetType("XvXR.Engine." + name, false);
                        if (type != null && !IsDeletedSourceType(type)) throw new InvalidOperationException("A vendor type already exists without its matching resource GUID: " + type.FullName + " in " + type.Assembly.GetName().Name);
                    }
                }
                foreach (string scriptGuid in AssetDatabase.FindAssets(name + " t:MonoScript", new[] { "Assets" }))
                {
                    string other = AssetDatabase.GUIDToAssetPath(scriptGuid);
                    if (File.Exists(other) && Path.GetFileNameWithoutExtension(other) == name)
                        throw new InvalidOperationException("A script with the same name is already installed: " + other);
                }
            }
            foreach (FileRecord record in manifest.files)
            {
                if (Path.GetExtension(record.path) != ".asmdef" || installed.ContainsKey(record.guid)) continue;
                string name = Path.GetFileName(record.path);
                foreach (string path in AssetDatabase.GetAllAssetPaths())
                    if (Path.GetFileName(path) == name) throw new InvalidOperationException("An assembly definition is already installed: " + path);
            }
        }

        private static bool IsDeletedSourceType(Type type)
        {
            // 编译失败时可能仍加载旧 Assembly-CSharp；DLL、自定义程序集及保留源文件中的同名类型仍算冲突。
            // A failed compilation can leave the previous Assembly-CSharp loaded. DLL/custom-assembly types and declarations
            // in surviving source files remain conflicts.
            if (type.Assembly.GetName().Name != "Assembly-CSharp") return false;
            string declaration = @"\b(?:class|struct|interface|enum|record)\s+" + Regex.Escape(type.Name) + @"\b";
            foreach (UnityEditor.Compilation.Assembly assembly in CompilationPipeline.GetAssemblies(AssembliesType.Editor))
            {
                if (assembly.name != "Assembly-CSharp") continue;
                foreach (string source in assembly.sourceFiles)
                    if (File.Exists(source) && Regex.IsMatch(File.ReadAllText(source), declaration, RegexOptions.Multiline)) return false;
                return true;
            }
            return false;
        }

        private static string ValidatePath(string project, string relative)
        {
            if (string.IsNullOrEmpty(relative) || !relative.StartsWith("Assets/", StringComparison.Ordinal) ||
                relative.Contains("\\") || relative.Contains(":") || relative.EndsWith("/", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid dependency path: " + relative);
            foreach (string segment in relative.Split('/'))
                if (segment == ".." || segment == "." || segment.Length == 0) throw new InvalidDataException("Invalid dependency path: " + relative);
            string assets = Path.GetFullPath(Path.Combine(project, "Assets"));
            string root = assets + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(project, relative));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Dependency escapes Assets: " + relative);
            string existing = full;
            while (!string.IsNullOrEmpty(existing) && existing.Length >= assets.Length)
            {
                if ((File.Exists(existing) || Directory.Exists(existing)) && (File.GetAttributes(existing) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Dependency target uses a symbolic link/junction: " + relative);
                existing = Path.GetDirectoryName(existing);
            }
            return full;
        }

        private static void ValidateGuid(string value)
        {
            if (value == null || !Regex.IsMatch(value, "^[0-9a-f]{32}$")) throw new InvalidDataException("Invalid dependency GUID.");
        }

        private static void VerifyMetadata(byte[] content, string expected, string path)
        {
            string text = System.Text.Encoding.UTF8.GetString(content);
            Match match = Regex.Match(text, @"^guid: ([0-9a-f]{32})", RegexOptions.Multiline);
            if (!match.Success || match.Groups[1].Value != expected) throw new InvalidDataException("Dependency metadata mismatch: " + path);
        }

        private static string GuidFromFile(string file)
        {
            Match match = Regex.Match(File.ReadAllText(file), @"^guid: ([0-9a-f]{32})", RegexOptions.Multiline);
            return match.Success ? match.Groups[1].Value : "";
        }

        private static byte[] ReadVerified(Dictionary<string, ZipArchiveEntry> entries, string path, string expected)
        {
            if (!entries.TryGetValue(path, out ZipArchiveEntry entry) || entry.Length > 256L * 1024 * 1024)
                throw new InvalidDataException("Missing/oversized dependency entry: " + path);
            byte[] content;
            using (Stream stream = entry.Open())
            using (var buffer = new MemoryStream()) { stream.CopyTo(buffer); content = buffer.ToArray(); }
            using (SHA256 sha = SHA256.Create())
            {
                string actual = BitConverter.ToString(sha.ComputeHash(content)).Replace("-", "").ToLowerInvariant();
                if (actual != expected) throw new InvalidDataException("Dependency checksum mismatch: " + path);
            }
            return content;
        }

        private static void CreateDirectory(string path, string project, List<string> created)
        {
            if (Directory.Exists(path)) return;
            string assets = Path.Combine(project, "Assets");
            if (string.IsNullOrEmpty(path) || path.Length < assets.Length) throw new InvalidDataException("Invalid dependency directory.");
            CreateDirectory(Path.GetDirectoryName(path), project, created);
            Directory.CreateDirectory(path);
            created.Add(path);
        }
    }
}
#endif
