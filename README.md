# AquaDesk · 水光桌面

> 把 macOS 的桌面质感搬到 Windows：Dock、顶部菜单栏、液态玻璃、圆角、启动台。
> 不动系统底层，只换你看到的那一层。

**当前版本：0.3.3** · 单文件 exe，自包含，约 69.5 MB · 目标机器不用装 .NET

---

## 它是什么

Windows 的原生桌面已经很好用，但视觉语言停在另一个时代。AquaDesk 不做换壁纸那种表面功夫，
而是把「你看到的那一层」整个换掉：

- 底部 **Dock**：磁贴化图标、悬停放大 + 邻居让位、运行指示条、窗口预览、文件夹、拖拽排序
- 顶部 **菜单栏**：前台应用名、时钟、电量、WiFi、蓝牙、音量、输入法
- **液态玻璃**：八层光学叠加（tint、聚光、亮边、指针高光、反光、光泽、玻璃边、内阴影）
- **启动台**：全屏网格、搜索、分页、文件夹、拖入
- **系统集成**：开机自启、隐藏原生任务栏、隐藏桌面图标

资源管理器、程序、驱动全都照旧，只是外壳变成了 mac 的样子。

## 版本索引

每个版本一个文件夹，内含该版本的完整变更说明，可直接作为 GitHub Release 正文。

| 版本 | 日期 | 主题 | 说明 |
| --- | --- | --- | --- |
| [v0.0.1](versions/v0.0.1/README.md) | 2026-10-20 | 首个可分发包 | Dock + 液态玻璃 + 设置页 |
| [v0.2.0](versions/v0.2.0/README.md) | 2026-10-21 | 功能成型 | 启动台、菜单栏、废纸篓、系统集成 |
| [v0.2.1](versions/v0.2.1/README.md) | 2026-10-21 | 系统面板 | WiFi / 蓝牙 / 音量 / 输入法 |
| [v0.2.2](versions/v0.2.2/README.md) | 2026-10-26 | Dock 文件夹 | 我的电脑、文件夹、设置页红绿灯 |
| [v0.2.3](versions/v0.2.3/README.md) | 2026-10-26 | 窗口预览 | Dock 里直接看窗口缩略图 |
| [v0.3.0](versions/v0.3.0/README.md) | 2026-10-26 | 预览与布局 | 预览修复、可拖系统图标、AppBar 占位 |
| [v0.3.1](versions/v0.3.1/README.md) | 2026-10-26 | 四边停靠 | 上下左右都能停，托盘唤起修复 |
| [v0.3.2](versions/v0.3.2/README.md) | 2026-10-26 | 缩放与聚焦 | 界面缩放、屏幕适配、扫描收紧 |
| [v0.3.3](versions/v0.3.3/README.md) | 2026-10-31 | 自启修复 | 关不掉的开机自启终于关掉了 |

完整变更历史见 [CHANGELOG.md](CHANGELOG.md)。

**打包好的 exe 不在仓库里**，统一放在 [Releases](../../releases) 页面 ——
单文件 69.5 MB，8 个版本塞进仓库会超过 550 MB，`clone` 会很难受。

| 版本 | exe | 源码 |
| --- | --- | --- |
| v0.0.1 | ✗ 已被覆盖删除，Release 里也没有 | ✗ 未归档 |
| v0.2.0 ~ v0.3.2 | ✓ 见 Releases | ✗ 未归档 |
| v0.3.3 | ✓ 见 Releases | ✓ 完整源码（`versions/v0.3.3/src/`） |

> **为什么旧版本没有源码**：项目从第一行代码到 0.3.2 一直没有用版本控制，
> 源码只有一份持续演进的副本，旧版本构建完就被覆盖了。
> 试过从单文件 exe 反编译还原：C# 能出来，但 **XAML 全部丢失**（BAML 无法还原成可编译的标记），
> 得到的工程编不过，所以没有放进来。
> 从 0.3.3 起 `build.ps1` 会在打包时自动生成 `releases\AquaDesk-<版本>-src.zip`，不会再丢。

## 快速开始

### 直接运行

到 [Releases](../../releases) 下载 `AquaDesk-<版本>.exe`，双击即可。
单文件自包含，不需要安装 .NET。想直接用最新版就挑最新的那个 Release。

