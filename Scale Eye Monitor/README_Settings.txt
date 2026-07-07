Scale Eye Monitor - Settings Guide
=================================

Where settings are
------------------
- Settings UI: Tray icon -> Settings...
- Settings file (JSON): %APPDATA%\<App Name>\settings.json
- Logs: %APPDATA%\<App Name>\logs\log_yyyyMMdd.txt
- General readme: README_Program.txt (Tray icon -> Readme)
- This guide: README_Settings.txt (Settings -> Settings info)

Important notes
---------------
- The eye setting is a full SOAP endpoint URL. It is not auto-corrected or rewritten.
- The weight IP field requires a strict IPv4 address (no DNS names).
- Most numeric settings are clamped to safe limits on load/save.
- The top-line Eye Status drives the tray icon and notifications.
  The Detail line is informational only.
- Tray tooltip text is capped at 63 characters (Windows NotifyIcon limit).
- Notifications (when enabled) fire only on Eye Status headline transitions
  (OK / Blocked / Alignment off / Disconnected). The duration follows the
  Notification Duration setting (Short ≈ 7s, Long ≈ 25s).
- The Detail line is informational only. It is truncated to about 300 characters
  for hygiene and may be visually ellipsized if the window is narrow.

How polling works (high level)
------------------------------
Each cycle the app:

1) (If Weight Mode is enabled) reads the latest weight snapshot and updates Scale Status:
   - In motion | Zero | Stable | Unavailable | '—'

2) Chooses poll timing from one branch (poll interval + confirm delay):
   - Eyes-only or weight unavailable -> PollSeconds + EyeConfirmDelaySeconds
   - Weight in motion -> checks weight every 1 second; no eye network calls
   - Weight StableNonZero during "fast window" -> PollSecondsStableNonZero + WeightEyeConfirmDelayFastSeconds
   - Weight StableZero or normal StableNonZero -> WeightPollSeconds + WeightEyeConfirmDelaySeconds

3) Eye Check #1:
   - If Weight Mode is usable AND weight is Zero: runs a short burst of N eye queries (Eye #1 only)
   - Otherwise: one eye query (with retry/failure policy)

4) If Eye #1 looks blocked, waits the confirm delay and runs Eye Check #2.
   If motion begins during the confirm delay, the confirm is aborted.

5) If Eye #2 confirms a TRUE/blocked eye condition:
    - Blocked only if weight is Stable (stable, non-zero) at commit time
    - Alignment off when stable at Zero
    - Obstructed when running eyes-only or when weight is unavailable

What "0" means
--------------
In the Settings UI, most fields can't be set to 0. If you manually edit settings.json:

- For most "seconds" and "counts" fields:
  0 -> clamped to 1 (not disabled)

- The exception:
  StableNonZeroFastWindowSeconds = 0 -> disables the "fast window" feature


EYE / GENERAL SETTINGS
----------------------

Eye endpoint URL (JSON: EyeUrl)
- Exact SOAP endpoint URL for the Kahler eye controller, for example:
    http://192.168.1.50/Service.asmx
- Required for normal operation.

Input ID (JSON: InputId)
- Which eye input to query.
- Range: 0-3.

Poll interval (seconds) (JSON: PollSeconds)
- Base polling interval in Eyes-only mode.
- Also used as the fallback timing if Weight Mode is enabled but weight is unavailable.
- Range: 1-120.

Confirm delay (seconds) (JSON: EyeConfirmDelaySeconds)
- Time between Eye check #1 and Eye check #2 (confirm) when not using weight timing.
- Range: 1-120.

Failure retry delay (seconds) (JSON: FailureRetryDelaySeconds)
- Wait time between eye retry attempts when a poll fails.
- Range: 1-30.

Failure retry count (JSON: FailureRetryCount)
- Number of failed attempts (per poll) before the app forces Disconnected.
- Range: 1-10.
- Note: Some failure types are treated as "offline/unreachable" and will switch to Disconnected immediately,
  without waiting for multiple retries.


START WITH WINDOWS (NOT IN settings.json)
-----------------------------------------
Start with Windows (stored in HKCU\...\Run)
- Adds/removes a per-user startup entry in the registry.
- Whether the window starts hidden is controlled by Start in tray (tray menu option).


NOTIFICATIONS
-------------
Notification Duration (JSON: NotificationDuration + NotificationsEnabled)
- Controls how long notifications are shown.
- Options:
  - Short: short duration (≈ 7 seconds)
  - Long: long duration (≈ 25 seconds)
  - Disabled: turns notifications off (same as unchecking "Enable notifications" in the tray menu)
- Default (clean install/no settings file): Short.

Enable notifications (tray menu) (JSON: NotificationsEnabled)
- Master on/off switch for notifications.
- If Notification Duration is set to Disabled, this will be off.
- If you re-enable notifications from the tray menu, the app restores the last Short/Long duration
  and updates the Settings dropdown to match.


