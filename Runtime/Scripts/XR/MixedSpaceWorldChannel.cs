using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 管理逻辑 World 运行行为的普通 C# 对象，由 MixedSpaceRuntime 持有；场景组件保持被动。
    /// Owns the runtime behavior of one logical world. This is a regular C#
    /// object managed by MixedSpaceRuntime; the scene component remains passive.
    /// </summary>
    public sealed class MixedSpaceWorldChannel : IMixedSpaceWorldChannel
    {
        // 将 1 秒除以 60 后向上取整到纳秒，确保实时位姿提交频率不超过 60 Hz。
        // Round one sixtieth of a second up to a whole nanosecond to cap live pose submissions at 60 Hz.
        private const long MinimumTrackingIntervalNanoseconds = 16_666_667;

        private readonly IMixedSpacePlatformAdapter m_Platform;
        private readonly IMixedSpacePoseFusion m_PoseFusion;
        private readonly IMixedSpaceTrackingLossSource m_TrackingLossSource;
        private readonly bool m_WritePoseLogs;
        private readonly MixedSpacePoseFusionConfig m_PoseFusionConfig;
        private bool? m_LastLoggedTracking;
        private bool m_Disposed;
        private MixedSpaceAlignmentSolution m_LastObservation;
        private long m_ObservationSequence;
        private float m_NextDiagnosticTime;
        private float m_NextFailureLogTime;
        private long m_LastSubmittedTrackingTimestamp;
        private long m_LastHandledTrackingLossTimestamp;
        private bool m_FusionResetFailed;
        private int m_SkippedBackwardTrackingSamples;
        private int m_SkippedDuplicateTrackingSamples;
        private int m_SkippedTooCloseTrackingSamples;
        private float m_NextTrackingTimestampLogTime;
        private float m_CadenceWindowStart;
        private int m_CadenceSamples;
        private int m_CadenceSubmissions;
        private int m_CadenceOutputs;
        private int m_CadenceWorldWrites;
        private MixedSpaceTrackingTimestampSource m_CadenceTimestampSource;

        // 原生调用是同步的，缓冲区归当前通道所有。
        // Native calls are synchronous; these buffers belong to this channel.
        private readonly float[] m_UnityTrackingPose = new float[16];
        private readonly float[] m_NativeTrackingPose = new float[16];
        private readonly float[] m_NativeWorldPose = new float[16];
        private readonly float[] m_UnityWorldPose = new float[16];

        public MixedSpaceWorldChannel(
            MixedSpaceWorldRoot view,
            IMixedSpacePlatformAdapter platform,
            IMixedSpacePoseFusionFactory poseFusionFactory, bool writePoseLogs = false)
            : this(view, platform, poseFusionFactory, writePoseLogs, MixedSpacePoseFusionConfig.Default)
        {
        }

        public MixedSpaceWorldChannel(
            MixedSpaceWorldRoot view,
            IMixedSpacePlatformAdapter platform,
            IMixedSpacePoseFusionFactory poseFusionFactory, bool writePoseLogs, MixedSpacePoseFusionConfig poseFusionConfig)
        {
            View = view ? view : throw new ArgumentNullException(nameof(view));
            m_Platform = platform ?? throw new ArgumentNullException(nameof(platform));
            m_TrackingLossSource = platform as IMixedSpaceTrackingLossSource;
            m_LastHandledTrackingLossTimestamp = m_TrackingLossSource?.LastTrackingLossTimestampNanoseconds ?? 0;
            View.IsPoseReliable = false;
            m_WritePoseLogs = writePoseLogs;
            poseFusionConfig.Validate();
            m_PoseFusionConfig = poseFusionConfig;

            IMixedSpacePoseFusion candidate = poseFusionFactory?.Create();
            if (candidate == null)
            {
                MixedSpaceLogger.LogError("Pose fusion session is unavailable. The World pose cannot be updated.");
                return;
            }

            if (candidate is MixedSpacePoseFusion nativeFusion)
                nativeFusion.WritePoseLogs = m_WritePoseLogs;

            int result = candidate.Initialize();
            if (result == 0) result = ApplyFusionConfig(candidate);
            if (result == 0)
            {
                m_PoseFusion = candidate;
            }
            else
            {
                MixedSpaceLogger.LogError($"Pose fusion initialization failed with code {result}.");
                candidate.Dispose();
            }
        }

        public MixedSpaceWorldRoot View { get; }
        public Matrix4x4 CurrentWorldPose => View ? View.CurrentPose : Matrix4x4.identity;

        public Task PublishAsync(MixedSpaceAlignmentSolution solution)
        {
            // 即使输出模型同步且无阶段队列，也保留异步接口以失败 Task 报错的契约。
            // Keep the asynchronous port's faulted-Task contract even though the output model itself is synchronous and has
            // no stage queue.
            try
            {
                Publish(solution);
                return Task.CompletedTask;
            }
            catch (Exception exception) { return Task.FromException(exception); }
        }

        private void Publish(MixedSpaceAlignmentSolution solution)
        {
            ThrowIfDisposed();
            if (solution == null || !View || m_PoseFusion == null)
                return;
            // 定位结果可能在跟踪丢失后才到达；拒绝在丢失时刻或之前采集的结果。
            // A localization result can arrive after tracking is lost. Reject captures at or before the loss.
            MixedSpacePlatformPoseSample sample = m_Platform.SamplePose();
            TraceTrackingState(sample);
            if (!ResetFusionAfterTrackingLoss() || !sample.IsTracking ||
                (m_LastHandledTrackingLossTimestamp > 0 &&
                 solution.TimestampNanoseconds <= m_LastHandledTrackingLossTimestamp))
                return;
            if (!IsValidPose(solution.WorldPose) || !IsValidPose(solution.TrackerPose) || !IsValidPose(solution.MapPose))
            {
                ReportFailure("invalid_localization_observation", -3);
                return;
            }
            // 通过一致性检查后仍保留原始观测，不将平滑后的显示位姿写回原生融合。
            // Preserve the original observation after consistency checks; never feed a smoothed display pose into fusion.
            if (solution.TimestampNanoseconds <= 0)
                throw new InvalidOperationException("Localization requires a positive device timestamp.");
            MixedSpaceAlignmentSolution output = solution;
            float[] nativeMapPose = ConvertToNative(output.MapPose, out int mapConversion);
            RequireSuccess("map_to_native", mapConversion);
            float[] nativeTrackingPose = ConvertToNative(output.TrackerPose, out int trackingConversion);
            RequireSuccess("capture_to_native", trackingConversion);
            var localization = new MixedSpacePoseFusionLocalization
            {
                Position = new[] { nativeMapPose[12], nativeMapPose[13], nativeMapPose[14] },
                Rotation = new[]
                {
                    nativeMapPose[0], nativeMapPose[1], nativeMapPose[2],
                    nativeMapPose[4], nativeMapPose[5], nativeMapPose[6],
                    nativeMapPose[8], nativeMapPose[9], nativeMapPose[10]
                },
                Confidence = output.Confidence,
                TrackingPose = nativeTrackingPose,
                TimestampNanoseconds = output.TimestampNanoseconds
            };

            int submission = m_PoseFusion.AddLocalization(localization);
            RequireSuccess("add_localization", submission);
            CommitObservation(solution);
            if (m_WritePoseLogs)
            {
                TraceAlignment("submit", output.TimestampNanoseconds, sample.IsTracking, null,
                    new[] { "map_to_native", "capture_to_native", "add_localization" },
                    new[] { mapConversion, trackingConversion, submission });
                MixedSpaceLocalizationJournal.AppendLocalizationLog(output, localization);
            }
        }

        public Matrix4x4 ProjectToLocal(Vector3 position, Quaternion rotation)
        {
            Matrix4x4 pose = Matrix4x4.TRS(position, rotation, Vector3.one);
            return CurrentWorldPose.inverse * pose;
        }

        public Task ClearAsync()
        {
            try
            {
                ThrowIfDisposed();
                m_LastObservation = null;
                View.IsPoseReliable = false;
                // 此处只清除托管侧的观测状态，保留原生融合会话。
                // Clear the last managed observation without resetting the native fusion session.
                if (m_WritePoseLogs)
                    TraceAlignment("clear", 0, false, null, null, null);
                return Task.CompletedTask;
            }
            catch (Exception exception) { return Task.FromException(exception); }
        }

        internal bool NeedsTrackingSample => !m_Disposed && View && m_PoseFusion != null;

        public void Tick()
        {
            if (NeedsTrackingSample)
                Tick(m_Platform.SamplePose());
        }

        internal void Tick(MixedSpacePlatformPoseSample sample)
        {
            if (!NeedsTrackingSample)
                return;

            if (m_WritePoseLogs)
            {
                TraceTrackingState(sample);
                RecordTrackingCadence(sample.TimestampSource);
            }
            bool fusionReady = ResetFusionAfterTrackingLoss();
            if (!sample.IsTracking || !fusionReady)
            {
                View.IsPoseReliable = false;
                return;
            }
            if (sample.TimestampNanoseconds <= 0)
            {
                ReportOutputFailure("invalid_tracking_timestamp", -3);
                return;
            }
            // 限频仅用于 GetTransform；异步返回的定位结果仍使用图像采集时间。
            // Rate limiting applies only to GetTransform. Asynchronous localization results retain the capture time.
            long intervalNanoseconds = sample.TimestampNanoseconds - m_LastSubmittedTrackingTimestamp;
            if (m_LastSubmittedTrackingTimestamp != 0 && intervalNanoseconds < MinimumTrackingIntervalNanoseconds)
            {
                if (intervalNanoseconds < 0) ++m_SkippedBackwardTrackingSamples;
                else if (intervalNanoseconds == 0) ++m_SkippedDuplicateTrackingSamples;
                else ++m_SkippedTooCloseTrackingSamples;
                TraceSkippedTrackingTimestamp(sample, intervalNanoseconds);
                return;
            }

            Matrix4x4 trackingPose = Matrix4x4.TRS(
                sample.Position,
                sample.Rotation,
                Vector3.one);
            if (!IsValidPose(trackingPose))
            {
                ReportOutputFailure("invalid_tracking_pose", -3);
                return;
            }
            for (int i = 0; i < 16; ++i)
                m_UnityTrackingPose[i] = trackingPose[i];
            Array.Clear(m_NativeTrackingPose, 0, 16);
            int trackingConversion = m_PoseFusion.ConvertUnityToNativePose(
                m_UnityTrackingPose, m_NativeTrackingPose);
            if (!CheckSuccess("tracking_to_native", trackingConversion))
                return;
            if (!IsValidNativePose(m_NativeTrackingPose))
            {
                ReportOutputFailure("invalid_native_tracking_pose", -3);
                return;
            }
            Array.Clear(m_NativeWorldPose, 0, 16);
            // 跨跟踪丢失和重置保留上次提交时间；原生调用失败也计入限频。
            // Keep the last submission time across loss and reset. Failed native calls also count toward the rate limit.
            m_LastSubmittedTrackingTimestamp = sample.TimestampNanoseconds;
            if (m_WritePoseLogs)
            {
                ++m_CadenceSubmissions;
                MixedSpaceLocalizationJournal.AppendGetTransformInput(
                    View, sample.TimestampNanoseconds, m_NativeTrackingPose);
            }
            int fusionResult = m_PoseFusion.GetTransform(
                sample.TimestampNanoseconds,
                m_NativeTrackingPose,
                m_NativeWorldPose);
            if (!CheckSuccess("get_transform", fusionResult))
                return;
            if (!IsValidNativePose(m_NativeWorldPose))
            {
                ReportOutputFailure("invalid_native_world_pose", -3);
                return;
            }

            if (m_LastObservation == null || !NeedsTrackingSample)
                return;

            Array.Clear(m_UnityWorldPose, 0, 16);
            int outputConversion = m_PoseFusion.ConvertNativePoseToUnity(m_NativeWorldPose, m_UnityWorldPose);
            if (!CheckSuccess("output_to_unity", outputConversion))
                return;
            Matrix4x4 pose = new Matrix4x4();
            for (int i = 0; i < 16; i++)
                pose[i] = m_UnityWorldPose[i];
            if (!View.TryApplyPose(pose, out bool transformWritten, m_WritePoseLogs,
                sample.TimestampNanoseconds, sample, fusionResult))
            {
                ReportOutputFailure("invalid_world_pose", -3);
                return;
            }
            View.IsPoseReliable = true;
            if (m_WritePoseLogs)
            {
                ++m_CadenceOutputs;
                if (transformWritten) ++m_CadenceWorldWrites;
            }
            if (m_WritePoseLogs && Time.realtimeSinceStartup >= m_NextDiagnosticTime)
            {
                m_NextDiagnosticTime = Time.realtimeSinceStartup + 1f;
                TraceAlignment("fusion_output", sample.TimestampNanoseconds, sample.IsTracking, pose,
                    new[] { "tracking_to_native", "get_transform", "output_to_unity" },
                    new[] { trackingConversion, fusionResult, outputConversion });
            }
        }

        // 记录首个实时跟踪状态及后续变化，避免按帧重复写入相同状态。
        // Log the first live tracking state and later changes without repeating the same state each frame.
        private void TraceTrackingState(MixedSpacePlatformPoseSample sample)
        {
            if (!m_WritePoseLogs || m_LastLoggedTracking == sample.IsTracking) return;
            m_LastLoggedTracking = sample.IsTracking;
            MixedSpaceLocalizationJournal.AppendLiveTrackingStatus(View, sample);
        }

        private void RecordTrackingCadence(MixedSpaceTrackingTimestampSource timestampSource)
        {
            float now = Time.realtimeSinceStartup;
            if (m_CadenceSamples == 0)
                m_CadenceWindowStart = now;
            else if (now - m_CadenceWindowStart >= 5f)
            {
                MixedSpaceLocalizationJournal.AppendFusionCadence(View, m_CadenceTimestampSource,
                    now - m_CadenceWindowStart, m_CadenceSamples, m_CadenceSubmissions,
                    m_CadenceOutputs, m_CadenceWorldWrites);
                m_CadenceWindowStart = now;
                m_CadenceSamples = 0;
                m_CadenceSubmissions = 0;
                m_CadenceOutputs = 0;
                m_CadenceWorldWrites = 0;
                m_CadenceTimestampSource = MixedSpaceTrackingTimestampSource.Unspecified;
            }

            if (timestampSource != MixedSpaceTrackingTimestampSource.Unspecified)
                m_CadenceTimestampSource = timestampSource;
            ++m_CadenceSamples;
        }

        private void TraceSkippedTrackingTimestamp(MixedSpacePlatformPoseSample sample, long intervalNanoseconds)
        {
            if (!m_WritePoseLogs && MixedSpaceLogger.Level > MixedSpaceLogger.LoggingLevel.Verbose) return;
            if (Time.realtimeSinceStartup < m_NextTrackingTimestampLogTime) return;
            m_NextTrackingTimestampLogTime = Time.realtimeSinceStartup + 5f;
            try
            {
                string action = intervalNanoseconds > 0 ? "skip_too_close" : "skip_non_increasing";
                string text = $"[TIME-CHECK] api=PoseFusionGetTransform action={action} timestamp_ns={sample.TimestampNanoseconds} timestamp_source={sample.TimestampSource} last_submitted_ns={m_LastSubmittedTrackingTimestamp} delta_ns={intervalNanoseconds} skipped_backward={m_SkippedBackwardTrackingSamples} skipped_duplicate={m_SkippedDuplicateTrackingSamples} skipped_too_close={m_SkippedTooCloseTrackingSamples}";
                if (m_WritePoseLogs)
                {
                    MixedSpacePoseFusionLogFile.Initialize();
                    MixedSpacePoseFusionLogFile.Append(2, text);
                }
                MixedSpaceLogger.Log(text, MixedSpaceLogger.LoggingLevel.Verbose, "TimestampCheck");
                m_SkippedBackwardTrackingSamples = 0;
                m_SkippedDuplicateTrackingSamples = 0;
                m_SkippedTooCloseTrackingSamples = 0;
            }
            catch { }
        }

        // 每个通道处理同一丢失事件一次。重置失败时暂停融合，直到后续丢失触发的重置成功。
        // Handle each observed loss once per channel. If reset fails, suspend fusion until a later loss is reset successfully.
        private bool ResetFusionAfterTrackingLoss()
        {
            long lostAt = m_TrackingLossSource?.LastTrackingLossTimestampNanoseconds ?? 0;
            if (lostAt <= m_LastHandledTrackingLossTimestamp)
                return !m_FusionResetFailed;

            m_LastHandledTrackingLossTimestamp = lostAt;
            m_LastObservation = null;
            View.IsPoseReliable = false;

            m_FusionResetFailed = true;
            int result = m_PoseFusion.Reset();
            m_FusionResetFailed = result != 0;
            if (m_FusionResetFailed)
                MixedSpaceLogger.LogWarning($"PoseFusionReset failed after tracking loss with code {result}.");
            if (m_WritePoseLogs)
                TraceAlignment("tracking_loss_reset", lostAt, false, null, new[] { "PoseFusionReset" }, new[] { result });

            return !m_FusionResetFailed;
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;
            m_Disposed = true;
            m_PoseFusion?.Dispose();
        }

        private void TraceAlignment(string stage, long timestamp, bool tracking, Matrix4x4? output,
            string[] operations, int[] returnCodes)
        {
            if (m_WritePoseLogs && View)
                MixedSpaceLocalizationJournal.AppendAlignmentDiagnostic(View, m_ObservationSequence,
                    m_LastObservation, stage, timestamp, tracking, output, operations, returnCodes);
        }

        private float[] ConvertToNative(Matrix4x4 pose, out int returnCode)
        {
            if (!IsValidPose(pose))
                throw new InvalidOperationException("Cannot submit an invalid pose to native fusion.");
            float[] nativePose = new float[16];
            returnCode = m_PoseFusion.ConvertUnityToNativePose(ToColumnMajorArray(pose), nativePose);
            if (returnCode == 0 && !IsValidNativePose(nativePose))
                throw new InvalidOperationException("Native pose conversion returned an invalid matrix.");
            return nativePose;
        }

        private void CommitObservation(MixedSpaceAlignmentSolution solution)
        {
            m_LastObservation = solution;
            ++m_ObservationSequence;
            m_NextDiagnosticTime = 0;
        }

        private int ApplyFusionConfig(IMixedSpacePoseFusion fusion)
        {
            int result = fusion.SetConfig(m_PoseFusionConfig);
            // 缺少配置入口时，通道使用原生库默认值继续初始化。
            // The channel can initialize with native defaults if the configuration entry point is unavailable.
            return result == MixedSpacePoseFusion.ConfigurationNotSupported ? 0 : result;
        }

        private static void RequireSuccess(string operation, int result)
        {
            if (result != 0)
                throw new InvalidOperationException($"Pose fusion {operation} failed with code {result}.");
        }

        private bool CheckSuccess(string operation, int result)
        {
            if (result == 0) return true;
            ReportOutputFailure(operation, result);
            return false;
        }

        private void ReportOutputFailure(string operation, int result)
        {
            if (View) View.IsPoseReliable = false;
            ReportFailure(operation, result);
        }

        private void ReportFailure(string operation, int result)
        {
            // 持续失败时避免每帧分配警告字符串。
            // Avoid allocating a warning string every frame during persistent failure.
            if (Time.realtimeSinceStartup < m_NextFailureLogTime) return;
            m_NextFailureLogTime = Time.realtimeSinceStartup + 5f;
            MixedSpaceLogger.LogWarning($"Pose fusion {operation} failed with code {result}; keeping the last world pose.");
        }

        private static bool IsValidPose(Matrix4x4 pose)
        {
            return MixedSpaceWorldRoot.TryPreparePose(pose, out _);
        }

        private static bool IsValidNativePose(float[] values)
        {
            var pose = new Matrix4x4();
            for (int i = 0; i < 16; ++i)
                pose[i] = values[i];
            return IsValidPose(pose);
        }

        private static float[] ToColumnMajorArray(Matrix4x4 matrix)
        {
            return new[]
            {
                matrix.m00, matrix.m10, matrix.m20, matrix.m30,
                matrix.m01, matrix.m11, matrix.m21, matrix.m31,
                matrix.m02, matrix.m12, matrix.m22, matrix.m32,
                matrix.m03, matrix.m13, matrix.m23, matrix.m33
            };
        }


        private void ThrowIfDisposed()
        {
            if (m_Disposed)
                throw new ObjectDisposedException(nameof(MixedSpaceWorldChannel));
        }

    }

    /// <summary>
    /// 管理单 SDK 的全部 World 通道，提供统一的运行时生命周期入口。
    /// Owns all world channels for one SDK instance and provides their single
    /// lifecycle entry point to MixedSpaceRuntime.
    /// </summary>
    public sealed class MixedSpaceWorldRegistry : IDisposable
    {
        private readonly IMixedSpacePlatformAdapter m_Platform;
        private readonly IMixedSpacePoseFusionFactory m_PoseFusionFactory;
        private readonly bool m_WritePoseLogs;
        private readonly MixedSpacePoseFusionConfig m_PoseFusionConfig;
        private readonly Dictionary<MixedSpaceWorldRoot, MixedSpaceWorldChannel> m_Channels =
            new Dictionary<MixedSpaceWorldRoot, MixedSpaceWorldChannel>();
        private bool m_Disposed;

        private MixedSpaceWorldChannel[] m_TickChannels = Array.Empty<MixedSpaceWorldChannel>();
        private bool m_TickChannelsDirty;

        public MixedSpaceWorldRegistry(
            IMixedSpacePlatformAdapter platform,
            IMixedSpacePoseFusionFactory poseFusionFactory, bool writePoseLogs = false)
            : this(platform, poseFusionFactory, writePoseLogs, MixedSpacePoseFusionConfig.Default)
        {
        }

        public MixedSpaceWorldRegistry(
            IMixedSpacePlatformAdapter platform,
            IMixedSpacePoseFusionFactory poseFusionFactory, bool writePoseLogs, MixedSpacePoseFusionConfig poseFusionConfig)
        {
            m_Platform = platform ?? throw new ArgumentNullException(nameof(platform));
            m_PoseFusionFactory = poseFusionFactory;
            m_WritePoseLogs = writePoseLogs;
            poseFusionConfig.Validate();
            m_PoseFusionConfig = poseFusionConfig;
        }

        public IMixedSpaceWorldChannel Open(MixedSpaceWorldRoot view)
        {
            if (m_Disposed)
                throw new ObjectDisposedException(nameof(MixedSpaceWorldRegistry));
            if (!view)
                throw new ArgumentNullException(nameof(view));

            if (!m_Channels.TryGetValue(view, out MixedSpaceWorldChannel channel))
            {
                channel = new MixedSpaceWorldChannel(view, m_Platform, m_PoseFusionFactory,
                    m_WritePoseLogs, m_PoseFusionConfig);
                m_Channels.Add(view, channel);
                m_TickChannelsDirty = true;
            }

            return channel;
        }

        public IReadOnlyList<IMixedSpaceWorldChannel> ActiveChannels =>
            m_Channels.Values.Cast<IMixedSpaceWorldChannel>().ToArray();

        /// <summary>
        /// 最后一张地图离开仓库后释放通道；场景 GameObject 生命周期仍由准备层管理。
        /// Releases a channel after its final map has left the runtime library.
        /// Scene GameObject lifetime remains the provisioning layer's concern.
        /// </summary>
        public bool Close(MixedSpaceWorldRoot view)
        {
            if (!view || !m_Channels.TryGetValue(view, out MixedSpaceWorldChannel channel))
                return false;

            m_Channels.Remove(view);
            m_TickChannelsDirty = true;
            channel.Dispose();
            return true;
        }

        public void Tick()
        {
            if (m_Disposed)
                return;

            if (m_TickChannelsDirty)
            {
                m_TickChannels = m_Channels.Values.ToArray();
                m_TickChannelsDirty = false;
            }
            // 回调可能在 Tick 中打开或关闭通道，迭代稳定快照以保持安全。
            // Keep a stable snapshot if callbacks open/close channels during this tick.
            MixedSpaceWorldChannel[] channels = m_TickChannels;
            MixedSpacePlatformPoseSample? sample = null;
            foreach (MixedSpaceWorldChannel channel in channels)
            {
                if (!channel.NeedsTrackingSample)
                    continue;
                // Share one live sample across all worlds in this update. Each native session keeps its own history.
                if (!sample.HasValue)
                    sample = m_Platform.SamplePose();
                channel.Tick(sample.Value);
            }
        }

        public async Task ClearAllAsync()
        {
            foreach (MixedSpaceWorldChannel channel in m_Channels.Values.ToArray())
            {
                if (channel.View)
                    await channel.ClearAsync();
            }
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;
            m_Disposed = true;
            foreach (MixedSpaceWorldChannel channel in m_Channels.Values)
                channel.Dispose();
            m_Channels.Clear();
            m_TickChannels = Array.Empty<MixedSpaceWorldChannel>();
            m_TickChannelsDirty = false;
        }
    }
}
