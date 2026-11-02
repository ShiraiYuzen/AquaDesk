# AquaDesk（.NET Framework 4.8 版）

这是 AquaDesk 的 **net48 分支**：基于 .NET 8 源码改到 .NET Framework 4.8（运行时由系统提供、进程间共享，内存与磁盘占用更低）。

![Branch](https://img.shields.io/badge/branch-net48-orange)
![Release](https://img.shields.io/badge/release-v0.6.1--net48-blue)
![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6)
![.NET](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4)
![Size](https://img.shields.io/badge/zip-~0.7%20MB-informational)

## net48 版下载入口

- 发布列表：`https://github.com/ShiraiYuzen/AquaDesk/releases`（找 tag 以 `-net48` 结尾的发布，如 `v0.6.1-net48`）
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
| 状态 | 实验分支，只收 net48 相关改动 | 正式主线 |

## 致谢

- 一键自更新与 net48 兼容层（NetFxShims）参考自 **GlassBoard-Source**（克隆其仓库作为实现参考），感谢原作者。
- 感谢 .NET / WPF 社区的各项开源资料与示例。

> ⚠️ 这是 **net48 实验分支**。稳定的正式版（.NET 8 自包含）请去仓库 `main` 分支 / 正式 Release。
> 源码与 .NET 8 主线分线维护，本分支只收 net48 相关改动。
