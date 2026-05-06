#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs or upgrades Ari Agent, handling the locked-service edge case.
.DESCRIPTION
    Right-click this file -> "Run with PowerShell" (or run from an admin terminal).
    Kills the running ari.exe, disables its SCM recovery so it doesn't respawn,
    then runs the MSI with full file-overwrite flags.
#>

param(
    [string]$MsiPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
if (!$MsiPath) {
    # Pick the newest 1.2.x MSI in artifacts/publish
    $MsiPath = Get-ChildItem (Join-Path $repoRoot "artifacts\publish\AriAgentInstaller*.msi") |
               Sort-Object LastWriteTime -Descending |
               Select-Object -First 1 -ExpandProperty FullName
}

if (!$MsiPath -or !(Test-Path $MsiPath)) {
    Write-Error "MSI not found. Run scripts/build-msi.ps1 first, or pass -MsiPath explicitly."
}

Write-Host "MSI: $MsiPath"
Write-Host ""

# 1. Disable SCM recovery so force-kill does not trigger auto-restart
Write-Host "[1/4] Disabling service recovery actions..."
& sc.exe failure AriAgent reset= 0 actions= none 2>$null
Write-Host "      Done."

# 2. Stop the service (best effort — it may be hung and unresponsive)
Write-Host "[2/4] Stopping AriAgent service..."
& sc.exe stop AriAgent 2>$null
Start-Sleep -Seconds 4

# 3. Force-kill any remaining ari.exe process
Write-Host "[3/4] Terminating ari.exe process..."
& taskkill /F /IM ari.exe /T 2>$null
Start-Sleep -Seconds 2

# Verify the process is gone
$proc = Get-Process ari -ErrorAction SilentlyContinue
if ($proc) {
    Write-Warning "ari.exe (PID $($proc.Id)) is still running — install may not replace ari.exe."
} else {
    Write-Host "      Process confirmed dead."
}

# 4. Install with REINSTALLMODE=amus to force-overwrite all files
#    REINSTALL=ALL   -> reinstall all components
#    REINSTALLMODE=a -> overwrite regardless of version
#               m -> machine registry values
#               u -> user registry values
#               s -> shortcuts
Write-Host "[4/4] Running MSI installer (force overwrite)..."
$args = @("/i", "`"$MsiPath`"", "REINSTALL=ALL", "REINSTALLMODE=amus", "/qb", "/l*vx", "`"$repoRoot\artifacts\publish\install-log.txt`"")
$proc = Start-Process msiexec -ArgumentList $args -Wait -PassThru
Write-Host ""

if ($proc.ExitCode -eq 0) {
    Write-Host "Install succeeded (exit 0)."
    Write-Host ""
    $svc = Get-Service AriAgent -ErrorAction SilentlyContinue
    $exe = Get-Item "C:\Program Files\Ari\ari.exe" -ErrorAction SilentlyContinue
    if ($svc)  { Write-Host "Service status : $($svc.Status)" }
    if ($exe)  { Write-Host "ari.exe date   : $($exe.LastWriteTime)" }
} else {
    Write-Warning "MSI exited with code $($proc.ExitCode). Check artifacts\publish\install-log.txt."
}
