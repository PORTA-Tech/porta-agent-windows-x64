$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

Write-Host "Running Step 20 acceptance gates..." -ForegroundColor Cyan

$testProject = "src/Porta.Agent.Core.Tests/Porta.Agent.Core.Tests.csproj"
$filter = "Category=AcceptanceGate"

dotnet test $testProject --filter $filter -v minimal
if ($LASTEXITCODE -ne 0) {
    throw "Step 20 acceptance gates FAILED."
}

Write-Host "Step 20 acceptance gates PASSED." -ForegroundColor Green
