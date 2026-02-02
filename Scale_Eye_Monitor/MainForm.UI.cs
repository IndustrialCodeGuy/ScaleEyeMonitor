using System.Diagnostics;

namespace Scale_Eye_Monitor
{
    /*
 * MainForm.Ui.cs
 * --------------
 * Partial: MainForm (Scale Eye Monitor)
 *
 * Responsibilities:
 *   - “Check Now” behavior (manual poll):
 *       * Non-blocking gate check (skip if a poll is already running)
 *       * Runs PollOnceAsync under the same gate used by the scheduler
 *       * Reschedules the next loop poll from “now” to avoid immediate double-polls
 *   - Window helpers (show/activate status window from tray click)
 *   - Unified presentation helpers:
 *       * CommitHeadline(...) updates:
 *           - Status label + detail label + HTTP + Last Poll
 *           - Tray tooltip text
 *           - Tray + window icons
 *           - Toast/balloon on headline transitions only
 *       * UpdateDetail(...) updates only the informational labels (no tray/icon/toast churn)
 *   - Toast/balloon fallback:
 *       * Toast is preferred when toast shortcut/AUMID is configured
 *       * Balloon is fallback only when toast cannot be shown
 *
 * Notes:
 *   - HeadlineState is the single source of truth for user-visible state.
 *   - Methods are UI-thread safe (BeginInvoke/Invoke guards).
 */

    public sealed partial class MainForm
    {
        private bool _alwaysOnTop;
        private ToolStripMenuItem? _miAlwaysOnTop;

        // =====================================================================
        //  "Check Now" / manual poll behavior
        // =====================================================================

