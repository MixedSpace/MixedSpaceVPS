using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace MixedSpace
{
    /// <summary>A sanitized, bounded runtime diagnostic record. Repeated consecutive messages share one row.</summary>
    public sealed class MixedSpaceRuntimeLogEntry
    {
        public long Id { get; }
        public DateTime Time { get; }
        public LogType Type { get; }
        public string Category { get; }
        public string Message { get; }
        public string StackTrace { get; }
        public int Count { get; }
        public bool IsError => Type == LogType.Error || Type == LogType.Exception || Type == LogType.Assert;

        internal MixedSpaceRuntimeLogEntry(long id, DateTime time, LogType type, string category,
            string message, string stackTrace, int count = 1)
        {
            Id = id; Time = time; Type = type; Category = category;
            Message = message; StackTrace = stackTrace; Count = count;
        }
    }

    /// <summary>Thread-safe diagnostic history, independent of Console verbosity. Contains no Unity objects.</summary>
    public static class MixedSpaceRuntimeLog
    {
        public const int Capacity = 200;
        private static readonly object Gate = new object();
        private static readonly List<MixedSpaceRuntimeLogEntry> Entries = new List<MixedSpaceRuntimeLogEntry>();
        private static long s_NextId;
        private static int s_Revision;
        [ThreadStatic] internal static bool ForwardingToConsole;
        public static int Revision => Volatile.Read(ref s_Revision);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Initialize()
        {
            Clear();
            Application.logMessageReceivedThreaded -= ReceiveUnityLog;
            Application.logMessageReceivedThreaded += ReceiveUnityLog;
        }

        public static MixedSpaceRuntimeLogEntry[] Snapshot()
        {
            lock (Gate) return Entries.ToArray();
        }

        public static void Clear()
        {
            lock (Gate) { Entries.Clear(); Interlocked.Increment(ref s_Revision); }
        }

        /// <summary>Application errors can use the same popup and history as built-in SDK errors.</summary>
        public static void ReportError(string code, string message, string details = null)
        {
            MixedSpaceLogger.LogError("[" + code + "] " + message +
                (string.IsNullOrEmpty(details) ? string.Empty : "\n" + details));
        }

        internal static void ReceiveSdk(MixedSpaceLogRecord record)
        {
            LogType type = record.Severity == MixedSpaceLogSeverity.Error ? LogType.Error :
                record.Severity == MixedSpaceLogSeverity.Warning ? LogType.Warning : LogType.Log;
            Add(type, Category(record.Source + " " + record.Message), MixedSpaceLogText.Format(record), string.Empty);
        }

        private static void ReceiveUnityLog(string message, string stackTrace, LogType type)
        {
            // SDK messages are already recorded before Console filtering. Capture other Unity warnings/errors,
            // including application exceptions, without duplicating SDK Console forwarding.
            if (ForwardingToConsole || type == LogType.Log) return;
            Add(type, "Unity", message, stackTrace);
        }

        private static string Category(string text)
        {
            string lower = text.ToLowerInvariant();
            if (lower.Contains("token") || lower.Contains("license") || lower.Contains("validation") || lower.Contains("authorization")) return "授权";
            if (lower.Contains("fusion") || lower.Contains("worldchannel") || lower.Contains("pose isolation")) return "位姿融合";
            if (lower.Contains("localiz") || lower.Contains("xrloc-miss") || lower.Contains("xrloc-config") || lower.Contains("solve")) return "定位";
            if (lower.Contains("map")) return "地图";
            return "运行时";
        }

        private static string SafeText(string value, int limit)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            string safe = MixedSpaceLogText.Redact(value);
            return safe.Length <= limit ? safe : safe.Substring(0, limit) + "\n[truncated]";
        }

        private static void Add(LogType type, string category, string message, string stackTrace)
        {
            message = SafeText(message, 4096);
            stackTrace = SafeText(stackTrace, 8192);
            lock (Gate)
            {
                DateTime now = DateTime.Now;
                var last = Entries.Count > 0 ? Entries[Entries.Count - 1] : null;
                if (last != null && last.Type == type && last.Category == category && last.Message == message && last.StackTrace == stackTrace)
                    Entries[Entries.Count - 1] = new MixedSpaceRuntimeLogEntry(last.Id, now, type, category, message, stackTrace, last.Count + 1);
                else
                {
                    if (Entries.Count == Capacity) Entries.RemoveAt(0);
                    Entries.Add(new MixedSpaceRuntimeLogEntry(++s_NextId, now, type, category, message, stackTrace));
                }
                Interlocked.Increment(ref s_Revision);
            }
        }
    }
}