重复启动 = **新版本接管**：双击任意一个 exe，正在跑的旧版本会自动退出、新版本接手。
所以升级时不用先去托盘里找「退出」。

### 从源码构建

需要 .NET 8 SDK：

```powershell
# 修改 csproj 里的 Version / AssemblyVersion / FileVersion / InformationalVersion
# 注意：AssemblyVersion 与 FileVersion 必须是纯数字四段（0.3.3 → 0.3.3.0）
.\build.ps1
```

产物在 `dist\AquaDesk-<版本>.exe`，旧版本自动归档到 `releases\`。

> `build.ps1 -Version x.y.z` 也会顺手改版本号，但它用 `Set-Content` 覆写 csproj，
> 在受限环境里可能被安全策略拦下 —— 手动改 csproj 再跑无参脚本最稳。

### 调试

```powershell
dotnet run --project src\AquaDesk.App    # 直接跑
AquaDesk.exe --settings                  # 启动即弹设置页
AquaDesk.exe --restore                   # 强制恢复被隐藏的任务栏（进程被强杀后用）
```

日志在 `%TEMP%\AquaDesk-crash.log`。

## 目录结构

```
.
├─ README.md                 本文件
├─ CHANGELOG.md              全版本变更历史
├─ build.ps1                 打包脚本（会自动归档源码快照）
├─ docs/
│  ├─ 01-产品构想.md          产品定位与功能分层
│  └─ 02-架构决策.md          ADR-001~010，改视觉/交互前先读
├─ src/AquaDesk.App/          WPF 源码（.NET 8，0.3.3）
└─ versions/
   ├─ v0.0.1/README.md
   ├─ v0.2.0/README.md       发布说明（exe 见 Releases）
   ├─ ...
   └─ v0.3.3/
      ├─ src/                该版本的完整源码
      └─ README.md
```

仓库本体约 1 MB，全是文本和源码；二进制走 Releases。

## 发布新版本

打包后把 exe 传到 Releases，别提交进仓库：

```bash
# 用 gh CLI（推荐）
gh release create v0.3.3 \
  dist/AquaDesk-0.3.3.exe \
  --title "v0.3.3 · 自启修复" \
  --notes-file versions/v0.3.3/README.md
```

网页端也行：仓库 → Releases → 起草新版本 → 标签填 `v0.3.3` → 上传 exe →
正文直接粘 `versions/v0.3.3/README.md` 的内容。

一个 Release 可以同时挂多个版本的文件，但建议一版一个 Release，标签与版本号对齐。


## 技术栈

| 层 | 选择 | 理由 |
| --- | --- | --- |
| 运行时 | .NET 8 + WPF | 单文件发布 69.5 MB，目标机器零依赖 |
| 玻璃 | 自绘八层 + 屏幕采样折射 | WPF 没有真折射，自己采样桌面再模糊 |
| 系统集成 | Win32 P/Invoke + WinRT | 任务栏、AppBar、Radio Manager 都要原生 |
| 图标 | Shell ImageList + 像素分析 | 避免每次取图驻留 240KB |

### 已知取舍

- **内存偏重**：WPF + CsWinRT 投影 + 自包含运行时，工作集约 325 MB、私有约 200 MB。
  同功能原生 Win32 版实测只要 21 MB —— 但那个版本维护成本高，已停。
- **窗口预览是静态抓图**：用 `PrintWindow` 而不是 DWM 实时缩略图，
  因为 Dock 是透明窗口，DWM 缩略图子窗口会被画成不透明方块。
- **提权窗口抓不到**：任务管理器这类进程受 UIPI 保护，预览退回应用图标、关闭菜单置灰。
- **任务栏只能靠 autohide 释放那 48px**：`SPI_SETWORKAREA` 会被 Shell 立刻覆盖回去，
  代价是鼠标碰底边任务栏会弹一下。

## 许可证

本项目尚未指定开源许可证。在添加 LICENSE 文件之前，默认保留所有权利。
如果打算公开分发，建议补一份（MIT 或 Apache-2.0 都适合这类工具）。

## 致谢

界面语言参考 macOS Aqua 设计语言。所有图标均为矢量自绘或取自系统，不含第三方素材。
