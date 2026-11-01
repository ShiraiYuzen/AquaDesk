# 生成本项目的代码签名证书（自签名，免费）
#
#   .\tools\make_signing_cert.ps1              # 复用已有证书
#   .\tools\make_signing_cert.ps1 -New         # 强制新建（换证书 / 私钥丢失时）
#
# 做四件事：
#   1. 在 CurrentUser\My 里建一个 10 年期的代码签名证书
#   2. 把**公钥**导出到 certs\AquaDesk-CodeSigning.cer，随仓库分发
#   3. 把**私钥**导出成 certs\private\*.pfx（带密码），供离线备份
#   4. 打印指纹，供 README / Release 说明核对
#
# 关于 KeyExportPolicy：
#   0.6.3 及之前用的是 NonExportable —— 私钥偷不走，但**丢了也找不回来**。
#   机器重装 / 换电脑就意味着指纹永久作废，整条「指纹锚点」信任链断掉。
#   所以改成 Exportable：允许导出一次 .pfx 做离线备份，代价是私钥可被导出，
#   因此 .pfx 必须放进密码管理器或 U 盘，绝不能进仓库（.gitignore 已挡住）。
#
# 注意：自签名证书不会被 Windows 默认信任 —— 用户要「装一次」这个 .cer
# （受信任的根 + 受信任的发布者），签名才在**他那台机器**上显示为可信发布者。

param(
    [string]$Subject = "CN=ShiraiYuzen (AquaDesk), O=ShiraiYuzen",
    [switch]$New,
    [string]$PfxPassword = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$certDir = Join-Path $root "certs"
$privateDir = Join-Path $certDir "private"
$legacyDir = Join-Path $certDir "legacy"
New-Item -ItemType Directory -Force -Path $certDir | Out-Null

$cerPath = Join-Path $certDir "AquaDesk-CodeSigning.cer"

$cert = $null
if (-not $New) {
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $Subject } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

if ($null -eq $cert) {
    Write-Host "创建自签名代码签名证书（私钥可导出，便于备份）…" -ForegroundColor Cyan

    # 用 splatting 而不是反引号续行：PowerShell 5.1 对长参数列表的续行解析很脆
    $createArgs = @{
        Type              = "CodeSigningCert"
        Subject           = $Subject
        KeyUsage          = "DigitalSignature"
        KeySpec           = "Signature"
        KeyExportPolicy   = "Exportable"
        KeyLength         = 3072
        NotAfter          = (Get-Date).AddYears(10)
        CertStoreLocation = "Cert:\CurrentUser\My"
    }

    $cert = New-SelfSignedCertificate @createArgs
}
else {
    Write-Host "已存在签名证书，直接复用（要换请加 -New）" -ForegroundColor Cyan
}

# 公钥：指纹变了就把旧的那张归档，别直接覆盖掉历史
if (Test-Path -LiteralPath $cerPath) {
    $old = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cerPath
    if ($old.Thumbprint -ne $cert.Thumbprint) {
        New-Item -ItemType Directory -Force -Path $legacyDir | Out-Null
        $legacyPath = Join-Path $legacyDir "AquaDesk-CodeSigning-$($old.Thumbprint.Substring(0,8)).cer"
        Move-Item -LiteralPath $cerPath -Destination $legacyPath -Force
        Write-Host "  旧公钥归档  $legacyPath" -ForegroundColor DarkGray
    }
}

Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null

# 私钥：导出一份带密码的 pfx 供离线备份
$pfxPath = $null
if ($cert.HasPrivateKey) {
    if ([string]::IsNullOrWhiteSpace($PfxPassword)) {
        # 随机密码：避免把「密码写在私钥旁边」变成没有密码
        $bytes = New-Object byte[] 18
        [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $PfxPassword = [Convert]::ToBase64String($bytes)
    }

    New-Item -ItemType Directory -Force -Path $privateDir | Out-Null
    $stamp = (Get-Date).ToString("yyyyMMdd")
    $pfxPath = Join-Path $privateDir "AquaDesk-CodeSigning-$stamp.pfx"

    try {
        $sec = ConvertTo-SecureString -String $PfxPassword -Force -AsPlainText
        Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $sec | Out-Null
    }
    catch {
        Write-Host "  私钥导出失败：$($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host "  说明这张证书不是 Exportable（0.6.3 之前建的），要备份只能 -New 重建" -ForegroundColor Yellow
        $pfxPath = $null
    }
}

Write-Host ""
Write-Host "证书就绪" -ForegroundColor Green
Write-Host "  主题    $($cert.Subject)"
Write-Host "  指纹    $($cert.Thumbprint)"
Write-Host "  有效期  至 $($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "  公钥    $cerPath"

if ($pfxPath) {
    $pwdFile = Join-Path $privateDir "PFX-PASSWORD.txt"
    @(
        "pfx      = $([System.IO.Path]::GetFileName($pfxPath))"
        "password = $PfxPassword"
        "thumbprint = $($cert.Thumbprint)"
        ""
        "把 pfx 和这串密码写进密码管理器 / 存到 U 盘，然后删掉本文件。"
        "这个目录已被 .gitignore 排除，但别指望它安全 —— 密码和私钥放一起等于没加密。"
    ) | Set-Content -LiteralPath $pwdFile -Encoding UTF8

    Write-Host "  私钥    $pfxPath" -ForegroundColor Yellow
    Write-Host "  密码    $PfxPassword" -ForegroundColor Yellow
    Write-Host "          （同时写在 $pwdFile，转存后请删除该文件）" -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "重要：没有这份 pfx，重装系统后就再也签不出同一指纹的包。" -ForegroundColor Yellow
}
else {
    Write-Host "  私钥    未导出（不是 Exportable 证书）" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "下一步：重新跑 .\build.ps1，产物会自动签名。" -ForegroundColor DarkGray
