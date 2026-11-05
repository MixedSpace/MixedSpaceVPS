using System;
using System.Collections.Generic;
using MixedSpace.XR;

namespace MixedSpace
{
    /// <summary>
    /// 会话最新的求解结果，不是相机帧或场景写入回执。定位和重置均增加修订号，空修订使基于旧结果的呈现失效，无需第二套视图重置协议。
    /// A session's latest solved fix, not a camera frame or a scene-write receipt.
    /// Revisions advance on both fixes and resets. An empty revision invalidates
    /// every presentation derived from the previous fix without resetting views
    /// through a second lifecycle/processor protocol.
    /// </summary>
    public sealed class MixedSpaceLocalizationSnapshot
    {
        internal MixedSpaceLocalizationSnapshot(long revision,
            MixedSpaceAlignmentRequest request = null,
            MixedSpaceAlignmentSolution solution = null, int previousMapKey = -1)
        {
            Revision = revision;
            Request = request;
            Solution = solution;
            PreviousMapKey = previousMapKey;
        }

        public long Revision { get; }
        public MixedSpaceAlignmentRequest Request { get; }
        public MixedSpaceAlignmentSolution Solution { get; }
        public int PreviousMapKey { get; }
        public bool HasFix => Request != null;
        public int MapKey => Request?.MapKey ?? -1;
        public bool IsFirstFix => HasFix && PreviousMapKey < 0;
        public bool MapChanged => HasFix && MapKey != PreviousMapKey;
    }

    /// <summary>
    /// 只读应用接口，订阅未来修订；Snapshot 提供当前值而不重放历史事件。通知在会话/Unity 线程执行，不能修改位姿。
    /// Read-only application port. Subscriptions observe future revisions;
    /// Snapshot supplies the current value without replaying a historical event.
    /// Notifications run on the session/Unity thread and cannot transform poses.
    /// </summary>
    public interface IMixedSpaceLocalizationReadModel
    {
        MixedSpaceLocalizationSnapshot Snapshot { get; }
        IDisposable Subscribe(Action<MixedSpaceLocalizationSnapshot> changed);
    }

    /// <summary>
    /// Unity 呈现器连接一次并返回释放句柄；不参与定位、不返回变换后的帧、不持有地图历史，也不得修改求解器状态。
    /// A Unity presenter connects once, returning its teardown handle. It does not
    /// participate in localization execution, return a transformed frame, or own
    /// the session's map history. Implementations must not mutate solver state.
    /// </summary>
    public interface IMixedSpaceLocalizationBinding
    {
        IDisposable Connect(IMixedSpaceLocalizationReadModel source);
    }

    /// <summary>
    /// 单会话持有的单写者状态，写接口为内部接口；使用方只通过只读端口访问不可变元数据。订阅者失败相互隔离；对象限于单线程，不是跨线程事件总线。
    /// Single-writer state owned by one session. The writer API is internal;
    /// consumers only see immutable metadata through the read-only port.
    /// Subscriber failures are isolated from localization and other subscribers.
    /// This class is thread-confined, not a cross-thread event bus.
    /// </summary>
    public sealed class MixedSpaceLocalizationReadModel : IMixedSpaceLocalizationReadModel, IDisposable
    {
        private readonly List<Subscription> m_Subscriptions = new List<Subscription>();
        private readonly Action<Exception> m_ReportFailure;
        private bool m_Disposed;

        public MixedSpaceLocalizationReadModel(Action<Exception> reportFailure = null)
        {
            m_ReportFailure = reportFailure ?? (error =>
                MixedSpaceLogger.LogError($"Localization view failed: {error.Message}"));
        }

        public MixedSpaceLocalizationSnapshot Snapshot { get; private set; } =
            new MixedSpaceLocalizationSnapshot(0);

        public IDisposable Subscribe(Action<MixedSpaceLocalizationSnapshot> changed)
        {
            ThrowIfDisposed();
            if (changed == null) throw new ArgumentNullException(nameof(changed));
            var subscription = new Subscription(this, changed);
            m_Subscriptions.Add(subscription);
            return subscription;
        }

