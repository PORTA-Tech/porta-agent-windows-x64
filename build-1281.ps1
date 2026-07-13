$ErrorActionPreference = "Stop"
$root = "C:\Users\hp\porta-agent-windows-x64"
$payload = "$root\installer\payload"
$sharedPayload = "$root\installer\payload\SharedFiles"
$version = "1.2.111"

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

Write-Host "=== Copying shared files ===" -ForegroundColor Cyan
if (!(Test-Path $sharedPayload)) { New-Item -ItemType Directory -Path $sharedPayload | Out-Null }
Get-ChildItem "$root\publish\service" -Exclude "ari.exe","*.pdb" | Copy-Item -Destination $sharedPayload -Force

Write-Host "=== Building MSI (ProductVersion=$version) ===" -ForegroundColor Cyan
dotnet build "$root\installer\AriAgent.Installer.wixproj" -c Release /p:ProductVersion=$version
if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }

Write-Host ""
Write-Host "=== BUILD COMPLETE ($version) ===" -ForegroundColor Green
$msi = Get-ChildItem "$root\installer\bin\Release\*.msi" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "MSI: $($msi.FullName)" -ForegroundColor Yellow
Write-Host "SHA256:" -ForegroundColor Cyan
(Get-FileHash $msi.FullName -Algorithm SHA256).Hash
Write-Host ""
Write-Host "Next: run scripts\install.ps1 as Administrator to install locally and verify." -ForegroundColor Cyan
