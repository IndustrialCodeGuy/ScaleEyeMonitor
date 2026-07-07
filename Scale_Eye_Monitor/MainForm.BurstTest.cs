using System.Diagnostics;

namespace Scale_Eye_Monitor
{
    /*
     * MainForm.BurstTest.cs
     * ---------------------
     * Partial: MainForm (Scale Eye Monitor)
     *
     * Responsibility:
     *   Implements the “Burst Test” diagnostic run launched from SettingsForm.
     *
     * What it does:
     *   - Runs N SOAP eye queries (IsInputOn) with an optional delay between calls
     *   - Logs per-iteration timing + HTTP/error details
     *   - Reports status back to SettingsForm via IProgress<BurstMsg>
     *   - Supports Cancel (re-click button or closing dialog) and shutdown cancellation
     *
     * Notes:
     *   - This partial is the only place that wires SettingsForm burst UI events.
     *   - The underlying SOAP call is in MainForm.EyeSoap.cs (QueryIsInputOnAsync).
     */

    public sealed partial class MainForm
    {
        // =====================================================================
        //  Burst test state
        // =====================================================================
        private CancellationTokenSource? _burstCts;
        private Task? _burstTask;

        // True while waiting-for-poll OR actively running the burst loop.
        private bool IsBurstRunning => _burstCts is not null;

        // =====================================================================
        //  Progress message types (SettingsForm status box)
        // =====================================================================
        private enum BurstMsgKind { Status, Detail }
        private readonly record struct BurstMsg(BurstMsgKind Kind, string Text);

