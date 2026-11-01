# AquaDesk v0.6.1

> 发布：2026-11-01 · 安全加固：缩略图隐私边界 + 分发包校验

## 一句话

Dock 的窗口缩略图不再抓「不该抓」的窗口（密码管理器 / DRM / 显式声明禁止捕获的），
并新增「预览只显示图标」隐私开关；Release 开始附带 SHA256 校验和。

## 为什么要做

这个应用的窗口预览用 `PrintWindow` 把**任意**窗口抓成静态图，贴在常驻 Topmost 浮层上。
也就是说，密码管理器、银行页面、DRM 视频、私人聊天只要开着，就可能出现在 Dock 缩略图里 ——
而 Dock 是屏幕最上层，**任何截图、录屏、以及旁边的人都能看到**。

这不是「被攻击」的风险，是**信息泄露给旁观者**的风险。对这类工具来说，比传统安全漏洞更实际。

## 缩略图隐私边界（两道闸）

1. **尊重窗口自己的声明**：查 `GetWindowDisplayAffinity`，只要设了 `WDA_MONITOR` 或
   `WDA_EXCLUDEFROMCAPTURE`（Win10 2004+，密码管理器与 DRM 播放器普遍这么干）→ 不抓，
   预览退回应用图标。系统级本该尊重这类声明，之前是绕过它的。
2. **已知密码 / 密钥类应用黑名单**：按 exe 名兜住没设 WDA 的那些 ——
   1Password、Bitwarden、KeePass / KeePassXC、LastPass、Dashlane、Enpass、NordPass、
   RoboForm、Keeper、Proton Pass、Authy、WinAuth、Strongbox。

黑名单是「兜底」，不是完整方案：任何没设 WDA 又不在名单里的应用仍会被抓（浏览器里的网银页面
也抓不到进程级别）。所以配了下面这个开关。

## 新增：预览只显示图标（隐私开关）

图标页 → 窗口预览下方新增「预览只显示图标」：

- 打开 → 一张缩略图都不抓，全部退回应用图标（保留标题、角标、点击切换等全部功能）
- 切换即时生效：打开时丢掉已经抓到的图，关闭时立刻重抓一轮
- 也纳入了「恢复默认」与「全部重置」

## 分发完整性：SHA256SUMS

exe 没有代码签名，下载页面之外无法证明「这个文件是我发的」。所以：

- `build.ps1` 打包时自动生成 `dist/SHA256SUMS.txt`（sha256sum 兼容格式），随 exe 上传
- README 补了校验方法（Windows PowerShell：`Get-FileHash`；Linux/macOS：`sha256sum -c`）
- 本版 exe 的 SHA256 见 Release 附件

## 说明

这轮只做「低风险、可验证」的加固：不改架构、不引入依赖、不动权限模型（仍然是
`asInvoker` + `uiAccess=false`）。代码签名证书与「系统状态崩溃恢复」的清单式验证留待后续。
