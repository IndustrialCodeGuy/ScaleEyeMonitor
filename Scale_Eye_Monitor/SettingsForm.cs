namespace Scale_Eye_Monitor
{
    /*
 * SettingsForm
 * ------------
 * UI-only dialog for editing AppSettings values.
 *
 * Responsibilities:
 *   - Render controls in a scrollable layout matching AppSettings/UI order.
 *   - Perform basic input validation (strict IPv4 for Eye IP; Weight IP required when enabled).
 *   - Expose typed Value accessors for MainForm to read.
 *   - Provide UI hooks for burst diagnostics (button + status textbox), but does NOT execute burst.
 *   - Provide a single async “ApplyHandlerAsync” callback for MainForm-owned commit logic.
 *     (MainForm decides what to restart, what to save, and whether to force an immediate poll.)
 *
 * Owned by MainForm:
 *   - Persistence (settings.json), Run-at-login registry changes
 *   - Burst execution/cancellation (WireBurstTest + CTS)
 *   - All runtime decisions (restart weight monitor, recreate HttpClient, poll scheduling)
 *
 * Conditional UI:
 *   - Weight section is shown/hidden by “Enable Weight Mode”.
 *   - Burst section + status + button are shown/hidden by “Debug logging”.
 *
 * Apply behavior:
 *   - OK click locks the UI, awaits ApplyHandlerAsync, then explicitly closes on success.
 *   - DialogResult is held at None during async apply to prevent WinForms auto-close.
 */

    public sealed class SettingsForm : Form
    {
        // =====================================================================
        //  Controls (declared in the same order they appear in the UI)
        // =====================================================================

        // ---- Eye / general ----
        private readonly TextBox txtLocation = new() { Width = 260 };
        private readonly TextBox txtIp = new() { Width = 260 };

        private readonly NumericUpDown numInputId = new()
        {
            Minimum = (decimal)AppSettings.Limits.InputIdMin,
            Maximum = (decimal)AppSettings.Limits.InputIdMax,
            Increment = 1,
            Width = 60,
            ReadOnly = true
        };

        private readonly NumericUpDown numPoll = new()
        {
            Minimum = (decimal)AppSettings.Limits.PollSecondsMin,
            Maximum = (decimal)AppSettings.Limits.PollSecondsMax,
            Increment = 1,
            Width = 60,
            ReadOnly = true
        };

        private readonly NumericUpDown numEyeConfirmDelay = new()
        {
            Minimum = (decimal)AppSettings.Limits.EyeConfirmDelaySecondsMin,
            Maximum = (decimal)AppSettings.Limits.EyeConfirmDelaySecondsMax,
            Increment = 1,
            Width = 60,
            ReadOnly = true
        };

        private readonly NumericUpDown numFailRetryDelay = new()
        {
            Minimum = (decimal)AppSettings.Limits.FailureRetryDelaySecondsMin,
            Maximum = (decimal)AppSettings.Limits.FailureRetryDelaySecondsMax,
            Increment = 1,
            Width = 60,
            ReadOnly = true
        };

        private readonly NumericUpDown numFailRetryCount = new()
        {
            Minimum = (decimal)AppSettings.Limits.FailureRetryCountMin,
            Maximum = (decimal)AppSettings.Limits.FailureRetryCountMax,
            Increment = 1,
            Width = 60,
            ReadOnly = true
        };

        // ---- Run-at-login ----
        private readonly CheckBox chkRunAtLogin = new()
        {
            Text = "Start with Windows",
            AutoSize = true
        };

        // ---- Weight mode toggle ----
        private readonly CheckBox chkWeightMode = new()
        {
            Text = "Enable Weight Mode",
            AutoSize = true
        };

        // ---- Weight settings (conditional) ----
        private readonly TextBox txtWeightIp = new() { Width = 260 };

        private readonly NumericUpDown numWeightPort = new()
        {
            Minimum = (decimal)AppSettings.Limits.WeightPortMin,
            Maximum = (decimal)AppSettings.Limits.WeightPortMax,
            Increment = 1,
            Width = 60,
            ReadOnly = true
        };

        private readonly NumericUpDown numWeightPoll = new()
        {
            Minimum = (decimal)AppSettings.Limits.WeightPollSecondsMin,
            Maximum = (decimal)AppSettings.Limits.WeightPollSecondsMax,
            Increment = 1,
            Width = 60,
            ReadOnly = true
        };

        private readonly NumericUpDown numWeightEyeConfirmDelay = new()
        {
            Minimum = (decimal)AppSettings.Limits.WeightEyeConfirmDelaySecondsMin,
            Maximum = (decimal)AppSettings.Limits.WeightEyeConfirmDelaySecondsMax,
            Increment = 1,
            Width = 60,
            ReadOnly = true
        };

        private readonly NumericUpDown numPollStableNonZero = new()
        {
            Minimum = (decimal)AppSettings.Limits.PollSecondsStableNonZeroMin,
            Maximum = (decimal)AppSettings.Limits.PollSecondsStableNonZeroMax,
            Increment = 1,
            Width = 60,
            ReadOnly = true
        };

        private readonly NumericUpDown numWeightEyeConfirmDelayFast = new()
        {
            Minimum = (decimal) AppSettings.Limits.WeightEyeConfirmDelayFastSecondsMin,
            Maximum = (decimal)AppSettings.Limits.WeightEyeConfirmDelayFastSecondsMax,
            Increment = 1,
            Width = 60,
            ReadOnly = true
        };

        private readonly NumericUpDown numStableNonZeroFastWindow = new()
        {
            Minimum = (decimal)AppSettings.Limits.StableNonZeroFastWindowSecondsMin,
            Maximum = (decimal)AppSettings.Limits.StableNonZeroFastWindowSecondsMax,
            Increment = 10,
            Width = 60,
            ReadOnly = true
        };

        private readonly NumericUpDown numWeightBurstCount = new()
        {
            Minimum = (decimal)AppSettings.Limits.WeightBurstCountMin,
            Maximum = (decimal)AppSettings.Limits.WeightBurstCountMax,
            Increment = 1,
            Width = 60,
            ReadOnly = true
        };

        private readonly NumericUpDown numWeightBurstDelayMs = new()
        {
            Minimum = (decimal)AppSettings.Limits.WeightBurstDelayMsMin,
            Maximum = (decimal)AppSettings.Limits.WeightBurstDelayMsMax,
            Increment = 50,
            Width = 60,
            ReadOnly = true
        };

        private readonly NumericUpDown numWeightBurstMinTrueSuccess = new()
        {
            Minimum = (decimal)AppSettings.Limits.WeightBurstMinTrueSuccessMin,
            Maximum = (decimal)AppSettings.Limits.WeightBurstMinTrueSuccessMax,
            Increment = 1,
            Width = 60,
            ReadOnly = true
        };

        private readonly NumericUpDown numStableBand = new()
        {
            Minimum = (decimal)AppSettings.Limits.WeightStableBandMin,
            Maximum = (decimal)AppSettings.Limits.WeightStableBandMax,
            Increment = 1,
            Width = 60,
            ReadOnly = true
        };

        private readonly NumericUpDown numZeroBand = new()
        {
            Minimum = (decimal)AppSettings.Limits.WeightZeroBandMin,
            Maximum = (decimal)AppSettings.Limits.WeightZeroBandMax,
            Increment = 10,
            Width = 60,
            ReadOnly = true
        };

        private readonly NumericUpDown numWindowSeconds = new()
        {
            Minimum = (decimal)AppSettings.Limits.WeightWindowSecondsMin,
            Maximum = (decimal)AppSettings.Limits.WeightWindowSecondsMax,
            Increment = 1,
            Width = 60,
            ReadOnly = true
        };

        private readonly NumericUpDown numStaleSeconds = new()
        {
            Minimum = (decimal)AppSettings.Limits.WeightStaleSecondsMin,
            Maximum = (decimal)AppSettings.Limits.WeightStaleSecondsMax,
            Increment = 1,
            Width = 60,
            ReadOnly = true
        };

        // ---- Debug logging toggle ----
        private readonly CheckBox chkDebugLogging = new()
        {
            Text = "Debug logging (verbose)",
            AutoSize = true
        };

        // ---- Burst test settings (conditional; gated by DebugLogging) ----
        private readonly NumericUpDown numBurstCount = new()
        {
            Minimum = (decimal)AppSettings.Limits.BurstTestCountMin,
            Maximum = (decimal)AppSettings.Limits.BurstTestCountMax,
            Increment = 1,
            Width = 60,
            ReadOnly = true
        };

        private readonly NumericUpDown numBurstDelayMs = new()
        {
            Minimum = (decimal)AppSettings.Limits.BurstTestDelayMsMin,
            Maximum = (decimal)AppSettings.Limits.BurstTestDelayMsMax,
            Increment = 50,
            Width = 60,
            ReadOnly = true
        };

        // ---- Status + actions ----
        private readonly TextBox txtBurstStatus = new()
        {
            ReadOnly = true,
            Multiline = true,
            TabStop = false,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            Height = 38,
            Width = 260,
            ScrollBars = ScrollBars.Vertical
        };

        private readonly Button btnBurst = new() { Text = "IsInputOn Poll Test" };
        private readonly Button btnOK = new() { Text = "OK" };
        private readonly Button btnCancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };

        // =====================================================================
        //  Value accessors (match UI order)
        // =====================================================================
        public string LocationNameValue => txtLocation.Text.Trim();
        public string IpAddressValue => txtIp.Text.Trim();
        public int InputIdValue => (int)numInputId.Value;
        public int PollSecondsValue => (int)numPoll.Value;

        public int EyeConfirmDelaySecondsValue => (int)numEyeConfirmDelay.Value;
        public int FailureRetryDelaySecondsValue => (int)numFailRetryDelay.Value;
        public int FailureRetryCountValue => (int)numFailRetryCount.Value;

        public bool RunAtLoginEnabledValue => chkRunAtLogin.Checked;

        public bool WeightModeEnabledValue => chkWeightMode.Checked;
        public string WeightIpValue => txtWeightIp.Text.Trim();
        public int WeightPortValue => (int)numWeightPort.Value;

        public int WeightPollSecondsValue => (int)numWeightPoll.Value;
        public int WeightEyeConfirmDelaySecondsValue => (int)numWeightEyeConfirmDelay.Value;
        public int PollSecondsStableNonZeroValue => (int)numPollStableNonZero.Value;
        public int WeightEyeConfirmDelayFastSecondsValue => (int)numWeightEyeConfirmDelayFast.Value;
        public int StableNonZeroFastWindowSecondsValue => (int)numStableNonZeroFastWindow.Value;

        public int WeightBurstCountValue => (int)numWeightBurstCount.Value;
        public int WeightBurstDelayMsValue => (int)numWeightBurstDelayMs.Value;
        public int WeightBurstMinTrueSuccessValue => (int)numWeightBurstMinTrueSuccess.Value;

        public int WeightStableBandValue => (int)numStableBand.Value;
        public int WeightZeroBandValue => (int)numZeroBand.Value;
        public int WeightWindowSecondsValue => (int)numWindowSeconds.Value;
        public int WeightStaleSecondsValue => (int)numStaleSeconds.Value;

        public bool DebugLoggingValue => chkDebugLogging.Checked;
        public int BurstTestCountValue => (int)numBurstCount.Value;
        public int BurstTestDelayMsValue => (int)numBurstDelayMs.Value;

        // =====================================================================
        //  Callbacks wired by MainForm
        // =====================================================================
        public Action? BurstTestHandler { get; set; }
        public Func<Task<(bool ok, string? error)>>? ApplyHandlerAsync { get; set; }

        // =====================================================================
        //  State
        // =====================================================================
        private bool _burstRunning;

        // =====================================================================
        //  Construction
        // =====================================================================
        public SettingsForm(AppSettings s, bool firstRun, bool runAtLoginCurrent)
        {
            Text = "Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = MinimizeBox = false;

            // This is intentional (current behavior)
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            ClientSize = new Size(400, 398);
            MinimumSize = new Size(400, 398);
            MaximumSize = new Size(420, 440);

            AcceptButton = btnOK;
            CancelButton = btnCancel;

            // -----------------------------
            // Scrollable content container
            // -----------------------------
            var contentPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                Padding = new Padding(12, 12, 12, 12)
            };

            // -----------------------------
            // Main grid layout (labels + fields)
            // -----------------------------
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };

            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); // labels
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // fields

            // =================================================================
            //  Local layout helpers
            // =================================================================
            static void AddLabeledRow(TableLayoutPanel target, string labelText, Control field)
            {
                int row = target.RowCount++;
                target.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                var lbl = new Label
                {
                    Text = labelText,
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                    Margin = new Padding(0, 6, 8, 6)
                };

                field.Dock = DockStyle.Fill;
                field.Margin = new Padding(0, 3, 0, 3);

                target.Controls.Add(lbl, 0, row);
                target.Controls.Add(field, 1, row);
            }

            void AddFullWidthRow(Control c, Padding? margin = null)
            {
                int row = grid.RowCount++;
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                c.Dock = DockStyle.Fill;
                c.Margin = margin ?? new Padding(0, 3, 0, 3);

                grid.Controls.Add(c, 0, row);
                grid.SetColumnSpan(c, 2);
            }

            void AddSpacer(int height)
            {
                var p = new Panel
                {
                    Size = new Size(10, height),
                    MinimumSize = new Size(10, height),
                    Margin = new Padding(0)
                };
                AddFullWidthRow(p, new Padding(0));
            }

            // =================================================================
            //  Wrappers referenced by UI toggles
            // =================================================================
            TableLayoutPanel? weightWrap = null;
            TableLayoutPanel? burstWrap = null;
            TableLayoutPanel? actionsRow = null;

            // =================================================================
            //  Standardize checkbox look (prevents label clipping / layout jitter)
            // =================================================================
            void NormalizeCheckBoxRow(CheckBox cb)
            {
                cb.AutoSize = false;
                cb.Dock = DockStyle.Fill;
                cb.TextAlign = ContentAlignment.MiddleLeft;
                cb.AutoEllipsis = true;
                cb.Margin = new Padding(0);
                cb.Padding = new Padding(0);
            }

            NormalizeCheckBoxRow(chkRunAtLogin);
            NormalizeCheckBoxRow(chkWeightMode);
            NormalizeCheckBoxRow(chkDebugLogging);

            // =================================================================
            //  Build: Eye / general rows
            // =================================================================
            AddLabeledRow(grid, "Location:", txtLocation);
            AddLabeledRow(grid, "IP Address:", txtIp);
            AddLabeledRow(grid, "Input ID:", numInputId);
            AddLabeledRow(grid, "Poll Seconds:", numPoll);
            AddLabeledRow(grid, "Eye Confirm Delay (sec):", numEyeConfirmDelay);
            AddLabeledRow(grid, "Failure Retry Delay (sec):", numFailRetryDelay);
            AddLabeledRow(grid, "Failure Retry Count:", numFailRetryCount);

            // =================================================================
            //  Build: Run-at-login + Weight Mode toggles
            // =================================================================
            AddSpacer(8);
            AddFullWidthRow(chkRunAtLogin, new Padding(0));
            AddSpacer(8);
            AddFullWidthRow(chkWeightMode, new Padding(0));
            AddSpacer(8);

            // =================================================================
            //  Build: Weight section (wrapped so we can collapse it)
            // =================================================================
            {
                var weightSection = new TableLayoutPanel
                {
                    ColumnCount = 2,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Top,
                    Margin = new Padding(0),
                    Padding = new Padding(0)
                };
                weightSection.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
                weightSection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                AddLabeledRow(weightSection, "Weight IP:", txtWeightIp);
                AddLabeledRow(weightSection, "Weight Port:", numWeightPort);
                AddLabeledRow(weightSection, "Poll interval (weight mode default):", numWeightPoll);
                AddLabeledRow(weightSection, "Eye Confirm Delay (weight mode default, sec):", numWeightEyeConfirmDelay); // NEW
                AddLabeledRow(weightSection, "Poll interval (stable non-zero):", numPollStableNonZero);
                AddLabeledRow(weightSection, "Eye Confirm Delay (fast mode, sec):", numWeightEyeConfirmDelayFast);
                AddLabeledRow(weightSection, "Stable non-zero poll duration (sec):", numStableNonZeroFastWindow);
                AddLabeledRow(weightSection, "Poll burst count:", numWeightBurstCount);
                AddLabeledRow(weightSection, "Poll burst delay (ms):", numWeightBurstDelayMs);
                AddLabeledRow(weightSection, "Min TRUE successes:", numWeightBurstMinTrueSuccess);
                AddLabeledRow(weightSection, "Stable Band (+/- Lbs):", numStableBand);
                AddLabeledRow(weightSection, "Zero Band (abs <):", numZeroBand);
                AddLabeledRow(weightSection, "Stable Window (sec):", numWindowSeconds);
                AddLabeledRow(weightSection, "Stale After (sec):", numStaleSeconds);

                weightWrap = new TableLayoutPanel
                {
                    ColumnCount = 1,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Top,
                    Margin = new Padding(0),
                    Padding = new Padding(0)
                };
                weightWrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                var weightTopSpacer = new Panel { Height = 8, Dock = DockStyle.Top, Margin = new Padding(0) };
                var weightBottomSpacer = new Panel { Height = 8, Dock = DockStyle.Top, Margin = new Padding(0) };

                weightWrap.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                weightWrap.Controls.Add(weightTopSpacer, 0, weightWrap.RowCount++);

                weightWrap.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                weightWrap.Controls.Add(weightSection, 0, weightWrap.RowCount++);

                weightWrap.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                weightWrap.Controls.Add(weightBottomSpacer, 0, weightWrap.RowCount++);

                AddFullWidthRow(weightWrap, new Padding(0));
            }

            // =================================================================
            //  Build: Debug toggle + Burst settings (wrapped so we can collapse it)
            // =================================================================
            AddFullWidthRow(chkDebugLogging, new Padding(0));
            AddSpacer(8);

            {
                var burstSection = new TableLayoutPanel
                {
                    ColumnCount = 2,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Top,
                    Margin = new Padding(0),
                    Padding = new Padding(0)
                };
                burstSection.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
                burstSection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                AddLabeledRow(burstSection, "Burst Test Count:", numBurstCount);
                AddLabeledRow(burstSection, "Burst Delay (ms):", numBurstDelayMs);

                burstWrap = new TableLayoutPanel
                {
                    ColumnCount = 1,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Top,
                    Margin = new Padding(0),
                    Padding = new Padding(0)
                };
                burstWrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

                var burstTopSpacer = new Panel { Height = 8, Dock = DockStyle.Top, Margin = new Padding(0) };
                var burstBottomSpacer = new Panel { Height = 8, Dock = DockStyle.Top, Margin = new Padding(0) };

                burstWrap.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                burstWrap.Controls.Add(burstTopSpacer, 0, burstWrap.RowCount++);

                burstWrap.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                burstWrap.Controls.Add(burstSection, 0, burstWrap.RowCount++);

                burstWrap.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                burstWrap.Controls.Add(burstBottomSpacer, 0, burstWrap.RowCount++);

                AddFullWidthRow(burstWrap, new Padding(0));
            }

            // =================================================================
            //  Actions row (status + burst + OK/Cancel)
            // =================================================================
            {
                btnOK.Size = new Size(80, 28);
                btnCancel.Size = new Size(80, 28);
                btnBurst.Size = new Size(120, 28);

                btnBurst.Margin = new Padding(0, 0, 0, contentPanel.Padding.Bottom);
                btnOK.Margin = new Padding(10, 0, 10, 0);
                btnCancel.Margin = new Padding(0);

                btnBurst.Anchor = AnchorStyles.Left;

                var okCancelPanel = new FlowLayoutPanel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    FlowDirection = FlowDirection.RightToLeft,
                    WrapContents = false,
                    Margin = new Padding(0, 0, 0, contentPanel.Padding.Bottom),
                    Padding = new Padding(0),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right
                };
                okCancelPanel.Controls.Add(btnCancel);
                okCancelPanel.Controls.Add(btnOK);

                txtBurstStatus.Margin = new Padding(0, 0, 0, 6);
                txtBurstStatus.Dock = DockStyle.Fill;

                actionsRow = new TableLayoutPanel
                {
                    ColumnCount = 2,
                    RowCount = 2,
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Margin = new Padding(0),
                    Padding = new Padding(0)
                };

                actionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                actionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

                // Row 0: status box (collapsed to 0 when Debug is off)
                actionsRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
                // Row 1: burst button + OK/Cancel
                actionsRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                actionsRow.Controls.Add(txtBurstStatus, 0, 0);
                actionsRow.SetColumnSpan(txtBurstStatus, 2);

                actionsRow.Controls.Add(btnBurst, 0, 1);
                actionsRow.Controls.Add(okCancelPanel, 1, 1);

                AddFullWidthRow(actionsRow, new Padding(0));
            }

            // =================================================================
            //  UI behavior helpers (visibility + enabled state)
            // =================================================================
            
            void ApplyWeightUi()
            {
                bool show = chkWeightMode.Checked;
                if (weightWrap != null) weightWrap.Visible = show;

                // Keep enabled state consistent with current checkboxes
                SetUiLocked(locked: false, allowBurstButton: false);

                grid.PerformLayout();
                contentPanel.PerformLayout();
            }

            void ApplyDebugBurstUi()
            {
                // Safety: never hide burst UI while a burst is running (keeps Cancel available).
                bool show = chkDebugLogging.Checked || _burstRunning;

                if (burstWrap != null) burstWrap.Visible = show;
                txtBurstStatus.Visible = show;
                btnBurst.Visible = show;

                if (!show && !_burstRunning)
                    txtBurstStatus.Text = "";

                // Collapse the fixed-height status row when hidden
                if (actionsRow != null && actionsRow.RowStyles.Count >= 1)
                {
                    actionsRow.SuspendLayout();
                    actionsRow.RowStyles[0].SizeType = SizeType.Absolute;
                    actionsRow.RowStyles[0].Height = show ? 40 : 0;
                    actionsRow.ResumeLayout(true);
                }

                // Keep enabled state consistent with current checkboxes
                SetUiLocked(locked: false, allowBurstButton: false);

                grid.PerformLayout();
                contentPanel.PerformLayout();
            }

            // =================================================================
            //  Wheel passthrough (scroll instead of numeric changes)
            // =================================================================
            void EnableWheel(Control c) => WheelPassthrough.Enable(c, contentPanel);

            foreach (var c in new Control[]
            {
                txtLocation, txtIp, txtWeightIp,

                numInputId, numPoll, numEyeConfirmDelay, numFailRetryDelay, numFailRetryCount,

                numWeightPort, numWeightPoll, numWeightEyeConfirmDelay, numPollStableNonZero, numWeightEyeConfirmDelayFast, numStableNonZeroFastWindow,
                numWeightBurstCount, numWeightBurstDelayMs, numWeightBurstMinTrueSuccess,
                numStableBand, numZeroBand, numWindowSeconds, numStaleSeconds,

                numBurstCount, numBurstDelayMs
            })
            {
                EnableWheel(c);
            }

            // =================================================================
            //  Wire actions
            // =================================================================
            btnBurst.Click += (_, __) => BurstTestHandler?.Invoke();

            // =================================================================
            //  Add grid into scroll container
            // =================================================================
            contentPanel.Controls.Add(grid);
            Controls.Add(contentPanel);

            // =================================================================
            //  Prefill values
            // =================================================================
            chkRunAtLogin.Checked = runAtLoginCurrent;

            if (!firstRun)
            {
                txtLocation.Text = s.LocationName ?? "";
                txtIp.Text = s.IpAddress ?? "";
            }

            numInputId.Value = Math.Clamp(s.InputId, (int)numInputId.Minimum, (int)numInputId.Maximum);
            numPoll.Value = Math.Clamp(s.PollSeconds, AppSettings.Limits.PollSecondsMin, AppSettings.Limits.PollSecondsMax);

            numEyeConfirmDelay.Value = Math.Clamp(s.EyeConfirmDelaySeconds, (int)numEyeConfirmDelay.Minimum, (int)numEyeConfirmDelay.Maximum);
            numFailRetryDelay.Value = Math.Clamp(s.FailureRetryDelaySeconds, (int)numFailRetryDelay.Minimum, (int)numFailRetryDelay.Maximum);
            numFailRetryCount.Value = Math.Clamp(s.FailureRetryCount, (int)numFailRetryCount.Minimum, (int)numFailRetryCount.Maximum);

            chkWeightMode.Checked = s.WeightModeEnabled;
            txtWeightIp.Text = s.WeightIp ?? "";
            numWeightPort.Value = Math.Clamp(s.WeightPort, (int)numWeightPort.Minimum, (int)numWeightPort.Maximum);

            numWeightPoll.Value = Math.Clamp(s.WeightPollSeconds, (int)numWeightPoll.Minimum, (int)numWeightPoll.Maximum);
            
            numWeightEyeConfirmDelay.Value = Math.Clamp(
                s.WeightEyeConfirmDelaySeconds,
                (int)numWeightEyeConfirmDelay.Minimum,
                (int)numWeightEyeConfirmDelay.Maximum);

            numWeightEyeConfirmDelayFast.Value = Math.Clamp(
                s.WeightEyeConfirmDelayFastSeconds,
                (int)numWeightEyeConfirmDelayFast.Minimum,
                (int)numWeightEyeConfirmDelayFast.Maximum);

            numPollStableNonZero.Value = Math.Clamp(s.PollSecondsStableNonZero, (int)numPollStableNonZero.Minimum, (int)numPollStableNonZero.Maximum);
            
            numStableNonZeroFastWindow.Value = Math.Clamp(s.StableNonZeroFastWindowSeconds, (int)numStableNonZeroFastWindow.Minimum, (int)numStableNonZeroFastWindow.Maximum);

            numWeightBurstCount.Value = Math.Clamp(s.WeightBurstCount, (int)numWeightBurstCount.Minimum, (int)numWeightBurstCount.Maximum);
            numWeightBurstDelayMs.Value = Math.Clamp(s.WeightBurstDelayMs, (int)numWeightBurstDelayMs.Minimum, (int)numWeightBurstDelayMs.Maximum);
            numWeightBurstMinTrueSuccess.Value = Math.Clamp(
                s.WeightBurstMinTrueSuccess,
                (int)numWeightBurstMinTrueSuccess.Minimum,
                (int)numWeightBurstMinTrueSuccess.Maximum);

            numStableBand.Value = Math.Clamp(s.WeightStableBand, (int)numStableBand.Minimum, (int)numStableBand.Maximum);
            numZeroBand.Value = Math.Clamp(s.WeightZeroBand, (int)numZeroBand.Minimum, (int)numZeroBand.Maximum);
            numWindowSeconds.Value = Math.Clamp(s.WeightWindowSeconds, (int)numWindowSeconds.Minimum, (int)numWindowSeconds.Maximum);
            numStaleSeconds.Value = Math.Clamp(s.WeightStaleSeconds, (int)numStaleSeconds.Minimum, (int)numStaleSeconds.Maximum);

            numBurstCount.Value = Math.Clamp(s.BurstTestCount, (int)numBurstCount.Minimum, (int)numBurstCount.Maximum);
            numBurstDelayMs.Value = Math.Clamp(s.BurstTestDelayMs, (int)numBurstDelayMs.Minimum, (int)numBurstDelayMs.Maximum);

            chkDebugLogging.Checked = s.DebugLogging;

            // Apply initial conditional visibility
            ApplyWeightUi();
            ApplyDebugBurstUi();
            
            chkWeightMode.CheckedChanged += (_, __) => ApplyWeightUi();
            chkDebugLogging.CheckedChanged += (_, __) => ApplyDebugBurstUi();

            // =================================================================
            //  Validation + Apply (MainForm-owned)
            // =================================================================
            btnOK.Click += async (_, __) =>
            {
                // ----- validation -----
                if (string.IsNullOrWhiteSpace(IpAddressValue))
                {
                    MessageBox.Show("IP Address cannot be empty.", "Settings",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    return;
                }

                if (!IpValidator.IsStrictIPv4(IpAddressValue))
                {
                    MessageBox.Show("IP address is invalid.", "Settings",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    return;
                }

                if (WeightModeEnabledValue)
                {
                    if (string.IsNullOrWhiteSpace(WeightIpValue))
                    {
                        MessageBox.Show("Weight IP cannot be empty when Weight Mode is enabled.", "Settings",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        DialogResult = DialogResult.None;
                        return;
                    }

                    if (!IpValidator.IsStrictIPv4(WeightIpValue))
                    {
                        MessageBox.Show("Weight IP address is invalid.", "Settings",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        DialogResult = DialogResult.None;
                        return;
                    }
                }

                if (ApplyHandlerAsync is null)
                {
                    DialogResult = DialogResult.None;
                    return;
                }

                // Prevent WinForms from closing automatically while we await.
                DialogResult = DialogResult.None;

                // Hard stop for rapid double-clicks
                btnOK.Enabled = false;

                // Lock the whole dialog while we wait/apply.
                SetUiLocked(locked: true, allowBurstButton: false);

                bool closing = false;

                try
                {
                    var (ok, error) = await ApplyHandlerAsync().ConfigureAwait(true);
                    if (!ok)
                    {
                        if (!string.IsNullOrWhiteSpace(error))
                            MessageBox.Show(error, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                        // keep open; unlock in finally
                        return;
                    }

                    // Success -> close explicitly
                    closing = true;
                    DialogResult = DialogResult.OK;
                    Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    // keep open; unlock in finally
                }
                finally
                {
                    if (!closing)
                        SetUiLocked(locked: false, allowBurstButton: false);
                }
            };

            // NOTE:
            // We intentionally do NOT cancel burst.
            // MainForm.WireBurstTest() owns canceling burst work because it owns CTS + running state.
        }

        // =====================================================================
        //  Burst Test button state (MainForm toggles this during a run)
        // =====================================================================
        public void SetBurstRunning(bool running)
        {
            if (IsDisposed || Disposing) return;

            void Apply()
            {
                _burstRunning = running;
                btnBurst.Text = running ? "Cancel" : "IsInputOn Poll Test";
                btnBurst.Enabled = true;
                btnOK.Enabled = !running;
                btnCancel.Enabled = !running;
                chkDebugLogging.Enabled = !running;
            }

            if (InvokeRequired)
            {
                if (!IsHandleCreated) return;
                try { BeginInvoke((Action)Apply); } catch { }
            }
            else Apply();
        }

        public void SetBurstStatus(string text, bool append = false)
        {
            if (IsDisposed || Disposing) return;

            void Apply()
            {
                if (!append)
                {
                    txtBurstStatus.Text = text ?? "";
                    return;
                }

                if (txtBurstStatus.TextLength > 0)
                    txtBurstStatus.AppendText(Environment.NewLine);

                txtBurstStatus.AppendText(text ?? "");
            }

            if (InvokeRequired)
            {
                if (!IsHandleCreated) return;
                try { BeginInvoke((Action)Apply); } catch { }
            }
            else Apply();
        }

        // =====================================================================
        //  UI locking (used by MainForm during apply/burst)
        // =====================================================================
        public void SetUiLocked(bool locked, bool allowBurstButton)
        {
            if (IsDisposed || Disposing) return;

            void Apply()
            {
                bool en = !locked;

                // Eye / general
                txtLocation.Enabled = en;
                txtIp.Enabled = en;
                numInputId.Enabled = en;
                numPoll.Enabled = en;
                numEyeConfirmDelay.Enabled = en;
                numFailRetryDelay.Enabled = en;
                numFailRetryCount.Enabled = en;
                chkRunAtLogin.Enabled = en;

                // Weight section
                chkWeightMode.Enabled = en;
                bool weightEn = en && chkWeightMode.Checked;
                txtWeightIp.Enabled = weightEn;
                numWeightPort.Enabled = weightEn;
                numWeightPoll.Enabled = weightEn;
                numWeightEyeConfirmDelay.Enabled = weightEn;
                numPollStableNonZero.Enabled = weightEn;
                numWeightEyeConfirmDelayFast.Enabled = weightEn;
                numStableNonZeroFastWindow.Enabled = weightEn;
                numWeightBurstCount.Enabled = weightEn;
                numWeightBurstDelayMs.Enabled = weightEn;
                numWeightBurstMinTrueSuccess.Enabled = weightEn;
                numStableBand.Enabled = weightEn;
                numZeroBand.Enabled = weightEn;
                numWindowSeconds.Enabled = weightEn;
                numStaleSeconds.Enabled = weightEn;

                // Debug / burst settings
                chkDebugLogging.Enabled = en && !_burstRunning;
                bool burstSettingsEn = en && chkDebugLogging.Checked;
                numBurstCount.Enabled = burstSettingsEn;
                numBurstDelayMs.Enabled = burstSettingsEn;

                // Buttons
                btnOK.Enabled = en && !_burstRunning;
                btnCancel.Enabled = en && !_burstRunning;

                // Burst button is special: allow it when locked if we want Cancel available
                btnBurst.Enabled = allowBurstButton || burstSettingsEn || _burstRunning;

                // Always keep status box usable (scroll/select)
                txtBurstStatus.Enabled = true;
            }

            if (InvokeRequired)
            {
                if (!IsHandleCreated) return;
                try { BeginInvoke((Action)Apply); } catch { }
            }
            else Apply();
        }
    }
}
