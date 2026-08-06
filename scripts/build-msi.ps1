# DISABLED 2026-08-06. Do not restore this script's build logic.
#
# This script published with /p:PublishSingleFile=false. Since 1.2.126 the installer no
# longer harvests payload\SharedFiles, because the shipped exes are self-contained
# single-file bundles with native libraries embedded (~101 MB ari.exe, not a ~151 KB
# apphost). Publishing non-single-file emits hundreds of loose DLLs that the MSI does not
# carry, so the installed service starts, cannot find ari.dll, times out twice against the
# SCM, and the transaction rolls back.
#
# That is not hypothetical. On 2026-08-06 this script produced an MSI that failed to
# install with Error 1920, took the AriAgent service down, and cost ~13 minutes of capture
# data on the founder's machine. The numbered release script has carried a guard predicting
# this exact failure since 1.2.126 ("the installed agent would fail to start with a
# missing-assembly error that points nowhere near this file"); this script had no guard.
#
# The flags are deliberately NOT ported here. Two copies of the publish settings is how the
# drift happened in the first place -- the numbered script is the single source of truth.
#
# To build a release: copy the highest-numbered build-<n>.ps1 at the repo root to the next
# number and run that. Bump the version in the three .csproj files first
# (Porta.Agent.Service, Porta.UserAgent, Porta.ControlCenter).

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$newest = Get-ChildItem -LiteralPath $repoRoot -Filter "build-*.ps1" -File -ErrorAction SilentlyContinue |
    Where-Object { $_.BaseName -match '^build-\d+$' } |
    Sort-Object { [int]($_.BaseName -replace '^build-', '') } |
    Select-Object -Last 1

if ($newest) {
    $pointer = "The current release script is '$($newest.Name)' at the repo root. Copy it to the next number and run that."
} else {
    $pointer = "No build-<n>.ps1 was found at the repo root -- locate the current release script before building."
}

throw @"
scripts/build-msi.ps1 is DISABLED and will not build a shippable MSI.

It publishes with PublishSingleFile=false, which produces an installer that fails to start
the service and rolls back. It did exactly that on 2026-08-06.

$pointer

See the header of this file, and Loop_And_Report_Build_Brief.md section 9.
"@
