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
 *            - Blocked only when weight is StableNonZero at commit time
 *            - Otherwise AlignmentOff
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

        private volatile int _desiredPollSeconds = -1; // scheduler uses this as the base target interval
        private readonly SemaphoreSlim _pollWake = new(0, 1); // wakes the loop to recompute delay
        private long _nextPollDueUtcTicks; // 0 = unset; DateTime.UtcNow.Ticks-based

        // Active "single poll" cancel (Settings apply uses this)
        private CancellationTokenSource? _activePollCts;

        // "Check Now" suppression: prevent immediate loop poll right after manual check
        private int _skipNextLoopPoll; // 0/1

        private void CancelActivePoll()
        {
            try
            {
                _activePollCts?.Cancel();
            }
            catch { }
        }

        // =====================================================================
        //  StableNonZero attention window (prevents fast polling all night)
        // =====================================================================
        private WeightMode _lastWeightModeSeen = WeightMode.Unavailable;
        private DateTime _stableNonZeroFastSince = DateTime.MinValue;
        private bool _stableNonZeroFastActive;

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

                var due = new DateTime(ticks, DateTimeKind.Utc);
                var now = DateTime.UtcNow;
                var delay = due - now;
                if (delay <= TimeSpan.Zero)
                    return;

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var delayTask = Task.Delay(delay, linkedCts.Token);
                var wakeTask = _pollWake.WaitAsync(linkedCts.Token);

                var done = await Task.WhenAny(delayTask, wakeTask).ConfigureAwait(false);
                linkedCts.Cancel(); // cancel the loser so we don't orphan a waiter

                if (done == delayTask)
                    return; // due reached

                // woke => recompute delay using the (possibly updated) due time
            }
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
            _desiredPollSeconds = Math.Max(1, PollSeconds);

            try
            {
                _pollLoopCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
                _pollLoopTask = Task.Run(() => PollLoopAsync(_pollLoopCts.Token));
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
                _ = task.ContinueWith(t => _ = t.Exception,
                    TaskContinuationOptions.OnlyOnFaulted);
        }

        private async Task PollLoopAsync(CancellationToken token)
        {
            // Initial schedule: first poll ~5s after startup
            System.Threading.Interlocked.Exchange(
                ref _nextPollDueUtcTicks,
                DateTime.UtcNow.AddSeconds(5).Ticks);

            while (!token.IsCancellationRequested && !_shutdown)
            {
                await WaitUntilNextDueAsync(token).ConfigureAwait(false);

                await PollOnceSafeAsync(waitForGate: true, fromLoop: true, token).ConfigureAwait(false);

                int seconds = System.Threading.Volatile.Read(ref _desiredPollSeconds);
                if (seconds <= 0) seconds = PollSeconds;
                seconds = Math.Max(1, seconds);

                System.Threading.Interlocked.Exchange(
                    ref _nextPollDueUtcTicks,
                    DateTime.UtcNow.AddSeconds(seconds).Ticks);
            }
        }

        // =====================================================================
        //  Poll entrypoint (gate-safe wrapper)
        // =====================================================================
        private async Task PollOnceSafeAsync(bool waitForGate = false, bool fromLoop = false, CancellationToken token = default)
        {
            if (_shutdown) return;

            try
            {
                if (waitForGate)
                    await _opGate.WaitAsync(token).ConfigureAwait(false);
                else if (!await _opGate.WaitAsync(0, token).ConfigureAwait(false))
                    return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            CancellationTokenSource? pollCts = null;

            try
            {
                if (fromLoop && System.Threading.Interlocked.Exchange(ref _skipNextLoopPoll, 0) == 1)
                    return;

                pollCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken, token);
                _activePollCts = pollCts;

                await PollOnceAsync(pollCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignore (settings cancel or shutdown)
            }
            catch (Exception ex)
            {
                if (_shutdown) return;

                Log($"ERROR: {ex.GetType().Name}: {ex.Message}");
                UpdateDetail($"Error: {ex.Message}", httpShown: "—", stamp: DateTime.Now);
            }
            finally
            {
                if (ReferenceEquals(_activePollCts, pollCts))
                    _activePollCts = null;

                try { pollCts?.Dispose(); } catch { }

                try { _opGate.Release(); } catch { }
            }
        }



        // =====================================================================
        //  Core poll logic
        // =====================================================================
        private async Task PollOnceAsync(CancellationToken token)
        {
            var now = DateTime.Now;

            var url = BuildEyeUrl();
            if (string.IsNullOrWhiteSpace(url))
            {
                // Fall back to base cadence when eyes aren't configured (prevents getting "stuck" in fast cadence).
                SetDesiredPollSeconds(PollSeconds);

                // Not a “confirmed network issue”, so no toast; keep headline stable.
                UpdateDetail("IP not configured", httpShown: "—", stamp: now);
                return;
            }

            // Weight mode snapshot (if enabled)
            WeightState ws = new(WeightMode.Unavailable, null, 0, DateTime.MinValue, "");

            // Keep stable-nonzero fast-window tracking updated
            if (WeightModeEnabled)
            {
                ws = GetWeightStateSnapshot();
                UpdateStableNonZeroFastWindow(ws.Mode, now);
            }
            //else
            //{
            //    ResetStableNonZeroFastWindow();
            //}

            // ===========================
            // Decide timing (poll interval + confirm delay) in one place
            // ===========================
            var (pollSeconds, confirmDelaySeconds, pauseEyes) = GetTimingFromWeightState(ws);
            SetDesiredPollSeconds(pollSeconds);

            if (pauseEyes)
            {
                ShowMotionPaused(ws, now, httpShown: "—", logIfDebug: true);
                return;
            }

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
                var (successCount, anyFalse, lastSuccess, abortedForMotion) =
                    await QueryEyeBurstAnyOkAsync(url, InputId, WeightBurstCount, WeightBurstDelayMs, token);

                if (abortedForMotion)
                {
                    SetDesiredPollSeconds(WeightMotionCheckSeconds);
                    var wsM = GetWeightStateSnapshot();
                    ShowMotionPaused(wsM, now, httpShown: "—", logIfDebug: true);
                    return;
                }

                if (successCount > 0 && lastSuccess is not null)
                {
                    httpShown = lastSuccess.HttpText;

                    // Any successful FALSE => OK immediately (anti-false-positive)
                    if (anyFalse)
                    {
                        LogVerbose($"Eye1 BURST => OK (any false). HTTP='{httpShown}' WeightMode={ws.Mode} w={ws.Weight} range={ws.Range}");
                        SetOkState(httpShown);
                        return;
                    }

                    // Require at least N successes before treating as TRUE-candidate
                    int minTrue = Math.Max(1, WeightBurstMinTrueSuccess);
                    minTrue = Math.Min(minTrue, WeightBurstCount); // keep it sane vs count

                    if (successCount >= minTrue)
                    {
                        eye1Val = true;
                        LogVerbose($"Eye1 BURST => TRUE-candidate ({successCount}/{WeightBurstCount} successes, no false). HTTP='{httpShown}' WeightMode={ws.Mode} w={ws.Weight} range={ws.Range}");
                    }
                    else
                    {
                        // INCONCLUSIVE (network flicker): do NOT change state, do NOT confirm
                        string msg = $"Inconclusive: {successCount}/{WeightBurstCount} successes (no false) — ignoring";
                        if (DebugLogging)
                            Log($"Eye1 BURST => {msg}. HTTP='{httpShown}' WeightMode={ws.Mode} w={ws.Weight} range={ws.Range}");

                        UpdateDetail(msg, httpShown, now);
                        return;
                    }
                }
                else
                {
                    // 0 successes in burst => run existing failure policy ONCE
                    var eye1 = await QueryEyeWithFailurePolicyAsync(url, InputId, "Eye check #1 (burst no success)", token);
                    if (eye1 is null)
                        return;

                    httpShown = eye1.HttpText;
                    eye1Val = eye1.Value!.Value;

                    LogVerbose($"Eye1 (fallback)={eye1Val} HTTP='{httpShown}' WeightMode={ws.Mode} w={ws.Weight} range={ws.Range}");

                    if (!eye1Val)
                    {
                        SetOkState(httpShown);
                        return;
                    }
                }
            }
            else
            {
                var eye1 = await QueryEyeWithFailurePolicyAsync(url, InputId, "Eye check #1", token);
                if (eye1 is null)
                    return;

                httpShown = eye1.HttpText;
                eye1Val = eye1.Value!.Value;

                LogVerbose($"Eye1={eye1Val} HTTP='{httpShown}' WeightMode={(WeightModeEnabled ? ws.Mode : WeightMode.Unavailable)} w={ws.Weight} range={ws.Range}");

                if (!eye1Val)
                {
                    SetOkState(httpShown);
                    return;
                }
            }

            // ===========================
            // Eye1 TRUE candidate => confirm
            // ===========================
            LogVerbose($"Eye1 TRUE candidate — confirming after {confirmDelaySeconds}s...");
            await Task.Delay(TimeSpan.FromSeconds(confirmDelaySeconds), token);

            // If motion starts during confirm, abort (eyes unreliable)
            if (WeightModeEnabled)
            {
                var ws2 = GetWeightStateSnapshot();
                if (ws2.Mode == WeightMode.Unstable)
                {
                    SetDesiredPollSeconds(WeightMotionCheckSeconds);
                    string msg = $"Motion started during confirm: w={ws2.Weight} range={ws2.Range} -> abort confirm";
                    UpdateDetail(msg, httpShown, DateTime.Now);
                    LogVerbose(msg);
                    return;
                }
            }

            // Eye Check #2 (confirm) - direct call (no failure-policy wrapper)
            var eye2 = await QueryIsInputOnAsync(url, InputId, token);
            httpShown = eye2.HttpText;

            if (eye2.Value is null)
            {
                LogVerbose($"Eye2 FAILED HTTP='{eye2.HttpText}' Err='{eye2.Error}' Detail='{eye2.Detail}'");
                await QueryEyeWithFailurePolicyAsync(url, InputId, "Failure procedure", token);
                return;
            }

            bool eye2Val = eye2.Value.Value;
            LogVerbose($"Eye2={eye2Val} HTTP='{httpShown}'");

            if (!eye2Val)
            {
                LogVerbose("Eye2 FALSE — ignoring Eye1 TRUE (OK).");
                SetOkState(httpShown);
                return;
            }

            // Final guard: if motion detected right before commit, ignore
            if (WeightModeEnabled)
            {
                var ws3 = GetWeightStateSnapshot();
                if (ws3.Mode == WeightMode.Unstable)
                {
                    SetDesiredPollSeconds(WeightMotionCheckSeconds);
                    string msg = $"Motion detected before commit: w={ws3.Weight} range={ws3.Range} -> ignore eye TRUE";
                    UpdateDetail(msg, httpShown, DateTime.Now);
                    LogVerbose(msg);
                    return;
                }
            }

            // ===========================
            // Confirmed event (commit HEADLINE state)
            // ===========================
            HeadlineState alarmState = HeadlineState.AlignmentOff;

            // Rule:
            // - Blocked only when weight is StableNonZero at commit time.
            // - If weight returns to StableZero and eyes are still TRUE, next confirmed commit becomes AlignmentOff.
            if (WeightModeEnabled)
            {
                var wsCommit = GetWeightStateSnapshot();
                if (wsCommit.Mode == WeightMode.StableNonZero)
                    alarmState = HeadlineState.Blocked;
            }

            bool changed = (_headline != alarmState);

            if (changed)
                Log($"Confirmed: {HeadlineToText(alarmState)} (Eye2 TRUE).");
            else
                LogVerbose($"Confirmed: {HeadlineToText(alarmState)} (Eye2 TRUE).");

            CommitHeadline(alarmState, httpShown, DateTime.Now);
        }

        // =====================================================================
        //  Eye1 burst helper (used only in normal weight-mode polling)
        // =====================================================================
        private async Task<(int successCount, bool anyFalse, SoapQueryResult? lastSuccess, bool abortedForMotion)>
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
                    return (successCount, false, lastSuccess, true);

                var q = await QueryIsInputOnAsync(url, inputId, token).ConfigureAwait(false);

                if (q.Value is not null)
                {
                    successCount++;
                    lastSuccess = q;

                    if (q.Value.Value == false)
                        return (successCount, true, lastSuccess, false);
                }

                if (i < count - 1 && delayMs > 0)
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
            }

            return (successCount, false, lastSuccess, false);
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
            LogVerbose($"Poll interval set to {seconds}s");
        }

        // =====================================================================
        //  Small helpers used by PollOnceAsync
        // =====================================================================
        private string BuildEyeUrl()
        {
            return string.IsNullOrWhiteSpace(IpAddress) ? "" : $"http://{IpAddress}/Service.asmx";
        }

        private void SetOkState(string httpShown)
        {
            // Use the actual moment we set OK (important after confirm delays).
            var stamp = DateTime.Now;

            bool changed = (_headline != HeadlineState.Ok);

            CommitHeadline(HeadlineState.Ok, httpShown, stamp);

            if (changed)
                Log("Headline -> OK.");
            else
                LogVerbose("Headline -> OK.");
        }

        private void ShowMotionPaused(WeightState ws, DateTime stamp, string httpShown, bool logIfDebug)
        {
            string msg = $"Motion: w={ws.Weight} range={ws.Range} -> eye polling paused";
            UpdateDetail(msg, httpShown, stamp);
            if (logIfDebug && DebugLogging) Log(msg);
        }
    }
}
