[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$BackendUrl = "https://porta-control-plane.onrender.com",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SignArtifacts,
    [string]$CodeSignPfxPath = "",
    [string]$CodeSignPfxBase64 = "",
    [string]$CodeSignPfxPassword = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-SignToolPath {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $sdkRoot = "C:\Program Files (x86)\Windows Kits\10\bin"
    if (-not (Test-Path $sdkRoot)) {
        return $null
    }

    $candidate = Get-ChildItem $sdkRoot -Recurse -Filter signtool.exe -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*\x64\signtool.exe" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    return $candidate?.FullName
}

function Invoke-SignFile {
    param(
        [Parameter(Mandatory = $true)][string]$SignToolPath,
        [Parameter(Mandatory = $true)][string]$PfxPath,
        [Parameter(Mandatory = $true)][string]$PfxPassword,
        [Parameter(Mandatory = $true)][string]$TimestampServer,
        [Parameter(Mandatory = $true)][string]$FilePath
    )

    if (-not (Test-Path $FilePath)) {
        throw "Cannot sign missing file '$FilePath'."
    }

    Write-Host "Signing: $FilePath"
    & $SignToolPath sign /fd SHA256 /f $PfxPath /p $PfxPassword /tr $TimestampServer /td SHA256 $FilePath
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed for '$FilePath' with exit code $LASTEXITCODE."
    }
}

if ([string]::IsNullOrWhiteSpace($CodeSignPfxPath)) {
    $CodeSignPfxPath = $env:PORTA_CODESIGN_PFX_PATH
}

if ([string]::IsNullOrWhiteSpace($CodeSignPfxBase64)) {
    $CodeSignPfxBase64 = $env:PORTA_CODESIGN_PFX_BASE64
}

if ([string]::IsNullOrWhiteSpace($CodeSignPfxPassword)) {
    $CodeSignPfxPassword = $env:PORTA_CODESIGN_PFX_PASSWORD
}

$shouldSign = $SignArtifacts.IsPresent -or
    -not [string]::IsNullOrWhiteSpace($CodeSignPfxPath) -or
    -not [string]::IsNullOrWhiteSpace($CodeSignPfxBase64)

$tempPfxPath = $null
$effectivePfxPath = $null
$signToolPath = $null

trap {
    if ($tempPfxPath -and (Test-Path $tempPfxPath)) {
        Remove-Item $tempPfxPath -Force
    }
    throw $_
}

if ($shouldSign) {
    if ([string]::IsNullOrWhiteSpace($CodeSignPfxPassword)) {
        throw "Code signing requested but certificate password is missing. Set -CodeSignPfxPassword or PORTA_CODESIGN_PFX_PASSWORD."
    }

    if (-not [string]::IsNullOrWhiteSpace($CodeSignPfxBase64)) {
        $tempPfxPath = Join-Path $env:TEMP ("porta-codesign-" + [Guid]::NewGuid().ToString("N") + ".pfx")
        [IO.File]::WriteAllBytes($tempPfxPath, [Convert]::FromBase64String($CodeSignPfxBase64))
        $effectivePfxPath = $tempPfxPath
    }
    elseif (-not [string]::IsNullOrWhiteSpace($CodeSignPfxPath)) {
        $effectivePfxPath = (Resolve-Path $CodeSignPfxPath).Path
    }
    else {
        throw "Code signing requested but no certificate provided. Use -CodeSignPfxPath or -CodeSignPfxBase64."
    }

    $signToolPath = Get-SignToolPath
    if ([string]::IsNullOrWhiteSpace($signToolPath)) {
        throw "Code signing requested but signtool.exe was not found."
    }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$serviceProject = Join-Path $repoRoot "src/Porta.Agent.Service/Porta.Agent.Service.csproj"
$servicePublishDir = Join-Path $repoRoot "src/Porta.Agent.Service/bin/$Configuration/net8.0-windows/$Runtime/publish"
$ariExe = Join-Path $servicePublishDir "ari.exe"
$serviceConfig = Join-Path $servicePublishDir "appsettings.json"
$trayProject = Join-Path $repoRoot "src/Porta.Tray/Porta.Tray.csproj"
$trayPublishDir = Join-Path $repoRoot "src/Porta.Tray/bin/$Configuration/net8.0-windows/$Runtime/publish"
$trayExe = Join-Path $trayPublishDir "Porta.Tray.exe"
$controlCenterProject = Join-Path $repoRoot "src/Porta.ControlCenter/Porta.ControlCenter.csproj"
$controlCenterPublishDir = Join-Path $repoRoot "src/Porta.ControlCenter/bin/$Configuration/net8.0-windows/$Runtime/publish"
$controlCenterExe = Join-Path $controlCenterPublishDir "Porta.ControlCenter.exe"
$installerProject = Join-Path $repoRoot "installer/AriAgent.Installer.wixproj"
$payloadDir = Join-Path $repoRoot "installer/payload"
$payloadStamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMddHHmmss")
$payloadExe = Join-Path $payloadDir "ari-$payloadStamp.exe"
$payloadTrayExe = Join-Path $payloadDir "AriTray-$payloadStamp.exe"
$payloadControlCenterExe = Join-Path $payloadDir "Porta.ControlCenter-$payloadStamp.exe"
$payloadServiceConfig = Join-Path $payloadDir "appsettings-$payloadStamp.json"

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

if (-not (Test-Path $serviceConfig)) {
    throw "Service config not found at '$serviceConfig'."
}

Write-Host "Publishing Ari tray app ($Configuration, $Runtime)..."
dotnet publish $trayProject -c $Configuration -r $Runtime --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish for tray app failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $trayExe)) {
    throw "Published tray binary not found at '$trayExe'."
}

