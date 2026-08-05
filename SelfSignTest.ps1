# SelfSignTest.ps1
# 本地自签名部署脚本（仅本机有效，用于开发期体验 uiAccess 强置顶）。
# 流程：生成/复用自签名代码签名证书 → 将其根导入本机“受信任的根证书颁发机构” →
#       临时把 app.manifest 的 uiAccess 改为 true 并重建 → 对 exe 签名 →
#       复制发布文件到 Program Files\LunchHelper。
#
# 必须以管理员身份运行（写入 Program Files 与 LocalMachine 证书存储需要提权）。
#
# 安全提示：脚本会把一个自签名证书加入本机“受信任的根证书颁发机构”。
# 这会使本机信任由该证书签名的任意程序，仅在个人本地部署时使用，切勿用于他机，
# 用毕如需撤销可运行本脚本配套的 Remove-SelfSignCert（或手动在 certmgr 删除该根）。

param(
    [string]$ProjectDir = $PSScriptRoot,
    [string]$DeployDir  = "$env:ProgramFiles\LunchHelper",
    [string]$CertName   = "LunchHelper Local UIACCESS CA",
    [int]$ValidYears    = 10
)

$ErrorActionPreference = "Stop"

# 管理员检查
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "必须以管理员身份运行（右键 PowerShell → 以管理员身份运行）。"
    exit 1
}

# 1. 证书：生成或复用自签名代码签名证书
$cert = Get-ChildItem "Cert:\LocalMachine\My" |
    Where-Object { $_.Subject -eq "CN=$CertName" } | Select-Object -First 1
if (-not $cert) {
    Write-Host "[1/5] 生成自签名代码签名证书：$CertName"
    $cert = New-SelfSignedCertificate -CertStoreLocation "Cert:\LocalMachine\My" `
        -Subject "CN=$CertName" -KeySpec KeyExchange -KeyLength 2048 `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3") `
        -NotAfter (Get-Date).AddYears($ValidYears)

    # 将公钥导入“受信任的根证书颁发机构”，使签名链在本机可信（安全相关步骤）
    $rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "LocalMachine")
    $rootStore.Open("ReadWrite")
    $rootStore.Add($cert)
    $rootStore.Close()
    Write-Host "      已将证书根导入 LocalMachine\Root（受信任根）。此证书仅本机信任。" -ForegroundColor Yellow
} else {
    Write-Host "[1/5] 复用已存在的证书：$CertName"
}

# 2~3. 临时翻转 app.manifest 的 uiAccess 为 true 并重建（重建后恢复，保持源码默认 false）
$manifest    = Join-Path $ProjectDir "app.manifest"
$manifestBak = "$manifest.bak"
Copy-Item $manifest $manifestBak -Force
try {
    Write-Host "[2/5] 临时将 app.manifest 的 uiAccess 改为 true"
    $content = (Get-Content $manifest -Raw) -replace 'uiAccess="false"', 'uiAccess="true"'
    Set-Content $manifest $content -NoNewline

    Write-Host "[3/5] 重新构建（调用 build.bat）"
    Push-Location $ProjectDir
    & cmd /c build.bat
    if ($LASTEXITCODE -ne 0) { throw "build.bat 返回非零退出码：$LASTEXITCODE" }
    Pop-Location
} finally {
    Write-Host "      恢复 app.manifest 为 uiAccess=false"
    Move-Item $manifestBak $manifest -Force
}

# 4. 对 exe 签名（uiAccess 的硬性前提：必须经过证书链可信的签名）
$exe = Join-Path $ProjectDir "bin\Release\LunchHelper.exe"
if (-not (Test-Path $exe)) { throw "未找到构建产物：$exe" }
Write-Host "[4/5] 使用自签名证书对 exe 签名"
try {
    Set-AuthenticodeSignature -FilePath $exe -Certificate $cert `
        -TimestampServerUrl "http://timestamp.digicert.com" -HashAlgorithm SHA256
} catch {
    # 自签名证书无法被公共时间戳服务背书时，退化为无时间戳签名（本地 uiAccess 仍有效）
    Write-Host "      时间戳失败，改用无时间戳签名（本地有效）。" -ForegroundColor Yellow
    Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256
}

# 5. 部署所需的发布文件到 Program Files
Write-Host "[5/5] 部署到 $DeployDir"
if (-not (Test-Path $DeployDir)) { New-Item -ItemType Directory -Path $DeployDir -Force | Out-Null }
$releaseDir = Join-Path $ProjectDir "bin\Release"
$items = @(
    "LunchHelper.exe",
    "Microsoft.Web.WebView2.Core.dll",
    "Microsoft.Web.WebView2.WinForms.dll",
    "Microsoft.Web.WebView2.Wpf.dll",
    "WebView2Loader.dll",
    "runtimes",
    "plugins",
    "LICENSE",
    "README.md",
    "THIRD-PARTY-NOTICES.md"
)
foreach ($item in $items) {
    $src = Join-Path $releaseDir $item
    if (Test-Path $src) { Copy-Item $src $DeployDir -Recurse -Force }
}

Write-Host "完成。请在 $DeployDir 下双击 LunchHelper.exe 验证 uiAccess 强置顶（应可覆盖任务管理器）。" -ForegroundColor Green
Write-Host "注意：实际被调用的必须是此 Program Files 副本；若由 ClassIsland 拉起，请将其指向该路径。" -ForegroundColor Cyan
