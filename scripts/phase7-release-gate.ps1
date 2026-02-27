[CmdletBinding()]
param(
    [string]$MsiPath = "C:\Users\hp\porta-agent-windows-x64\installer\bin\x64\Release\AriAgentInstaller.msi",
    [int]$ApiStartupTimeoutSeconds = 45
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
        [string[]]$Args
    )

    $filtered = @($Args | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($filtered.Count -ne $Args.Count) {
        throw "Invoke-Msi got null/empty argument."
    }

    $p = Start-Process msiexec.exe -ArgumentList $filtered -PassThru -Wait
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

function Stop-PortaProcesses {
    Get-Process AriTray,Porta.ControlCenter -ErrorAction SilentlyContinue | Stop-Process -Force
    $service = Get-Service AriAgent -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') {
        Stop-Service AriAgent -Force -ErrorAction SilentlyContinue
    }
}

function Unprotect-TokenBytes {
    param(
        [Parameter(Mandatory = $true)][byte[]]$ProtectedBytes,
        [Parameter(Mandatory = $true)][byte[]]$EntropyBytes
    )

    try { Add-Type -AssemblyName System.Security.Cryptography.ProtectedData -ErrorAction Stop } catch {}
    try { Add-Type -AssemblyName System.Security -ErrorAction Stop } catch {}

    $protectedDataType = [type]::GetType("System.Security.Cryptography.ProtectedData, System.Security.Cryptography.ProtectedData", $false)
    if (-not $protectedDataType) {
        $protectedDataType = [type]::GetType("System.Security.Cryptography.ProtectedData, System.Security", $false)
    }

    if ($protectedDataType) {
        $scopeType = [type]::GetType("System.Security.Cryptography.DataProtectionScope, System.Security.Cryptography.ProtectedData", $false)
        if (-not $scopeType) {
            $scopeType = [type]::GetType("System.Security.Cryptography.DataProtectionScope, System.Security", $true)
        }

        $scope = [System.Enum]::Parse($scopeType, "LocalMachine")
        return $protectedDataType::Unprotect($ProtectedBytes, $EntropyBytes, $scope)
    }

    if (-not ("PortaDpapiNative" -as [type])) {
        Add-Type -Language CSharp -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class PortaDpapiNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static byte[] Unprotect(byte[] protectedData, byte[] entropy)
    {
        if (protectedData == null || protectedData.Length == 0)
        {
            throw new ArgumentException("protectedData is empty");
        }

        DATA_BLOB inBlob = new DATA_BLOB();
        DATA_BLOB entBlob = new DATA_BLOB();
        DATA_BLOB outBlob = new DATA_BLOB();
        try
        {
            inBlob.cbData = protectedData.Length;
            inBlob.pbData = Marshal.AllocHGlobal(protectedData.Length);
            Marshal.Copy(protectedData, 0, inBlob.pbData, protectedData.Length);

            entBlob.cbData = entropy.Length;
            entBlob.pbData = Marshal.AllocHGlobal(entropy.Length);
            Marshal.Copy(entropy, 0, entBlob.pbData, entropy.Length);

            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob, IntPtr.Zero, IntPtr.Zero, 0, ref outBlob))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            byte[] result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (entBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(entBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}
"@
    }

    return [PortaDpapiNative]::Unprotect($ProtectedBytes, $EntropyBytes)
}

function Get-LocalApiContext {
    $securePath = Join-Path $env:ProgramData "PortaAgent\security\local-api.secure.json"
    $endpointPath = Join-Path $env:ProgramData "PortaAgent\local-api.endpoint.json"

    if (!(Test-Path $securePath) -or !(Test-Path $endpointPath)) {
        return $null
    }

    $secure = Get-Content $securePath -Raw | ConvertFrom-Json
    $endpoint = Get-Content $endpointPath -Raw | ConvertFrom-Json

    if (-not $secure.authTokenProtected -or -not $endpoint.port -or -not $endpoint.host) {
        return $null
    }

    $entropy = [byte[]](0x50,0x4F,0x52,0x54,0x41,0x5F,0x4C,0x4F,0x43,0x41,0x4C,0x5F,0x41,0x50,0x49)
    $protectedBytes = [Convert]::FromBase64String([string]$secure.authTokenProtected)
    $plainBytes = Unprotect-TokenBytes -ProtectedBytes $protectedBytes -EntropyBytes $entropy
    $token = [System.Text.Encoding]::UTF8.GetString($plainBytes)

    return [pscustomobject]@{
        BaseUrl = "http://$($endpoint.host):$($endpoint.port)/v1"
        Headers = @{ "X-PORTA-TOKEN" = $token }
    }
}

function Invoke-Api {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Path,
        [object]$Body = $null,
        [switch]$AllowFailure
    )

    if (-not $script:ApiContext) {
        throw "ApiContext not initialized"
    }

    $uri = "$($script:ApiContext.BaseUrl)$Path"
    try {
        if ($null -eq $Body) {
            $result = Invoke-RestMethod -Method $Method -Uri $uri -Headers $script:ApiContext.Headers
        }
        else {
            $json = $Body | ConvertTo-Json -Depth 8 -Compress
            $result = Invoke-RestMethod -Method $Method -Uri $uri -Headers $script:ApiContext.Headers -ContentType "application/json" -Body $json
        }

        return [pscustomobject]@{
            Success = $true
            StatusCode = 200
            Body = $result
            Error = $null
        }
    }
    catch {
        $statusCode = 0
        $errorText = $_.Exception.Message
        $errorJson = $null

        if ($_.Exception.Response) {
            try { $statusCode = [int]$_.Exception.Response.StatusCode } catch {}
            try {
                $stream = $_.Exception.Response.GetResponseStream()
                if ($stream) {
                    $reader = New-Object System.IO.StreamReader($stream)
                    $errorText = $reader.ReadToEnd()
                    $reader.Dispose()
                }
            }
            catch {}
            try {
                if ($errorText -and $errorText.Trim().StartsWith('{')) {
                    $errorJson = $errorText | ConvertFrom-Json
                }
            }
            catch {}
        }

        if (-not $AllowFailure) {
            throw
        }

        return [pscustomobject]@{
            Success = $false
            StatusCode = $statusCode
            Body = $errorJson
            Error = $errorText
        }
    }
}

