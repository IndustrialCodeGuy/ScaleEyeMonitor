using System.Buffers;
using System.Net.Sockets;

namespace Scale_Eye_Monitor
{
    /*
     * MainForm.WeightMonitor.cs
     * -------------------------
     * Partial: MainForm (Scale Eye Monitor)
     *
     * Responsibilities:
     *   Maintain a background TCP connection to the scale weight stream and publish a
     *   lightweight WeightState snapshot used by PollOnceAsync() for gating decisions:
     *     - Start/stop driven by settings (WeightModeEnabled + endpoint validation)
     *     - Connect/reconnect with exponential backoff
     *     - Read bytes and assemble newline-delimited ASCII lines
     *     - Parse weight from the device’s ASCII output (first 2+ digit token, optional '-' prefix)
     *     - Throttle accepted parsed samples (currently ~2 Hz) to reduce churn
     *     - Maintain a rolling time window and compute stability (min/max range)
     *     - Expose GetWeightStateSnapshot() with stale detection (WeightStaleSeconds)
     *     - Emit toasts/logs on connect/disconnect/failure with throttling
     *
     * Polling integration:
     *   - When weight transitions INTO Unstable (motion):
     *       * CancelActivePoll() stops confirm delays / in-flight eye calls ASAP
     *       * RescheduleNextPollFromNow(pollSoon:true) wakes the poll loop immediately
     *       * Polling then runs in “motion cadence” (WeightMotionCheckSeconds) without eye calls
     *
     * Depends on:
     *   - Runtime fields: WeightModeEnabled, WeightIp/Port, bands/windows/stale seconds, DebugLogging
     *   - Logging helper: Log()
     *   - ToastHelper + IpValidator
     */

    public sealed partial class MainForm
    {
        // =====================================================================
        //  Types (Weight)
        // =====================================================================
        private enum WeightMode { Unavailable, Unstable, StableZero, StableNonZero }

        private readonly record struct WeightState(
            WeightMode Mode,
            int? Weight,
            int Range,
            DateTime LastUpdate,
            string Error);

        // =====================================================================
        //  Weight monitor toast/log spam control (runtime state)
        // =====================================================================
        private bool _weightWasConnected = false;
        private bool _weightOutageActive = false;           // true after any failure until we reconnect
        private bool _weightDisconnectToastShown = false;   // ensures “lost” toast fires once per outage
        private bool _weightEverConnected = false;          // distinguishes startup-connect-fail vs reconnect
        private bool _weightStartupToastShown = false;      // only once per run (until a successful connect)
        private string _weightLastErrorSig = "";
        private DateTime _weightLastErrorLogUtc = DateTime.MinValue;
        private DateTime _muteWeightToastsUntilUtc = DateTime.MinValue;

        private bool WeightToastsMuted => DateTime.UtcNow < _muteWeightToastsUntilUtc;

        // Optional: periodic reminder while failing (set 0 to disable)
        private static readonly TimeSpan _weightFailReminderInterval = TimeSpan.FromMinutes(5);

        // =====================================================================
        //  State + snapshot storage (protected by _weightLock)
        // =====================================================================
        private readonly object _weightLock = new();
        private readonly Queue<(DateTime Ts, int Weight)> _weightSamples = new();
        private WeightState _weightState = new(WeightMode.Unavailable, null, 0, DateTime.MinValue, "not started");

        // =====================================================================
        //  Background task lifecycle
        // =====================================================================
        private CancellationTokenSource? _weightCts;
        private Task? _weightTask;

        // =====================================================================
        //  Start/stop lifecycle (driven by settings)
        // =====================================================================
        private void StartOrStopWeightMonitor()
        {
            // Disabled => ensure stopped and do not carry weight-mode history into eyes-only mode.
            if (!WeightModeEnabled)
            {
                ResetAlignmentCycleTracking("weight mode disabled");
                StopWeightMonitor();
                return;
            }

            // Validate endpoint (prevents the background loop from hammering an empty/invalid weight endpoint).
            if (string.IsNullOrWhiteSpace(WeightIp) ||
                !IpValidator.IsStrictIPv4(WeightIp) ||
                WeightPort <= 0)
            {
                StopWeightMonitor(); // make sure any prior instance is down
                SetWeightUnavailable("invalid weight endpoint");
                Log($"WeightMonitor NOT started: invalid endpoint (ip='{WeightIp}', port={WeightPort})");
                return;
            }

            // Already running?
            if (_weightTask is not null && !_weightTask.IsCompleted)
                return;

            StartWeightMonitor();
        }

