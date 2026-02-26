[CmdletBinding()]
param(
    [string]$MsiPath = "C:\Users\hp\porta-agent-windows-x64\installer\bin\x64\Release\AriAgentInstaller.msi",
    [string]$InstallLogPath = "C:\Users\hp\porta-agent-windows-x64\Phase1-Install.log",
    [string]$UninstallLogPath = "C:\Users\hp\porta-agent-windows-x64\Phase1-Uninstall.log"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Check {
    param(
        [string]$Name,
        [bool]$Pass,
        [string]$Detail
    )

    $status = if ($Pass) { "PASS" } else { "FAIL" }
    Write-Host "[$status] $Name :: $Detail"
    return $Pass
}

function Invoke-Msi {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$MsiArgs
    )

    $raw = @($MsiArgs)
    $sanitized = @($raw | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($sanitized.Count -ne $raw.Count) {
        throw "Invoke-Msi received a null/empty argument. Raw args: $($raw -join ' | ')"
    }

    $p = Start-Process msiexec.exe -ArgumentList $sanitized -Wait -PassThru
    return $p.ExitCode
}

function Get-AriProductCode {
    $paths = @(
        "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*",
        "HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*"
    )

    $entry = Get-ItemProperty $paths -ErrorAction SilentlyContinue |
        Where-Object {
            $_.PSObject.Properties.Name -contains "DisplayName" -and
            $_.DisplayName -eq "Ari Agent"
        } |
        Select-Object -First 1

    if (-not $entry) {
        return $null
    }

    if ($entry.PSChildName -match "^\{[0-9A-Fa-f-]+\}$") {
        return $entry.PSChildName
    }

    if ($entry.UninstallString -match "\{[0-9A-Fa-f-]+\}") {
        return $matches[0]
    }

    return $null
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Run this script from an elevated (Administrator) PowerShell."
}

if (-not (Test-Path $MsiPath)) {
    throw "MSI not found: $MsiPath"
}

$allPass = $true

Write-Host "Stopping running processes/services..."
Get-Process AriTray,Porta.ControlCenter -ErrorAction SilentlyContinue | Stop-Process -Force
if (Get-Service AriAgent -ErrorAction SilentlyContinue) {
    Stop-Service AriAgent -Force -ErrorAction SilentlyContinue
}

$existingCode = Get-AriProductCode
if ($existingCode) {
    Write-Host "Uninstalling existing Ari Agent: $existingCode"
    $code = Invoke-Msi @("/x", $existingCode, "/qn", "/norestart", "/log", $UninstallLogPath)
    $allPass = (Write-Check "Pre-clean uninstall" ($code -eq 0) "ExitCode=$code") -and $allPass
}
else {
    Write-Host "No prior Ari Agent install detected."
}

Write-Host "Installing MSI..."
$installCode = Invoke-Msi @("/i", $MsiPath, "/qn", "/norestart", "/log", $InstallLogPath)
$allPass = (Write-Check "Install MSI" ($installCode -eq 0) "ExitCode=$installCode") -and $allPass

Start-Sleep -Seconds 2

$installDir = "C:\Program Files\Ari"
$requiredFiles = @("ari.exe", "AriTray.exe", "Porta.ControlCenter.exe", "appsettings.json")
$missingFiles = @()
foreach ($f in $requiredFiles) {
    if (-not (Test-Path (Join-Path $installDir $f))) {
        $missingFiles += $f
    }
}
$allPass = (Write-Check "Artifacts installed" ($missingFiles.Count -eq 0) ("Missing=" + ($missingFiles -join ","))) -and $allPass

$startMenuLink = "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\PORTA\PORTA Control Center.lnk"
$linkExists = Test-Path $startMenuLink
$allPass = (Write-Check "Start Menu link exists" $linkExists $startMenuLink) -and $allPass

if ($linkExists) {
    $wshell = New-Object -ComObject WScript.Shell
    $shortcut = $wshell.CreateShortcut($startMenuLink)
    $targetOk = $shortcut.TargetPath -ieq (Join-Path $installDir "Porta.ControlCenter.exe")
    $allPass = (Write-Check "Start Menu target is Control Center" $targetOk ("Target=" + $shortcut.TargetPath)) -and $allPass
}

$runKey = Get-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run" -ErrorAction SilentlyContinue
$runVal = $runKey."PORTA Tray"
$runOk = -not [string]::IsNullOrWhiteSpace($runVal)
$allPass = (Write-Check "Tray autostart configured" $runOk ("RunValue=" + $runVal)) -and $allPass

if ($linkExists) {
    Start-Process $startMenuLink
    Start-Sleep -Seconds 1
    Start-Process $startMenuLink
    Start-Sleep -Seconds 2
    $ccCount = (Get-Process Porta.ControlCenter -ErrorAction SilentlyContinue | Measure-Object).Count
    $allPass = (Write-Check "Start Menu opens same Control Center instance" ($ccCount -eq 1) "ProcessCount=$ccCount") -and $allPass
}

Start-Process (Join-Path $installDir "AriTray.exe")
Start-Sleep -Seconds 1
Start-Process (Join-Path $installDir "AriTray.exe")
Start-Sleep -Seconds 2
$trayCount = (Get-Process AriTray -ErrorAction SilentlyContinue | Measure-Object).Count
$allPass = (Write-Check "Tray single-instance" ($trayCount -eq 1) "ProcessCount=$trayCount") -and $allPass

$ccCountFinal = (Get-Process Porta.ControlCenter -ErrorAction SilentlyContinue | Measure-Object).Count
$allPass = (Write-Check "Control Center single-instance" ($ccCountFinal -eq 1) "ProcessCount=$ccCountFinal") -and $allPass

Write-Host "Testing uninstall cleanup..."
$installedCode = Get-AriProductCode
if (-not $installedCode) {
    $allPass = (Write-Check "Locate installed product code for uninstall" $false "Not found") -and $allPass
}
else {
    Get-Process AriTray,Porta.ControlCenter -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Get-Service AriAgent -ErrorAction SilentlyContinue) {
        Stop-Service AriAgent -Force -ErrorAction SilentlyContinue
    }

    $code = Invoke-Msi @("/x", $installedCode, "/qn", "/norestart", "/log", $UninstallLogPath)
    $allPass = (Write-Check "Uninstall MSI" ($code -eq 0) "ExitCode=$code") -and $allPass

    Start-Sleep -Seconds 2
    $linkGone = -not (Test-Path $startMenuLink)
    $allPass = (Write-Check "Start Menu link removed" $linkGone $startMenuLink) -and $allPass

    $runKeyAfter = Get-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Run" -ErrorAction SilentlyContinue
    $runValue = $null
    if ($runKeyAfter -and ($runKeyAfter.PSObject.Properties.Name -contains "PORTA Tray")) {
        $runValue = $runKeyAfter."PORTA Tray"
    }
    $runGone = [string]::IsNullOrWhiteSpace($runValue)
    $allPass = (Write-Check "Tray autostart removed" $runGone ("RunValue=" + $runValue)) -and $allPass
}

if ($allPass) {
    Write-Host "`nPHASE 1 ACCEPTANCE: PASS"
    exit 0
}

Write-Host "`nPHASE 1 ACCEPTANCE: FAIL"
exit 1
