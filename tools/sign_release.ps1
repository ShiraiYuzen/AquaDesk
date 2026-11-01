# 给发布清单做分离式签名（RSA-SHA256），与 exe 的 Authenticode 签名共用同一张证书。
#
# 为什么需要它：exe 的 Authenticode 签名只能证明「这个 exe 由这把私钥签过」，
# 但如果有人把 exe 换掉、再把 SHA256SUMS.txt 里的哈希一起改掉，单看哈希发现不了。
# 让私钥把清单本身也签一遍，改清单就会让签名失效。
#
# 用法：.\tools\sign_release.ps1 -DistDir .\dist
# 产物：SHA256SUMS.txt.sig（Base64）、SIGNING.txt（人读的签名信息）

[CmdletBinding()]
param(
    [string]$DistDir = (Join-Path $PSScriptRoot "..\dist"),
    [string]$SubjectLike = "*AquaDesk*"
)

$ErrorActionPreference = "Stop"

$sums = Join-Path $DistDir "SHA256SUMS.txt"
if (-not (Test-Path -LiteralPath $sums)) {
    Write-Host "  清单不存在：$sums" -ForegroundColor Yellow
    exit 1
}

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -like $SubjectLike } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) {
    Write-Host "  清单签名 跳过（未找到签名证书）" -ForegroundColor DarkGray
    exit 1
}

$rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
if (-not $rsa) {
    Write-Host "  清单签名 失败（证书没有可用的 RSA 私钥）" -ForegroundColor Yellow
    exit 1
}

# 签的是清单的原始字节（不是规范化后的文本），验签方必须按同样方式读
$bytes = [System.IO.File]::ReadAllBytes($sums)
$sig = $rsa.SignData($bytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

$sigPath = "$sums.sig"
[System.IO.File]::WriteAllText($sigPath, [Convert]::ToBase64String($sig), [System.Text.Encoding]::ASCII)

# 人读的签名信息：即使没有 README，拿到这三个文件也能核对
$info = @(
    "AquaDesk release signature"
    "signer-subject   = $($cert.Subject)"
    "signer-thumbprint= $($cert.Thumbprint)"
    "signer-not-after = $($cert.NotAfter.ToString('yyyy-MM-dd'))"
    "algorithm        = RSA-PKCS1-SHA256"
    "signed-file      = SHA256SUMS.txt"
    "signature-file   = SHA256SUMS.txt.sig (base64)"
    ""
    "verify-with      = .\verify_release.ps1   # 或仓库里的 tools\verify_release.ps1"
) -join "`r`n"
[System.IO.File]::WriteAllText((Join-Path $DistDir "SIGNING.txt"), $info,
    (New-Object System.Text.UTF8Encoding($true)))

Write-Host "  清单签名  SHA256SUMS.txt.sig  $($cert.Thumbprint)" -ForegroundColor DarkGray
