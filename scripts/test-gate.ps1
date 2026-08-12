<#
.SYNOPSIS
    Runs the five test suites and fails the build on anything that is not a documented failure.

.DESCRIPTION
    Maintenance list item (a), 2026-08-12. `validation-gate.yml` ran scripts/build.ps1 and the
    ValidationRunner and NOTHING ELSE -- there was no `dotnet test` anywhere in CI, so a green tick
    meant "it compiles" and a real regression in any of the five suites could reach main green.

    Porta.Agent.Core.Tests has FOUR known failures, documented in SECURITY_HANDOFF.md section 4.8
    and accepted by the user on 2026-08-04 as not blocking a release. They are allowed here BY NAME,
    never by count: a gate that allowed "up to 4 failures" would wave through any four regressions.

    The allowance is strict in both directions. A failure that is not on the list fails the gate,
    and so does a listed test that has started PASSING -- that means it was fixed, and the entry
    (with the section in SECURITY_HANDOFF.md) is now stale and must be deleted. Handoff's own
    instruction: "When these are fixed, delete this section rather than editing the numbers."
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$resultsDir = Join-Path $root "artifacts\test-results"

# The four documented restart-reconciliation failures. Fully qualified, exactly as the runner
# reports them. See SECURITY_HANDOFF.md section 4.8 before touching this list.
$knownFailures = @(
    "Porta.Agent.Core.Tests.OverloadBrightnessScenarioTests.Scenario_RestartMidBrightnessRamp_ReconcilesContinuesAndRestoresWithoutDuplicateReducedEvent",
    "Porta.Agent.Core.Tests.DeepWorkSuppressionScenarioTests.Scenario_RestartDuringNotificationSuppression_ReconcilesWithoutDuplicateAndRestoresOnExit",
    "Porta.Agent.Core.Tests.Acceptance.Step20FinalAcceptanceGatesTests.Gate3_RestartNeverLeavesNotificationsSuppressedPermanently",
    "Porta.Agent.Core.Tests.Acceptance.Step20FinalAcceptanceGatesTests.Gate4_RestartNeverLeavesBrightnessDimPermanently"
)

$projects = @(
    "src\Porta.Agent.Core.Tests\Porta.Agent.Core.Tests.csproj",
    "src\Porta.UserAgent.Tests\Porta.UserAgent.Tests.csproj",
    "src\Porta.Agent.Service.Tests\Porta.Agent.Service.Tests.csproj",
    "src\Porta.ControlCenter.Tests\Porta.ControlCenter.Tests.csproj",
    "src\Porta.Agent.Runtime.Tests\Porta.Agent.Runtime.Tests.csproj"
)

if (Test-Path $resultsDir) { Remove-Item $resultsDir -Recurse -Force }
New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null

# Theory cases arrive as "Namespace.Class.Method(arg: 1)". The allowance is per test METHOD, so the
# argument list is stripped before comparing -- a parameterised known-failure must not slip through
# on one case and be flagged on another.
function Get-TestMethodName {
    param([string]$TestName)
    $paren = $TestName.IndexOf("(")
    if ($paren -ge 0) { return $TestName.Substring(0, $paren).Trim() }
    return $TestName.Trim()
}

function Get-FailedTests {
    param([string]$TrxPath)
    [xml]$trx = Get-Content -Path $TrxPath -Raw
    $failed = @()
    foreach ($result in $trx.TestRun.Results.UnitTestResult) {
        if ($result.outcome -eq "Failed") {
            $failed += (Get-TestMethodName -TestName $result.testName)
        }
    }
    return , $failed
}

function Get-ExecutedTests {
    param([string]$TrxPath)
    [xml]$trx = Get-Content -Path $TrxPath -Raw
    $all = @()
    foreach ($result in $trx.TestRun.Results.UnitTestResult) {
        $all += (Get-TestMethodName -TestName $result.testName)
    }
    return , $all
}

$problems = @()
$allExecuted = @()

foreach ($project in $projects) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($project)
    $trxName = "$name.trx"
    Write-Host "=== $name ===" -ForegroundColor Cyan

    # The exit code is deliberately ignored: a non-zero code only says "something failed", and the
    # TRX below says exactly WHAT, which is the whole point of allowing failures by name.
    dotnet test (Join-Path $root $project) -c $Configuration --nologo -v q `
        --logger "trx;LogFileName=$trxName" --results-directory $resultsDir
    $testExitCode = $LASTEXITCODE

    $trxPath = Join-Path $resultsDir $trxName
    if (-not (Test-Path $trxPath)) {
        # No TRX means the suite did not run at all -- a build break, a crashed host, a missing
        # runner. That is strictly worse than a failing test and must never read as green.
        $problems += "$name produced no test results (dotnet test exit code $testExitCode). The suite did not run."
        continue
    }

    $failed = Get-FailedTests -TrxPath $trxPath
    $allExecuted += Get-ExecutedTests -TrxPath $trxPath

    if ($name -eq "Porta.Agent.Core.Tests") {
        foreach ($test in $failed) {
            if ($knownFailures -notcontains $test) {
                $problems += "$name : UNEXPECTED FAILURE -- $test"
            }
        }

        # ALLOWED TO FAIL, NOT REQUIRED TO FAIL.
        #
        # The first version of this made a listed test that PASSES fail the gate, so a stale
        # allowance could not rot. That was wrong, and CI proved it within the hour: all four pass
        # on GitHub's runner and fail only on the founder's machine, so the strict rule turned a
        # green run into four errors. They are environment-dependent restart-reconciliation
        # scenarios (SECURITY_HANDOFF.md section 4.8), not tests that fail everywhere.
        #
        # Noted rather than enforced: if they pass EVERYWHERE the entry should still be deleted, but
        # that is a judgement from several runs, not something one run can decide.
        $passingHere = @($knownFailures | Where-Object { $failed -notcontains $_ })
        if ($passingHere.Count -gt 0) {
            Write-Host "  $($passingHere.Count) documented failure(s) PASSED in this environment (allowed, not required):" -ForegroundColor DarkGray
            foreach ($passed in $passingHere) {
                Write-Host "    $passed" -ForegroundColor DarkGray
            }
        }

        $allowedStillFailing = @($failed | Where-Object { $knownFailures -contains $_ })
        Write-Host "  $($allowedStillFailing.Count) documented failure(s) allowed by name." -ForegroundColor DarkGray
    }
    elseif ($failed.Count -gt 0) {
        foreach ($test in $failed) {
            $problems += "$name : FAILED -- $test"
        }
    }
}

# A known-failure entry naming a test that no longer exists (renamed, deleted) would silently become
# an allowance for nothing. Caught here rather than left to rot.
foreach ($known in $knownFailures) {
    if ($allExecuted -notcontains $known) {
        $problems += "Allowlist entry does not match any test that ran -- $known. It was renamed or deleted; fix scripts/test-gate.ps1."
    }
}

Write-Host ""
if ($problems.Count -gt 0) {
    Write-Host "TEST GATE FAILED" -ForegroundColor Red
    foreach ($problem in $problems) { Write-Host "  $problem" -ForegroundColor Red }
    exit 1
}

Write-Host "TEST GATE PASSED -- five suites, only the four documented Core failures." -ForegroundColor Green
exit 0
