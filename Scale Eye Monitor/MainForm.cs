using Microsoft.Win32;

namespace Scale_Eye_Monitor
{
    /*
     * MainForm (Scale Eye Monitor) - Partial class split
     * --------------------------------------------------
     * Purpose:
     *   Monitor Kahler scale eye input (SOAP over HTTP) and present a unified status via:
     *     - Small status window (labels: Eye Status + Scale Status + Detail + Last Poll + HTTP)
     *     - Tray icon + tooltip
     *     - Optional notifications (headline toasts + informational warning/info balloons)
     *       (gated by “Enable notifications” and “Notification Duration”)
     *
     * Core model:
     *   - UI/tray/toasts are driven by a single stable “headline” state:
     *       Unknown / OK / Blocked / Alignment off / Obstructed / Disconnected
     *       (Obstructed uses the AlignmentOff state when running eyes-only or without usable weight.)
     *   - “Detail” text is informational only and does NOT change tray text, icons, or toasts.
     *   - Tray tooltip text is headline-only and is truncated to NotifyIcon’s hard limit (63 chars).
     *     Detail text is truncated for UI/log hygiene (currently 300 chars) and may be visually ellipsized.
     *
     * Window behavior:
     *   - “Always on top” applies to the MAIN window only (TopMost), not to modal dialogs (Settings).
     *   - When starting hidden (StartInTray), the form suppresses initial visibility to prevent
     *     a brief startup flash (no “show then hide”).
     *
     * File layout:
     *   - MainForm.cs
     *       * Core fields, constructor/startup wiring, tray/menu, single-instance “show me” message,
     *         shutdown/cleanup (OnHandleDestroyed).
     *   - MainForm.Ui.cs
     *       * UI + tray helpers (headline vs detail updates, icons, toasts, show/hide window, Check Now).
     *   - MainForm.Settings.cs
     *       * Settings dialog flow, apply/diff logic, run-at-login registry, HTTP re-init decisions.
     *   - MainForm.Polling.cs
     *       * Poll loop scheduler + PollOnce logic:
     *           - weight snapshot gating (pause eyes during motion)
     *           - unified timing selection (poll interval + confirm delay)
     *           - Eye #1 burst only in StableZero (anti-false-positive)
     *           - confirm-delay + Eye #2 confirm before committing a headline state
     *   - MainForm.EyeSoap.cs
     *       * SOAP call + retry/failure policy; forces headline Disconnected on offline conditions.
     *   - MainForm.WeightMonitor.cs
     *       * TCP weight monitor loop + stability window; publishes WeightState snapshots; wakes poll loop
     *         and cancels in-flight polls when motion begins.
     *   - MainForm.BurstTest.cs
     *       * Diagnostic burst test runner for the eye endpoint (invoked from SettingsForm).
     *   - MainForm.Logging.cs
     *       * File logging and “open logs folder”.
     *
     * Startup flow (constructor):
     *   1) Build UI + tray + menu and wire events.
     *   2) Load settings.json (or defaults). If missing/invalid settings, show Settings dialog.
     *      (On true first-run, Cancel exits the app; otherwise Cancel keeps existing settings.)
     *   3) Apply settings to runtime fields (including AlwaysOnTop).
     *   4) Start/stop weight monitor (if WeightModeEnabled and endpoint valid).
     *   5) Initialize HttpClient (device-compat settings: HTTP/1.0, ConnectionClose, etc.).
     *   6) Start polling scheduler.
     *
     * Startup visibility:
     *   - The app always runs with a tray icon.
     *   - Window visibility at launch is controlled by:
     *       * StartInTray setting (persisted; hides on normal launch).
     *   - “Start with Windows” (HKCU Run key) is independent from StartInTray.
     *
     * Cleanup:
     *   - Uses OnHandleDestroyed with guards:
     *       * Skip teardown during handle recreation.
     *       * Run teardown only once.
     *   - Cancels lifetime token, stops poll loop and weight monitor, disposes tray/menu/http/icons.
     */

