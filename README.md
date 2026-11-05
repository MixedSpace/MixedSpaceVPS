# MixedSpaceVPS

面向 Unity 空间增强现实应用的视觉定位（VPS）SDK，提供云端与设备端定位、地图加载与管理、位姿融合和诊断日志。

- 包标识：`com.ningfeng.mixedspace`
- 版本：`2.0.0`
- 最低 Unity 版本：`2022.3`（按包配置声明）

## 安装

### Git URL

在 Unity 的 Package Manager 中选择 **Add package from git URL**，输入：

```text
https://github.com/MixedSpace/MixedSpaceVPS.git#v2.0.0
```

此地址在 GitHub 的 `v2.0.0` 标签发布后可用。

### OpenUPM

OpenUPM 收录且 `2.0.0` 构建完成后，在 **Project Settings > Package Manager** 添加 Scoped Registry：

- Name：`OpenUPM`
- URL：`https://package.openupm.com`
- Scope：`com.ningfeng.mixedspace`

然后在 Package Manager 中选择 **Add package by name**：

- Name：`com.ningfeng.mixedspace`
- Version：`2.0.0`

添加 Registry 时请保留项目原有的 Registry 配置。

## 使用说明与示例

运行时策略由 `MixedSpaceRunPlan` 配置，平台能力通过适配器接入。

详细使用说明见 [混空VPS v2.0.0.docx](<混空VPS v2.0.0.docx>)。
本包提供 `RokidAdapter` 示例，可在 Package Manager 的 Samples 中导入。

## 许可证

除第三方组件另有声明外，本项目采用 [Apache License 2.0](LICENSE)。
第三方组件的许可证与版权声明见 [Third Party Notices.md](<Third Party Notices.md>)。
