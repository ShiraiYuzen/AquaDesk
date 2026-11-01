# AquaDesk · 水光桌面

> 把 macOS 的桌面质感搬到 Windows：Dock、顶部菜单栏、液态玻璃、圆角、启动台。
> 不动系统底层，只换你看到的那一层。

**当前版本：0.6.2** · 单文件 exe，自包含，约 69.6 MB · 目标机器不用装 .NET

![Release](https://img.shields.io/github/v/release/ShiraiYuzen/AquaDesk?label=release&color=blue)
![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6)
![.NET](https://img.shields.io/badge/.NET-8%20%2B%20WPF-512BD4)
![Size](https://img.shields.io/badge/single--file%20exe-69.6%20MB-informational)
![License](https://img.shields.io/badge/License-MIT-green)

---

## 它是什么

Windows 的原生桌面已经很好用，但视觉语言停在另一个时代。AquaDesk 不做换壁纸那种表面功夫，
而是把「你看到的那一层」整个换掉：

- 底部 **Dock**：磁贴化图标、悬停放大 + 邻居让位、运行指示条、窗口预览、文件夹、拖拽排序
- 顶部 **时间胶囊**：时钟；检测到在放歌时自动拉宽成播放条（播放器图标、曲名歌手、上一首 / 暂停 / 下一首）
- 右侧 **状态胶囊**：设置、电源、后台程序、电量、WiFi、蓝牙、音量、输入法，两排按 27px 列网格排，
  数量变化时自动分行（偶数对半、奇数上多一、下排居中）
- **后台程序面板**：列出所有窗口程序和托盘程序（含当前前台），点一行就切过去（替代系统托盘）
- **窗口层级三档**：最大化时让开 / 始终最上层（不含全屏应用）/ 始终最下层
- **液态玻璃**：八层光学叠加（tint、聚光、亮边、反射、光泽、玻璃边、内阴影）；
  不再抓屏折射 —— 那样会把自己从任何截图和录屏里排除掉
- **启动台**：全屏网格、搜索、分页、文件夹、拖入
- **系统集成**：开机自启、隐藏原生任务栏、隐藏桌面图标
- **设置页**：五类分栏；选项实时生效，改完按「保存」落盘（关窗兜底存一次）

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
| [v0.4.3](versions/v0.4.3/README.md) | 2026-10-31 | 胶囊重构 | 折射下线、托盘删除、正在播放、图标重做（含 0.3.4~0.4.3 十二版） |
| [v0.4.8](versions/v0.4.8/README.md) | 2026-10-31 | 设置页换框架 | 保存按钮、多选一改下拉框、Dock 让开提速到 ~96ms |
| [v0.4.9](versions/v0.4.9/README.md) | 2026-10-31 | 入口开关修复 | 设置/电源开关改管胶囊、切英文后图标对齐 |
| [v0.4.10](versions/v0.4.10/README.md) | 2026-10-31 | 图标分行修复 | 可见性读配置字段、共享列硬对齐 |
| [v0.4.11](versions/v0.4.11/README.md) | 2026-10-31 | 下排居中 | 放弃共享列对齐，每排各自严格居中 |
| [v0.4.12](versions/v0.4.12/README.md) | 2026-10-31 | 任务栏恢复 + 灵动岛首版 | 任务栏子窗口逐个恢复、输入法发给焦点窗口、SMTC 播放条（吸收原 0.4.12+0.4.13） |
| [v0.5.0](versions/v0.5.0/README.md) | 2026-11-01 | 灵动岛完整形态 | 拖动跟手、播放形态可拖、播放宽高可调、状态胶囊 WPS 沉底修复（吸收原 0.4.15~0.4.19） |
| [v0.6.0](versions/v0.6.0/README.md) | 2026-11-01 | 窗口预览按应用合并 | 角标、窗口列表、关闭所有窗口（吸收原 0.4.20+0.4.21） |
| [v0.6.1](versions/v0.6.1/README.md) | 2026-11-01 | 安全加固 | 缩略图隐私边界、预览只显示图标、Release 附 SHA256 |
| [v0.6.2](versions/v0.6.2/README.md) | 2026-11-01 | 签名与崩溃恢复 | Authenticode 自签名 + 时间戳、崩溃/强杀后任务栏自愈、`--restore` 实装 |

完整变更历史见 [CHANGELOG.md](CHANGELOG.md)。

**打包好的 exe 不在仓库里**，统一放在 [Releases](../../releases) 页面 ——
单文件 69.5 MB，8 个版本塞进仓库会超过 550 MB，`clone` 会很难受。

| 版本 | exe | 源码 |
| --- | --- | --- |
| v0.0.1 | ✗ 已被覆盖删除，Release 里也没有 | ✗ 未归档 |
| v0.2.0 ~ v0.3.2 | ✓ 见 Releases | ✗ 未归档 |
| v0.3.3 | ✓ 见 Releases | ✓ 完整源码（`versions/v0.3.3/src/`） |
| v0.3.4 ~ v0.3.11 | 连续迭代版，未逐个发布 | 0.3.11 起每版有 src.zip（打包时自动归档，未进仓库） |
| v0.4.0 ~ v0.4.7 | 连续迭代版，未逐个发布 | 每版都有 src.zip（打包时自动归档，未进仓库） |
| v0.4.3 | ✓ 见 Releases（tag 是重排前的 `v0.3.15`） | ✓ 完整源码（`versions/v0.4.3/src/`） |
| v0.4.8 | ✓ 见 Releases | ✓ 完整源码（`versions/v0.4.8/src/`） |
| v0.4.9 | ✓ 见 Releases | ✓ 完整源码（`versions/v0.4.9/src/`） |
| v0.4.10 | ✓ 见 Releases | ✓ 完整源码（`versions/v0.4.10/src/`） |
| v0.4.11 | ✓ 见 Releases | ✓ 完整源码（`versions/v0.4.11/src/`） |
| v0.4.12 | ✗ 未单独发布（重排前构建名 0.4.13） | ✓ 完整源码（`versions/v0.4.12/src/`） |
| v0.5.0 | ✗ 未单独发布（对应内部构建 0.4.19） | ✓ 完整源码（`versions/v0.5.0/src/`） |
| v0.6.0 | ✓ 见 Releases | ✓ 完整源码（`versions/v0.6.0/src/`） |
| v0.6.1 | ✓ 见 Releases（附 `SHA256SUMS.txt`） | ✓ 完整源码（`versions/v0.6.1/src/`） |
| v0.6.2 | ✓ 见 Releases（附 `SHA256SUMS.txt` + `.cer`） | ✓ 完整源码（`versions/v0.6.2/src/`） |

> **为什么旧版本没有源码**：项目从第一行代码到 0.3.2 一直没有用版本控制，
> 源码只有一份持续演进的副本，旧版本构建完就被覆盖了。
> 试过从单文件 exe 反编译还原：C# 能出来，但 **XAML 全部丢失**（BAML 无法还原成可编译的标记），
> 得到的工程编不过，所以没有放进来。
> 从 0.3.3 起 `build.ps1` 会在打包时自动生成 `releases\AquaDesk-<版本>-src.zip`，不会再丢。

## 快速开始

### 直接运行

到 [Releases](../../releases) 下载 `AquaDesk-<版本>.exe`，双击即可。
单文件自包含，不需要安装 .NET。想直接用最新版就挑最新的那个 Release。

### 校验下载的文件（建议）

exe 没有代码签名，所以每个 Release 都附了 `SHA256SUMS.txt`。下载后核对一下，
能确认文件在传输途中没被篡改：

```powershell
# Windows PowerShell —— 先记下 SHA256SUMS.txt 里那串哈希
Get-FileHash .\AquaDesk-0.6.1.exe -Algorithm SHA256 | Format-List
```

```bash
# Linux / macOS —— 和 exe 放同一目录，直接校验
sha256sum -c SHA256SUMS.txt
```

两边的哈希对得上就没问题；对不上说明文件被改过，别运行。

### 校验签名

0.6.2 起 exe 带 Authenticode 签名（SHA256 + 时间戳），用的是一张**自签名**证书
（`certs/AquaDesk-CodeSigning.cer`，指纹 `BF1DDC33C869D5C08E4417777F88E7041692390F`）：

```powershell
Get-AuthenticodeSignature .\AquaDesk-0.6.2.exe | Format-List Status, StatusMessage, SignerCertificate
```

看到 `SignerCertificate` 的主题是 `CN=ShiraiYuzen (AquaDesk)` 且指纹一致，说明文件出自同一把私钥。

自签名证书默认不被 Windows 信任，`Status` 会显示「证书链终止于不受信任的根」—— 这是预期行为，
**不是**签名坏了。想让它在这台机器上变成可信发布者：

```powershell
.\tools\trust_signing_cert.ps1          # 装入 CurrentUser 的受信任根 + 受信任发布者
.\tools\trust_signing_cert.ps1 -Remove  # 撤销
```

正式证书（SignPath 免费开源签名 / Microsoft Trusted Signing ≈$10/月 / 商业 OV）需要项目方去申请，
在此之前自签名 + SHA256 是能提供的最强保证。

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
├─ build.ps1                 打包脚本（自动签名 + 归档源码 + 生成 SHA256）
├─ certs/
│  └─ AquaDesk-CodeSigning.cer  签名公钥（自签名，供核对 / 自行信任）
├─ tools/
│  ├─ make_signing_cert.ps1   生成自签名代码签名证书
│  └─ trust_signing_cert.ps1  把公钥证书装进受信任根（可撤销）
├─ docs/
│  ├─ 01-产品构想.md          产品定位与功能分层
│  └─ 02-架构决策.md          ADR-001~010，改视觉/交互前先读
├─ src/AquaDesk.App/          WPF 源码（.NET 8，0.6.2）
└─ versions/
   ├─ v0.0.1/README.md
   ├─ v0.2.0/README.md       发布说明（exe 见 Releases）
   ├─ ...
   ├─ v0.4.3/
   ├─ v0.4.8/
   ├─ v0.4.9/
   ├─ v0.4.10/
   ├─ v0.4.11/
   ├─ v0.4.12/
   ├─ v0.5.0/
   ├─ v0.6.0/
   ├─ v0.6.1/
   └─ v0.6.2/
      ├─ src/                该版本的完整源码
      └─ README.md
```

仓库本体约 1 MB，全是文本和源码；二进制走 Releases。

## 发布新版本

打包后把 exe 传到 Releases，别提交进仓库：

```bash
# 用 gh CLI（推荐）
gh release create v0.4.11 \
  dist/AquaDesk-0.4.11.exe \
  --title "v0.4.11 · 下排居中" \
  --notes-file versions/v0.4.11/README.md
```

网页端也行：仓库 → Releases → 起草新版本 → 标签填 `v0.4.11` → 上传 exe →
正文直接粘 `versions/v0.4.11/README.md` 的内容。

一个 Release 可以同时挂多个版本的文件，但建议一版一个 Release，标签与版本号对齐。


## 技术栈

| 层 | 选择 | 理由 |
| --- | --- | --- |
| 运行时 | .NET 8 + WPF | 单文件发布 69.5 MB，目标机器零依赖 |
| 玻璃 | 自绘八层 | 抓屏折射已停用 —— 它会让自己在任何截图里消失 |
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
- **没有抓屏折射**：那种效果要求把自己从屏幕捕获里排除（`WDA_EXCLUDEFROMCAPTURE`），
  代价是用户截图、录屏都拍不到 Dock。和「能截图」互斥，选了后者。
- **签名是自签名的**：能验证完整性与发布者一致性，但不被 Windows 默认信任，
  SmartScreen 仍会拦。正式证书的成本与路径见上文「校验签名」。
- **硬杀恢复靠下次启动**：`taskkill /F` 时进程内代码跑不到，只能靠标记文件在下次启动时自愈；
  想「进程一死立刻还原」得常驻第二个进程，不值得。

## 许可证

[MIT](LICENSE) © 2026 ShiraiYuzen

可以自由使用、修改、商用、再分发，保留版权声明即可。

## 致谢

界面语言参考 macOS Aqua 设计语言。所有图标均为矢量自绘或取自系统，不含第三方素材。
