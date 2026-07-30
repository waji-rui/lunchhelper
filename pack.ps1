# pack.ps1 — Build the portable release ZIP for LunchHelper.
#
# Copies the built exe + WebView2 runtime DLLs + plugins + docs into a staging
# folder and compresses it to LunchHelper-<Version>.zip. Shared by CI and the
# local build.bat so the published layout never drifts from local testing.
#
# Usage (CI):    powershell -File pack.ps1 -ReleaseDir bin\Release -Version v1.2.3 -OutDir .
# Usage (local): powershell -File pack.ps1   (defaults: bin\Release, version "dev")

param(
    [string]$ReleaseDir = "bin\Release",
    [string]$Version = "dev",
    [string]$OutDir = "."
)

$ErrorActionPreference = "Stop"

# 防御：去掉尾部反斜杠，避免 batch/CI 传入 "...\" 导致 Join-Path 拼出非法路径
$OutDir = $OutDir.TrimEnd('\')

$stage = Join-Path $env:TEMP ("lh_pack_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $stage | Out-Null

function Copy-IfExists($src, $dest) {
    if (Test-Path $src) { Copy-Item -Path $src -Destination $dest -Recurse -Force }
}

# 1) main executable
Copy-Item (Join-Path $ReleaseDir "LunchHelper.exe") $stage

# 2) managed + native DLLs sitting next to the exe (WebView2.*.dll, WebView2Loader.dll, ...)
Get-ChildItem (Join-Path $ReleaseDir "*.dll") | Copy-Item -Destination $stage -Force

# 3) WebView2 native loader under runtimes/ (present in some package layouts)
Copy-IfExists (Join-Path $ReleaseDir "runtimes") (Join-Path $stage "runtimes")

# 4) bundled plugins
Copy-IfExists (Join-Path $ReleaseDir "plugins") (Join-Path $stage "plugins")

# 5) license / readme / third-party notices from repo root
foreach ($f in @("LICENSE", "README.md", "THIRD-PARTY-NOTICES.md")) {
    if (Test-Path $f) { Copy-Item $f $stage }
}

$zip = Join-Path $OutDir ("LunchHelper-" + $Version + ".zip")
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -Force

Write-Host "PACKED -> $zip"
