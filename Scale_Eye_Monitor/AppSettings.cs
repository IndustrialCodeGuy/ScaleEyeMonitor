using System.Text.Json;

namespace Scale_Eye_Monitor
{
    /*
 * AppSettings
 * -----------
 * Persisted settings model + load/save helpers + clamp/normalize.
 *
 * Primary goal:
 *   Keep SettingsForm UI order, JSON property order, and MainForm.ApplySettings order aligned
 *   so settings remain easy to reason about and diff.
 *
 * UI / property ordering (matches SettingsForm):
 *   1) Eye settings
 *   2) Start with Windows (stored in HKCU Run key; NOT persisted here)
 *   3) Weight Mode toggle + weight settings
 *   4) Debug logging
 *   5) Burst test settings
 *
 * Weight Mode settings (current behavior):
 *   - WeightModeEnabled gates whether the weight monitor runs.
 *   - WeightPollSeconds is the default polling interval when weight is usable
 *     (StableZero or StableNonZero after fast-window).
 *   - PollSecondsStableNonZero + StableNonZeroFastWindowSeconds form the “fast window”
 *     when a truck first reaches StableNonZero (prevents fast polling all night).
 *   - Confirm delay is selected from the same timing branch as poll seconds:
 *       * EyeConfirmDelaySeconds (eyes-only OR weight unavailable)
 *       * WeightEyeConfirmDelaySeconds (normal weight-mode)
 *       * WeightEyeConfirmDelayFastSeconds (StableNonZero fast window)
 *   - Weight burst settings are used by MainForm.Polling only when weight mode is usable
 *     AND the weight state is StableZero (anti-false-positive burst on Eye #1).
 *
 * Notes:
 *   - NormalizeAndClamp() is called on load and before save.
 *   - LoadOrDefaults includes back-compat shims for older JSON keys.
 *   - “Start with Windows” is intentionally NOT persisted here.
 */

    public sealed class AppSettings
    {
        // =====================================================================
        //  Eye / general (UI order)
        // =====================================================================
        public string LocationName { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public int InputId { get; set; } = 0;

        // Base polling interval (eyes-only and default when not in stable-nonzero fast mode)
        public int PollSeconds { get; set; } = 15;

        // Seconds between Eye check #1 and Eye check #2
        public int EyeConfirmDelaySeconds { get; set; } = 15;

        // Failure policy
        public int FailureRetryDelaySeconds { get; set; } = 5; // wait time between retries
        public int FailureRetryCount { get; set; } = 3;        // failures-in-a-row before forcing disconnect

        // =====================================================================
        //  Weight mode (UI order)
        // =====================================================================
        public bool WeightModeEnabled { get; set; } = false;

        public string WeightIp { get; set; } = "";
        public int WeightPort { get; set; } = 4662;

        // Weight-mode default poll interval (used for StableZero and “normal” polling)
        public int WeightPollSeconds { get; set; } = 15;

        // NEW: confirm delay used when Weight Mode is usable (enabled + not Unavailable)
        public int WeightEyeConfirmDelaySeconds { get; set; } = 15;

        // Eye poll interval when stable non-zero weight is present
        public int PollSecondsStableNonZero { get; set; } = 5;

        // Seconds between Eye check #1 and Eye check #2 during StableNonZero fast-window
        public int WeightEyeConfirmDelayFastSeconds { get; set; } = 15;

        // How long we keep the fast StableNonZero poll rate after entering StableNonZero.
        // After this, we revert to WeightPollSeconds until next motion event.
        public int StableNonZeroFastWindowSeconds { get; set; } = 300;

        // Weight-mode “normal poll burst” (StableZero and post-timeout StableNonZero)
        public int WeightBurstCount { get; set; } = 3;
        public int WeightBurstDelayMs { get; set; } = 200;

        // Require at least N successful TRUE samples to treat burst as TRUE-candidate
        // (Any successful FALSE still returns OK immediately.)
        public int WeightBurstMinTrueSuccess { get; set; } = 1;

        // Stability gating (weight stream interpretation)
        public int WeightStableBand { get; set; } = 20;   // +/- band
        public int WeightZeroBand { get; set; } = 50;     // abs(weight) < this => treat as "zero"
        public int WeightWindowSeconds { get; set; } = 3; // rolling window for stability
        public int WeightStaleSeconds { get; set; } = 3;  // if no update => unavailable

        // =====================================================================
        //  Debug / diagnostics (UI order)
        // =====================================================================
        public bool DebugLogging { get; set; } = false;

        // Burst test settings (only relevant when DebugLogging is enabled in UI)
        public int BurstTestCount { get; set; } = 50;
        public int BurstTestDelayMs { get; set; } = 200;
        public bool AlwaysOnTop { get; set; } = false;

        // =====================================================================
        //  Helpers
        // =====================================================================
        public string BuildServiceUrl()
        {
            if (string.IsNullOrWhiteSpace(IpAddress)) return "";
            return $"http://{IpAddress}/Service.asmx";
        }

        public static AppSettings LoadOrDefaults(string path, out bool existed)
        {
            existed = false;

            try
            {
                if (File.Exists(path))
                {
                    existed = true;

                    var json = File.ReadAllText(path);

                    // Normal deserialize (case-insensitive for safety)
                    var s = JsonSerializer.Deserialize<AppSettings>(
                        json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    ) ?? new AppSettings();

                    // Back-compat shims (only applied when the new key is missing)
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        // Old key "UseWeightBackup" -> new WeightModeEnabled
                        if (!root.TryGetProperty(nameof(WeightModeEnabled), out _)
                            && root.TryGetProperty("UseWeightBackup", out var legacy)
                            && (legacy.ValueKind == JsonValueKind.True || legacy.ValueKind == JsonValueKind.False))
                        {
                            s.WeightModeEnabled = legacy.GetBoolean();
                        }

                        // If weight-mode poll settings are missing, keep old behavior.
                        if (!root.TryGetProperty(nameof(WeightPollSeconds), out _))
                            s.WeightPollSeconds = s.PollSeconds;

                        if (!root.TryGetProperty(nameof(StableNonZeroFastWindowSeconds), out _))
                            s.StableNonZeroFastWindowSeconds = 300;

                        if (!root.TryGetProperty(nameof(WeightBurstCount), out _))
                            s.WeightBurstCount = 3;

                        if (!root.TryGetProperty(nameof(WeightBurstDelayMs), out _))
                            s.WeightBurstDelayMs = 200;

                        // Back-compat default for the newer "min true success" rule
                        if (!root.TryGetProperty(nameof(WeightBurstMinTrueSuccess), out _))
                            s.WeightBurstMinTrueSuccess = 1;

                        // Back-compat default for separate weight-mode confirm delay
                        if (!root.TryGetProperty(nameof(WeightEyeConfirmDelaySeconds), out _))
                            s.WeightEyeConfirmDelaySeconds = s.EyeConfirmDelaySeconds;
                        
                        if (!root.TryGetProperty(nameof(WeightEyeConfirmDelayFastSeconds), out _))
                            s.WeightEyeConfirmDelayFastSeconds = s.WeightEyeConfirmDelaySeconds;
                    }
                    catch
                    {
                        // ignore JSON doc parse errors; keep deserialized values
                    }

                    s.NormalizeAndClamp();
                    return s;
                }
            }
            catch
            {
                // ignore and fall back to defaults
                existed = false; // treat unreadable/invalid file as not usable
            }

            var d = new AppSettings();
            d.NormalizeAndClamp();
            return d;
        }

