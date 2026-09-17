# =============================================================================
#  fix-corp-cert.ps1 —— 让 Node 信任公司 TLS 代理的根证书
#
#  症状：公司网络用 TLS 中间人代理；Windows 信任它，但 Node 用自己的 CA 列表，
#        于是 pnpm / 模型 API 调用会报 SELF_SIGNED_CERT_IN_CHAIN 或
#        UNABLE_TO_VERIFY_LEAF_SIGNATURE。
#
#  做法：连接你的模型端点，抓取服务器实际证书链，导出 PEM，并通过
#        app-npm\.env 的 NODE_EXTRA_CA_CERTS 注入给 DSH 子进程。
#
#  用法：解压后双击运行，或在包根目录执行
#        powershell -ExecutionPolicy Bypass -File fix-corp-cert.ps1
#        然后 stop-dsh.bat + start-dsh.bat 重启生效
# =============================================================================
$ErrorActionPreference = 'Continue'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not (Test-Path (Join-Path $root 'app-npm'))) { $root = (Get-Location).Path }

# 需要抓取证书的端点（按你 settings.yaml 里配置的 provider 改）
$targets = @('api.deepseek.com', 'api.scnet.cn')

$out  = Join-Path $root 'corp-root-ca.pem'
$pems = New-Object System.Collections.Generic.List[string]

foreach ($h in $targets) {
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $tcp.Connect($h, 443)
        $ssl = New-Object System.Net.Security.SslStream($tcp.GetStream(), $false, { $true })
        $ssl.AuthenticateAsClient($h)

        $cert  = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($ssl.RemoteCertificate)
        $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
        $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
        $null = $chain.Build($cert)

        $n = 0
        foreach ($el in $chain.ChainElements) {
            $pem = "-----BEGIN CERTIFICATE-----`n" +
                   [Convert]::ToBase64String($el.Certificate.RawData, 'InsertLineBreaks') +
                   "`n-----END CERTIFICATE-----"
            if (-not $pems.Contains($pem)) { $pems.Add($pem); $n++ }
        }
        Write-Host ("[ok]   {0}：抓到 {1} 级证书链" -f $h, $n) -ForegroundColor Green
        $ssl.Dispose(); $tcp.Close()
    } catch {
        Write-Host ("[warn] {0} 连接失败：{1}" -f $h, $_.Exception.Message) -ForegroundColor Yellow
    }
}

if ($pems.Count -eq 0) {
    Write-Host '[错误] 没有抓到任何证书。确认端点可达（浏览器能打开）后重试。' -ForegroundColor Red
    exit 1
}

Set-Content -LiteralPath $out -Value ($pems -join "`n") -Encoding ASCII
Write-Host ("已写出根证书链: {0}（{1} 个证书）" -f $out, $pems.Count) -ForegroundColor Cyan

# 注入 app-npm\.env（start-dsh.ps1 会把 .env 里的变量传给 DSH 子进程）
$appEnv = Join-Path $root 'app-npm\.env'
if (-not (Test-Path $appEnv)) {
    $example = Join-Path $root '.env.example'
    if (Test-Path $example) { Copy-Item $example $appEnv }
    else { Set-Content -LiteralPath $appEnv -Value '' -Encoding UTF8 }
}
$lines = @(Get-Content -LiteralPath $appEnv -ErrorAction SilentlyContinue)
if ($lines -match '^\s*NODE_EXTRA_CA_CERTS\s*=') {
    Write-Host 'app-npm\.env 里已有 NODE_EXTRA_CA_CERTS，未改动。' -ForegroundColor Yellow
} else {
    Add-Content -LiteralPath $appEnv -Value '' -Encoding UTF8
    Add-Content -LiteralPath $appEnv -Value '# 公司 TLS 代理根证书（fix-corp-cert.ps1 生成）' -Encoding UTF8
    Add-Content -LiteralPath $appEnv -Value ("NODE_EXTRA_CA_CERTS=" + $out) -Encoding UTF8
    Write-Host ("已写入 app-npm\.env: NODE_EXTRA_CA_CERTS={0}" -f $out) -ForegroundColor Green
}

Write-Host ''
Write-Host '完成。请执行 stop-dsh.bat 后再 start-dsh.bat 使其生效。' -ForegroundColor Green
