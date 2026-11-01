# AquaDesk v0.6.3

> 发布：2026-11-01 · 把「这文件是作者发的」变成一条命令

## 一句话

发布清单开始带签名，并附一个验签脚本 —— 下载后跑 `.\verify_release.ps1`，
三层检查全过就说明文件确实出自作者本人那把私钥。本版**没有 exe 功能改动**。

## 为什么加这个

0.6.2 给 exe 加了 Authenticode 签名，但它只解决一半问题：

- 签名证明「这个 exe 由这把私钥签过」
- 但 `SHA256SUMS.txt` 是明文，**谁都能改**。把 exe 换掉、再把哈希一起改掉，
  只做哈希比对的做法完全发现不了。

所以让私钥把清单本身也签一遍：改清单 → 签名失效。这样哈希和签名互相锁住，
再配合「指纹公布在 README」这个锚点，整条链就闭合了。

## 做了什么

### 新增：清单签名

`tools/sign_release.ps1` 用**同一张**代码签名证书对 `SHA256SUMS.txt` 做
RSA-PKCS1-SHA256 分离签名，产出：

| 文件 | 作用 |
|---|---|
| `SHA256SUMS.txt.sig` | Base64 签名，给脚本验 |
| `SIGNING.txt` | 人读信息：证书主题、指纹、有效期、算法、被签文件 |

`build.ps1` 在生成清单后自动调用它，并把 `verify_release.ps1` 一起复制进 `dist/`，
所以 Release 里下到的就是「exe + 清单 + 签名 + 验签脚本」四件套。

### 新增：一条命令验作者

```powershell
.\verify_release.ps1
```

三层递进，任一层不过立刻退出码 1 并说明卡在哪：

| # | 检查 | 挡住什么 |
|---|---|---|
| 1 | exe 的 SHA256 与清单一致 | 传输损坏、文件被替换 |
| 2 | exe 的签名证书指纹 = `BF1DDC33…390F` | 别人拿自己的证书重签一遍 |
| 3 | 清单签名有效 | 换掉 exe 后顺手把哈希一起改了 |

## 信任的锚点

```
BF1DDC33C869D5C08E4417777F88E7041692390F
```

这串指纹写在 README 里，由仓库的提交历史保护。它才是「是作者发的」的判据 ——
证书本身谁都能造一张写着同样名字的，但造不出同样的指纹。

## 关于正式签名（SignPath）

本来准备了 SignPath Foundation 的免费开源签名申请材料（`docs/03-代码签名申请.md`），
**本版决定不提交**，原因有两个：

1. SignPath 证明的是「二进制由该仓库的 CI 产出」，证书上的发布者是
   **SignPath Foundation**，不是作者本人 —— 对「证明作者是我」这个目标不对口。
2. 真正能在别人机器上显示「已验证发布者：ShiraiYuzen」的只有 CA 核验过身份的证书
   （OV/EV，约千元/年），和这个项目的体量不成比例。

材料保留在仓库里，哪天想用可以直接提交。

## 验证方式

```powershell
# 推荐：一条命令
.\verify_release.ps1

# 手动：两个值都要对
Get-FileHash .\AquaDesk-0.6.3.exe -Algorithm SHA256            # == SHA256SUMS.txt
(Get-AuthenticodeSignature .\AquaDesk-0.6.3.exe).SignerCertificate.Thumbprint
```

`Status` 显示「证书链终止于不受信任的根」是自签名的预期结果，不是签名坏了。
想在本机认它：`tools/trust_signing_cert.ps1`（可 `-Remove` 撤销）。