        private async void CheckNow_Click(object? sender, EventArgs e)
        {
            if (_shutdown) return;

            // Non-blocking: if a poll is already running, do nothing.
            if (!await _opGate.WaitAsync(0))
                return;

            CancellationTokenSource? pollCts = null;

            try
            {
                pollCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
                _activePollCts = pollCts;

                await PollOnceAsync(pollCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Ignore (settings cancel / shutdown)
            }
            catch (Exception ex)
            {
                Log($"CheckNow ERROR: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_activePollCts, pollCts))
                    _activePollCts = null;

                try { pollCts?.Dispose(); } catch { }

                // Prevent the poll loop from immediately running a due poll right after Check Now.
                System.Threading.Interlocked.Exchange(ref _skipNextLoopPoll, 1);

                // Anchor next scheduled poll from now (prevents a quick extra poll).
                RescheduleNextPollFromNow(pollSoon: true);

                try { _opGate.Release(); } catch { }
            }
        }

        // =====================================================================
        //  Window helpers
        // =====================================================================
        private void ShowStatusWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void SetAlwaysOnTop(bool enabled)
        {
            _alwaysOnTop = enabled;
            TopMost = enabled;

            if (_miAlwaysOnTop is not null)
                _miAlwaysOnTop.Text = enabled ? " Always on top  ✔" : " Always on top";
        }

        private void ToggleAlwaysOnTop()
        {
            SetAlwaysOnTop(!_alwaysOnTop);
            SaveAlwaysOnTopSetting();
        }

        private void SaveAlwaysOnTopSetting()
        {
            try
            {
                // Preserve all other settings keys; just update this one.
                var s = AppSettings.LoadOrDefaults(SettingsPath, out _);
                s.AlwaysOnTop = _alwaysOnTop;
                s.Save(SettingsPath);
            }
            catch (Exception ex)
            {
                Log($"AlwaysOnTop save failed: {ex.Message}");
            }
        }

        // =====================================================================
        //  UI updates (headline + detail)
        // =====================================================================
        private static string HeadlineToText(HeadlineState s) => s switch
        {
            HeadlineState.Ok => "OK",
            HeadlineState.Blocked => "Blocked",
            HeadlineState.AlignmentOff => "Alignment off",
            HeadlineState.Disconnected => "Disconnected",
            _ => "(initializing…)"
        };

        // Call ONLY when a confirmed headline event happens (OK/Blocked/AlignmentOff/Disconnected).
        // This keeps UI + tray + toast fully unified and stable.
        private void CommitHeadline(HeadlineState state, string httpShown, DateTime stamp, string? detail = null, bool toast = true)
        {
            if (_shutdown) return;

            void Run()
            {
                if (_shutdown) return;

                bool changed = (_headline != state);

                // Always refresh the window text (even if headline didn't change)
                lblStatus.Text = $"Status: {HeadlineToText(state)}";

                var httpUi = NormalizeHttpShown(httpShown, out var extra);
                var mergedDetail = MergeDetail(detail, extra);

                const int maxLen = 300;
                if (mergedDetail.Length > maxLen) mergedDetail = mergedDetail.Substring(0, maxLen) + "…";

                // Non-debug: only log when UI-relevant values change (prevents per-poll repeats)
                var sig = $"H|{state}|{httpUi}|{mergedDetail}";
                var msg = $"State: {HeadlineToText(state)} | HTTP: {httpUi}"
                          + (string.IsNullOrWhiteSpace(mergedDetail) ? "" : $" | {mergedDetail}");

                LogUiChangeIfNeeded(sig, msg);

                lblDetail.Text = mergedDetail;
                lblHttp.Text = $"HTTP Code: {httpUi}";
                lblLast.Text = $"Last Poll: {stamp:hh:mm:ss tt}";

                // Only change tray/icons/toast when the headline actually changes
                if (changed)
                {
                    _headline = state;

                    // Icons are part of the “headline” presentation.
                    SetIcons(state == HeadlineState.Ok ? iconConnected : iconDisconnected);

                    // Tray hover text is stable headline-only (no detail churn).
                    SetTrayTextSafe($"Scale Eye Monitor ({LocationName}) {HeadlineToText(state)}");

                    // Leaving “forced disconnect” bookkeeping behind once we have a real headline.
                    if (state != HeadlineState.Disconnected)
                        ClearForcedReason();

                    if (toast && state != HeadlineState.Unknown)
                        ShowHeadlineToastOrBalloon(state);
                }
            }

            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                if (!IsHandleCreated) return;
                try { BeginInvoke((Action)Run); } catch { }
            }
            else Run();
        }

        // Call for informational UI only (no toasts, no tray text, no icon changes).
        private void UpdateDetail(string detail, string httpShown, DateTime stamp)
        {
            if (_shutdown) return;

            void Run()
            {
                if (_shutdown) return;

                var httpUi = NormalizeHttpShown(httpShown, out var extra);
                var mergedDetail = MergeDetail(detail, extra);

                const int maxLen = 300;
                if (mergedDetail.Length > maxLen) mergedDetail = mergedDetail.Substring(0, maxLen) + "…";

                // Non-debug: only log when informational UI changes (prevents per-poll repeats)
                var sig = $"D|{httpUi}|{mergedDetail}";
                var msg = $"Detail: HTTP: {httpUi}"
                          + (string.IsNullOrWhiteSpace(mergedDetail) ? "" : $" | {mergedDetail}");

                LogUiChangeIfNeeded(sig, msg);

                lblDetail.Text = mergedDetail;
                lblHttp.Text = $"HTTP Code: {httpUi}";
                lblLast.Text = $"Last Poll: {stamp:hh:mm:ss tt}";
            }

            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                if (!IsHandleCreated) return;
                try { BeginInvoke((Action)Run); } catch { }
            }
            else Run();
        }

        private void SetTrayTextSafe(string text)
        {
            if (_shutdown) return;

            void Set()
            {
                if (_shutdown) return;

                if (string.IsNullOrWhiteSpace(text))
                    text = "Scale Eye Monitor";

                text = text.Replace('\r', ' ').Replace('\n', ' ');

                // NotifyIcon.Text max length is 63 chars (hard limit; exceptions are common if exceeded)
                const int max = 63;
                if (text.Length > max)
                    text = text.Substring(0, max - 1) + "…";

                try { tray.Text = text; } catch { }
            }

            if (InvokeRequired)
            {
                if (!IsHandleCreated) return;
                try { BeginInvoke((Action)Set); } catch { }
            }
            else Set();
        }

