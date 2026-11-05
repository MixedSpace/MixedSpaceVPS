using UnityEngine;
using UnityEngine.Events;

namespace MixedSpace
{
    /// <summary>
    /// 场景持有的应用输出事件，与可复用运行策略分开保存，UnityEvent 目标留在场景中。
    /// UI 输入由应用控制器显式派发会话命令；本组件只保存事件，不处理输入。
    /// SDK 显式绑定自身作为所有者，本组件不遍历场景查找 SDK。
    /// Scene-owned application output events remain separate from reusable runtime policies. UI
    /// controllers dispatch commands; this component stores events only. The SDK explicitly binds itself as owner,
    /// without scene-wide discovery.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("MixedSpace/Application Binding")]
    public sealed class MixedSpaceApplicationBinding : MonoBehaviour
    {
        [SerializeField] private MixedSpaceApplicationSignals m_ApplicationSignals = new MixedSpaceApplicationSignals();
        [SerializeField, Tooltip("SDK 运行时初始化完成，可调用定位控制方法；不代表定位成功。\nSDK initialization completed; localization controls are available. This does not mean localization succeeded.")]
        private UnityEvent m_RuntimeReady = new UnityEvent();
        [SerializeField, Tooltip("许可等级刷新完成，不是定位成功通知。\nEntitlement refresh completed; this is not a localization success notification.")]
        private UnityEvent m_EntitlementResolved = new UnityEvent();
        [SerializeField, Tooltip("运行时重建流程通知，不等于会话重置完成；就绪请监听 Runtime Ready。\nRuntime rebuild notification, not session reset completion. Listen to Runtime Ready for readiness.")]
        private UnityEvent m_RuntimeRebuilt = new UnityEvent();
        private MixedSpaceSDK m_Owner;

        /// <summary>
        /// 定位、会话和跟踪事件集合，可在 Inspector 中绑定响应对象。
        /// Localization, session and tracking events whose targets can be assigned in the
        /// Inspector.
        /// </summary>
        public MixedSpaceApplicationSignals Signals => m_ApplicationSignals;
        /// <summary>
        /// 运行时初始化完成事件，不代表定位成功。
        /// Runtime initialization completed; this does not indicate a successful localization.
        /// </summary>
        public UnityEvent Ready { get => m_RuntimeReady; set => m_RuntimeReady = value ?? new UnityEvent(); }
        /// <summary>
        /// 许可等级刷新完成事件。
        /// Raised when entitlement refresh completes.
        /// </summary>
        public UnityEvent Entitlement { get => m_EntitlementResolved; set => m_EntitlementResolved = value ?? new UnityEvent(); }
        /// <summary>
        /// 运行时重建流程通知；会话重置完成使用 LocalizationResetCompleted。
        /// Runtime rebuild notification; use LocalizationResetCompleted for session reset
        /// completion.
        /// </summary>
        public UnityEvent Rebuilt { get => m_RuntimeRebuilt; set => m_RuntimeRebuilt = value ?? new UnityEvent(); }

        internal void Attach(MixedSpaceSDK owner)
        {
            if (m_Owner != null && m_Owner != owner)
                throw new System.InvalidOperationException("An application binding cannot be shared by SDK instances.");
            m_Owner = owner;
        }

        internal void Detach()
        {
            m_Owner = null;
        }

        private void OnDestroy() { Detach(); }
    }
}
