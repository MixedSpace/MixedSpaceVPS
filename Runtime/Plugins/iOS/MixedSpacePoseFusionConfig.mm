// Optional configuration entry point: older iOS payloads do not export PoseFusionSetConfig.
// The opaque config pointer refers to the managed 20-byte PoseFusionConfig layout.
extern "C" int PoseFusionSetConfig(void *pose_fusion, const void *config) __attribute__((weak_import));
extern "C" int MixedSpacePoseFusionSetConfigIfAvailable(void *pose_fusion, const void *config)
{
    if (!PoseFusionSetConfig) return -1001;
    return PoseFusionSetConfig(pose_fusion, config);
}
