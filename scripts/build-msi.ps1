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
$servicePublishDir = Join-Path $repoRoot "src/Porta.Agent.Service/bin/$Configuration/net8.0-windows/$Runtime/publish"
$ariExe = Join-Path $servicePublishDir "ari.exe"
$runtimeProject = Join-Path $repoRoot "src/Porta.Agent.Runtime/Porta.Agent.Runtime.csproj"
$runtimePublishDir = Join-Path $repoRoot "src/Porta.Agent.Runtime/bin/$Configuration/net8.0-windows/$Runtime/publish"
$ariTrayExe = Join-Path $runtimePublishDir "Porta.Agent.Runtime.exe"
$installerProject = Join-Path $repoRoot "installer/AriAgent.Installer.wixproj"
$payloadDir = Join-Path $repoRoot "installer/payload"
$payloadExe = Join-Path $payloadDir "ari.exe"
$payloadTrayExe = Join-Path $payloadDir "AriTray.exe"

Write-Host "Publishing ari.exe ($Configuration, $Runtime)..."
dotnet publish $serviceProject -c $Configuration -r $Runtime --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $ariExe)) {
    throw "Published binary not found at '$ariExe'."
}

Write-Host "Publishing Ari tray app ($Configuration, $Runtime)..."
dotnet publish $runtimeProject -c $Configuration -r $Runtime --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish for tray app failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $ariTrayExe)) {
    throw "Published tray binary not found at '$ariTrayExe'."
}

if (-not (Test-Path $payloadDir)) {
    New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
}

Copy-Item $ariExe $payloadExe -Force
Copy-Item $ariTrayExe $payloadTrayExe -Force
Write-Host "Payload prepared: $payloadExe"
Write-Host "Tray payload prepared: $payloadTrayExe"

Write-Host "Building MSI..."
dotnet build $installerProject -c $Configuration `
    /p:ProductVersion=$Version `
    /p:AriBackendUrlDefault=$BackendUrl `
    /p:AriExeSource=$payloadExe `
    /p:AriTrayExeSource=$payloadTrayExe
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
