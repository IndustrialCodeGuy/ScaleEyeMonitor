# Scale Eye Monitor (`ScaleEyeMonitor`)

Scale Eye Monitor watches a **Kahler “eye” input** via **SOAP/HTTP (`IsInputOn`)** and presents a unified status via:

- Small status window (labels)
- Tray icon + tooltip
- Optional toast notifications (**headline transitions only**)

It supports two operating modes:

1. **Eyes-only** (no weight integration)
2. **Weight Mode** (uses a TCP weight stream to gate eye behavior and adjust polling)

---

## What this app is (and isn’t)

This app is primarily an **alignment / beam-interruption monitor** for the eye sensor.

Even with confirm logic, the eye input alone cannot reliably prove “truck on scale”:
- wind / debris / partial beam breaks can cause short TRUE spikes
- polling cadence reduces sampling randomness, but can’t eliminate real-world variability

If you need the best “truck present” signal available here, **Weight Mode** is the intended path.

---

## UI and tray behavior

### Window
- Shows:
  - **Status** (headline)
  - **Detail** (informational)
  - **Last Poll**
  - **HTTP Code**
- Closing with the **X** hides to tray (unless **Exit** was chosen).

### Tray icon
- **Left-click**: open the status window
- **Right-click menu**:
  - Settings…
  - Readme
  - Check Now
  - Open Logs Folder
  - Always on top (toggle)
  - Exit

### Always-on-top
- Controlled by settings **and** the tray menu.
- When enabled, the status window stays above other windows.

### Startup / `--tray`
Run with `--tray` to start hidden/minimized to the tray.

---

## Headline vs detail model (important)

The app uses two kinds of user-facing text:

- **Headline state** (stable, drives icons/tray/toasts)
  - `Unknown` / `Ok` / `Blocked` / `AlignmentOff` / `Disconnected`
- **Detail text** (informational only)
  - Updates UI detail line but does **not** change tray icons or fire toasts by itself.

Toasts are emitted on **headline transitions only** (not on repeated polls).

---

## Files and folders

### Settings
- **Settings file**:  
  `%AppData%\<ExeNameWithoutExtension>\settings.json`  
  (The folder name follows the executable name without extension.)

### Logs
- **Logs folder**:  
  `<Application folder>\logs`

---

## Operating modes

## 1) Eyes-only mode (Weight Mode disabled)

### Definition
- `WeightModeEnabled = false`
- The app uses eye polling + confirm delay only.

### Behavior (simplified)
1. Every `PollSeconds`:
   - **Eye Check #1** runs (with retry/failure policy)
2. If Eye1 is `FALSE`:
   - Headline becomes `Ok`
3. If Eye1 is `TRUE`:
   - Wait `EyeConfirmDelaySeconds`
   - Run **Eye Check #2** (confirm)
   - If Eye2 is `TRUE` → headline becomes **`AlignmentOff`**
   - If Eye2 is `FALSE` → ignore spike → headline becomes `Ok`

### Timing guidance
For alignment monitoring, set `EyeConfirmDelaySeconds` to roughly:
> worst-case truck entry time + small buffer

The goal is to avoid confirming short TRUE spikes.

---

## 2) Weight Mode (Weight Mode enabled)

### Definition
- `WeightModeEnabled = true`
- A background TCP reader connects to `WeightIp:WeightPort`
- It classifies weight into modes that gate eye behavior and adjust timing

---

## Weight stream processing

### Sampling and parsing
- TCP loop reads bytes and assembles newline-delimited ASCII lines.
- It accepts up to about **2 samples per second** (throttling to reduce churn).
- Parsing extracts the **first digit run of length ≥ 2**, with an optional `-` immediately before the digit run.

### Stability window
Using `WeightWindowSeconds` (rolling window):
- Keep samples within the time window
- Compute `min` and `max`
- `range = max - min`

Stability:
- **stable** if `range <= (WeightStableBand * 2)`  
  (band is +/-; the total swing allowed is 2× band)

Zero:
- **zero** if `abs(weight) < WeightZeroBand`

### Weight modes
- **Unavailable**
  - Not started, stopped, disconnected, error, or **stale**
- **Unstable**
  - Weight is changing more than the stability band allows
- **StableZero**
  - Stable and near zero
- **StableNonZero**
  - Stable and not near zero (typically “truck present”)

### Stale handling
If no accepted weight updates arrive within `WeightStaleSeconds`, the snapshot is treated as **Unavailable ("stale")**.

---

## How Weight Mode changes eye behavior

### A) Motion gating (most important)
When weight mode is **Unstable (motion)**:
- The app **pauses eye network calls**
- It runs a fast local “motion cadence” loop (default 1s) to re-check weight state
- UI detail indicates motion and that eyes are paused/ignored

This avoids using the eye input when the system is least reliable (vehicle movement).

### B) Dynamic timing (poll + confirm delay)
Each poll computes timing from the current weight snapshot:
- Poll cadence (seconds)
- Confirm delay (seconds)
- Whether to pause eyes due to motion

Stable-nonzero also supports a **fast window**:
- For `StableNonZeroFastWindowSeconds` after entering StableNonZero, the app may use the “fast mode” timing (poll + confirm delay) before settling into the normal stable-nonzero cadence.

### C) Burst protection (StableZero only)
When weight is usable and **StableZero**:
- **Eye Check #1** uses a **burst** (`WeightBurstCount` with `WeightBurstDelayMs`)
- If any successful response returns `FALSE`, the result is treated as **OK immediately** (anti-false-positive)
- If the burst has **some successes** but fewer than `WeightBurstMinTrueSuccess` and no false:
  - The result is treated as **inconclusive**
  - **No headline change**
  - Detail is updated (optionally logged in debug mode)
