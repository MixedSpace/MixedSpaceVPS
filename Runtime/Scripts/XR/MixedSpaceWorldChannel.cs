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
        private readonly IMixedSpacePlatformAdapter m_Platform;
        private readonly IMixedSpacePoseFusion m_PoseFusion;
        private readonly MixedSpaceWorldPoseState m_OutputState;
        private readonly bool m_WritePoseLogs;
        private readonly MixedSpacePoseFusionConfig m_PoseFusionConfig;
        private bool m_WasTracking;
        private bool m_Disposed;
        private MixedSpaceAlignmentSolution m_LastObservation;
        private long m_ObservationSequence;
        private float m_NextDiagnosticTime;
        private float m_NextFailureLogTime;
        private long m_LastSubmittedTrackingTimestamp;
        private int m_SkippedBackwardTrackingSamples;
        private int m_SkippedDuplicateTrackingSamples;
        private float m_NextTrackingTimestampLogTime;

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
            m_OutputState = new MixedSpaceWorldPoseState(view.CaptureOutputSettings());
            m_WritePoseLogs = writePoseLogs;
            poseFusionConfig.Validate();
            m_PoseFusionConfig = poseFusionConfig;

            IMixedSpacePoseFusion candidate = poseFusionFactory?.Create();
            if (candidate == null)
                return;

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
            if (solution == null || !View)
                return;
            // 定位结果到达时，相应采集可能已失去跟踪。
            // A localization result can arrive after its capture lost tracking.
            if (m_PoseFusion != null && !m_Platform.SamplePose().IsTracking)
                return;
            if (!IsValidPose(solution.WorldPose) || !IsValidPose(solution.TrackerPose) || !IsValidPose(solution.MapPose))
            {
                ReportFailure("invalid_localization_observation", -3);
                return;
            }
            if (m_PoseFusion == null)
            {
                // 平台可以不提供融合实现；已求解世界位姿仍可作为有效、确定的回退输出。
                // A platform may intentionally provide no fusion implementation. The solved world pose remains a valid,
                // deterministic fallback.
                Matrix4x4 pose = View.ProcessAlignment ?
                    m_OutputState.Accept(solution, Time.deltaTime) : solution.WorldPose;
                View.ApplyPose(pose, m_WritePoseLogs, solution.TimestampNanoseconds);
                View.IsPoseReliable = true;
                CommitObservation(solution);
                TraceAlignment("direct_output", solution.TimestampNanoseconds, false, pose, null, null);
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
                TraceAlignment("submit", output.TimestampNanoseconds, m_WasTracking, null,
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
                // 即使处理关闭也清理历史，重新启用时不能恢复重置前的样本。
                // Clear even when processing is disabled; re-enabling must not resurrect samples from before the session reset.
                m_OutputState.Clear();
                m_LastObservation = null;
                View.IsPoseReliable = false;
                m_WasTracking = false;
                // Clear managed observation/output state without resetting the native fusion session.
                if (m_WritePoseLogs)
                    TraceAlignment("clear", 0, false, null, null, null);
                return Task.CompletedTask;
            }
            catch (Exception exception) { return Task.FromException(exception); }
        }

        public void Tick()
        {
            if (m_Disposed || !View)
                return;

            if (m_PoseFusion == null)
            {
                // 禁用位姿处理时仍推进平滑时间，直到 World 关闭。
                // Advance smoothing time while pose processing is disabled, until the World closes.
                m_OutputState.Advance(Time.deltaTime);
                // 回退输出仅在定位结果到达时发布，不在两次定位之间逐帧修改 Transform。
                // Preserve fallback output cadence: publish on a localization, not continuously to the Transform between
                // localizations.
                return;
            }

            MixedSpacePlatformPoseSample sample = m_Platform.SamplePose();
            if (!sample.IsTracking)
            {
                View.IsPoseReliable = false;
                m_WasTracking = false;
                return;
            }
            m_WasTracking = true;
            if (sample.TimestampNanoseconds <= 0)
                return;
            // Only live GetTransform samples must be strictly increasing. Delayed localization
            // observations keep their original capture times and are not subject to this check.
            if (sample.TimestampNanoseconds <= m_LastSubmittedTrackingTimestamp)
            {
                if (sample.TimestampNanoseconds < m_LastSubmittedTrackingTimestamp)
                    ++m_SkippedBackwardTrackingSamples;
                else ++m_SkippedDuplicateTrackingSamples;
                TraceSkippedTrackingTimestamp(sample.TimestampNanoseconds);
                return;
            }

            Matrix4x4 trackingPose = Matrix4x4.TRS(
                sample.Position,
                sample.Rotation,
                Vector3.one);
            if (!IsValidPose(trackingPose))
            {
                ReportFailure("invalid_tracking_pose", -3);
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
                ReportFailure("invalid_native_tracking_pose", -3);
                return;
            }
            Array.Clear(m_NativeWorldPose, 0, 16);
            // Retain this high-water mark across tracking loss and ClearAsync: neither resets the
            // native fusion session. Record every submitted time even if native returns an error.
            m_LastSubmittedTrackingTimestamp = sample.TimestampNanoseconds;
            int fusionResult = m_PoseFusion.GetTransform(
                sample.TimestampNanoseconds,
                m_NativeTrackingPose,
                m_NativeWorldPose);
            if (!CheckSuccess("get_transform", fusionResult))
                return;
            if (!IsValidNativePose(m_NativeWorldPose))
            {
                ReportFailure("invalid_native_world_pose", -3);
                return;
            }

            Array.Clear(m_UnityWorldPose, 0, 16);
            int outputConversion = m_PoseFusion.ConvertNativePoseToUnity(m_NativeWorldPose, m_UnityWorldPose);
            if (!CheckSuccess("output_to_unity", outputConversion))
                return;
            Matrix4x4 pose = new Matrix4x4();
            for (int i = 0; i < 16; i++)
                pose[i] = m_UnityWorldPose[i];
            if (!IsValidPose(pose))
            {
                ReportFailure("invalid_world_pose", -3);
                return;
            }
            if (m_LastObservation == null) return;
            View.ApplyPose(pose, m_WritePoseLogs, sample.TimestampNanoseconds, sample, fusionResult);
            View.IsPoseReliable = true;
            if (m_WritePoseLogs && Time.realtimeSinceStartup >= m_NextDiagnosticTime)
            {
                m_NextDiagnosticTime = Time.realtimeSinceStartup + 1f;
                TraceAlignment("fusion_output", sample.TimestampNanoseconds, sample.IsTracking, pose,
                    new[] { "tracking_to_native", "get_transform", "output_to_unity" },
                    new[] { trackingConversion, fusionResult, outputConversion });
            }
        }

        private void TraceSkippedTrackingTimestamp(long timestamp)
        {
            if (Time.realtimeSinceStartup < m_NextTrackingTimestampLogTime) return;
            m_NextTrackingTimestampLogTime = Time.realtimeSinceStartup + 5f;
            try
            {
                string text = $"[TIME-CHECK] api=PoseFusionGetTransform action=skip_non_increasing timestamp_ns={timestamp} last_submitted_ns={m_LastSubmittedTrackingTimestamp} skipped_backward={m_SkippedBackwardTrackingSamples} skipped_duplicate={m_SkippedDuplicateTrackingSamples}";
                MixedSpacePoseFusionLogFile.Initialize();
                MixedSpacePoseFusionLogFile.Append(2, text);
                MixedSpaceLogger.Log(text, MixedSpaceLogger.LoggingLevel.Verbose, "TimestampCheck");
                m_SkippedBackwardTrackingSamples = 0;
                m_SkippedDuplicateTrackingSamples = 0;
            }
            catch { }
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;
            m_Disposed = true;
            m_OutputState.Clear();
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
            // Older native payloads retain their existing fallback behavior and report that custom values were not applied.
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
            ReportFailure(operation, result);
            return false;
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
                channel = new MixedSpaceWorldChannel(view, m_Platform, m_PoseFusionFactory, m_WritePoseLogs, m_PoseFusionConfig);
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
            foreach (MixedSpaceWorldChannel channel in channels)
            {
                if (channel.View)
                    channel.Tick();
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
