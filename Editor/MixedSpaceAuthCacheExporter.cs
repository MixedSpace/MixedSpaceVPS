#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;

namespace MixedSpace.EditorTools
{
    public static class MixedSpaceAuthCacheExporter
    {
        public static string FindGeneratedCacheFile(string developerToken)
        {
            string name = MixedSpaceAuthCache.GetCacheFileName(developerToken);
            if (name == null) return null;
            string directory = MixedSpaceAuthCache.GetCacheDirectory();
            string[] candidates = {
                directory == null ? null : Path.Combine(directory, name),
                Path.Combine(Directory.GetCurrentDirectory(), name),
                Path.Combine(Path.GetDirectoryName(Application.dataPath), name),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XRLoc", name),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vps.njyjxr", name),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XRLoc", name)
            };
            foreach (string candidate in candidates)
                if (candidate != null && File.Exists(candidate) && new FileInfo(candidate).Length > 0) return candidate;
            return null;
        }

        public static string ExportToStreamingAssets(string developerToken)
        {
            string source = FindGeneratedCacheFile(developerToken);
            if (source == null) throw new FileNotFoundException("No native-issued authorization cache was found.");
            string directory = Path.Combine(Application.dataPath, "StreamingAssets", MixedSpaceAuthCache.StreamingSubdirectory);
            Directory.CreateDirectory(directory);
            string destination = Path.Combine(directory, MixedSpaceAuthCache.GetCacheFileName(developerToken));
            File.Copy(source, destination, true);
            return destination;
        }

    }
}
#endif
