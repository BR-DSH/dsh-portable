# ===========================================================================
#  DshDesktop 构建脚本 —— 桌面版（内嵌 WebView2 的 DSH 界面）
#  自包含单文件（内置 .NET 运行时，目标电脑免安装）。
#  用法:  powershell -ExecutionPolicy Bypass -File build-desktop.ps1
# ===========================================================================
$ErrorActionPreference = 'Stop'

$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$pkgRoot = Split-Path -Parent $here          # ...\dsh
$app     = Join-Path $here 'DshDesktop\DshDesktop.csproj'
$publish = Join-Path $here 'publish-desktop'

Write-Host "[build] 发布 DshDesktop.exe (win-x64, 自包含单文件) ..." -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $app -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o $publish | Out-Null
if ($LASTEXITCODE -ne 0) { throw "DshDesktop 发布失败" }

$built = Join-Path $publish 'DshDesktop.exe'
$dest  = Join-Path $pkgRoot 'DshDesktop.exe'
Copy-Item $built $dest -Force

Write-Host "[build] 完成: $dest" -ForegroundColor Green
Get-Item $dest | Select-Object FullName, Length, LastWriteTime | Format-List