        private static string NormalizeHttpShown(string httpShown, out string? extraDetail)
        {
            extraDetail = null;

            if (string.IsNullOrWhiteSpace(httpShown))
                return "—";

            var s = httpShown.Trim();
            if (s == "—")
                return "—";

            // Accept common status formats and keep them in the HTTP row:
            //   "200 OK"
            //   "HTTP 200 OK"
            //   "HTTP/1.1 200 OK"
            // Anything else -> treat as "extraDetail" and show "—" for HTTP.
            int codeIdx = FindHttpStatusCodeIndex(s);
            if (codeIdx >= 0)
                return s.Substring(codeIdx).Trim();   // keeps "200 OK" (drops "HTTP/1.1 " etc)

            extraDetail = s;
            return "—";
        }

        private static int FindHttpStatusCodeIndex(string s)
        {
            // Find a 3-digit code in range 100–599, preferring early matches.
            // We allow it to appear after prefixes like "HTTP/1.1 ".
            for (int i = 0; i + 2 < s.Length; i++)
            {
                char a = s[i], b = s[i + 1], c = s[i + 2];
                if (!char.IsDigit(a) || !char.IsDigit(b) || !char.IsDigit(c)) continue;

                // boundary check: avoid matching part of a longer number
                if (i > 0 && char.IsDigit(s[i - 1])) continue;
                if (i + 3 < s.Length && char.IsDigit(s[i + 3])) continue;

                int code = (a - '0') * 100 + (b - '0') * 10 + (c - '0');
                if (code >= 100 && code <= 599)
                    return i;
            }
            return -1;
        }

        private static string MergeDetail(string? detail, string? extra)
        {
            detail = detail?.Trim();
            extra = extra?.Trim();

            if (string.IsNullOrEmpty(extra)) return detail ?? "";
            if (string.IsNullOrEmpty(detail)) return extra;
            return $"{detail} — {extra}";
        }


        // =====================================================================
        //  Tray / window icons
        // =====================================================================
        private void SetIcons(Icon icon)
        {
            if (_shutdown) return;

            void Set()
            {
                if (_shutdown) return;

                try
                {
                    tray.Icon = icon;
                    this.Icon = icon;

                    // Toggle visibility to force refresh in some shells
                    tray.Visible = false;
                    tray.Visible = true;
                }
                catch { }
            }

            if (IsDisposed || Disposing) return;   
            
            if (InvokeRequired)
            {
                if (!IsHandleCreated) return;
                try { BeginInvoke((Action)Set); } catch { }
            }
            else Set();
        }

        // =====================================================================
        //  Notifications (toast preferred; balloon fallback) - HEADLINE ONLY
        // =====================================================================
        private void ShowHeadlineToastOrBalloon(HeadlineState state)
        {
            if (_shutdown) return;

            void Run()
            {
                if (_shutdown) return;

                string title = "Scale Eye Monitor";
                string text = $"Input {InputId} is now {HeadlineToText(state)}";

                try
                {
                    tray.BalloonTipTitle = title;
                    tray.BalloonTipText = text;
                }
                catch { }

                bool toastShown = false;

                // Prefer toast (matches the “main icon” branding behavior when AUMID+shortcut are correct)
                try
                {
                    string headerPng = Path.Combine(
                        Application.StartupPath,
                        state == HeadlineState.Ok ? "ScaleEye_Connected.png" : "ScaleEye_Disconnected.png");

                    // ToastHelper currently requires a valid absolute path.
                    if (File.Exists(headerPng))
                    {
                        ToastHelper.ShowToast(title, text, headerPng);
                        toastShown = true;
                    }
                }
                catch
                { } // ignore

                // Fallback only if toast wasn't shown
                if (!toastShown)
                {
                    try { tray.ShowBalloonTip(3000); } catch { }
                }
            }

            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                if (!IsHandleCreated) return;
                try { BeginInvoke((Action)Run); } catch { }
            }
            else Run();
        }

        // =====================================================================
        //  Misc UX helpers
        // =====================================================================
        private void OpenReadme()
        {
            try
            {
                string path = Path.Combine(Application.StartupPath, "README.txt");

                if (!File.Exists(path))
                {
                    MessageBox.Show(
                        $"Readme not found:\n{path}\n\nAdd README.txt next to the exe.",
                        "Scale Eye Monitor",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log($"OpenReadme failed: {ex.Message}");
                MessageBox.Show(ex.Message, "Scale Eye Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
