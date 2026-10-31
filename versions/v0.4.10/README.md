# AquaDesk v0.4.10

> 发布：2026-10-31 · 产物 `AquaDesk-0.4.10.exe`（69.6 MB，自包含单文件）· 后续版本见 [v0.4.11](../v0.4.11/README.md)

## 一句话

状态胶囊的图标分行对不齐（附图反馈「没对齐」），两个根因一起修：
可见性读了不可靠的 `Element.Visibility`，加上两排各自居中在格数差为奇数时错开半格。

## 两个根因

### 1. 可见性读了 `Element.Visibility`，被重置

`ApplyRowLayout` 里 `FillRow` 会 `TopRow.Children.Clear()` 再重加子元素。`BrandIcon` /
`WindowsIcon` 的 `Visibility` 绑定（`{Binding ShowBrandIcon}`）在元素被摘出再挂回时，
会被本地值覆盖成 `Visible` —— 结果「关掉开关后重启，图标又冒出来」，
可见项从 6 错算成 8，分行跟着错。

现在一律读配置字段（`ShowBrandIcon` / `ShowWindowsIcon` / `ShowBackgroundAppsButton` /
`ShowBatteryPercent`）＋ 明确的 `_inputStateVisible` 字段，并在分行时把这些值写回
`element.Visibility` 兜底。

### 2. 两排各自居中，格数差为奇数时错半格

上排 4 格、下排 3 格时，两排各自的中心对不上，差半格（13.5px）。
0.4.10 改用「两排共用同一套 `totalCells` 列，窄排算 `lead` 补空格」，
列索引天然一一对应，下排图标硬保证压在上排正下方。

> 注：这个「共享列」方案在差为奇数时会让下排整体偏半格，0.4.11 又改回了
> 「每排各自居中」。详见 [v0.4.11](../v0.4.11/README.md)。

## 升级注意

配置不变，无迁移。

## 文件

- 产物：`AquaDesk-0.4.10.exe`（69.6 MB）
- 源码：`versions/v0.4.10/src/`（完整工程，可直接 `dotnet build`）
- 变更历史：[CHANGELOG.md](../../CHANGELOG.md)