    public sealed partial class MainForm : Form
    {
        // =====================================================================
        //  UI
        // =====================================================================
        private readonly Label lblStatus = new() { AutoSize = true, Text = "Status: Loading" };
        private readonly Label lblScaleStatus = new() { AutoSize = true, Text = "Scale Status: —" };
        private readonly Label lblDetail = new() { AutoSize = true, Top = 24, Text = "" };
        private readonly Label lblLast = new() { AutoSize = true, Top = 48, Text = "Last Poll: —" };
        private readonly Label lblHttp = new() { AutoSize = true, Top = 72, Text = "HTTP Code: —" };
        private readonly Button btnCheck = new() { Text = "Check Now", Width = 100, Top = 100, Left = 0 };
        private readonly LinkLabel lnkLogs = new() { Text = "Open Logs Folder", Top = 132, Left = 0, AutoSize = true };

        // =====================================================================
        //  Tray
        // =====================================================================
        private readonly NotifyIcon tray = new();
        private readonly ContextMenuStrip trayMenu = new();
        private readonly Icon iconConnected;    // OK
        private readonly Icon iconDisconnected; // Disconnected (confirmed/forced/unknown)

        // =====================================================================
        //  Operation gating + lifetime cancellation
        // =====================================================================
        private readonly SemaphoreSlim _opGate = new(1, 1);
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly CancellationToken _lifetimeToken;

        // =====================================================================
        //  Engine / runtime state
        // =====================================================================
        private SocketsHttpHandler _handler = default!;
        private HttpClient _http = default!;

        // Stable “headline” state (UI + tray + toasts are driven ONLY by this).
        // _headlineEyesOnlyAlarm is presentation-only: when AlignmentOff is committed
        // without usable weight context, the user-facing text is “Obstructed”.
        private HeadlineState _headline = HeadlineState.Unknown;
        private bool _headlineEyesOnlyAlarm;

        // One-shot: when the eye endpoint/input changes, the next committed headline
        // should notify even if the logical state remains the same at the new location.
        private bool _forceNextHeadlineToast;

        private volatile bool _shutdown = false;

        // Session lock suppression (toast coalescing)
        private volatile bool _sessionLocked;
        private HeadlineState? _pendingToastWhileLocked;
        private string? _pendingToastDetailWhileLocked;
        private bool _pendingToastEyesOnlyAlarmWhileLocked;
        private HeadlineState _headlineAtLock = HeadlineState.Unknown;
        private bool _headlineEyesOnlyAlarmAtLock;
        private bool _haveLockSnapshot;

        private bool _allowExit = false;    // if false, closing hides to tray

        // Prevent startup flash when starting in tray.
        private bool _suppressInitialShow;

        internal bool StartupCanceled { get; private set; }

        protected override void SetVisibleCore(bool value)
        {
            if (_suppressInitialShow && value)
            {
                // Keep the main form from ever becoming visible during startup.
                ShowInTaskbar = false;
                value = false;
            }

            base.SetVisibleCore(value);
        }

