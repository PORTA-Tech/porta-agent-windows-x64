[CmdletBinding()]
param(
    [int]$DurationSeconds = 120,
    [string]$OutputCsv = "docs/phase6-soak-results.csv"
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

function Run-Scenario {
    param(
        [string]$Name,
        [string]$ProjectPath,
        [bool]$SuppressionEnabled,
        [bool]$ActuationEnabled,
        [int]$DurationSec
    )

    $logsDir = "docs/soak-logs"
    New-Item -ItemType Directory -Path $logsDir -Force | Out-Null

    $utcStart = [DateTimeOffset]::UtcNow
    $logFile = Join-Path $logsDir ("{0}-{1}.log" -f $Name, $utcStart.ToString("yyyyMMddTHHmmssZ"))
    $errFile = Join-Path $logsDir ("{0}-{1}.err.log" -f $Name, $utcStart.ToString("yyyyMMddTHHmmssZ"))
    $durationMs = $DurationSec * 1000

    $env:PORTA_SUPPRESSION_ENABLED = if ($SuppressionEnabled) { "1" } else { "0" }
    $env:PORTA_ACTUATION_ENABLED = if ($ActuationEnabled) { "1" } else { "0" }
    $env:PORTA_AUTO_STOP_MS = "$durationMs"

    $args = "run --project `"$ProjectPath`" --no-build"
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
    foreach ($line in $metricLines) {
        $m = Parse-MetricsLine -Line $line
        if ($m.ContainsKey("queue_depth")) { $maxQueueDepth = [Math]::Max($maxQueueDepth, [int]$m["queue_depth"]) }
        if ($m.ContainsKey("dropped")) { $maxDropped = [Math]::Max($maxDropped, [int]$m["dropped"]) }
    }

    $avgCpu = 0.0
    $maxMem = 0.0
    if ($samples.Count -gt 0) {
        $avgCpu = [Math]::Round((($samples | Measure-Object -Property CpuPct -Average).Average), 3)
        $maxMem = [Math]::Round((($samples | Measure-Object -Property WorkingSetMb -Maximum).Maximum), 2)
    }

    return [pscustomobject]@{
        scenario = $Name
        utc_start = $utcStart.ToString("O")
        utc_end = $utcEnd.ToString("O")
        duration_seconds = [Math]::Round(($utcEnd - $utcStart).TotalSeconds, 1)
        suppression_enabled = $SuppressionEnabled
        actuation_enabled = $ActuationEnabled
        avg_cpu_pct = $avgCpu
        max_working_set_mb = $maxMem
        max_queue_depth = $maxQueueDepth
        max_dropped = $maxDropped
        metrics_samples = $metricLines.Count
        log_file = $logFile
    }
}

dotnet build src/Porta.Agent.Runtime/Porta.Agent.Runtime.csproj -c Release | Out-Null
dotnet build src/Porta.Agent.Service/Porta.Agent.Service.csproj -c Release | Out-Null

$results = @()
$results += Run-Scenario -Name "runtime_off" -ProjectPath "src/Porta.Agent.Runtime/Porta.Agent.Runtime.csproj" -SuppressionEnabled:$false -ActuationEnabled:$false -DurationSec $DurationSeconds
$results += Run-Scenario -Name "runtime_on" -ProjectPath "src/Porta.Agent.Runtime/Porta.Agent.Runtime.csproj" -SuppressionEnabled:$true -ActuationEnabled:$true -DurationSec $DurationSeconds
$results += Run-Scenario -Name "service_mode" -ProjectPath "src/Porta.Agent.Service/Porta.Agent.Service.csproj" -SuppressionEnabled:$false -ActuationEnabled:$false -DurationSec $DurationSeconds

$results | Export-Csv -Path $OutputCsv -NoTypeInformation
$results | Format-Table -AutoSize
