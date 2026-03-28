param(
  [string]$Root = 'C:\Services\MorseSafetyAlerts',
  [string]$ServiceName = 'MorseSafetyAlerts'
)

$ErrorActionPreference = 'Stop'

$exe = Join-Path $Root 'MorseSafetyAlerts.exe'
if (!(Test-Path $exe)) { throw "Missing $exe" }

$binArg = '"' + $exe + '"'

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
  Write-Host "Service exists; updating config..."
  if ($existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force; Start-Sleep -Seconds 2 }
  sc.exe config $ServiceName binPath= $binArg start= auto | Out-Host
} else {
  Write-Host "Creating service..."
  sc.exe create $ServiceName binPath= $binArg start= auto obj= LocalSystem DisplayName= 'Morse Safety Alerts' | Out-Host
}

sc.exe description $ServiceName 'Morse storm/lightning safety alert watcher + Expo push sender' | Out-Host
sc.exe failure $ServiceName reset= 60 actions= restart/5000/restart/5000/restart/5000 | Out-Host

Start-Service -Name $ServiceName
sc.exe query $ServiceName | Out-Host