WEIGHT MODE SETTINGS
--------------------

Enable Weight Mode (JSON: WeightModeEnabled)
- When enabled, the app connects to a newline-delimited ASCII weight stream over TCP.
- While weight is In motion, eye polling is paused and the app checks weight quickly (1 second).
- Default: false.

Weight IP Address (JSON: WeightIp)
Weight Port (JSON: WeightPort)
- Endpoint for the weight stream.
- Port range: 1-65535. (Default 4662)
- If Weight Mode is enabled but the Weight IP/Port is missing or invalid, the weight feed is treated as unavailable
  and the app falls back to eyes-only timing until the endpoint is corrected.

Weight poll interval (seconds) (JSON: WeightPollSeconds)
- Default poll interval while weight is usable (Zero or normal Stable).
- Range: 1-120.

Weight confirm delay (seconds) (JSON: WeightEyeConfirmDelaySeconds)
- Confirm delay used while weight is usable (normal weight-mode timing).
- Range: 1-120.

Stable (non-zero) fast poll interval (seconds) (JSON: PollSecondsStableNonZero)
- Faster poll interval used only when a truck FIRST reaches Stable and the "fast window" is active.
- Range: 1-60.

Stable fast window (seconds) (JSON: StableNonZeroFastWindowSeconds)
- Limits how long the app stays in the fast Stable poll rate after entering Stable.
- After the window expires, polling reverts to WeightPollSeconds until the next motion event.
- Range: 0-300.
- Special: 0 disables the fast-window feature.

Stable fast confirm delay (seconds) (JSON: WeightEyeConfirmDelayFastSeconds)
- Confirm delay used during the Stable fast window.
- Range: 1-120.


Burst sampling (used ONLY when Scale Status is "Zero")
------------------------------------------------------
Burst count (JSON: WeightBurstCount)
Burst delay (ms) (JSON: WeightBurstDelayMs)
Min TRUE successes (JSON: WeightBurstMinTrueSuccess)

When weight is stable at Zero, Eye Check #1 uses a burst to reduce false "blocked" events:
- If ANY successful result is FALSE -> the app returns OK immediately.
- TRUE is treated as a "TRUE-candidate" only if at least "Min TRUE successes" samples succeed
  and none of the successful samples are FALSE.
- If the burst has too few successes (network flicker), it is treated as "inconclusive" and the app
  does not change the headline state.

Ranges:
- Burst count: 1-20
- Burst delay: 100-3000 ms
- Min TRUE successes: 1-20 (automatically capped so it never exceeds Burst count)


Weight stability tuning
-----------------------
Stability band (+/-) (JSON: WeightStableBand)
- How much the weight is allowed to swing and still be considered stable.
- Range: 1-500.

Zero band (abs) (JSON: WeightZeroBand)
- If a stable weight is near zero, it is treated as Zero (empty scale).
- Rule: abs(weight) < WeightZeroBand.
- Range: 1-500.

Stability window (seconds) (JSON: WeightWindowSeconds)
- Rolling time window used to decide stable vs in motion (min/max range over this window).
- Range: 1-30.

Stale timeout (seconds) (JSON: WeightStaleSeconds)
- If no acceptable weight update arrives within this time while the eyes are connected, weight becomes unavailable and Scale Status shows "Unavailable".
- Range: 1-30.

Practical tuning tips:
- If weight flips between Zero and Stable near the boundary, increase Zero band slightly.
- If weight is In motion too often due to normal noise, increase Stability band or Stability window slightly.
- If weight feed is bursty, set Stale timeout a bit larger than Stability window so brief gaps don't disable Weight Mode.


DEBUG / DIAGNOSTICS
-------------------
Debug logging (JSON: DebugLogging)
- OFF: logs meaningful changes (state transitions/UI changes).
- ON: logs additional detail and enables the "IsInputOn Poll Test" tools in Settings.
- Default: false.

Burst test count (JSON: BurstTestCount)
Burst test delay (ms) (JSON: BurstTestDelayMs)
- Used ONLY by the "IsInputOn Poll Test" button in Settings (diagnostic).
- Count range: 1-100
- Delay range: 100-3000 ms


TRAY OPTIONS (not in Settings window)
-------------------------------------
Start in tray (JSON: StartInTray)
- Tray menu option. When enabled, the app starts with the main window hidden (tray only).
- Saved to settings.json immediately when toggled.

Enable notifications (JSON: NotificationsEnabled)
- Tray menu option. Master on/off for notifications.
- Kept in sync with Notification Duration:
  - Setting Notification Duration to Disabled turns this off.
  - Re-enabling from the tray restores the last Short/Long duration.

Always on top (JSON: AlwaysOnTop)
- Tray menu option. Keeps the status window above other windows (TopMost).
- Saved to settings.json immediately when toggled.


End of Settings Guide
---------------------
