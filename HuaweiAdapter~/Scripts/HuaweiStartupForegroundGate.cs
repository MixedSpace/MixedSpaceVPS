using System;
using System.Threading;
using System.Threading.Tasks;

namespace MixedSpace.XR.Huawei
{
    /// <summary>
    /// 权限回调可能早于 Unity 的暂停或焦点回调。
    /// Permission callbacks can precede Unity's pause/focus callbacks.
    /// </summary>
    internal static class HuaweiStartupForegroundGate
    {
        public static async Task WaitAsync(Func<bool> foreground, CancellationToken token,
            Func<CancellationToken, Task> nextCheck = null)
        {
            if (foreground == null) throw new ArgumentNullException(nameof(foreground));
            nextCheck = nextCheck ?? (ct => Task.Delay(50, ct));
            bool previous = false;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                bool current = foreground();
                token.ThrowIfCancellationRequested();
                // 即使权限回调先到，也至少经过一次异步调度边界，再检查暂停状态。
                // Always cross an asynchronous boundary, even if the permission callback arrives before the corresponding pause
                // callback.
                if (previous && current) return;
                previous = current;
                await nextCheck(token);
                token.ThrowIfCancellationRequested();
            }
        }
    }
}
