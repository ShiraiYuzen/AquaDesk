# AquaDesk v0.3.2

> 发布：2026-10-26 · 产物 `AquaDesk-0.3.2.exe`（约 69.5 MB，自包含单文件）

## 一句话

换台电脑也能直接用：界面按屏幕自动缩放，启动台只收真正的应用，点图标必定响应。

## 本版更新

### 新增

- **界面缩放**（`UiScale` 80%~150%，设置 → 外观有滑块）：
  Dock 高度、磁贴宽度、圆角、放大系数、字号全部从缩放后的图标尺寸算；
  菜单栏高度也跟着 `26 × UiScale`。
- **屏幕比例适配**：默认缩放取 `min(物理宽/1920, 物理高/1080)`，5% 取整并夹在 0.8~1.2。
  16:9 等于按高度算；16:10 取宽度那个，避免 Dock 横向撑满；4:3 按宽度压下来。
  用物理像素算，不用 DIP（DIP 已含系统缩放，再乘一次会重复放大）。
- **启动台扫描改成结构判断优先**，换台电脑同样成立：
  1. 目标必须是 `.exe`（`.bat`/`.cmd`/`.ps1`/`.msi`/`.url` 一律丢）
  2. 名字精确黑名单（git-bash / node / powershell / python / java / docker / cmake …）
  3. 后缀黑名单（`*-cli` / `*console` / `*service` / `*updater` / `*setup` / `*installer` / 含 `unins`）
  4. 路径黑名单（nodejs / git / python3 / jdk / dotnet / msys / mingw / cmake / maven / gradle / golang / rust …）
  5. **PE 头 Subsystem == 3（控制台程序）直接丢** —— 这条最通用
  6. 解析不出目标（UWP / shell 命名空间）保留，宁可多留不误杀
- 目标落在 `%WINDIR%` 下的一律跳过；UWP 应用按图标来源区分系统自带与第三方。
- `--test-scan` 自测参数，扫描结果直接写日志。

### 修复

- **点 Dock 图标有时不最小化**，三处成因逐个修掉：
  1. 双击去抖：第一下最小化、第二下又还原，合起来像「没反应」→ 加 350ms 同图标去抖；
  2. 窗口预览项被当成可拖项，点击被拖拽态吞掉 → `CanDrag` 排除 `Window`；
  3. `FindMainWindow` 选中了带 `WS_EX_NOACTIVATE` 的幽灵窗口（Electron / Chromium 系常见，
     有标题、`IsWindowVisible` 为真、但永远激活不了），`SetForegroundWindow` 返回 true 而前台纹丝不动。
     改为排除该类窗口，候选多于一个时优先前台窗口、否则取面积最大的。
  新增 `WindowActivator`：挂目标线程 → `BringWindowToTop` → `SetForegroundWindow` → `SetFocus`
  → 失败再 `SwitchToThisWindow`。**`SetForegroundWindow` 只改前台窗口不改键盘焦点**，
  少这一步会出现「窗口在最上面但像在后台，要再点一下才能操作」。
- 设置页「最大化」铺满整块屏幕会把红绿灯压在菜单栏底下，改为铺满工作区。
- 设置窗口在小屏 / 4:3 上可能装不下，`Loaded` 时按工作区收边。

### 变更

- **开机自启改为计划任务优先**：登录触发器 + 交互式令牌 + 普通权限（`LUA`）+ 优先级 4，
  任务计划服务直接拉起，早于 Explorer 处理 Run 项。
  任务指向 `%LocalAppData%\AquaDesk\bin\AquaDesk.exe` 固定副本，改版本号时只同步副本、不重注册任务。
  不勾「以最高权限运行」—— 提权后 UIPI 会禁止从资源管理器往窗口拖文件。

## 安装

双击 `AquaDesk-0.3.2.exe`。首次开启自启可能会弹一次 UAC（注册计划任务需要）。

## 已知问题

- **关闭自启后下次开机仍可能自启**（Run 项命名遗留问题，v0.3.3 修）。
- 内存仍在 300 MB 量级。

## 升级自

v0.3.1。配置新增 `UiScale`，缺省 1。

## 构建

本版源码未单独归档。构建方式见仓库根目录 `README.md`。

## 本版文件

| 文件 | 说明 |
| --- | --- |
| `README.md` | 本版发布说明，可直接作为 GitHub Release 正文 |

- **exe 不在仓库里**：69.5 MB 的单文件放进仓库会把体积撑爆，统一放在
  [Releases](../../releases) 页面，文件名 `AquaDesk-0.3.2.exe`。
- 校验用 SHA-256（前 32 位）：`618ca8a498512606d7fdeb024c1291cf`
- **源码未单独归档**：项目当时没有版本控制。完整源码见仓库 `src/`（0.3.3）。
