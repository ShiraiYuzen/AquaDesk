# AquaDesk v0.4.9

> 发布：2026-10-31 · 产物 `AquaDesk-0.4.9.exe`（69.6 MB，自包含单文件）· 后续版本见 [v0.4.11](../v0.4.11/README.md)

## 一句话

两个入口（设置 / 电源）在 Dock 上关不掉、切英文键盘后胶囊图标缩成 8 个还不对齐，
两个都是「入口开关和胶囊显隐」没对齐的问题，一次修掉。

## 修了什么

### 入口开关打开了会往 Dock 上再冒一颗

`ShowBrandIcon` / `ShowWindowsIcon` 原本管的是 Dock 上的显隐，可这两颗 0.3.11 就搬到
状态胶囊上了 —— 打开开关等于同一个功能两处都有。

现在 `DockViewModel.IsSystemEnabled` 对 Brand / Windows 恒返回 false（Dock 上彻底不摆），
开关改为控制**胶囊上那一格**显不显示，默认开。

配置迁移 `SettingsVersion = 3`：上一轮迁移把它们置成过 false，不迁移的话升级后胶囊上
会既没有设置、也没有电源入口。

### 切到英文键盘后胶囊只剩「8 个」且不对齐

`InputState.GetBadge()` 在英文键盘下返回 null，原来的代码只把格子里那行文字 `Collapsed`，
`InputArea` 本身还占着格子 —— 可见项从 9 变 8，布局却还是 5 + 4，下排空出一格。

现在连 `InputArea` 一起收，并重算分行：9 个是 5 + 4，8 个是 4 + 4。

### 设置页文案

「固定入口」改名「胶囊入口」，两行说明都写明「这个开关管的是胶囊」，
并提示关掉后右键 Dock 玻璃条空白处仍能进设置。

## 升级注意

`SettingsVersion` 升到 3，自动迁移，不用手工改配置。

## 文件

- 产物：`AquaDesk-0.4.9.exe`（69.6 MB）
- 源码：`versions/v0.4.9/src/`（完整工程，可直接 `dotnet build`）
- 变更历史：[CHANGELOG.md](../../CHANGELOG.md)
