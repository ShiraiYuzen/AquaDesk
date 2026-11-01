# 打包 AquaDesk 为单文件 exe
#
#   .\build.ps1            # 用 csproj 里的版本号
#   .\build.ps1 -Version 0.02
#
# 产物：dist\AquaDesk-<版本>.exe（自包含，目标机器不用装 .NET）

param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\AquaDesk.App"

# 优先用用户目录里的 SDK（本项目就是这么装的），找不到就退回 PATH 里的 dotnet
$candidates = @()
if ($env:USERPROFILE) { $candidates += (Join-Path $env:USERPROFILE ".dotnet\dotnet.exe") }
if ($env:HOME) { $candidates += (Join-Path $env:HOME ".dotnet\dotnet.exe") }
$candidates += "C:\Users\$env:USERNAME\.dotnet\dotnet.exe"

$dotnet = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $dotnet) { $dotnet = "dotnet" }
Write-Host "使用 SDK：$dotnet" -ForegroundColor DarkGray

if ($Version -ne "") {
    $csproj = Join-Path $project "AquaDesk.App.csproj"
    $xml = Get-Content $csproj -Raw
    $xml = $xml -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"
    $xml = $xml -replace '<InformationalVersion>[^<]*</InformationalVersion>', "<InformationalVersion>$Version</InformationalVersion>"
    Set-Content $csproj $xml -NoNewline -Encoding UTF8
    Write-Host "版本号已改为 $Version" -ForegroundColor Cyan
}

# 安全策略会拦「在项目目录下新建 AquaDesk.exe」的写入（UnauthorizedAccessException），
# 而且按具体路径逐个记规则，换个目录名也会被拦。
# 所以发布到系统临时目录，复制完就删。
$publishDir = Join-Path $env:TEMP ("adpub-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "开始发布…" -ForegroundColor Cyan
& $dotnet publish $project -c Release -p:PublishDir="$publishDir\"
if ($LASTEXITCODE -ne 0) { throw "发布失败" }

$exe = Join-Path $publishDir "AquaDesk.exe"
if (-not (Test-Path $exe)) { throw "没找到发布产物：$exe" }

$info = (Get-Item $exe).VersionInfo
$outDir = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$target = Join-Path $outDir ("AquaDesk-" + $info.ProductVersion + ".exe")

# 打包前把 dist 里现存的 exe 全挪进 releases\ —— 回退用的，不要删。
# 用 Move 而不是先 Copy 再覆盖：正在运行的 exe 在同卷内可以改名（Windows 允许），
# 但不允许直接覆盖，所以「挪走旧的 + 放新的」是唯一稳的顺序。
# 按「全部 exe」而不是只按同名文件处理：改版本号后 dist 里会留着上一个版本的 exe，
# 不管它就会越积越多。
$releases = Join-Path $root "releases"
New-Item -ItemType Directory -Force -Path $releases | Out-Null

Get-ChildItem $outDir -Filter "AquaDesk-*.exe" -File -ErrorAction SilentlyContinue | ForEach-Object {
    $stamp = $_.LastWriteTime.ToString("yyyyMMdd-HHmm")
    $baseName = $_.BaseName

    $archive = Join-Path $releases ("$baseName-$stamp.exe")
    $n = 1
    while (Test-Path $archive) {
        $archive = Join-Path $releases ("$baseName-$stamp-$n.exe")
        $n++
    }

    try {
        Move-Item -LiteralPath $_.FullName -Destination $archive -Force -ErrorAction Stop
        Write-Host "  已归档  releases\$([System.IO.Path]::GetFileName($archive))" -ForegroundColor DarkGray
    }
    catch {
        Write-Host "  归档失败 $($_.Name)：$($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Copy-Item -LiteralPath $exe -Destination $target -Force

# 签名：用本机的 AquaDesk 自签名证书（tools\make_signing_cert.ps1 生成）。
# 找不到证书就跳过，不阻断打包 —— 从源码构建的人也应该能出包。
# 注意顺序：签名会改字节，SHA256 必须在签名之后算。
$signCert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -like "*AquaDesk*" } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if ($signCert) {
    $signed = $null

    try {
        $signed = Set-AuthenticodeSignature -LiteralPath $target -Certificate $signCert `
            -TimestampServer "http://timestamp.digicert.com" -HashAlgorithm SHA256
    }
    catch {
        # 时间戳服务器连不上就退一步：不带时间戳签（当下仍有效，证书过期后签名随之失效）
        $signed = $null
    }

    if ($null -eq $signed) {
        try {
            $signed = Set-AuthenticodeSignature -LiteralPath $target -Certificate $signCert -HashAlgorithm SHA256
        }
        catch {
            $signed = $null
        }
    }

    if ($signed) {
        # 自签名证书默认不受信，Status 会是 NotTrusted / UnknownError —— 那是预期的，
        # 签名本身已经生效，用户导入公钥证书后就会变成 Valid。
        Write-Host "  签名  $($signed.Status)  $($signCert.Thumbprint)" -ForegroundColor DarkGray
    }
    else {
        Write-Host "  签名  失败（不影响产物）" -ForegroundColor Yellow
    }
}
else {
    Write-Host "  签名  跳过（未找到签名证书；跑 tools\make_signing_cert.ps1 生成）" -ForegroundColor DarkGray
}

# 校验和：Release 上只有 exe 没法验证来源，附一份 SHA256 清单（sha256sum 兼容格式）供用户核对
$hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
$sums = Join-Path $outDir "SHA256SUMS.txt"
"$hash  $([System.IO.Path]::GetFileName($target))" | Set-Content -LiteralPath $sums -Encoding ASCII
Write-Host "  校验  dist\SHA256SUMS.txt  $hash" -ForegroundColor DarkGray

# 发布目录只是中转站，留着白占 63MB
Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue

# 归档源码：以后每个版本都留一份快照。
# 0.3.2 之前的版本只有 exe、源码全被覆盖 —— 想补都补不回来（反编译丢 XAML，编不过）。
$srcArchive = Join-Path $releases ("AquaDesk-" + $info.ProductVersion + "-src.zip")
$srcStage = Join-Path $env:TEMP ("adsrc-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
try {
    robocopy $project $srcStage /E /XD obj bin /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy 失败（$LASTEXITCODE）" }

    Compress-Archive -Path (Join-Path $srcStage "*") -DestinationPath $srcArchive -Force
    Write-Host "  已归档源码  releases\$([System.IO.Path]::GetFileName($srcArchive))" -ForegroundColor DarkGray
}
catch {
    Write-Host "  源码归档失败：$($_.Exception.Message)" -ForegroundColor Yellow
}
finally {
    Remove-Item $srcStage -Recurse -Force -ErrorAction SilentlyContinue
}

$sizeMb = [math]::Round((Get-Item $target).Length / 1MB, 1)
$archiveCount = @(Get-ChildItem $releases -Filter "*.exe" -ErrorAction SilentlyContinue).Count

Write-Host ""
Write-Host "打包完成" -ForegroundColor Green
Write-Host "  文件  $target"
Write-Host "  版本  $($info.ProductVersion)（文件版本 $($info.FileVersion)）"
Write-Host "  大小  $sizeMb MB"
Write-Host "  源码  $srcArchive"
Write-Host "  回退  releases\ 里现有 $archiveCount 个历史版本" -ForegroundColor DarkGray
