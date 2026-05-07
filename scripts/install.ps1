#Requires -RunAsAdministrator
param(
    [string]$MsiPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Continue"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$payloadDir = Join-Path $repoRoot "installer\payload"
$installDir = "C:\Program Files\Ari"

if (!$MsiPath) {
    $MsiPath = Get-ChildItem (Join-Path $repoRoot "artifacts\publish\AriAgentInstaller*.msi") |
               Sort-Object LastWriteTime -Descending |
               Select-Object -First 1 -ExpandProperty FullName
}

Write-Host "Payload: $payloadDir"
Write-Host "Install: $installDir"

# 1. Disable SCM recovery so kills don't trigger auto-restart
Write-Host "[1/5] Disabling service recovery..."
& sc.exe failure AriAgent reset= 0 actions= none 2>$null
Write-Host "      Done."

# 2. Stop service
Write-Host "[2/5] Stopping service..."
& sc.exe stop AriAgent 2>$null
Start-Sleep -Seconds 5

# 3. Kill ALL Ari processes (UserAgent and ControlCenter load the same DLLs)
Write-Host "[3/5] Killing all Ari processes..."
& taskkill /F /IM ari.exe /T 2>$null
& taskkill /F /IM Porta.UserAgent.exe /T 2>$null
& taskkill /F /IM Porta.ControlCenter.exe /T 2>$null
Start-Sleep -Seconds 3
Write-Host "      Done."

# 4. Overwrite all binaries
Write-Host "[4/5] Copying binaries..."
$sharedDir = Join-Path $payloadDir "SharedFiles"

$rootFiles = @("ari.exe", "Porta.UserAgent.exe", "Porta.ControlCenter.exe")
foreach ($f in $rootFiles) {
    $src = Join-Path $payloadDir $f
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $installDir $f) -Force
        Write-Host "      $f -> $((Get-Item (Join-Path $installDir $f)).LastWriteTime)"
    }
}

$sharedFiles = @(
    "ari.dll", "ari.deps.json", "ari.runtimeconfig.json",
    "Porta.Agent.Core.dll", "Porta.Agent.LocalApi.dll",
    "Porta.Agent.Common.dll", "Porta.Agent.Runtime.dll"
)
foreach ($f in $sharedFiles) {
    $src = Join-Path $sharedDir $f
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $installDir $f) -Force
        Write-Host "      $f -> $((Get-Item (Join-Path $installDir $f)).LastWriteTime)"
    }
}

# 5. Re-enable recovery and start service
Write-Host "[5/5] Starting service..."
& sc.exe failure AriAgent reset= 60 actions= restart/15000/restart/15000/restart/15000 2>$null
Start-Service AriAgent -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

$svc = Get-Service AriAgent -ErrorAction SilentlyContinue
$core = Get-Item (Join-Path $installDir "Porta.Agent.Core.dll") -ErrorAction SilentlyContinue
$exe  = Get-Item (Join-Path $installDir "ari.exe") -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "ari.exe              : $($exe.LastWriteTime)"
Write-Host "Porta.Agent.Core.dll : $($core.LastWriteTime)"
Write-Host "Service              : $($svc.Status)"

$cutoff = Get-Date "2026-05-07 08:00:00"
if ($exe.LastWriteTime -gt $cutoff -and $core.LastWriteTime -gt $cutoff) {
    Write-Host ""
    Write-Host "SUCCESS - installed (binaries: $($exe.LastWriteTime.ToString('yyyy-MM-dd HH:mm')))."
} else {
    Write-Host ""
    Write-Host "WARNING: some files may be old. Check the dates above."
}
