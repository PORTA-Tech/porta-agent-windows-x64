param(
    [Parameter(Mandatory = $true)]
    [string]$ControlPlaneBaseUrl,

    [string]$OperatorId = "validation-runner",

    [string]$OperatorRole = "operator",

    [string]$MachineId = $env:COMPUTERNAME,

    [string]$UserAgentPath = "C:\Program Files\Ari\Porta.UserAgent.exe",

    [switch]$AllowProcessControl,

    [string]$VerifyPersistenceCheckpoint,

    [string]$IdentityUserId,

    [string]$IdentityPassword,

    [string]$IdentityOrgId,

    [switch]$AllowIdentityRelink,

    [switch]$SkipIdentityRolloutGates
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$args = @(
    "run",
    "--project", "src/Porta.Agent.ValidationRunner/Porta.Agent.ValidationRunner.csproj",
    "--",
    "fleet-gates",
    "--control-plane-base-url", $ControlPlaneBaseUrl,
    "--operator-id", $OperatorId,
    "--operator-role", $OperatorRole,
    "--machine-id", $MachineId,
    "--user-agent-path", $UserAgentPath
)

if (-not $SkipIdentityRolloutGates) {
    if ([string]::IsNullOrWhiteSpace($IdentityUserId) -or [string]::IsNullOrWhiteSpace($IdentityPassword)) {
        throw "IdentityUserId and IdentityPassword are required unless SkipIdentityRolloutGates is set."
    }

    $args += @("--identity-user-id", $IdentityUserId, "--identity-password", $IdentityPassword)

    if ($IdentityOrgId) {
        $args += @("--identity-org-id", $IdentityOrgId)
    }

    if ($AllowIdentityRelink) {
        $args += "--allow-identity-relink"
    }
}
else {
    $args += "--skip-identity-rollout-gates"
}

if ($AllowProcessControl) {
    $args += "--allow-process-control"
}

if ($VerifyPersistenceCheckpoint) {
    $args += @("--verify-persistence-checkpoint", $VerifyPersistenceCheckpoint)
}

Write-Host "Running fleet validation gates..." -ForegroundColor Cyan
dotnet @args
if ($LASTEXITCODE -ne 0) {
    throw "Fleet validation gates FAILED."
}

Write-Host "Fleet validation gates PASSED." -ForegroundColor Green
