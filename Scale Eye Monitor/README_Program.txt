Scale Eye Monitor (ScaleEyeMonitor)
===================================

Purpose
-------
Scale Eye Monitor watches a Kahler KA-2000 "eye" input (SOAP/HTTP IsInputOn) and shows:
- A small status window
- A tray icon (Connected vs Disconnected)
- Optional notifications on state changes

It is primarily an alignment monitor for the eye sensor. The main "Eye Status" shown is:
- OK
- Blocked
- Obstructed
- Alignment off
- Disconnected

"Obstructed" is used when a TRUE eye condition is confirmed without usable weight context
(eyes-only mode, or Weight Mode enabled but unavailable). Internally this uses the same
AlignmentOff state, but the user-facing text avoids implying a sensor alignment problem
when the app cannot prove vehicle/weight context.

Two operating modes are supported:
1) Eyes-only (no weight integration)
2) Weight Mode (uses a scale weight TCP stream to gate eye behavior and adjust polling)


Important Reality Check
-----------------------
This app is primarily an "alignment monitor" for the eye sensor.
Even with clever logic, the eye input alone cannot reliably prove "truck on scale":
- Wind, debris, partial beam breaks, and timing variance can cause short TRUE spikes.
- Faster polling reduces polling-induced randomness, but cannot eliminate real-world variability.

If you need "truck present" truth, the weight stream is the best available signal.


Quick Start
----------
1) Run the app (you should see a tray icon).
2) Right-click the tray icon -> Settings...
3) Enter the Eye endpoint URL and InputId (0-3)
4) Click OK.

If you have a weight feed, enable Weight Mode in Settings and enter:
- Weight IP Address
- Weight Port (default 4662)


Files and Folders
-----------------
- Settings file:
  %APPDATA%\<App Name>\settings.json
  (Exact folder name uses the app's Product Name. It is typically the exe name without extension.)

- Logs folder:
  %APPDATA%\<App Name>\logs\
  (Log files are named like: log_yyyyMMdd.txt)

- Readme files (recommended next to the exe):
  <Application folder>\README_Program.txt
  <Application folder>\README_Settings.txt

Notes:
- The tray icons are loaded from the application folder (next to the exe):
  ScaleEye_Connected32.ico and ScaleEye_Disconnected32.ico


Tray Icon / UI
--------------
- Left-click tray icon: opens the status window
- Right-click tray icon menu:
  - Settings...
  - Readme
  - Check Now
  - Open Logs Folder
  - Start with Windows
  - Start in tray
  - Always on top
  - Exit

- Closing the window with the X hides it to the tray (unless Exit is chosen).

Window fields:
- Eye Status: OK / Blocked / Obstructed / Alignment off / Disconnected
- Scale Status:
  - Zero        (stable, near zero)
  - Stable      (stable, non-zero)
  - In motion   (weight changing)
  - Unavailable (Weight Mode enabled, eyes connected, but weight feed unavailable)
  - --          (startup/loading, eyes disconnected, or Weight Mode disabled/hidden)
- Detail: extra info (does not change the main Eye Status)
- Last Poll: the last time the app actually checked the eye
- HTTP Code: the last HTTP result from the eye device
  (During "In motion", the HTTP Code is kept at the last known value.)

Startup toggles (tray menu):
- Start with Windows uses a per-user HKCU Run key.
- Start in tray controls whether the window starts hidden (tray only).
- Always on top applies to the main status window only.


Settings Overview
-----------------
General / Eye Settings:
- EyeUrl:
  Full SOAP endpoint URL for the KA-2000 eye device, such as:
  http://192.168.1.50/Service.asmx

- InputId:
  Which eye input to query (0..3)

- PollSeconds:
  Base periodic poll interval (seconds)

- EyeConfirmDelaySeconds:
  When Eye check #1 returns TRUE, wait this long before confirming with Eye check #2

Failure Policy (Eyes):
- FailureRetryCount:
  How many failed attempts in a row before forcing "Disconnected"

- FailureRetryDelaySeconds:
  Delay between retries (seconds)

Weight Mode Settings (when enabled):
- WeightIp / WeightPort (default 4662)
- WeightPollSeconds: weight-mode polling interval when weight is usable
- WeightEyeConfirmDelaySeconds: confirm delay used when weight is usable
- PollSecondsStableNonZero: faster poll interval during the StableNonZero "fast window"
- WeightEyeConfirmDelayFastSeconds: confirm delay during StableNonZero fast window
- WeightStableBand / WeightZeroBand / WeightWindowSeconds / WeightStaleSeconds:
  weight stability and stale rules

(See README_Settings.txt for the full list with plain-language guidance.)


Eyes-Only Mode (Weight Mode DISABLED)
-------------------------------------
Definition:
- WeightModeEnabled = false
- The app uses only IsInputOn polling + confirm delay.

Behavior:
1) Every PollSeconds:
   - Eye Check #1 runs (with failure policy)
2) If Eye1 == FALSE:
   - State becomes OK (Connected icon)
3) If Eye1 == TRUE:
   - Wait EyeConfirmDelaySeconds
   - Eye Check #2 runs
   - If Eye2 == TRUE: Eye Status becomes Obstructed
   - If Eye2 == FALSE: ignore Eye1 spike and remain OK

