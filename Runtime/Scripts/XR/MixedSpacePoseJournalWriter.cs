using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>Writes serialized pose journal rows in bounded batches off the Unity thread.</summary>
    internal static class MixedSpacePoseJournalWriter
    {
        private const int Capacity = 2048;
        private const int BatchSize = 64;
        private static readonly object Gate = new object();
        private static readonly Queue<Line> Pending = new Queue<Line>();
        private static Task s_Worker;
        private static int s_Dropped;
        private static bool s_ReportedWriteFailure;

        private readonly struct Line
        {
            public readonly string Path;
            public readonly string Json;

            public Line(string path, string json)
            {
                Path = path;
                Json = json;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RegisterShutdown()
        {
            Application.quitting -= Flush;
            Application.quitting += Flush;
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void RegisterEditorShutdown()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Flush;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Flush;
            UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(UnityEditor.PlayModeStateChange state)
        {
            if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode) Flush();
        }
#endif

        // Path and JSON are frozen by the caller. The worker never reads a Unity object.
        internal static void Append(string path, string json)
        {
            lock (Gate)
            {
                if (Pending.Count >= Capacity)
                {
                    ++s_Dropped;
                    return;
                }
                Pending.Enqueue(new Line(path, json));
                if (s_Worker == null) s_Worker = Task.Run(DrainAsync);
            }
        }

        private static async Task DrainAsync()
        {
            // Coalesce sparse per-frame rows so writing every frame is unnecessary.
            await Task.Delay(50).ConfigureAwait(false);
            var lines = new List<Line>(BatchSize);
            var files = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
            while (true)
            {
                int dropped;
                lock (Gate)
                {
                    if (Pending.Count == 0 && s_Dropped == 0)
                    {
                        s_Worker = null;
                        return;
                    }
                    for (int i = 0; i < BatchSize && Pending.Count > 0; ++i)
                        lines.Add(Pending.Dequeue());
                    dropped = s_Dropped;
                    s_Dropped = 0;
                }

                foreach (Line line in lines)
                {
                    if (!files.TryGetValue(line.Path, out StringBuilder batch))
                        files.Add(line.Path, batch = new StringBuilder());
                    batch.AppendLine(line.Json);
                }

                foreach (KeyValuePair<string, StringBuilder> file in files)
                {
                    try { File.AppendAllText(file.Key, file.Value.ToString()); }
                    catch (Exception error)
                    {
                        if (!s_ReportedWriteFailure)
                        {
                            s_ReportedWriteFailure = true;
                            MixedSpaceLogger.LogWarning("Pose journal write failed (" + error.GetType().Name + ").");
                        }
                    }
                }
                if (dropped != 0)
                    MixedSpaceLogger.LogWarning($"Pose journal queue full; dropped diagnostic rows={dropped}.");
                lines.Clear();
                files.Clear();
            }
        }

        internal static void Flush()
        {
            Task worker;
            lock (Gate) worker = s_Worker;
            if (worker != null && !worker.Wait(2000))
                MixedSpaceLogger.LogWarning("Pose journal flush timed out; some queued rows may be incomplete.");
        }
    }
}
