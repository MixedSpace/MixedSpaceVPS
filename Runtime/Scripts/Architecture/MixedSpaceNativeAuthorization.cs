


namespace MixedSpace
{
    /// <summary>
    /// 通过 MixedSpaceXrLocNative 选择授权服务器并解释验证返回码。
    /// Selects the authorization server and interprets validation status through MixedSpaceXrLocNative.
    /// </summary>
    internal static class MixedSpaceNativeAuthorization
    {
        /// <summary>
        /// 仅原生状态 0 表示验证成功，库加载失败交给运行时诊断。验证前重新应用 Inspector 服务器选择，覆盖 SDK 尚未初始化的编辑器云绑定流程。
        /// Returns true only for native status zero; loading failures propagate to runtime diagnostics.
        /// The xrloc server profile is re-asserted from the Inspector selection before verifying:
        /// the Editor cloud binding path can run before SDK initialization, so startup-time
        /// configuration alone cannot cover it.
        /// </summary>
        internal static bool Validate(string developerToken, int xrLocServerId)
        {
            int serverStatus = MixedSpaceXrLocNative.SetServer(xrLocServerId);
            if (serverStatus != 0)
                MixedSpaceLogger.LogWarning($"XrLoc server selection rejected: status={serverStatus}");
            int status = MixedSpaceXrLocNative.VerifyDeveloperToken(developerToken);
            if (status == 0) return true;
            MixedSpaceLogger.LogError($"Native token verification failed: status={status}");
            return false;
        }
    }
}