- If burst yields **0 successes**:
  - The normal failure policy is invoked once (to decide if this is offline/unreachable)

When weight is **StableNonZero**, Eye #1 is **one-shot** (no burst) until the mode changes away from StableNonZero.

### D) Confirm protection (motion during confirm)
If Eye1 is `TRUE` and the app is waiting the confirm delay:
- If weight becomes **Unstable** during the delay:
  - Confirm is aborted
  - Headline is not committed based on the eye input

### E) Labeling (`Blocked` vs `AlignmentOff`)
A confirmed eye `TRUE` becomes:
- **`Blocked`** only if weight is **StableNonZero at commit time**
- Otherwise **`AlignmentOff`**

This intentionally ties “Blocked” to “truck present” as best as possible.

---

## Eye network failure policy (`Disconnected`)

- The SOAP call has a retry wrapper:
  - Retries up to `FailureRetryCount`
  - Delay between attempts: `FailureRetryDelaySeconds`
  - Throttles repeated log/UI noise during prolonged failures
  - Detects “probably offline” using socket-error heuristics

When the device is deemed **offline/unreachable**, the app forces headline:
- **`Disconnected`** (toast on transition)

### Weight monitor interaction during eye outages
If eyes are forced **Disconnected** and Weight Mode is enabled:
- The app stops the weight monitor
- Weight state is set Unavailable (reason like “eyes offline”)
- When eyes recover, weight monitoring can be resumed

---

## Debug logging and burst test

- Enabling **Debug logging**:
  - Produces more verbose logs
  - Shows the burst/poll test section in Settings
- Burst test repeatedly calls `IsInputOn` for diagnostics
  - While running:
    - OK/Cancel disabled
    - Burst button becomes **Cancel**

---

## Settings reference (high level)

### Eye / general
- `LocationName` – used in tray text
- `IpAddress` – eye device IPv4
- `InputId` – which input to query
- `PollSeconds` – eyes-only base poll interval
- `EyeConfirmDelaySeconds` – eyes-only confirm delay
- `FailureRetryDelaySeconds`, `FailureRetryCount` – eye retry/failure policy
- `AlwaysOnTop` – keep window top-most

### Weight mode
- `WeightModeEnabled`
- `WeightIp`, `WeightPort`
- `WeightPollSeconds` – weight-mode default cadence
- `PollSecondsStableNonZero` – stable-nonzero cadence
- `WeightEyeConfirmDelaySeconds` – weight-mode default confirm delay
- `WeightEyeConfirmDelayFastSeconds` – fast-window confirm delay
- `StableNonZeroFastWindowSeconds` – duration of fast stable-nonzero behavior
- `WeightBurstCount`, `WeightBurstDelayMs`, `WeightBurstMinTrueSuccess`
- `WeightStableBand`, `WeightZeroBand`, `WeightWindowSeconds`, `WeightStaleSeconds`

### Diagnostics
- `DebugLogging`
- `BurstTestCount`, `BurstTestDelayMs`

---

## Troubleshooting

- **Headline = `Disconnected`**
  - Wrong IP, device down, network issue, or unreachable endpoint.
  - Confirm eye device power/network; check routing/firewall.

- **Frequent `AlignmentOff` in eyes-only mode**
  - Increase `EyeConfirmDelaySeconds` (worst-case entry time + buffer).

- **Weight Mode always `Unavailable`**
  - Verify `WeightIp:WeightPort`, that the stream is reachable, and that it emits parseable weights.
  - Check for stale: raise `WeightStaleSeconds` if the stream is slow/quiet.

- **Weight Mode frequently `Unstable`**
  - Increase `WeightStableBand` and/or `WeightWindowSeconds` if the signal naturally jitters.

- **“Inconclusive burst” detail messages**
  - Indicates intermittent network success without enough confidence to treat as TRUE.
  - Adjust burst parameters or investigate network stability.

---

## Example configuration (`settings.json`)

Below is an example `settings.json` showing the full schema in the same order as the Settings UI.

> Note: The app expects strict IPv4 values for `IpAddress` and (when enabled) `WeightIp`.
> The example uses loopback (`127.0.0.1`) for illustration.

```json
{
  "LocationName": "Location1",
  "IpAddress": "127.0.0.1",
  "InputId": 0,
  "PollSeconds": 15,
  "EyeConfirmDelaySeconds": 20,
  "FailureRetryDelaySeconds": 2,
  "FailureRetryCount": 3,

  "WeightModeEnabled": true,
  "WeightIp": "127.0.0.1",
  "WeightPort": 4662,

  "WeightPollSeconds": 60,
  "WeightEyeConfirmDelaySeconds": 20,
  "PollSecondsStableNonZero": 5,
  "WeightEyeConfirmDelayFastSeconds": 5,
  "StableNonZeroFastWindowSeconds": 120,

  "WeightBurstCount": 3,
  "WeightBurstDelayMs": 500,
  "WeightBurstMinTrueSuccess": 1,

  "WeightStableBand": 20,
  "WeightZeroBand": 1,
  "WeightWindowSeconds": 3,
  "WeightStaleSeconds": 2,

  "DebugLogging": false,
  "BurstTestCount": 5,
  "BurstTestDelayMs": 1000,

  "AlwaysOnTop": true
}

---

## License
(Choose and add your repo license here.)

---