Best-Practice Timing Rule (for alignment monitoring):
- EyeConfirmDelaySeconds should be:
    "worst-case truck entry time" + small buffer
  Because the goal is to avoid confirming transient TRUE spikes.

Notes:
- Faster polling helps reduce randomness introduced by polling cadence, but it does not turn eyes
  into a true "truck present" sensor.


Weight Mode (Weight Mode ENABLED)
--------------------------------
Definition:
- WeightModeEnabled = true
- A background TCP reader connects to WeightIp:WeightPort (default 4662)
- It classifies the live weight stream into modes that gate eye behavior

Weight Stream Sampling
----------------------
- The TCP loop reads lines continuously.
- It throttles accepted parsed samples to about 2 samples per second (roughly every 500ms).
- Parsing extracts the first digit-run of length >= 2 (supports a leading '-' if present).

Stability Window (Rolling)
--------------------------
Using WeightWindowSeconds (default 2 seconds):
- Keep samples in a rolling time window
- Compute min/max across that window
- range = max - min

Stability decision:
- stable if range <= (WeightStableBand * 2)
  (StableBand is +/- around the weight; total swing allowed is 2x band)

Zero decision:
- "zero" if abs(weight) < WeightZeroBand

Weight Modes
------------
- Unavailable:
  Not connected, error, stopped, or stale (no updates within WeightStaleSeconds)

- Unstable:
  Weight is changing more than the stability band allows

- StableZero:
  Stable and near zero

- StableNonZero:
  Stable and not near zero (typically "truck present")

Stale Handling
--------------
If no accepted weight updates arrive within WeightStaleSeconds (default 3s),
the snapshot is treated as Unavailable ("stale"), even if the last mode was stable.


How Weight Mode Affects Eye Polling
-----------------------------------
A) Dynamic polling interval:
- Eyes-only (or weight unavailable): PollSeconds
- When weight is usable (StableZero / StableNonZero): WeightPollSeconds
- When weight is StableNonZero during the "fast window": PollSecondsStableNonZero
  (The fast window is limited by StableNonZeroFastWindowSeconds to avoid fast polling all night.)

B) Motion gating (most important):
- If weight mode == Unstable ("In motion"):
  - The app pauses eye polling (no eye network calls during motion).
  - No confirm logic runs during motion.
  - UI indicates motion and keeps HTTP Code at the last known value.

C) Confirm protection:
When Eye1 == TRUE and we are waiting the confirm delay:
- If weight becomes Unstable during the confirm delay:
  - Confirm is aborted (eyes unreliable while moving)

D) Labeling:
If the eye is confirmed TRUE while:
- Weight Mode is disabled, or weight is unavailable:
  Eye Status shows "Obstructed".
- Weight is StableZero:
  Eye Status shows "Alignment off" and this alignment-off state is latched until a confirmed FALSE clears it.
- Weight is StableNonZero:
  Eye Status shows "Blocked" only if Alignment off was not already latched at StableZero.
  If Alignment off was already latched, the app keeps "Alignment off" until a confirmed FALSE clears it.

Blocked msg min weight affects the Detail guidance line, not the headline state:
- At/above the configured minimum, Blocked detail says to confirm the vehicle is fully on the scale.
- Below the configured minimum, Blocked detail uses the alignment/obstruction guidance.

(Scale Status is shown separately as: Zero / Stable / In motion / Unavailable / --. When the eyes are forced disconnected,
weight monitoring is stopped and Scale Status is cleared while the app waits for eye recovery.)


Lifecycle Notes (Eyes vs Weight)
--------------------------------
- Weight monitor starts when WeightModeEnabled is true and endpoint is valid.
- If the eye device is forced "offline/unreachable", the app stops the weight monitor
  and treats weight as unavailable ("eyes offline") until eyes recover.


Weight Stream Notifications
--------------------------
- If the weight stream is unreachable at startup: one warning notification
- If weight stream disconnects after being connected: one warning notification
- If it later restores after an outage: one info notification

(After settings changes, weight notifications may be muted briefly to avoid noise.)


Debug Logging / Burst Test
--------------------------
- Debug logging enables verbose output and exposes a poll/burst diagnostics section in Settings.
- Burst test repeatedly calls IsInputOn for diagnostics; while it runs:
  - OK/Cancel are disabled
  - Burst button becomes Cancel


Troubleshooting
---------------
- "DNS Error: ..." / "TCP Error: ..." / "HTTP Error: ...":
  The eye device is unreachable or is returning an unusable response. Check the IP, wiring, device power, and network path.

- Frequent "Obstructed" in Eyes-only mode:
  Increase EyeConfirmDelaySeconds to worst-case truck entry time + buffer.

- Weight mode always Unavailable:
  Check WeightIp/WeightPort, firewall/routing, and that the weight stream sends data.
  Ensure the stream sends parseable numeric weights regularly.

- Weight mode stuck Unstable (In motion):
  Increase WeightStableBand or WeightWindowSeconds if the live signal naturally jitters.

- Weight mode marked stale:
  Increase WeightStaleSeconds if the stream is slow/quiet; otherwise investigate dropouts.


End of README
-------------
