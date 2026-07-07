using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;

namespace Scale_Eye_Monitor
{
    /*
     * MainForm.EyeSoap.cs
     * -------------------
     * Partial: MainForm (Scale Eye Monitor)
     *
     * Responsibility:
     *   Implement the Kahler SOAP-over-HTTP eye query and the retry/failure policy around it.
     *
     * Contains:
     *   - QueryIsInputOnAsync:
     *       * Builds SOAP envelope and sends HTTP POST
     *       * Uses device-compat HTTP settings (HTTP/1.0, ConnectionClose, no Expect: 100-continue)
     *       * Parses IsInputOnResult from XML
     *       * Returns rich result with HttpText, error/detail, and raw response
     *   - QueryEyeWithFailurePolicyAsync:
     *       * Retries up to FailureRetryCount with FailureRetryDelaySeconds between attempts
     *       * Throttles repeated log/UI noise during prolonged failure
     *       * Fast-fails clearly final socket failures on attempt 1
     *       * Forces headline Disconnected for failed communication after fast-fail or retry exhaustion
     *       * On recovery, clears failure tracking and can restart the weight monitor
     *
     * UI/State integration:
     *   - Uses CommitHeadline(Disconnected, ...) only for confirmed connectivity failures.
     *   - Keeps transient per-attempt failure text out of the public UI while disconnected.
     *   - When eyes are forced Disconnected and WeightModeEnabled:
     *       * Weight monitor is stopped and weight state is set Unavailable (clarity + avoids noise).
     *
     * Notes:
     *   - HeadlineState is defined here and is the only “public facing” state enum.
     */

    public sealed partial class MainForm
    {
        // =====================================================================
        //  Types (Eye/SOAP)
        // =====================================================================
        // Only the “real” eye headline states.
        // (Detail-only messages never touch this.)
        private enum HeadlineState { Unknown, Ok, Blocked, AlignmentOff, Disconnected }

        private readonly record struct SoapQueryResult(
            bool? Value,
            int HttpCode,
            string HttpText,
            string Error,
            string Detail,
            string RawResponse);

        // =====================================================================
        //  Constants
        // =====================================================================
        // Optional: reminder during prolonged outage (set 0 to disable)
        private const int EyeFailureRepeatLogSeconds = 60;
        private const string InvalidSoapUrlReason = "Config Error: Invalid eye URL format";

        // =====================================================================
        //  Eye failure throttling (log/UI spam control)
        // =====================================================================
        private bool _eyeFailureActive;
        private string? _lastEyeFailureKey;
        private DateTime _lastEyeFailureLogUtc = DateTime.MinValue;

        // Used by failure policy to avoid spamming tray text changes with identical reason strings.
        private string _lastForcedReason = "";
        private bool _forcedDisconnectActive;

        // Invalid/non-absolute request URLs are local configuration errors. Once
        // seen, do not keep retrying the same bad URL until settings change it.
        private bool _invalidSoapUrlActive;
        private string _invalidSoapUrl = "";

        private void ClearForcedReason()
        {
            _lastForcedReason = "";
            _forcedDisconnectActive = false;
        }

        private void ClearInvalidSoapUrlState()
        {
            _invalidSoapUrlActive = false;
            _invalidSoapUrl = "";
        }

        private void MarkInvalidSoapUrl(string url)
        {
            _invalidSoapUrlActive = true;
            _invalidSoapUrl = (url ?? "").Trim();
        }

        private bool IsInvalidSoapUrlActiveFor(string url) =>
            _invalidSoapUrlActive && string.Equals((url ?? "").Trim(), _invalidSoapUrl, StringComparison.Ordinal);

        private void ResetEyeFailureTracking()
        {
            _eyeFailureActive = false;
            _lastEyeFailureKey = null;
            _lastEyeFailureLogUtc = DateTime.MinValue;
        }

        // =====================================================================
        //  Helpers: SOAP fault + exception detail formatting
        // =====================================================================
        private static string TryExtractSoapFault(string xmlText)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(xmlText)) return "";

                var doc = new XmlDocument();
                doc.LoadXml(xmlText);

