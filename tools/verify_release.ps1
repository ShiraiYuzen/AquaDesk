# 验证 AquaDesk 下载是否出自作者本人。
#
# 三个文件放同一目录后运行：
#   AquaDesk-<版本>.exe
#   SHA256SUMS.txt
#   SHA256SUMS.txt.sig
#
#   .\verify_release.ps1
#
# 退出码 0 = 全部通过，1 = 有问题。

[CmdletBinding()]
param(
    [string]$Dir = $PSScriptRoot,
    [string]$ExePath = ""
)

$ErrorActionPreference = "Stop"

# 这张指纹必须与 README「验证作者身份」一节里写的一致。
# 它是整条信任链的锚点：指纹对上 = 用的是同一把私钥 = 同一个发布者。
$ExpectedThumbprint = "BF1DDC33C869D5C08E4417777F88E7041692390F"

function Fail($msg) {
    Write-Host ""
    Write-Host "  [X] $msg" -ForegroundColor Red
    exit 1
}

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $candidate = Get-ChildItem -LiteralPath $Dir -Filter "AquaDesk-*.exe" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $candidate) { Fail "没找到 AquaDesk-*.exe，用 -ExePath 指定路径" }
    $ExePath = $candidate.FullName
}

$exeDir = Split-Path -Parent $ExePath
$sumsPath = Join-Path $exeDir "SHA256SUMS.txt"
$sigPath = "$sumsPath.sig"

if (-not (Test-Path -LiteralPath $sumsPath)) { Fail "缺少 SHA256SUMS.txt（应和 exe 放同一目录）" }
if (-not (Test-Path -LiteralPath $sigPath)) { Fail "缺少 SHA256SUMS.txt.sig" }

Write-Host ""
Write-Host "  exe : $(Split-Path -Leaf $ExePath)" -ForegroundColor Gray
Write-Host ""

# ---- 第 1 步：exe 的哈希是否与清单一致 ----
$actual = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash.ToLowerInvariant()
$listed = $null
foreach ($line in Get-Content -LiteralPath $sumsPath) {
    if ($line -match '^\s*([0-9a-fA-F]{64})\s+\*?(.+?)\s*$') {
        if ($Matches[2] -eq (Split-Path -Leaf $ExePath)) { $listed = $Matches[1].ToLowerInvariant(); break }
    }
}
if (-not $listed) { Fail "SHA256SUMS.txt 里没有这个 exe 的条目" }

if ($actual -ne $listed) {
    Fail "SHA256 不匹配`n     清单: $listed`n     实际: $actual`n     文件被改过，别运行"
}
Write-Host "  [1/3] SHA256 与清单一致" -ForegroundColor Green

# ---- 第 2 步：清单的签名是否可信（用 exe 自带的签名者证书验）----
$auth = Get-AuthenticodeSignature -LiteralPath $ExePath
if (-not $auth.SignerCertificate) { Fail "exe 没有 Authenticode 签名" }

$thumb = $auth.SignerCertificate.Thumbprint
if ($thumb -ne $ExpectedThumbprint) {
    Fail "签名证书指纹不符`n     期望: $ExpectedThumbprint`n     实际: $thumb`n     这不是作者发布的文件"
}
Write-Host "  [2/3] 签名证书指纹与 README 公布的一致" -ForegroundColor Green

# ---- 第 3 步：清单确实被这把私钥签过（防「换 exe + 改哈希」）----
$rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($auth.SignerCertificate)
if (-not $rsa) { Fail "取不到证书公钥" }

$sigB64 = (Get-Content -LiteralPath $sigPath -Raw).Trim()
try { $sig = [Convert]::FromBase64String($sigB64) } catch { Fail "签名文件不是合法的 Base64" }

$sumsBytes = [System.IO.File]::ReadAllBytes($sumsPath)
$ok = $rsa.VerifyData($sumsBytes, $sig,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

if (-not $ok) { Fail "清单签名无效（SHA256SUMS.txt 被改过）" }
Write-Host "  [3/3] 清单签名有效" -ForegroundColor Green

Write-Host ""
Write-Host "  通过：文件出自 CN=ShiraiYuzen (AquaDesk) 这把私钥，且与 README 公布的指纹一致。" -ForegroundColor Green
Write-Host ""
exit 0