        public void Save(string path)
        {
            NormalizeAndClamp();

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Copy(tmp, path, overwrite: true);
            File.Delete(tmp);
        }

        // =====================================================================
        //  Limits (match setting groups; not UI-specific but kept parallel)
        // =====================================================================
        public static class Limits
        {
            // Eye / general
            public const int InputIdMin = 0;
            public const int InputIdMax = 3;

            public const int PollSecondsMin = 1;
            public const int PollSecondsMax = 120;

            public const int EyeConfirmDelaySecondsMin = 1;
            public const int EyeConfirmDelaySecondsMax = 120;

            public const int FailureRetryDelaySecondsMin = 1;
            public const int FailureRetryDelaySecondsMax = 60;

            public const int FailureRetryCountMin = 1;
            public const int FailureRetryCountMax = 10;

            // Weight mode
            public const int WeightPortMin = 1;
            public const int WeightPortMax = 65535;

            public const int WeightPollSecondsMin = 1;
            public const int WeightPollSecondsMax = 120;

            // Weight mode
            public const int WeightEyeConfirmDelaySecondsMin = 1;
            public const int WeightEyeConfirmDelaySecondsMax = 120;

            public const int PollSecondsStableNonZeroMin = 1;
            public const int PollSecondsStableNonZeroMax = 60;

            public const int WeightEyeConfirmDelayFastSecondsMin = 1;
            public const int WeightEyeConfirmDelayFastSecondsMax = 120;

            public const int StableNonZeroFastWindowSecondsMin = 0;     // allow 0 => disable fast-window behavior
            public const int StableNonZeroFastWindowSecondsMax = 3600;  // 1 hour cap (adjust if you want)

            public const int WeightBurstCountMin = 1;
            public const int WeightBurstCountMax = 20;

            public const int WeightBurstDelayMsMin = 0;
            public const int WeightBurstDelayMsMax = 2000;

            public const int WeightBurstMinTrueSuccessMin = 1;
            public const int WeightBurstMinTrueSuccessMax = 20;