                var node = doc.SelectSingleNode("//*[local-name()='Fault']/*[local-name()='faultstring']");
                return node?.InnerText?.Trim() ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static bool IsInvalidSoapUrlException(Exception ex)
        {
            if (ex is UriFormatException)
                return true;

            string message = ex.Message ?? "";

            if (ex is NotSupportedException)
                return true;

            if (ex is InvalidOperationException || ex is ArgumentException)
            {
                return message.Contains("invalid request URI", StringComparison.OrdinalIgnoreCase) ||
                       message.Contains("absolute URI", StringComparison.OrdinalIgnoreCase) ||
                       message.Contains("BaseAddress", StringComparison.OrdinalIgnoreCase) ||
                       message.Contains("scheme", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static bool IsInvalidSoapUrlResult(SoapQueryResult q)
        {
            if (string.Equals(q.Error, "InvalidSoapUrl", StringComparison.OrdinalIgnoreCase))
                return true;

            return string.Equals(q.Error, nameof(InvalidOperationException), StringComparison.OrdinalIgnoreCase) &&
                ((q.Detail ?? "").Contains("invalid request URI", StringComparison.OrdinalIgnoreCase) ||
                (q.Detail ?? "").Contains("absolute URI", StringComparison.OrdinalIgnoreCase) ||
                (q.Detail ?? "").Contains("BaseAddress", StringComparison.OrdinalIgnoreCase));
        }

        private static string DescribeHttpRequestException(HttpRequestException ex)
        {
            if (TryFindSocketException(ex, out var se))
                return $"SocketError={se.SocketErrorCode} ({se.Message})";

            if (ex.InnerException is IOException io)
                return $"IO error ({io.Message})";

            return ex.InnerException?.Message ?? ex.Message;
        }

        private static bool TryFindSocketException(Exception ex, out SocketException socketException)
        {
            for (Exception? current = ex; current is not null; current = current.InnerException)
            {
                if (current is SocketException se)
                {
                    socketException = se;
                    return true;
                }
            }

            socketException = null!;
            return false;
        }

        private string FormatFailureMsg(string stage, SoapQueryResult q, int attempt, int maxAttempts)
        {
            string core = string.IsNullOrWhiteSpace(q.HttpText)
                ? q.Error
                : $"{q.HttpText}: {q.Error}";

            if (string.IsNullOrWhiteSpace(core))
                core = "Unknown connection error";

            if (!string.IsNullOrWhiteSpace(q.Detail))
                core += $" ({q.Detail})";

            return $"{stage} FAILED [{attempt}/{maxAttempts}] - {core}";
        }

        // =====================================================================
        //  Core SOAP request: IsInputOn
        // =====================================================================
        private async Task<SoapQueryResult> QueryIsInputOnAsync(string url, int inputId, CancellationToken token = default)
        {
            string envelope = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
               xmlns:xsd=""http://www.w3.org/2001/XMLSchema""
               xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
  <soap:Body>
    <IsInputOn xmlns=""Kahler/"">
      <input>{inputId}</input>
    </IsInputOn>
  </soap:Body>
</soap:Envelope>";

            try
            {
                using var content = new StringContent(envelope, Encoding.UTF8, "text/xml");
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

                // WSDL says SOAPAction="Kahler/IsInputOn"
                req.Headers.TryAddWithoutValidation("SOAPAction", "\"Kahler/IsInputOn\"");

                // device compatibility
                req.Version = HttpVersion.Version10;
                req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                req.Headers.ConnectionClose = true;
                req.Headers.ExpectContinue = false;

                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, token)
                                            .ConfigureAwait(false);

                int code = (int)resp.StatusCode;
                string httpText = $"{code} {resp.ReasonPhrase}";

                string xmlText = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    string fault = TryExtractSoapFault(xmlText);
                    string detail = string.IsNullOrWhiteSpace(fault) ? "" : $"SOAP Fault: {fault}";

                    return new SoapQueryResult(
                        Value: null,
                        HttpCode: code,
                        HttpText: httpText,
                        Error: "HTTP error",
                        Detail: detail,
                        RawResponse: xmlText);
                }

                using var xr = XmlReader.Create(
                    new StringReader(xmlText),
                    new XmlReaderSettings { Async = true, IgnoreComments = true, IgnoreWhitespace = true });

                while (await xr.ReadAsync().ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested();

                    if (xr.NodeType == XmlNodeType.Element && xr.LocalName == "IsInputOnResult")
                    {
                        string s = await xr.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        if (bool.TryParse(s, out bool b))
                        {
                            return new SoapQueryResult(
                                Value: b,
                                HttpCode: code,
                                HttpText: httpText,
                                Error: "",
                                Detail: "",
                                RawResponse: DebugLogging ? xmlText : "");
                        }
                    }
                }

                return new SoapQueryResult(
                    Value: null,
                    HttpCode: code,
                    HttpText: httpText,
                    Error: "Missing IsInputOnResult",
                    Detail: "",
                    RawResponse: xmlText);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // User-cancel (or shutdown-linked cancel): let caller handle
                throw;
            }
            catch (TaskCanceledException)
            {
                // HttpClient timeout path (not user-cancel).
                // Keep this as structured failure metadata only; user-facing
                // disconnected text is produced by GetDisconnectedReasonAsync(...).
                return new SoapQueryResult(
                    Value: null,
                    HttpCode: 0,
                    HttpText: "",
                    Error: "TimedOut",
                    Detail: "HttpError=TimedOut",
                    RawResponse: "");
            }
            catch (Exception ex) when (IsInvalidSoapUrlException(ex))
            {
                return new SoapQueryResult(
                    Value: null,
                    HttpCode: 0,
                    HttpText: "",
                    Error: "InvalidSoapUrl",
                    Detail: ex.Message,
                    RawResponse: "");
            }
            catch (HttpRequestException ex)
            {
                string detail = DescribeHttpRequestException(ex);

                return new SoapQueryResult(
                    Value: null,
                    HttpCode: 0,
                    HttpText: "Send failed",
                    Error: "HttpRequestException",
                    Detail: detail,
                    RawResponse: "");
            }
            catch (Exception ex)
            {
                return new SoapQueryResult(
                    Value: null,
                    HttpCode: 0,
                    HttpText: "Error",
                    Error: ex.GetType().Name,
                    Detail: ex.Message,
                    RawResponse: "");
            }
        }

        // =====================================================================
        //  Failure policy wrapper: retry + throttled log/UI
        // =====================================================================
        private async Task<SoapQueryResult?> QueryEyeWithFailurePolicyAsync(
            string url,
            int inputId,
            string stage,
            CancellationToken token = default)
        {
            int maxAttempts = Math.Max(1, FailureRetryCount);
            SoapQueryResult lastFail = default!;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (_shutdown) return null;

                var q = await QueryIsInputOnAsync(url, inputId, token).ConfigureAwait(false);

                if (q.Value is not null)
                {
                    if (_eyeFailureActive)
                    {
                        Log($"Eye reachable again (recovered). Last failure was: {_lastEyeFailureKey}");
                        ResetEyeFailureTracking();
                        ClearForcedReason();

                        // Resume weight monitoring now that the eye device is reachable again.
                        StartOrStopWeightMonitor();
                    }

                    return q;
                }

                lastFail = q;

                if (IsInvalidSoapUrlResult(q))
                {
                    ForceInvalidSoapUrlFromFailure(url, stage, q);
                    return null;
                }

                // Some socket failures are final enough to commit Disconnected immediately.
                bool offline = (attempt == 1 && IsProbablyOffline(q));

                // A "failure kind" signature. If this changes, we treat it as meaningful.
                // (Keep "offline" separate so the UI/log line reflects it clearly.)
                string key = $"{q.HttpText}|{q.Error}|{q.Detail}";
                bool firstOfOutage = !_eyeFailureActive;
                bool keyChanged = !string.Equals(key, _lastEyeFailureKey, StringComparison.Ordinal);

                bool periodicReminder =
                    EyeFailureRepeatLogSeconds > 0 &&
                    (DateTime.UtcNow - _lastEyeFailureLogUtc).TotalSeconds >= EyeFailureRepeatLogSeconds;

                // If it is a fast-fail socket condition, show it as a single-attempt final failure.
                int attemptsShown = offline ? 1 : maxAttempts;

                // Only log on: first failure, kind change, or periodic reminder.
                // User-facing disconnected text is committed only by ForceDisconnectedFromFailure(...).
                // Do not show transient "Eye check FAILED" details here; during a prolonged
                // disconnected state they can replace the stable reason without changing state.
                bool shouldLog = DebugLogging || firstOfOutage || keyChanged || (attempt == 1 && periodicReminder);

                _eyeFailureActive = true;
                _lastEyeFailureKey = key;

                string? msg = null;
                if (shouldLog)
                    msg = FormatFailureMsg(offline ? stage + " (offline)" : stage, q, attempt, attemptsShown);

                if (shouldLog)
                {
                    Log(msg!);
                    _lastEyeFailureLogUtc = DateTime.UtcNow;
                }

                // Fast-fail socket condition => do not keep retrying inside this poll cycle.
                if (offline)
                {
                    string reason = await GetDisconnectedReasonAsync(url, q, token).ConfigureAwait(false);
                    ForceDisconnectedFromFailure(reason, GetHttpShownForDisconnectedUi(q));
                    return null;
                }

                // Delay between attempts when the failure is not a fast-fail offline condition.
                if (attempt < maxAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(FailureRetryDelaySeconds), token).ConfigureAwait(false);
            }

            string finalReason = await GetDisconnectedReasonAsync(url, lastFail, token).ConfigureAwait(false);
            ForceDisconnectedFromFailure(finalReason, GetHttpShownForDisconnectedUi(lastFail));

            return null;
        }

