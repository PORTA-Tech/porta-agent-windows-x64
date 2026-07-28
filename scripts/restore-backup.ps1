<#
.SYNOPSIS
    Restores core-state.db from a nightly local backup.

.DESCRIPTION
    Stops the Ari service, moves the current database aside (never deletes it), copies the chosen
    backup into place, and starts the service again.

    The database being replaced is kept as core-state.db.replaced-<timestamp>, together with its
    -wal and -shm sidecars. If the restore turns out to be the wrong choice, the original is still
    there -- and it is complete, including whatever the WAL had not yet checkpointed.

    If anything fails part-way, the swap is undone before the service comes back, so the agent
    never restarts on a half-written database.

.PARAMETER BackupFile
    Full path to the backup to restore. Defaults to the newest one in the backups folder.

.PARAMETER List
    Show available backups and exit without changing anything.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\restore-backup.ps1 -List

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\restore-backup.ps1

.NOTES
    Must be run as Administrator -- the service runs as LocalSystem and the backups folder is
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

# The sidecars SQLite keeps next to a database. Named once so the move-aside and the roll-back
# cannot drift apart.
$sidecarSuffixes = @('-wal', '-shm')

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

function Get-BigEndianUInt32 {
    param([byte[]] $Bytes, [int] $Offset)
    # Plain arithmetic rather than -shl: PowerShell's shift operators work on signed types, and
    # a page count with the high bit set would come back negative.
    return ([int64] $Bytes[$Offset]     * 16777216L) +
           ([int64] $Bytes[$Offset + 1] * 65536L)    +
           ([int64] $Bytes[$Offset + 2] * 256L)      +
            [int64] $Bytes[$Offset + 3]
}

<#
    Checks that a file really is a complete SQLite database, without needing a SQLite driver.

    The first 100 bytes of the file are the header, and four of its fields are enough:
        offset 0   16 bytes   the magic string "SQLite format 3\0"
        offset 16   2 bytes   page size, big-endian (the value 1 means 65536)
        offset 24   4 bytes   file change counter
        offset 28   4 bytes   database size in pages
        offset 92   4 bytes   version-valid-for number

    When the change counter and version-valid-for agree, the page count is authoritative, so
    pageSize * pageCount must equal the file length. That is precisely the check that catches a
    truncated or half-copied file -- which a "larger than 1 KB" test does not -- and truncation is
    the failure this script exists to recover from.

    Returns $null when the file is good, or a sentence describing the problem.
