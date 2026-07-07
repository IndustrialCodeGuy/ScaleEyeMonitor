using Microsoft.Win32;

namespace Scale_Eye_Monitor
{
    /*
     * MainForm.Settings.cs
     * --------------------
     * Partial: MainForm (Scale Eye Monitor)
     *
     * Responsibilities:
     *   - Own the runtime “settings fields” (copied from AppSettings; kept in UI order).
     *   - ApplySettings(AppSettings): copy normalized settings into runtime fields.
     *   - Settings dialog lifecycle:
     *       * Enforce single-instance settings dialog (_settingsDialogOpen / _settingsDlg)
     *       * Bring-to-front behavior when the dialog is already open
     *       * Validate/commit dialog values into AppSettings
     *       * Save settings.json and apply Run-at-login (HKCU Run key)
     *   - Diff-based runtime updates after settings changes:
     *       * Recreate HttpClient when the eye endpoint URL or InputId changes
     *       * Restart weight monitor when Weight Mode / endpoint changes; stop when disabled
     *       * Apply persisted UX toggles (StartInTray / AlwaysOnTop)
     *       * Optionally run an immediate poll after settings changes
     *
     * Concurrency model (important):
     *   - Polling is guarded by _opGate. Settings apply cancels any active poll
     *     and waits for the gate so changes apply against a quiet system.
     *   - Immediate poll after the dialog closes re-enters through PollOnceSafeAsync.
     *
     * Ordering rule:
     *   Any “copy settings” blocks should follow SettingsForm order:
     *     Eye/general -> Notifications -> Start with Windows (registry) -> Weight Mode/settings -> Debug/burst -> Startup UX (StartInTray/AlwaysOnTop)
     */

    public sealed partial class MainForm
    {
        // =====================================================================
        //  Settings (runtime fields, populated from JSON)
        //  Ordering matches SettingsForm / AppSettings UI order
        // =====================================================================
        // ---- Eye / general ----
        private string EyeUrl = "";
        private int InputId;
        private int PollSeconds;

        // Seconds between Eye check #1 and Eye check #2
        private int EyeConfirmDelaySeconds;

        // Failure policy (eyes)
        private int FailureRetryDelaySeconds;
        private int FailureRetryCount;

        // ---- Weight mode ----
        private bool WeightModeEnabled;
        private string WeightIp = "";
        private int WeightPort;

        // Weight-mode default poll interval (used for StableZero and “normal” polling)
        private int WeightPollSeconds;

        // Confirm delay (weight mode default vs fast window)
        private int WeightEyeConfirmDelaySeconds;
        private int WeightEyeConfirmDelayFastSeconds;

        // Eye polling adjustment when stable non-zero weight (truck present)
        private int PollSecondsStableNonZero = 5;

        // How long we keep the fast StableNonZero poll rate after entering StableNonZero.
        private int StableNonZeroFastWindowSeconds;

        // Weight-mode “normal poll burst”
        private int WeightBurstCount;
        private int WeightBurstDelayMs;
        private int WeightBurstMinTrueSuccess;

        // Minimum absolute stable weight before Blocked uses vehicle-on-scale guidance.
        private int BlockedGuidanceMinWeight = 1000;

        // Weight gating tuning
        private int WeightStableBand = 20;      // +/- band for stability
        private int WeightZeroBand = 50;        // abs(weight) < band => “zero”
        private int WeightWindowSeconds = 2;    // stability window length
        private int WeightStaleSeconds = 3;     // if no updates within this, mark “stale/unavailable”

        // ---- Debug / diagnostics ----
        private bool DebugLogging;

        // Burst test (settings-driven)
        private int BurstTestCount = 20;
        private int BurstTestDelayMs = 500;

        // ---- Startup behavior ----
        private bool StartInTray = true;

        // =====================================================================
        //  Paths
        // =====================================================================
        private static readonly string AppDataDir =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Application.ProductName ?? "Scale Eye Monitor");

        private static readonly string LogDir = Path.Combine(AppDataDir, "logs");
        private static readonly string SettingsPath = Path.Combine(AppDataDir, "settings.json");

        // =====================================================================
        //  Registry Run key (Start with Windows)
        // =====================================================================
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "ScaleEyeMonitor";

        // =====================================================================
        //  Settings dialog state
        // =====================================================================
        private bool _settingsDialogOpen;
        private SettingsForm? _settingsDlg;

