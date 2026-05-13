# wal_fix_v2.ps1 - WAL Truncate Fix (improved - uses taskkill, no hanging)
# Self-elevate to admin
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell -ArgumentList "-ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs
    exit
}

$log = "C:\Users\hp\porta-agent-windows-x64\wal_fix_v2_log.txt"
"=== WAL Fix v2 - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') ===" | Tee-Object $log
"Running as Administrator." | Tee-Object $log -Append

# ── STEP 1: Check WAL size BEFORE ──
$walPath = "C:\ProgramData\PortaAgent\core-state.db-wal"
try {
    $before = (Get-Item $walPath -ErrorAction Stop).Length
    $beforeMB = [math]::Round($before / 1MB, 2)
    "BEFORE WAL size: $beforeMB MB ($before bytes)" | Tee-Object $log -Append
} catch {
    "ERROR reading WAL: $_" | Tee-Object $log -Append
}

# ── STEP 2: Get the service PID and kill it directly ──
try {
    $svc = Get-WmiObject Win32_Service -Filter "Name='AriAgent'" -ErrorAction Stop
    $pid = $svc.ProcessId
    "AriAgent service PID: $pid  State: $($svc.State)" | Tee-Object $log -Append

    if ($pid -and $pid -ne 0) {
        "Killing PID $pid with taskkill /F..." | Tee-Object $log -Append
        & taskkill /F /PID $pid 2>&1 | Tee-Object $log -Append
    } else {
        "Service PID is 0 - service may already be stopped." | Tee-Object $log -Append
    }
} catch {
    "ERROR querying service: $_" | Tee-Object $log -Append
}

# ── STEP 3: Also kill any remaining Ari/Porta processes by name ──
Start-Sleep -Seconds 2
$procs = Get-Process | Where-Object { $_.Name -match "ari|porta" }
if ($procs) {
    foreach ($p in $procs) {
        "Killing by name: $($p.Name) (PID $($p.Id))" | Tee-Object $log -Append
        & taskkill /F /PID $p.Id 2>&1 | Tee-Object $log -Append
    }
} else {
    "No Ari/Porta processes found (clean)." | Tee-Object $log -Append
}
Start-Sleep -Seconds 2

# ── STEP 4: Verify service is stopped ──
$svcStatus = (Get-Service AriAgent -ErrorAction SilentlyContinue).Status
"Service status after kill: $svcStatus" | Tee-Object $log -Append

# ── STEP 5: Verify no processes holding DB ──
$remaining = Get-Process | Where-Object { $_.Name -match "ari|porta" }
if ($remaining) {
    "WARNING: Still running: $($remaining.Name -join ', ')" | Tee-Object $log -Append
} else {
    "Verified: No Ari/Porta processes running." | Tee-Object $log -Append
}

# ── STEP 6: Run WAL TRUNCATE checkpoint via Python ──
"Running WAL TRUNCATE checkpoint..." | Tee-Object $log -Append
$pyScript = "import sqlite3; conn = sqlite3.connect(r'C:\ProgramData\PortaAgent\core-state.db'); r = conn.execute('PRAGMA wal_checkpoint(TRUNCATE)').fetchall(); conn.commit(); conn.close(); print('TRUNCATE result:', r)"

$result = $null
foreach ($cmd in @("python", "py", "python3")) {
    try {
        $result = & $cmd -c $pyScript 2>&1
        "Python ($cmd) result: $result" | Tee-Object $log -Append
        break
    } catch {
        "Python cmd '$cmd' not found or failed." | Tee-Object $log -Append
    }
}

# ── STEP 7: Check WAL size AFTER ──
Start-Sleep -Seconds 1
try {
    $after = (Get-Item $walPath -ErrorAction Stop).Length
    $afterMB = [math]::Round($after / 1MB, 2)
    "AFTER WAL size: $afterMB MB ($after bytes)" | Tee-Object $log -Append
    if ($afterMB -lt 1) {
        "SUCCESS: WAL is near zero!" | Tee-Object $log -Append
    } else {
        "WARNING: WAL still large at $afterMB MB" | Tee-Object $log -Append
    }
} catch {
    "ERROR reading WAL after: $_" | Tee-Object $log -Append
}

# ── STEP 8: Restart service ──
"Restarting AriAgent service..." | Tee-Object $log -Append
try {
    Start-Service AriAgent -ErrorAction Stop
    Start-Sleep -Seconds 6
    $status = (Get-Service AriAgent).Status
    "AriAgent restarted. Status: $status" | Tee-Object $log -Append
} catch {
    "ERROR restarting: $_" | Tee-Object $log -Append
}

# ── STEP 9: API health check ──
Start-Sleep -Seconds 3
try {
    $ep = Get-Content "C:\ProgramData\PortaAgent\local-api.endpoint.json" | ConvertFrom-Json
    $port = $ep.Port
    "API port: $port" | Tee-Object $log -Append
    $resp = Invoke-WebRequest "http://127.0.0.1:$port/healthz" -TimeoutSec 10 -UseBasicParsing
    "API HEALTH: StatusCode=$($resp.StatusCode)  Content=$($resp.Content)" | Tee-Object $log -Append
} catch {
    "API health check FAILED: $_" | Tee-Object $log -Append
}

"=== DONE. Log: $log ===" | Tee-Object $log -Append
Write-Host ""
Write-Host "Press any key to close..." -ForegroundColor Cyan
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
