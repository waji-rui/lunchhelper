<#
  LunchHelper local uiAccess self-sign test script (local machine only, do not distribute)
  Purpose: experience real uiAccess topmost behavior on this machine.
  Requirements:
    1. Run this script as Administrator (installing the cert into Trusted Root CA needs admin).
    2. app.manifest must have uiAccess="true" (already set for you).
    3. Build LunchHelper.exe first (VS / build.bat).
  Note: self-signed cert in Trusted Root CA is trusted only on this machine; not for others.
  Signing: uses signtool.exe if present, otherwise falls back to PowerShell's
           built-in Set-AuthenticodeSignature (no Windows SDK needed).
#>

param(
    [string]$ExePath = "",
    [string]$CertName = "LunchHelper Test"
)

# 1) Admin check
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Run PowerShell as Administrator."
    exit 1
}

# 2) Locate built exe
if (-not $ExePath) {
    Write-Error "Specify -ExePath, e.g. .\SelfSignTest.ps1 -ExePath D:\program\LH\LunchHelper\bin\Release\LunchHelper.exe"
    exit 1
}
if (-not (Test-Path $ExePath)) {
    Write-Error "Cannot find LunchHelper.exe at: $ExePath"
    exit 1
}
Write-Host "Target exe: $ExePath"

# 3) Remove any stale test certs (public in Root + private in My), then create a fresh one.
#    This avoids the "private key missing" problem where a previous run left only the
#    public copy in LocalMachine\Root. Recreating each run keeps the Root store clean.
$subject = "CN=$CertName"
Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Subject -eq $subject } | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem Cert:\CurrentUser\My   | Where-Object { $_.Subject -eq $subject } | Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "Creating new code-signing self-signed cert..."
$signCert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -KeyUsage DigitalSignature -NotAfter (Get-Date).AddYears(5)
Write-Host "Created test cert: $($signCert.Thumbprint)"
# Export public cert and import into LocalMachine Trusted Root CA
$cerPath = Join-Path $env:TEMP "$CertName.cer"
$signCert | Export-Certificate -FilePath $cerPath -Type CERT | Out-Null
Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Write-Host "Cert imported into LocalMachine Trusted Root CA (this machine only)"

# 4) Sign exe: prefer signtool, fall back to PowerShell built-in
function Find-SignTool {
    $paths = @(
        "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe",
        "C:\Program Files (x86)\Windows Kits\10\bin\*\x86\signtool.exe"
    )
    foreach ($p in $paths) {
        $f = Resolve-Path $p -ErrorAction SilentlyContinue
        if ($f) { return $f[0].Path }
    }
    return $null
}

$signtool = Find-SignTool
if ($signtool) {
    Write-Host "Using signtool: $signtool"
    & $signtool sign /n $CertName /fd SHA256 $ExePath
    if ($LASTEXITCODE -ne 0) { Write-Error "Signing failed"; exit 1 }
} else {
    Write-Host "signtool not found; using PowerShell Set-AuthenticodeSignature (no Windows SDK needed)"
    $result = Set-AuthenticodeSignature -FilePath $ExePath -Certificate $signCert -HashAlgorithm SHA256
    if ($result.Status -ne "Valid") {
        Write-Error "Signing failed: $($result.StatusMessage)"
        exit 1
    }
}
Write-Host "Signed: $ExePath"

# 5) Deploy to Program Files (secure location, satisfies uiAccess 2nd condition)
$deploy = Join-Path $env:ProgramFiles "LunchHelper"
if (-not (Test-Path $deploy)) { New-Item -ItemType Directory -Path $deploy | Out-Null }
Copy-Item $ExePath $deploy -Force
$srcDir = Split-Path $ExePath
if (Test-Path (Join-Path $srcDir "config.json")) { Copy-Item (Join-Path $srcDir "config.json") $deploy -Force }
Write-Host "Deployed to: $deploy\LunchHelper.exe"

Write-Host ""
Write-Host "Done. Now run: $deploy\LunchHelper.exe -lock"
Write-Host "If the lock screen covers Start menu / Action Center, uiAccess is working."
Write-Host ""
Write-Host "Cleanup (remove test cert, run as Admin):"
Write-Host "  Get-ChildItem Cert:\LocalMachine\Root | Where-Object { `$_.Subject -eq 'CN=$CertName' } | Remove-Item -Force"
Write-Host "  Get-ChildItem Cert:\CurrentUser\My   | Where-Object { `$_.Subject -eq 'CN=$CertName' } | Remove-Item -Force"
