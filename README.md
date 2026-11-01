# AquaDesk · 水光桌面

> 把 macOS 的桌面质感搬到 Windows：Dock、顶部菜单栏、液态玻璃、圆角、启动台。
> 不动系统底层，只换你看到的那一层。

**当前版本：0.6.0** · 两种形态任选：单文件便携版 / Velopack 安装版 · 目标机器不用装 .NET

![Release](https://img.shields.io/github/v/release/ShiraiYuzen/AquaDesk?label=release&color=blue)
![Build](https://github.com/ShiraiYuzen/AquaDesk/actions/workflows/build.yml/badge.svg)
![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6)
![.NET](https://img.shields.io/badge/.NET-8%20%2B%20WPF-512BD4)
![Size](https://img.shields.io/badge/exe-~70%20MB-informational)
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

开发期一共产生过 40 多个内部构建，公开发布时按**功能阶段**归并成 6 个版本，编号从 `0.1` 起。

| 版本 | 日期 | 主题 | 说明 |
| --- | --- | --- | --- |
| [v0.1.0](versions/v0.1.0/README.md) | 2026-10-20 | Dock 基础 | 图标、悬停放大、拖放排序、单文件发布 |
| [v0.2.0](versions/v0.2.0/README.md) | 2026-10-21 | 玻璃与启动台 | 真折射液态玻璃、启动台、文件夹、WiFi/蓝牙/音量面板 |
| [v0.3.0](versions/v0.3.0/README.md) | 2026-10-26 | 窗口预览与停靠 | 窗口缩略图、系统图标、四边停靠、界面缩放 |
| [v0.4.0](versions/v0.4.0/README.md) | 2026-10-31 | 灵动岛与外观 | 顶栏改时间胶囊、右侧状态胶囊、外观切换、设置页重做 |
| [v0.5.0](versions/v0.5.0/README.md) | 2026-11-01 | 系统区成型 | 灵动岛播放形态、状态胶囊网格、后台程序面板、浮层让位 |
| [v0.6.0](versions/v0.6.0/README.md) | 2026-11-02 | 安装与更新 | 检查更新、Velopack 安装版、代码签名与验签 |

每个版本文件夹里的说明可以直接作为 GitHub Release 正文。完整变更历史见 [CHANGELOG.md](CHANGELOG.md)。

**打包好的 exe 不在仓库里**，统一放在 [Releases](../../releases) 页面 ——
单文件约 70 MB，放进仓库会让 `clone` 很难受。源码就在 `src/`，每版另有 `*-src.zip` 归档在 Releases。


## 快速开始

### 直接运行

到 [Releases](../../releases) 下载，两种形态二选一：

| 产物 | 适合谁 | 说明 |
| --- | --- | --- |
| `AquaDesk-<版本>.exe` | 绿色软件党 / 极客 | 单文件自包含，放哪都能跑，不装 .NET；检查更新时提示内容 + 一键打开发布页手动下载 |
| `AquaDesk-<版本>-Setup.zip` | 普通用户 | 解压后运行 `AquaDesk.Installer.exe`，弹框选安装目录，装完自动更新交给 Velopack 框架 |

想直接要「装好就能自动更新」的，下安装版；想要「一个 exe 走天下、手动掌控更新」的，下便携版。
两个都带同一个自签证书（指纹 `62DF52A9…`）。

### 验证作者身份（推荐）

把 Release 里的四个文件放同一目录：

```
AquaDesk-0.6.0.exe
SHA256SUMS.txt
SHA256SUMS.txt.sig
verify_release.ps1
```

在那个目录打开 PowerShell（地址栏敲 `powershell` 回车即可），运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\verify_release.ps1
```

> 加 `-ExecutionPolicy Bypass` 是因为 Windows 默认禁止运行未签名脚本，
> 直接敲 `.\verify_release.ps1` 会报「在此系统上禁止运行脚本」——那是系统策略，不是脚本有问题。
> 若提示文件被阻止，先跑 `Unblock-File .\verify_release.ps1`。

它依次检查三件事：

| # | 检查 | 挡住什么 |
|---|---|---|
| 1 | exe 的 SHA256 与 `SHA256SUMS.txt` 一致 | 传输损坏、文件被替换 |
| 2 | exe 的签名证书指纹在可信列表内 | 别人拿自己的证书重新签一遍 |
| 3 | `SHA256SUMS.txt` 的签名有效 | 换掉 exe 后顺手把哈希一起改了 |

三项全过会打印「通过：文件出自 CN=ShiraiYuzen (AquaDesk) 这把私钥」。

**信任的锚点是这些指纹**（证书换过一次代，两代都可信）：

```
62DF52A960B467FE81D1B471A08A8447A86FFA5B    ← v0.6.4 起
BF1DDC33C869D5C08E4417777F88E7041692390F    ← v0.6.2 ~ v0.6.3
```

它们写在本 README 里，而本 README 由仓库的提交历史保护 —— 只要列表没变，
无论从哪个镜像下载，核对得上就是同一个人发的。

> **为什么有两代**：v0.6.3 及之前那张证书把私钥设成了「不可导出」——防偷，但也备份不了，
> 机器一旦损坏指纹就永久作废，锚点跟着断。v0.6.4 换成可导出的证书并离线备份了私钥，
> 所以从这版起指纹是 `62DF52A9…`。历史版本的包仍用旧指纹验证，脚本里做了白名单。

#### 手动验证（不想跑脚本也可以）

```powershell
# 1. 哈希：和 SHA256SUMS.txt 里那一行对比
Get-FileHash .\AquaDesk-0.6.0.exe -Algorithm SHA256 | Format-List

# 2. 指纹：应等于上面那串
(Get-AuthenticodeSignature .\AquaDesk-0.6.0.exe).SignerCertificate.Thumbprint
```

```bash
# Linux / macOS：和 exe 放同一目录
sha256sum -c SHA256SUMS.txt
```

#### 关于这张证书

`certs/AquaDesk-CodeSigning.cer`，**自签名**，10 年期（至 2036-11-01），
与清单签名共用同一张。所以要知道它证明什么、不证明什么：

| 能证明 | 不能证明 |
|---|---|
| 文件出自这把私钥，指纹与 README 一致 | 微软/CA 认可这个发布者 |
| 清单没被改过（改了签名就失效） | SmartScreen 不弹「未知发布者」 |

`Get-AuthenticodeSignature` 的 `Status` 会显示「证书链终止于不受信任的根」，
这是自签名的正常结果，**不是签名坏了**。

想让 Windows 在本机认它（可选，只影响你这台机器）：

```powershell
.\tools\trust_signing_cert.ps1          # 装入 CurrentUser 的受信任根 + 受信任发布者
.\tools\trust_signing_cert.ps1 -Remove  # 撤销
```

> 想彻底消掉 SmartScreen 警告，只能买 CA 核验过身份的证书（OV/EV，约千元/年）。
> 免费的开源签名方案 SignPath Foundation 需要项目方提交申请，本项目暂未申请；
> 材料已备在 [docs/03-代码签名申请.md](docs/03-代码签名申请.md)，随时可提交。
> 注意即使通过，证书上显示的发布者也是 SignPath Foundation，不是作者本人。

重复启动 = **新版本接管**：双击任意一个 exe，正在跑的旧版本会自动退出、新版本接手。
所以升级时不用先去托盘里找「退出」。

### 从源码构建

需要 .NET 8 SDK：

```powershell
# 修改 csproj 里的 Version / AssemblyVersion / FileVersion / InformationalVersion
# 注意：AssemblyVersion 与 FileVersion 必须是纯数字四段（0.3.3 → 0.3.3.0）
.\build.ps1             # 只出便携版（单文件 exe）
.\build.ps1 -Velopack   # 便携版 + Velopack 安装版 + 安装引导器，一次全出
```

产物：便携版在 `dist\AquaDesk-<版本>.exe`，安装版在 `dist\velopack\`（Setup.exe / Portable.zip / nupkg / 更新清单），
旧版本自动归档到 `releases\`。

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
│  ├─ trust_signing_cert.ps1  把公钥证书装进受信任根（可撤销）
│  ├─ sign_release.ps1        给 SHA256SUMS.txt 做分离签名（RSA-SHA256）
│  └─ verify_release.ps1      一条命令验证「这份文件是作者发的」
├─ docs/
│  ├─ 01-产品构想.md          产品定位与功能分层
│  ├─ 02-架构决策.md          ADR-001~010，改视觉/交互前先读
│  └─ 03-代码签名申请.md       SignPath Foundation 申请材料（暂未提交）
├─ .github/workflows/build.yml  CI：构建（并通过 SignPath 签名，若已配置）
├─ signpath/
│  └─ artifact-configuration.xml  签名产物配置（单文件 PE + Authenticode）
├─ src/AquaDesk.App/          WPF 源码（.NET 8）
├─ src/AquaDesk.Installer/    图形化安装引导器（选目录 → 调 Setup.exe --installto）
└─ versions/
   ├─ v0.1.0/README.md         各版本的发布说明（exe 见 Releases）
   ├─ v0.2.0/README.md
   ├─ v0.3.0/README.md
   ├─ v0.4.0/README.md
   ├─ v0.5.0/README.md
   └─ v0.6.0/README.md
```

仓库本体约 1 MB，全是文本和源码；二进制走 Releases。

## 发布新版本

打包后把 exe 传到 Releases，别提交进仓库：

```bash
# 用 gh CLI（推荐）
gh release create v0.6.0 \
  dist/AquaDesk-0.6.0.exe \
  --title "v0.6.0 · 安装与更新" \
  --notes-file versions/v0.6.0/README.md
```

网页端也行：仓库 → Releases → 起草新版本 → 标签填 `v0.6.0` → 上传 exe →
正文直接粘 `versions/v0.6.0/README.md` 的内容。

一个 Release 可以同时挂多个版本的文件，但建议一版一个 Release，标签与版本号对齐。


## 技术栈

| 层 | 选择 | 理由 |
| --- | --- | --- |
| 运行时 | .NET 8 + WPF | 便携版单文件约 70 MB，目标机器零依赖 |
| 玻璃 | 自绘八层 | 抓屏折射已停用 —— 它会让自己在任何截图里消失 |
| 系统集成 | Win32 P/Invoke + WinRT | 任务栏、AppBar、Radio Manager 都要原生 |
| 图标 | Shell ImageList + 像素分析 | 避免每次取图驻留 240KB |
| 安装 / 更新（安装版） | Velopack | delta 更新、下载校验、失败回滚，签名沿用同一张自签证书 |

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
- **签名是自签名的**：能验证「文件出自哪把私钥」且指纹公布在本仓库，但不被 Windows 默认信任，
  SmartScreen 仍会拦。正式证书的成本与路径见上文「验证作者身份」。
- **硬杀恢复靠下次启动**：`taskkill /F` 时进程内代码跑不到，只能靠标记文件在下次启动时自愈；
  想「进程一死立刻还原」得常驻第二个进程，不值得。

## 许可证

[MIT](LICENSE) © 2026 ShiraiYuzen

可以自由使用、修改、商用、再分发，保留版权声明即可。

## 致谢

界面语言参考 macOS Aqua 设计语言。所有图标均为矢量自绘或取自系统，不含第三方素材。
