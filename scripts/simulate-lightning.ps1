param(
  [int]$Count = 2,
  [string]$Root = 'C:\Services\MorseSafetyAlerts'
)

$ErrorActionPreference = 'Stop'

$stateDir = Join-Path $Root 'state'
if (!(Test-Path $stateDir)) { New-Item -ItemType Directory -Force -Path $stateDir | Out-Null }

$cmdPath = Join-Path $stateDir 'commands.json'

$cmd = @{
  SimulateLightningStrikes = $Count
  ClearLightningWindow = $false
}

$cmd | ConvertTo-Json | Out-File -FilePath $cmdPath -Encoding utf8
Write-Host "Wrote $cmdPath"
