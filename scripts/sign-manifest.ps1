<#
 给 version.json 签名的脚本（ECDSA P-256 / SHA-256，openssl 实现）。

 用法：
   powershell -ExecutionPolicy Bypass -File scripts\sign-manifest.ps1 -Path dist\version.json

 签名明文（必须与 src/MDVTool.Desktop/UpdateService.cs 里的 SignedPayload 完全一致）：
   "{version}|{url}|{sha256小写}|{size}"

 私钥默认放 %USERPROFILE%\.mdv\signing-key.pem，不进仓库；
 客户端只内置公钥，即使更新服务器被攻破，没有私钥也签不出能通过校验的清单。
#>
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$Key = (Join-Path $env:USERPROFILE '.mdv\signing-key.pem')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Path)) { throw "找不到清单：$Path" }
if (-not (Test-Path $Key)) { throw "找不到签名私钥：$Key" }

# openssl 不一定在 PATH 里（Git for Windows 自带一份）
$openssl = (Get-Command openssl -ErrorAction SilentlyContinue)?.Source
if (-not $openssl) {
    foreach ($p in @("$env:ProgramFiles\Git\usr\bin\openssl.exe",
                     "$env:ProgramFiles\Git\mingw64\bin\openssl.exe",
                     "$env:ProgramFiles\OpenSSL-Win64\bin\openssl.exe")) {
        if (Test-Path $p) { $openssl = $p; break }
    }
}
if (-not $openssl) { throw "找不到 openssl，请安装 Git for Windows 或 OpenSSL" }

$utf8 = New-Object System.Text.UTF8Encoding($false)
$json = [IO.File]::ReadAllText((Resolve-Path $Path), $utf8)
$obj = $json | ConvertFrom-Json

$version = [string]$obj.version
$url = [string]$obj.url
$sha = ([string]$obj.sha256).ToLowerInvariant()
$size = [string]$obj.size

if ([string]::IsNullOrWhiteSpace($version) -or [string]::IsNullOrWhiteSpace($url) -or
    [string]::IsNullOrWhiteSpace($sha) -or [string]::IsNullOrWhiteSpace($size)) {
    throw "清单字段不全（需要 version / url / sha256 / size），拒绝签名"
}

$payload = "$version|$url|$sha|$size"
$tmpData = [IO.Path]::GetTempFileName()
$tmpSig = [IO.Path]::GetTempFileName()
try {
    [IO.File]::WriteAllText($tmpData, $payload, $utf8)          # 不能带换行，字节要和客户端算的一致

    & $openssl dgst -sha256 -sign $Key -out $tmpSig $tmpData
    if ($LASTEXITCODE -ne 0) { throw "openssl 签名失败" }

    $sigB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($tmpSig))

    if ($obj.PSObject.Properties.Name -contains 'sig') {
        $obj.sig = $sigB64
    } else {
        $obj | Add-Member -NotePropertyName sig -NotePropertyValue $sigB64
    }

    $out = $obj | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText((Resolve-Path $Path), $out, $utf8)

    Write-Host "SIGNED  $Path"
    Write-Host "  payload = $payload"
    Write-Host "  sig     = $($sigB64.Substring(0, [Math]::Min(44, $sigB64.Length)))..."
}
finally {
    Remove-Item $tmpData, $tmpSig -Force -ErrorAction SilentlyContinue
}