        // =====================================================================
        //  Apply settings to runtime fields
        //  (Order matches AppSettings / SettingsForm)
        // =====================================================================
        private void ApplySettings(AppSettings s)
        {
            s.NormalizeAndClamp();

            // ---- Eye / general ----
            EyeUrl = s.EyeUrl;
            InputId = s.InputId;
            PollSeconds = s.PollSeconds;

            EyeConfirmDelaySeconds = s.EyeConfirmDelaySeconds;
            FailureRetryDelaySeconds = s.FailureRetryDelaySeconds;
            FailureRetryCount = s.FailureRetryCount;

            // ---- Notifications ----
            SetNotificationDuration(s.NotificationDuration);
            SetNotificationsEnabled(s.NotificationsEnabled);

            // ---- Weight mode ----
            WeightModeEnabled = s.WeightModeEnabled;

            WeightIp = s.WeightIp;
            WeightPort = s.WeightPort;

            WeightPollSeconds = s.WeightPollSeconds;
            PollSecondsStableNonZero = s.PollSecondsStableNonZero;
            WeightEyeConfirmDelayFastSeconds = s.WeightEyeConfirmDelayFastSeconds;
            WeightEyeConfirmDelaySeconds = s.WeightEyeConfirmDelaySeconds;
            StableNonZeroFastWindowSeconds = s.StableNonZeroFastWindowSeconds;

            WeightBurstCount = s.WeightBurstCount;
            WeightBurstDelayMs = s.WeightBurstDelayMs;
            WeightBurstMinTrueSuccess = s.WeightBurstMinTrueSuccess;
            BlockedGuidanceMinWeight = s.BlockedGuidanceMinWeight;

            WeightStableBand = s.WeightStableBand;
            WeightZeroBand = s.WeightZeroBand;
            WeightWindowSeconds = s.WeightWindowSeconds;
            WeightStaleSeconds = s.WeightStaleSeconds;

            // ---- Debug / burst ----
            DebugLogging = s.DebugLogging;
            BurstTestCount = s.BurstTestCount;
            BurstTestDelayMs = s.BurstTestDelayMs;

            StartInTray = s.StartInTray;
            SetAlwaysOnTop(s.AlwaysOnTop);

            // If weight mode was turned off by settings, clear the fast-window tracking
            if (!WeightModeEnabled)
                ResetStableNonZeroFastWindow();

            // Update the main layout and "Scale Status" top-row label immediately
            // after applying settings. This hides Scale Status when Weight Mode is off.
            try
            {
                UiSafe(ApplyMainLayoutMetrics);

                WeightState ws = WeightModeEnabled ? GetWeightStateSnapshot() : new WeightState(WeightMode.Unavailable, null, 0, DateTime.MinValue, "");
                UpdateScaleStatusFromWeight(ws);
            }
            catch { }
        }

        // =====================================================================
        //  HTTP client init/re-init (settings dependent)
        // =====================================================================
        private void InitHttp()
        {
            // Recreate handler/client so new settings apply cleanly
            _http?.Dispose();
            _handler?.Dispose();

            _handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 1,
                UseProxy = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(2),
            };

