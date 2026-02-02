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
 *       * Detects “probably offline” using socket error heuristics (attempt 1 only)
 *       * Forces headline Disconnected for offline or repeated failures
 *       * On recovery, clears failure tracking and can restart the weight monitor
 *
 * UI/State integration:
 *   - Uses CommitHeadline(Disconnected, ...) only for confirmed connectivity failures.
 *   - Uses UpdateDetail(...) for informational failure messages without changing headline.
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

        private sealed record SoapQueryResult(
            bool? Value,
            int HttpCode,
            string HttpText,
            string Error,
            string Detail,
            string RawResponse);

        // Eye failure throttling (log/UI spam control)
        private bool _eyeFailureActive;
        private string? _lastEyeFailureKey;
        private DateTime _lastEyeFailureLogUtc = DateTime.MinValue;

        // Optional: reminder during prolonged outage (set 0 to disable)
        private const int EyeFailureRepeatLogSeconds = 60;

        // Used by failure policy to avoid spamming tray text changes with identical reason strings.
        private string _lastForcedReason = "";
        private bool _forcedDisconnectActive;
        private void ClearForcedReason()
        {
            _lastForcedReason = "";
            _forcedDisconnectActive = false;
        }

        // =====================================================================
        //  Helpers: SOAP fault + exception detail formatting
        // =====================================================================

        private void ResetEyeFailureTracking()
        {
            _eyeFailureActive = false;
            _lastEyeFailureKey = null;
            _lastEyeFailureLogUtc = DateTime.MinValue;
        }

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

        private static string DescribeHttpRequestException(HttpRequestException ex)
        {
            if (ex.InnerException is SocketException se)
                return $"SocketError={se.SocketErrorCode} ({se.Message})";

            if (ex.InnerException is IOException io)
                return $"IO error ({io.Message})";

            return ex.InnerException?.Message ?? ex.Message;
        }

        private string FormatFailureMsg(string stage, SoapQueryResult q, int attempt, int maxAttempts)
        {
            string core = string.IsNullOrWhiteSpace(q.Detail)
                ? $"{q.HttpText}: {q.Error}"
                : $"{q.HttpText}: {q.Error} ({q.Detail})";

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

                using var xr = XmlReader.Create(new StringReader(xmlText),
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
                                RawResponse: xmlText);
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
                // HttpClient timeout path (not user-cancel)
                return new SoapQueryResult(
                    Value: null,
                    HttpCode: 0,
                    HttpText: "Timeout",
                    Error: $"Timeout after {_http.Timeout.TotalSeconds:0.0}s",
                    Detail: "",
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
                        // NEW: resume weight monitor now that eyes are back
                        StartOrStopWeightMonitor(); // will only start if WeightModeEnabled is true
                    }
                    return q;
                }

                lastFail = q;

                // Decide if this looks like a true offline/unreachable situation.
                bool offline = (attempt == 1 && IsProbablyOffline(q));

                // A "failure kind" signature. If this changes, we treat it as meaningful.
                // (Keep "offline" separate so the UI/log line reflects it clearly.)
                string key = $"{q.HttpText}|{q.Error}|{q.Detail}";
                bool firstOfOutage = !_eyeFailureActive;
                bool keyChanged = !string.Equals(key, _lastEyeFailureKey, StringComparison.Ordinal);

                bool periodicReminder =
                    EyeFailureRepeatLogSeconds > 0 &&
                    (DateTime.UtcNow - _lastEyeFailureLogUtc).TotalSeconds >= EyeFailureRepeatLogSeconds;

                // If it's offline, we treat this as a single-attempt "final" for this poll.
                int attemptsShown = offline ? 1 : maxAttempts;

                // Only log/UI on: first failure, kind change, or periodic reminder
                bool shouldLog = DebugLogging || firstOfOutage || keyChanged || (attempt == 1 && periodicReminder);
                bool shouldUi = DebugLogging || firstOfOutage || keyChanged || (attempt == 1 && periodicReminder);

                _eyeFailureActive = true;
                _lastEyeFailureKey = key;

                string msg = FormatFailureMsg(offline ? stage + " (offline)" : stage, q, attempt, attemptsShown);

                if (shouldLog)
                {
                    Log(msg);
                    _lastEyeFailureLogUtc = DateTime.UtcNow;
                }

                if (shouldUi)
                {
                    UpdateDetail(msg, httpShown: q.HttpText, stamp: DateTime.Now);
                }

                // Offline => don't keep retrying inside this poll cycle.
                if (offline)
                {
                    ForceDisconnectedFromFailure("device unreachable", q.HttpText);
                    return null;
                }

                // delay between attempts (only when not offline)
                if (attempt < maxAttempts)
                    await Task.Delay(TimeSpan.FromSeconds(FailureRetryDelaySeconds), token).ConfigureAwait(false);
            }

            ForceDisconnectedFromFailure("network failure", lastFail.HttpText);
            return null;
        }

        // =====================================================================
        //  Forced-disconnected state transitions (used by failure policy)
        // =====================================================================
        private void ForceDisconnectedFromFailure(string reason, string httpShown)
        {
            _forcedDisconnectActive = true;

            bool stateChanged = (_headline != HeadlineState.Disconnected);
            bool reasonChanged = !string.Equals(reason, _lastForcedReason, StringComparison.Ordinal);

            if (stateChanged)
            {
                _lastForcedReason = reason;

                // Confirmed network issue => headline Disconnected (toast on transition only)
                CommitHeadline(
                    HeadlineState.Disconnected,
                    httpShown: httpShown,
                    stamp: DateTime.Now,
                    detail: $"Disconnected ({reason})",
                    toast: true);

                // NEW: if weight mode is enabled, stop monitoring while eyes are offline
                if (WeightModeEnabled)
                {
                    // Treat this as an "outage" for weight monitoring so that when we resume
                                        // (after eyes recover), LogWeightConnectedOnce() will emit the "restored" toast.
                    _weightOutageActive = true;
                    
                                        // Stop without wiping outage/toast history (so "restored" can fire on resume).
                    StopWeightMonitor(resetToastState: false);
                    SetWeightUnavailable("eyes offline"); // optional but helpful for snapshot clarity
                }

                Log($"Forced state -> Disconnected due to {reason}.");
            }

            if (!stateChanged && (reasonChanged))
            {
                _lastForcedReason = reason;
                // Headline stays Disconnected; reason is informational only.
                UpdateDetail($"Disconnected ({reason})", httpShown: httpShown, stamp: DateTime.Now);
            }
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

            var token = d.Substring(i, j - i);
            return Enum.TryParse(token, ignoreCase: true, out se);
        }

        private static bool IsProbablyOffline(SoapQueryResult q)
        {
            if (q.HttpCode != 0) return false;

            if (TryGetSocketError(q, out var se))
            {
                return se is SocketError.HostNotFound
                         or SocketError.NetworkUnreachable
                         or SocketError.ConnectionRefused
                         or SocketError.AddressNotAvailable;
                // NOTE: removed TimedOut
            }

            // NOTE: removed HttpText=="Timeout" fast-path
            return false;
        }
    }
}
