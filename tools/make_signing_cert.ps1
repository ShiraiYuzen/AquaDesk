# 生成本项目的代码签名证书（自签名，免费）
#
#   .\tools\make_signing_cert.ps1
#
# 做三件事：
#   1. 在 CurrentUser\My 里建一个 10 年期的代码签名证书（私钥不可导出，只留在本机证书库）
#   2. 把**公钥**导出到 certs\AquaDesk-CodeSigning.cer，随仓库分发
#   3. 打印指纹，供 README / Release 说明核对
#
# 注意：自签名证书不会被 Windows 默认信任 —— 用户要「装一次」这个 .cer
# （受信任的根 + 受信任的发布者），签名才在**他那台机器**上显示为可信发布者。
# 想免掉这一步只能买正式证书，或申请开源的免费签名（见 README）。

param(
    [string]$Subject = "CN=ShiraiYuzen (AquaDesk), O=ShiraiYuzen"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$certDir = Join-Path $root "certs"
New-Item -ItemType Directory -Force -Path $certDir | Out-Null
$cerPath = Join-Path $certDir "AquaDesk-CodeSigning.cer"

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $Subject } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($null -eq $cert) {
    Write-Host "创建自签名代码签名证书…" -ForegroundColor Cyan

    # 用 splatting 而不是反引号续行：PowerShell 5.1 对长参数列表的续行解析很脆
    $createArgs = @{
        Type               = "CodeSigningCert"
        Subject            = $Subject
        KeyUsage           = "DigitalSignature"
        KeySpec            = "Signature"
        KeyExportPolicy    = "NonExportable"
        NotAfter           = (Get-Date).AddYears(10)
        CertStoreLocation  = "Cert:\CurrentUser\My"
    }

    $cert = New-SelfSignedCertificate @createArgs
}
else {
    Write-Host "已存在签名证书，直接复用" -ForegroundColor Cyan
}

Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null

Write-Host ""
Write-Host "证书就绪" -ForegroundColor Green
Write-Host "  主题    $($cert.Subject)"
Write-Host "  指纹    $($cert.Thumbprint)"
Write-Host "  有效期  至 $($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "  公钥    $cerPath"
Write-Host ""
Write-Host "下一步：重新跑 .\build.ps1，产物会自动签名。" -ForegroundColor DarkGray
