using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>Bounded, asynchronous copies of actual localization inputs. No camera lease survives Append.</summary>
    internal static class MixedSpaceCaptureImageJournal
    {
        private const int MaximumImages = 4;
        private const long MaximumBytes = 32L * 1024 * 1024;
        private static readonly object Gate = new object();
        private static readonly Queue<Item> Queue = new Queue<Item>();
        private static Task s_Worker;
        private static int s_Images, s_Dropped;
        private static long s_Bytes, s_Sequence;

        [Serializable] private sealed class Entry
        {
            public int schema_version = 1;
            public string session_id, frame_id, status, image_path, pixel_format;
            public long timestamp_ns, timestamp_ms;
            public int width, height, channels, stride_bytes, byte_length;
            public float[] intrinsics, tracking_matrix_column_major;
            public double[] lens_distortion;
            public Quaternion display_rotation;
            public bool has_native_image_rotation_angle;
            public int native_image_rotation_angle;
        }
        private sealed class Item
        {
            public string Manifest, Image, Json, FailedJson;
            public byte[] Pixels;
            public int ReservedBytes;
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
        internal static void Append(IMixedSpaceCameraCapture capture)
        {
            int reserved = 0;
            try
            {
                if (capture == null) return;
                var p = capture.Profile;
                int size = checked(p.PixelWidth * p.PixelHeight * p.ChannelCount);
                if (size <= 0) return;
                string manifest = MixedSpacePoseLogFiles.GetPath("localization_images.json");
                string session = Path.GetFileNameWithoutExtension(manifest).Substring("localization_images_".Length);
                string id = p.CapturedAtNanoseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" +
                    System.Threading.Interlocked.Increment(ref s_Sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
                string relative = "RawData_" + session + "/capture_" + id + ".bin";
                Matrix4x4 tracking = Matrix4x4.TRS(p.TrackerPosition, p.TrackerRotation, Vector3.one);
                var matrix = new float[16]; for (int i = 0; i < 16; i++) matrix[i] = tracking[i];
                var entry = new Entry { session_id = session, frame_id = id, timestamp_ns = p.CapturedAtNanoseconds,
                    timestamp_ms = p.CapturedAtNanoseconds / 1000000, width = p.PixelWidth, height = p.PixelHeight,
                    channels = p.ChannelCount, stride_bytes = checked(p.PixelWidth * p.ChannelCount), byte_length = size,
                    pixel_format = p.PixelFormat.ToString(), image_path = relative, tracking_matrix_column_major = matrix,
                    intrinsics = new[] { p.CameraIntrinsics.x, p.CameraIntrinsics.y, p.CameraIntrinsics.z, p.CameraIntrinsics.w },
                    lens_distortion = p.LensDistortion.ToArray(), display_rotation = p.DisplayRotation,
                    has_native_image_rotation_angle = p.NativeImageRotationAngle.HasValue,
                    native_image_rotation_angle = p.NativeImageRotationAngle ?? 0 };
                lock (Gate)
                {
                    if (s_Images >= MaximumImages || s_Bytes + size > MaximumBytes)
                    {
                        entry.status = "dropped_queue_full";
                        if (Queue.Count < 64) Queue.Enqueue(new Item { Manifest = manifest, Json = JsonUtility.ToJson(entry) });
                        ++s_Dropped;
                        if ((s_Dropped & (s_Dropped - 1)) == 0)
                            MixedSpaceLogger.LogWarning("Localization image journal queue full; dropped images=" + s_Dropped + ". Pose/localization processing continues.");
                        StartWorker(); return;
                    }
                    ++s_Images; s_Bytes += size; reserved = size;
                }
                byte[] pixels;
                using (var lease = capture.LeasePixels()) pixels = lease.CopyPixels();
                if (pixels == null || pixels.Length != size) throw new InvalidDataException("Localization image is not tightly packed as declared.");
                entry.status = "write_failed"; string failed = JsonUtility.ToJson(entry);
                entry.status = "saved";
                var item = new Item { Manifest = manifest, Image = Path.Combine(Path.GetDirectoryName(manifest), relative),
                    Json = JsonUtility.ToJson(entry), FailedJson = failed, Pixels = pixels, ReservedBytes = size };
                lock (Gate) { Queue.Enqueue(item); reserved = 0; StartWorker(); }
            }
            catch (Exception error)
            {
                if (reserved != 0) lock (Gate) { --s_Images; s_Bytes -= reserved; }
                MixedSpaceLogger.LogError("Localization image journal copy failed (" + error.GetType().Name + ").");
            }
        }
        private static void StartWorker()
        {
            // Gate is held by the caller; one consumer preserves image/manifest ordering.
            if (s_Worker == null) s_Worker = Task.Run((Action)Drain);
        }
        private static void Drain()
        {
            while (true)
            {
                Item item;
                lock (Gate)
                {
                    if (Queue.Count == 0) { s_Worker = null; return; }
                    item = Queue.Dequeue();
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.Manifest));
                    if (item.Pixels != null)
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(item.Image));
                            File.WriteAllBytes(item.Image, item.Pixels);
                        }
                        catch
                        {
                            File.AppendAllText(item.Manifest, item.FailedJson + Environment.NewLine);
                            throw;
                        }
                    }
                    File.AppendAllText(item.Manifest, item.Json + Environment.NewLine);
                }
                catch (Exception error)
                {
                    MixedSpaceLogger.LogError("Localization image journal write failed (" + error.GetType().Name + ").");
                }
                finally
                {
                    if (item.ReservedBytes != 0) lock (Gate) { --s_Images; s_Bytes -= item.ReservedBytes; }
                }
            }
        }
        internal static void Flush()
        {
            Task worker; lock (Gate) worker = s_Worker;
            if (worker != null && !worker.Wait(5000))
                MixedSpaceLogger.LogWarning("Localization image journal flush timed out; some queued images may be incomplete.");
        }
    }
}
