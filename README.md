# AquaDesk（.NET Framework 4.8 版）

这是 AquaDesk 的 **net48 分支**：基于 .NET 8 源码改到 .NET Framework 4.8（运行时由系统提供、进程间共享，内存与磁盘占用更低）。

## net48 版下载入口

- 发布列表：`https://github.com/ShiraiYuzen/AquaDesk/releases`（找 tag 以 `-net48` 结尾的发布，如 `v0.6.0-net48`）
- 解压 `AquaDesk-net48-<版本>.zip` 后双击 `AquaDesk.exe` 即可运行；目标机需已安装 **.NET Framework 4.8**。
- 应用内「检查更新 / 一键更新」会自动认 net48 发布线（tag 以 `-net48` 结尾），不会误拉 .NET 8 的包。

> ⚠️ 这是 **net48 实验分支**。稳定的正式版（.NET 8 自包含）请去仓库 `main` 分支 / 正式 Release。
> 源码与 .NET 8 主线分线维护，本分支只收 net48 相关改动。
