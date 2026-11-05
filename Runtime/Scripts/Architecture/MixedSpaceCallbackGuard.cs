using System;
using UnityEngine.Events;

namespace MixedSpace
{
    /// <summary>
    /// 隔离应用回调与运行时控制流，单个 UI 或诊断订阅者异常不应中断初始化或释放。
    /// Isolates application callbacks from runtime control flow. One faulty UI or
    /// diagnostics subscriber must not interrupt initialization or teardown.
    /// </summary>
    internal static class MixedSpaceCallbackGuard
    {
        public static void Invoke(UnityEvent unityEvent, string signalName)
        {
            Invoke(() => unityEvent?.Invoke(), signalName);
        }

        public static void Invoke(Action callback, string signalName)
        {
            try
            {
                callback?.Invoke();
            }
            catch (Exception exception)
            {
                MixedSpaceLogger.LogError(
                    $"{signalName} subscriber failed: {exception.Message}");
            }
        }

        public static void Publish<T>(Action<T> subscribers, T value, string signalName)
        {
            if (subscribers == null)
                return;

            foreach (Delegate callback in subscribers.GetInvocationList())
            {
                try
                {
                    ((Action<T>)callback).Invoke(value);
                }
                catch (Exception exception)
                {
                    MixedSpaceLogger.LogError(
                        $"{signalName} subscriber failed: {exception.Message}");
                }
            }
        }
    }
}
