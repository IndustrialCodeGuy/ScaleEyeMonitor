using System.Diagnostics;
using System.Threading.Channels;

namespace Scale_Eye_Monitor
{
    /*
     * MainForm.Logging.cs
     * -------------------
     * Partial: MainForm (Scale Eye Monitor)
     *
     * Responsibility:
     *   Centralized file logging + “open logs folder” helper used by MainForm and Settings flows.
     *
     * Notes:
     *   - Log files are written to LogDir (see MainForm core partial for LogDir definition).
     *   - Logging is best-effort: failures are written to Debug output and surfaced to the main status label
     *     (if the UI is still alive) to avoid throwing from background threads/timer callbacks.
     *
     * Implementation:
     *   - Log() enqueues lines to a single background writer (Channel)
     *   - One writer owns the file handle (prevents rare contention/lock scenarios)
     *   - Daily rolling file name (log_yyyyMMdd.txt)
     */

    public sealed partial class MainForm
    {
        // =====================================================================
        //  Logging (single-writer queue)
        // =====================================================================
        private const int LogQueueCapacity = 2048;
        private Channel<string>? _logChannel;
        private Task? _logTask;
        private CancellationTokenSource? _logCts;
        private readonly object _logSync = new();

        private void EnsureLogWriterStarted()
        {
            lock (_logSync)
            {
                if (_logTask is not null && !_logTask.IsCompleted)
                    return;

                _logChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(LogQueueCapacity)
                {
                    SingleReader = true,
                    SingleWriter = false,

                    // If logging stalls (AV scan, IO hang, etc.), avoid unbounded memory growth.
                    // Drop newest entries when full (TryWrite returns false, so we can debug-trace drops).
                    FullMode = BoundedChannelFullMode.DropWrite
                });

                _logCts?.Dispose();
                _logCts = new CancellationTokenSource();

                _logTask = Task.Run(() => LogWriterLoopAsync(_logChannel.Reader, _logCts.Token));
            }
        }

        internal async Task StopLoggingAsync()
        {
            Task? taskToWait;
            CancellationTokenSource? ctsToDispose;

            lock (_logSync)
            {
                // Complete FIRST so the writer drains queued lines and exits naturally.
                try { _logChannel?.Writer.TryComplete(); } catch { }

                taskToWait = _logTask;
                ctsToDispose = _logCts;

                // Clear references after completing the writer so future Log() calls become no-ops.
                _logTask = null;
                _logChannel = null;
                _logCts = null;
            }

            // Now wait outside the lock.
            try
            {
                if (taskToWait is not null)
                {
                    // Don't let shutdown hang forever if IO blocks.
                    var finished = await Task.WhenAny(taskToWait, Task.Delay(2000)).ConfigureAwait(false);
                    if (!ReferenceEquals(finished, taskToWait))
                    {
                        try { ctsToDispose?.Cancel(); } catch { }
                        // Give it a brief chance to exit after cancellation.
                        await Task.WhenAny(taskToWait, Task.Delay(1000)).ConfigureAwait(false);
                    }
                }
            }
            catch { }

            // Ensure cancellation is requested before disposing.
            try { ctsToDispose?.Cancel(); } catch { }
            try { ctsToDispose?.Dispose(); } catch { }
        }

        private void Log(string message)
        {
            try
            {
                // Don't resurrect logging during shutdown/teardown.
                if (_shutdown || _cleanupDone || IsDisposed || Disposing)
                {
                    Debug.WriteLine("LOG (shutdown): " + message);
                    return;
                }

                EnsureLogWriterStarted();

                var line = $"[{DateTime.Now:MM/dd/yyyy_HH:mm:ss}] {message}";
                if (!(_logChannel?.Writer.TryWrite(line) ?? false))
                    Debug.WriteLine("LOG DROP (channel not available): " + line);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("LOG ENQUEUE FAIL: " + ex.Message);
                TrySurfaceLogFailToUi("Log FAIL: " + ex.Message);
            }
        }

        private async Task LogWriterLoopAsync(ChannelReader<string> reader, CancellationToken token)
        {
            StreamWriter? sw = null;
            DateTime currentDay = DateTime.MinValue;
            bool surfacedFail = false;

            void CloseWriter()
            {
                try { sw?.Dispose(); } catch { }
                sw = null;
                currentDay = DateTime.MinValue;
            }

            StreamWriter GetWriterForToday()
            {
                var today = DateTime.Today;
                if (sw is not null && today == currentDay)
                    return sw;

                CloseWriter();
                currentDay = today;

                var path = Path.Combine(LogDir, $"log_{today:yyyyMMdd}.txt");

                // Single owner for writes; allow other processes to read/tail the file.
                var fs = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);

                sw = new StreamWriter(fs) { AutoFlush = true };
                return sw;
            }

            try
            {
                Directory.CreateDirectory(LogDir);

                while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var line))
                    {
                        if (token.IsCancellationRequested && !reader.Completion.IsCompleted)
                            token.ThrowIfCancellationRequested();

                        // A couple quick retries for rare transient IO locks (AV/indexer/etc.)
                        bool wrote = false;
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            try
                            {
                                GetWriterForToday().WriteLine(line);
                                wrote = true;
                                break;
                            }
                            catch (IOException) when (attempt < 2)
                            {
                                await Task.Delay(25, token).ConfigureAwait(false);

                                // Reopen writer on the next attempt in case the underlying handle got unhappy.
                                CloseWriter();
                            }
                        }

                        if (!wrote)
                        {
                            Debug.WriteLine("LOG WRITE FAIL (dropped): " + line);

                            if (!surfacedFail)
                            {
                                surfacedFail = true;
                                TrySurfaceLogFailToUi("Log FAIL: could not write to log file.");
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine("LOG WRITER FAIL: " + ex.Message);
                TrySurfaceLogFailToUi("Log FAIL: " + ex.Message);
            }
            finally
            {
                CloseWriter();
            }
        }

        private void TrySurfaceLogFailToUi(string msg)
        {
            try { UiSafe(() => lblStatus.Text = msg); } catch { }
        }

        private void OpenLogFolder()
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                Process.Start(new ProcessStartInfo { FileName = LogDir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log($"OpenLogFolder failed: {ex.Message}");
            }
        }

        // =====================================================================
        //  Non-debug de-dupe (log only when something changes)
        // =====================================================================
        private string? _lastNonDebugUiLogSig;

        private void LogUiChangeIfNeeded(string signature, string message)
        {
            // When debug is OFF: log only when the signature changes.
            if (!DebugLogging)
            {
                if (string.Equals(_lastNonDebugUiLogSig, signature, StringComparison.Ordinal))
                    return;

                _lastNonDebugUiLogSig = signature;
            }

            Log(message);
        }
    }
}
