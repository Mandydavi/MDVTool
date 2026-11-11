param(
    [switch]$SkipBuild,
    [switch]$PushOnly,
    [string]$Version = "",
    [string]$Note = "",
    [string]$Feed = "",
    [string]$PushTo = ""
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root "dist"
$appProj = Join-Path $root "src\MDVTool.Desktop\MDVTool.csproj"
$stubProj = Join-Path $root "src\MDVTool.Pack\MDVTool.Pack.csproj"
$stubExe = Join-Path $root "src\MDVTool.Pack\bin\Release\net48\MDVPack.exe"

if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

function Resolve-Version {
    if ($Version) { return $Version }
    if (Test-Path $appProj) {
        $m = [regex]::Match((Get-Content $appProj -Raw), '<Version>([^<]+)</Version>')
        if ($m.Success) { return $m.Groups[1].Value }
    }
    return "1.0.0"
}

function Push-Files([string]$v) {
    if (-not $PushTo) { throw "-PushTo is required" }
    if (-not (Test-Path $PushTo)) { New-Item -ItemType Directory -Force $PushTo | Out-Null }
    $names = @("version.json", "MDVTool-$v-Setup.exe", "MDVTool-$v-Update.zip", "MDVTool-$v-Portable.zip")
    foreach ($n in $names) {
        $src = Join-Path $dist $n
        if (Test-Path $src) { Copy-Item $src $PushTo -Force }
    }
    Write-Host "PUSHED  -> $PushTo"
    Write-Host "FEED    -> $(Join-Path $PushTo 'version.json')"
}

$v = Resolve-Version

if ($PushOnly) {
    Push-Files $v
    return
}

Write-Host "Building package stub..."
& dotnet build $stubProj -c Release -v:q -nologo
if ($LASTEXITCODE -ne 0) { throw "stub build failed" }
if (-not (Test-Path $stubExe)) { throw "stub exe missing: $stubExe" }

$stage = Join-Path $env:TEMP ("mdvpack-" + [guid]::NewGuid().ToString("N"))
$payload = Join-Path $stage "payload"
New-Item -ItemType Directory -Force $payload | Out-Null

if ($SkipBuild) {
    $main = Join-Path $root "dist\MDV Tool.exe"
    Write-Host "Reusing existing main exe"
} else {
    $pub = Join-Path $stage "publish"
    Write-Host "Publishing latest self-contained single file..."
    & dotnet publish $appProj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -o $pub -v:q -nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
    $main = Join-Path $pub "MDVTool.exe"
}
if (-not (Test-Path $main)) { throw "main exe not found: $main" }
Copy-Item $main (Join-Path $payload "MDV Tool.exe") -Force

foreach ($f in @("README.md", "LICENSE", "setup.json")) {
    $p = Join-Path $root $f
    if (Test-Path $p) { Copy-Item $p $payload -Force }
}
foreach ($f in @("aria2c.exe", "MDVSetup.exe")) {
    $p = Join-Path $root ("bin\" + $f)
    if (Test-Path $p) { Copy-Item $p $payload -Force }
}
$extraDoc = Join-Path $root "docs\DISTRIBUTION.txt"
if (Test-Path $extraDoc) { Copy-Item $extraDoc $payload -Force }
$extSrc = Join-Path $root "extensions\BrowserExtension"
if (Test-Path $extSrc) {
    Copy-Item $extSrc (Join-Path $payload "BrowserExtension") -Recurse -Force
}

Write-Host "Compressing portable zip..."
$portableTmp = Join-Path $stage "portable.zip"
Compress-Archive -Path (Join-Path $payload "*") -DestinationPath $portableTmp -CompressionLevel Optimal
$portable = Join-Path $dist "MDVTool-$v-Portable.zip"
[IO.File]::Copy($portableTmp, $portable, $true)

$stubBytes = [IO.File]::ReadAllBytes($stubExe)
$zipBytes = [IO.File]::ReadAllBytes($portableTmp)

function Write-Pack([string]$mode, [string]$out) {
    $cfg = '{"mode":"' + $mode + '","name":"MDV Tool","version":"' + $v +
           '","exe":"MDV Tool.exe","id":"MDVTool"}'
    $cfgBytes = [Text.Encoding]::UTF8.GetBytes($cfg)
    $fs = [IO.File]::Create($out)
    try {
        $fs.Write($stubBytes, 0, $stubBytes.Length)
        $fs.Write($zipBytes, 0, $zipBytes.Length)
        $fs.Write($cfgBytes, 0, $cfgBytes.Length)
        $fs.Write([Text.Encoding]::ASCII.GetBytes("MDVPACK1"), 0, 8)
        $fs.Write([BitConverter]::GetBytes([int64]$zipBytes.Length), 0, 8)
        $fs.Write([BitConverter]::GetBytes([int64]$cfgBytes.Length), 0, 8)
    } finally { $fs.Close() }
    Write-Host "  $out"
}

Write-Host "Building Setup.exe and SingleFile.exe..."
Write-Pack "setup" (Join-Path $dist "MDVTool-$v-Setup.exe")
Write-Pack "sfx" (Join-Path $dist "MDVTool-$v-SingleFile.exe")

Write-Host "Building bundle zip..."
$bundle = Join-Path $stage "bundle"
$mainDir = Join-Path $bundle "Main"
$instDir = Join-Path $bundle "Installer"
New-Item -ItemType Directory -Force $mainDir | Out-Null
New-Item -ItemType Directory -Force $instDir | Out-Null
Copy-Item (Join-Path $payload "*") $mainDir -Recurse -Force
Copy-Item (Join-Path $root "bin\MDVSetup.exe") $instDir -Force
Copy-Item (Join-Path $root "setup.json") $instDir -Force
if (Test-Path $extSrc) {
    Copy-Item $extSrc (Join-Path $bundle "BrowserExtension") -Recurse -Force
}
$manifest = [ordered]@{
    product   = "MDV Tool"
    version   = $v
    generated = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
    contents  = @(
        [ordered]@{ path = "Main"; desc = "Portable app + engine, offline ready" },
        [ordered]@{ path = "Installer"; desc = "Online installer and its config" },
        [ordered]@{ path = "BrowserExtension"; desc = "Chrome / Edge sniffing extension" }
    )
} | ConvertTo-Json -Depth 5
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $bundle "MANIFEST.json"), $manifest, $utf8NoBom)
$bundleZip = Join-Path $dist "MDVTool-$v-Bundle.zip"
Compress-Archive -Path (Join-Path $bundle "*") -DestinationPath $bundleZip -CompressionLevel Optimal -Force