            // We explicitly dispose _handler ourselves, so prevent HttpClient from owning it.
            _http = new HttpClient(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(5) };
        }

        // =====================================================================
        //  Settings dialog flow
        // =====================================================================
        private void Settings_Click(object? sender, EventArgs e)
        {
            if (_shutdown) return;

            if (_settingsDialogOpen)
            {
                BringSettingsToFront();
                return;
            }
            _settingsDialogOpen = true;
            // Ensure the modal settings dialog isn't dragged into topmost behavior.
            SetAlwaysOnTop(_alwaysOnTop);

            bool gateHeld = false;
            void ReleaseGate()
            {
                if (!gateHeld) return;
                gateHeld = false;
                try { _opGate.Release(); } catch { }
            }

            try
            {
                var s = AppSettings.LoadOrDefaults(SettingsPath, out _);
                bool runAtLoginCurrent = IsRunAtLoginEnabled();

                bool doImmediatePoll = false;

                using var dlg = new SettingsForm(s, firstRun: false, runAtLoginCurrent: runAtLoginCurrent);
                _settingsDlg = dlg;

                WireBurstTest(dlg);

                dlg.ApplyHandlerAsync = async () =>
                {
                    if (IsBurstRunning)
                    {
                        CancelBurstTest();
                        return (false, "Burst test is running and is being canceled. Wait for it to stop, then click OK again.");
                    }

                    CancelActivePoll();

                    // Wait for any in-flight poll to finish (dialog already locked by SettingsForm).
                    await _opGate.WaitAsync(_lifetimeToken).ConfigureAwait(true);
                    gateHeld = true;

                    try
                    {
                        // Snapshot "before"
                        var old = CloneSettings(s);

                        var (ok, err) = CommitSettingsFromDialog(dlg, s);
                        if (!ok) { ReleaseGate(); return (false, err); }

                        var now = CloneSettings(s);

                        // -----------------------------
                        // Detect changes (grouped like UI)
                        // -----------------------------
                        bool eyeUrlChanged =
                            !string.Equals(Norm(old.EyeUrl), Norm(now.EyeUrl), StringComparison.OrdinalIgnoreCase);

                        bool eyeEndpointChanged =
                            eyeUrlChanged ||
                            old.InputId != now.InputId;

                        bool eyeTimingOrPolicyChanged =
                            old.PollSeconds != now.PollSeconds ||
                            old.EyeConfirmDelaySeconds != now.EyeConfirmDelaySeconds ||
                            old.FailureRetryDelaySeconds != now.FailureRetryDelaySeconds ||
                            old.FailureRetryCount != now.FailureRetryCount;

                        bool weightModeChanged =
                            old.WeightModeEnabled != now.WeightModeEnabled;

                        bool weightEndpointChanged =
                            weightModeChanged ||
                            !string.Equals(Norm(old.WeightIp), Norm(now.WeightIp), StringComparison.OrdinalIgnoreCase) ||
                            old.WeightPort != now.WeightPort;

                        bool weightTimingChanged =
                            old.WeightPollSeconds != now.WeightPollSeconds ||
                            old.WeightEyeConfirmDelaySeconds != now.WeightEyeConfirmDelaySeconds ||
                            old.PollSecondsStableNonZero != now.PollSecondsStableNonZero ||
                            old.StableNonZeroFastWindowSeconds != now.StableNonZeroFastWindowSeconds ||
                            old.WeightEyeConfirmDelayFastSeconds != now.WeightEyeConfirmDelayFastSeconds ||
                            old.WeightBurstCount != now.WeightBurstCount ||
                            old.WeightBurstDelayMs != now.WeightBurstDelayMs ||
                            old.WeightBurstMinTrueSuccess != now.WeightBurstMinTrueSuccess;

                        bool weightTuningChanged =
                            old.WeightStableBand != now.WeightStableBand ||
                            old.WeightZeroBand != now.WeightZeroBand ||
                            old.WeightWindowSeconds != now.WeightWindowSeconds ||
                            old.WeightStaleSeconds != now.WeightStaleSeconds ||
                            old.BlockedGuidanceMinWeight != now.BlockedGuidanceMinWeight;

                        bool debugOrBurstChanged =
                            old.DebugLogging != now.DebugLogging ||
                            old.BurstTestCount != now.BurstTestCount ||
                            old.BurstTestDelayMs != now.BurstTestDelayMs;

                        bool runtimeWeightWasConnected = _weightWasConnected;

                        // -----------------------------
                        // Apply to runtime fields
                        // -----------------------------
                        ApplySettings(s);
                        UpdateStartupTrayMenuText();

                        // IMPORTANT:
                        // _desiredPollSeconds should not be forced to "1" here unconditionally.
                        // That can accidentally leave the app polling at 1s when only non-timing settings change.
                        // Timer/loop logic should derive from PollSeconds + weight state.
                        if (old.PollSeconds != now.PollSeconds || eyeEndpointChanged || weightEndpointChanged || weightTimingChanged)
                        {
                            SetDesiredPollSeconds(PollSeconds);
                        }

                        // -----------------------------
                        // Runtime restarts / UI updates
                        // -----------------------------
                        if (eyeEndpointChanged)
                        {
                            InitHttp();

                            _muteWeightToastsUntilUtc = DateTime.UtcNow.AddSeconds(60);

                            // Informational only (no toast). Keep headline stable.
                            UpdateDetail("Reconnecting eyes…", httpShown: "—", stamp: DateTime.Now);

                            ResetEyeFailureTracking();
                            ClearForcedReason();

                            // Endpoint/input changes are effectively a new monitored target.
                            // Show the next confirmed headline toast even if the new target
                            // has the same state as the previous location.
                            _forceNextHeadlineToast = true;

                            if (eyeUrlChanged)
                                ClearInvalidSoapUrlState();
                        }
                        else if (weightEndpointChanged && WeightModeEnabled)
                        {
                            if (runtimeWeightWasConnected)
                            {
                                try
                                {
                                    ToastHelper.ShowWarningToast(
                                        "Scale Eye Monitor",
                                        "Weight stream disconnected (settings changed). Reconnecting…");
                                }
                                catch { }
                            }

                            _weightOutageActive = true;
                            _weightDisconnectToastShown = false;
                            _weightEverConnected = false;
                            _weightStartupToastShown = false;
                        }

                        if (weightEndpointChanged)
                        {
                            // Endpoint/mode changed: restart only when enabled; otherwise stop cleanly.
                            if (WeightModeEnabled)
                                RestartWeightMonitor();
                            else
                                StopWeightMonitor();
                        }
                        else if (!WeightModeEnabled)
                        {
                            // No endpoint change, but mode is off => ensure stopped.
                            StopWeightMonitor();
                        }

                        bool weightBehaviorChanged = weightEndpointChanged || weightTimingChanged || weightTuningChanged;

                        doImmediatePoll =
                            eyeEndpointChanged ||
                            eyeTimingOrPolicyChanged ||
                            weightBehaviorChanged ||
                            debugOrBurstChanged;

                        // NOTE: gate is released when the dialog closes; immediate poll reacquires it via PollOnceSafeAsync.
                        return (true, null);
                    }
                    catch (Exception ex)
                    {
                        ReleaseGate();
                        return (false, ex.Message);
                    }
                };

                bool ok = false;

                try
                {
                    ok = (dlg.ShowDialog(this) == DialogResult.OK);
                }
                finally
                {
                    _settingsDlg = null;
                    ReleaseGate();
                }

                if (ok && doImmediatePoll)
                {
                    // Run right after the settings dialog closes (keeps UI snappy).
                    BeginInvoke(new Action(async () =>
                    {
                        await PollOnceSafeAsync(waitForGate: true, fromLoop: false, token: _lifetimeToken).ConfigureAwait(true);
                        RescheduleNextPollFromNow();
                    }));
                }
            }
            finally
            {
                _settingsDialogOpen = false;

                // Restore main window topmost state (if enabled).
                SetAlwaysOnTop(_alwaysOnTop);
            }
        }

        private void BringSettingsToFront()
        {
            void Run()
            {
                var dlg = _settingsDlg;
                if (dlg is null || dlg.IsDisposed) return;

                dlg.BringToFront();
                dlg.Activate();
            }

            UiSafe(Run);
        }

        // =====================================================================
        //  Weight monitor restart helper used by settings apply
        // =====================================================================
        private void RestartWeightMonitor()
        {
            StopWeightMonitor();
            StartOrStopWeightMonitor();
        }

        // =====================================================================
        //  Run-at-login (HKCU\...\Run)
        // =====================================================================
        private static bool IsRunAtLoginEnabled()
        {
            try
            {
                using var rk = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                var val = rk?.GetValue(RunValue) as string;

                string desired = $"\"{Application.ExecutablePath}\"";
                return string.Equals(val, desired, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void EnsureRunAtLogin(bool enable)
        {
            try
            {
                using var rk = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                             ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, true);

                if (enable)
                {
                    var exe = Application.ExecutablePath;
                    string desired = $"\"{exe}\"";

                    var current = rk.GetValue(RunValue) as string;
                    bool changed = !string.Equals(current, desired, StringComparison.Ordinal);

                    if (changed)
                        rk.SetValue(RunValue, desired);

                    if (string.IsNullOrEmpty(current))
                        Log("Enabled Start with Windows.");
                    else if (changed)
                        Log("Updated Start with Windows.");
                }
                else
                {
                    rk.DeleteValue(RunValue, false);
                    Log("Disabled Start with Windows.");
                }
            }
            catch (Exception ex)
            {
                Log($"Run-at-login change failed: {ex.Message}");
                MessageBox.Show($"Failed to change Start-with-Windows:\n{ex.Message}", "Scale Eye Monitor",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // =====================================================================
        //  Dialog commit + persistence (SettingsForm -> AppSettings)
        //  (Order matches SettingsForm UI)
        // =====================================================================
        private (bool ok, string? error) CommitSettingsFromDialog(SettingsForm dlg, AppSettings s)
        {
            try
            {
                // ---- Eye / general ----
                s.EyeUrl = dlg.EyeUrlValue;
                s.InputId = dlg.InputIdValue;
                s.PollSeconds = dlg.PollSecondsValue;

                s.EyeConfirmDelaySeconds = dlg.EyeConfirmDelaySecondsValue;
                s.FailureRetryDelaySeconds = dlg.FailureRetryDelaySecondsValue;
                s.FailureRetryCount = dlg.FailureRetryCountValue;

                // ---- Notifications ----
                var nd = dlg.NotificationDurationValue;

                if (string.Equals(nd, "Disabled", StringComparison.OrdinalIgnoreCase))
                {
                    // Disable notifications, but keep the last Short/Long choice in s.NotificationDuration.
                    s.NotificationsEnabled = false;
                }
                else
                {
                    s.NotificationsEnabled = true;
                    s.NotificationDuration = string.Equals(nd, "Long", StringComparison.OrdinalIgnoreCase) ? "Long" : "Short";
                }

                // ---- Weight mode ----
                s.WeightModeEnabled = dlg.WeightModeEnabledValue;

                s.WeightIp = dlg.WeightIpValue;
                s.WeightPort = dlg.WeightPortValue;

                s.WeightPollSeconds = dlg.WeightPollSecondsValue;
                s.WeightEyeConfirmDelaySeconds = dlg.WeightEyeConfirmDelaySecondsValue;
                s.PollSecondsStableNonZero = dlg.PollSecondsStableNonZeroValue;
                s.WeightEyeConfirmDelayFastSeconds = dlg.WeightEyeConfirmDelayFastSecondsValue;
                s.StableNonZeroFastWindowSeconds = dlg.StableNonZeroFastWindowSecondsValue;

                s.WeightBurstCount = dlg.WeightBurstCountValue;
                s.WeightBurstDelayMs = dlg.WeightBurstDelayMsValue;
                s.WeightBurstMinTrueSuccess = dlg.WeightBurstMinTrueSuccessValue;
                s.BlockedGuidanceMinWeight = dlg.BlockedGuidanceMinWeightValue;

                s.WeightStableBand = dlg.WeightStableBandValue;
                s.WeightZeroBand = dlg.WeightZeroBandValue;
                s.WeightWindowSeconds = dlg.WeightWindowSecondsValue;
                s.WeightStaleSeconds = dlg.WeightStaleSecondsValue;

                // ---- Debug / burst ----
                s.DebugLogging = dlg.DebugLoggingValue;
                s.BurstTestCount = dlg.BurstTestCountValue;
                s.BurstTestDelayMs = dlg.BurstTestDelayMsValue;

                // Persist
                // AppSettings.Save ensures settings directory exists
                s.Save(SettingsPath);

                // Run-at-login (registry-owned; not in AppSettings)
                EnsureRunAtLogin(dlg.RunAtLoginEnabledValue);
                UpdateStartupTrayMenuText();

                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // =====================================================================
        //  Settings object helpers (copy/clone/normalize)
        // =====================================================================
        private static void CopyInto(AppSettings dst, AppSettings src)
        {
            // ---- Eye / general ----
            dst.EyeUrl = src.EyeUrl;
            dst.InputId = src.InputId;
            dst.PollSeconds = src.PollSeconds;

            dst.EyeConfirmDelaySeconds = src.EyeConfirmDelaySeconds;
            dst.FailureRetryDelaySeconds = src.FailureRetryDelaySeconds;
            dst.FailureRetryCount = src.FailureRetryCount;

            // ---- Weight mode ----
            dst.WeightModeEnabled = src.WeightModeEnabled;

            dst.WeightIp = src.WeightIp;
            dst.WeightPort = src.WeightPort;

            dst.WeightPollSeconds = src.WeightPollSeconds;
            dst.WeightEyeConfirmDelaySeconds = src.WeightEyeConfirmDelaySeconds;
            dst.PollSecondsStableNonZero = src.PollSecondsStableNonZero;
            dst.WeightEyeConfirmDelayFastSeconds = src.WeightEyeConfirmDelayFastSeconds;
            dst.StableNonZeroFastWindowSeconds = src.StableNonZeroFastWindowSeconds;

            dst.WeightBurstCount = src.WeightBurstCount;
            dst.WeightBurstDelayMs = src.WeightBurstDelayMs;
            dst.WeightBurstMinTrueSuccess = src.WeightBurstMinTrueSuccess;
            dst.BlockedGuidanceMinWeight = src.BlockedGuidanceMinWeight;

            dst.WeightStableBand = src.WeightStableBand;
            dst.WeightZeroBand = src.WeightZeroBand;
            dst.WeightWindowSeconds = src.WeightWindowSeconds;
            dst.WeightStaleSeconds = src.WeightStaleSeconds;

            // ---- Debug / burst ----
            dst.DebugLogging = src.DebugLogging;
            dst.BurstTestCount = src.BurstTestCount;
            dst.BurstTestDelayMs = src.BurstTestDelayMs;

            dst.AlwaysOnTop = src.AlwaysOnTop;
            dst.StartInTray = src.StartInTray;

            dst.NotificationsEnabled = src.NotificationsEnabled;
            dst.NotificationDuration = src.NotificationDuration;
        }

        private static AppSettings CloneSettings(AppSettings s)
        {
            var copy = new AppSettings();
            CopyInto(copy, s);
            return copy;
        }

        private static string Norm(string? x) => (x ?? "").Trim();
    }
}