        private void ApplyMainLayoutMetrics()
        {
            if (IsDisposed || Disposing) return;

            var m = DpiLayout.For(this);

            SuspendLayout();
            try
            {
                int clientW = m.Scale(320);
                int pad = m.Scale(6);
                int topPad = m.Scale(6);
                int bottomPad = m.Scale(6);
                int gap = m.Scale(2);
                int detailTopGap = m.Scale(4);
                int rowH = m.TextRowHeight(Font);
                int buttonH = Math.Max(m.Scale(24), rowH + m.Scale(4));
                int buttonW = m.Scale(100);

                ClientSize = new Size(clientW, Math.Max(ClientSize.Height, m.Scale(80)));
                int mid = ClientSize.Width / 2;

                // Row 1 (Status | Scale Status)
                // Hide Scale Status when Weight Mode is disabled by settings. When hidden,
                // let Eye Status use the full row instead of leaving an empty right column.
                bool showScaleStatus = WeightModeEnabled;

                lblStatus.AutoSize = false;
                lblStatus.AutoEllipsis = true;
                lblStatus.TextAlign = ContentAlignment.MiddleLeft;
                lblStatus.SetBounds(
                    pad,
                    topPad,
                    showScaleStatus ? mid - (pad * 2) : ClientSize.Width - (pad * 2),
                    rowH);

                lblScaleStatus.Visible = showScaleStatus;
                lblScaleStatus.AutoSize = false;
                lblScaleStatus.AutoEllipsis = true;
                lblScaleStatus.TextAlign = ContentAlignment.MiddleLeft;
                lblScaleStatus.SetBounds(mid, topPad, ClientSize.Width - mid - pad, rowH);

                // Row 2 (Detail)
                lblDetail.AutoSize = false;
                lblDetail.AutoEllipsis = true;
                lblDetail.TextAlign = ContentAlignment.MiddleLeft;
                lblDetail.SetBounds(pad, lblStatus.Bottom + detailTopGap, ClientSize.Width - (pad * 2), rowH);

                // Row 3 (Last Poll | HTTP Code)
                int row3Top = lblDetail.Bottom + gap;

                lblLast.AutoSize = false;
                lblLast.AutoEllipsis = true;
                lblLast.TextAlign = ContentAlignment.MiddleLeft;
                lblLast.SetBounds(pad, row3Top, mid - (pad * 2), rowH);

                lblHttp.AutoSize = false;
                lblHttp.AutoEllipsis = true;
                lblHttp.TextAlign = ContentAlignment.MiddleLeft;
                lblHttp.SetBounds(mid, row3Top, ClientSize.Width - mid - pad, rowH);

                // Row 4 (Check button)
                btnCheck.SetBounds(pad, lblHttp.Bottom + gap, buttonW, buttonH);

                // Row 4 right column (Logs link aligned with Last Poll column)
                lnkLogs.AutoSize = false;
                lnkLogs.AutoEllipsis = true;
                lnkLogs.TextAlign = ContentAlignment.MiddleRight;
                lnkLogs.SetBounds(mid, btnCheck.Top, ClientSize.Width - mid - pad, btnCheck.Height);

                // Tighten window height to content.
                ClientSize = new Size(ClientSize.Width, btnCheck.Bottom + bottomPad);
            }
            finally
            {
                ResumeLayout(true);
            }
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            ApplyMainLayoutMetrics();
        }

        // =====================================================================
        //  Cleanup guards
        // =====================================================================
        private bool _cleanupDone;

