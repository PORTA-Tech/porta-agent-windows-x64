<#
.SYNOPSIS
    Restores core-state.db from a nightly local backup.

.DESCRIPTION
    Stops the Ari service, moves the current database aside (never deletes it), copies the chosen
    backup into place, clears the stale WAL sidecar files, and starts the service again.

    The database being replaced is kept as core-state.db.replaced-<timestamp>. If the restore turns
    out to be the wrong choice, the original is still there.

.PARAMETER BackupFile
    Full path to the backup to restore. Defaults to the newest one in the backups folder.

.PARAMETER List
    Show available backups and exit without changing anything.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\restore-backup.ps1 -List

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\restore-backup.ps1

.NOTES
    Must be run as Administrator — the service runs as LocalSystem and the backups folder is
    read-only for ordinary users.
#>
[CmdletBinding()]
param(
    [string] $BackupFile,
    [switch] $List
)

$ErrorActionPreference = 'Stop'

$dataRoot   = Join-Path $env:ProgramData 'PortaAgent'
$dbPath     = Join-Path $dataRoot 'core-state.db'
$backupDir  = Join-Path $dataRoot 'backups'
$serviceName = 'AriAgent'

function Assert-Administrator {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must be run as Administrator.'
    }
}

function Get-Backups {
    if (-not (Test-Path $backupDir)) { return @() }
    # .tmp files are unfinished snapshots and must never be offered for restore.
    Get-ChildItem -Path $backupDir -Filter 'core-state-*.db' |
        Where-Object { $_.Extension -eq '.db' } |
        Sort-Object LastWriteTime -Descending
}

Assert-Administrator

$backups = Get-Backups

if ($List) {
    if ($backups.Count -eq 0) {
        Write-Host "No backups found in $backupDir"
        return
    }
    Write-Host "Available backups in ${backupDir}:`n"
    $backups | ForEach-Object {
        '{0}  {1,10:N2} MB  {2}' -f $_.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'), ($_.Length / 1MB), $_.Name
    }
    return
}

if ($BackupFile) {
    if (-not (Test-Path $BackupFile)) { throw "Backup not found: $BackupFile" }
    $chosen = Get-Item $BackupFile
} else {
    if ($backups.Count -eq 0) { throw "No backups found in $backupDir" }
    $chosen = $backups[0]
    Write-Host "No -BackupFile given; using the newest: $($chosen.Name)"
}

# Sanity-check before touching anything: a truncated or empty file is not a restore candidate.
if ($chosen.Length -lt 1KB) {
    throw "Refusing to restore $($chosen.Name): the file is only $($chosen.Length) bytes and looks incomplete."
}

Write-Host "`nAbout to restore:"
Write-Host "  from : $($chosen.FullName)"
Write-Host "  taken: $($chosen.LastWriteTime)"
Write-Host "  to   : $dbPath"
$answer = Read-Host "`nType YES to continue"
if ($answer -cne 'YES') {
    Write-Host 'Cancelled. Nothing was changed.'
    return
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

# Stopping the service is NOT enough on its own, for two reasons install.ps1 already handles:
#   1. SCM recovery would auto-restart ari.exe part-way through the swap.
#   2. ari.exe (and the UserAgent/ControlCenter, which load the same DLLs) keep the database file
#      open, so Move-Item fails with "file in use" — after the database has already been moved.
$replaced = $null

# Everything from here to the finally block runs with the service down and recovery disabled.
# ANY failure in between — a locked file, a full disk, a typo — must still leave the machine with
# a running agent. This script only ever runs when something has already gone wrong; it must not
# be able to turn one problem into two.
try {

Write-Host 'Disabling service auto-restart...'
& sc.exe failure $serviceName reset= 0 actions= none 2>$null | Out-Null

if ($service) {
    Write-Host "Stopping $serviceName..."
    & sc.exe stop $serviceName 2>$null | Out-Null
    Start-Sleep -Seconds 5
}

# Stop-Process rather than taskkill: this script runs with $ErrorActionPreference = 'Stop', and
# taskkill writes "process not found" to stderr, which PowerShell escalates into a TERMINATING
# error. install.ps1 gets away with taskkill only because it does not set that preference.
# Aborting here would leave the service stopped with recovery disabled — the worst possible
# moment, since this script only runs when something has already gone wrong.
Write-Host 'Closing any remaining Ari processes...'
foreach ($processName in @('ari', 'Porta.UserAgent', 'Porta.ControlCenter')) {
    Get-Process -Name $processName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 3

# Verify the file really is free before touching anything. Better to abort here, with the database
# untouched, than to fail half-way with it moved aside.
if (Test-Path $dbPath) {
    try {
        $handle = [System.IO.File]::Open($dbPath, 'Open', 'ReadWrite', 'None')
        $handle.Close()
    }
    catch {
        # The finally block restarts the service; just report why we stopped.
        throw 'The database is still locked by another process. Nothing was changed.'
    }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

if (Test-Path $dbPath) {
    $replaced = "$dbPath.replaced-$stamp"
    Write-Host "Moving the current database aside -> $(Split-Path $replaced -Leaf)"
    Move-Item -Path $dbPath -Destination $replaced
}

Write-Host 'Copying the backup into place...'
Copy-Item -Path $chosen.FullName -Destination $dbPath

# The -wal and -shm files belong to the database that was just moved away. Left behind, SQLite
# would try to apply them to the restored file.
foreach ($sidecar in @("$dbPath-wal", "$dbPath-shm")) {
    if (Test-Path $sidecar) {
        Remove-Item -Path $sidecar -Force
        Write-Host "Removed stale $(Split-Path $sidecar -Leaf)"
    }
}

}
finally {
    # Runs on success AND on any failure above. Restoring the SCM policy and starting the service
    # is not optional — a half-finished restore that also leaves the agent dead is far worse than
    # a failed restore that leaves it running.
    Write-Host 'Re-enabling service auto-restart...'
    & sc.exe failure $serviceName reset= 60 actions= restart/15000/restart/15000/restart/15000 2>$null | Out-Null

    if ($service) {
        Write-Host "Starting $serviceName..."
        Start-Service -Name $serviceName -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 3
        $status = (Get-Service -Name $serviceName -ErrorAction SilentlyContinue).Status
        Write-Host "Service: $status"
        if ($status -ne 'Running') {
            Write-Warning "$serviceName did not start. Start it manually: Start-Service $serviceName"
        }
    }
}

Write-Host "`nDone."
Write-Host "  Restored from : $($chosen.Name)"
if ($replaced) {
    Write-Host "  Previous database kept at: $replaced"
    Write-Host '  Delete it once you are satisfied the restore is good.'
}
