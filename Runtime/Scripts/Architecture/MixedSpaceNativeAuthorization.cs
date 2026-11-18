namespace MixedSpace
{
    /// <summary>
    /// 通过 MixedSpaceXrLocNative 选择授权服务器并解释验证返回码。
    /// Selects the authorization server and interprets validation status through MixedSpaceXrLocNative.
    /// </summary>
    internal static class MixedSpaceNativeAuthorization
    {
        /// <summary>
        /// 仅原生状态 0 表示验证成功。验证前配置正式服务器，库加载失败交给运行时诊断。
        /// Returns true only for native status zero; loading failures propagate to runtime diagnostics.
        /// The production server is selected before validation, including editor map operations.
        /// </summary>
        internal static bool Validate(string developerToken)
        {
            if (string.IsNullOrWhiteSpace(developerToken))
            {
                MixedSpaceLogger.LogError("[TOKEN_MISSING] Token 验证失败：未配置 Developer Token。");
                return false;
            }
            int serverStatus = MixedSpaceXrLocNative.SetServer();
            if (serverStatus != 0)
                MixedSpaceLogger.LogWarning($"XrLoc server selection rejected: status={serverStatus}");
            int status = MixedSpaceXrLocNative.VerifyDeveloperToken(developerToken);
            if (status == 0) return true;
            MixedSpaceLogger.LogError($"[TOKEN_VALIDATION_FAILED] Token 验证失败：原生返回 status={status}，请检查 Token 和网络连接。");
            return false;
        }
    }
}
