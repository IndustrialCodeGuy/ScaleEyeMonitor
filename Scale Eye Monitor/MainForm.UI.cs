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
     *       * Runs PollOnceSafeAsync(...) under the same gate used by the scheduler
     *       * Reschedules the next loop poll from “now” to avoid immediate double-polls
     *   - Window helpers (show/activate status window from tray click)
     *   - Startup / tray UX toggles:
     *       * Start with Windows (HKCU Run key) toggle from tray
     *       * Start in tray (persisted setting) toggle from tray
     *       * Always on top toggle from tray (persisted setting; main window only)
     *       * Enable notifications toggle from tray (persisted setting)
     *         - “Notification Duration” (Short/Long/Disabled) is set in Settings and stays in sync
     *   - Unified presentation helpers:
     *       * CommitHeadline(...) updates:
     *           - Eye status label + detail label + HTTP + Last Poll
     *           - Tray tooltip text
     *           - Tray + window icons
     *           - Toast/balloon on headline transitions only
     *       * UpdateDetail(...) updates only the informational labels (no tray/icon/toast churn)
     *       * UpdateScaleStatusFromWeight(...) updates the Scale Status label (weight mode summary).
     *   - Notifications:
     *       * Headline transitions: toast preferred; balloon fallback if toast cannot be shown
     *       * Warning/info messages: balloon-only via ToastHelper.ShowWarningToast/ShowInfoToast
     *       * “Notification Duration” controls toast duration (short/long) and balloon timeout
     *       * “Disabled” turns off all notifications (tray + settings stay synced)
     *
     * Notes:
     *   - HeadlineState is the single source of truth for user-visible state.
     *   - Methods are UI-thread safe (BeginInvoke/Invoke guards).
     *   - Tray tooltip text is capped to NotifyIcon.Text's 63-character limit.
     *   - Detail text is truncated for UI/log hygiene (currently 300 chars) and may be visually ellipsized.
     *   - Toasts are suppressed while the session is locked (coalesced and optionally shown on unlock).
     */

    public sealed partial class MainForm
    {
        private bool _alwaysOnTop;
        private ToolStripMenuItem? _miAlwaysOnTop;

        private bool _notificationsEnabled = true;
        private string _notificationDuration = "Short"; // "Short" or "Long"
        private int _notificationBalloonMs = 7000;      // 7000 short, 25000 long

        private ToolStripMenuItem? _miStartWithWindows;
        private ToolStripMenuItem? _miStartInTray;
        private ToolStripMenuItem? _miEnableNotifications;

        // UI remembers the last displayed HTTP status so motion-paused updates don't overwrite it.
        private string _lastHttpUi = "—";

        private static bool HasHttpCodeUi(string httpUi)
        {
            return !string.IsNullOrWhiteSpace(httpUi) &&
                   !string.Equals(httpUi.Trim(), "—", StringComparison.Ordinal);
        }

        private void RefreshScaleStatusFromCurrentWeight()
        {
            WeightState ws = WeightModeEnabled
                ? GetWeightStateSnapshot()
                : new WeightState(WeightMode.Unavailable, null, 0, DateTime.MinValue, "");

            UpdateScaleStatusFromWeight(ws);
        }

        // Cached "Scale Status" text shown on the top row.
        private string _scaleStatusUi = "—";

        // Last sanitized NotifyIcon.Text value; avoids repeatedly assigning the same tooltip.
        private string _lastTrayTextUi = "";

        // Last committed base detail text, before HTTP details are merged in. Used so
        // temporary informational messages (for example, motion paused) can restore
        // the same guidance that was chosen at commit time.
        private string _headlineDetail = "";

        // =====================================================================
        //  UI-thread marshaling (dedupe helper)
        // =====================================================================
        private void UiSafe(Action action)
        {
            if (_shutdown) return;
            if (IsDisposed || Disposing) return;

            void Wrapped()
            {
                if (_shutdown) return;
                action();
            }

            if (InvokeRequired)
            {
                if (!IsHandleCreated) return;
                try { BeginInvoke((Action)Wrapped); } catch { }
            }
            else
            {
                Wrapped();
            }
        }

        private const int DetailUiMaxLen = 300;

        // Interprets the "httpShown" contract used by polling:
        // - "-" means "leave HTTP row unchanged" (used while motion pauses eye calls).
        // - otherwise normalize and update the cached UI value.
        private string GetHttpUi(string httpShown, out string? extraDetail)
        {
            var hs = httpShown.AsSpan().Trim();
            if (hs.Length == 1 && hs[0] == '-')
            {
                extraDetail = null;
                return _lastHttpUi;
            }

            var httpUi = NormalizeHttpShown(httpShown, out extraDetail);
            _lastHttpUi = httpUi;
            return httpUi;
        }

        private static string TruncateForUi(string s, int maxLen)
        {
            if (s.Length <= maxLen) return s;
            return string.Concat(s.AsSpan(0, maxLen), "…");
        }

        private const int NotifyIconTextMaxLen = 63;
        private const char TrayTooltipLineBreak = '\r';

        private const string OkGuidance = "Eyes are clear";
        private const string AlignmentOffGuidance = "Check alignment or clear obstruction";
        private const string VehicleOnScaleGuidance = "Confirm vehicle is fully on scale";

        private static string GetHeadlineGuidance(HeadlineState state) => state switch
        {
            HeadlineState.Ok => OkGuidance,
            HeadlineState.Blocked => VehicleOnScaleGuidance,
            HeadlineState.AlignmentOff => AlignmentOffGuidance,
            _ => ""
        };

        private static string SanitizeNotifyIconText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                text = "Scale Eye Monitor";

            // NotifyIcon.Text has a 63-character limit; assigning longer text can throw.
            text = NormalizeNotifyIconLineBreaks(text);

            if (text.Length <= NotifyIconTextMaxLen)
                return text;

            int limit = NotifyIconTextMaxLen - 1;
            while (limit > 0 && (text[limit - 1] == '\r' || text[limit - 1] == '\n'))
                limit--;

            return string.Concat(text.AsSpan(0, limit), "…");
        }

        private static string NormalizeNotifyIconLineBreaks(string text)
        {
            if (text.IndexOf('\r') < 0 && text.IndexOf('\n') < 0)
                return text;

            var sb = new System.Text.StringBuilder(text.Length);
            bool inLineBreak = false;

            foreach (char ch in text)
            {
                if (ch == '\r' || ch == '\n')
                {
                    if (!inLineBreak)
                    {
                        sb.Append(TrayTooltipLineBreak);
                        inLineBreak = true;
                    }
                    continue;
                }

                sb.Append(ch);
                inLineBreak = false;
            }

            return sb.ToString();
        }

        // =====================================================================
        //  "Check Now" / manual poll behavior
        // =====================================================================
        private async void CheckNow_Click(object? sender, EventArgs e)
        {
            // Non-blocking: if a poll is already running, do nothing.
            bool ran = await PollOnceSafeAsync(
                waitForGate: false,
                fromLoop: false,
                token: _lifetimeToken,
                debugGateUnavailable: true,
                suppressNextLoopPoll: true)
            .ConfigureAwait(true);

            if (!ran) return;

            // Skip/anchoring is handled inside PollOnceSafeAsync while it still owns _opGate.
        }

        // =====================================================================
        //  Window helpers
        // =====================================================================
        private void ShowStatusWindow()
        {
            _suppressInitialShow = false;
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void SetAlwaysOnTop(bool enabled)
        {
            _alwaysOnTop = enabled;
            // Never force the Settings dialog to behave "always on top".
            TopMost = enabled && !_settingsDialogOpen;

            if (_miAlwaysOnTop is not null)
                _miAlwaysOnTop.Text = WithCheck(enabled, " Always on top");
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
        private static string HeadlineToText(HeadlineState s, bool eyesOnlyAlarm = false) => s switch
        {
            HeadlineState.Ok => "OK",
            HeadlineState.Blocked => "Blocked",
            HeadlineState.AlignmentOff => eyesOnlyAlarm ? "Obstructed" : "Alignment off",
            HeadlineState.Disconnected => "Disconnected",
            _ => "Loading"
        };

        private string FormatTrayTooltip(HeadlineState state, string? detail, bool eyesOnlyAlarm = false)
        {
            detail = detail?.Trim();

            if (!string.IsNullOrWhiteSpace(detail))
            {
                return $"Eye Status: {HeadlineToText(state, eyesOnlyAlarm)}{TrayTooltipLineBreak}{detail}";
            }

            return $"Eye Status: {HeadlineToText(state, eyesOnlyAlarm)}";
        }

        private string FormatScaleStatus(WeightState ws)
        {
            if (!WeightModeEnabled)
                return "—";

            // Keep startup/loading and eye-side disconnected states visually distinct
            // from a weight-device outage. Until the eye HTTP row has a real value,
            // the scale status is not meaningful yet. When the eyes are disconnected,
            // weight monitoring is intentionally stopped, so keep the scale side as "—".
            if (!HasHttpCodeUi(_lastHttpUi) || _forcedDisconnectActive || _headline == HeadlineState.Disconnected)
                return "—";

            return ws.Mode switch
            {
                WeightMode.Unavailable => "Unavailable",
                WeightMode.Unstable => "In motion",
                WeightMode.StableZero => "Zero",
                WeightMode.StableNonZero => "Stable",
                _ => "—"
            };
        }

        private void UpdateScaleStatusFromWeight(WeightState ws)
        {
            var next = FormatScaleStatus(ws);
            if (string.Equals(next, _scaleStatusUi, StringComparison.Ordinal))
                return;

            // Update cached text first so any other UI updates that run shortly after
            // (headline/detail) can reflect the latest scale status.
            _scaleStatusUi = next;

            void Run()
            {
                lblScaleStatus.Text = $"Scale Status: {next}";
            }

            UiSafe(Run);
        }

        // Call ONLY when a confirmed headline event happens (OK/Blocked/AlignmentOff/Disconnected).
        // This keeps UI + tray + toast fully unified and stable.
        private void CommitHeadline(
            HeadlineState state,
            string httpShown,
            DateTime stamp,
            string? detail = null,
            bool toast = true,
            bool eyesOnlyAlarm = false)
        {
            void Run()
            {
                // Only AlignmentOff has an eyes-only alternate label.
                eyesOnlyAlarm = (state == HeadlineState.AlignmentOff) && eyesOnlyAlarm;

                bool changed = (_headline != state);
                bool forceToast = _forceNextHeadlineToast;
                _forceNextHeadlineToast = false;

                string headlineText = HeadlineToText(state, eyesOnlyAlarm);

                // Always refresh the window text (even if headline didn't change).
                lblStatus.Text = $"Eye Status: {headlineText}";

                string httpUi = GetHttpUi(httpShown, out var extra);
                detail = string.IsNullOrWhiteSpace(detail)
                    ? GetHeadlineGuidance(state)
                    : detail.Trim();

                // Inbound/outbound suspicion is supporting detail, not part of the
                // Eye Status headline. Keep it on the second/details line.
                if (state == HeadlineState.AlignmentOff && !eyesOnlyAlarm)
                    detail = $"{detail}{GetAlignmentSuspectDetailSuffix()}";

                _headlineDetail = detail;
                var mergedDetail = MergeDetail(detail, extra);
                mergedDetail = TruncateForUi(mergedDetail, DetailUiMaxLen);

                // Log when UI-relevant values change (prevents per-poll repeats).
                var sig = $"H|{state}|{eyesOnlyAlarm}|{httpUi}|{mergedDetail}";
                var msg = $"State: {headlineText} | HTTP: {httpUi}"
                                          + (string.IsNullOrWhiteSpace(mergedDetail) ? "" : $" | {mergedDetail}");

                LogUiChangeIfNeeded(sig, msg);

                lblDetail.Text = mergedDetail;
                lblHttp.Text = $"HTTP Code: {httpUi}";
                lblLast.Text = $"Last Poll: {stamp:hh:mm:ss tt}";

                // On startup, Scale Status remains "—" while HTTP Code is still "—".
                // Once a non-disconnected headline has a real HTTP value, refresh it
                // immediately instead of waiting for the next poll. Disconnected clears
                // separately after the weight state is marked unavailable.
                if (state != HeadlineState.Disconnected)
                    RefreshScaleStatusFromCurrentWeight();


                string trayTooltip = FormatTrayTooltip(state, mergedDetail, eyesOnlyAlarm);

                // Store presentation state even when only the eyes-only label changes.
                _headlineEyesOnlyAlarm = eyesOnlyAlarm;

                // Change icons when the headline actually changes, but allow selected
                // runtime events (for example, an eye endpoint/input change) to force one
                // status toast even when the new target reports the same logical state.
                if (changed)
                {
                    _headline = state;

                    // Icons are part of the “headline” presentation.
                    SetIcons(state == HeadlineState.Ok ? iconConnected : iconDisconnected);
                }

                SetTrayTextSafe(trayTooltip);

                // Leaving “forced disconnect” bookkeeping behind once we have a real headline.
                if (state != HeadlineState.Disconnected)
                    ClearForcedReason();

                if ((changed || forceToast) && toast && state != HeadlineState.Unknown)
                    ShowHeadlineToastOrBalloon(state, mergedDetail, eyesOnlyAlarm);

            }

            UiSafe(Run);
        }

        // Call for informational UI only (no toasts, no tray text, no icon changes).
        private void UpdateDetailNoLastPoll(string detail, string httpShown, DateTime stamp) =>
            UpdateDetail(detail, httpShown, stamp, updateLastPoll: false);

        private void UpdateDetail(string detail, string httpShown, DateTime stamp, bool updateLastPoll = true)
        {
            void Run()
            {
                string httpUi = GetHttpUi(httpShown, out var extra);
                var mergedDetail = MergeDetail(detail, extra);
                mergedDetail = TruncateForUi(mergedDetail, DetailUiMaxLen);

                // Debug-only: include informational detail UI changes in the log.
                if (DebugLogging)
                {
                    var sig = $"D|{httpUi}|{mergedDetail}";
                    var msg = $"Detail: HTTP: {httpUi}"
                              + (string.IsNullOrWhiteSpace(mergedDetail) ? "" : $" | {mergedDetail}");

                    LogUiChangeIfNeeded(sig, msg);
                }

                lblDetail.Text = mergedDetail;
                lblHttp.Text = $"HTTP Code: {httpUi}";

                if (updateLastPoll)
                    lblLast.Text = $"Last Poll: {stamp:hh:mm:ss tt}";
            }

            UiSafe(Run);
        }

        private void SetTrayTextSafe(string text)
        {
            void Set()
            {
                text = SanitizeNotifyIconText(text);

                if (string.Equals(_lastTrayTextUi, text, StringComparison.Ordinal))
                    return;

                try
                {
                    tray.Text = text;
                    _lastTrayTextUi = text;
                }
                catch { }
            }

            UiSafe(Set);
        }

        // =====================================================================
        //  HTTP string normalization + merge helpers
        // =====================================================================
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
                return s.AsSpan(codeIdx).Trim().ToString(); // keeps "200 OK" (drops "HTTP/1.1 " etc)

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
        private const string CheckSuffix = "  ✔";

        private static string WithCheck(bool on, string text)
            => on ? (text + CheckSuffix) : text;

        private void UpdateStartupTrayMenuText()
        {
            if (_miStartWithWindows is not null)
                _miStartWithWindows.Text = WithCheck(IsRunAtLoginEnabled(), " Start with Windows");

            if (_miStartInTray is not null)
                _miStartInTray.Text = WithCheck(StartInTray, " Start in tray");
        }

        private void ToggleStartWithWindowsFromTray()
        {
            bool enable = !IsRunAtLoginEnabled();
            EnsureRunAtLogin(enable);
            UpdateStartupTrayMenuText();
        }

        private void ToggleStartInTrayFromTray()
        {
            bool enable = !StartInTray;

            // Persist the preference
            try
            {
                var s = AppSettings.LoadOrDefaults(SettingsPath, out bool existed);

                // If file exists but couldn't be read, don't clobber it.
                if (!existed && File.Exists(SettingsPath))
                    return;

                if (s.StartInTray != enable)
                {
                    s.StartInTray = enable;
                    s.Save(SettingsPath);
                }

                StartInTray = enable;
            }
            catch (Exception ex)
            {
                Log($"Start-in-tray save failed: {ex.Message}");
                return;
            }

            UpdateStartupTrayMenuText();
        }


        private void SetNotificationDuration(string durationShortOrLong)
        {
            _notificationDuration =
                string.Equals(durationShortOrLong, "Long", StringComparison.OrdinalIgnoreCase) ? "Long" : "Short";

            _notificationBalloonMs = (_notificationDuration == "Long") ? 25000 : 7000;

            // If settings dialog is open, keep it in sync (unless disabled).
            _settingsDlg?.SetNotificationDurationUi(_notificationsEnabled, _notificationDuration);
        }

        private void SetNotificationsEnabled(bool enabled)
        {
            _notificationsEnabled = enabled;

            // Global gate for any ToastHelper calls.
            ToastHelper.Enabled = enabled;

            if (_miEnableNotifications is not null)
                _miEnableNotifications.Text = WithCheck(enabled, " Enable notifications");

            // Sync Settings dropdown if the dialog is open.
            _settingsDlg?.SetNotificationDurationUi(enabled, _notificationDuration);
        }

        private void ToggleNotificationsFromTray()
        {
            SetNotificationsEnabled(!_notificationsEnabled);
            SaveNotificationsEnabledSetting();
        }

        private void SaveNotificationsEnabledSetting()
        {
            try
            {
                var s = AppSettings.LoadOrDefaults(SettingsPath, out _);
                s.NotificationsEnabled = _notificationsEnabled;
                s.Save(SettingsPath);
            }
            catch (Exception ex)
            {
                Log($"NotificationsEnabled save failed: {ex.Message}");
            }
        }

        private void SetIcons(Icon icon)
        {
            void Set()
            {
                try
                {
                    tray.Icon = icon;
                    this.Icon = icon;

                    // Toggle visibility to force refresh in some shells.
                    tray.Visible = false;
                    tray.Visible = true;
                }
                catch { }
            }

            UiSafe(Set);
        }

        // =====================================================================
        //  Notifications (toast preferred; balloon fallback) - HEADLINE ONLY
        // =====================================================================
        private void ShowHeadlineToastOrBalloon(HeadlineState state, string? detail = null, bool eyesOnlyAlarm = false)
        {
            if (_shutdown) return;
            if (!_notificationsEnabled) return;
            if (_sessionLocked)
            {
                // Coalesce: remember only the most recent headline that happened while locked.
                _pendingToastWhileLocked = state;
                _pendingToastDetailWhileLocked = detail;
                _pendingToastEyesOnlyAlarmWhileLocked = (state == HeadlineState.AlignmentOff) && eyesOnlyAlarm;
                return;
            }

            void Run()
            {
                eyesOnlyAlarm = (state == HeadlineState.AlignmentOff) && eyesOnlyAlarm;

                string title = $"Eye Status: {HeadlineToText(state, eyesOnlyAlarm)}";
                string text = state == HeadlineState.Disconnected
                    ? detail?.Trim() ?? ""
                    : GetHeadlineGuidance(state);

                bool toastShown = false;

                // Prefer toast (matches the “main icon” branding behavior when AUMID+shortcut are correct).
                try
                {
                    string headerPng = Path.Combine(
                        Application.StartupPath,
                        state == HeadlineState.Ok ? "ScaleEye_Green128.png" : "ScaleEye_Red128.png");

                    // ToastHelper currently requires a valid absolute path.
                    if (File.Exists(headerPng))
                    {
                        ToastHelper.ShowToast(title, text, headerPng, longDuration: (_notificationDuration == "Long"));
                        toastShown = true;
                    }
                }
                catch
                {
                    // ignore
                }

                // Fallback only if toast wasn't shown.
                if (!toastShown)
                {
                    try
                    {
                        var icon = (state == HeadlineState.Ok)
                            ? System.Windows.Forms.ToolTipIcon.Info
                            : System.Windows.Forms.ToolTipIcon.Warning;

                        tray.ShowBalloonTip(_notificationBalloonMs, title, text, icon);
                    }
                    catch { }
                }
            }

            UiSafe(Run);
        }

        // =====================================================================
        //  Misc UX helpers
        // =====================================================================
        private void OpenReadme()
        {
            try
            {
                string path = Path.Combine(Application.StartupPath, "README_Program.txt");

                if (!File.Exists(path))
                {
                    MessageBox.Show(
                        $"Readme not found:\n{path}\n\nAdd README_Program.txt next to the exe.",
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
