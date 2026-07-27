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
if ($service -and $service.Status -ne 'Stopped') {
    Write-Host "Stopping $serviceName..."
    Stop-Service -Name $serviceName -Force
    (Get-Service -Name $serviceName).WaitForStatus('Stopped', '00:01:00')
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

if ($service) {
    Write-Host "Starting $serviceName..."
    Start-Service -Name $serviceName
}

Write-Host "`nDone."
Write-Host "  Restored from : $($chosen.Name)"
if ($replaced) {
    Write-Host "  Previous database kept at: $replaced"
    Write-Host '  Delete it once you are satisfied the restore is good.'
}
