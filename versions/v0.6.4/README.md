# AquaDesk v0.6.4

> 发布：2026-11-01 · 签名私钥从「不可导出」改成「可备份」

## 一句话

换了签名证书：私钥改为可导出并离线备份成 `.pfx`，避免机器一坏指纹永久作废、整条证明链断裂。
**exe 没有任何功能改动**，重新打包只是为了换签名。

## 为什么必须换

v0.6.3 建立了「指纹 = 作者身份」的验证体系，但那张证书创建时写的是：

```powershell
KeyExportPolicy = "NonExportable"
```

好处是私钥偷不走，**代价是导不出来**。于是：

| 情况 | 后果 |
|---|---|
| 重装系统 / 换电脑 | 无法再用同一把私钥签名 |
| 证书库损坏 | 同上 |
| 想备份到 U 盘 | 根本导不出来 |

任何一种发生，指纹 `BF1DDC33…` 就永久作废。而 v0.6.3 的整个信任模型建立在
「README 里的这串字不变」之上 —— 指纹一变，旧包和新包的关系就得靠解释，而不是靠验证。

当时只有 2 个版本用过这把私钥，**是换证书代价最小的时刻**，所以现在就换。

## 换了什么

### 证书

| 项目 | v0.6.2 ~ v0.6.3 | v0.6.4 起 |
|---|---|---|
| 私钥导出 | NonExportable | **Exportable** |
| 密钥长度 | 2048 | **3072** |
| 指纹 | `BF1DDC33…390F` | `62DF52A9…FA5B` |
| 离线备份 | 不可能 | `certs/private/*.pfx` |

旧公钥没有丢，归档在 `certs/legacy/AquaDesk-CodeSigning-BF1DDC33.cer`。

### 验签脚本：支持两代指纹

`tools/verify_release.ps1` 里原来是硬编码一个指纹，现在改成白名单：

```powershell
$TrustedSigners = [ordered]@{
    "62DF52A960B467FE81D1B471A08A8447A86FFA5B" = "v0.6.4 起"
    "BF1DDC33C869D5C08E4417777F88E7041692390F" = "v0.6.2 ~ v0.6.3"
}
```

输出会标明命中的是哪一代，例如「签名证书指纹可信（v0.6.4 起）」。
这样**旧版本的包不会因为换证书而变成验不过**。

### 证书脚本

`tools/make_signing_cert.ps1`：

- `-New` 强制重建（默认复用已有证书）
- 指纹变化时自动把旧 `.cer` 挪到 `certs/legacy/`，不覆盖历史
- 生成后自动导出 `.pfx`，随机密码打印到控制台
- `-PfxPassword` 可指定自己的密码

## 备份说明（重要）

`certs/private/AquaDesk-CodeSigning-20261101.pfx` + 那串随机密码，
是**唯一**能在换机后继续用同一指纹签名的东西。

- 存到密码管理器或 U 盘，不要只留在这台机器上
- `certs/private/` 已被 `.gitignore` 排除，但同目录下的 `PFX-PASSWORD.txt`
  在转存之后应该删掉 —— 密码和私钥放一起等于没加密

## 验证

```powershell
powershell -ExecutionPolicy Bypass -File .\verify_release.ps1
```

应显示 `[2/3] 签名证书指纹可信（v0.6.4 起）`。

本版 SHA256：`21ac3a49c708ec1bc7a29a89036ed85d74b5e04351f72d1c2d3ce426a867293c`
