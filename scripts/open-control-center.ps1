param(
    [switch]$Restart
)

$exe = "C:\Users\hp\porta-agent-windows-x64\src\Porta.ControlCenter\bin\Release\net8.0-windows\Porta.ControlCenter.exe"

if (-not (Test-Path $exe)) {
    Write-Error "Control Center executable not found: $exe"
    exit 1
}

if ($Restart) {
    Stop-Process -Name "Porta.ControlCenter" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
}

$existing = Get-Process -Name "Porta.ControlCenter" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($existing) {
    Write-Output "Control Center already running (PID: $($existing.Id))."
    exit 0
}

Start-Process $exe
Write-Output "Control Center launched: $exe"
