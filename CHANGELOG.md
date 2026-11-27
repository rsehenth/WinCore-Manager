# Changelog

## 0.2.0-rc.4 - 2026-11-27 (pre-release)

- Redesigned interface across every page and window, with dark, light and high contrast themes,
  vector icons and decorative art. The light theme remains the default.
- New goat logo as the application icon.
- Dashboard with live CPU, memory, GPU and system drive gauges, compact drive health, recent activity
  and quick links. The gauges reuse the Sensors monitoring session and pause when the window is inactive.
- Intel CPU Package temperature with optional per-core readings through a separately installed PawnIO
  driver, SATA SSD temperature and endurance estimates, and clearer unavailable states.
- The default window is larger (1280 x 820).
- Advanced Space Analysis with a configuration dialog, live file and folder previews, quick discovery,
  SHA-256 duplicate review, sortable rankings and a navigable space map.
- Sensors in the side navigation with drive activity, shared NVMe SMART readings and WDDM utilization
  where available. My Computer retains the hardware inventory and links to Sensors.
- App Center navigation label in both supported languages.
- Smaller self-contained portable ZIP with trimmed standalone update and recovery helpers.

## 0.1.0 - 2026-11-26 (pre-release)

First public version.

- Dashboard, Updates, Maintenance, Uninstaller, Startup, Cleanup, Tweaks, My Computer, Essentials, History, Settings and About.
- Application updates through WinGet with supervision, hidden updates and supervised recovery of interrupted operations.
- Leftover scan with backup before removal.
- Light and Dark themes; English (US) and Portuguese (Brazil).
- Portable, self-contained Windows x64 package with an independent Recovery assistant.
- Local operation history stored in SQLite.

In-app self-update is not enabled in this version.
