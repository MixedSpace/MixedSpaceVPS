using MixedSpace;
using MixedSpace.XR;
using TMPro;
using UnityEngine;

namespace MixedSpace.Starter
{
    /// <summary>
    /// 定位计数的只读展示端：自动查找活动 SDK，不参与定位调度或修改计数。
    /// 从程序启动起累计定位结果；运行时恢复或重建不清零显示计数。
    /// 首次手动重新定位冻结原计数，此后只重置第二行的重新定位计数。
    /// Read-only localization-count view that discovers the active SDK without scheduling or changing
    /// source counts. Totals survive runtime recovery and rebuilds. The first manual relocalization freezes the
    /// original row; later resets restart only the relocalization row.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("MixedSpace/UI/Localization Counter View")]
    public sealed class MixedSpaceLocalizationCounterView : MonoBehaviour
    {
        private static ITrackingStatus s_LastStatus;
        private static int s_LastSourceSuccess;
        private static int s_LastSourceAttempts;
        private static int s_TotalSuccess;
        private static int s_TotalAttempts;
        private static bool s_FirstRowFrozen;
        private static int s_FirstRowSuccess;
        private static int s_FirstRowAttempts;

        private MixedSpaceSDK m_Sdk;
        private float m_NextSdkLookup;

        [SerializeField, Tooltip("显示统计的 TMP 文本；未指定时只查找当前物体。\nTMP label for statistics; when unset, search only the current object.")]
        private TMP_Text m_Text;

        private int m_LastSuccess = -1;
        private int m_LastAttempts = -1;
        private bool m_HasRendered;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForNewRun()
        {
            s_LastStatus = null;
            s_LastSourceSuccess = 0;
            s_LastSourceAttempts = 0;
            s_TotalSuccess = 0;
            s_TotalAttempts = 0;
            s_FirstRowFrozen = false;
            s_FirstRowSuccess = 0;
            s_FirstRowAttempts = 0;
        }

        private void Reset()
        {
            m_Text = GetComponent<TMP_Text>();
        }

        private void OnEnable()
        {
            if (m_Text == null) m_Text = GetComponent<TMP_Text>();
            m_HasRendered = false;
            m_NextSdkLookup = 0f;
            Refresh();
        }

        private void LateUpdate()
        {
            Refresh();
        }

        /// <summary>
        /// 首次调用冻结原计数；每次调用均让第二行从零重新计数。
        /// The first call freezes the original counts; every call restarts the second row from
        /// zero.
        /// </summary>
        public static void ResetDisplayedCounts()
        {
            var sdk = UnityEngine.Object.FindAnyObjectByType<MixedSpaceSDK>();
            Accumulate(sdk != null ? sdk.TrackingHealth : null);
            if (!s_FirstRowFrozen)
            {
                s_FirstRowSuccess = s_TotalSuccess;
                s_FirstRowAttempts = s_TotalAttempts;
                s_FirstRowFrozen = true;
            }
            s_TotalSuccess = 0;
            s_TotalAttempts = 0;

            foreach (var view in UnityEngine.Object.FindObjectsOfType<MixedSpaceLocalizationCounterView>(true))
            {
                view.m_HasRendered = false;
                if (view.isActiveAndEnabled) view.Refresh();
            }
        }

        /// <summary>
        /// 可由应用主动调用，也会在启用期间自动刷新。
        /// SDK 尚未初始化时显示本次运行已累计的数值；数值不变时不重新分配文本。
        /// May be refreshed explicitly and updates automatically while enabled. Before SDK initialization
        /// it displays this run's accumulated counts and avoids reassigning unchanged text.
        /// </summary>
        public void Refresh()
        {
            if (m_Text == null) return;
            // 无 SDK 时最多每秒搜索一次，避免每帧遍历场景；暂停时间不影响重试。
            // Without an SDK, search at most once per second to avoid per-frame scene scans; retries use unscaled time.
            if (m_Sdk == null && Time.unscaledTime >= m_NextSdkLookup)
            {
                m_NextSdkLookup = Time.unscaledTime + 1f;
                m_Sdk = UnityEngine.Object.FindAnyObjectByType<MixedSpaceSDK>();
            }
            Accumulate(m_Sdk != null ? m_Sdk.TrackingHealth : null);
            int success = s_TotalSuccess;
            int attempts = s_TotalAttempts;
            if (m_HasRendered && success == m_LastSuccess && attempts == m_LastAttempts) return;

            // 使用英文避免依赖 TMP 字体是否包含中文字形；顺序为成功次数 / 尝试次数。
            // Use English to avoid requiring Chinese glyphs in the TMP font; display successes / attempts.
            m_Text.text = s_FirstRowFrozen
                ? $"Localization: {s_FirstRowSuccess} / {s_FirstRowAttempts}\nRelocalization: {success} / {attempts}"
                : $"Localization: {success} / {attempts}";
            if (s_FirstRowFrozen)
                m_Text.rectTransform.SetSizeWithCurrentAnchors(
                    RectTransform.Axis.Vertical,
                    Mathf.Max(m_Text.rectTransform.rect.height, m_Text.preferredHeight));
            m_LastSuccess = success;
            m_LastAttempts = attempts;
            m_HasRendered = true;
        }

        private static void Accumulate(ITrackingStatus statistics)
        {
            if (statistics == null) return;
            if (!ReferenceEquals(statistics, s_LastStatus))
            {
                s_LastStatus = statistics;
                s_LastSourceSuccess = 0;
                s_LastSourceAttempts = 0;
            }
            int sourceSuccess = statistics.LocalizationSuccessCount;
            int sourceAttempts = statistics.LocalizationAttemptCount;
            s_TotalSuccess = AddSaturating(s_TotalSuccess,
                sourceSuccess >= s_LastSourceSuccess
                    ? sourceSuccess - s_LastSourceSuccess : sourceSuccess);
            s_TotalAttempts = AddSaturating(s_TotalAttempts,
                sourceAttempts >= s_LastSourceAttempts
                    ? sourceAttempts - s_LastSourceAttempts : sourceAttempts);
            s_LastSourceSuccess = sourceSuccess;
            s_LastSourceAttempts = sourceAttempts;
        }

        private static int AddSaturating(int total, int increment) =>
            increment <= 0 ? total : total > int.MaxValue - increment
                ? int.MaxValue : total + increment;
    }
}
