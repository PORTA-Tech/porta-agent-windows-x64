$ErrorActionPreference = "Stop"
$root = "C:\Users\hp\porta-agent-windows-x64"
$payload = "$root\installer\payload"
$sharedPayload = "$root\installer\payload\SharedFiles"
$version = "1.2.138"

# CLEAN REBUILD, deliberately, not the usual incremental publish.
#
# This build carries the SQLitePCLRaw 2.1.12 upgrade for CVE-2025-6965. An incremental
# build was measured leaving the OLD native e_sqlite3.dll (SQLite 3.41.2) in place while
# the package graph already reported 2.1.12 -- so the payload would have shipped the
# vulnerable binary while every version number said otherwise. install.ps1 only copies
# from payload, so nothing downstream would have caught it.
#
# Stale SharedFiles are wiped too: Copy-Item -Force overwrites what it copies but never
# removes what publish no longer produces.
Write-Host "=== CLEAN: removing publish output and stale payload ===" -ForegroundColor Yellow
foreach ($dir in @("$root\publish\service", "$root\publish\useragent", "$root\publish\controlcenter", $sharedPayload)) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}
foreach ($exe in @("$payload\ari.exe", "$payload\Porta.UserAgent.exe", "$payload\Porta.ControlCenter.exe")) {
    if (Test-Path $exe) { Remove-Item $exe -Force }
}
dotnet clean "$root\PORTA.Windows.sln" -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Clean failed" }
Write-Host "Clean complete" -ForegroundColor Green

Write-Host "=== Building Porta.Agent.Service (ari.exe) ===" -ForegroundColor Cyan
dotnet publish "$root\src\Porta.Agent.Service" -c Release -r win-x64 --self-contained -o "$root\publish\service" /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw "Service build failed" }
Copy-Item "$root\publish\service\ari.exe" "$payload\ari.exe" -Force
Write-Host "ari.exe copied" -ForegroundColor Green

Write-Host "=== Building Porta.UserAgent ===" -ForegroundColor Cyan
dotnet publish "$root\src\Porta.UserAgent" -c Release -r win-x64 --self-contained -o "$root\publish\useragent" /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw "UserAgent build failed" }
Copy-Item "$root\publish\useragent\Porta.UserAgent.exe" "$payload\Porta.UserAgent.exe" -Force
Write-Host "Porta.UserAgent.exe copied" -ForegroundColor Green

Write-Host "=== Building Porta.ControlCenter ===" -ForegroundColor Cyan
dotnet publish "$root\src\Porta.ControlCenter" -c Release -r win-x64 --self-contained -o "$root\publish\controlcenter" /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw "ControlCenter build failed" }
Copy-Item "$root\publish\controlcenter\Porta.ControlCenter.exe" "$payload\Porta.ControlCenter.exe" -Force
Write-Host "Porta.ControlCenter.exe copied" -ForegroundColor Green

# GUARD: the shared payload is gone as of 1.2.126. The installer no longer harvests
# payload\SharedFiles, because these exes are self-contained single-file with native
# libraries embedded and nothing needs to sit beside them. Verified by installing 1.2.125
# with an empty payload: service Running, database ready, local API responding, widget
# tracking, Control Center signed in.
#
# That is only true while the publish stays single-file. If someone turns PublishSingleFile
# off, publish starts emitting hundreds of loose DLLs that the MSI would no longer carry,
# and the installed agent would fail to start with a missing-assembly error that points
# nowhere near this file. So assert it, loudly, rather than let it regress silently.
Write-Host "=== Checking publish output is self-contained ===" -ForegroundColor Cyan
$loose = @(Get-ChildItem "$root\publish\service" -File | Where-Object { $_.Name -ne "ari.exe" -and $_.Extension -ne ".pdb" })
if ($loose.Count -gt 0) {
    $names = ($loose | Select-Object -First 8 | ForEach-Object { $_.Name }) -join ", "
    throw "publish\service emitted $($loose.Count) loose file(s) ($names ...). The installer no longer ships a shared payload, so these would be MISSING at runtime. Either restore the shared payload mechanism or fix the publish settings. Do NOT ship this MSI."
}
Write-Host "publish\service contains only ari.exe (+pdbs) as expected" -ForegroundColor Green

# The old SharedFiles directory is not recreated. Remove a stale one so nothing lingers.
if (Test-Path $sharedPayload) { Remove-Item $sharedPayload -Recurse -Force }

# GATE: verify the native SQLite that will actually RUN, and fail closed.
#
# The first version of this gate looked for a loose payload\SharedFiles\e_sqlite3.dll and
# threw when it was absent. That assumption was wrong. These three exes are published
# self-contained single-file with IncludeNativeLibrariesForSelfExtract=true, so the native
# SQLite is embedded INSIDE each exe and the publish output contains no loose DLL at all.
# The loose copy that used to sit in SharedFiles was a fossil of an older non-single-file
# build (SQLite 3.41.2, dated 2023) that survived only because Copy-Item -Force never
# deletes what publish stopped producing.
#
# So: check inside the payload exes, which is what ships and what loads. If a loose DLL is
# present as well, check it too -- a restored fossil must not smuggle a vulnerable copy
# back in.
$minimum = [version]"3.50.2"   # CVE-2025-6965 / GHSA-2m69-gcr7-jv3q fixed here