function Wait-ApiReady {
    param([int]$TimeoutSeconds)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-Api -Method GET -Path "/status" -AllowFailure
            if ($r.Success -and $r.Body) {
                return $true
            }
        }
        catch {}

        Start-Sleep -Seconds 1
    }

    return $false
}

function Get-ZipEntries {
    param([Parameter(Mandatory = $true)][string]$ZipPath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        return @($zip.Entries | ForEach-Object { $_.FullName })
    }
    finally {
        $zip.Dispose()
    }
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Run this script from elevated (Administrator) PowerShell."
}

if (-not (Test-Path $MsiPath)) {
    throw "MSI not found: $MsiPath"
}

$allPass = $true

Write-Host "Phase 7 release gate starting..."
Stop-PortaProcesses

$existingCode = Get-AriProductCode
if ($existingCode) {
    $preUninstallCode = Invoke-Msi @('/x', $existingCode, '/qn', '/norestart')
    $allPass = (Write-Check "Pre-clean uninstall" ($preUninstallCode -eq 0) "ExitCode=$preUninstallCode") -and $allPass
}

$installCode = Invoke-Msi @('/i', $MsiPath, '/qn', '/norestart')
$allPass = (Write-Check "Install MSI" ($installCode -eq 0) "ExitCode=$installCode") -and $allPass
if ($installCode -ne 0) {
    Write-Host "`nPHASE 7 RELEASE GATE: FAIL"
    exit 1
}

$installDir = "C:\Program Files\Ari"
$startMenuLink = "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\PORTA\PORTA Control Center.lnk"
$controlCenterExe = Join-Path $installDir "Porta.ControlCenter.exe"
$trayExe = Join-Path $installDir "AriTray.exe"

$allPass = (Write-Check "Start Menu link exists" (Test-Path $startMenuLink) $startMenuLink) -and $allPass
$allPass = (Write-Check "Control Center binary exists" (Test-Path $controlCenterExe) $controlCenterExe) -and $allPass
$allPass = (Write-Check "Tray binary exists" (Test-Path $trayExe) $trayExe) -and $allPass

if (Test-Path $startMenuLink) {
    Start-Process $startMenuLink
    Start-Sleep -Seconds 1
    Start-Process $startMenuLink
    Start-Sleep -Seconds 2
    $ccCount = (Get-Process Porta.ControlCenter -ErrorAction SilentlyContinue | Measure-Object).Count
    $allPass = (Write-Check "No duplicate Control Center" ($ccCount -eq 1) "ProcessCount=$ccCount") -and $allPass
}

Start-Process $trayExe
Start-Sleep -Seconds 1
Start-Process $trayExe
Start-Sleep -Seconds 2
$trayCount = (Get-Process AriTray -ErrorAction SilentlyContinue | Measure-Object).Count
$allPass = (Write-Check "No duplicate Tray" ($trayCount -eq 1) "ProcessCount=$trayCount") -and $allPass