        // =====================================================================
        //  Construction / startup
        // =====================================================================
        public MainForm()
        {
            _lifetimeToken = _lifetimeCts.Token;

            // Track lock/unlock so we can suppress toast spam while locked.
            try { SystemEvents.SessionSwitch += SystemEvents_SessionSwitch; } catch { }

            // --- basic form ---
            AutoScaleMode = AutoScaleMode.None;
            Font = SystemFonts.MessageBoxFont;

            Text = "Scale Eye Monitor";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;

            // Manual layout is metric-driven so labels/buttons keep enough pixel room
            // at 125%/150%/200% DPI instead of relying on fixed 96-DPI bounds.
            ApplyMainLayoutMetrics();
            Load += (_, __) => ApplyMainLayoutMetrics();

            Controls.Add(lblStatus);
            Controls.Add(lblScaleStatus);
            Controls.Add(lblDetail);
            Controls.Add(lblLast);
            Controls.Add(lblHttp);
            Controls.Add(btnCheck);
            Controls.Add(lnkLogs);

            // --- UI events ---
            btnCheck.Click += CheckNow_Click;
            lnkLogs.LinkClicked += (_, __) => OpenLogFolder();

            // --- tray setup ---
            var connectedIconPath = Path.Combine(Application.StartupPath, "ScaleEye_Connected32.ico");
            try { iconConnected = new Icon(connectedIconPath); }
            catch { iconConnected = (Icon)SystemIcons.Application.Clone(); }

            var disconnectedIconPath = Path.Combine(Application.StartupPath, "ScaleEye_Disconnected32.ico");
            try { iconDisconnected = new Icon(disconnectedIconPath); }
            catch { iconDisconnected = (Icon)SystemIcons.Application.Clone(); }

            tray.Icon = iconDisconnected;
            this.Icon = tray.Icon;
            tray.Visible = true;

            // Route ToastHelper “warning/info” calls to the tray balloon tip (built-in icons).
            ToastHelper.BalloonNotifier = (title, text, icon) =>
            {
                if (_shutdown) return;
                if (!_notificationsEnabled) return;
                if (_sessionLocked) return;

                try { tray.ShowBalloonTip(_notificationBalloonMs, title, text, icon); } catch { }
            };

            var miSettings = new ToolStripMenuItem(" Settings…", null, Settings_Click);
            var miReadme = new ToolStripMenuItem(" Readme");
            _miStartWithWindows = new ToolStripMenuItem();
            _miStartInTray = new ToolStripMenuItem();
            var miLogs = new ToolStripMenuItem(" Open Logs Folder");
            _miAlwaysOnTop = new ToolStripMenuItem(" Always on top", null, (_, __) => ToggleAlwaysOnTop());
            _miEnableNotifications = new ToolStripMenuItem(" Enable notifications", null, (_, __) => ToggleNotificationsFromTray());
            var miCheck = new ToolStripMenuItem(" Check Now", null, CheckNow_Click);
            var miExit = new ToolStripMenuItem(" Exit", null, (_, __) =>
            {
                _allowExit = true;
                Close();
            });

            miReadme.Click += (_, __) => OpenReadme();
            miLogs.Click += (_, __) => OpenLogFolder();
            _miStartWithWindows.Click += (_, __) => ToggleStartWithWindowsFromTray();
            _miStartInTray.Click += (_, __) => ToggleStartInTrayFromTray();

            trayMenu.ShowImageMargin = false;
            trayMenu.ShowCheckMargin = false;

            trayMenu.Items.Add(miSettings);
            trayMenu.Items.Add(miReadme);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(miCheck);
            trayMenu.Items.Add(miLogs);
            trayMenu.Items.Add(_miStartWithWindows);
            trayMenu.Items.Add(_miStartInTray);
            trayMenu.Items.Add(_miAlwaysOnTop);
            trayMenu.Items.Add(_miEnableNotifications);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(miExit);

            tray.ContextMenuStrip = trayMenu;

            // show window on tray left-click
            tray.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    ShowStatusWindow();
            };

            // Minimize-to-tray behavior: “X” hides unless Exit was chosen
            FormClosing += (_, e) =>
            {
                if (!_allowExit && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    Hide();
                }
            };

            // --- settings load / first-run dialog ---
            var s = AppSettings.LoadOrDefaults(SettingsPath, out bool existed);
            // These settings should be known early:
            // - StartInTray affects initial window visibility
            StartInTray = s.StartInTray;
            SetAlwaysOnTop(s.AlwaysOnTop);
            SetNotificationDuration(s.NotificationDuration);
            SetNotificationsEnabled(s.NotificationsEnabled);

            // StartInTray hides the main window before first paint so startup can be tray-only.
            bool startHidden = StartInTray;
            _suppressInitialShow = startHidden;
            if (startHidden)
            {
                ShowInTaskbar = false;
                WindowState = FormWindowState.Minimized;
            }

            UpdateStartupTrayMenuText();

            // Ensure the app has a toast-capable Start Menu shortcut/AUMID.
            ToastHelper.EnsureShortcut(s.DebugLogging);

            // The eye endpoint is now an exact SOAP endpoint URL. Do not validate/fix
            // it here; HttpClient failures are classified at poll time.
            bool weightIpBad =
                s.WeightModeEnabled &&
                (string.IsNullOrWhiteSpace(s.WeightIp) || !IpValidator.IsStrictIPv4(s.WeightIp));

            bool needsDialog =
                !existed ||
                string.IsNullOrWhiteSpace(s.EyeUrl) ||
                weightIpBad;