# Scans a large binary in chunks. ReadAllBytes on a 200 MB exe would allocate the file
# twice over; the overlap keeps a version string from being split across chunk boundaries.
function Get-EmbeddedSqliteVersions {
    param([string]$Path)
    $found = New-Object System.Collections.Generic.HashSet[string]
    $chunk = 8388608
    $overlap = 32
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $buffer = New-Object byte[] $chunk
        $carry = ""
        while (($read = $stream.Read($buffer, 0, $chunk)) -gt 0) {
            $text = $carry + [System.Text.Encoding]::ASCII.GetString($buffer, 0, $read)
            foreach ($m in [regex]::Matches($text, '3\.\d{2}\.\d+')) { [void]$found.Add($m.Value) }
            if ($text.Length -ge $overlap) { $carry = $text.Substring($text.Length - $overlap) }
            else { $carry = $text }
        }
    }
    finally { $stream.Dispose() }
    return $found
}

Write-Host "=== Verifying native SQLite in the PAYLOAD ===" -ForegroundColor Cyan
# Exactly the three files the MSI ships. There is no loose DLL to check any more: the
# shared payload is gone, so the embedded copy is the only SQLite that can ever load.
$targets = @("$payload\ari.exe", "$payload\Porta.UserAgent.exe", "$payload\Porta.ControlCenter.exe")

foreach ($target in $targets) {
    if (!(Test-Path $target)) { throw "Payload file missing, cannot verify: $target" }
    $versions = Get-EmbeddedSqliteVersions -Path $target
    $name = Split-Path $target -Leaf
    if ($versions.Count -eq 0) {
        throw "No SQLite version string found in $name. Cannot prove it is patched -- do NOT ship this MSI."
    }
    # Fail closed: EVERY match must be at or past the fix. Passing on "at least one match
    # is fine" would wave through a binary carrying both a patched and a stale copy.
    $bad = $versions | Where-Object { [version]$_ -lt $minimum }
    if ($bad) {
        throw "PAYLOAD SQLITE IS VULNERABLE in ${name}: found $($bad -join ', '), need >= $minimum (CVE-2025-6965). Do NOT ship this MSI."
    }
    Write-Host "  $name -> SQLite $(($versions | Sort-Object) -join ', ') (>= $minimum) OK" -ForegroundColor Green
}
Write-Host "Payload SQLite verified against CVE-2025-6965" -ForegroundColor Green

Write-Host "=== Building MSI (ProductVersion=$version) ===" -ForegroundColor Cyan
dotnet build "$root\installer\AriAgent.Installer.wixproj" -c Release /p:ProductVersion=$version
if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }

Write-Host ""
Write-Host "=== BUILD COMPLETE ($version) ===" -ForegroundColor Green

# The MSI is written to bin\x64\Release (Platform=x64), NOT bin\Release. Every earlier
# build script globbed bin\Release and took the newest .msi, which only ever produced the
# right answer because the two paths happen to be hardlinked. If that link breaks -- a
# clean, a copy, a different WiX version -- the script would print the SHA256 of a STALE
# MSI, and that hash is the fleet's only integrity check on auto-update (requireSignature
# is still false). So: take the exact build output path, and prove it is this build.
$msiPath = "$root\installer\bin\x64\Release\AriAgentInstaller.msi"
if (!(Test-Path $msiPath)) { throw "MSI not found at the expected build output path: $msiPath" }

$msi = Get-Item $msiPath
if ($msi.LastWriteTime -lt (Get-Date).AddMinutes(-30)) {
    throw "MSI at $msiPath is older than this build ($($msi.LastWriteTime)). Refusing to report a stale hash."
}

# Read ProductVersion back out of the MSI itself. The csproj, the script variable and the
# built artifact can disagree; only the artifact matters.
$wi = New-Object -ComObject WindowsInstaller.Installer
$db = $wi.GetType().InvokeMember("OpenDatabase", "InvokeMethod", $null, $wi, @($msi.FullName, 0))
$view = $db.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $db, @("SELECT Value FROM Property WHERE Property='ProductVersion'"))
$view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null)
$rec = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)
$builtVersion = $rec.GetType().InvokeMember("StringData", "GetProperty", $null, $rec, @(1))
if ($builtVersion -ne $version) {
    throw "MSI ProductVersion is '$builtVersion' but this build is '$version'. Do NOT publish this hash."
}

Write-Host "MSI:            $($msi.FullName)" -ForegroundColor Yellow
Write-Host "ProductVersion: $builtVersion (read from the MSI, not the csproj)" -ForegroundColor Yellow
Write-Host "Size:           $('{0:N0}' -f $msi.Length) bytes" -ForegroundColor Yellow
Write-Host "SHA256:" -ForegroundColor Cyan
(Get-FileHash $msi.FullName -Algorithm SHA256).Hash
Write-Host ""
Write-Host "NOT RELEASED. Nothing is published until the manifest on Render is updated." -ForegroundColor Yellow
Write-Host "Next: run scripts\install.ps1 as Administrator to install locally and verify." -ForegroundColor Cyan
