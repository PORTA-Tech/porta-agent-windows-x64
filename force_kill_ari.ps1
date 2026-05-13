# force_kill_ari.ps1 - Force kill stuck AriAgent processes
# Self-elevate
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs
    exit
}

Write-Host "=== Force Kill Ari Processes ===" -ForegroundColor Cyan

# Use sc.exe to force stop the service
Write-Host "Attempting sc stop AriAgent..." -ForegroundColor Yellow
& sc.exe stop AriAgent
Start-Sleep -Seconds 2

# Kill all matching processes by name using taskkill (more aggressive than Stop-Process)
$patterns = @("ari", "porta", "AriAgent")
foreach ($pattern in $patterns) {
    Write-Host "taskkill /F /IM *$pattern*..." -ForegroundColor Yellow
    & taskkill /F /FI "IMAGENAME eq *$pattern*" 2>&1 | Write-Host
}

# Also try by service name
Write-Host "Killing by service..." -ForegroundColor Yellow
& sc.exe queryex AriAgent | Write-Host

Start-Sleep -Seconds 2

# Check remaining processes
$remaining = Get-Process | Where-Object { $_.Name -match "ari|porta" }
if ($remaining) {
    Write-Host "Still running:" -ForegroundColor Red
    $remaining | Select-Object Name, Id | Format-Table | Out-String | Write-Host
    # Force kill each one
    foreach ($p in $remaining) {
        Write-Host "Force killing PID $($p.Id) ($($p.Name))..." -ForegroundColor Red
        & taskkill /F /PID $p.Id
    }
} else {
    Write-Host "All Ari/Porta processes terminated." -ForegroundColor Green
}

Write-Host "Done. Press any key..." -ForegroundColor Cyan
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
