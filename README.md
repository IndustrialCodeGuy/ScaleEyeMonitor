# Scale Eye Monitor (`ScaleEyeMonitor`)

Scale Eye Monitor is a Windows tray utility for monitoring a Kahler KA-2000 style photoelectric “eye” input through SOAP/HTTP `IsInputOn` calls.

It provides a small status window, tray icon, tooltip details, optional notifications, logging, and optional integration with a TCP scale-weight stream.

## What it is for

Scale Eye Monitor is primarily an alignment and beam-interruption monitor for a scale eye sensor.

It can help identify these common conditions:

- Eye input is clear / OK
- Eye input appears blocked or obstructed
- Eye alignment appears off while the scale is empty
- Eye controller is disconnected or unreachable
- Optional weight stream is unavailable, stable, zero, or in motion

When Weight Mode is enabled, the app can use the weight stream to make better decisions about when an eye condition should be treated as `Blocked`, `Obstructed`, or `Alignment off`.

## What it is not

This app is not a safety system, certified interlock, legal-for-trade weighing component, or replacement for operator judgment.

The eye input alone cannot reliably prove that a truck is on the scale. Wind, debris, timing, partial beam breaks, or a vehicle entering/leaving can all create short TRUE conditions. Weight Mode is the intended path when the app needs the best available “truck present” context.

## Main features

- Windows tray app with small status window
- Tray icon and tooltip status
- Optional Windows notifications on headline status changes
- Eyes-only mode using SOAP/HTTP `IsInputOn`
- Optional Weight Mode using a newline-delimited TCP weight stream
- Motion gating: pauses eye network checks while the scale is in motion
- Stable-zero burst sampling to reduce false blocked/alignment events
- Confirm-delay logic before committing a TRUE eye condition
- Disconnected/offline handling with retry policy and cleaner user-facing messages
- AppData-based settings and logs
- Optional start-with-Windows and start-in-tray behavior
- Per-monitor DPI-aware WinForms UI

## Repository layout

```text
.
├─ README.md
├─ LICENSE.md
└─ Scale_Eye_Monitor/
   ├─ Scale Eye Monitor.csproj
   ├─ Program.cs
   ├─ MainForm*.cs
   ├─ SettingsForm.cs
   ├─ AppSettings.cs
   ├─ README_Program.txt
   ├─ README_Settings.txt
   └─ icons / image assets
```

The root `README.md` is the public overview. The detailed operator and settings documentation is kept with the application files:

- `Scale_Eye_Monitor/README_Program.txt`
- `Scale_Eye_Monitor/README_Settings.txt`

Those files are copied to the publish output and are intended to sit next to the executable.

## Requirements

### Runtime

- Windows 10 or newer
- Kahler KA-2000 style SOAP endpoint, or compatible endpoint exposing `IsInputOn`
- Optional TCP weight stream for Weight Mode

### Build

- .NET 8 SDK
- Windows development environment capable of building WinForms projects

The project targets:

```text
net8.0-windows10.0.19041.0
```

## Building

From the repository root:

```powershell
cd .\Scale_Eye_Monitor
dotnet build "Scale Eye Monitor.csproj" -c Release
```

## Publishing

Framework-dependent publish:

```powershell
cd .\Scale_Eye_Monitor
dotnet publish "Scale Eye Monitor.csproj" -c Release -r win-x64 --self-contained false
```

Self-contained publish:

```powershell
cd .\Scale_Eye_Monitor
dotnet publish "Scale Eye Monitor.csproj" -c Release -r win-x64 --self-contained true
```

The app expects its readme and icon/image assets to be available in the publish output. Avoid trimming or aggressive single-file settings unless they have been tested with the tray icons, toast assets, and bundled readme files.

## First run

On first run, open Settings and configure at least:

- Eye endpoint URL (`EyeUrl`)
- Input ID (`InputId`)

Example eye endpoint:

```text
http://192.168.1.50/Service.asmx
```

For Weight Mode, also configure:

- Weight IP address (`WeightIp`)
- Weight TCP port (`WeightPort`, default `4662`)

## Operating modes

### Eyes-only mode

Weight Mode disabled.

The app polls the eye endpoint directly. If the first eye check returns TRUE, the app waits the configured confirm delay and checks again before changing the headline status.

In eyes-only mode, a confirmed TRUE condition is shown as `Obstructed`, because the app does not have weight context to prove whether a vehicle is fully on the scale.

### Weight Mode

Weight Mode enabled.

The app reads a TCP weight stream and classifies the scale as unavailable, in motion, stable at zero, or stable non-zero. That weight context affects eye polling and status decisions.

At a high level:

- In motion: eye network checks are paused
- Stable zero: eye check #1 may use burst sampling
- Stable non-zero: confirmed TRUE eye conditions can be shown as `Blocked`
- Stable zero with confirmed TRUE can be shown as `Alignment off`
- Weight unavailable falls back to eye-only style behavior and timing

## Status model

The top-line eye status is the headline state. It drives the tray icon and notifications.

Common eye statuses:

- `OK`
- `Blocked`
- `Obstructed`
- `Alignment off`
- `Disconnected`

The detail line is informational. It can change without changing the tray icon or firing a notification.

Scale status is shown separately when Weight Mode is enabled:

- `Zero`
- `Stable`
- `In motion`
- `Unavailable`
- `—`

## Files and folders

Settings and logs are stored under the user’s AppData folder:

```text
%APPDATA%\<App Name>\settings.json
%APPDATA%\<App Name>\logs\log_yyyyMMdd.txt
```

The exact folder name follows the app/product name used by the executable.

## Startup behavior

The app can optionally:

- Start with Windows using a per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry
- Start hidden in the tray
- Keep the main status window always on top

These options are controlled from the tray menu and Settings UI.

## Troubleshooting

### Eye status is `Disconnected`

Check the eye endpoint URL, controller power, network path, firewall/routing, and whether the SOAP endpoint is reachable from the PC running the app.

### Frequent `Obstructed` or `Alignment off`

Review the confirm delay and the physical eye alignment. In eyes-only mode, the confirm delay should be long enough to avoid normal vehicle entry/exit timing being mistaken for a persistent blocked condition.

### Weight status is `Unavailable`

Check the weight IP/port, verify that the TCP stream is reachable, and confirm that the device is sending parseable newline-delimited weight values often enough to avoid stale handling.

### Weight status stays `In motion`

Review the weight stability settings. A naturally noisy stream may need a larger stability band or a longer stability window.

## Documentation

For full behavior details, see:

- `Scale_Eye_Monitor/README_Program.txt`

For setting-by-setting guidance, see:

- `Scale_Eye_Monitor/README_Settings.txt`

## License

Copyright © 2026 Dan Michel.

This project is licensed under the PolyForm Noncommercial License 1.0.0. You may use, study, modify, and share this software for noncommercial purposes, subject to the license terms. Commercial or for-profit use requires separate written permission from the copyright holder.

Because commercial use is restricted, this project is source-available rather than OSI open source. See `LICENSE.md` for the full license text.
Third-party assets may be subject to their own licenses. The application (EXE) icon uses or is derived from a Flaticon icon by Freepik and is attributed in `THIRD_PARTY_NOTICES.md`.
