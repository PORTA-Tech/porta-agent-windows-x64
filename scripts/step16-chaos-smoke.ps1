param(
  [int]$Cycles = 3
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$runtimeProject = Join-Path $repo "src/Porta.Agent.Runtime/Porta.Agent.Runtime.csproj"
$outDir = Join-Path $repo "docs"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outFile = Join-Path $outDir "step16-agent-chaos-smoke.log"

$consentPath = Join-Path $env:TEMP "porta-step16-consent.json"
@"
{
  "State": "ConsentedActive",
  "UpdatedAtUtc": "$(Get-Date -AsUTC -Format o)"
}
"@ | Set-Content -Path $consentPath -Encoding UTF8

$env:PORTA_CONSENT_STATE_PATH = $consentPath
$env:PORTA_LAYER1_SIGNAL_POLICY = "whitelist_v1"
$env:PORTA_SIGNAL_SCHEMA_VERSION = "1.0.0"

"step,started_utc,stopped_utc,status" | Set-Content -Path $outFile -Encoding UTF8

for ($i = 1; $i -le $Cycles; $i++) {
  $start = (Get-Date).ToUniversalTime().ToString("o")
  $proc = Start-Process -FilePath "dotnet" -ArgumentList "run --project `"$runtimeProject`" --configuration Release" -WorkingDirectory $repo -PassThru
  Start-Sleep -Seconds 8
  try {
    Stop-Process -Id $proc.Id -Force -ErrorAction Stop
    $status = "killed"
  } catch {
    $status = "already_stopped"
  }
  Start-Sleep -Seconds 2
  $stop = (Get-Date).ToUniversalTime().ToString("o")
  "$i,$start,$stop,$status" | Add-Content -Path $outFile -Encoding UTF8
}

Write-Host "step16 chaos smoke complete: $outFile"
