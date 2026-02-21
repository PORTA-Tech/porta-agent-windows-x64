[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ServiceName = "AriAgent",
    [string]$DisplayName = "Ari Agent",
    [string]$Description = "PORTA Ari Agent Windows service host.",
    [string]$ProjectPath = "src/Porta.Agent.Service/Porta.Agent.Service.csproj",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$PublishDir = "artifacts/service/publish",
    [switch]$ForceReinstall,
    [switch]$StartAfterInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-Administrator {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Administrator privileges are required."
    }
}

function Get-ServiceOrNull([string]$name) {
    try {
        return Get-Service -Name $name -ErrorAction Stop
    }
    catch {
        return $null
    }
}

Assert-Administrator

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$resolvedProject = Resolve-Path (Join-Path $repoRoot $ProjectPath)
$resolvedPublishDir = Join-Path $repoRoot $PublishDir

if ($ForceReinstall) {
    & "$PSScriptRoot/uninstall-service.ps1" -ServiceName $ServiceName -ErrorAction Stop
}
elseif (Get-ServiceOrNull $ServiceName) {
    throw "Service '$ServiceName' already exists. Use -ForceReinstall to replace it."
}

Write-Host "Publishing service host..."
dotnet publish $resolvedProject -c $Configuration -r $Runtime --self-contained false -o $resolvedPublishDir

$exePath = Join-Path $resolvedPublishDir "ari.exe"
if (-not (Test-Path $exePath)) {
    throw "Published executable not found at '$exePath'."
}

if ($PSCmdlet.ShouldProcess($ServiceName, "Create service")) {
    Write-Host "Creating service '$ServiceName'..."
    & sc.exe create $ServiceName binPath= "`"$exePath`"" start= auto DisplayName= "`"$DisplayName`""
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed with exit code $LASTEXITCODE." }

    & sc.exe description $ServiceName "$Description"
    if ($LASTEXITCODE -ne 0) { throw "sc.exe description failed with exit code $LASTEXITCODE." }

    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000
    if ($LASTEXITCODE -ne 0) { throw "sc.exe failure failed with exit code $LASTEXITCODE." }

    & sc.exe failureflag $ServiceName 1
    if ($LASTEXITCODE -ne 0) { throw "sc.exe failureflag failed with exit code $LASTEXITCODE." }
}

if ($StartAfterInstall) {
    Write-Host "Starting service '$ServiceName'..."
    Start-Service -Name $ServiceName
}

Write-Host "Service install complete."
Write-Host "Service Name: $ServiceName"
Write-Host "Binary Path : $exePath"
