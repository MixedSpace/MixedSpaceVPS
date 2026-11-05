using System;
using System.IO;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 每次应用启动或编辑器播放会话使用独立文件命名空间，包括禁用域重载时。文件由写入者延迟创建，重启定位不拆分运行会话。
    /// One file namespace per application launch (or Editor Play session, even without domain reload).
    /// Files are created lazily by writers; restarting localization does not split a running session.
    /// </summary>
    public static class MixedSpacePoseLogFiles
    {
        private static string s_RunId = CreateRunId();

        private static string CreateRunId() =>
            DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture)
            + "_" + Guid.NewGuid().ToString("N");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void BeginRun() => s_RunId = CreateRunId();

        /// <summary>
        /// 保留原扩展名，添加共享 UTC 时间戳和唯一运行 ID。
        /// Keep the original extension and add a shared UTC timestamp and unique run ID.
        /// </summary>
        public static string GetPath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
                throw new ArgumentException("A log file basename is required.", nameof(fileName));
            return Path.Combine(Application.persistentDataPath,
                Path.GetFileNameWithoutExtension(fileName) + "_" + s_RunId + Path.GetExtension(fileName));
        }
    }

    /// <summary>
    /// 每次成功提交将原生融合输入及采集位姿写为一行 JSON，保留字段名、时间单位和追加方式以便离线比较；World 输出矩阵及 Unity 位姿另存，日志失败不打断定位。
    /// Writes native fusion input and its capture pose as one JSON line per successful submission.
    /// Preserves field names, timestamp units and append behavior for offline comparison.
    /// Also records World output matrices and their extracted Unity pose in a separate file.
    /// Logging failures are reported without interrupting localization.
    /// </summary>
    internal static class MixedSpaceLocalizationJournal
    {
        private const string PoseFusionLogFileName = "pose_fusion_localizations.json";

        // Keep a numeric, session-local correlation key without narrowing EntityId to int.
        private static long GetWorldLogId(UnityEngine.Object world)
        {
#if UNITY_6000_6_OR_NEWER
            return unchecked((long)world.GetEntityId().GetRawData());
#else
            return world.GetInstanceID();
#endif
        }

        /// <summary>
        /// 将目标世界矩阵、融合输出与场景实际矩阵关联到同一定位序号。
        /// 返回码保留原值；未取得原生接口契约前不推断是否接受观测。
        /// Correlates target world, fusion output and actual scene matrices with one localization
        /// sequence. Preserve raw return codes; do not infer observation acceptance without the native contract.
        /// </summary>
        internal static void AppendAlignmentDiagnostic(MixedSpaceWorldRoot world, long sequence,
            MixedSpaceAlignmentSolution observation, string stage, long timestamp, bool tracking,
            Matrix4x4? output, string[] operations, int[] returnCodes)
        {
            try
            {
                var entry = new AlignmentDiagnostic
                {
                    stage = stage, observation_sequence = sequence,
                    world_instance_id = GetWorldLogId(world), world_name = world.name,
                    map_id = observation?.MapKey ?? 0, confidence = observation?.Confidence ?? 0,
                    observation_timestamp_ns = observation?.TimestampNanoseconds ?? 0,
                    sample_timestamp_ns = timestamp, tracking = tracking,
                    host_realtime_seconds = Time.realtimeSinceStartup,
                    target_world_matrix = observation == null ? null : MatrixValues(observation.WorldPose),
                    capture_tracking_matrix = observation == null ? null : MatrixValues(observation.TrackerPose),
                    localized_map_matrix = observation == null ? null : MatrixValues(observation.MapPose),
                    raw_localized_position = observation?.LocalizedPosition ?? Vector3.zero,
                    raw_localized_rotation = observation?.LocalizedRotation ?? Quaternion.identity,
                    camera_orientation = observation?.CameraOrientation ?? Quaternion.identity,
                    map_translation = observation?.Placement.Translation ?? Vector3.zero,
                    map_orientation = observation?.Placement.Orientation ?? Quaternion.identity,
                    map_scale = observation?.Placement.Scale ?? Vector3.one,
                    output_matrix = output.HasValue ? MatrixValues(output.Value) : null,
                    output_valid = output.HasValue && output.Value.ValidTRS(),
                    output_usable = output.HasValue && MixedSpaceWorldRoot.TryPreparePose(output.Value, out _),
                    actual_world_matrix = MatrixValues(world.CurrentPose),
                    operations = operations, return_codes = returnCodes
                };
                File.AppendAllText(MixedSpacePoseLogFiles.GetPath("alignment_diagnostics.json"),
                    JsonUtility.ToJson(entry) + Environment.NewLine);
            }
            catch (Exception e)
            {
                MixedSpaceLogger.LogError("Alignment diagnostic write failed (" + e.GetType().Name + ").");
            }
        }

        private static float[] MatrixValues(Matrix4x4 matrix)
        {
            var values = new float[16];
            for (int i = 0; i < values.Length; ++i) values[i] = matrix[i];
            return values;
        }

        [Serializable]
        private sealed class AlignmentDiagnostic
        {
            public string stage;
            public long observation_sequence;
            public long world_instance_id;
            public string world_name;
            public int map_id;
            public int confidence;
            public long observation_timestamp_ns;
            public long sample_timestamp_ns;
            public float host_realtime_seconds;
            public bool tracking;
            public float[] target_world_matrix;
            public float[] capture_tracking_matrix;
            public float[] localized_map_matrix;
            public Vector3 raw_localized_position;
            public Quaternion raw_localized_rotation;
            public Quaternion camera_orientation;
            public Vector3 map_translation;
            public Quaternion map_orientation;
            public Vector3 map_scale;
            public float[] output_matrix;
            public bool output_valid;
            // output_valid 保留原始矩阵校验结果；此字段记录容差规范化后能否应用。
            // output_valid preserves the raw validation result; this field records usability after tolerance-based
            // normalization.
            public bool output_usable;
            public float[] actual_world_matrix;
            public string[] operations;
            public int[] return_codes;
        }

        /// <summary>
        /// 记录 ApplyPose 接收的 Unity 坐标矩阵，采用列优先、位置 XYZ、旋转 XYZW；无效矩阵记录 applied=false。
        /// Records the Unity-coordinate ApplyPose matrix in column-major order with XYZ position and XYZW rotation; invalid matrices use applied=false.
        /// </summary>
        internal static void AppendWorldPoseLog(MixedSpaceWorldRoot world, Matrix4x4 pose,
            bool applied, Vector3 position, Quaternion rotation, long timestampNanoseconds,
            MixedSpacePlatformPoseSample? trackingSample = null, int? fusionReturnCode = null)
        {
            try
            {
                var matrix = new float[16];
                for (int i = 0; i < matrix.Length; i++) matrix[i] = pose[i];
                var entry = new WorldPoseLogEntry
                {
                    timestamp_ms = timestampNanoseconds / 1000000,
                    timestamp = timestampNanoseconds,
                    world_instance_id = GetWorldLogId(world),
                    world_name = world.name,
                    matrix_column_major = matrix,
                    applied = applied,
                    position = applied ? new[] { position.x, position.y, position.z } : null,
                    rotation = applied ? new[] { rotation.x, rotation.y, rotation.z, rotation.w } : null,
                    tracking = trackingSample?.IsTracking ?? false,
                    tracking_position = trackingSample.HasValue ? new[]
                        { trackingSample.Value.Position.x, trackingSample.Value.Position.y, trackingSample.Value.Position.z } : null,
                    tracking_rotation = trackingSample.HasValue ? new[]
                        { trackingSample.Value.Rotation.x, trackingSample.Value.Rotation.y,
                          trackingSample.Value.Rotation.z, trackingSample.Value.Rotation.w } : null,
                    fusion_return_code = fusionReturnCode ?? 0
                };
                File.AppendAllText(MixedSpacePoseLogFiles.GetPath("world_applied_poses.json"),
                    JsonUtility.ToJson(entry) + Environment.NewLine);
            }
            catch (Exception exception)
            {
                MixedSpaceLogger.LogError("World pose log write failed (" + exception.GetType().Name + ").");
            }
        }

        [Serializable]
        private sealed class WorldPoseLogEntry
        {
            public long timestamp_ms;
            public long timestamp;
            public long world_instance_id;
            public string world_name;
            public float[] matrix_column_major;
            public bool applied;
            public float[] position;
            public float[] rotation;
            public bool tracking;
            public float[] tracking_position;
            public float[] tracking_rotation;
            public int fusion_return_code;
        }

        internal static void AppendLocalizationLog(
            MixedSpaceAlignmentSolution solution,
            MixedSpacePoseFusionLocalization localization)
        {
            try
            {
                var entry = new PoseFusionLogEntry
                {
                    timestamp_ms = solution.TimestampNanoseconds / 1000000,
                    capturePose = new CapturePoseLogData
                    {
                        position = new[]
                        {
                            solution.CapturePosition.x,
                            solution.CapturePosition.y,
                            solution.CapturePosition.z
                        },
                        rotation = new[]
                        {
                            solution.CaptureRotation.x,
                            solution.CaptureRotation.y,
                            solution.CaptureRotation.z,
                            solution.CaptureRotation.w
                        },
                        timestamp = solution.TimestampNanoseconds
                    },
                    poseFusionLocInfo = new PoseFusionLocalizationLogData
                    {
                        position = localization.Position,
                        rotation = localization.Rotation,
                        confidence = localization.Confidence,
                        slam_pose = localization.TrackingPose,
                        timestamp = localization.TimestampNanoseconds
                    }
                };

                string path = MixedSpacePoseLogFiles.GetPath(PoseFusionLogFileName);
                File.AppendAllText(path, JsonUtility.ToJson(entry) + Environment.NewLine);
            }
            catch (Exception exception)
            {
                MixedSpaceLogger.LogError(
                    $"Failed to append pose-fusion localization log: {exception.Message}");
            }
        }

        [Serializable]
        private sealed class PoseFusionLocalizationLogData
        {
            public float[] position;
            public float[] rotation;
            public int confidence;
            public float[] slam_pose;
            public long timestamp;
        }

        [Serializable]
        private sealed class CapturePoseLogData
        {
            public float[] position;
            public float[] rotation;
            public long timestamp;
        }

        [Serializable]
        private sealed class PoseFusionLogEntry
        {
            public long timestamp_ms;
            public CapturePoseLogData capturePose;
            public PoseFusionLocalizationLogData poseFusionLocInfo;
        }
    }
}
