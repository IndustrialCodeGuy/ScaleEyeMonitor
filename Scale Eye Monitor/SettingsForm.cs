namespace Scale_Eye_Monitor
{
    /*
     * SettingsForm
     * ------------
     * UI-only dialog for editing AppSettings values.
     *
     * Responsibilities:
     *   - Render controls in a scrollable layout matching AppSettings/UI order.
     *   - Perform basic input validation (SOAP endpoint URL required; Weight IP required when enabled).
     *   - Expose typed Value accessors for MainForm to read.
     *   - Provide UI hooks for burst diagnostics (button + status textbox), but does NOT execute burst.
     *   - Provide a “Settings info” link (README_Settings.txt) to explain settings behavior/meaning.
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
     *   - Burst UI is never hidden while a burst is running (keeps burst cancel available).
     *
     * Notifications:
     *   - “Notification Duration” (Short/Long/Disabled) is shown above “Start with Windows”.
     *   - “Windows notifications” reflects the current OS/app toast permission and opens Windows Settings.
     *   - “Disabled” maps to the same runtime toggle as tray “Enable notifications” (kept in sync).
     *
     * Apply behavior:
     *   - OK click locks the UI, awaits ApplyHandlerAsync, then explicitly closes on success.
     *   - DialogResult is held at None during async apply to prevent WinForms auto-close.
     *
     * UX notes:
     *   - Dialog is centered on the active/owner screen (manual positioning; not tied to main window location).
     *   - Mouse wheel is passed through to the scroll container (prevents accidental numeric changes).
     */

    public sealed class SettingsForm : Form
    {
        // =====================================================================
        //  Controls (declared in the same order they appear in the UI)
        // =====================================================================

        // ---- Eye / general ----
        private readonly TextBox txtEyeUrl = new() { Width = 260 };

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

        // ---- Notifications (above Run-at-login) ----
        private readonly WheelPassthroughComboBox cboNotificationDuration = new()
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 120
        };

        private readonly LinkLabel lnkWindowsNotifications = new()
        {
            Text = "Unknown",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft
        };

        // ---- Run-at-login ----
        private readonly GlyphOnlyCheckBox chkRunAtLogin = new()
        {
            Text = "Start with Windows",
            AutoSize = true
        };

        // ---- Weight mode toggle ----
        private readonly GlyphOnlyCheckBox chkWeightMode = new()
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
            Minimum = (decimal)AppSettings.Limits.WeightEyeConfirmDelayFastSecondsMin,
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

        private readonly NumericUpDown numBlockedGuidanceMinWeight = new()
        {
            Minimum = (decimal)AppSettings.Limits.BlockedGuidanceMinWeightMin,
            Maximum = (decimal)AppSettings.Limits.BlockedGuidanceMinWeightMax,
            Increment = 100,
            Width = 80,
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
        private readonly GlyphOnlyCheckBox chkDebugLogging = new()
        {
            Text = "Debug logging (verbose)",
            AutoSize = true
        };

        private readonly LinkLabel lnkSettingsInfo = new()
        {
            Text = "Settings info",
            AutoSize = true,
            Anchor = AnchorStyles.Right
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
        //  Small control specializations
        // =====================================================================
        private sealed class GlyphOnlyCheckBox : CheckBox
        {
            private const int WmNcHitTest = 0x0084;
            private static readonly IntPtr HtTransparent = new(-1);

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WmNcHitTest)
                {
                    Point clientPoint = PointToClient(GetScreenPoint(m.LParam));
                    if (!GetGlyphBounds().Contains(clientPoint))
                    {
                        m.Result = HtTransparent;
                        return;
                    }
                }

                base.WndProc(ref m);
            }

            private static Point GetScreenPoint(IntPtr lParam)
            {
                long value = lParam.ToInt64();
                int x = unchecked((short)(value & 0xFFFF));
                int y = unchecked((short)((value >> 16) & 0xFFFF));
                return new Point(x, y);
            }

            private Rectangle GetGlyphBounds()
            {
                using var g = CreateGraphics();
                Size glyph = CheckBoxRenderer.GetGlyphSize(
                    g,
                    System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal);

                int x = CheckAlign switch
                {
                    ContentAlignment.TopRight or ContentAlignment.MiddleRight or ContentAlignment.BottomRight
                        => ClientSize.Width - Padding.Right - glyph.Width,
                    ContentAlignment.TopCenter or ContentAlignment.MiddleCenter or ContentAlignment.BottomCenter
                        => (ClientSize.Width - glyph.Width) / 2,
                    _ => Padding.Left
                };

                int y = CheckAlign switch
                {
                    ContentAlignment.BottomLeft or ContentAlignment.BottomCenter or ContentAlignment.BottomRight
                        => ClientSize.Height - Padding.Bottom - glyph.Height,
                    ContentAlignment.TopLeft or ContentAlignment.TopCenter or ContentAlignment.TopRight
                        => Padding.Top,
                    _ => (ClientSize.Height - glyph.Height) / 2
                };

                return new Rectangle(x, y, glyph.Width, glyph.Height);
            }
        }

        private sealed class WheelPassthroughComboBox : ComboBox
        {
            public Control? WheelScrollTarget { get; set; }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WheelPassthrough.WmMouseWheel && !DroppedDown)
                {
                    Control? target = WheelScrollTarget;
                    if (target is not null && WheelPassthrough.ForwardRawWheel(target, m.WParam, m.LParam))
                        return;
                }

                base.WndProc(ref m);
            }
        }

        // =====================================================================
        //  Value accessors (match UI order)
        // =====================================================================
        public string EyeUrlValue => txtEyeUrl.Text.Trim();
        public int InputIdValue => (int)numInputId.Value;
        public int PollSecondsValue => (int)numPoll.Value;

        public int EyeConfirmDelaySecondsValue => (int)numEyeConfirmDelay.Value;
        public int FailureRetryDelaySecondsValue => (int)numFailRetryDelay.Value;
        public int FailureRetryCountValue => (int)numFailRetryCount.Value;
        public string NotificationDurationValue => (cboNotificationDuration.SelectedItem as string) ?? "Short";
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
        public int BlockedGuidanceMinWeightValue => (int)numBlockedGuidanceMinWeight.Value;

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

        // SettingsForm builds most layout helpers as constructor-local functions.
        // Keep a thin field bridge so Form.OnDpiChanged can use the same sizing
        // path as startup/layout changes instead of relying on a constructor-local
        // DpiChanged event subscription.
        private Action<int?>? _applyLayoutMetricsForDpi;
        private Action? _centerOnActiveScreen;

        // =====================================================================
        //  Construction
        // =====================================================================
        public SettingsForm(AppSettings s, bool firstRun, bool runAtLoginCurrent)
        {
            AutoScaleMode = AutoScaleMode.None;
            Font = SystemFonts.MessageBoxFont;

            Text = "Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.Manual;
            MaximizeBox = MinimizeBox = false;

            // Keep the dialog itself on the same explicit ClientSize DPI path as MainForm.
            // FixedDialog prevents user resizing, so avoid MinimumSize/MaximumSize locks;
            // stale bounds can block live DPI width changes during a monitor/DPI move.
            AutoSize = false;
            ClientSize = new Size(380, 398);
            MinimumSize = Size.Empty;
            MaximumSize = Size.Empty;

            void CenterOnActiveScreen()
            {
                var screen = Owner is not null ? Screen.FromControl(Owner) : Screen.FromPoint(Cursor.Position);
                var wa = screen.WorkingArea;
                int x = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
                int y = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
                Location = new Point(x, y);
            }

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

            var scaledLabelColumns = new List<ColumnStyle>();
            var labeledRowLabels = new List<Label>();
            var labeledRowFields = new List<Control>();
            var spacerPanels = new List<(Panel Panel, int HeightDip)>();

            var gridLabelColumn = new ColumnStyle(SizeType.Absolute, 160);
            grid.ColumnStyles.Add(gridLabelColumn); // labels
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // fields
            scaledLabelColumns.Add(gridLabelColumn);

            // =================================================================
            //  Local layout helpers
            // =================================================================
            void AddLabeledRow(TableLayoutPanel target, string labelText, Control field)
            {
                int row = target.RowCount++;
                target.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                var lbl = new Label
                {
                    Text = labelText,
                    AutoSize = true,
                    Anchor = AnchorStyles.Left
                };

                field.Dock = DockStyle.Fill;

                labeledRowLabels.Add(lbl);
                labeledRowFields.Add(field);

                target.Controls.Add(lbl, 0, row);
                target.Controls.Add(field, 1, row);
            }

            void AddFullWidthRow(Control c, Padding? margin = null)
            {
                int row = grid.RowCount++;
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                if (c is CheckBox)
                {
                    c.Dock = DockStyle.None;
                    c.Anchor = AnchorStyles.Left;
                }
                else
                {
                    c.Dock = DockStyle.Fill;
                }

                c.Margin = margin ?? new Padding(0, 3, 0, 3);

                grid.Controls.Add(c, 0, row);
                grid.SetColumnSpan(c, 2);
            }

            void AddSpacer(int heightDip)
            {
                var p = new Panel
                {
                    Size = new Size(10, heightDip),
                    MinimumSize = new Size(10, heightDip),
                    Margin = new Padding(0)
                };
                spacerPanels.Add((p, heightDip));
                AddFullWidthRow(p, new Padding(0));
            }

            // =================================================================
            //  Wrappers referenced by UI toggles
            // =================================================================
            TableLayoutPanel? weightWrap = null;
            TableLayoutPanel? burstWrap = null;
            TableLayoutPanel? actionsRow = null;
            FlowLayoutPanel? okCancelPanel = null;

            // =================================================================
            //  Standardize checkbox look (prevents label clipping / layout jitter)
            // =================================================================
            void NormalizeCheckBoxRow(CheckBox cb)
            {
                cb.AutoSize = true;
                cb.Dock = DockStyle.None;
                cb.Anchor = AnchorStyles.Left;
                cb.TextAlign = ContentAlignment.MiddleLeft;
                cb.AutoEllipsis = false;
                cb.Margin = new Padding(0);
                cb.Padding = new Padding(0);
            }

            NormalizeCheckBoxRow(chkRunAtLogin);
            NormalizeCheckBoxRow(chkWeightMode);
            NormalizeCheckBoxRow(chkDebugLogging);

            lnkSettingsInfo.Margin = new Padding(0);
            lnkWindowsNotifications.Margin = new Padding(0);

            cboNotificationDuration.Items.AddRange(["Short", "Long", "Disabled"]);

            // =================================================================
            //  Build: Eye / general rows
            // =================================================================
            AddLabeledRow(grid, "Eye endpoint URL:", txtEyeUrl);
            AddLabeledRow(grid, "Input ID:", numInputId);
            AddLabeledRow(grid, "Poll Interval (sec):", numPoll);
            AddLabeledRow(grid, "Eye Confirm Delay (sec):", numEyeConfirmDelay);
            AddLabeledRow(grid, "Failure Retry Delay (sec):", numFailRetryDelay);
            AddLabeledRow(grid, "Failure Retry Count:", numFailRetryCount);

            // =================================================================
            //  Build: Run-at-login + Weight Mode toggles
            // =================================================================
            AddSpacer(8);
            AddLabeledRow(grid, "Notification Duration:", cboNotificationDuration);
            AddLabeledRow(grid, "Windows notifications:", lnkWindowsNotifications);
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
                    ColumnCount = 3,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Top,
                    Margin = new Padding(0),
                    Padding = new Padding(0)
                };
                var weightLabelColumn = new ColumnStyle(SizeType.Absolute, 150);
                weightSection.ColumnStyles.Add(weightLabelColumn);
                weightSection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                scaledLabelColumns.Add(weightLabelColumn);

                AddLabeledRow(weightSection, "Weight IP:", txtWeightIp);
                AddLabeledRow(weightSection, "Weight Port:", numWeightPort);
                AddLabeledRow(weightSection, "Poll interval (sec):", numWeightPoll);
                AddLabeledRow(weightSection, "Eye Confirm Delay (sec):", numWeightEyeConfirmDelay);
                AddLabeledRow(weightSection, "Poll interval (stable non-zero, sec):", numPollStableNonZero);
                AddLabeledRow(weightSection, "Eye Confirm Delay (fast mode, sec):", numWeightEyeConfirmDelayFast);
                AddLabeledRow(weightSection, "Stable non-zero poll duration (sec):", numStableNonZeroFastWindow);
                AddLabeledRow(weightSection, "Poll burst count:", numWeightBurstCount);
                AddLabeledRow(weightSection, "Poll burst delay (ms):", numWeightBurstDelayMs);
                AddLabeledRow(weightSection, "Min TRUE successes:", numWeightBurstMinTrueSuccess);
                AddLabeledRow(weightSection, "Blocked msg min weight:", numBlockedGuidanceMinWeight);
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
                spacerPanels.Add((weightTopSpacer, 8));
                spacerPanels.Add((weightBottomSpacer, 8));

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

            {
                var debugRow = new TableLayoutPanel
                {
                    ColumnCount = 3,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Top,
                    Margin = new Padding(0),
                    Padding = new Padding(0)
                };

                debugRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                debugRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                debugRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

                chkDebugLogging.Dock = DockStyle.None;
                chkDebugLogging.Anchor = AnchorStyles.Left;
                lnkSettingsInfo.Dock = DockStyle.Fill;
                lnkSettingsInfo.TextAlign = ContentAlignment.MiddleRight;

                debugRow.Controls.Add(chkDebugLogging, 0, 0);
                debugRow.Controls.Add(lnkSettingsInfo, 2, 0);
                AddFullWidthRow(debugRow, new Padding(0));
            }

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
                var burstLabelColumn = new ColumnStyle(SizeType.Absolute, 150);
                burstSection.ColumnStyles.Add(burstLabelColumn);
                burstSection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                scaledLabelColumns.Add(burstLabelColumn);

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
                spacerPanels.Add((burstTopSpacer, 8));
                spacerPanels.Add((burstBottomSpacer, 8));

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

                okCancelPanel = new FlowLayoutPanel
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
                actionsRow.Controls.Add(okCancelPanel!, 1, 1);

                AddFullWidthRow(actionsRow, new Padding(0));
            }

            void ApplySettingsLayoutMetrics(int? dpi = null)
            {
                if (IsDisposed || Disposing) return;

                var m = dpi.HasValue ? new DpiMetrics(dpi.Value) : DpiLayout.For(this);

                SuspendLayout();
                contentPanel.SuspendLayout();
                grid.SuspendLayout();
                actionsRow?.SuspendLayout();

                try
                {
                    // Match MainForm: set the scaled client area directly and do not
                    // re-lock the outer form bounds. FormBorderStyle.FixedDialog already
                    // prevents user resizing, and stale min/max bounds can block live DPI
                    // size changes.
                    MinimumSize = Size.Empty;
                    MaximumSize = Size.Empty;
                    AutoSize = false;
                    ClientSize = m.Size(380, 398);

                    contentPanel.Padding = m.Padding(12);

                    foreach (var column in scaledLabelColumns)
                        column.Width = m.Scale(160);

                    int rowH = m.TextRowHeight(Font);
                    int fieldH = m.FieldHeight(Font);

                    foreach (var lbl in labeledRowLabels)
                    {
                        lbl.Margin = m.Padding(0, 6, 8, 6);
                        lbl.MinimumSize = new Size(0, rowH);
                    }

                    foreach (var field in labeledRowFields)
                    {
                        field.Margin = m.Padding(0, 3, 0, 3);
                        field.MinimumSize = new Size(0, fieldH);
                    }

                    foreach (var (panel, heightDip) in spacerPanels)
                    {
                        var size = m.Size(10, heightDip);
                        panel.Size = size;
                        panel.MinimumSize = size;
                        panel.Height = size.Height;
                    }

                    foreach (var cb in new[] { chkRunAtLogin, chkWeightMode, chkDebugLogging })
                    {
                        cb.MinimumSize = new Size(0, rowH);
                        cb.Height = rowH;
                    }

                    lnkSettingsInfo.MinimumSize = new Size(0, rowH);
                    lnkWindowsNotifications.MinimumSize = new Size(0, rowH);
                    cboNotificationDuration.MinimumSize = new Size(0, fieldH);

                    btnOK.Size = m.Size(80, 28);
                    btnCancel.Size = m.Size(80, 28);
                    btnBurst.Size = m.Size(120, 28);

                    btnBurst.Margin = m.Padding(0, 0, 0, 12);
                    btnOK.Margin = m.Padding(10, 0, 10, 0);
                    btnCancel.Margin = new Padding(0);
                    if (okCancelPanel is not null)
                        okCancelPanel.Margin = m.Padding(0, 0, 0, 12);

                    txtBurstStatus.Margin = m.Padding(0, 0, 0, 6);
                    txtBurstStatus.MinimumSize = new Size(0, m.Scale(40));

                    if (actionsRow != null && actionsRow.RowStyles.Count >= 1)
                    {
                        actionsRow.RowStyles[0].SizeType = SizeType.Absolute;
                        actionsRow.RowStyles[0].Height = txtBurstStatus.Visible ? m.Scale(40) : 0;
                    }

                    grid.PerformLayout();
                    contentPanel.PerformLayout();
                }
                finally
                {
                    actionsRow?.ResumeLayout(true);
                    grid.ResumeLayout(true);
                    contentPanel.ResumeLayout(true);
                    ResumeLayout(true);
                }
            }

            // Let the Form.OnDpiChanged override reuse the same constructor-local
            // metrics path that startup and conditional UI changes use.
            _applyLayoutMetricsForDpi = ApplySettingsLayoutMetrics;
            _centerOnActiveScreen = CenterOnActiveScreen;

            // =================================================================
            //  UI behavior helpers (visibility + enabled state)
            // =================================================================

            void ApplyWeightUi()
            {
                bool show = chkWeightMode.Checked;
                if (weightWrap != null) weightWrap.Visible = show;

                // Keep enabled state consistent with current checkboxes
                SetUiLocked(locked: false, allowBurstButton: false);

                ApplySettingsLayoutMetrics();
                grid.PerformLayout();
                contentPanel.PerformLayout();
            }

            void ApplyDebugBurstUi()
            {
                // Safety: never hide burst UI while a burst is running (keeps burst cancel available).
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
                    actionsRow.RowStyles[0].Height = show ? DpiLayout.For(this).Scale(40) : 0;
                    actionsRow.ResumeLayout(true);
                }

                // Keep enabled state consistent with current checkboxes
                SetUiLocked(locked: false, allowBurstButton: false);

                ApplySettingsLayoutMetrics();
                grid.PerformLayout();
                contentPanel.PerformLayout();
            }

            // =================================================================
            //  Wheel passthrough (scroll instead of numeric changes)
            // =================================================================
            void EnableWheel(Control c) => WheelPassthrough.Enable(c, contentPanel);
            cboNotificationDuration.WheelScrollTarget = contentPanel;

            foreach (var c in new Control[]
            {
                txtEyeUrl, txtWeightIp,

                numInputId, numPoll, numEyeConfirmDelay, numFailRetryDelay, numFailRetryCount,

                numWeightPort, numWeightPoll, numWeightEyeConfirmDelay, numPollStableNonZero,
                numWeightEyeConfirmDelayFast, numStableNonZeroFastWindow,
                numWeightBurstCount, numWeightBurstDelayMs, numWeightBurstMinTrueSuccess,
                numBlockedGuidanceMinWeight,
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
                txtEyeUrl.Text = s.EyeUrl ?? "";
            }

            numInputId.Value = Math.Clamp(s.InputId, (int)numInputId.Minimum, (int)numInputId.Maximum);
            numPoll.Value = Math.Clamp(s.PollSeconds, AppSettings.Limits.PollSecondsMin, AppSettings.Limits.PollSecondsMax);

            numEyeConfirmDelay.Value = Math.Clamp(s.EyeConfirmDelaySeconds, (int)numEyeConfirmDelay.Minimum, (int)numEyeConfirmDelay.Maximum);
            numFailRetryDelay.Value = Math.Clamp(s.FailureRetryDelaySeconds, (int)numFailRetryDelay.Minimum, (int)numFailRetryDelay.Maximum);
            numFailRetryCount.Value = Math.Clamp(s.FailureRetryCount, (int)numFailRetryCount.Minimum, (int)numFailRetryCount.Maximum);

            SetNotificationDurationUi(s.NotificationsEnabled, s.NotificationDuration);

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

            // Keep "Min TRUE successes" <= "Burst count" at the UI level (so users can't select an impossible value).
            void SyncMinTrueMax()
            {
                int max = (int)numWeightBurstCount.Value;
                int min = (int)numWeightBurstMinTrueSuccess.Minimum;
                if (max < min) max = min;

                numWeightBurstMinTrueSuccess.Maximum = max;
                if (numWeightBurstMinTrueSuccess.Value > max)
                    numWeightBurstMinTrueSuccess.Value = max;
            }

            SyncMinTrueMax();
            numWeightBurstCount.ValueChanged += (_, __) => SyncMinTrueMax();

            numBlockedGuidanceMinWeight.Value = Math.Clamp(
                s.BlockedGuidanceMinWeight,
                (int)numBlockedGuidanceMinWeight.Minimum,
                (int)numBlockedGuidanceMinWeight.Maximum);

            numStableBand.Value = Math.Clamp(s.WeightStableBand, (int)numStableBand.Minimum, (int)numStableBand.Maximum);
            numZeroBand.Value = Math.Clamp(s.WeightZeroBand, (int)numZeroBand.Minimum, (int)numZeroBand.Maximum);
            numWindowSeconds.Value = Math.Clamp(s.WeightWindowSeconds, (int)numWindowSeconds.Minimum, (int)numWindowSeconds.Maximum);
            numStaleSeconds.Value = Math.Clamp(s.WeightStaleSeconds, (int)numStaleSeconds.Minimum, (int)numStaleSeconds.Maximum);

            numBurstCount.Value = Math.Clamp(s.BurstTestCount, (int)numBurstCount.Minimum, (int)numBurstCount.Maximum);
            numBurstDelayMs.Value = Math.Clamp(s.BurstTestDelayMs, (int)numBurstDelayMs.Minimum, (int)numBurstDelayMs.Maximum);

            chkDebugLogging.Checked = s.DebugLogging;

            // Apply initial conditional visibility and DPI metrics.
            ApplyWeightUi();
            ApplyDebugBurstUi();

            Load += (_, __) =>
            {
                RefreshWindowsNotificationLink();
                ApplySettingsLayoutMetrics();
                CenterOnActiveScreen();
            };

            Activated += (_, __) => RefreshWindowsNotificationLink();

            // Live DPI changes are handled by OnDpiChanged, matching MainForm.
            chkWeightMode.CheckedChanged += (_, __) => ApplyWeightUi();
            chkDebugLogging.CheckedChanged += (_, __) => ApplyDebugBurstUi();

            lnkSettingsInfo.LinkClicked += (_, __) => OpenReadmeSettings();
            lnkWindowsNotifications.LinkClicked += (_, __) => OpenWindowsNotificationSettings();

            // =================================================================
            //  Validation + Apply (MainForm-owned)
            // =================================================================
            btnOK.Click += async (_, __) =>
            {
                // ----- validation -----
                if (string.IsNullOrWhiteSpace(EyeUrlValue))
                {
                    MessageBox.Show("SOAP endpoint URL cannot be empty.", "Settings",
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

        private void UiSafe(Action apply)
        {
            if (IsDisposed || Disposing) return;

            if (InvokeRequired)
            {
                if (!IsHandleCreated) return;
                try { BeginInvoke((Action)apply); } catch { }
            }
            else apply();
        }

        private void RefreshWindowsNotificationLink()
        {
            void Apply()
            {
                lnkWindowsNotifications.Text = ToastHelper.GetWindowsNotificationStatusText();
            }

            UiSafe(Apply);
        }

        private static void OpenWindowsNotificationSettings()
        {
            if (!ToastHelper.OpenWindowsNotificationSettings(out string? error) &&
                !string.IsNullOrWhiteSpace(error))
            {
                MessageBox.Show(error, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static void OpenReadmeSettings()
        {
            // Expected to live next to the EXE when published (same folder as README_Program.txt).
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "README_Settings.txt");

            try
            {
                if (!File.Exists(path))
                {
                    MessageBox.Show(
                    $"Settings info not found:\n{path}\n\nAdd README_Settings.txt next to the exe.",
                    "Settings",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                    return;
                }

                System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                // Match the Apply() failure style used elsewhere in this dialog.
                MessageBox.Show(ex.Message, "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public void SetNotificationDurationUi(bool enabled, string durationShortOrLong)
        {
            void Apply()
            {
                string want = !enabled
                    ? "Disabled"
                    : (string.Equals(durationShortOrLong, "Long", StringComparison.OrdinalIgnoreCase) ? "Long" : "Short");

                int idx = cboNotificationDuration.FindStringExact(want);
                if (idx < 0) idx = cboNotificationDuration.FindStringExact("Short");
                if (idx < 0 && cboNotificationDuration.Items.Count > 0) idx = 0;

                if (idx >= 0) cboNotificationDuration.SelectedIndex = idx;
            }

            UiSafe(Apply);
        }

        // =====================================================================
        //  Burst Test button state (MainForm toggles this during a run)
        // =====================================================================
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);

            // Use the DPI from the event, not DeviceDpi, so the live resize uses
            // the target DPI immediately. This mirrors MainForm's override path
            // while preserving SettingsForm's constructor-local layout helpers.
            _applyLayoutMetricsForDpi?.Invoke(e.DeviceDpiNew);
            _centerOnActiveScreen?.Invoke();
        }

        public void SetBurstRunning(bool running)
        {
            void Apply()
            {
                _burstRunning = running;
                btnBurst.Text = running ? "Cancel" : "IsInputOn Poll Test";
                btnBurst.Enabled = true;
                btnOK.Enabled = !running;
                btnCancel.Enabled = !running;
                chkDebugLogging.Enabled = !running;
            }

            UiSafe(Apply);
        }

        public void SetBurstStatus(string text, bool append = false)
        {
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

            UiSafe(Apply);
        }

        // =====================================================================
        //  UI locking (used by MainForm during apply/burst)
        // =====================================================================
        public void SetUiLocked(bool locked, bool allowBurstButton)
        {
            void Apply()
            {
                bool en = !locked;

                // Eye / general
                txtEyeUrl.Enabled = en;
                numInputId.Enabled = en;
                numPoll.Enabled = en;
                numEyeConfirmDelay.Enabled = en;
                numFailRetryDelay.Enabled = en;
                numFailRetryCount.Enabled = en;
                cboNotificationDuration.Enabled = en;
                lnkWindowsNotifications.Enabled = en;
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
                numBlockedGuidanceMinWeight.Enabled = weightEn;
                numStableBand.Enabled = weightEn;
                numZeroBand.Enabled = weightEn;
                numWindowSeconds.Enabled = weightEn;
                numStaleSeconds.Enabled = weightEn;

                // Debug / burst settings
                chkDebugLogging.Enabled = en && !_burstRunning;
                lnkSettingsInfo.Enabled = en;
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

            UiSafe(Apply);
        }
    }
}