        private void StartWeightMonitor()
        {
            // Stop any prior loop without wiping toast/outage history.
            // (Restarts should behave like reconnects, not "fresh startup".)
            StopWeightMonitor(resetToastState: false);

            // Link to app lifetime so shutdown cancels ConnectAsync/ReadAsync promptly.
            _weightCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
            _weightTask = Task.Run(() => WeightMonitorLoopAsync(_weightCts.Token));

            // Observe faults so they don't surface as unobserved exceptions later
            _ = _weightTask.ContinueWith(t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        private void StopWeightMonitor() => StopWeightMonitor(resetToastState: true);

        private void StopWeightMonitor(bool resetToastState)
        {
            var cts = _weightCts;
            var task = _weightTask;

            _weightCts = null;
            _weightTask = null;

            try { cts?.Cancel(); } catch { }

            // Dispose CTS only after the loop is done (non-blocking)
            if (cts is not null)
            {
                if (task is not null && !task.IsCompleted)
                {
                    _ = task.ContinueWith(_ =>
                    {
                        try { cts.Dispose(); } catch { }
                    }, TaskContinuationOptions.ExecuteSynchronously);
                }
                else
                {
                    try { cts.Dispose(); } catch { }
                }
            }

            // Always: we are no longer connected.
            _weightWasConnected = false;

            // Optionally reset toast/outage history (normal stops).
            // For "eyes offline" stops we preserve this so a resume can emit "restored".
            if (resetToastState)
            {
                _weightEverConnected = false;
                _weightStartupToastShown = false;
                _weightOutageActive = false;
                _weightDisconnectToastShown = false;
                _weightLastErrorSig = "";
                _weightLastErrorLogUtc = DateTime.MinValue;
            }

            lock (_weightLock)
            {
                _weightSamples.Clear();
                _weightState = new WeightState(WeightMode.Unavailable, null, 0, DateTime.MinValue, "stopped");
            }
        }

        // =====================================================================
        //  Snapshot API (used by polling logic)
        // =====================================================================
        private WeightState GetWeightStateSnapshot()
        {
            lock (_weightLock)
            {
                var st = _weightState;

                if (st.Mode != WeightMode.Unavailable && st.LastUpdate != DateTime.MinValue)
                {
                    if ((DateTime.Now - st.LastUpdate).TotalSeconds > WeightStaleSeconds)
                        return new WeightState(WeightMode.Unavailable, st.Weight, st.Range, st.LastUpdate, "stale");
                }

                return st;
            }
        }

        // =====================================================================
        //  Background loop (connect/read/parse/window/stability)
        // =====================================================================
        private async Task WeightMonitorLoopAsync(CancellationToken token)
        {
            int backoffMs = 1000;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var client = new TcpClient { NoDelay = true };
                    await client.ConnectAsync(WeightIp, WeightPort, token).ConfigureAwait(false);

                    using NetworkStream ns = client.GetStream();

                    // Connected -> reset backoff + log transition once
                    backoffMs = 1000;
                    LogWeightConnectedOnce();

                    // Throttle: accept at most one parsed sample every n ms
                    long nextAcceptMs = Environment.TickCount64; // accept immediately on connect

                    // Stall timer: if no bytes arrive for a while, force-close to break ReadAsync
                    TimeSpan readTimeout = TimeSpan.FromSeconds(Math.Max(2, WeightStaleSeconds + 10));

                    using var stallTimer = new System.Threading.Timer(_ =>
                    {
                        try { client.Close(); } catch { }
                    }, null, System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);

                    void ArmStall()
                    {
                        try { stallTimer.Change(readTimeout, System.Threading.Timeout.InfiniteTimeSpan); } catch { }
                    }

                    ArmStall();

                    // Cancellation should also break pending reads (belt/suspenders)
                    using var cancelReg = token.Register(() => { try { client.Close(); } catch { } });

                    byte[] readBuf = ArrayPool<byte>.Shared.Rent(4096);
                    byte[] lineBuf = ArrayPool<byte>.Shared.Rent(4096);

                    int lineLen = 0;
                    bool discardingLongLine = false;

                    try
                    {
                        // Local NON-ASYNC helper: keeps ReadOnlySpan<byte> out of the async state machine (CS9202)
                        void ProcessLine(int len)
                        {
                            if (len <= 0) return;

                            ReadOnlySpan<byte> line = new(lineBuf, 0, len);

                            // trim trailing \r (CRLF)
                            if (line.Length > 0 && line[^1] == (byte)'\r')
                                line = line[..^1];

                            // ~2 Hz gate BEFORE parse (throttles accepted parsed samples)
                            long nowMs = Environment.TickCount64;
                            if (nowMs < nextAcceptMs)
                                return;

                            if (TryParseWeightFromAsciiLine(line, out int w))
                            {
                                nextAcceptMs = nowMs + 500;
                                UpdateWeightFromSample(w, DateTime.Now);
                            }
                        }

                        while (!token.IsCancellationRequested)
                        {
                            int n = await ns.ReadAsync(readBuf.AsMemory(0, readBuf.Length), token).ConfigureAwait(false);
                            if (n == 0)
                                throw new IOException("Weight stream closed.");

                            ArmStall(); // any bytes received => not stalled

                            for (int i = 0; i < n; i++)
                            {
                                byte b = readBuf[i];

                                if (b == (byte)'\n')
                                {
                                    if (!discardingLongLine)
                                        ProcessLine(lineLen);

                                    // reset accumulator either way
                                    lineLen = 0;
                                    discardingLongLine = false;
                                    continue;
                                }

                                if (discardingLongLine)
                                    continue;

                                if (lineLen < lineBuf.Length)
                                {
                                    lineBuf[lineLen++] = b;
                                }
                                else
                                {
                                    // line too long; discard until newline
                                    discardingLongLine = true;
                                    lineLen = 0;
                                }
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(readBuf);
                        ArrayPool<byte>.Shared.Return(lineBuf);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested)
                        break;

                    HandleWeightMonitorFailure(ex, backoffMs);

                    try { await Task.Delay(backoffMs, token).ConfigureAwait(false); } catch { }
                    backoffMs = Math.Min(backoffMs * 2, 30000);
                }
            }
        }

        // =====================================================================
        //  Failure handling (toasts + logging + unavailable state)
        // =====================================================================
        private void HandleWeightMonitorFailure(Exception ex, int backoffMs)
        {
            bool wasConnected = _weightWasConnected;

            SetWeightUnavailable($"{ex.GetType().Name}: {ex.Message}");
            _weightOutageActive = true;

            // Startup case: we've never connected yet
            if (!wasConnected && !_weightEverConnected && !_weightStartupToastShown)
            {
                _weightStartupToastShown = true;

                try
                {
                    if (!WeightToastsMuted)
                    {
                        ToastHelper.ShowWarningToast(
                            "Scale Eye Monitor",
                            $"Weight stream unreachable at startup ({WeightIp}:{WeightPort}). " +
                            "Weight mode is unavailable; using eyes-only until it connects.");
                    }
                }
                catch { }
            }

            // Disconnect case: we WERE connected and then dropped
            if (wasConnected && !_weightDisconnectToastShown)
            {
                _weightDisconnectToastShown = true;

                try
                {
                    if (!WeightToastsMuted)
                    {
                        ToastHelper.ShowWarningToast(
                            "Scale Eye Monitor",
                            "Weight stream disconnected. Falling back to eyes-only until it returns.");
                    }
                }
                catch { }
            }

            if (DebugLogging)
            {
                _weightWasConnected = false; // keep state accurate
                Log($"WeightMonitor ERROR: {ex.GetType().Name}: {ex.Message} (retry in {backoffMs}ms)");
            }
            else
            {
                LogWeightFailureThrottled(ex, backoffMs);
            }
        }

        // =====================================================================
        //  Weight-mode transition hook (wake poll loop ASAP on motion)
        // =====================================================================
        private void OnWeightModeTransition(WeightMode prev, WeightState now)
        {
            if (!WeightModeEnabled) return;

            // Trigger only on transition INTO motion
            if (now.Mode == WeightMode.Unstable && prev != WeightMode.Unstable)
            {
                // Stop any confirm delay / in-flight eye calls ASAP
                CancelActivePoll();

                // Adopt motion cadence immediately
                SetDesiredPollSeconds(WeightMotionCheckSeconds);

                // Wake the loop to run ASAP (single polling path)
                RescheduleNextPollFromNow(pollSoon: true);
            }
        }

        // =====================================================================
        //  State update helpers (under _weightLock)
        // =====================================================================
        private void SetWeightUnavailable(string reason)
        {
            lock (_weightLock)
            {
                _weightSamples.Clear();
                _weightState = new WeightState(WeightMode.Unavailable, null, 0, DateTime.Now, reason);
            }

            ResetAlignmentCycleTracking($"weight unavailable: {reason}");

            if (WeightModeEnabled && !_forcedDisconnectActive)
            {
                SetDesiredPollSeconds(PollSeconds);          // eyes-only cadence immediately
                RescheduleNextPollFromNow(pollSoon: true);   // wake loop to recompute now
            }
        }

        private void UpdateWeightFromSample(int weight, DateTime ts)
        {
            WeightMode prevMode;
            WeightState newState;
            bool modeChanged;
            string? modeMsg = null;

            lock (_weightLock)
            {
                _weightSamples.Enqueue((ts, weight));

                // Trim samples outside the window
                while (_weightSamples.Count > 0 &&
                       (ts - _weightSamples.Peek().Ts).TotalSeconds > WeightWindowSeconds)
                {
                    _weightSamples.Dequeue();
                }

                // Compute min/max over window
                int min = weight, max = weight;
                foreach (var (_, sampleWeight) in _weightSamples)
                {
                    if (sampleWeight < min) min = sampleWeight;
                    if (sampleWeight > max) max = sampleWeight;
                }

                int range = max - min;

                // Stable if the total range over the window is within +/-band
                bool stable = range <= (WeightStableBand * 2);
                bool isZero = Math.Abs(weight) < WeightZeroBand;

                WeightMode mode = stable ? (isZero ? WeightMode.StableZero : WeightMode.StableNonZero) : WeightMode.Unstable;

                prevMode = _weightState.Mode;
                newState = new WeightState(mode, weight, range, ts, "");
                _weightState = newState;

                modeChanged = (mode != prevMode);

                if (DebugLogging && modeChanged)
                    modeMsg = $"Weight mode -> {mode} (w={weight}, range={range})";
            }

            // IMPORTANT: notify AFTER the lock (can cancel polls / wake scheduler)
            NoteAlignmentCycleWeight(newState);

            if (modeMsg is not null)
                Log(modeMsg);

            if (modeChanged)
                OnWeightModeTransition(prevMode, newState);

            if (prevMode == WeightMode.Unavailable && newState.Mode != WeightMode.Unavailable && !_forcedDisconnectActive)
                RescheduleNextPollFromNow(pollSoon: true);
        }

        // =====================================================================
        //  Logging helpers
        // =====================================================================
        private void LogWeightConnectedOnce()
        {
            // Only log transition: disconnected -> connected
            if (_weightWasConnected) return;

            _weightDisconnectToastShown = false;
            _weightWasConnected = true;
            _weightEverConnected = true;
            _weightStartupToastShown = false;
            _weightLastErrorSig = "";
            _weightLastErrorLogUtc = DateTime.MinValue;

            if (_weightOutageActive)
            {
                _weightOutageActive = false;
                _weightDisconnectToastShown = false;

                try
                {
                    if (!WeightToastsMuted)
                        ToastHelper.ShowInfoToast("Scale Eye Monitor", "Weight monitoring restored. Weight gating is active again.");
                }
                catch { }
            }

            Log("WeightMonitor connected.");
        }

        private void LogWeightFailureThrottled(Exception ex, int backoffMs)
        {
            _weightWasConnected = false;

            string sig = $"{ex.GetType().Name}|{ex.Message}";
            var nowUtc = DateTime.UtcNow;

            bool changed = !string.Equals(sig, _weightLastErrorSig, StringComparison.Ordinal);
            bool remind = _weightFailReminderInterval > TimeSpan.Zero &&
                          (nowUtc - _weightLastErrorLogUtc) >= _weightFailReminderInterval;

            if (changed || remind || _weightLastErrorLogUtc == DateTime.MinValue)
            {
                _weightLastErrorSig = sig;
                _weightLastErrorLogUtc = nowUtc;

                Log($"WeightMonitor ERROR: {ex.GetType().Name}: {ex.Message} (retry in {backoffMs}ms)");
            }
        }

        // =====================================================================
        //  Parsing
        // =====================================================================
        private static bool TryParseWeightFromAsciiLine(ReadOnlySpan<byte> line, out int weight)
        {
            weight = 0;
            if (line.IsEmpty) return false;

            int start = -1;
            int digits = 0;

            for (int i = 0; i < line.Length; i++)
            {
                byte c = line[i];

                bool isDigit = (c >= (byte)'0' && c <= (byte)'9');
                if (isDigit)
                {
                    if (digits == 0) start = i;
                    digits++;
                    continue;
                }

                if (digits >= 2)
                    return TryParseToken(line, start, i, out weight);

                start = -1;
                digits = 0;
            }

            if (digits >= 2 && start >= 0)
                return TryParseToken(line, start, line.Length, out weight);

            return false;

            static bool TryParseToken(ReadOnlySpan<byte> line, int start, int end, out int w)
            {
                w = 0;

                bool neg = (start > 0 && line[start - 1] == (byte)'-');

                long val = 0;
                for (int i = start; i < end; i++)
                {
                    byte c = line[i];
                    if (c < (byte)'0' || c > (byte)'9') return false;

                    val = (val * 10) + (c - (byte)'0');
                    if (val > int.MaxValue) return false; // conservative
                }

                w = neg ? (int)-val : (int)val;
                return true;
            }
        }
    }
}
