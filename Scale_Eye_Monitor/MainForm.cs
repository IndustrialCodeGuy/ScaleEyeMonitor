namespace Scale_Eye_Monitor
{
    /*
 * MainForm (Scale Eye Monitor) - Partial class split
 * --------------------------------------------------
 * Purpose:
 *   Monitor Kahler scale eye input (SOAP over HTTP) and present a unified status via:
 *     - Small status window (labels)
 *     - Tray icon + tooltip
 *     - Optional toasts (headline transitions only)
 *
 * Core model:
 *   - UI/tray/toasts are driven by a single stable “headline” state:
 *       Unknown / OK / Blocked / Alignment off / Disconnected
 *   - “Detail” text is informational only and does NOT change tray text, icons, or toasts.
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
 *   2) Load settings.json (or defaults). If missing/invalid IP, show Settings dialog.
 *   3) Apply settings to runtime fields.
 *   4) Start/stop weight monitor (if WeightModeEnabled and endpoint valid).
 *   5) Initialize HttpClient (device-compat settings: HTTP/1.0, ConnectionClose, etc.).
 *   6) Start polling scheduler.
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
        private readonly Label lblStatus = new() { AutoSize = true, Text = "Status: (initializing…)" };
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

        
        // Stable “headline” state (UI + tray + toasts are driven ONLY by this)
        private HeadlineState _headline = HeadlineState.Unknown;
        private volatile bool _shutdown = false;

        private bool _allowExit = false;    // if false, closing hides to tray
        private readonly bool _startHidden; // --tray

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

            // --- basic form ---
            Text = "Scale Eye Monitor";
            Width = 300;
            Height = 200;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;

            // --- layout ---
            const int pad = 6;        // left/right padding
            const int topPad = 6;     // extra space above the first line
            const int bottomPad = 6;
            const int rowH = 20;
            const int gap = 2;

            int mid = ClientSize.Width / 2;

            // Row 1 (Status)
            lblStatus.AutoSize = false;
            lblStatus.AutoEllipsis = true;
            lblStatus.SetBounds(pad, topPad, ClientSize.Width - (pad * 2), rowH);

            // Row 2 (Detail)
            lblDetail.AutoSize = false;
            lblDetail.AutoEllipsis = true;
            lblDetail.SetBounds(pad, lblStatus.Bottom + gap, ClientSize.Width - (pad * 2), rowH);

            // Row 3 (Last Poll | HTTP Code)
            int row3Top = lblDetail.Bottom + gap;

            // Last Poll on left half
            lblLast.AutoSize = false;
            lblLast.AutoEllipsis = true;
            lblLast.TextAlign = ContentAlignment.MiddleLeft;
            lblLast.SetBounds(pad, row3Top, mid - (pad * 2), rowH);

            // HTTP Code on right half
            lblHttp.AutoSize = false;
            lblHttp.AutoEllipsis = true;
            lblHttp.TextAlign = ContentAlignment.MiddleLeft;
            lblHttp.SetBounds(mid, row3Top, ClientSize.Width - mid - pad, rowH);


            // Row 4 (Check button)
            btnCheck.SetBounds(pad, lblHttp.Bottom + gap, 100, rowH + 4);

            // Row 4 right column (Logs link aligned with Last Poll column)
            lnkLogs.AutoSize = false;
            lnkLogs.AutoEllipsis = true;
            lnkLogs.TextAlign = ContentAlignment.MiddleRight;
            lnkLogs.SetBounds(mid, btnCheck.Top, ClientSize.Width - mid - pad, btnCheck.Height);

            // Tighten window height to content
            ClientSize = new Size(ClientSize.Width, btnCheck.Bottom + bottomPad);


            Controls.Add(lblStatus);
            Controls.Add(lblDetail);
            Controls.Add(lblLast);
            Controls.Add(lblHttp);
            Controls.Add(btnCheck);
            Controls.Add(lnkLogs);

            // --- UI events ---
            btnCheck.Click += CheckNow_Click;
            lnkLogs.LinkClicked += (_, __) => OpenLogFolder();

            // --- command-line mode ---
            _startHidden = Environment.GetCommandLineArgs().Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

            // --- tray setup ---
            var connectedIconPath = Path.Combine(Application.StartupPath, "ScaleEye_Connected.ico");
            try { iconConnected = new Icon(connectedIconPath); } catch { iconConnected = (Icon)SystemIcons.Application.Clone(); }

            var disconnectedIconPath = Path.Combine(Application.StartupPath, "ScaleEye_Disconnected.ico");
            try { iconDisconnected = new Icon(disconnectedIconPath); } catch { iconDisconnected = (Icon)SystemIcons.Application.Clone(); }

            tray.Icon = iconDisconnected;
            this.Icon = tray.Icon;
            tray.Visible = true;

            var miSettings = new ToolStripMenuItem(" Settings…", null, Settings_Click);
            var miReadme = new ToolStripMenuItem(" Readme");
            var miCheck = new ToolStripMenuItem(" Check Now", null, CheckNow_Click);
            var miLogs = new ToolStripMenuItem(" Open Logs Folder");
            _miAlwaysOnTop = new ToolStripMenuItem(" Always on top", null, (_, __) => ToggleAlwaysOnTop());
            var miExit = new ToolStripMenuItem(" Exit", null, (_, __) =>
            {
                _allowExit = true;
                Close();
            });

            miReadme.Click += (_, __) => OpenReadme();
            miLogs.Click += (_, __) => OpenLogFolder();

            trayMenu.ShowImageMargin = false;
            trayMenu.ShowCheckMargin = false;

            trayMenu.Items.Add(miSettings);
            trayMenu.Items.Add(miReadme);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(miCheck);
            trayMenu.Items.Add(miLogs);
            trayMenu.Items.Add(_miAlwaysOnTop);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(miExit);

            tray.ContextMenuStrip = trayMenu;

            // show window on tray left-click
            tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowStatusWindow(); };

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

            // Ensures the app has a toast-capable shortcut (if you’re using toasts elsewhere)
            ToastHelper.EnsureShortcut(s.DebugLogging);

            // We require a strict IPv4 for the eye device
            bool weightIpBad = s.WeightModeEnabled && (string.IsNullOrWhiteSpace(s.WeightIp) || !IpValidator.IsStrictIPv4(s.WeightIp));

            bool needsDialog =
                !existed ||
                string.IsNullOrWhiteSpace(s.IpAddress) ||
                !IpValidator.IsStrictIPv4(s.IpAddress) ||
                weightIpBad;

            if (needsDialog)
            {
                // First run: blank Location/IP but keep numeric defaults
                var initialForDialog = existed ? s : new AppSettings
                {
                    // Eye / general
                    LocationName = s.LocationName,
                    IpAddress = s.IpAddress,
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

                    // Debug / burst
                    DebugLogging = s.DebugLogging,
                    BurstTestCount = s.BurstTestCount,
                    BurstTestDelayMs = s.BurstTestDelayMs
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
                    if (IsBurstRunning)
                    {
                        CancelBurstTest();
                        return Task.FromResult<(bool ok, string? error)>(
                            (false, "Burst test is running and is being canceled. Wait for it to stop, then click OK again."));
                    }

                    var (ok, err) = CommitSettingsFromDialog(dlg, dlgSettings);
                    if (!ok)
                        return Task.FromResult<(bool ok, string? error)>((false, err));

                    CopyInto(s, dlgSettings);
                    return Task.FromResult<(bool ok, string? error)>((true, null));
                };

                var result = dlg.ShowDialog(this);

                if (result != DialogResult.OK)
                {
                    // True first-run + user cancels => exit app
                    if (!existed)
                    {
                        // Ensure this is a real exit, not "hide to tray".
                        BeginInvoke(new Action(() => { _allowExit = true; Close(); }));
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
            SetTrayTextSafe($"Scale Eye Monitor ({LocationName})");

            // Init HTTP client after settings are known
            InitHttp();

            // Start poll loop after settings are known
            _desiredPollSeconds = PollSeconds;
            StartPollLoop();

            // Initial window state
            Shown += (_, __) =>
            {
                if (_startHidden)
                {
                    Hide();
                    WindowState = FormWindowState.Minimized;
                }
            };
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
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            if (IsDisposed) return;
                            ShowStatusWindow();
                        }));
                    }
                    catch { }
                }
            }

            base.WndProc(ref m);
        }

        // =====================================================================
        //  Cleanup
        // =====================================================================
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CleanupOnce();
            }
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
