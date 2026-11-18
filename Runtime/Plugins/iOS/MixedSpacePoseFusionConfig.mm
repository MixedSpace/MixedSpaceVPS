// 弱引用兼容缺少可选入口的 iOS 静态库；托管端按版本选择配置结构布局。
// Weak imports allow iOS static libraries without these optional symbols. Managed code selects the config layout by version.
extern "C" int PoseFusionGetVersion(void *version) __attribute__((weak_import));
extern "C" int PoseFusionSetConfig(void *pose_fusion, const void *config) __attribute__((weak_import));

extern "C" int MixedSpacePoseFusionGetVersionIfAvailable(void *version)
{
    if (!PoseFusionGetVersion) return -1001;
    return PoseFusionGetVersion(version);
}

extern "C" int MixedSpacePoseFusionSetConfigIfAvailable(void *pose_fusion, const void *config)
{
    if (!PoseFusionSetConfig) return -1001;
    return PoseFusionSetConfig(pose_fusion, config);
}
