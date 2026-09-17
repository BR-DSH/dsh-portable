# =============================================================================
#  repair-natives.ps1 —— 补齐 pnpm store 无法重建的原生/下载产物
#
#  背景：pnpm 的离线 store 只保存"包的原始文件"，安装脚本产出的东西
#  （node-gyp 编译结果、postinstall 下载的 exe）不在其中。内网机器
#  无法联网获取，所以随包放在 vendor\native-fixups\ 里，首启复制回位。
#
#  由 start-dsh.ps1 在依赖重建之后自动调用；也可手动单独运行。
# =============================================================================
param(
    [string]$Root = (Split-Path -Parent $MyInvocation.MyCommand.Path)
)

$src = Join-Path $Root 'vendor\native-fixups'
if (-not (Test-Path -LiteralPath $src -PathType Container)) { return }

$dest = Join-Path $Root 'dsh-home\profiles\web\node_modules'
if (-not (Test-Path -LiteralPath $dest -PathType Container)) { return }

$copied = 0
Get-ChildItem -LiteralPath $src -Recurse -File -Force | ForEach-Object {
    $rel = $_.FullName.Substring($src.Length + 1)
    $target = Join-Path $dest $rel
    if (Test-Path -LiteralPath $target -PathType Leaf) { return }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    $script:copied++
}

if ($copied -gt 0) {
    Write-Host "[提示] 已补齐 $copied 个离线缓存无法重建的原生/下载文件。" -ForegroundColor Green
}
