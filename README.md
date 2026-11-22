# DockSprig（.NET Framework 4.8 版）

> **原名 AquaDesk。** 历史发布的 ZIP、启动文件和标签沿用原有名称。

这是 DockSprig 的 **net48 分支**：基于 .NET 8 源码改到 .NET Framework 4.8（运行时由系统提供、进程间共享，内存与磁盘占用更低）。

![Branch](https://img.shields.io/badge/branch-net48-orange)
![Version](https://img.shields.io/badge/source-v0.6.2--net48-blue)
![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6)
![.NET](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4)
![Size](https://img.shields.io/badge/zip-~0.7%20MB-informational)

## 效果图

在相同主题和演示数据下，`net48` 的 Dock、时间胶囊、状态胶囊与启动台画面和 `main` 一致。查看 [主分支效果图](https://github.com/ShiraiYuzen/DockSprig/blob/main/README.md#效果图)。

## 0.6.2 · 透明玻璃界面

- Dock 和状态胶囊保留透出壁纸的玻璃底板，用细亮边模拟玻璃厚度，底板透明度仍可调。
- 前台应用以水蓝短线标记，音量、网络、电源和输入法各用固定位置。
- 启动台按可用空间调整行列和分页，应用名称支持两行，键盘焦点有描边。
- 设置页保留 Windows 原生标题栏与左侧分类。「外观」页可以即时预览玻璃透明度与圆角。
- 后台进程扫描移出 UI 线程。关闭窗口预览后停止枚举，暂停时不再强制 GC 或裁剪工作集。

## net48 版下载入口

下载前可查看 [Code signing policy](https://github.com/ShiraiYuzen/DockSprig#code-signing-policy) 和 [Privacy policy](https://github.com/ShiraiYuzen/DockSprig#privacy-policy)。

- 发布列表：`https://github.com/ShiraiYuzen/DockSprig/releases`（找 tag 以 `-net48` 结尾的发布，如 `v0.6.1-net48`）
- 解压 `AquaDesk-net48-<版本>.zip` 后双击 `AquaDesk.exe` 即可运行；目标机需已安装 **.NET Framework 4.8**。
- 应用内「检查更新 / 一键更新」会自动认 net48 发布线（tag 以 `-net48` 结尾），不会误拉 .NET 8 的包。

## 与 main 分支的区别

| | net48 分支（本分支） | main 分支（.NET 8） |
| --- | --- | --- |
| 运行时 | .NET Framework 4.8（系统自带、进程间共享） | .NET 8 自包含单文件（打包运行时） |
| 内存占用 | 稳定约 212 MB | ~300+ MB |
| 安装包 | ~0.7 MB 的 zip（目标机需装 .NET 4.8） | ~70 MB 单文件 exe（无需预装运行时） |
| 更新方式 | 应用内「一键更新」（下载 zip → 原地覆盖 → 自动重启）或去 Releases 手动下载 | Velopack 安装版 + 应用内自动更新 |
| 适合场景 | 想显著降低内存 / 磁盘占用的机器 | 追求开箱即用、自动更新、免装运行时 |
| 状态 | 实验分支，同步共用界面与性能改动 | 正式主线 |

## 许可证

[MIT License](LICENSE)

Copyright © 2026 ShiraiYuzen。此许可涵盖由 ShiraiYuzen 拥有版权的项目源码。第三方材料适用各自的许可。

`src/AquaDesk.App/Services/UpdateService.cs` 中源自 [GlassBoard-Source](https://github.com/llm147369/GlassBoard-Source) 的代码，如有，适用其权利人授予的许可。上游未明确授权的部分不包含在本项目的 MIT 授权中。

## 致谢

- 一键自更新与 net48 兼容层（NetFxShims）参考自 **GlassBoard-Source**（克隆其仓库作为实现参考），感谢原作者。
- 感谢 .NET / WPF 社区的各项开源资料与示例。

> ⚠️ 这是 **net48 实验分支**。稳定的正式版（.NET 8 自包含）请去仓库 `main` 分支 / 正式 Release。
> 两个分支同步界面与性能改动，分别维护各自的运行时与更新方式。
