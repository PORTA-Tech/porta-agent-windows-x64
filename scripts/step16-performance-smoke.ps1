param(
  [int]$DurationSeconds = 120
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$runtimeProject = Join-Path $repo "src/Porta.Agent.Runtime/Porta.Agent.Runtime.csproj"
$outDir = Join-Path $repo "docs"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outFile = Join-Path $outDir "step16-agent-performance-smoke.csv"

$consentPath = Join-Path $env:TEMP "porta-step16-consent.json"
@"
{
  "State": "ConsentedActive",
  "UpdatedAtUtc": "$(Get-Date -AsUTC -Format o)"
}
"@ | Set-Content -Path $consentPath -Encoding UTF8

$env:PORTA_CONSENT_STATE_PATH = $consentPath
$env:PORTA_AUTO_STOP_MS = [string]($DurationSeconds * 1000)
$env:PORTA_LAYER1_SIGNAL_POLICY = "whitelist_v1"
$env:PORTA_SIGNAL_SCHEMA_VERSION = "1.0.0"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "dotnet"
$psi.Arguments = "run --project `"$runtimeProject`" --configuration Release"
$psi.WorkingDirectory = $repo
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true

$proc = New-Object System.Diagnostics.Process
$proc.StartInfo = $psi
$null = $proc.Start()

"timestamp_utc,cpu_percent,working_set_mb" | Set-Content -Path $outFile -Encoding UTF8

while (-not $proc.HasExited) {
  Start-Sleep -Milliseconds 1000
  try {
    $p = Get-Process -Id $proc.Id -ErrorAction Stop
    $cpu = [math]::Round($p.CPU, 4)
    $memMb = [math]::Round($p.WorkingSet64 / 1MB, 2)
    "$((Get-Date).ToUniversalTime().ToString("o")),$cpu,$memMb" | Add-Content -Path $outFile -Encoding UTF8
  } catch {
  }
}

Write-Host "step16 performance smoke complete: $outFile"
