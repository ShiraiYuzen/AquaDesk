# AquaDesk v0.3.3

> 发布：2026-10-31 · 产物 `AquaDesk-0.3.3.exe`（69.5 MB，自包含单文件）· **当前最新**

## 一句话

修掉一个藏了很久的坑：设置里关掉的开机自启，其实从来没被真正关掉。

## 本版更新

### 修复

**关掉开机自启后仍然每次开机自启。**

根因在注册表值名上：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 里这一项叫 **`!AquaDesk`**
（带感叹号），是早期版本尝试「用前缀插队启动」时留下的。后来发现这条路根本无效
（注册表值枚举顺序不是按名字排的，加感叹号反而排到最后），名字改回 `AquaDesk`，
但旧值没人清理。而代码里：

- `RemoveRunValue()` 只删 `AquaDesk` → `!AquaDesk` 永远删不掉；
- `HasRunValue()` 只读 `AquaDesk` → `IsEnabled()` 返回 false，**程序自己也认为自启是关的**；
- `LegacyValueName` 还被误写成与 `ValueName` 相同的值（复制粘贴失误），等于「清理老名字」这段逻辑一直在空转。

于是形成死循环：设置里 off、界面 off、程序认为 off，Windows 每次开机照 `!AquaDesk` 拉起。

**修法**：Run 键处理不再写死名字，改成按名字模糊匹配 ——
任何含 `aquadesk` 的值名都算自己的，读、写、删三处统一走这条规则。
启动时若设置是「关」，会自动清掉任何残留项。

### 验证

1. 手工删除机器上的 `!AquaDesk`，确认无其他来源（Startup 文件夹空、无 AquaDesk 计划任务、
   RunOnce 空、任务管理器 StartupApproved 里也没有它 —— 说明不是被禁用，而是从来没被删过）。
2. 故意再写一条 `!AquaDesk` 进注册表 → 重启程序 → 10 秒内被自动清理干净。

## 安装

双击 `AquaDesk-0.3.3.exe`。已经装过旧版本的直接双击即可，**新版本会接管正在运行的旧进程**。

## 已知问题

- 内存仍在 300 MB 量级（WPF + CsWinRT + 自包含运行时的固有成本）。
- 提权窗口（任务管理器）预览仍抓不到图。
- 任务栏靠 autohide 释放工作区，鼠标碰底边会弹一下。

## 升级自

v0.3.2。配置完全兼容。如果之前关过自启但没生效，升级后第一次启动会自动清掉残留项，
不需要手动改注册表。

## 本版文件

| 文件 | 说明 |
| --- | --- |
| `src/` | **完整源码**（.NET 8 + WPF，58 个文件），可构建 |
| `README.md` | 本版发布说明，可直接作为 GitHub Release 正文 |

- **exe 不在仓库里**：见 [Releases](../../releases)，文件名 `AquaDesk-0.3.3.exe`（69.5 MB）。
- 校验用 SHA-256（前 32 位）：`a9c44183e3787fd03c46809156cc847f`
- 这是**唯一保留了源码的版本**。更早的版本只有 exe（在 Releases）和发布说明。

## 构建

```powershell
cd src
# 1. 改 AquaDesk.App/AquaDesk.App.csproj 里的四个版本字段
#    Version=0.3.3 / AssemblyVersion=0.3.3.0 / FileVersion=0.3.3.0 / InformationalVersion=0.3.3
# 2. 打包
.\build.ps1
```

> 不要用 `build.ps1 -Version`：它用 `Set-Content` 覆写 csproj，在受限环境里会被安全策略拦下。