        // =====================================================================
        //  Disconnected UI reason formatting
        // =====================================================================
        private void ForceInvalidSoapUrlFromFailure(string url, string stage, SoapQueryResult q)
        {
            MarkInvalidSoapUrl(url);

            string key = $"InvalidSoapUrl|{_invalidSoapUrl}|{q.Detail}";
            bool firstOfOutage = !_eyeFailureActive;
            bool keyChanged = !string.Equals(key, _lastEyeFailureKey, StringComparison.Ordinal);
            bool shouldLog = DebugLogging || firstOfOutage || keyChanged;

            _eyeFailureActive = true;
            _lastEyeFailureKey = key;

            if (shouldLog)
            {
                string detail = string.IsNullOrWhiteSpace(q.Detail) ? "" : $" ({q.Detail})";
                Log($"{stage} FAILED - {InvalidSoapUrlReason}{detail}");
                _lastEyeFailureLogUtc = DateTime.UtcNow;
            }

            ForceDisconnectedFromFailure(InvalidSoapUrlReason, "—");
        }

        private async Task<string> GetDisconnectedReasonAsync(string url, SoapQueryResult q, CancellationToken token)
        {
            if (IsInvalidSoapUrlResult(q))
                return InvalidSoapUrlReason;

            // Preserve the phase when the final symptom is a timeout. A timeout before
            // TCP connect is a TCP error; a timeout after TCP connect is an HTTP/SOAP error.
            if (IsTimeoutResult(q))
                return await GetTimeoutDisconnectedReasonAsync(url, token).ConfigureAwait(false);

            if (TryGetSocketError(q, out var se))
                return FormatSocketDisconnectedReason(se);

            if (IsMissingInputResult(q))
                return "Response Error: Missing Query Result";

            string http = GetHttpStatusForDisconnectedReason(q);
            if (!string.IsNullOrWhiteSpace(http))
                return $"HTTP Error: {FormatDisconnectedErrorName(http)}";

            return "Unknown connection error";
        }

