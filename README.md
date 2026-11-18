# MixedSpaceVPS

面向 Unity 空间增强现实应用的视觉定位（VPS）SDK，提供云端与设备端定位、地图加载与管理、位姿融合和诊断日志。

- 包标识：`com.njyjxr.mixedspace`
- 版本：`2.0.0`
- 发布分支：`release/2.0.0-20261118`
- 最低 Unity 版本：`2022.3`（按包配置声明）

## 安装

### Git URL

在 Unity 的 Package Manager 中选择 **Add package from git URL**，输入：

```text
https://github.com/MixedSpace/MixedSpaceVPS.git#release/v2.0.0
```

此地址在 GitHub 的 `release/v2.0.0` 标签发布后可用。

### OpenUPM

OpenUPM 收录且 `2.0.0` 构建完成后，在 **Project Settings > Package Manager** 添加 Scoped Registry：

- Name：`OpenUPM`
- URL：`https://package.openupm.com`
- Scope：`com.njyjxr.mixedspace`

然后在 Package Manager 中选择 **Add package by name**：

- Name：`com.njyjxr.mixedspace`
- Version：`2.0.0`

添加 Registry 时请保留项目原有的 Registry 配置。

## 使用说明与示例

运行时策略由 `MixedSpaceRunPlan` 配置，平台能力通过适配器接入。

历史版本使用说明见 [混空VPS v2.0.0.docx](<混空VPS v2.0.0.docx>)，本次适配器配置请以随包示例及其说明为准。
本包提供以下示例，可在 Package Manager 的 Samples 中分别导入：

- `XREALAdapter`：XREAL Aura Android XR Camera 0 与 OpenXR 位姿适配器，包含连续定位示例。使用前需配置标定、相机外参及近似采帧时序；不包含录制与诊断试验工具。
- `MobileAdapter`：移动端接入示例，用于参考 MixedSpace SDK 的场景配置和平台适配方式。
- `Pico4UAdapter`：PICO 4 Ultra 相机适配器与示例场景，通过 PXR_CameraImage 获取图像并接入跟踪位姿。导入前需安装提供对应相机 API 的 PICO Unity SDK。
- `Pico4UEAdapter`：PICO 4 Ultra Enterprise 企业相机适配器与示例场景，接入相机图像和跟踪位姿。导入前需安装兼容企业相机 API 的 PICO Unity Integration SDK。
- `Pico4EAdapter`：PICO 4 Enterprise VST 相机适配器与示例场景。导入前需安装对应的 PICO 厂商 SDK。
- `HuaweiAdapter`：Android 平台 Huawei AR Engine 相机适配器与示例，包含配套的厂商 Unity SDK 文件。
- `XRanyAdapter`：XRany GxrSdk 左侧鱼眼相机适配器与示例场景。导入前需安装 GxrSdk。
- `RokidAdapter`：Rokid 设备平台适配器与示例，用于将相机图像和跟踪位姿接入 MixedSpace SDK。
- `XVXRAdapter`：XVXR 设备平台适配器与示例，用于将相机图像和跟踪位姿接入 MixedSpace SDK。
- `QuestAdapter`：Quest 3 / Quest 3S 相机与 VPS 定位示例。通过 Quest 设置与依赖检查的 Fix 或 Fix All 安装缺少的 Meta 和 Unity 依赖，并配置 TMP 与 Android。

## 许可证

除第三方组件另有声明外，本项目采用 [Apache License 2.0](LICENSE)。
第三方组件的许可证与版权声明见 [Third Party Notices.md](<Third Party Notices.md>)。

## 从旧版迁移

本次源码替换包含接口变化，例如移除 `MixedSpaceNativeCore`、`MixedSpaceHybridNativeCore` 和 `MixedSpaceSDK.RefreshLicenseAsync()`；使用这些接口的应用需要同步迁移。

同路径资源保留已发布版本的 GUID，包内示例引用已同步映射。旧的 `Runtime/Prefabs/MixedSpaceRunPlan.asset` 保留供现有项目引用。

OpenUPM 中已发布的旧 `2.0.0` 不会因 GitHub 分支或标签变化而更新。旧包撤回及同版本重新发布须经 OpenUPM 维护者确认，完成前请使用本页的 Git URL 安装。
