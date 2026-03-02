[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$BackendUrl = "https://porta-control-plane.onrender.com",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$serviceProject = Join-Path $repoRoot "src/Porta.Agent.Service/Porta.Agent.Service.csproj"
$publishDir = Join-Path $repoRoot "src/Porta.Agent.Service/bin/$Configuration/net8.0-windows/$Runtime/publish"
$ariExe = Join-Path $publishDir "ari.exe"
$trayExe = Join-Path $repoRoot "src/Porta.Tray/bin/$Configuration/net8.0-windows/Porta.Tray.exe"
$controlCenterExe = Join-Path $repoRoot "src/Porta.ControlCenter/bin/$Configuration/net8.0-windows/Porta.ControlCenter.exe"
$installerProject = Join-Path $repoRoot "installer/AriAgent.Installer.wixproj"
$payloadDir = Join-Path $repoRoot "installer/payload"
$payloadExe = Join-Path $payloadDir "ari.exe"
$payloadTrayExe = Join-Path $payloadDir "AriTray.exe"
$payloadControlCenterExe = Join-Path $payloadDir "Porta.ControlCenter.exe"

Write-Host "Publishing ari.exe ($Configuration, $Runtime)..."
dotnet publish $serviceProject -c $Configuration -r $Runtime --self-contained true /p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $ariExe)) {
    throw "Published binary not found at '$ariExe'."
}

if (-not (Test-Path $trayExe)) {
    throw "Tray binary not found at '$trayExe'. Build Porta.Tray first."
}

if (-not (Test-Path $controlCenterExe)) {
    throw "Control Center binary not found at '$controlCenterExe'. Build Porta.ControlCenter first."
}

if (-not (Test-Path $payloadDir)) {
    New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
}

Copy-Item $ariExe $payloadExe -Force
Copy-Item $trayExe $payloadTrayExe -Force
Copy-Item $controlCenterExe $payloadControlCenterExe -Force
Write-Host "Payload prepared: $payloadExe"
Write-Host "Payload prepared: $payloadTrayExe"
Write-Host "Payload prepared: $payloadControlCenterExe"

Write-Host "Building MSI..."
dotnet build $installerProject -c $Configuration `
    /p:ProductVersion=$Version `
    /p:AriBackendUrlDefault=$BackendUrl `
    /p:AriExeSource=$payloadExe `
    /p:AriTrayExeSource=$payloadTrayExe `
    /p:ControlCenterExeSource=$payloadControlCenterExe
if ($LASTEXITCODE -ne 0) {
    throw "MSI build failed with exit code $LASTEXITCODE."
}

Write-Host "MSI build complete. Output:"
$msi = Get-ChildItem (Join-Path $repoRoot "installer/bin") -Filter "*.msi" -Recurse |
    Sort-Object LastWriteTime -Descending
if (-not $msi) {
    throw "No MSI was produced under installer/bin."
}
$msi | Select-Object -First 1 FullName, Length, LastWriteTime
