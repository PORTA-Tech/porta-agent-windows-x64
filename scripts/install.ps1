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

# 1. Disable SCM recovery
Write-Host "[1/5] Disabling service recovery..."
& sc.exe failure AriAgent reset= 0 actions= none 2>$null
Write-Host "      Done."

# 2. Stop service
Write-Host "[2/5] Stopping service..."
& sc.exe stop AriAgent 2>$null
Start-Sleep -Seconds 5

# 3. Kill any remaining process
Write-Host "[3/5] Killing ari.exe..."
& taskkill /F /IM ari.exe /T 2>$null
Start-Sleep -Seconds 2

# 4. Directly overwrite the binaries (MSI REINSTALL is unreliable for in-use exes)
Write-Host "[4/5] Copying binaries directly..."
$sharedDir = Join-Path $payloadDir "SharedFiles"

# Root-level exes come from payload root
$rootFiles = @("ari.exe", "Porta.UserAgent.exe", "Porta.ControlCenter.exe")
foreach ($f in $rootFiles) {
    $src = Join-Path $payloadDir $f
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $installDir $f) -Force
        Write-Host "      $f -> $((Get-Item (Join-Path $installDir $f)).LastWriteTime)"
    }
}

# DLLs and support files come from SharedFiles
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

# 5. Start the service
Write-Host "[5/5] Starting service..."
& sc.exe failure AriAgent reset= 60 actions= restart/15000/restart/15000/restart/15000 2>$null
Start-Service AriAgent -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

$svc = Get-Service AriAgent -ErrorAction SilentlyContinue
$exe = Get-Item (Join-Path $installDir "ari.exe") -ErrorAction SilentlyContinue
Write-Host ""
Write-Host "ari.exe date : $($exe.LastWriteTime)"
Write-Host "Service      : $($svc.Status)"

if ($exe.LastWriteTime -gt (Get-Date "2026-05-06 14:00:00")) {
    Write-Host ""
    Write-Host "SUCCESS - 1.2.4 is installed."
} else {
    Write-Host ""
    Write-Host "WARNING: ari.exe date looks wrong. Check manually."
}
