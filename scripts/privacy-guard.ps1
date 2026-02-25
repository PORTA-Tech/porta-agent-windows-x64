param(
  [Parameter(Mandatory = $true)]
  [string]$RepoRoot
)

$ErrorActionPreference = "Stop"

$targets = @(
  "src/Porta.Agent.Runtime",
  "src/Porta.Agent.Service"
)

$banned = @(
  @{ Name = "clipboard"; Pattern = "\bSystem\.Windows\.Forms\.Clipboard\b|\bClipboard\." },
  @{ Name = "screen_capture"; Pattern = "\bCopyFromScreen\b|\bBitBlt\b|\bPrintWindow\b" },
  @{ Name = "file_content_watch"; Pattern = "\bFileSystemWatcher\b" },
  @{ Name = "packet_inspection"; Pattern = "\bSharpPcap\b|\bPacketDotNet\b|\bWinPcap\b|\blibpcap\b" }
)

$violations = @()

foreach ($relative in $targets) {
  $root = Join-Path $RepoRoot $relative
  if (-not (Test-Path $root)) { continue }
  $files = Get-ChildItem -Path $root -Recurse -File -Include *.cs
  foreach ($file in $files) {
    $content = Get-Content -Path $file.FullName -Raw
    foreach ($rule in $banned) {
      if ($content -match $rule.Pattern) {
        $violations += "$($file.FullName): disallowed_api=$($rule.Name)"
      }
    }
  }
}

if ($violations.Count -gt 0) {
  Write-Error ("Privacy guard failed:`n" + ($violations -join "`n"))
}

Write-Host "Privacy guard passed."
