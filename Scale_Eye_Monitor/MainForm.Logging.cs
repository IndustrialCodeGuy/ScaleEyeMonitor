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
     * Option 2 implementation:
     *   - Log() enqueues lines to a single background writer (Channel)
     *   - One writer owns the file handle (prevents rare contention/lock scenarios)
     *   - Daily rolling file name (log_yyyyMMdd.txt)
     */

    public sealed partial class MainForm
    {
        // =====================================================================
        //  Logging (single-writer queue)
        // =====================================================================
        private Channel<string>? _logChannel;
        private Task? _logTask;
        private CancellationTokenSource? _logCts;
        private readonly object _logSync = new();

        private void EnsureLogWriterStarted()
        {
            if (_shutdown || _cleanupDone) return;

            lock (_logSync)
            {
                if (_shutdown || _cleanupDone) return;

                if (_logTask is not null && !_logTask.IsCompleted)
                    return;

                _logChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false
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

                // Don't null everything yet if you want writers to no-op safely,
                // but it's fine to clear references here if your Log() checks for null.
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

            try
            {
                Directory.CreateDirectory(LogDir);

                while (await reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var line))
                    {
                        if (token.IsCancellationRequested && !reader.Completion.IsCompleted)
                            token.ThrowIfCancellationRequested();

                        // Roll file daily
                        var today = DateTime.Today;
                        if (sw is null || today != currentDay)
                        {
                            try { sw?.Dispose(); } catch { }
                            currentDay = today;

                            var path = Path.Combine(LogDir, $"log_{today:yyyyMMdd}.txt");

                            // Single owner for writes; allow other processes to read/tail the file.
                            var fs = new FileStream(
                                path,
                                FileMode.Append,
                                FileAccess.Write,
                                FileShare.ReadWrite);

                            sw = new StreamWriter(fs) { AutoFlush = true };
                        }

                        // A couple quick retries for rare transient IO locks (AV/indexer/etc.)
                        bool wrote = false;
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            try
                            {
                                sw.WriteLine(line);
                                wrote = true;
                                break;
                            }
                            catch (IOException) when (attempt < 2)
                            {
                                await Task.Delay(25, token).ConfigureAwait(false);

                                // Reopen writer (in case the underlying handle got unhappy)
                                try { sw?.Dispose(); } catch { }
                                sw = null;
                                currentDay = DateTime.MinValue; // force reopen next loop
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
                try { sw?.Dispose(); } catch { }
            }
        }

        private void TrySurfaceLogFailToUi(string msg)
        {
            try
            {
                if (_shutdown) return;
                if (IsDisposed || Disposing) return;

                if (IsHandleCreated)
                    BeginInvoke(new Action(() => lblStatus.Text = msg));
            }
            catch { }
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

        private void LogVerbose(string message)
        {
            if (DebugLogging) Log(message);
        }

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
