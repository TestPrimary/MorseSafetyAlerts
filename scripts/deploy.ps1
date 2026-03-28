param(
  [string]$Deploy = 'C:\inetpub\wwwroot\_deploy\MorseSafetyAlerts',
  [string]$Root = 'C:\Services\MorseSafetyAlerts',
  [string]$ServiceName = 'MorseSafetyAlerts'
)

$ErrorActionPreference = 'Stop'

Write-Host "Stopping service $ServiceName..."
Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

if (!(Test-Path $Root)) { New-Item -ItemType Directory -Force -Path $Root | Out-Null }

Write-Host "Copying files $Deploy -> $Root..."
robocopy $Deploy $Root /MIR /R:2 /W:2 | Out-Host
$rc=$LASTEXITCODE
Write-Host "robocopy exit=$rc"
if ($rc -ge 8) { throw "robocopy failed ($rc)" }

Write-Host "Starting service $ServiceName..."
Start-Service -Name $ServiceName

sc.exe query $ServiceName | Out-Host

Write-Host "Status file (if present):"
Get-Content (Join-Path $Root 'state\status.json') -ErrorAction SilentlyContinue | Out-Host
