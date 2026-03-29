# NOTES — 2026-03-28

## What this is
This is the **always-on watcher/sender** for Safety Alerts (Storm + Lightning).

Per Jim’s requirements:
- Runs as a Windows Service on `jwebserver1`
- Ticks/polls continuously (no Task Scheduler)
- Triggers push notifications directly via Expo Push API
- Does not require an API endpoint to initiate sending

## Current status
- Implemented:
  - **Storm** polling via NWS active alerts endpoint (point query)
  - **Lightning** watcher via Blitzortung MQTT subscription with rolling window + threshold
- Episode logic: start + all-clear (storm + lightning)
- Targeting logic:
  - Storm: `Enabled=1` and `StormEnabled=1`
  - Lightning: `Enabled=1` and `LightningEnabled=1`
  - If `GpsOnlyEnabled=1`, requires `SafetyGeofenceStates.InGeofence=1` with fresh timestamp
- Delivery tracking:
  - Inserts rows into `SafetyAlertDeliveries`
  - Marks `sent` or `failed` based on Expo ticket response
  - Receipt polling cleans up dead tokens (DeviceNotRegistered → deactivate)

## Installed on jwebserver1
- `C:\Services\MorseSafetyAlerts`
- Service name: `MorseSafetyAlerts`
- Startup fix: service uses `ContentRootPath = AppContext.BaseDirectory` so it finds `appsettings.json`.

## Next work
- Lightning watcher (relay source + threshold + all-clear)
- Receipt processing:
  - call Expo receipts endpoint and deactivate tokens when DeviceNotRegistered
- Add rate limiting/backoff if NWS errors