        private static string GetHttpShownForDisconnectedUi(SoapQueryResult q)
        {
            string http = GetHttpStatusForDisconnectedReason(q);
            return string.IsNullOrWhiteSpace(http) ? "—" : http;
        }

        private static bool IsTimeoutResult(SoapQueryResult q)
        {
            if (string.Equals(q.HttpText, "Timeout", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(q.Error, "TimedOut", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(q.Detail, "HttpError=TimedOut", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (TryGetSocketError(q, out var se) && se == SocketError.TimedOut)
                return true;

            return false;
        }

        private async Task<string> GetTimeoutDisconnectedReasonAsync(string url, CancellationToken token)
        {
            var tcp = await ProbeTcpEndpointAsync(url, token).ConfigureAwait(false);

            if (tcp.Connected)
                return $"HTTP Error: {FormatDisconnectedErrorName("TimedOut")}";

            if (tcp.SocketError is SocketError se)
                return FormatSocketDisconnectedReason(se);

            return $"TCP Error: {FormatDisconnectedErrorName("TimedOut")}";
        }

        private readonly record struct TcpProbeResult(bool Connected, SocketError? SocketError);

        private static async Task<TcpProbeResult> ProbeTcpEndpointAsync(string url, CancellationToken token)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return new TcpProbeResult(false, null);

            int port = uri.IsDefaultPort
                ? (string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80)
                : uri.Port;

            using var tcp = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));

            try
            {
                await tcp.ConnectAsync(uri.Host, port, timeoutCts.Token).ConfigureAwait(false);
                return new TcpProbeResult(true, null);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return new TcpProbeResult(false, SocketError.TimedOut);
            }
            catch (SocketException ex)
            {
                return new TcpProbeResult(false, ex.SocketErrorCode);
            }
            catch
            {
                return new TcpProbeResult(false, null);
            }
        }

        private static string FormatSocketDisconnectedReason(SocketError se)
        {
            string prefix = IsDnsSocketError(se) ? "DNS Error" : "TCP Error";
            return $"{prefix}: {FormatSocketErrorName(se)}";
        }

        private static string FormatSocketErrorName(SocketError se) =>
            FormatDisconnectedErrorName(se.ToString());

        private static string FormatDisconnectedErrorName(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            var sb = new StringBuilder(value.Length + 8);
            sb.Append(value[0]);

            for (int i = 1; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsUpper(c) && sb[^1] != ' ')
                    sb.Append(' ');

                sb.Append(c);
            }

            return sb.ToString();
        }

