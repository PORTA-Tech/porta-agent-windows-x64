namespace Porta.ControlCenter.Updates;

internal static class WindowsUpdateInstallScriptBuilder
{
    public static string Build()
    {
        return """
param(
    [Parameter(Mandatory = $true)]
    [string]$ContextPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$script:Context = $null
$script:InstallerExitCode = 1
$script:RestartRequired = $false
$script:ServiceSeen = $false

function Write-UpdateStatus {
    param(
        [bool]$Succeeded,
        [string]$Version,
        [bool]$RestartRequired,
        [int]$ExitCode,
        [string]$Message,
        [string]$LogPath
    )

    if ($null -eq $script:Context -or [string]::IsNullOrWhiteSpace($script:Context.StatusPath)) {
        return
    }

    $payload = [ordered]@{
        succeeded = $Succeeded
        version = $Version
        restartRequired = $RestartRequired
        exitCode = $ExitCode
        message = $Message
        logPath = $LogPath
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    }

    $payload | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $script:Context.StatusPath -Encoding UTF8
}

function Wait-ForProcessExit {
    param(
        [int]$ProcessId,
        [int]$TimeoutSeconds
    )

    if ($ProcessId -le 0) {
        return
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($null -eq $process) {
            return
        }

        Start-Sleep -Milliseconds 500
    }

    $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -ne $process) {
        Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
    }
}

function Start-ControlCenter {
    param(
        [string]$PreferredPath,
        [string]$FallbackPath
    )

    $launchPath = $null
    if (-not [string]::IsNullOrWhiteSpace($PreferredPath) -and (Test-Path $PreferredPath)) {
        $launchPath = $PreferredPath
    }
    elseif (-not [string]::IsNullOrWhiteSpace($FallbackPath) -and (Test-Path $FallbackPath)) {
        $launchPath = $FallbackPath
    }

    if ([string]::IsNullOrWhiteSpace($launchPath)) {
        return
    }

    $explorerPath = Join-Path $env:WINDIR "explorer.exe"
    Start-Process -FilePath $explorerPath -ArgumentList @($launchPath) -ErrorAction SilentlyContinue | Out-Null
}

try {
    $script:Context = Get-Content -LiteralPath $ContextPath -Raw | ConvertFrom-Json
    if (-not (Test-Path $script:Context.InstallerPath)) {
        throw "The downloaded update package is missing."
    }

    if ($script:Context.RequireSha256Validation -and [string]::IsNullOrWhiteSpace($script:Context.ExpectedSha256)) {
        throw "The published update is missing a SHA-256 checksum."
    }

    if (-not [string]::IsNullOrWhiteSpace($script:Context.ExpectedSha256)) {
        $actualHash = (Get-FileHash -LiteralPath $script:Context.InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne $script:Context.ExpectedSha256.ToLowerInvariant()) {
            throw "The downloaded update package no longer matches the published SHA-256 checksum."
        }
    }

    if ($script:Context.RequireSignatureValidation) {
        $signature = Get-AuthenticodeSignature -LiteralPath $script:Context.InstallerPath
        if ($signature.Status -ne "Valid") {
            throw "The downloaded update package is not signed with a valid Windows Authenticode signature. Status: $($signature.Status)."
        }
    }

    Wait-ForProcessExit -ProcessId ([int]$script:Context.CurrentControlCenterPid) -TimeoutSeconds 45

    if (-not [string]::IsNullOrWhiteSpace($script:Context.ServiceName)) {
        $service = Get-Service -Name $script:Context.ServiceName -ErrorAction SilentlyContinue
        if ($null -ne $service) {
            $script:ServiceSeen = $true
            if ($service.Status -ne "Stopped") {
                Stop-Service -Name $script:Context.ServiceName -Force -ErrorAction Stop
                $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
            }
        }
    }

    Get-Process -Name "Porta.UserAgent" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process -Name "Porta.ControlCenter" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    $msiArguments = @(
        "/i", $script:Context.InstallerPath,
        "/qn",
        "/norestart",
        "REBOOT=ReallySuppress",
        "/L*V", $script:Context.LogPath
    )

    $msiProcess = Start-Process -FilePath "msiexec.exe" -ArgumentList $msiArguments -Wait -PassThru -WindowStyle Hidden
    $script:InstallerExitCode = $msiProcess.ExitCode
    $script:RestartRequired = $script:InstallerExitCode -eq 3010

    if (($script:InstallerExitCode -ne 0) -and ($script:InstallerExitCode -ne 3010)) {
        throw "Windows Installer failed with exit code $($script:InstallerExitCode). See log at $($script:Context.LogPath)."
    }

    $successMessage = if ($script:RestartRequired) {
        "Ari updated successfully. Windows may need a restart to finish the upgrade."
    }
    else {
        "Ari updated successfully."
    }

    Write-UpdateStatus -Succeeded $true `
        -Version $script:Context.Version `
        -RestartRequired $script:RestartRequired `
        -ExitCode $script:InstallerExitCode `
        -Message $successMessage `
        -LogPath $script:Context.LogPath
}
catch {
    $message = $_.Exception.Message
    Write-UpdateStatus -Succeeded $false `
        -Version $(if ($null -ne $script:Context) { $script:Context.Version } else { "unknown" }) `
        -RestartRequired $false `
        -ExitCode $script:InstallerExitCode `
        -Message $message `
        -LogPath $(if ($null -ne $script:Context) { $script:Context.LogPath } else { "" })
}
finally {
    if ($script:ServiceSeen -and -not [string]::IsNullOrWhiteSpace($script:Context.ServiceName)) {
        try {
            Start-Service -Name $script:Context.ServiceName -ErrorAction Stop
        }
        catch {
        }
    }

    if ($null -ne $script:Context) {
        Start-ControlCenter -PreferredPath $script:Context.RestartControlCenterPath -FallbackPath $script:Context.CurrentControlCenterPath
    }
}

exit $script:InstallerExitCode
""";
    }
}