            if (needsDialog)
            {
                // First run: blank SOAP endpoint URL but keep numeric defaults
                var initialForDialog = existed
                    ? s
                    : new AppSettings
                    {
                        // Eye / general
                        EyeUrl = s.EyeUrl,
                        InputId = s.InputId,
                        PollSeconds = s.PollSeconds,

                        EyeConfirmDelaySeconds = s.EyeConfirmDelaySeconds,
                        FailureRetryDelaySeconds = s.FailureRetryDelaySeconds,
                        FailureRetryCount = s.FailureRetryCount,

                        // Weight mode (first-run safe default: disabled)
                        WeightModeEnabled = false,
                        WeightIp = s.WeightIp,
                        WeightPort = s.WeightPort,

                        WeightPollSeconds = s.WeightPollSeconds,
                        PollSecondsStableNonZero = s.PollSecondsStableNonZero,
                        WeightEyeConfirmDelayFastSeconds = s.WeightEyeConfirmDelayFastSeconds,
                        WeightEyeConfirmDelaySeconds = s.WeightEyeConfirmDelaySeconds,
                        StableNonZeroFastWindowSeconds = s.StableNonZeroFastWindowSeconds,

                        WeightBurstCount = s.WeightBurstCount,
                        WeightBurstDelayMs = s.WeightBurstDelayMs,
                        WeightBurstMinTrueSuccess = s.WeightBurstMinTrueSuccess,

                        WeightStableBand = s.WeightStableBand,
                        WeightZeroBand = s.WeightZeroBand,
                        WeightWindowSeconds = s.WeightWindowSeconds,
                        WeightStaleSeconds = s.WeightStaleSeconds,

                        BlockedGuidanceMinWeight = s.BlockedGuidanceMinWeight,

                        // Debug / burst
                        DebugLogging = s.DebugLogging,
                        BurstTestCount = s.BurstTestCount,
                        BurstTestDelayMs = s.BurstTestDelayMs,

                        // Startup behavior
                        StartInTray = s.StartInTray,
                        AlwaysOnTop = s.AlwaysOnTop,

                        NotificationsEnabled = s.NotificationsEnabled,
                        NotificationDuration = s.NotificationDuration
                    };

                bool runAtLoginCurrent = IsRunAtLoginEnabled();
                var dlgSettings = initialForDialog; // the object backing the dialog

                // Make sure _http exists early so burst test from the dialog can run
                InitHttp();

                using var dlg = new SettingsForm(dlgSettings, firstRun: !existed, runAtLoginCurrent: runAtLoginCurrent);

                // Wire burst handler BEFORE showing dialog
                WireBurstTest(dlg);

                dlg.ApplyHandlerAsync = () =>
                {
                    // Guard: if burst is running and OK is somehow invoked anyway (OK is normally disabled during burst),
                    // cancel burst and ask user to retry.
                    if (IsBurstRunning)
                    {
                        CancelBurstTest();
                        return Task.FromResult<(bool ok, string? error)>((false, null));
                    }

                    var (ok, err) = CommitSettingsFromDialog(dlg, dlgSettings);
                    if (!ok)
                        return Task.FromResult<(bool ok, string? error)>((false, err));

                    CopyInto(s, dlgSettings);
                    return Task.FromResult<(bool ok, string? error)>((true, null));
                };

                _settingsDialogOpen = true;
                SetAlwaysOnTop(_alwaysOnTop);
                DialogResult result;
                try
                {
                    result = dlg.ShowDialog(this);
                }
                finally
                {
                    _settingsDialogOpen = false;
                    SetAlwaysOnTop(_alwaysOnTop);
                }

                if (result != DialogResult.OK)
                {
                    // True first-run + user cancels => exit app
                    if (!existed)
                    {
                        StartupCanceled = true;
                        _allowExit = true;
                        return;
                    }

                    // Otherwise: continue using loaded settings
                }
            }

            // Apply settings to runtime fields
            ApplySettings(s);

            // Start/stop weight monitor based on settings
            StartOrStopWeightMonitor();

            // Tray hover text after settings
            SetTrayTextSafe($"Scale Eye Monitor: Loading");

            // Init HTTP client after settings are known
            InitHttp();

