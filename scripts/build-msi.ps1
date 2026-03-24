[CmdletBinding()]
param(
    [string]$Version = "1.0.10",
    [string]$BackendUrl = "https://porta-control-plane.onrender.com",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$serviceProject = Join-Path $repoRoot "src/Porta.Agent.Service/Porta.Agent.Service.csproj"
$userAgentProject = Join-Path $repoRoot "src/Porta.UserAgent/Porta.UserAgent.csproj"
$controlCenterProject = Join-Path $repoRoot "src/Porta.ControlCenter/Porta.ControlCenter.csproj"
$publishDir = Join-Path $repoRoot "src/Porta.Agent.Service/bin/$Configuration/net8.0-windows/$Runtime/publish"
$userAgentPublishDir = Join-Path $repoRoot "src/Porta.UserAgent/bin/$Configuration/net8.0-windows/$Runtime/publish"
$controlCenterPublishDir = Join-Path $repoRoot "src/Porta.ControlCenter/bin/$Configuration/net8.0-windows/$Runtime/publish"
$ariExe = Join-Path $publishDir "ari.exe"
$userAgentExe = Join-Path $userAgentPublishDir "Porta.UserAgent.exe"
$controlCenterExe = Join-Path $controlCenterPublishDir "Porta.ControlCenter.exe"
$installerProject = Join-Path $repoRoot "installer/AriAgent.Installer.wixproj"
$payloadDir = Join-Path $repoRoot "installer/payload"
$payloadExe = Join-Path $payloadDir "ari.exe"
$payloadUserAgentExe = Join-Path $payloadDir "Porta.UserAgent.exe"
$payloadControlCenterExe = Join-Path $payloadDir "Porta.ControlCenter.exe"
$payloadSharedDir = Join-Path $payloadDir "SharedFiles"

function Reset-Directory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (Test-Path $Path) {
        Remove-Item $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Merge-PayloadDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,
        [Parameter(Mandatory = $true)]
        [string]$Destination,
        [Parameter(Mandatory = $true)]
        [string[]]$ExcludeFiles
    )

    Get-ChildItem $Source -Recurse -File | ForEach-Object {
        if ($ExcludeFiles -contains $_.Name -or $_.Extension -eq ".pdb") {
            return
        }

        $sourceRoot = (Resolve-Path $Source).Path.TrimEnd('\')
        $filePath = $_.FullName
        $relativePath = $filePath.Substring($sourceRoot.Length).TrimStart('\')
        $targetPath = Join-Path $Destination $relativePath
        $targetDir = Split-Path -Parent $targetPath
        if (-not [string]::IsNullOrWhiteSpace($targetDir)) {
            New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        }

        Copy-Item $_.FullName $targetPath -Force
    }
}

Write-Host "Publishing ari.exe ($Configuration, $Runtime)..."
dotnet publish $serviceProject -c $Configuration -r $Runtime --self-contained true /p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $ariExe)) {
    throw "Published binary not found at '$ariExe'."
}

Write-Host "Publishing Porta.UserAgent.exe ($Configuration, $Runtime)..."
dotnet publish $userAgentProject -c $Configuration -r $Runtime --self-contained true /p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish for Porta.UserAgent failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $userAgentExe)) {
    throw "Published user agent binary not found at '$userAgentExe'."
}

Write-Host "Publishing Porta.ControlCenter.exe ($Configuration, $Runtime)..."
dotnet publish $controlCenterProject -c $Configuration -r $Runtime --self-contained true /p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish for Porta.ControlCenter failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $controlCenterExe)) {
    throw "Control Center binary not found at '$controlCenterExe'. Build Porta.ControlCenter first."
}

Reset-Directory -Path $payloadDir
New-Item -ItemType Directory -Path $payloadSharedDir -Force | Out-Null

Copy-Item $ariExe $payloadExe -Force
Copy-Item $userAgentExe $payloadUserAgentExe -Force
Copy-Item $controlCenterExe $payloadControlCenterExe -Force
Merge-PayloadDirectory -Source $publishDir -Destination $payloadSharedDir -ExcludeFiles @("ari.exe")
Merge-PayloadDirectory -Source $userAgentPublishDir -Destination $payloadSharedDir -ExcludeFiles @("Porta.UserAgent.exe")
Merge-PayloadDirectory -Source $controlCenterPublishDir -Destination $payloadSharedDir -ExcludeFiles @("Porta.ControlCenter.exe")
Write-Host "Payload prepared: $payloadExe"
Write-Host "Payload prepared: $payloadUserAgentExe"
Write-Host "Payload prepared: $payloadControlCenterExe"
Write-Host "Payload prepared: $payloadSharedDir"

Write-Host "Building MSI..."
dotnet build $installerProject -c $Configuration `
    /p:ProductVersion=$Version `
    /p:AriBackendUrlDefault=$BackendUrl `
    /p:AriExeSource=$payloadExe `
    /p:UserAgentExeSource=$payloadUserAgentExe `
    /p:ControlCenterExeSource=$payloadControlCenterExe `
    /p:SharedPayloadSourceDir=$payloadSharedDir
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