            public const int WeightStableBandMin = 1;
            public const int WeightStableBandMax = 500;

            public const int WeightZeroBandMin = 1;
            public const int WeightZeroBandMax = 5000;

            public const int WeightWindowSecondsMin = 1;
            public const int WeightWindowSecondsMax = 30;

            public const int WeightStaleSecondsMin = 1;
            public const int WeightStaleSecondsMax = 30;

            // Debug / burst
            public const int BurstTestCountMin = 1;
            public const int BurstTestCountMax = 500;

            public const int BurstTestDelayMsMin = 0;
            public const int BurstTestDelayMsMax = 5000;
        }

        // =====================================================================
        //  Normalize + clamp (keep in same order as properties)
        // =====================================================================
        public void NormalizeAndClamp()
        {
            // Strings
            LocationName = (LocationName ?? "").Trim();
            IpAddress = (IpAddress ?? "").Trim();
            WeightIp = (WeightIp ?? "").Trim();

            // Eye / general
            InputId = Math.Clamp(InputId, Limits.InputIdMin, Limits.InputIdMax);
            PollSeconds = Math.Clamp(PollSeconds, Limits.PollSecondsMin, Limits.PollSecondsMax);

            EyeConfirmDelaySeconds = Math.Clamp(EyeConfirmDelaySeconds, Limits.EyeConfirmDelaySecondsMin, Limits.EyeConfirmDelaySecondsMax);
            FailureRetryDelaySeconds = Math.Clamp(FailureRetryDelaySeconds, Limits.FailureRetryDelaySecondsMin, Limits.FailureRetryDelaySecondsMax);
            FailureRetryCount = Math.Clamp(FailureRetryCount, Limits.FailureRetryCountMin, Limits.FailureRetryCountMax);

            // Weight mode
            WeightPort = Math.Clamp(WeightPort, Limits.WeightPortMin, Limits.WeightPortMax);

            WeightPollSeconds = Math.Clamp(WeightPollSeconds, Limits.WeightPollSecondsMin, Limits.WeightPollSecondsMax);
            PollSecondsStableNonZero = Math.Clamp(PollSecondsStableNonZero, Limits.PollSecondsStableNonZeroMin, Limits.PollSecondsStableNonZeroMax);

            WeightEyeConfirmDelayFastSeconds = Math.Clamp(
                WeightEyeConfirmDelayFastSeconds,
                Limits.WeightEyeConfirmDelayFastSecondsMin,
                Limits.WeightEyeConfirmDelayFastSecondsMax);

            WeightEyeConfirmDelaySeconds = Math.Clamp(
                WeightEyeConfirmDelaySeconds,
                Limits.WeightEyeConfirmDelaySecondsMin,
                Limits.WeightEyeConfirmDelaySecondsMax);

            StableNonZeroFastWindowSeconds = Math.Clamp(
                StableNonZeroFastWindowSeconds,
                Limits.StableNonZeroFastWindowSecondsMin,
                Limits.StableNonZeroFastWindowSecondsMax);

            WeightBurstCount = Math.Clamp(WeightBurstCount, Limits.WeightBurstCountMin, Limits.WeightBurstCountMax);
            WeightBurstDelayMs = Math.Clamp(WeightBurstDelayMs, Limits.WeightBurstDelayMsMin, Limits.WeightBurstDelayMsMax);
            WeightBurstMinTrueSuccess = Math.Clamp(
                WeightBurstMinTrueSuccess,
                Limits.WeightBurstMinTrueSuccessMin,
                Limits.WeightBurstMinTrueSuccessMax);

            // Cross-setting sanity: can't require more TRUE successes than total burst samples
            if (WeightBurstMinTrueSuccess > WeightBurstCount)
                WeightBurstMinTrueSuccess = WeightBurstCount;

            WeightStableBand = Math.Clamp(WeightStableBand, Limits.WeightStableBandMin, Limits.WeightStableBandMax);
            WeightZeroBand = Math.Clamp(WeightZeroBand, Limits.WeightZeroBandMin, Limits.WeightZeroBandMax);
            WeightWindowSeconds = Math.Clamp(WeightWindowSeconds, Limits.WeightWindowSecondsMin, Limits.WeightWindowSecondsMax);
            WeightStaleSeconds = Math.Clamp(WeightStaleSeconds, Limits.WeightStaleSecondsMin, Limits.WeightStaleSecondsMax);

            // Debug / burst
            BurstTestCount = Math.Clamp(BurstTestCount, Limits.BurstTestCountMin, Limits.BurstTestCountMax);
            BurstTestDelayMs = Math.Clamp(BurstTestDelayMs, Limits.BurstTestDelayMsMin, Limits.BurstTestDelayMsMax);
        }
    }
}