            // Start poll loop after settings are known
            StartPollLoop();
        }

        // =====================================================================
        //  Single-instance “show me” message
        // =====================================================================
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == SingleInstance.ShowMeMessage)
            {
                if (!_shutdown)
                {
                    UiSafe(ShowStatusWindow);
                }
            }

            base.WndProc(ref m);
        }

        private void SystemEvents_SessionSwitch(object? sender, SessionSwitchEventArgs e)
        {
            if (_shutdown) return;

            if (e.Reason == SessionSwitchReason.SessionLock)
            {
                _sessionLocked = true;
                _headlineAtLock = _headline;
                _headlineEyesOnlyAlarmAtLock = _headlineEyesOnlyAlarm;
                _haveLockSnapshot = true;
                return;
            }

            if (e.Reason == SessionSwitchReason.SessionUnlock)
            {
                _sessionLocked = false;

                var pending = _pendingToastWhileLocked;
                var pendingDetail = _pendingToastDetailWhileLocked;
                bool pendingEyesOnlyAlarm = _pendingToastEyesOnlyAlarmWhileLocked;
                _pendingToastWhileLocked = null;
                _pendingToastDetailWhileLocked = null;
                _pendingToastEyesOnlyAlarmWhileLocked = false;

                // Only show a toast if the headline presentation changed while locked
                // (i.e., the final state/text differs from what it was at lock time).
                bool changedSinceLock =
                    pending.HasValue &&
                    (!_haveLockSnapshot ||
                    pending.Value != _headlineAtLock ||
                    pendingEyesOnlyAlarm != _headlineEyesOnlyAlarmAtLock);

                _haveLockSnapshot = false;

                if (changedSinceLock)
                {
                    var pendingState = pending.GetValueOrDefault();
                    UiSafe(() => ShowHeadlineToastOrBalloon(pendingState, pendingDetail, pendingEyesOnlyAlarm));
                }
            }
        }

        // =====================================================================
        //  Cleanup
        // =====================================================================
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                CleanupOnce();

            base.Dispose(disposing);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (RecreatingHandle)
            {
                base.OnHandleDestroyed(e);
                return;
            }

            // Safety net in case Dispose isn’t reached for some reason.
            if (!_cleanupDone && !IsDisposed && !Disposing)
                CleanupOnce();

            base.OnHandleDestroyed(e);
        }

        private void CleanupOnce()
        {
            if (_cleanupDone) return;

            _cleanupDone = true;
            _shutdown = true;

            try { SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch; } catch { }
            try { ToastHelper.BalloonNotifier = null; } catch { }

            // Stop weight monitor early so it can't wake/signal poll primitives during teardown
            StopWeightMonitor();

            var pollTask = _pollLoopTask;
            StopPollLoop();
            try { if (_pollWake.CurrentCount == 0) _pollWake.Release(); } catch { }

            try { _lifetimeCts.Cancel(); } catch { }
            try { _lifetimeCts.Dispose(); } catch { }

            if (pollTask is not null && !pollTask.IsCompleted)
            {
                _ = pollTask.ContinueWith(_ =>
                {
                    try { _pollWake.Dispose(); } catch { }
                }, TaskContinuationOptions.ExecuteSynchronously);
            }
            else
            {
                try { _pollWake.Dispose(); } catch { }
            }

            // Tray + menu
            try { tray.Visible = false; tray.Dispose(); } catch { }
            try { trayMenu.Dispose(); } catch { }

            // Burst (belt/suspenders)
            try { _burstCts?.Cancel(); } catch { }
            try { _burstCts?.Dispose(); } catch { }
            _burstCts = null;

            // HTTP
            try { _http?.Dispose(); } catch { }
            try { _handler?.Dispose(); } catch { }

            // Icons
            try { iconConnected?.Dispose(); } catch { }
            try { iconDisconnected?.Dispose(); } catch { }

            try { _opGate.Dispose(); } catch { }

            // Flush queued log lines LAST
            try { StopLoggingAsync().GetAwaiter().GetResult(); } catch { }
        }
    }
}
