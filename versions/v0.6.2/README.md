# AquaDesk v0.6.2

> 发布：2026-11-01 · 代码签名链路 + 系统状态崩溃恢复

## 一句话

exe 开始带 Authenticode 签名（自签名 + 时间戳），并附公钥证书供核对；
隐藏任务栏后如果进程崩溃 / 被强杀，下次启动会自动把任务栏捡回来，另有 `--restore` 手动救援。

## 一、代码签名

### 做了什么

- `tools/make_signing_cert.ps1`：生成本项目专用的**自签名代码签名证书**
  （10 年期，私钥不可导出、只存在本机证书库），公钥导出为 `certs/AquaDesk-CodeSigning.cer`。
- `build.ps1`：打包后自动签名（SHA256 + DigiCert 时间戳）。找不到证书就跳过，
  从源码构建的人照样能出包。
- 顺序修正：**先签名再算 SHA256** —— 签名会改字节，反过来算的校验和必然是错的。
- Release 同时提供 `.cer`，任何人可以用它验证「这个 exe 确实出自同一把私钥」。

### 必须说清楚的两件事

1. **自签名 ≠ 被信任**。Windows 默认不认这张证书，签名状态会显示
   「证书链终止于不受信任的根」，SmartScreen 也照旧会拦。
   它解决的是**完整性 + 发布者一致性**，不是「消灭警告」。
2. 想在本机把它变成「可信发布者」：跑一次 `tools/trust_signing_cert.ps1`
   （只写 `CurrentUser` 的根 + 受信任发布者，不需要管理员，`-Remove` 可撤销）。

### 想要「别人下载就显示可信发布者」

只能上正式证书。个人发布者的现实路径：

| 方案 | 成本 | 说明 |
|---|---|---|
| SignPath Foundation | 免费 | 开源项目专用，需申请审核、走其 CI 签名 |
| Microsoft Trusted Signing | 约 $10/月 | 个人可验证，Azure 订阅即可 |
| OV 证书 | 约 ￥1500+/年 | 私钥必须放硬件令牌，签名要插着它 |

## 二、系统状态崩溃恢复

隐藏任务栏需要把它切成自动隐藏 + 隐藏窗口，并在工作区里释放那 48px。
进程**正常退出**时 `OnExit` 会还原；但崩溃 / 强杀时会留下「没有任务栏的桌面」。

### 现在覆盖的路径

| 场景 | 处理 |
|---|---|
| UI 线程未处理异常 | `DispatcherUnhandledException` → 先还任务栏，再记日志 |
| 后台线程未处理异常 | `AppDomain.UnhandledException` → 同上 |
| 注销 / 关机 | `SessionEnding` → 同上 |
| `Environment.Exit` 等收尾 | `ProcessExit` → 同上 |
| **`taskkill /F` / 蓝屏**（进程内代码根本跑不到） | 隐藏时写标记文件 `taskbar-hidden.marker`；下次启动发现标记 → 先恢复再按设置应用 |
| 桌面已经坏了、程序又起不来 | `AquaDesk.exe --restore`：恢复任务栏 + 桌面图标，然后退出 |

### 设计取舍

- **桌面图标不在崩溃时自动还原**。它是用户自己的持久设置（正常退出也不还原），
  而启动时会按系统实际状态回写设置 —— 崩溃时强行显示会把用户的开关悄悄改掉。
  要恢复用 `--restore`。
- 标记文件只是「下次启动自愈」，不是常驻看门狗：要真正做到「进程一死立刻还原」，
  得再挂一个常驻进程，对这个体量的工具不划算。

## 验证方式

```powershell
# 签名
Get-AuthenticodeSignature .\AquaDesk-0.6.2.exe | Format-List Status, StatusMessage, SignerCertificate

# 完整性
Get-FileHash .\AquaDesk-0.6.2.exe -Algorithm SHA256     # 与 SHA256SUMS.txt 比对
```

崩溃恢复：开启「隐藏原生任务栏」→ 任务管理器里强杀 AquaDesk → 重新启动，
任务栏应先恢复、再按设置重新隐藏；或直接跑 `AquaDesk.exe --restore`。
