# AquaDesk v0.6.8

> 发布：2026-11-01 · 窗口预览区上限从 12 格降到 6 格

## 一句话

预览区最多同时显示 6 个应用，第 7 个应用起折叠进末尾的「全部窗口」入口（6 格时是 5 张缩略图 + 1 个入口），
Dock 不再被预览区拉得很长。

## 改动

只改一个常量：`WindowPreviewService.MaxPreviewSlots` 从 12 → 6。
两处 `Math.Clamp(..., 3, MaxPreviewSlots)` 自动采用新上限，其余逻辑不动。

- 1~6 个应用：全部平铺，不折叠。
- 第 7 个应用起：5 张预览 + 末尾「全部窗口」入口。
- 仍按 exe 合并：同一个应用开多少窗口只占 1 格，只在角标上记数。
- 3 格下限保留：固定图标多到让可用宽度不够用时，继续缩到 4 格、3 格。

## 验证

```powershell
powershell -ExecutionPolicy Bypass -File .\verify_release.ps1
```

应显示 `[2/3] 签名证书指纹可信（v0.6.4 起）`。
