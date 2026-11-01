# 把 AquaDesk 的签名公钥证书装进「受信任的根 + 受信任的发布者」
#
#   .\tools\trust_signing_cert.ps1          # 安装（只影响当前用户，不需要管理员）
#   .\tools\trust_signing_cert.ps1 -Remove  # 卸载
#
# 自签名证书默认不被 Windows 信任，所以签名状态会显示「证书链终止于不受信任的根」。
# 跑过这个脚本之后，本机再验证 AquaDesk-*.exe 的签名就会是 Valid，属性页里也显示发布者。
#
# 这只解决**你自己这台机器**；别人要看到可信发布者，得自己导入这个 .cer，
# 或者等上游换成正式证书（SignPath 免费开源签名 / Microsoft Trusted Signing）。

param(
    [switch]$Remove
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$cerPath = Join-Path $root "certs\AquaDesk-CodeSigning.cer"

if (-not (Test-Path $cerPath)) {
    throw "找不到公钥证书：$cerPath（先跑 tools\make_signing_cert.ps1）"
}

$thumbprint = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cerPath).Thumbprint

if ($Remove) {
    foreach ($store in @("Cert:\CurrentUser\Root", "Cert:\CurrentUser\TrustedPublisher")) {
        Get-ChildItem $store -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $thumbprint } |
            ForEach-Object {
                Remove-Item -LiteralPath $_.PSPath -Force
                Write-Host "已从 $store 移除" -ForegroundColor Yellow
            }
    }
}
else {
    foreach ($store in @("Cert:\CurrentUser\Root", "Cert:\CurrentUser\TrustedPublisher")) {
        Import-Certificate -FilePath $cerPath -CertStoreLocation $store | Out-Null
        Write-Host "已导入 $store" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "指纹 $thumbprint" -ForegroundColor DarkGray
Write-Host "验证：Get-AuthenticodeSignature .\dist\AquaDesk-*.exe" -ForegroundColor DarkGray