        internal void Record(MixedSpaceAlignmentRequest request, MixedSpaceAlignmentSolution solution)
        {
            ThrowIfDisposed();
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (solution == null) throw new ArgumentNullException(nameof(solution));
            if (solution.MapKey != request.MapKey)
                throw new ArgumentException("A solved fix must retain its source map identity.", nameof(solution));
            Publish(new MixedSpaceLocalizationSnapshot(
                Snapshot.Revision + 1, request, solution, Snapshot.MapKey));
        }

        internal void Clear()
        {
            ThrowIfDisposed();
            Publish(new MixedSpaceLocalizationSnapshot(Snapshot.Revision + 1));
        }

        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            // 解除监听前使主动拉取的呈现器失效。
            // Invalidate pull-based presenters before detaching their listeners.
            Publish(new MixedSpaceLocalizationSnapshot(Snapshot.Revision + 1));
            foreach (Subscription subscription in m_Subscriptions.ToArray())
                subscription.Dispose();
        }

        private void Publish(MixedSpaceLocalizationSnapshot snapshot)
        {
            Snapshot = snapshot;
            foreach (Subscription subscription in m_Subscriptions.ToArray())
            {
                // 回调可能重入重置或释放；新空修订发布后不再发送旧结果。
                // A callback can reset/dispose the session reentrantly. Never deliver an older fix after a newer empty/reset
                // revision.
                if (!ReferenceEquals(Snapshot, snapshot)) break;
                try { subscription.Deliver(snapshot); }
                catch (Exception error)
                {
                    try { m_ReportFailure(error); }
                    catch
                    {
                        // 诊断输出失败不得中断定位。
                        // A diagnostic sink must not break localization.
                    }
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (m_Disposed) throw new ObjectDisposedException(nameof(MixedSpaceLocalizationReadModel));
        }

        private sealed class Subscription : IDisposable
        {
            private MixedSpaceLocalizationReadModel m_Owner;
            private Action<MixedSpaceLocalizationSnapshot> m_Changed;

            public Subscription(MixedSpaceLocalizationReadModel owner,
                Action<MixedSpaceLocalizationSnapshot> changed)
            { m_Owner = owner; m_Changed = changed; }

            public void Deliver(MixedSpaceLocalizationSnapshot snapshot) => m_Changed?.Invoke(snapshot);

            public void Dispose()
            {
                m_Owner?.m_Subscriptions.Remove(this);
                m_Owner = null;
                m_Changed = null;
            }
        }
    }

    /// <summary>
    /// 管理单运行时的呈现连接；部分启动失败时断开已接入视图，释放时尝试清理所有句柄。
    /// Owns presentation connections for one runtime. Partial startup failure
    /// disconnects already attached views, and teardown attempts every handle.
    /// </summary>
    internal sealed class MixedSpaceLocalizationConnections : IDisposable
    {
        private readonly List<IDisposable> m_Handles = new List<IDisposable>();

        public MixedSpaceLocalizationConnections(IMixedSpaceLocalizationReadModel source,
            IEnumerable<IMixedSpaceLocalizationBinding> bindings)
        {
            try
            {
                foreach (IMixedSpaceLocalizationBinding binding in
                         bindings ?? Array.Empty<IMixedSpaceLocalizationBinding>())
                {
                    if (binding == null) continue;
                    m_Handles.Add(binding.Connect(source) ?? throw new InvalidOperationException(
                        "A localization binding must return a teardown handle."));
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            IDisposable[] handles = m_Handles.ToArray();
            m_Handles.Clear();
            for (int index = handles.Length - 1; index >= 0; index--)
            {
                try { handles[index].Dispose(); }
                catch (Exception error)
                { MixedSpaceLogger.LogError($"Localization view disconnect failed: {error.Message}"); }
            }
        }
    }
}