        // =====================================================================
        //  Wiring: SettingsForm handlers
        // =====================================================================
        private void WireBurstTest(SettingsForm dlg)
        {
            // If dialog closes while a burst is running, cancel it.
            dlg.FormClosing += (_, __) =>
            {
                if (IsBurstRunning) CancelBurstTest();
            };

            dlg.BurstTestHandler = () =>
            {
                if (_shutdown) return;

                // If running, treat as Cancel immediately (don't validate fields).
                if (IsBurstRunning)
                {
                    CancelBurstTest();
                    return;
                }

                string url = dlg.EyeUrlValue;
                if (string.IsNullOrWhiteSpace(url))
                {
                    MessageBox.Show(dlg, "SOAP endpoint URL cannot be empty.", "Burst Test", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                int inputId = dlg.InputIdValue;
                int count = Math.Clamp(dlg.BurstTestCountValue, 1, 500);
                int delayMs = Math.Clamp(dlg.BurstTestDelayMsValue, 0, 5000);

                string label = $"{count}x/{delayMs}ms";

                // Clear previous output for each run.
                dlg.SetBurstStatus("", append: false);

                IProgress<BurstMsg> progress = BuildBurstProgress(dlg);

                _ = ToggleBurstTestFromDialogAsync(dlg, url, inputId, count, delayMs, label, progress);
            };
        }

        private static IProgress<BurstMsg> BuildBurstProgress(SettingsForm dlg)
        {
            if (!dlg.DebugLoggingValue)
            {
                // Non-debug: show ONLY high-level status lines (replace)
                return new Progress<BurstMsg>(m =>
                {
                    if (dlg.IsDisposed || dlg.Disposing) return;

                    if (m.Kind == BurstMsgKind.Status)
                        dlg.SetBurstStatus(m.Text, append: false);
                });
            }

            // Debug: append everything
            return new Progress<BurstMsg>(m =>
            {
                if (dlg.IsDisposed || dlg.Disposing) return;
                dlg.SetBurstStatus(m.Text, append: true);
            });
        }

        // =====================================================================
        //  Entry points (toggle + cancel)
        // =====================================================================
        private void CancelBurstTest()
        {
            try { _burstCts?.Cancel(); } catch { }
        }

        private async Task ToggleBurstTestFromDialogAsync(SettingsForm dlg, string url, int inputId, int count, int delayMs, string labelSuffix, IProgress<BurstMsg> progress)
        {
            // Caller guarantees: not shutting down, and not already running.
            // (The SettingsForm burst button handler cancels when running.)

            // Create CTS FIRST so "Cancel" can cancel the wait-for-poll too.
            _burstCts?.Dispose();
            _burstCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
            var token = _burstCts.Token;

            bool gateHeld = false;

            // Lock the dialog except status + the burst button (Cancel).
            dlg.SetUiLocked(locked: true, allowBurstButton: true);
            dlg.SetBurstRunning(true);

            try
            {
                // Only show "waiting" if we actually have to wait for the gate.
                if (_opGate.Wait(0))
                {
                    gateHeld = true;
                }
                else
                {
                    progress.Report(new(BurstMsgKind.Status, "Waiting for current poll to finish…"));
                    CancelActivePoll();
                    await _opGate.WaitAsync(token).ConfigureAwait(true);
                    gateHeld = true;
                }

                _burstTask = RunBurstTestAsync(url, inputId, count, delayMs, labelSuffix: labelSuffix, progress: progress, token: token);

                await _burstTask.ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                progress.Report(new(BurstMsgKind.Status, string.IsNullOrWhiteSpace(labelSuffix) ? "Burst test canceled" : $"Burst test ({labelSuffix}) canceled"));
            }
            catch (Exception ex)
            {
                Log($"Burst test ERROR: {ex.GetType().Name}: {ex.Message}");
                try
                {
                    progress.Report(new(BurstMsgKind.Status, $"Burst test error: {ex.Message}"));
                }
                catch { }
            }
            finally
            {
                if (gateHeld)
                {
                    try { _opGate.Release(); } catch { }
                }

                try { if (!dlg.IsDisposed) dlg.SetBurstRunning(false); } catch { }
                try { if (!dlg.IsDisposed) dlg.SetUiLocked(locked: false, allowBurstButton: false); } catch { }

                _burstTask = null;

                try { _burstCts?.Dispose(); } catch { }
                _burstCts = null;
            }
        }

        // =====================================================================
        //  Burst wrapper (friendly status + summary)
        // =====================================================================
        private async Task RunBurstTestAsync(string url, int inputId, int count, int delayMs, string labelSuffix = "", IProgress<BurstMsg>? progress = null, CancellationToken token = default)
        {
            if (_shutdown) return;

            if (string.IsNullOrWhiteSpace(url))
            {
                progress?.Report(new(BurstMsgKind.Status, "Burst test: SOAP endpoint URL not configured"));
                return;
            }

            string label = string.IsNullOrWhiteSpace(labelSuffix) ? "Burst test" : $"Burst test ({labelSuffix})";

            try
            {
                progress?.Report(new(BurstMsgKind.Status, $"{label} running…"));

                var (ok, fail, canceled, _, wasCanceled) =
                    await RunEyeBurstTestAsync(url, inputId, count, delayMs, progress, token).ConfigureAwait(false);

                string verb = wasCanceled ? "Canceled" : "Complete";
                string canceledPart = wasCanceled ? $", canceled={canceled}" : "";
                string summary = $"{label} {verb} (ok={ok}, fail={fail}{canceledPart})";

                progress?.Report(new(BurstMsgKind.Status, summary));
            }
            catch (Exception ex)
            {
                Log($"{label} ERROR: {ex.GetType().Name}: {ex.Message}");
                progress?.Report(new(BurstMsgKind.Status, $"{label} error: {ex.Message}"));
            }
        }

        // =====================================================================
        //  Burst worker (raw loop)
        // =====================================================================
        private async Task<(int ok, int fail, int canceled, double totalSeconds, bool wasCanceled)> RunEyeBurstTestAsync(string url, int inputId, int count, int delayMs, IProgress<BurstMsg>? progress = null, CancellationToken token = default)
        {
            Log($"BurstTest START input={inputId} count={count} delayMs={delayMs}");
            var totalSw = Stopwatch.StartNew();

            int ok = 0;
            int fail = 0;
            bool wasCanceled = false;

            try
            {
                for (int i = 1; i <= count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (_shutdown) break;

                    var sw = Stopwatch.StartNew();
                    var q = await QueryIsInputOnAsync(url, inputId, token).ConfigureAwait(false);
                    sw.Stop();

                    bool success = (q.Value is not null);
                    if (success) ok++; else fail++;

                    string val = q.Value is null ? "NULL" : (q.Value.Value ? "TRUE" : "FALSE");

                    string line = $"BurstTest {i}/{count} @ {DateTime.Now:HH:mm:ss.fff}: {val} {sw.ElapsedMilliseconds}ms  HTTP='{q.HttpText}'  Err='{q.Error}'  Detail='{q.Detail}'";

                    Log(line);

                    progress?.Report(new(
                        BurstMsgKind.Detail,
                        $"Burst {i}/{count}: {val} ({sw.ElapsedMilliseconds}ms)  HTTP='{q.HttpText}'  Err='{q.Error}'"));

                    if (i < count && delayMs > 0)
                        await Task.Delay(delayMs, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                wasCanceled = true;
            }
            finally
            {
                totalSw.Stop();
            }

            int canceled = Math.Max(0, count - (ok + fail));
            string endWord = wasCanceled ? "CANCELED" : "END";

            Log($"BurstTest {endWord} (total {totalSw.Elapsed.TotalSeconds:0.000}s)");

            progress?.Report(new(BurstMsgKind.Detail, $"BurstTest {endWord} (total {totalSw.Elapsed.TotalSeconds:0.000}s)"));

            return (ok, fail, canceled, totalSw.Elapsed.TotalSeconds, wasCanceled);
        }
    }
}
