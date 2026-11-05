using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MixedSpace
{
    /// <summary>
    /// 运行时监督器接收的高层命令；应用请求目标行为，不自行协调会话、地图、跟踪和平台清理。
    /// Coarse-grained commands accepted by the SDK runtime supervisor. Application
    /// code requests an outcome instead of coordinating session, map, tracking and
    /// platform cleanup methods itself.
    /// </summary>
    public enum MixedSpaceRuntimeCommand
    {
        RecoverTracking,
        ClearWorld,
        Rebuild
    }

    public interface IMixedSpaceRuntimeControl
    {
        Task ExecuteAsync(
            MixedSpaceRuntimeCommand command,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// 串行执行修改整个运行时状态的命令，相同命令执行期间合并重复请求。
    /// Serializes commands that mutate runtime-wide state and coalesces duplicate
    /// requests while the same command is in flight.
    /// </summary>
    internal sealed class MixedSpaceRuntimeControl : IMixedSpaceRuntimeControl, IDisposable
    {
        private readonly Func<MixedSpaceRuntimeCommand, CancellationToken, Task> m_Handler;
        private readonly SemaphoreSlim m_Gate = new SemaphoreSlim(1, 1);
        private readonly object m_InFlightLock = new object();
        private readonly Dictionary<MixedSpaceRuntimeCommand, Task> m_InFlight =
            new Dictionary<MixedSpaceRuntimeCommand, Task>();
        private bool m_Disposed;

        public MixedSpaceRuntimeControl(
            Func<MixedSpaceRuntimeCommand, CancellationToken, Task> handler)
        {
            m_Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public Task ExecuteAsync(
            MixedSpaceRuntimeCommand command,
            CancellationToken cancellationToken = default)
        {
            lock (m_InFlightLock)
            {
                ThrowIfDisposed();
                if (m_InFlight.TryGetValue(command, out Task active) && !active.IsCompleted)
                    return active;

                Task operation = ExecuteSerializedAsync(command, cancellationToken);
                m_InFlight[command] = operation;
                return operation;
            }
        }

        internal bool TryGetInFlight(MixedSpaceRuntimeCommand command, out Task operation)
        {
            lock (m_InFlightLock)
            {
                if (m_InFlight.TryGetValue(command, out operation) && !operation.IsCompleted)
                    return true;

                operation = null;
                return false;
            }
        }

        public void Dispose()
        {
            lock (m_InFlightLock)
            {
                if (m_Disposed)
                    return;

                m_Disposed = true;
                m_InFlight.Clear();
            }

            // 生命周期取消后操作可能仍在退出 finally；保留轻量闸门，避免销毁时与最后一次 Release 竞争。
            // An operation that observed lifecycle cancellation may still be unwinding through its finally block. Leaving
            // the lightweight gate undisposed avoids racing that final Release during Unity teardown.
        }

        private async Task ExecuteSerializedAsync(
            MixedSpaceRuntimeCommand command,
            CancellationToken cancellationToken)
        {
            await m_Gate.WaitAsync(cancellationToken);
            try
            {
                await m_Handler(command, cancellationToken);
            }
            finally
            {
                m_Gate.Release();
                lock (m_InFlightLock)
                {
                    m_InFlight.Remove(command);
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (m_Disposed)
                throw new ObjectDisposedException(nameof(MixedSpaceRuntimeControl));
        }
    }
}
