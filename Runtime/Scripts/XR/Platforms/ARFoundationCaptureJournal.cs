using System;
using System.IO;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 可选采集诊断只消费已冻结的元数据，不重新采样位姿或持有图像内存；文件名、时钟单位和列优先矩阵布局保持日志兼容。
    /// Optional capture diagnostics consume only the already-frozen metadata.
    /// They cannot reacquire pose or hold image memory. File name, clock units and
    /// column-major matrix layout remain compatible with the existing journal.
    /// </summary>
    internal static class ARFoundationCaptureJournal
    {
        [Serializable] private sealed class PoseData
        {
            public float[] pose;
            public long timestamp;
            public int width;
            public int height;
        }
        [Serializable] private sealed class Entry
        {
            public long timestamp_ms;
            public PoseData capturePose;
        }

        public static void Append(MixedSpaceCaptureProfile profile)
        {
            try
            {
                File.AppendAllText(MixedSpacePoseLogFiles.GetPath("camera_capture_poses.json"),
                    Serialize(profile) + Environment.NewLine);
            }
            catch (Exception exception)
            {
                MixedSpaceLogger.LogError("Capture journal write failed (" + exception.GetType().Name + ").");
            }
        }

        internal static string Serialize(MixedSpaceCaptureProfile profile)
        {
            Matrix4x4 pose = Matrix4x4.TRS(profile.TrackerPosition, profile.TrackerRotation, Vector3.one);
            var entry = new Entry
            {
                timestamp_ms = profile.CapturedAtNanoseconds / 1000000,
                capturePose = new PoseData
                {
                    pose = new[] {
                        pose.m00, pose.m10, pose.m20, pose.m30,
                        pose.m01, pose.m11, pose.m21, pose.m31,
                        pose.m02, pose.m12, pose.m22, pose.m32,
                        pose.m03, pose.m13, pose.m23, pose.m33 },
                    timestamp = profile.CapturedAtNanoseconds,
                    width = profile.PixelWidth,
                    height = profile.PixelHeight
                }
            };
            return JsonUtility.ToJson(entry);
        }
    }
}
