# fix_ari_wal.ps1 - Ari WAL Truncate Fix
# Self-elevates to admin if not already running as admin

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Not admin - relaunching as admin..."
    Start-Process powershell -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs
    exit
}

$log = "C:\Users\hp\porta-agent-windows-x64\wal_fix_log.txt"
$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
"=== Ari WAL Fix Log - $timestamp ===" | Out-File $log -Encoding UTF8

Write-Host "Running as Administrator. Log: $log"

# Step 1: Check WAL size BEFORE
$walPath = "C:\ProgramData\PortaAgent\core-state.db-wal"
try {
    $item = Get-Item $walPath -ErrorAction Stop
    $walSizeMB = $item.Length / 1MB
    $walSizeBytes = $item.Length
    $msg = "BEFORE WAL size: $($walSizeMB.ToString('F2')) MB ($walSizeBytes bytes)"
    Write-Host $msg
    $msg | Out-File $log -Append -Encoding UTF8
} catch {
    $msg = "ERROR reading WAL: $_"
    Write-Host $msg
    $msg | Out-File $log -Append -Encoding UTF8
}

# Step 2: Stop AriAgent service
try {
    $svc = Get-Service AriAgent -ErrorAction Stop
    "Service status BEFORE stop: $($svc.Status)" | Out-File $log -Append -Encoding UTF8
    Stop-Service AriAgent -Force -ErrorAction Stop
    Start-Sleep -Seconds 3
    $svc = Get-Service AriAgent
    "AriAgent service stopped. Status: $($svc.Status)" | Out-File $log -Append -Encoding UTF8
    Write-Host "AriAgent stopped."
} catch {
    $msg = "ERROR stopping AriAgent service: $_"
    Write-Host $msg
    $msg | Out-File $log -Append -Encoding UTF8
}

# Step 3: Kill any Ari/Porta processes
$procs = Get-Process | Where-Object { $_.Name -match "ari|Porta" }
if ($procs) {
    foreach ($proc in $procs) {
        try {
            Stop-Process -Id $proc.Id -Force -ErrorAction Stop
            $msg = "Killed process: $($proc.Name) (PID $($proc.Id))"
            Write-Host $msg
            $msg | Out-File $log -Append -Encoding UTF8
        } catch {
            $msg = "Could not kill $($proc.Name): $_"
            Write-Host $msg
            $msg | Out-File $log -Append -Encoding UTF8
        }
    }
} else {
    "No Ari/Porta processes found to kill." | Out-File $log -Append -Encoding UTF8
    Write-Host "No Ari/Porta processes found."
}
Start-Sleep -Seconds 2

# Verify no processes remain
$remaining = Get-Process | Where-Object { $_.Name -match "ari|Porta" }
if ($remaining) {
    $msg = "WARNING: Processes still running: $($remaining.Name -join ', ')"
    Write-Host $msg
    $msg | Out-File $log -Append -Encoding UTF8
} else {
    "Verified: No Ari/Porta processes running." | Out-File $log -Append -Encoding UTF8
    Write-Host "No processes remaining."
}

# Step 4: Run WAL TRUNCATE checkpoint via Python
$pythonCommands = @("python", "py", "python3")
$checkpointDone = $false
foreach ($pyCmd in $pythonCommands) {
    try {
        $result = & $pyCmd -c "import sqlite3; conn = sqlite3.connect(r'C:\ProgramData\PortaAgent\core-state.db'); result = conn.execute('PRAGMA wal_checkpoint(TRUNCATE)'); rows = result.fetchall(); conn.commit(); conn.close(); print('TRUNCATE done. Result:', rows)" 2>&1
        $msg = "Python checkpoint ($pyCmd): $result"
        Write-Host $msg
        $msg | Out-File $log -Append -Encoding UTF8
        $checkpointDone = $true
        break
    } catch {
        $msg = "Python cmd '$pyCmd' failed: $_"
        Write-Host $msg
        $msg | Out-File $log -Append -Encoding UTF8
    }
}
if (-not $checkpointDone) {
    "ERROR: Could not run Python WAL checkpoint with any command." | Out-File $log -Append -Encoding UTF8
}

# Step 5: Check WAL size AFTER
try {
    $item = Get-Item $walPath -ErrorAction Stop
    $walSizeMBAfter = $item.Length / 1MB
    $walSizeBytesAfter = $item.Length
    $msg = "AFTER WAL size: $($walSizeMBAfter.ToString('F2')) MB ($walSizeBytesAfter bytes)"
    Write-Host $msg
    $msg | Out-File $log -Append -Encoding UTF8
} catch {
    $msg = "ERROR reading WAL after checkpoint: $_"
    Write-Host $msg
    $msg | Out-File $log -Append -Encoding UTF8
}

# Step 6: Restart service
try {
    Start-Service AriAgent -ErrorAction Stop
    Start-Sleep -Seconds 5
    $svc = Get-Service AriAgent
    $msg = "AriAgent restarted. Status: $($svc.Status)"
    Write-Host $msg
    $msg | Out-File $log -Append -Encoding UTF8
} catch {
    $msg = "ERROR starting AriAgent: $_"
    Write-Host $msg
    $msg | Out-File $log -Append -Encoding UTF8
}

# Step 7: Check API health
try {
    $endpointFile = "C:\ProgramData\PortaAgent\local-api.endpoint.json"
    $endpoint = Get-Content $endpointFile | ConvertFrom-Json
    $port = $endpoint.Port
    "API endpoint port: $port" | Out-File $log -Append -Encoding UTF8
    $response = Invoke-WebRequest "http://127.0.0.1:$port/healthz" -TimeoutSec 5 -UseBasicParsing
    $msg = "API HEALTH CHECK: StatusCode=$($response.StatusCode) Content=$($response.Content)"
    Write-Host $msg
    $msg | Out-File $log -Append -Encoding UTF8
} catch {
    $msg = "API health check FAILED: $_"
    Write-Host $msg
    $msg | Out-File $log -Append -Encoding UTF8
}

"=== Fix complete ===" | Out-File $log -Append -Encoding UTF8
Write-Host "=== Fix complete. Full log at: $log ==="
Write-Host ""
Write-Host "Press any key to close this window..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
