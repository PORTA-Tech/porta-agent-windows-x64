[CmdletBinding()]
param(
    [int]$DurationSeconds = 1800,
    [string]$OutputCsv = "docs/phase72-combined-soak-results.csv"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Parse-MetricsLine {
    param([string]$Line)
    $map = @{}
    foreach ($m in [regex]::Matches($Line, '([a-z_]+)=(".*?"|\S+)')) {
        $k = $m.Groups[1].Value
        $v = $m.Groups[2].Value.Trim('"')
        $map[$k] = $v
    }
    return $map
}

dotnet build src/Porta.Agent.Runtime/Porta.Agent.Runtime.csproj -c Release | Out-Null

$logsDir = "docs/soak-logs"
New-Item -ItemType Directory -Path $logsDir -Force | Out-Null

$utcStart = [DateTimeOffset]::UtcNow
$logFile = Join-Path $logsDir ("phase72-combined-{0}.log" -f $utcStart.ToString("yyyyMMddTHHmmssZ"))
$errFile = Join-Path $logsDir ("phase72-combined-{0}.err.log" -f $utcStart.ToString("yyyyMMddTHHmmssZ"))
$durationMs = $DurationSeconds * 1000

$env:PORTA_SUPPRESSION_ENABLED = "0"
$env:PORTA_ACTUATION_ENABLED = "0"
$env:PORTA_KEYBOARD_CAPTURE_ENABLED = "1"
$env:PORTA_AUTO_STOP_MS = "$durationMs"

$args = "run --project `"src/Porta.Agent.Runtime/Porta.Agent.Runtime.csproj`" -c Release --no-build"
$p = Start-Process -FilePath "dotnet" -ArgumentList $args -RedirectStandardOutput $logFile -RedirectStandardError $errFile -PassThru

$samples = New-Object System.Collections.Generic.List[object]
$lastCpu = 0.0
$lastTs = [DateTimeOffset]::UtcNow

while (-not $p.HasExited) {
    Start-Sleep -Milliseconds 1000
    try {
        $proc = Get-Process -Id $p.Id -ErrorAction Stop
        $now = [DateTimeOffset]::UtcNow
        $cpuDelta = $proc.CPU - $lastCpu
        $secDelta = [Math]::Max(0.001, ($now - $lastTs).TotalSeconds)
        $cpuPct = ($cpuDelta / ($secDelta * [Environment]::ProcessorCount)) * 100.0
        $samples.Add([pscustomobject]@{
                CpuPct = [Math]::Max(0.0, $cpuPct)
                WorkingSetMb = [Math]::Round($proc.WorkingSet64 / 1MB, 2)
            })
        $lastCpu = $proc.CPU
        $lastTs = $now
    }
    catch {
        break
    }
}

$utcEnd = [DateTimeOffset]::UtcNow
$lines = @()
if (Test-Path $logFile) {
    $lines = Get-Content $logFile
}
$metricLines = $lines | Where-Object { $_ -like "*name=runtime_metrics*" }

$maxQueueDepth = 0
$maxDropped = 0
$maxKeyboardDropped = 0
$maxFocusDropped = 0
$maxFocusEnqueued = 0
$maxFocusProcessed = 0

foreach ($line in $metricLines) {
    $m = Parse-MetricsLine -Line $line
    if ($m.ContainsKey("queue_depth")) { $maxQueueDepth = [Math]::Max($maxQueueDepth, [int]$m["queue_depth"]) }
    if ($m.ContainsKey("dropped")) { $maxDropped = [Math]::Max($maxDropped, [int]$m["dropped"]) }
    if ($m.ContainsKey("keyboard_dropped")) { $maxKeyboardDropped = [Math]::Max($maxKeyboardDropped, [int]$m["keyboard_dropped"]) }
    if ($m.ContainsKey("focus_dropped")) { $maxFocusDropped = [Math]::Max($maxFocusDropped, [int]$m["focus_dropped"]) }
    if ($m.ContainsKey("focus_enqueued")) { $maxFocusEnqueued = [Math]::Max($maxFocusEnqueued, [int]$m["focus_enqueued"]) }
    if ($m.ContainsKey("focus_processed")) { $maxFocusProcessed = [Math]::Max($maxFocusProcessed, [int]$m["focus_processed"]) }
}

$avgCpu = 0.0
$maxMem = 0.0
if ($samples.Count -gt 0) {
    $avgCpu = [Math]::Round((($samples | Measure-Object -Property CpuPct -Average).Average), 3)
    $maxMem = [Math]::Round((($samples | Measure-Object -Property WorkingSetMb -Maximum).Maximum), 2)
}

$result = [pscustomobject]@{
    scenario = "phase72_combined"
    utc_start = $utcStart.ToString("O")
    utc_end = $utcEnd.ToString("O")
    duration_seconds = [Math]::Round(($utcEnd - $utcStart).TotalSeconds, 1)
    suppression_enabled = $false
    actuation_enabled = $false
    keyboard_capture_enabled = $true
    avg_cpu_pct = $avgCpu
    max_working_set_mb = $maxMem
    max_queue_depth = $maxQueueDepth
    max_dropped = $maxDropped
    max_keyboard_dropped = $maxKeyboardDropped
    max_focus_dropped = $maxFocusDropped
    max_focus_enqueued = $maxFocusEnqueued
    max_focus_processed = $maxFocusProcessed
    metrics_samples = $metricLines.Count
    log_file = $logFile
    err_file = $errFile
}

if (Test-Path $OutputCsv) {
    $result | Export-Csv -Path $OutputCsv -NoTypeInformation -Append
}
else {
    $result | Export-Csv -Path $OutputCsv -NoTypeInformation
}

$result | Format-List
