# XREAL Android XR 适配器

采集 Aura Camera 0 图像，结合 OpenXR 位姿接入 MixedSpace SDK。

## 文件

- `MixedSpaceXrealAdapter`：相机权限、图像采集、位姿采样与相机生命周期。
- `MixedSpaceXrealPinholeRectifier`：可选的针孔去畸变。
- `XREALSample`：示例场景。
- `XREALRunPlan`：自动初始化与连续定位配置。

## 配置

1. 使用 Unity 6，导入 XREALAdapter 示例。
2. 打开 `MixedSpaceSDK/Adapters/XREAL 设置与依赖检查`，安装依赖并处理未通过项。
3. 在场景中配置 SDK Token、网络和地图。SDK 通过 RunPlan 自动启动。
4. 在适配器上配置相机外参、图像模型和采帧位姿选项。

手动 XR 检查和示例场景入口位于检测窗口右上角菜单。

## 相机参数

Camera 0 输出为 1920×1080，默认内参为：

- fx = 1280
- fy = 1280
- cx = 961.33
- cy = 536

内参采用左上角像素坐标。改变输出分辨率或裁剪后需重新配置。
`LensDistortion` 顺序为 k1、k2、p1、p2、k3；已校正图像留空。
`LensPositionInHead` 和 `LensRotationInHead` 配置镜头相对跟踪原点的变换。
`RenderedCamera` 使用 Main Camera 世界位姿。

## 采集与诊断

WebCamTexture 采帧使用读图时刻的近似位姿，不提供曝光时间戳。
定位日志由 SDK RunPlan 的诊断选项控制。
