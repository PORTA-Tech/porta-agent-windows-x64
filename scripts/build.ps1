[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$solution = "PORTA.Windows.sln"

if (-not $NoRestore) {
    dotnet restore $solution
}

$args = @(
    "build",
    $solution,
    "-c", $Configuration,
    "--nologo",
    "-m:1"
)

if ($NoRestore) {
    $args += "--no-restore"
}

dotnet @args
