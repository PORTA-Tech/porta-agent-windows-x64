[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ServiceName = "PortaAgentService"
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

$service = Get-ServiceOrNull $ServiceName
if (-not $service) {
    Write-Host "Service '$ServiceName' not found; nothing to uninstall."
    exit 0
}

if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
    Write-Host "Stopping service '$ServiceName'..."
    Stop-Service -Name $ServiceName -Force -ErrorAction Stop
    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(20))
}

if ($PSCmdlet.ShouldProcess($ServiceName, "Delete service")) {
    Write-Host "Deleting service '$ServiceName'..."
    & sc.exe delete $ServiceName
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE." }
}

Write-Host "Service uninstall complete."
