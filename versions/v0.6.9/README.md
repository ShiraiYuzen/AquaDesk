# AquaDesk v0.6.9

> 发布：2026-11-02 · 输入法面板列出全部输入法，并支持直接切换（讯飞 / 微信 / 微软拼音 / 系统自带）

## 一句话

输入法面板不再「永远只认搜狗」——现在把系统里所有启用的输入法都列出来，点一下直接切换。

## 原因

老实现按 `HKCU\Keyboard Layout\Preload` 列「键盘布局」，每个语言只取**第一个**带图标的 TSF profile。
于是 0804（简体中文）下永远只显示排第一的搜狗，讯飞、微信、微软拼音明明都注册在
`HKLM\SOFTWARE\Microsoft\CTF\TIP\{CLSID}\LanguageProfile\0x0804\{GUID}` 里，全被吞掉。

## 改动

- **列出全部输入法**：按语言把 profile 全量展开，一个输入法一行，各自读自己的
  `IconFile` + `IconIndex`，不再按语言「取一个代表」。
- **没图标的项现画一个**（`InputMethodIcon.Generated`）：橙底白字圆角方块，仿搜狗。
  美式键盘这类纯布局、或图标资源坏了的输入法也不会留空框。
- 列表加了 `MaxHeight=248` 的 ScrollViewer，输入法多时不会长出屏幕。

## 同语言切换的坑（关键修复）

搜狗 / 讯飞 / 微软拼音的 HKL **全都是 `00000804`**，光发 `WM_INPUTLANGCHANGEREQUEST` 是在原地打转。
现在分两层：

1. 不同语言：走 `LoadKeyboardLayout` + `WM_INPUTLANGCHANGEREQUEST`（老路，可靠）。
2. 同语言换输入法：走 TSF 的 `ActivateLanguageProfile`。

踩了两个坑（都写进代码注释了）：

- `CoCreateInstance(CLSID_TF_InputProcessorProfiles)` 在 WPF 进程里对这个接口一律回
  `E_NOINTERFACE`（0x80004002），**同样代码在控制台进程却成功**；换 STA/MTA、换 CLSCTX_ALL、
  先 QI IUnknown 都无效。改用 msctf.dll 导出的 `TF_CreateInputProcessorProfiles` 才通。
- 同一接口的 `GetActiveLanguageProfile` 在 Win11 上直接 AccessViolation 崩进程，所以
  「当前是哪个 profile」读不到，改成记住用户最后一次点的那条；语言层面仍实时读 `GetKeyboardLayout`。

只有 `Enable≠0` 的输入法会被列出来——在系统输入法列表里被关掉的（比如本机的微软拼音）不会出现。

## 验证

```powershell
powershell -ExecutionPolicy Bypass -File .\verify_release.ps1
```

应显示 `[2/3] 签名证书指纹可信（v0.6.4 起）`。

> 本机开发环境只有一个启用的中文输入法（搜狗），所以「同语言内多输入法切换」没能真机验证，
> 只验证了调用链返回 ok + 不同语言来回切正常。装了讯飞/微信的机器上点一下即可确认。
