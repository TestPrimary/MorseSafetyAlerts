# MorseSafetyAlerts

Windows Service that runs on **jwebserver1** and periodically:

- Polls NWS active alerts near Morse Reservoir
- Subscribes to Blitzortung MQTT strike feed (geohash-tiled topics) and maintains a rolling lightning strike window
- Creates/ends safety alert episodes in SQL
- Sends push notifications via **Expo Push API** to devices registered in the Morse Indiana API DB

## Where it runs
- Server: `jwebserver1` (192.168.100.20)
- Install path: `C:\Services\MorseSafetyAlerts`
- Service name: `MorseSafetyAlerts`
- Runs as: `LocalSystem`

## Configuration
Edit `appsettings.json` on the server (do not commit secrets):

- `ConnectionStrings:MorseIndiana` (SQL connection string)
- `Alerts:TickSeconds`
- `Alerts:Nws:*`
- `Alerts:Lightning:*` (MQTT host/port/topics, radius, window, trigger)

### Logging
- Console + Windows Event Log (existing)
- Rolling log files (Serilog) under:
  - `C:\Services\MorseSafetyAlerts\logs\` (when installed to the default path)

Controlled by `Serilog:*` in `appsettings.json`:
- `Serilog:MinimumLevel:*`
- `Serilog:WriteTo[0]:Args:path` (relative paths are resolved against the executable directory)
- `Serilog:WriteTo[0]:Args:rollingInterval`

## DB dependencies
Uses tables created by MORSEINDIANAAPI:
- `SafetyPushTokens`
- `SafetyAlertSettings`
- `SafetyGeofenceStates`
- `SafetyAlertEpisodes`
- `SafetyAlertDeliveries`

## Build
```bash
dotnet build -c Release
```

## Publish (win-x64 self-contained)
```bash
dotnet publish -c Release -r win-x64 --self-contained true -o publish_out
```

## Deploy (manual)
1) Copy published files to `C:\Services\MorseSafetyAlerts`
2) Ensure `appsettings.json` exists there with the real SQL connection string
3) Restart the service

## Testing (no API): command file triggers
The service checks for a file at:
- `C:\Services\MorseSafetyAlerts\state\commands.json`

If present, it will execute the command once and archive it to `commands.last.json`.

Examples:
- Simulate lightning strikes:
  - run `scripts\simulate-lightning.ps1 -Count 2`
- Clear lightning window:
  - run `scripts\clear-lightning.ps1`

```bat
sc stop MorseSafetyAlerts
sc start MorseSafetyAlerts
sc query MorseSafetyAlerts
```