Write-Host "Publishing Control Center app ($Configuration, $Runtime)..."
dotnet publish $controlCenterProject -c $Configuration -r $Runtime --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish for Control Center failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path $controlCenterExe)) {
    throw "Published Control Center binary not found at '$controlCenterExe'."
}

if (-not (Test-Path $payloadDir)) {
    New-Item -ItemType Directory -Path $payloadDir -Force | Out-Null
}

Copy-Item $ariExe $payloadExe -Force
Copy-Item $trayExe $payloadTrayExe -Force
Copy-Item $controlCenterExe $payloadControlCenterExe -Force
Copy-Item $serviceConfig $payloadServiceConfig -Force

if ($shouldSign) {
    Invoke-SignFile -SignToolPath $signToolPath -PfxPath $effectivePfxPath -PfxPassword $CodeSignPfxPassword -TimestampServer $TimestampUrl -FilePath $payloadExe
    Invoke-SignFile -SignToolPath $signToolPath -PfxPath $effectivePfxPath -PfxPassword $CodeSignPfxPassword -TimestampServer $TimestampUrl -FilePath $payloadTrayExe
    Invoke-SignFile -SignToolPath $signToolPath -PfxPath $effectivePfxPath -PfxPassword $CodeSignPfxPassword -TimestampServer $TimestampUrl -FilePath $payloadControlCenterExe
}

Write-Host "Payload prepared: $payloadExe"
Write-Host "Tray payload prepared: $payloadTrayExe"
Write-Host "Control Center payload prepared: $payloadControlCenterExe"
Write-Host "Service config payload prepared: $payloadServiceConfig"

Write-Host "Building MSI..."
dotnet build $installerProject -c $Configuration `
    -t:Rebuild `
    /p:ProductVersion=$Version `
    /p:AriBackendUrlDefault=$BackendUrl `
    /p:AriExeSource=$payloadExe `
    /p:AriTrayExeSource=$payloadTrayExe `
    /p:AriControlCenterExeSource=$payloadControlCenterExe `
    /p:AriServiceConfigSource=$payloadServiceConfig
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

if ($shouldSign) {
    Invoke-SignFile -SignToolPath $signToolPath -PfxPath $effectivePfxPath -PfxPassword $CodeSignPfxPassword -TimestampServer $TimestampUrl -FilePath $latestMsi.FullName
}

$latestMsi | Select-Object FullName, Length, LastWriteTime

if ($tempPfxPath -and (Test-Path $tempPfxPath)) {
    Remove-Item $tempPfxPath -Force
}
