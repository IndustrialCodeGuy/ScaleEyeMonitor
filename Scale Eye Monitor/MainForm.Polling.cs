namespace Scale_Eye_Monitor
{
    /*
     * MainForm.Polling.cs
     * -------------------
     * Partial: MainForm (Scale Eye Monitor)
     *
     * Responsibilities:
     *   - Own the polling scheduler (poll loop) with “next due” tracking:
     *       * _nextPollDueUtcTicks + _pollWake semaphore allow immediate rescheduling.
     *   - Provide a safe “one poll” entrypoint (PollOnceSafeAsync) guarded by _opGate.
     *   - Implement core polling logic (PollOnceAsync):
     *       1) Snapshot weight (when enabled) and update StableNonZero fast-window tracking.
     *       2) Select timing in one place:
     *            - pollSeconds + confirmDelaySeconds are chosen together from a single branch
     *            - pauseEyes is true during weight motion/unstable (no eye network calls)
     *       3) Eye check #1:
     *            - If weight is usable AND StableZero => use burst sampling:
     *                * Any successful FALSE => OK immediately (anti-false-positive)
     *                * TRUE is only a candidate if >= WeightBurstMinTrueSuccess successes
     *                * Inconclusive bursts do NOT change headline state
     *            - Otherwise => single SOAP call with failure policy wrapper
     *       4) If Eye #1 is TRUE-candidate => wait confirmDelaySeconds
     *          Abort confirm if motion begins.
     *       5) Eye check #2 (confirm):
     *            - Direct SOAP call; failure policy may force Disconnected
     *       6) If confirmed TRUE => commit a headline:
     *            - StableZero TRUE latches AlignmentOff until a confirmed FALSE.
     *            - StableNonZero TRUE becomes Blocked unless AlignmentOff is latched.
     *            - Weight-cycle history may add an inbound/outbound suspected suffix
     *              to the detail line when the configured vehicle minimum was crossed.
     *
     * UI rule:
     *   - Only confirmed headline events call CommitHeadline().
     *   - Informational/diagnostic text uses UpdateDetail() only.
     *
     * Depends on:
     *   - SOAP + failure policy: MainForm.EyeSoap.cs
     *   - Weight snapshots + motion wake/cancel: MainForm.WeightMonitor.cs
     *   - Headline/detail UI helpers: MainForm.Ui.cs
     */

    public sealed partial class MainForm
    {
        // =====================================================================
        //  Constants / fixed configuration
        // =====================================================================
        private const int WeightMotionCheckSeconds = 1; // while motion: check weight quickly, NO eye network calls

        // =====================================================================
        //  Poll loop state
        // =====================================================================
        private CancellationTokenSource? _pollLoopCts;
        private Task? _pollLoopTask;

        private int _desiredPollSeconds = -1;                     // scheduler uses this as the base target interval
        private readonly SemaphoreSlim _pollWake = new(0, 1);     // wakes the loop to recompute delay
        private long _nextPollDueUtcTicks;                        // 0 = unset; DateTime.UtcNow.Ticks-based

        // Active "single poll" cancel (Settings apply uses this)
        private CancellationTokenSource? _activePollCts;

        // "Check Now" suppression: prevent immediate loop poll right after manual check
        private int _skipNextLoopPoll; // 0/1

        // =====================================================================
        //  StableNonZero attention window (prevents fast polling all night)
        // =====================================================================
        private WeightMode _lastWeightModeSeen = WeightMode.Unavailable;
        private DateTime _stableNonZeroFastSince = DateTime.MinValue;
        private bool _stableNonZeroFastActive;

        // TRUE confirmed while the scale is at stable zero is latched until a
        // confirmed FALSE clears it. While latched, StableNonZero TRUE stays
        // AlignmentOff instead of switching to Blocked.
        //
        // Weight-mode cycle tracking is scoped from a confirmed clear StableZero
        // baseline to the next clear StableZero reset. Confirmed FALSE is absolute
        // evidence that the eyes were clear during the active cycle; the vehicle
        // minimum is used only to decide whether the final AlignmentOff headline
        // gets an inbound/outbound suspected suffix on the detail line.
        private readonly object _alignmentCycleLock = new();
        private bool _alignmentOffConfirmedAtStableZero;
        private bool _alignmentClearStableZeroBaseline;
        private bool _alignmentCycleExceededVehicleMin;
        private bool _alignmentCycleSawConfirmedFalse;
        private AlignmentSuspectKind _alignmentSuspect = AlignmentSuspectKind.None;

        private enum AlignmentSuspectKind
        {
            None,
            Inbound,
            Outbound
        }

        // Set while weight motion is pausing eye polling so the first stable poll
        // can replace the stale motion-paused detail before Eye #1 runs.
        private bool _eyePollingPausedForMotion;

        private void CancelActivePoll()
        {
            try { _activePollCts?.Cancel(); } catch { }
        }

        private void MarkAlignmentOffConfirmedAtStableZero()
        {
            bool wasLatched;
            AlignmentSuspectKind suspect;

            lock (_alignmentCycleLock)
            {
                wasLatched = _alignmentOffConfirmedAtStableZero;

                suspect = AlignmentSuspectKind.None;
                if (_alignmentClearStableZeroBaseline && _alignmentCycleExceededVehicleMin)
                {
                    suspect = _alignmentCycleSawConfirmedFalse
                        ? AlignmentSuspectKind.Outbound
                        : AlignmentSuspectKind.Inbound;
                }

                _alignmentOffConfirmedAtStableZero = true;
                _alignmentSuspect = suspect;
            }

            if (DebugLogging)
            {
                string suffix = suspect switch
                {
                    AlignmentSuspectKind.Inbound => " inbound suspected",
                    AlignmentSuspectKind.Outbound => " outbound suspected",
                    _ => ""
                };

                if (!wasLatched)
                    Log($"Alignment-off latch set: TRUE confirmed while scale was stable zero.{suffix}");
                else if (suspect != AlignmentSuspectKind.None)
                    Log($"Alignment-off latch remains set:{suffix}.");
            }
        }

        private void ClearAlignmentOffConfirmedAtStableZero(string reason)
        {
            bool wasLatched;

            lock (_alignmentCycleLock)
            {
                wasLatched = _alignmentOffConfirmedAtStableZero;
                _alignmentOffConfirmedAtStableZero = false;
                _alignmentSuspect = AlignmentSuspectKind.None;
            }

            if (wasLatched && DebugLogging)
                Log($"Alignment-off latch cleared: {reason}.");
        }

        private void ResetAlignmentCycleTracking(string reason)
        {
            bool wasLatched;
            bool hadCycleTracking;

            lock (_alignmentCycleLock)
            {
                wasLatched = _alignmentOffConfirmedAtStableZero;
                hadCycleTracking = _alignmentClearStableZeroBaseline ||
                                   _alignmentCycleExceededVehicleMin ||
                                   _alignmentCycleSawConfirmedFalse ||
                                   _alignmentSuspect != AlignmentSuspectKind.None;

                _alignmentOffConfirmedAtStableZero = false;
                _alignmentClearStableZeroBaseline = false;
                _alignmentCycleExceededVehicleMin = false;
                _alignmentCycleSawConfirmedFalse = false;
                _alignmentSuspect = AlignmentSuspectKind.None;
            }

            if (wasLatched && DebugLogging)
                Log($"Alignment-off latch cleared: {reason}.");
            else if (hadCycleTracking && DebugLogging)
                Log($"Alignment cycle tracking reset: {reason}.");
        }

        private void NoteConfirmedFalseForAlignmentCycle(WeightState ws, string reason)
        {
            if (!WeightModeEnabled || ws.Mode == WeightMode.Unavailable)
            {
                ClearAlignmentOffConfirmedAtStableZero(reason);
                return;
            }

            if (ws.Mode == WeightMode.StableZero)
            {
                bool wasLatched;

                lock (_alignmentCycleLock)
                {
                    wasLatched = _alignmentOffConfirmedAtStableZero;

                    _alignmentOffConfirmedAtStableZero = false;
                    _alignmentClearStableZeroBaseline = true;
                    _alignmentCycleExceededVehicleMin = false;
                    _alignmentCycleSawConfirmedFalse = false;
                    _alignmentSuspect = AlignmentSuspectKind.None;
                }

                if (wasLatched && DebugLogging)
                    Log($"Alignment-off latch cleared: {reason}.");
                else if (DebugLogging)
                    Log("Alignment cycle baseline set: FALSE confirmed while scale was stable zero.");

                return;
            }

            bool firstFalseDuringCycle = false;

            lock (_alignmentCycleLock)
            {
                if (_alignmentClearStableZeroBaseline && !_alignmentCycleSawConfirmedFalse)
                {
                    _alignmentCycleSawConfirmedFalse = true;
                    firstFalseDuringCycle = true;
                }
            }

            if (firstFalseDuringCycle && DebugLogging)
                Log($"Alignment cycle FALSE evidence recorded: {reason}.");

            ClearAlignmentOffConfirmedAtStableZero(reason);
        }

        private void NoteAlignmentCycleWeight(WeightState ws)
        {
            if (!WeightModeEnabled || !IsAtOrAboveAlignmentCycleVehicleMin(ws))
                return;

            bool firstVehicleMin = false;

            lock (_alignmentCycleLock)
            {
                if (_alignmentClearStableZeroBaseline && !_alignmentCycleExceededVehicleMin)
                {
                    _alignmentCycleExceededVehicleMin = true;
                    firstVehicleMin = true;
                }
            }

            if (firstVehicleMin && DebugLogging)
                Log($"Alignment cycle vehicle threshold crossed: w={ws.Weight}, min={BlockedGuidanceMinWeight}.");
        }

        private bool IsAlignmentOffLatchedAtStableZero()
        {
            lock (_alignmentCycleLock)
            {
                return _alignmentOffConfirmedAtStableZero;
            }
        }

        private string GetAlignmentSuspectDetailSuffix()
        {
            AlignmentSuspectKind suspect;

            lock (_alignmentCycleLock)
            {
                suspect = _alignmentOffConfirmedAtStableZero ? _alignmentSuspect : AlignmentSuspectKind.None;
            }

            return suspect switch
            {
                AlignmentSuspectKind.Inbound => " - inbound suspected",
                AlignmentSuspectKind.Outbound => " - outbound suspected",
                _ => ""
            };
        }

        private void ResetStableNonZeroFastWindow()
        {
            _stableNonZeroFastActive = false;
            _stableNonZeroFastSince = DateTime.MinValue;
            _lastWeightModeSeen = WeightMode.Unavailable;
        }

        private void UpdateStableNonZeroFastWindow(WeightMode current, DateTime now)
        {
            if (!WeightModeEnabled)
            {
                ResetStableNonZeroFastWindow();
                return;
            }

            if (current == WeightMode.StableNonZero)
            {
                // entering StableNonZero => start fast window
                if (_lastWeightModeSeen != WeightMode.StableNonZero)
                {
                    _stableNonZeroFastActive = (StableNonZeroFastWindowSeconds > 0);
                    _stableNonZeroFastSince = now;
                }
                else if (_stableNonZeroFastActive && StableNonZeroFastWindowSeconds > 0)
                {
                    if ((now - _stableNonZeroFastSince).TotalSeconds > StableNonZeroFastWindowSeconds)
                        _stableNonZeroFastActive = false;
                }
            }
            else
            {
                // any non-StableNonZero => clear
                _stableNonZeroFastActive = false;
                _stableNonZeroFastSince = DateTime.MinValue;
            }

            _lastWeightModeSeen = current;
        }

        // =====================================================================
        //  Unified timing selection (poll interval + confirm delay)
        //  These are intentionally intertwined: whenever one changes, the other
        //  is selected from the same timing branch.
        // =====================================================================
        private (int pollSeconds, int confirmDelaySeconds, bool pauseEyes) GetTimingFromWeightState(WeightState ws)
        {
            // Eyes-only (or weight disabled): use eye-only timing.
            if (!WeightModeEnabled)
                return (Math.Max(1, PollSeconds), Math.Max(1, EyeConfirmDelaySeconds), pauseEyes: false);

            // Weight mode enabled but no usable weight => fall back to eye-only timing.
            if (ws.Mode == WeightMode.Unavailable)
                return (Math.Max(1, PollSeconds), Math.Max(1, EyeConfirmDelaySeconds), pauseEyes: false);

            // Motion/unstable => NO eye network calls (confirm delay irrelevant here).
            if (ws.Mode == WeightMode.Unstable)
                return (Math.Max(1, WeightMotionCheckSeconds), Math.Max(1, EyeConfirmDelaySeconds), pauseEyes: true);

            // StableNonZero with fast-window active => fast timing (no burst).
            if (ws.Mode == WeightMode.StableNonZero && _stableNonZeroFastActive)
                return (Math.Max(1, PollSecondsStableNonZero), Math.Max(1, WeightEyeConfirmDelayFastSeconds), pauseEyes: false);

            // Normal weight-mode timing (StableZero OR StableNonZero after fast window).
            return (Math.Max(1, WeightPollSeconds), Math.Max(1, WeightEyeConfirmDelaySeconds), pauseEyes: false);
        }

        // =====================================================================
        //  Poll loop lifecycle
        // =====================================================================
        private void StartPollLoop()
        {
            if (_pollLoopTask is not null && !_pollLoopTask.IsCompleted)
                return;

            StopPollLoop();

            // Default to current period so the loop has a sane value.
            System.Threading.Volatile.Write(ref _desiredPollSeconds, Math.Max(1, PollSeconds));

            try
            {
                _pollLoopCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
                _pollLoopTask = Task.Run(() => PollLoopAsync(_pollLoopCts.Token));
                _ = _pollLoopTask.ContinueWith(t => _ = t.Exception,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            }
            catch
            {
                // ignore
            }
        }

        private void StopPollLoop()
        {
            var cts = _pollLoopCts;
            var task = _pollLoopTask;

            _pollLoopCts = null;
            _pollLoopTask = null;

            try { cts?.Cancel(); } catch { }
            try { cts?.Dispose(); } catch { }

            // Observe faults to avoid UnobservedTaskException.
            if (task is not null)
                _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }

        private async Task PollLoopAsync(CancellationToken token)
        {
            // Initial schedule: first poll ~5s after startup
            System.Threading.Interlocked.Exchange(ref _nextPollDueUtcTicks, DateTime.UtcNow.AddSeconds(5).Ticks);

            while (!token.IsCancellationRequested && !_shutdown)
            {
                await WaitUntilNextDueAsync(token).ConfigureAwait(false);

                // after WaitUntilNextDueAsync(token) returns, and before PollOnceSafeAsync:
                long dueBefore = System.Threading.Interlocked.Read(ref _nextPollDueUtcTicks);

                await PollOnceSafeAsync(waitForGate: true, fromLoop: true, token: token).ConfigureAwait(false);

                int seconds = System.Threading.Volatile.Read(ref _desiredPollSeconds);
                if (seconds <= 0) seconds = PollSeconds;
                seconds = Math.Max(1, seconds);

                long computedDue = DateTime.UtcNow.AddSeconds(seconds).Ticks;

                // If something rescheduled while PollOnce was running (e.g., motion transition),
                // don’t stomp it here.
                long dueAfter = System.Threading.Interlocked.Read(ref _nextPollDueUtcTicks);
                if (dueAfter == dueBefore)
                    System.Threading.Interlocked.Exchange(ref _nextPollDueUtcTicks, computedDue);
            }
        }

        // =====================================================================
        //  Poll scheduler (loop) - next-due tracking
        // =====================================================================
        private void RescheduleNextPollFromNow(bool pollSoon = false)
        {
            if (_shutdown) return;

            int seconds = System.Threading.Volatile.Read(ref _desiredPollSeconds);
            if (seconds <= 0) seconds = PollSeconds;
            seconds = Math.Max(1, seconds);

            var due = pollSoon ? DateTime.UtcNow : DateTime.UtcNow.AddSeconds(seconds);
            System.Threading.Interlocked.Exchange(ref _nextPollDueUtcTicks, due.Ticks);

            // Wake the loop (max 1 pending wake)
            try
            {
                if (_pollWake.CurrentCount == 0)
                    _pollWake.Release();
            }
            catch { }
        }

        private async Task WaitUntilNextDueAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && !_shutdown)
            {
                long ticks = System.Threading.Interlocked.Read(ref _nextPollDueUtcTicks);
                if (ticks <= 0)
                    return; // not scheduled => run immediately

                long nowTicks = DateTime.UtcNow.Ticks;
                long remainingTicks = ticks - nowTicks;
                if (remainingTicks <= 0)
                    return;

                var delay = TimeSpan.FromTicks(remainingTicks);

                // Wait for either: timeout (due) OR a wake signal.
                bool woke = await _pollWake.WaitAsync(delay, token).ConfigureAwait(false);
                if (!woke)
                    return; // timeout => due reached

                // woke => recompute delay using the (possibly updated) due time
            }
        }

        // =====================================================================
        //  Poll entrypoint (gate-safe wrapper)
        // =====================================================================
        private async Task<bool> PollOnceSafeAsync(
            bool waitForGate = false,
            bool fromLoop = false,
            CancellationToken token = default,
            bool debugGateUnavailable = false,
            bool suppressNextLoopPoll = false)
        {
            if (_shutdown) return false;

            try
            {
                if (waitForGate)
                {
                    await _opGate.WaitAsync(token).ConfigureAwait(false);
                }
                else if (!await _opGate.WaitAsync(0, token).ConfigureAwait(false))
                {
                    if (debugGateUnavailable)
                        System.Diagnostics.Debug.WriteLine($"[{DateTime.Now:MM/dd/yyyy_HH:mm:ss}] Check Now skipped: poll gate not available.");

                    return false;
                }
            }
            catch (OperationCanceledException) { return false; }
            catch (ObjectDisposedException) { return false; }

            bool ran = false;
            CancellationTokenSource? pollCts = null;

            try
            {
                if (fromLoop && System.Threading.Interlocked.Exchange(ref _skipNextLoopPoll, 0) == 1)
                {
                    // Check Now sets the loop due-time to immediate only so the loop can
                    // consume this skip right away. Once consumed, anchor the next scheduled
                    // poll to the normal interval from now; otherwise an already-due loop can
                    // immediately run a second real poll after the manual check.
                    RescheduleNextPollFromNow();
                    return false;
                }

                pollCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken, token);
                _activePollCts = pollCts;
                ran = true;

                await PollOnceAsync(pollCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore (settings cancel or shutdown)
            }
            catch (Exception ex)
            {
                if (_shutdown) return ran;

                Log($"ERROR: {ex.GetType().Name}: {ex.Message}");
                UpdateDetail($"Error: {ex.Message}", httpShown: "—", stamp: DateTime.Now);
            }
            finally
            {
                if (ReferenceEquals(_activePollCts, pollCts))
                    _activePollCts = null;

                // For manual Check Now, set the loop skip while still holding the
                // operation gate. This prevents a due loop poll from acquiring the
                // gate in the tiny gap after the manual poll completes but before
                // the caller could set _skipNextLoopPoll.
                if (ran && suppressNextLoopPoll)
                {
                    System.Threading.Interlocked.Exchange(ref _skipNextLoopPoll, 1);
                    RescheduleNextPollFromNow(pollSoon: true);
                }

                try { pollCts?.Dispose(); } catch { }
                try { _opGate.Release(); } catch { }
            }

            return ran;
        }

        // =====================================================================
        //  Core poll logic
        // =====================================================================
        private async Task PollOnceAsync(CancellationToken token)
        {
            var now = DateTime.Now;

            if (string.IsNullOrWhiteSpace(EyeUrl))
            {
                // Keep the top-row Scale Status current even if eyes aren't configured.
                WeightState ws0 = WeightModeEnabled
                    ? GetWeightStateSnapshot()
                    : new WeightState(WeightMode.Unavailable, null, 0, DateTime.MinValue, "");

                UpdateScaleStatusFromWeight(ws0);

                // Fall back to base cadence when eyes aren't configured (prevents getting "stuck" in fast cadence).
                SetDesiredPollSeconds(PollSeconds);

                // Not a “confirmed network issue”, so no toast; keep headline stable.
                UpdateDetail("SOAP endpoint URL not configured", httpShown: "—", stamp: now);
                SetTrayTextSafe($"Scale Eye Monitor: SOAP endpoint URL not configured");
                return;
            }

            if (IsInvalidSoapUrlActiveFor(EyeUrl))
            {
                // Local configuration problem: do not keep retrying the same invalid URL.
                // The state is cleared only when the SOAP endpoint URL setting changes.
                WeightState ws0 = WeightModeEnabled
                                    ? GetWeightStateSnapshot()
                                    : new WeightState(WeightMode.Unavailable, null, 0, DateTime.MinValue, "");

                UpdateScaleStatusFromWeight(ws0);
                SetDesiredPollSeconds(PollSeconds);
                CommitHeadline(HeadlineState.Disconnected, httpShown: "—", stamp: now, detail: InvalidSoapUrlReason, toast: false);
                return;
            }

            // Weight mode snapshot (if enabled)
            WeightState ws = new(WeightMode.Unavailable, null, 0, DateTime.MinValue, "");

            // Keep stable-nonzero fast-window tracking updated
            if (WeightModeEnabled)
            {
                ws = GetWeightStateSnapshot();
                UpdateStableNonZeroFastWindow(ws.Mode, now);
                NoteAlignmentCycleWeight(ws);
            }

            UpdateScaleStatusFromWeight(ws);

            // Decide timing (poll interval + confirm delay) in one place
            var (pollSeconds, confirmDelaySeconds, pauseEyes) = GetTimingFromWeightState(ws);
            SetDesiredPollSeconds(pollSeconds);

            if (pauseEyes)
            {
                ShowMotionPaused(ws, now, httpShown: _lastHttpUi);
                return;
            }

            ShowMotionResumedIfNeeded(ws, now, httpShown: _lastHttpUi);

            bool weightUsable = WeightModeEnabled && ws.Mode != WeightMode.Unavailable;

            // Burst Eye1 only during StableZero.
            // StableNonZero stays "one-shot" (no burst) until the mode changes away from StableNonZero.
            bool useBurstEye1 = weightUsable && ws.Mode == WeightMode.StableZero;

            // ===========================
            // Eye Check #1 (burst or normal)
            // ===========================
            string httpShown;
            bool eye1Val;

            if (useBurstEye1)
            {
                var (successCount, anyFalse, lastSuccess, abortedForMotion, invalidSoapUrl) =
                    await QueryEyeBurstAnyOkAsync(EyeUrl, InputId, WeightBurstCount, WeightBurstDelayMs, token)
                        .ConfigureAwait(false);

                if (invalidSoapUrl)
                    return;

                if (abortedForMotion)
                {
                    SetDesiredPollSeconds(WeightMotionCheckSeconds);
                    var wsM = GetWeightStateSnapshot();
                    UpdateScaleStatusFromWeight(wsM);
                    ShowMotionPaused(wsM, now, httpShown: _lastHttpUi);
                    return;
                }

                if (successCount > 0 && lastSuccess is not null)
                {
                    httpShown = lastSuccess?.HttpText ?? "—";

                    // Any successful FALSE => OK immediately (anti-false-positive)
                    if (anyFalse)
                    {
                        if (DebugLogging)
                            Log($"Eye1 BURST => OK (any false). HTTP='{httpShown}' WeightMode={ws.Mode} w={ws.Weight} range={ws.Range}");
                        NoteConfirmedFalseForAlignmentCycle(GetAlignmentCycleWeightSnapshotOr(ws), "Eye1 burst FALSE");
                        CommitHeadline(HeadlineState.Ok, httpShown, DateTime.Now);
                        return;
                    }

                    // Require at least N successes before treating as TRUE-candidate
                    int minTrue = Math.Max(1, WeightBurstMinTrueSuccess);
                    minTrue = Math.Min(minTrue, WeightBurstCount); // keep it sane vs count

                    if (successCount >= minTrue)
                    {
                        if (DebugLogging)
                            Log($"Eye1 BURST => TRUE-candidate ({successCount}/{WeightBurstCount} successes, no false). HTTP='{httpShown}' WeightMode={ws.Mode} w={ws.Weight} range={ws.Range}");
                    }
                    else
                    {
                        // INCONCLUSIVE (network flicker): do NOT change state, do NOT confirm
                        string msg = $"Inconclusive: {successCount}/{WeightBurstCount} successes (no false) — ignoring";
                        if (DebugLogging)
                            Log($"Eye1 BURST => {msg}. HTTP='{httpShown}' WeightMode={ws.Mode} w={ws.Weight} range={ws.Range}");

                        // While already disconnected, keep the stable disconnected reason visible.
                        // This partial-success/inconclusive message is informational only and does
                        // not represent a recovered or changed headline state.
                        if (_headline != HeadlineState.Disconnected)
                            UpdateDetail(msg, httpShown, now);

                        return;
                    }
                }
                else
                {
                    // 0 successes in burst => run existing failure policy ONCE
                    var eye1 = await QueryEyeWithFailurePolicyAsync(EyeUrl, InputId, "Eye check #1 (burst no success)", token)
                        .ConfigureAwait(false);

                    if (eye1 is null)
                        return;

                    httpShown = eye1?.HttpText ?? "—";
                    eye1Val = eye1?.Value ?? false;

                    if (DebugLogging)
                        Log($"Eye1 (fallback)={eye1Val} HTTP='{httpShown}' WeightMode={ws.Mode} w={ws.Weight} range={ws.Range}");

                    if (!eye1Val)
                    {
                        NoteConfirmedFalseForAlignmentCycle(GetAlignmentCycleWeightSnapshotOr(ws), "Eye1 FALSE");
                        CommitHeadline(HeadlineState.Ok, httpShown, DateTime.Now);
                        return;
                    }
                }
            }
            else
            {
                var eye1 = await QueryEyeWithFailurePolicyAsync(EyeUrl, InputId, "Eye check #1", token)
                    .ConfigureAwait(false);

                if (eye1 is null)
                    return;

                httpShown = eye1?.HttpText ?? "—";
                eye1Val = eye1?.Value ?? false;

                if (DebugLogging)
                    Log($"Eye1={eye1Val} HTTP='{httpShown}' WeightMode={(WeightModeEnabled ? ws.Mode : WeightMode.Unavailable)} w={ws.Weight} range={ws.Range}");

                if (!eye1Val)
                {
                    NoteConfirmedFalseForAlignmentCycle(GetAlignmentCycleWeightSnapshotOr(ws), "Eye1 FALSE");
                    CommitHeadline(HeadlineState.Ok, httpShown, DateTime.Now);
                    return;
                }
            }

            // ===========================
            // Eye1 TRUE candidate => confirm
            // ===========================
            if (DebugLogging)
                Log($"Eye1 TRUE candidate — confirming after {confirmDelaySeconds}s...");

            await Task.Delay(TimeSpan.FromSeconds(confirmDelaySeconds), token).ConfigureAwait(false);

            // If motion starts during confirm, abort (eyes unreliable)
            if (WeightModeEnabled)
            {
                var ws2 = GetWeightStateSnapshot();
                if (ws2.Mode == WeightMode.Unstable)
                {
                    // Motion started while we were waiting; pause eyes (don't commit to Blocked yet).
                    SetDesiredPollSeconds(WeightMotionCheckSeconds);
                    UpdateScaleStatusFromWeight(ws2);

                    if (DebugLogging)
                        Log($"Motion started during confirm: w={ws2.Weight} range={ws2.Range} -> pause eyes");
                    return;
                }
            }

            // Eye Check #2 (confirm) - direct call (no failure-policy wrapper)
            var eye2 = await QueryIsInputOnAsync(EyeUrl, InputId, token).ConfigureAwait(false);
            httpShown = eye2.HttpText;

            if (eye2.Value is null)
            {
                if (DebugLogging)
                    Log($"Eye2 FAILED HTTP='{eye2.HttpText}' Err='{eye2.Error}' Detail='{eye2.Detail}'");

                await QueryEyeWithFailurePolicyAsync(EyeUrl, InputId, "Failure procedure", token)
                    .ConfigureAwait(false);

                return;
            }

            bool eye2Val = eye2.Value.Value;
            if (DebugLogging)
                Log($"Eye2={eye2Val} HTTP='{httpShown}'");

            if (!eye2Val)
            {
                if (DebugLogging)
                    Log("Eye2 FALSE — ignoring Eye1 TRUE (OK).");

                WeightState wsFalse = WeightModeEnabled
                    ? GetWeightStateSnapshot()
                    : ws;

                NoteConfirmedFalseForAlignmentCycle(wsFalse, "Eye2 FALSE");
                CommitHeadline(HeadlineState.Ok, httpShown, DateTime.Now);
                return;
            }

            // Final guard: if motion detected right before commit, ignore
            if (WeightModeEnabled)
            {
                var ws3 = GetWeightStateSnapshot();

                if (ws3.Mode == WeightMode.Unstable)
                {
                    // Motion started while we were working; skip committing Blocked.
                    SetDesiredPollSeconds(WeightMotionCheckSeconds);
                    UpdateScaleStatusFromWeight(ws3);

                    if (DebugLogging)
                        Log($"Motion detected before commit: w={ws3.Weight} range={ws3.Range} -> pause eyes");
                    return;
                }
            }

            // ===========================
            // Confirmed event (commit HEADLINE state)
            // ===========================
            HeadlineState alarmState = HeadlineState.AlignmentOff;

            // Eyes-only presentation covers both explicit eyes-only mode and
            // weight-mode fallback when no usable weight is available. Internally
            // this remains AlignmentOff so the Blocked state keeps its stricter
            // “stable vehicle on scale” meaning.
            bool eyesOnlyAlarm = !WeightModeEnabled;

            // Rule:
            // - A confirmed TRUE at StableZero latches AlignmentOff. If the active
            //   weight cycle crossed the vehicle minimum, the latch records the
            //   best inbound/outbound suspicion for the detail-line suffix.
            // - Blocked only applies to StableNonZero TRUE when that latch is not set.
            // - A confirmed FALSE/OK clears the latch above; non-zero FALSE is also
            //   absolute evidence that the eyes were clear during the active cycle.
            bool useVehicleOnScaleGuidance = false;

            if (WeightModeEnabled)
            {
                var wsCommit = GetWeightStateSnapshot();
                eyesOnlyAlarm = (wsCommit.Mode == WeightMode.Unavailable);

                if (wsCommit.Mode == WeightMode.StableZero)
                {
                    MarkAlignmentOffConfirmedAtStableZero();
                }
                else if (wsCommit.Mode == WeightMode.StableNonZero)
                {
                    if (IsAlignmentOffLatchedAtStableZero())
                    {
                        if (DebugLogging)
                            Log("Blocked suppressed: AlignmentOff was already confirmed at stable zero and no FALSE has cleared it.");
                    }
                    else
                    {
                        alarmState = HeadlineState.Blocked;
                        useVehicleOnScaleGuidance = ShouldUseVehicleOnScaleGuidance(wsCommit);

                        if (!useVehicleOnScaleGuidance && DebugLogging)
                        {
                            Log($"Blocked guidance below minimum weight: w={wsCommit.Weight}, min={BlockedGuidanceMinWeight} -> using alignment guidance.");
                        }

                    }
                }
            }

            eyesOnlyAlarm = (alarmState == HeadlineState.AlignmentOff) && eyesOnlyAlarm;

            if (DebugLogging)
                Log($"Confirmed: {HeadlineToText(alarmState, eyesOnlyAlarm)} (Eye2 TRUE).");

            string? detail = alarmState switch
            {
                HeadlineState.Blocked => useVehicleOnScaleGuidance ? VehicleOnScaleGuidance : AlignmentOffGuidance,
                HeadlineState.AlignmentOff => GetHeadlineGuidance(alarmState),
                _ => null
            };


            CommitHeadline(alarmState, httpShown, DateTime.Now, detail, eyesOnlyAlarm: eyesOnlyAlarm);
        }

        // =====================================================================
        //  Eye1 burst helper (used only in normal weight-mode polling)
        // =====================================================================
        private async Task<(int successCount, bool anyFalse, SoapQueryResult? lastSuccess, bool abortedForMotion, bool invalidSoapUrl)>
            QueryEyeBurstAnyOkAsync(string url, int inputId, int count, int delayMs, CancellationToken token)
        {
            count = Math.Max(1, count);
            delayMs = Math.Max(0, delayMs);

            SoapQueryResult? lastSuccess = null;
            int successCount = 0;

            for (int i = 0; i < count; i++)
            {
                // If weight flips to motion, stop bursting (prevents further eye calls during motion)
                if (WeightModeEnabled && GetWeightStateSnapshot().Mode == WeightMode.Unstable)
                    return (successCount, false, lastSuccess, true, false);

                var q = await QueryIsInputOnAsync(url, inputId, token).ConfigureAwait(false);

                if (IsInvalidSoapUrlResult(q))
                {
                    ForceInvalidSoapUrlFromFailure(url, "Eye check #1 burst", q);
                    return (successCount, false, lastSuccess, false, true);
                }


                if (q.Value is not null)
                {
                    successCount++;
                    lastSuccess = q;

                    if (q.Value.Value == false)
                        return (successCount, true, lastSuccess, false, false);
                }

                if (i < count - 1 && delayMs > 0)
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
            }

            return (successCount, false, lastSuccess, false, false);
        }

        // =====================================================================
        //  Poll interval (dynamic)
        // =====================================================================
        private void SetDesiredPollSeconds(int seconds)
        {
            seconds = Math.Max(1, seconds);

            int prev = System.Threading.Volatile.Read(ref _desiredPollSeconds);
            if (prev == seconds) return;

            System.Threading.Volatile.Write(ref _desiredPollSeconds, seconds);

            if (DebugLogging)
                Log($"Poll interval set to {seconds}s");
        }

        // =====================================================================
        //  Small helpers used by PollOnceAsync
        // =====================================================================
        private bool ShouldUseVehicleOnScaleGuidance(WeightState ws)
        {
            if (ws.Mode != WeightMode.StableNonZero || ws.Weight is not int weight)
                return false;

            return Math.Abs((long)weight) >= BlockedGuidanceMinWeight;
        }

        private WeightState GetAlignmentCycleWeightSnapshotOr(WeightState fallback) =>
            WeightModeEnabled ? GetWeightStateSnapshot() : fallback;

        private bool IsAtOrAboveAlignmentCycleVehicleMin(WeightState ws)
        {
            if (ws.Mode == WeightMode.Unavailable || ws.Mode == WeightMode.StableZero || ws.Weight is not int weight)
                return false;

            return Math.Abs((long)weight) >= BlockedGuidanceMinWeight;
        }

        private void ShowMotionPaused(WeightState ws, DateTime stamp, string httpShown)
        {
            _eyePollingPausedForMotion = true;

            string msg = "Scale in motion, eye polling paused";
            UpdateDetailNoLastPoll(msg, httpShown, stamp);

            if (DebugLogging)
                Log($"Motion: w={ws.Weight} range={ws.Range} -> eye polling paused");
        }

        private void ShowMotionResumedIfNeeded(WeightState ws, DateTime stamp, string httpShown)
        {
            if (!_eyePollingPausedForMotion)
                return;

            _eyePollingPausedForMotion = false;

            if (!WeightModeEnabled || (ws.Mode != WeightMode.StableZero && ws.Mode != WeightMode.StableNonZero))
                return;

            // Clear the stale motion-paused detail as soon as weight is stable again,
            // without showing a separate "polling resumed" message. Restore the exact
            // guidance that was chosen at the last committed headline; this matters when
            // Blocked is using alignment guidance below the configured weight threshold.
            string detail = string.IsNullOrWhiteSpace(_headlineDetail)
                ? GetHeadlineGuidance(_headline)
                : _headlineDetail;

            UpdateDetailNoLastPoll(detail, httpShown, stamp);

            if (DebugLogging)
                Log($"Motion cleared: w={ws.Weight} range={ws.Range} -> eye polling resumed");
        }
    }
}