Write-Host "Writing version.json and update zip..."
$mainPath = Join-Path $payload "MDV Tool.exe"
$size = (Get-Item $mainPath).Length
$sha = (Get-FileHash $mainPath -Algorithm SHA256).Hash.ToLowerInvariant()
$setupName = "MDVTool-$v-Setup.exe"
if ($Feed) { $updateUrl = $Feed.TrimEnd('/') + "/" + $setupName } else { $updateUrl = $setupName }
$notes = if ($Note) { $Note } else { "MDV Tool $v" }
$verObj = [ordered]@{
    version = $v
    date    = (Get-Date -Format "yyyy-MM-dd")
    size    = $size
    url     = $updateUrl
    notes   = $notes
    sha256  = $sha
}
$verJson = $verObj | ConvertTo-Json
if (-not $utf8NoBom) { $utf8NoBom = New-Object System.Text.UTF8Encoding($false) }
[IO.File]::WriteAllText((Join-Path $dist "version.json"), $verJson, $utf8NoBom)

$upd = Join-Path $stage "update"
New-Item -ItemType Directory -Force $upd | Out-Null
Copy-Item $mainPath $upd -Force
$engine = Join-Path $payload "aria2c.exe"
if (Test-Path $engine) { Copy-Item $engine $upd -Force }
Copy-Item (Join-Path $dist "version.json") $upd -Force
$updZip = Join-Path $dist "MDVTool-$v-Update.zip"
Compress-Archive -Path (Join-Path $upd "*") -DestinationPath $updZip -CompressionLevel Optimal -Force

if ($PushTo) { Push-Files $v }

try { [IO.Directory]::Delete($stage, $true) } catch { }

Write-Host ""
Write-Host "DONE  version $v  ->  $dist"
Get-ChildItem $dist -File | Where-Object { $_.Name -like "*$v*" -or $_.Name -eq "version.json" } |
    ForEach-Object { Write-Host ("  {0,-42} {1,10:N0} bytes" -f $_.Name, $_.Length) }
Write-Host ""
Write-Host "Push to a share / USB / intranet folder with:"
Write-Host "  powershell -ExecutionPolicy Bypass -File pack.ps1 -PushOnly -PushTo <dir>"
