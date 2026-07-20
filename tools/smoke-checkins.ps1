<#
.SYNOPSIS
  Smoke helper for the check-in rework. See tools/SMOKE_CheckIns.md for the full runbook.

.DESCRIPTION
  -Action preview : build Debug + launch the real cards via the DEBUG-only --preview-checkins entry
                    point (fast, deterministic eyeball of the fixed label + away-aware EOD wrap).
  -Action reset   : clear the per-day check-in mutes (so a kind can re-fire) and restart the
                    UserAgent (the service watchdog relaunches it). For live-scheduler confirmation.
  -Action help    : print guidance.

  Robust by design: touches only a JSON mute file and the UserAgent process — no sqlite3, no
  authenticated local-API calls, no clock changes (the runbook tells you when to set the clock).
#>
[CmdletBinding()]
param(
    [ValidateSet('preview', 'reset', 'help')]
    [string]$Action = 'help'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$muteFile = Join-Path $env:LOCALAPPDATA 'PortaAgent\user-agent.notification-mute.v1.json'

function Invoke-Preview {
    $proj = Join-Path $repoRoot 'src\Porta.UserAgent\Porta.UserAgent.csproj'
    $exe = Join-Path $repoRoot 'src\Porta.UserAgent\bin\Debug\net8.0-windows\Porta.UserAgent.exe'

    Write-Host 'Building Porta.UserAgent (Debug) ...' -ForegroundColor Cyan
    dotnet build $proj -c Debug -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Debug build failed.' }
    if (-not (Test-Path $exe)) { throw "Preview exe not found at $exe" }

    Write-Host 'Launching preview — four cards; answer or dismiss (X / Esc) each to advance.' -ForegroundColor Green
    Write-Host '  1 Morning   2 Midday   3 EOD (normal)   4 EOD (missed-midday + away wrap)' -ForegroundColor Green
    Write-Host '  Check A: fixed question label sits ABOVE the controls (cards 2 & 3 & 4).'
    Write-Host '  Check B: card 4 reads as the away-aware wrap, not "Today''s goal was ...".'
    & $exe --preview-checkins
}

function Invoke-Reset {
    # Re-arm the check-ins by clearing the per-day mute store (backed up first). It fails toward
    # not-muted and rebuilds itself, so removing it is safe.
    if (Test-Path $muteFile) {
        $backup = "$muteFile.smoke-bak"
        Copy-Item $muteFile $backup -Force
        Remove-Item $muteFile -Force
        Write-Host "Cleared check-in mutes (backup: $backup)." -ForegroundColor Green
    }
    else {
        Write-Host "No mute file present ($muteFile) — nothing to clear (already re-armed)." -ForegroundColor Yellow
    }

    # Restart the UserAgent; the service watchdog relaunches it within a few seconds.
    $procs = Get-Process -Name 'Porta.UserAgent' -ErrorAction SilentlyContinue
    if ($procs) {
        $procs | Stop-Process -Force
        Write-Host 'Stopped Porta.UserAgent — the watchdog will relaunch it shortly.' -ForegroundColor Green
    }
    else {
        Write-Host 'Porta.UserAgent was not running; the watchdog should start it.' -ForegroundColor Yellow
    }

    Write-Host ''
    Write-Host 'Now use the machine continuously for ~4 minutes (clears the 90s cold-start + 3min' -ForegroundColor Cyan
    Write-Host 'settle-in) so the due check-in fires. See tools\SMOKE_CheckIns.md for per-kind setup.' -ForegroundColor Cyan
}

switch ($Action) {
    'preview' { Invoke-Preview }
    'reset' { Invoke-Reset }
    default {
        Write-Host 'Check-in smoke helper. Actions:' -ForegroundColor Cyan
        Write-Host '  -Action preview   Build Debug + render all four real cards on demand (recommended).'
        Write-Host '  -Action reset     Clear check-in mutes + restart the UserAgent (live confirmation).'
        Write-Host ''
        Write-Host 'Full runbook + acceptance checks: tools\SMOKE_CheckIns.md'
    }
}