# UI opens when service is down
$svc = Get-Service AriAgent -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') {
    Stop-Service AriAgent -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 1
Start-Process $controlCenterExe
Start-Sleep -Seconds 3
$uiAliveWhenServiceDown = (Get-Process Porta.ControlCenter -ErrorAction SilentlyContinue | Measure-Object).Count -ge 1
$allPass = (Write-Check "UI opens with service down" $uiAliveWhenServiceDown "ProcessAlive=$uiAliveWhenServiceDown") -and $allPass

if ($svc) {
    Start-Service AriAgent -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 3

$script:ApiContext = Get-LocalApiContext
$apiContextReady = $null -ne $script:ApiContext
$allPass = (Write-Check "Local API config present" $apiContextReady "ProgramData\\PortaAgent") -and $allPass
if (-not $apiContextReady) {
    Write-Host "`nPHASE 7 RELEASE GATE: FAIL"
    exit 1
}

$apiReady = Wait-ApiReady -TimeoutSeconds $ApiStartupTimeoutSeconds
$allPass = (Write-Check "Local API reachable" $apiReady "Timeout=${ApiStartupTimeoutSeconds}s") -and $allPass
if (-not $apiReady) {
    Write-Host "`nPHASE 7 RELEASE GATE: FAIL"
    exit 1
}

# Ensure consent active for proof/regulation checks
$rGrant = Invoke-Api -Method POST -Path "/consent/grant"
Start-Sleep -Seconds 4

$status = (Invoke-Api -Method GET -Path "/status").Body
$snapshot = (Invoke-Api -Method GET -Path "/snapshot/current").Body
$inputs = (Invoke-Api -Method GET -Path "/inputs/summary?window=10").Body

$inputsLive = $inputs -and $inputs.sampleCount -ge 1
$statesLive = $snapshot -and ($snapshot.focusStrength -ge 0 -and $snapshot.focusStrength -le 1) -and ($snapshot.overloadLikelihood -ge 0 -and $snapshot.overloadLikelihood -le 1)
$diagFields = $status -and -not [string]::IsNullOrWhiteSpace([string]$status.agentVersion) -and -not [string]::IsNullOrWhiteSpace([string]$status.schemaVersion) -and -not [string]::IsNullOrWhiteSpace([string]$status.policyVersion) -and $null -ne $status.lastEventTimestampUtc

$allPass = (Write-Check "Inputs show live counters" $inputsLive "SampleCount=$($inputs.sampleCount)") -and $allPass
$allPass = (Write-Check "Overview shows live states" $statesLive "Focus=$($snapshot.focusStrength),Overload=$($snapshot.overloadLikelihood)") -and $allPass
$allPass = (Write-Check "Diagnostics fields populated" $diagFields "Agent=$($status.agentVersion),Schema=$($status.schemaVersion),Policy=$($status.policyVersion)") -and $allPass

# Pause / resume gate
Invoke-Api -Method POST -Path "/control/pause" -Body @{ reason = "phase7_gate_pause" } | Out-Null
Start-Sleep -Seconds 6
$statusPaused = (Invoke-Api -Method GET -Path "/status").Body
$inputsPaused = (Invoke-Api -Method GET -Path "/inputs/summary?window=5").Body
$pauseGate = $statusPaused.paused -eq $true -and $inputsPaused.sampleCount -le 1
$allPass = (Write-Check "Pause stops collection/state updates" $pauseGate "Paused=$($statusPaused.paused),PausedSampleCount=$($inputsPaused.sampleCount)") -and $allPass

Invoke-Api -Method POST -Path "/control/resume" | Out-Null
Start-Sleep -Seconds 3
$statusResumed = (Invoke-Api -Method GET -Path "/status").Body
$allPass = (Write-Check "Resume re-enables activity" ($statusResumed.paused -eq $false) "Paused=$($statusResumed.paused)") -and $allPass

# Revoke consent gate
Invoke-Api -Method POST -Path "/consent/revoke" | Out-Null
Start-Sleep -Seconds 2
$statusRevoked = (Invoke-Api -Method GET -Path "/status").Body
$revokeGate = $statusRevoked.consentActive -eq $false -and $statusRevoked.paused -eq $true
$allPass = (Write-Check "Revoke consent stops everything" $revokeGate "ConsentActive=$($statusRevoked.consentActive),Paused=$($statusRevoked.paused)") -and $allPass

# Export bundle gate
Invoke-Api -Method POST -Path "/consent/grant" | Out-Null
Start-Sleep -Seconds 1
$exportResp = (Invoke-Api -Method POST -Path "/data/export").Body
$exportPath = [string]$exportResp.bundlePath
$exportExists = -not [string]::IsNullOrWhiteSpace($exportPath) -and (Test-Path $exportPath)
$allPass = (Write-Check "Export bundle generated" $exportExists $exportPath) -and $allPass

$bundleShapeOk = $false
if ($exportExists) {
    $entries = Get-ZipEntries -ZipPath $exportPath
    $required = @(
        "core-state.db",
        "versions.json",
        "diagnostics-summary.json",
        "settings-effective.json",
        "consent-record.json"
    )

    $requiredPresent = @($required | Where-Object { $entries -contains $_ })
    $hasServiceLog = $entries -contains "logs/service-core.log"
    $hasTrayLog = $entries -contains "logs/tray.log"
    $hasUiLog = $entries -contains "logs/control-center.log"
    $bundleShapeOk = ($requiredPresent.Count -eq $required.Count) -and $hasServiceLog -and $hasTrayLog -and $hasUiLog

    $allPass = (Write-Check "Export bundle content complete" $bundleShapeOk "Required=$($requiredPresent.Count)/$($required.Count),Logs(service/tray/ui)=$hasServiceLog/$hasTrayLog/$hasUiLog") -and $allPass
}

# Delete local data gate
Invoke-Api -Method POST -Path "/data/delete_local" | Out-Null
Start-Sleep -Seconds 2
$statusAfterDelete = (Invoke-Api -Method GET -Path "/status").Body
$dbPath = "C:\ProgramData\PortaAgent\core-state.db"
$serviceLog = "C:\ProgramData\PortaAgent\service-core.log"
$trayLog = "C:\ProgramData\PortaAgent\tray.log"
$uiLog = "C:\ProgramData\PortaAgent\control-center.log"
$dbBytes = if (Test-Path $dbPath) { (Get-Item $dbPath).Length } else { 0 }
$logBytes = 0
foreach ($p in @($serviceLog, $trayLog, $uiLog)) {
    if (Test-Path $p) { $logBytes += (Get-Item $p).Length }
}
$deleteGate = ($statusAfterDelete.consentActive -eq $false) -and ($statusAfterDelete.paused -eq $true) -and ($dbBytes -lt 5MB) -and ($logBytes -lt 256KB)
$allPass = (Write-Check "Delete local data works (reset + wipe)" $deleteGate "Consent=$($statusAfterDelete.consentActive),Paused=$($statusAfterDelete.paused),DbBytes=$dbBytes,LogBytes=$logBytes") -and $allPass

# Regulation/test-mode gate + undo/category-disable gate
Invoke-Api -Method POST -Path "/consent/grant" | Out-Null
Invoke-Api -Method POST -Path "/control/resume" | Out-Null
Invoke-Api -Method POST -Path "/settings/update" -Body @{ focusModeEnabled = $true } | Out-Null

$trigger1 = Invoke-Api -Method POST -Path "/testing/intervention" -Body @{ action = "focus_mode_enabled" } -AllowFailure
Start-Sleep -Seconds 1
$events = (Invoke-Api -Method GET -Path "/events/recent?limit=20").Body
$hasIntervention = $events | Where-Object { $_.action -eq "focus_mode_enabled" } | Select-Object -First 1
$regulationGate = $trigger1.Success -and $null -ne $hasIntervention
$allPass = (Write-Check "Intervention triggers and is visible" $regulationGate "TriggerSuccess=$($trigger1.Success),Visible=$($null -ne $hasIntervention)") -and $allPass

# Undo via category disable and verify block
Invoke-Api -Method POST -Path "/settings/update" -Body @{ focusModeEnabled = $false } | Out-Null
$trigger2 = Invoke-Api -Method POST -Path "/testing/intervention" -Body @{ action = "focus_mode_enabled" } -AllowFailure
$blocked = (-not $trigger2.Success) -and ($trigger2.Error -match "category_disabled" -or ($trigger2.Body -and [string]$trigger2.Body.message -eq "category_disabled"))
$allPass = (Write-Check "Undo/category disable prevents re-trigger" $blocked "Blocked=$blocked") -and $allPass

if ($allPass) {
    Write-Host "`nPHASE 7 RELEASE GATE: PASS"
    exit 0
}

Write-Host "`nPHASE 7 RELEASE GATE: FAIL"
exit 1