#>
function Test-SqliteFile {
    param([Parameter(Mandatory)][string] $Path)

    # FileShare.ReadWrite, not Read: this only ever reads 100 bytes, and refusing to look at a file
    # merely because something else has it open would make the function useless against a live
    # database -- and would report "locked" where the caller expects "valid" or "not valid".
    $stream = $null
    try {
        $stream = [System.IO.File]::Open($Path, 'Open', 'Read', 'ReadWrite')

        $header = New-Object byte[] 100
        $read = 0
        while ($read -lt 100) {
            $chunk = $stream.Read($header, $read, 100 - $read)
            if ($chunk -le 0) { break }
            $read += $chunk
        }
        if ($read -lt 100) {
            return "it is only $($stream.Length) bytes -- too small to be a SQLite database"
        }

        if ([System.Text.Encoding]::ASCII.GetString($header, 0, 15) -cne 'SQLite format 3') {
            return 'it does not start with a SQLite header'
        }

        $pageSize = ([int] $header[16] * 256) + [int] $header[17]
        if ($pageSize -eq 1) { $pageSize = 65536 }
        if ($pageSize -lt 512) {
            return "its header declares an impossible page size ($pageSize)"
        }

        $changeCounter   = Get-BigEndianUInt32 $header 24
        $pageCount       = Get-BigEndianUInt32 $header 28
        $versionValidFor = Get-BigEndianUInt32 $header 92

        # A mismatch here means the page-count field is stale, not that the file is bad -- an older
        # writer left it behind. Nothing to assert, so do not reject a database over it.
        if ($changeCounter -ne $versionValidFor -or $pageCount -eq 0) {
            return $null
        }

        $expected = [int64] $pageSize * $pageCount
        if ($stream.Length -ne $expected) {
            return ("it is $($stream.Length) bytes but its header describes $expected " +
                    '-- the file is truncated or was still being written')
        }

        return $null
    }
    finally {
        # Guarded: if the Open above threw, $stream is still null, and an exception raised in here
        # would replace the real reason with a null-reference message.
        if ($stream) { $stream.Dispose() }
    }
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

# Check the backup BEFORE touching anything. Restoring a truncated file over a working database is
# the one outcome this script must never produce.
Write-Host 'Checking the backup...'
$problem = Test-SqliteFile -Path $chosen.FullName
if ($problem) {
    throw "Refusing to restore $($chosen.Name): $problem"
}
Write-Host "  looks complete ($('{0:N2}' -f ($chosen.Length / 1MB)) MB)"

# The copy needs room for a second full-size database: moving the current one aside is a rename on
# the same volume, so it frees nothing. Disk-full is a plausible reason to be restoring in the
# first place, which makes this worth checking before the swap rather than discovering it midway.
$targetRoot  = (Split-Path -Path $dbPath -Qualifier) + '\'
$targetDrive = New-Object System.IO.DriveInfo($targetRoot)
if ($targetDrive.AvailableFreeSpace -lt $chosen.Length) {
    throw ("Not enough free space on $targetRoot to restore: the backup is " +
           "$('{0:N2}' -f ($chosen.Length / 1MB)) MB and only " +
           "$('{0:N2}' -f ($targetDrive.AvailableFreeSpace / 1MB)) MB is free. Nothing was changed.")
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
#      open, so Move-Item fails with "file in use" -- after the database has already been moved.
$stamp           = Get-Date -Format 'yyyyMMdd-HHmmss'
$replacedBase    = "$dbPath.replaced-$stamp"
$copyAttempted   = $false
$restoreComplete = $false

# Everything from here to the finally block runs with the service down and recovery disabled.
# ANY failure in between -- a locked file, a full disk, a typo -- must still leave the machine with
# a running agent, on a database that is whole. This script only ever runs when something has
# already gone wrong; it must not be able to turn one problem into two.
try {

Write-Host 'Disabling service auto-restart...'
try { & sc.exe failure $serviceName reset= 0 actions= none 2>$null | Out-Null } catch { }

if ($service) {
    Write-Host "Stopping $serviceName..."
    try { & sc.exe stop $serviceName 2>$null | Out-Null } catch { }
    Start-Sleep -Seconds 5
}

# Stop-Process rather than taskkill: this script runs with $ErrorActionPreference = 'Stop', and
# taskkill writes "process not found" to stderr, which PowerShell escalates into a TERMINATING
# error. install.ps1 gets away with taskkill only because it does not set that preference.
# Aborting here would leave the service stopped with recovery disabled -- the worst possible
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

if (Test-Path $dbPath) {
    Write-Host "Moving the current database aside -> $(Split-Path $replacedBase -Leaf)"
    Move-Item -Path $dbPath -Destination $replacedBase
}

# The -wal and -shm files belong to the database that was just moved aside, and the WAL can hold
# committed transactions that were never checkpointed into the main file. This script used to
# DELETE them, which quietly threw away the newest data in the very copy it was keeping as the
# undo -- the file was preserved, the last hours of it were not.
#
# They move with their database instead. The names are chosen so SQLite finds them by itself if
# the replaced database is ever opened: <db>.replaced-<stamp>-wal is the sidecar of
# <db>.replaced-<stamp>. Moving also clears them out of the way of the restored file, which is
# what the deletion was really for.
foreach ($suffix in $sidecarSuffixes) {
    $sidecar = "$dbPath$suffix"
    if (Test-Path $sidecar) {
        Move-Item -Path $sidecar -Destination "$replacedBase$suffix"
        Write-Host "  kept $(Split-Path $sidecar -Leaf) with it as $(Split-Path "$replacedBase$suffix" -Leaf)"
    }
}

Write-Host 'Copying the backup into place...'
$copyAttempted = $true
Copy-Item -Path $chosen.FullName -Destination $dbPath

# Verify what actually landed, not what we asked for. A copy interrupted by a full disk or a
# failing drive leaves a file of the right name and the wrong length, and the finally block below
# is about to start the agent on it.
Write-Host 'Verifying the restored database...'
$problem = Test-SqliteFile -Path $dbPath
if ($problem) {
    throw "The restored file is not usable: $problem"
}
Write-Host '  verified.'

$restoreComplete = $true

}
catch {
    # Undo the swap so the finally block starts the agent on the database it had, rather than on
    # whatever fragment is sitting at $dbPath. Guarded on the aside copy actually existing: if the
    # move itself failed, the original is still in place and must not be touched.
    if (-not $restoreComplete) {
        Write-Warning "Restore failed: $($_.Exception.Message)"
        try {
            if ($copyAttempted -and (Test-Path $dbPath)) {
                Remove-Item -Path $dbPath -Force
            }
            if (Test-Path $replacedBase) {
                Write-Warning 'Putting the original database back...'
                Move-Item -Path $replacedBase -Destination $dbPath
                foreach ($suffix in $sidecarSuffixes) {
                    if (Test-Path "$replacedBase$suffix") {
                        Move-Item -Path "$replacedBase$suffix" -Destination "$dbPath$suffix"
                    }
                }
                Write-Warning 'The original database is back in place, with its sidecars.'
            }
        }
        catch {
            Write-Warning "Could not put the original back automatically: $($_.Exception.Message)"
            Write-Warning "It is at: $replacedBase (with $($sidecarSuffixes -join ', ') alongside)"
        }
    }
    throw
}
finally {
    # Runs on success AND on any failure above. Restoring the SCM policy and starting the service
    # is not optional -- a half-finished restore that also leaves the agent dead is far worse than
    # a failed restore that leaves it running.
    #
    # Every step in here is individually guarded. This block is the reason the machine still has a
    # running agent after a failed restore, so nothing in it may throw and skip the lines below it.
    Write-Host 'Re-enabling service auto-restart...'
    try {
        & sc.exe failure $serviceName reset= 60 actions= restart/15000/restart/15000/restart/15000 2>$null | Out-Null
    }
    catch {
        Write-Warning "Could not restore the service recovery policy: $($_.Exception.Message)"
        Write-Warning "Set it by hand: sc.exe failure $serviceName reset= 60 actions= restart/15000/restart/15000/restart/15000"
    }

    if ($service) {
        Write-Host "Starting $serviceName..."
        try {
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 3
            $status = (Get-Service -Name $serviceName -ErrorAction SilentlyContinue).Status
            Write-Host "Service: $status"
            if ($status -ne 'Running') {
                Write-Warning "$serviceName did not start. Start it manually: Start-Service $serviceName"
            }
        }
        catch {
            Write-Warning "$serviceName did not start ($($_.Exception.Message))."
            Write-Warning "Start it manually: Start-Service $serviceName"
        }
    }
}

Write-Host "`nDone."
Write-Host "  Restored from : $($chosen.Name)"
if (Test-Path $replacedBase) {
    Write-Host "  Previous database kept at: $replacedBase"
    foreach ($suffix in $sidecarSuffixes) {
        if (Test-Path "$replacedBase$suffix") {
            Write-Host "    with $(Split-Path "$replacedBase$suffix" -Leaf)"
        }
    }
    Write-Host '  Delete them once you are satisfied the restore is good.'
}
