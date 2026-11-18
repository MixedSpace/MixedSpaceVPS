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
    /// 可选平台适配器在主线程 LateUpdate 中记录跟踪、渲染相机及 World 的对照快照。
    /// Optional adapter diagnostic port. Call from main-thread LateUpdate only when pose logging is enabled.
    /// </summary>
    public static class MixedSpacePoseDiagnostics
    {
        public static void AppendTrackingSample(MixedSpacePlatformPoseSample sample, string trackingPoseSource = "platform") =>
            MixedSpaceLocalizationJournal.AppendTrackingDiagnostic(sample, trackingPoseSource);
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
        private const string TrackingStatusLogFileName = "tracking_status.json";

        internal static void AppendLiveTrackingStatus(MixedSpaceWorldRoot world, MixedSpacePlatformPoseSample sample)
        {
            try
            {
                AppendTrackingStatus(new LiveTrackingStatus
                {
                    stage = "live_pose",
                    world_instance_id = GetWorldLogId(world),
                    timestamp_ns = sample.TimestampNanoseconds,
                    timestamp_source = sample.TimestampSource.ToString(),
                    host_realtime_seconds = Time.realtimeSinceStartupAsDouble,
                    tracking = sample.IsTracking
                });
            }
            catch (Exception exception)
            {
                MixedSpaceLogger.LogWarning($"Failed to append live tracking status ({exception.GetType().Name}).");
            }
        }

        /// <summary>
        /// 采集轮询没有图像时，图像时间戳记为 0，仍记录跟踪状态和 Unity 启动后的秒数。
        /// When no image is available, log the tracking state and seconds since Unity started, with a zero image timestamp.
        /// </summary>
        internal static void AppendCaptureTrackingStatus(IMixedSpacePlatformFrame frame)
        {
            try
            {
                bool hasCapture = frame.HasCapture;
                AppendTrackingStatus(new CaptureTrackingStatus
                {
                    stage = "capture",
                    timestamp_ns = hasCapture ? frame.Capture.Profile.CapturedAtNanoseconds : 0,
                    host_realtime_seconds = Time.realtimeSinceStartupAsDouble,
                    platform_tracking_quality = frame.Tracking.Quality,
                    has_capture = hasCapture
                });
            }
            catch (Exception exception)
            {
                MixedSpaceLogger.LogWarning($"Failed to append capture tracking status ({exception.GetType().Name}).");
            }
        }

        private static void AppendTrackingStatus(object entry) =>
            MixedSpacePoseJournalWriter.Append(MixedSpacePoseLogFiles.GetPath(TrackingStatusLogFileName),
                JsonUtility.ToJson(entry));

        [Serializable]
        private sealed class LiveTrackingStatus
        {
            public string stage;
            public long world_instance_id;
            public long timestamp_ns;
            public string timestamp_source;
            public double host_realtime_seconds;
            public bool tracking;
        }

        [Serializable]
        private sealed class CaptureTrackingStatus
        {
            public string stage;
            public long timestamp_ns;
            public double host_realtime_seconds;
            public int platform_tracking_quality;
            public bool has_capture;
        }

        internal static string GetPositionPriorLogPath()
        {
            try
            {
                return MixedSpacePoseLogFiles.GetPath("xrloc_prior_localizations.json");
            }
            catch (Exception exception)
            {
                MixedSpaceLogger.LogWarning($"Could not prepare XRLoc position prior log ({exception.GetType().Name}).");
                return null;
            }
        }

        /// <summary>
        /// 用图像采集时间关联位置先验请求和原生返回状态，包含未命中的调用。
        /// Correlate position prior requests and native results, including misses, by the image capture time.
        /// </summary>
        internal static void AppendPositionPriorLog(
            string path,
            long timestampNanoseconds,
            int[] mapIds,
            int solver,
            int rotationAngle,
            float[] rotationPrior,
            MixedSpaceXrLocPosePrior prior,
            int status,
            MixedSpaceXrLocPose pose,
            long elapsedMs)
        {
            try
            {
                bool hasPose = status == (int)MixedSpaceXrLocStatus.Ok && pose.success != 0;
                var entry = new XrLocPositionPriorLogEntry
                {
                    timestamp_ns = timestampNanoseconds,
                    timestamp_ms = timestampNanoseconds / 1000000,
                    elapsed_ms = elapsedMs,
                    input = new XrLocPositionPriorInput
                    {
                        map_id = prior.mapId,
                        position = new[] { prior.x, prior.y, prior.z },
                        search_map_ids = mapIds,
                        solver = solver,
                        rotation_angle = rotationAngle,
                        rotation_prior_xyzw = rotationPrior
                    },
                    output = new XrLocPositionPriorOutput
                    {
                        status = status,
                        success = pose.success != 0,
                        map_id = pose.mapId,
                        confidence = pose.confidence,
                        position = hasPose ? new[] { pose.px, pose.py, pose.pz } : Array.Empty<double>(),
                        rotation_row_major = hasPose
                            ? new[]
                            {
                                pose.r00, pose.r01, pose.r02,
                                pose.r10, pose.r11, pose.r12,
                                pose.r20, pose.r21, pose.r22
                            }
                            : Array.Empty<double>()
                    }
                };

                string json = JsonUtility.ToJson(entry);
                MixedSpacePoseJournalWriter.Append(path, json);
            }
            catch (Exception exception)
            {
                MixedSpaceLogger.LogWarning($"Failed to append XRLoc position prior log ({exception.GetType().Name}).");
            }
        }

        [Serializable]
        private sealed class XrLocPositionPriorLogEntry
        {
            public long timestamp_ns;
            public long timestamp_ms;
            public long elapsed_ms;
            public XrLocPositionPriorInput input;
            public XrLocPositionPriorOutput output;
        }

        [Serializable]
        private sealed class XrLocPositionPriorInput
        {
            public int map_id;
            public double[] position;
            public int[] search_map_ids;
            public int solver;
            public int rotation_angle;
            public float[] rotation_prior_xyzw;
        }

        [Serializable]
        private sealed class XrLocPositionPriorOutput
        {
            public int status;
            public bool success;
            public int map_id;
            public int confidence;
            public double[] position;
            public double[] rotation_row_major;
        }
        // Keep a numeric, session-local correlation key without narrowing EntityId to int.
        private static long GetWorldLogId(UnityEngine.Object world)
        {
#if UNITY_6000_6_OR_NEWER
            return unchecked((long)world.GetEntityId().GetRawData());
#else
            return world.GetInstanceID();
#endif
        }

        internal static void AppendFusionCadence(MixedSpaceWorldRoot world, MixedSpaceTrackingTimestampSource source,
            float elapsedSeconds, int samples, int submissions, int outputs, int worldWrites)
        {
            try
            {
                string message = FormattableString.Invariant(
                    $"[POSE-CADENCE] world_instance_id={GetWorldLogId(world)} timestamp_source={source} window_seconds={elapsedSeconds:F3} samples={samples} submissions={submissions} valid_outputs={outputs} world_writes={worldWrites} sample_hz={samples / elapsedSeconds:F2} submission_hz={submissions / elapsedSeconds:F2}");
                MixedSpacePoseFusionLogFile.Initialize();
                MixedSpacePoseFusionLogFile.Append(2, message);
            }
            catch (Exception exception)
            {
                MixedSpaceLogger.LogError("Fusion cadence log write failed (" + exception.GetType().Name + ").");
            }
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
                MixedSpacePoseJournalWriter.Append(MixedSpacePoseLogFiles.GetPath("alignment_diagnostics.json"),
                    JsonUtility.ToJson(entry));
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

        /// <summary>
        /// 以 5 Hz 记录同一次 LateUpdate 中的平台跟踪、Unity 相机及其父级。
        /// LateUpdate 早于 XR 的 BeforeRender/预测修正，不能把单帧差值当作外参误差。
        /// Records physical tracking and Unity camera/parent poses at 5 Hz in LateUpdate.
        /// This precedes XR BeforeRender prediction; a single-frame delta is not an extrinsic error.
        /// </summary>
        internal static void AppendTrackingDiagnostic(MixedSpacePlatformPoseSample sample, string trackingPoseSource = "physical_camera")
        {
            try
            {
                Camera camera = Camera.main;
                Transform view = camera ? camera.transform : null;
                Transform parent = view ? view.parent : null;
                Matrix4x4 physics = Matrix4x4.TRS(sample.Position, sample.Rotation, Vector3.one);
                float[] relativeTracking = view && sample.IsTracking ?
                    MatrixValues(view.worldToLocalMatrix * physics) : null;
                MixedSpaceWorldRoot[] roots = UnityEngine.Object.FindObjectsOfType<MixedSpaceWorldRoot>();
                var worlds = new TrackingWorldDiagnostic[roots.Length];
                for (int i = 0; i < roots.Length; ++i)
                {
                    MixedSpaceWorldRoot world = roots[i];
                    Transform worldParent = world.transform.parent;
                    worlds[i] = new TrackingWorldDiagnostic
                    {
                        world_instance_id = GetWorldLogId(world), world_name = world.name,
                        reliable = world.IsPoseReliable,
                        world_matrix = MatrixValues(world.transform.localToWorldMatrix),
                        parent_world_matrix = worldParent ? MatrixValues(worldParent.localToWorldMatrix) : null
                    };
                }
                var entry = new TrackingDiagnostic
                {
                    schema_version = 1,
                    phase = "LateUpdate",
                    frame_count = Time.frameCount,
                    host_realtime_seconds = Time.realtimeSinceStartup,
                    timestamp_ns = sample.TimestampNanoseconds,
                    tracking = sample.IsTracking,
                    tracking_pose_source = trackingPoseSource,
                    tracking_timestamp_source = sample.TimestampSource.ToString(),
                    tracking_matrix_column_major = sample.IsTracking ? MatrixValues(physics) : null,
                    has_render_camera = view != null,
                    render_camera_name = camera ? camera.name : null,
                    render_camera_world_matrix = view ? MatrixValues(view.localToWorldMatrix) : null,
                    render_camera_local_matrix = view ? MatrixValues(Matrix4x4.TRS(
                        view.localPosition, view.localRotation, view.localScale)) : null,
                    render_parent_name = parent ? parent.name : null,
                    render_parent_world_matrix = parent ? MatrixValues(parent.localToWorldMatrix) : null,
                    render_root_world_matrix = view ? MatrixValues(view.root.localToWorldMatrix) : null,
                    tracking_in_render_matrix = relativeTracking,
                    physics_in_render_matrix = trackingPoseSource == "physical_camera" ? relativeTracking : null,
                    worlds = worlds
                };
                MixedSpacePoseJournalWriter.Append(MixedSpacePoseLogFiles.GetPath("tracking_render_poses.json"),
                    JsonUtility.ToJson(entry));
            }
            catch (Exception error)
            {
                MixedSpaceLogger.LogError("Tracking diagnostic write failed (" + error.GetType().Name + ").");
            }
        }

        [Serializable]
        private sealed class TrackingDiagnostic
        {
            public int schema_version;
            public string phase;
            public int frame_count;
            public float host_realtime_seconds;
            public long timestamp_ns;
            public bool tracking;
            public string tracking_pose_source;
            public string tracking_timestamp_source;
            public float[] tracking_matrix_column_major;
            public bool has_render_camera;
            public string render_camera_name;
            public float[] render_camera_world_matrix;
            public float[] render_camera_local_matrix;
            public string render_parent_name;
            public float[] render_parent_world_matrix;
            public float[] render_root_world_matrix;
            public float[] physics_in_render_matrix;
            public float[] tracking_in_render_matrix;
            public TrackingWorldDiagnostic[] worlds;
        }

        [Serializable]
        private sealed class TrackingWorldDiagnostic
        {
            public long world_instance_id;
            public string world_name;
            public bool reliable;
            public float[] world_matrix;
            public float[] parent_world_matrix;
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
        /// 记录融合输出及实际 Transform 写入状态。有效但相同的输出仍会记录。
        /// Records the fused pose and whether it wrote the scene transform, including valid unchanged outputs.
        /// </summary>
        internal static void AppendWorldPoseLog(MixedSpaceWorldRoot world, Matrix4x4 pose,
            bool applied, bool transformWritten, Vector3 position, Quaternion rotation, long timestampNanoseconds,
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
                    transform_written = transformWritten,
                    position = applied ? new[] { position.x, position.y, position.z } : null,
                    rotation = applied ? new[] { rotation.x, rotation.y, rotation.z, rotation.w } : null,
                    tracking = trackingSample?.IsTracking ?? false,
                    tracking_timestamp_source = (trackingSample?.TimestampSource ?? MixedSpaceTrackingTimestampSource.Unspecified).ToString(),
                    tracking_position = trackingSample.HasValue ? new[]
                        { trackingSample.Value.Position.x, trackingSample.Value.Position.y, trackingSample.Value.Position.z } : null,
                    tracking_rotation = trackingSample.HasValue ? new[]
                        { trackingSample.Value.Rotation.x, trackingSample.Value.Rotation.y,
                          trackingSample.Value.Rotation.z, trackingSample.Value.Rotation.w } : null,
                    fusion_return_code = fusionReturnCode ?? 0
                };
                MixedSpacePoseJournalWriter.Append(MixedSpacePoseLogFiles.GetPath("world_applied_poses.json"),
                    JsonUtility.ToJson(entry));
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
            public bool transform_written;
            public float[] position;
            public float[] rotation;
            public bool tracking;
            public string tracking_timestamp_source;
            public float[] tracking_position;
            public float[] tracking_rotation;
            public int fusion_return_code;
        }

        /// <summary>
        /// 记录传给 GetTransform 的原生列主序跟踪矩阵和纳秒时间戳，包含后续调用失败的输入。
        /// Record the native column-major tracking matrix and nanosecond timestamp before GetTransform, including inputs to calls that fail.
        /// </summary>
        internal static void AppendGetTransformInput(
            MixedSpaceWorldRoot world, long timestampNanoseconds, float[] trackingPose)
        {
            try
            {
                var entry = new PoseFusionGetTransformInput
                {
                    timestamp_ns = timestampNanoseconds,
                    timestamp_ms = timestampNanoseconds / 1_000_000,
                    world_instance_id = GetWorldLogId(world),
                    world_name = world.name,
                    tracking_pose_column_major = trackingPose
                };
                // 在通道复用跟踪缓冲区前完成序列化，异步写入只使用生成的 JSON。
                // Serialize before the channel reuses the tracking buffer. The async writer only receives the JSON.
                string json = JsonUtility.ToJson(entry);
                MixedSpacePoseJournalWriter.Append(MixedSpacePoseLogFiles.GetPath("pose_fusion_get_transform.json"), json);
            }
            catch (Exception exception)
            {
                MixedSpaceLogger.LogError(
                    $"Failed to append PoseFusionGetTransform input log: {exception.Message}");
            }
        }

        [Serializable]
        private sealed class PoseFusionGetTransformInput
        {
            public long timestamp_ns;
            public long timestamp_ms;
            public long world_instance_id;
            public string world_name;
            public float[] tracking_pose_column_major;
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
                MixedSpacePoseJournalWriter.Append(path, JsonUtility.ToJson(entry));
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