        private static bool IsDnsSocketError(SocketError se) =>
            se is SocketError.HostNotFound
            or SocketError.NoData
            or SocketError.TryAgain;

        private static bool IsMissingInputResult(SoapQueryResult q) =>
            string.Equals(q.Error, "Missing IsInputOnResult", StringComparison.OrdinalIgnoreCase);

        private static string GetHttpStatusForDisconnectedReason(SoapQueryResult q)
        {
            if (q.HttpCode < 100 || q.HttpCode > 599)
                return "";

            string s = q.HttpText?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(s))
                return q.HttpCode.ToString(System.Globalization.CultureInfo.InvariantCulture);

            return s;
        }

        // =====================================================================
        //  Forced-disconnected state transitions (used by failure policy)
        // =====================================================================
        private void ForceDisconnectedFromFailure(string reason, string httpShown)
        {
            _forcedDisconnectActive = true;

            ResetAlignmentCycleTracking($"eye disconnect: {reason}");

            bool stateChanged = (_headline != HeadlineState.Disconnected);
            bool reasonChanged = !string.Equals(reason, _lastForcedReason, StringComparison.Ordinal);

            // Keep the disconnected presentation unified through the headline path.
            // Even when the state/reason is unchanged, this refreshes Last Poll while
            // preserving the stable second-line disconnected reason.
            CommitHeadline(
                HeadlineState.Disconnected,
                httpShown: httpShown,
                stamp: DateTime.Now,
                detail: reason,
                toast: true);

            if (stateChanged || reasonChanged)
                _lastForcedReason = reason;

            // If weight mode is enabled, enforce the eyes-offline weight outage on every
            // forced-disconnect pass, not only on the first headline transition. This covers
            // cases where settings restart the weight monitor while the eye side is already
            // disconnected; the next failed eye poll must still stop weight monitoring.
            if (WeightModeEnabled)
            {
                // Treat this as an outage for weight monitoring so that when we resume
                // after eyes recover, LogWeightConnectedOnce() can emit the restored toast.
                _weightOutageActive = true;

                // Stop without wiping outage/toast history so restored can fire on resume.
                StopWeightMonitor(resetToastState: false);
                SetWeightUnavailable("eyes offline");

                // Clear the visible Scale Status immediately on disconnect instead
                // of leaving the last stable/zero value on screen until the next poll.
                RefreshScaleStatusFromCurrentWeight();
            }

            if (stateChanged)
                Log($"Forced state -> Disconnected due to {reason}.");
        }

        // =====================================================================
        //  Offline detection helpers
        // =====================================================================
        private static bool TryGetSocketError(SoapQueryResult q, out SocketError se)
        {
            se = default;

            var d = q.Detail ?? "";
            const string prefix = "SocketError=";

            int i = d.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;

            i += prefix.Length;

            int j = i;
            while (j < d.Length && (char.IsLetterOrDigit(d[j]) || d[j] == '_'))
                j++;

            var token = d[i..j];
            return Enum.TryParse(token, ignoreCase: true, out se);
        }

        private static bool IsProbablyOffline(SoapQueryResult q)
        {
            if (q.HttpCode != 0) return false;

            if (TryGetSocketError(q, out var se))
            {
                return se is SocketError.HostNotFound
                     or SocketError.NetworkUnreachable
                     or SocketError.HostUnreachable
                     or SocketError.HostDown
                     or SocketError.ConnectionRefused
                     or SocketError.AddressNotAvailable;
                // TimedOut intentionally retries/probes so the UI can distinguish
                // TCP Error: TimedOut from HTTP Error: TimedOut.
            }

            return false;
        }
    }
}
