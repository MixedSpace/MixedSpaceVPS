using System;
using UnityEngine;

namespace MixedSpace
{
    /// <summary>
    /// 平台扩展包在 SDK 验证和初始化前注册服务的基类。Android、iOS 和 HarmonyOS 派生类在 RegisterServices 中同步调用各 Set 接口；较早执行顺序使公共 SDK
    /// 入口无需硬编码平台分支。
    /// Base component for platform-specific packages that must register adapters
    /// before MixedSpaceSDK begins token validation and initialization.
    /// 
    /// Android, iOS and HarmonyOS packages derive from this type and call the SDK's
    /// SetPlatformInfo, SetNativeCore, SetPoseFusionFactory, SetNetworkEnvironment,
    /// SetHttpTransport or SetMapRepository methods from RegisterServices. The early execution order
    /// avoids platform checks and conditional-compilation branches in the public
    /// SDK facade.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public abstract class MixedSpacePlatformBootstrap : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("可选的显式 SDK 引用；为空时自动查找场景中的 SDK。\nOptional explicit SDK reference. The scene SDK is discovered automatically when empty.")]
        private MixedSpaceSDK m_Sdk;

        protected MixedSpaceSDK Sdk { get; private set; }

        private void Awake()
        {
            Sdk = m_Sdk != null ? m_Sdk : FindObjectOfType<MixedSpaceSDK>();
            if (Sdk == null)
            {
                MixedSpaceLogger.LogError($"{GetType().Name} could not find MixedSpaceSDK in the active scene.");
                enabled = false;
                return;
            }

            try
            {
                RegisterServices(Sdk);
            }
            catch (Exception e)
            {
                MixedSpaceLogger.LogError($"{GetType().Name} platform registration failed: {e.Message}");
                Debug.LogException(e);
                enabled = false;
            }
        }

        /// <summary>
        /// 同步注册平台需要的全部适配器；异步平台初始化交给 IMixedSpacePlatformAdapter。
        /// Registers all adapters required by this platform. Implementations must be
        /// synchronous; asynchronous platform setup belongs in IMixedSpacePlatformAdapter.
        /// </summary>
        protected abstract void RegisterServices(MixedSpaceSDK sdk);
    }
}
