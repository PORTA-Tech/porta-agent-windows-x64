[CmdletBinding()]
param(
    [string]$Version = "1.0.20",
    [string]$BackendUrl = "https://porta-control-plane.onrender.com",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$SignToolPath = $env:PORTA_SIGNTOOL_PATH,
    [string]$SigningThumbprint = $env:PORTA_SIGNING_CERT_THUMBPRINT,
    [string]$TimestampUrl = $env:PORTA_SIGNING_TIMESTAMP_URL,
    [switch]$RequireSigning
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

function Resolve-SignTool {
    param(
        [string]$ConfiguredPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ConfiguredPath)) {
        if (-not (Test-Path $ConfiguredPath)) {
            throw "Configured SignTool path '$ConfiguredPath' does not exist."
        }

        return (Resolve-Path $ConfiguredPath).Path
    }

    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    return $null
}

function Sign-MsiIfConfigured {
    param(
        [Parameter(Mandatory = $true)]
        [string]$MsiPath
    )

    $signTool = Resolve-SignTool -ConfiguredPath $SignToolPath
    $hasSigningInputs = -not [string]::IsNullOrWhiteSpace($SigningThumbprint)

    if (-not $hasSigningInputs) {
        if ($RequireSigning) {
            throw "MSI signing is required, but no signing thumbprint was provided."
        }

        Write-Warning "MSI signing skipped because no signing thumbprint was provided."
        return
    }

    if ([string]::IsNullOrWhiteSpace($signTool)) {
        throw "MSI signing was requested, but signtool.exe could not be found."
    }

    $arguments = @(
        "sign",
        "/fd", "sha256",
        "/sha1", $SigningThumbprint
    )

    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $arguments += @("/tr", $TimestampUrl, "/td", "sha256")
    }

    $arguments += $MsiPath

    Write-Host "Signing MSI with Authenticode..."
    & $signTool @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "MSI signing failed with exit code $LASTEXITCODE."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $MsiPath
    if ($signature.Status -ne "Valid") {
        throw "MSI signing completed, but signature validation returned '$($signature.Status)'."
    }

    Write-Host "MSI signature validated."
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
$latestMsi = $msi | Select-Object -First 1

Sign-MsiIfConfigured -MsiPath $latestMsi.FullName

$hash = Get-FileHash -LiteralPath $latestMsi.FullName -Algorithm SHA256
Write-Host "MSI SHA256: $($hash.Hash.ToLowerInvariant())"
$signatureStatus = (Get-AuthenticodeSignature -LiteralPath $latestMsi.FullName).Status
Write-Host "MSI signature status: $signatureStatus"
$latestMsi | Select-Object FullName, Length, LastWriteTime
