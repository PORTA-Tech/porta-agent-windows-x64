using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Porta.Agent.Core.Security;

namespace Porta.Agent.Service.Fleet;

internal sealed class ControlPlaneClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient? _httpClient;

    public ControlPlaneClient()
    {
        string? configuredUrl = Environment.GetEnvironmentVariable("PORTA_BACKEND_URL");
        if (string.IsNullOrWhiteSpace(configuredUrl))
        {
            DisabledReason = "BACKEND_URL_NOT_CONFIGURED";
            return;
        }

        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out Uri? baseUri))
        {
            DisabledReason = "BACKEND_URL_INVALID";
            return;
        }

        if (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            DisabledReason = "BACKEND_URL_TLS_REQUIRED";
            return;
        }

        string? pinnedSha256 = NormalizeHex(Environment.GetEnvironmentVariable("PORTA_TLS_CERT_SHA256_PIN"));
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(pinnedSha256))
        {
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, policyErrors) =>
            {
                if (policyErrors != System.Net.Security.SslPolicyErrors.None || cert is null)
                {
                    return false;
                }

                string presented = NormalizeHex(Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()))) ?? string.Empty;
                return string.Equals(presented, pinnedSha256, StringComparison.Ordinal);
            };
        }

        // Every request through this client gets the device credential and 401 logging.
        // Doing it per-call-site is how machine-summary and five others were missed.
        _httpClient = new HttpClient(new DeviceCredentialHandler(handler))
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public bool IsEnabled => _httpClient is not null;

    public string? DisabledReason { get; }

    public async Task<bool> PostMachineSummaryAsync(MachineSummaryReportPayload report, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (_httpClient is null)
        {
            return false;
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                "api/agent/machine-summary",
                report,
                JsonOptions,
                token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> PostDailyStatsAsync(DailyStatsReportPayload payload, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (_httpClient is null)
        {
            return false;
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                "api/agent/daily-stats",
                payload,
                JsonOptions,
                token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // Part 5 — one narrative line per high-value moment. Null on any failure or an empty line;
    // the caller falls back to its template. The caller supplies the timeout budget via token.
    public async Task<string?> PostNarrativeAsync(NarrativeRequestPayload payload, CancellationToken token)
        => (await PostNarrativeWithOutcomeAsync(payload, token).ConfigureAwait(false)).Line;

    /// <summary>
    /// Same call, but reports the HTTP status rather than collapsing every non-2xx to null. A 401
    /// under PORTA_DEVICE_AUTH_MODE=enforce, a 400 from payload validation, and a server that
    /// answered 200 with a null line are three completely different bugs that used to look
    /// identical from the agent side.
    /// </summary>
    public async Task<NarrativeCallOutcome> PostNarrativeWithOutcomeAsync(
        NarrativeRequestPayload payload,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (_httpClient is null)
        {
            return new NarrativeCallOutcome(null, $"client_null:{DisabledReason ?? "unknown"}");
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                "api/agent/narrative",
                payload,
                JsonOptions,
                token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new NarrativeCallOutcome(null, $"http_{(int)response.StatusCode}");
            }

            NarrativeResponsePayload? parsed = await response.Content
                .ReadFromJsonAsync<NarrativeResponsePayload>(cancellationToken: token)
                .ConfigureAwait(false);
            string? line = parsed?.Line?.Trim();
            return string.IsNullOrWhiteSpace(line)
                // 200 with no line means the server ran and declined to write one — its own log
                // carries the specific reason (cap, key, unknown account, model error, refusal).
                ? new NarrativeCallOutcome(null, "http_200_empty_line")
                : new NarrativeCallOutcome(line, string.Empty);
        }
        catch (OperationCanceledException)
        {
            return new NarrativeCallOutcome(null, "cancelled");
        }
        catch (Exception ex)
        {
            return new NarrativeCallOutcome(null, $"transport:{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// The weekly digest. Same shape as the daily's post and the same failure behaviour: a false
    /// return means the caller does not mark the week sent, so the next poll retries.
    /// </summary>
    public async Task<bool> PostWeeklyDigestAsync(WeeklyDigestReportPayload payload, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (_httpClient is null)
        {
            return false;
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                "api/agent/weekly-digest",
                payload,
                JsonOptions,
                token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> PostDailyDigestAsync(DailyDigestReportPayload payload, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (_httpClient is null)
        {
            return false;
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                "api/agent/daily-digest",
                payload,
                JsonOptions,
                token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Step 10 phase C/D — ask the control plane to render the report exactly as the client would
    /// receive it. Renders, sends nothing.
    ///
    /// The agent never re-renders this locally. One renderer for preview and for send, or the
    /// preview becomes a promise the product breaks quietly.
    /// </summary>
    /// <summary>
    /// Phase 0.2 — read this account's own display name and email back from the server.
    ///
    /// Data coming IN, not out: the agent asks who it is so the confirm screen can show the truth
    /// instead of two blank boxes. Nothing is sent but the account id the machine already holds.
    /// Fail-soft — a null leaves the boxes empty, which is where they were before.
    /// </summary>
    public async Task<ReportSenderResult?> GetReportSenderAsync(string accountId, CancellationToken token)
    {
        if (_httpClient is null || string.IsNullOrWhiteSpace(accountId))
        {
            return null;
        }

        try
        {
            using HttpResponseMessage response = await _httpClient
                .GetAsync($"api/reports/sender?accountId={Uri.EscapeDataString(accountId)}", token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<ReportSenderResult>(JsonOptions, token)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Phase 1.4 — ask the server to draft the summary. Fail-soft; null = no draft.</summary>
    public async Task<ReportSummaryResult?> PostReportSummaryAsync(object payload, CancellationToken token)
    {
        if (_httpClient is null)
        {
            return null;
        }

        try
        {
            using HttpResponseMessage response = await _httpClient
                .PostAsJsonAsync("api/reports/summary", payload, JsonOptions, token)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<ReportSummaryResult>(JsonOptions, token).ConfigureAwait(false)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<ReportPreviewResult?> PostReportPreviewAsync(
        ReportSendPayload payload,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (_httpClient is null)
        {
            return null;
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                "api/reports/preview",
                payload,
                JsonOptions,
                token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<ReportPreviewResult>(JsonOptions, token)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Send the report. The server composes and sends it, because the Resend key must never reach
    /// this machine (spec §6a).
    ///
    /// <b>A null return is NOT "not sent".</b> It means the outcome is unknown — the request may
    /// have been delivered and acted on before the connection dropped. The caller must leave the
    /// local record as pending rather than reporting a failure, because "it failed" would invite a
    /// retry that could put a second copy in a client's inbox. The server's idempotency on
    /// <c>reportId</c> is the backstop; not lying to the user is the first line.
    /// </summary>
    public async Task<ReportSendResult?> PostReportSendAsync(
        ReportSendPayload payload,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (_httpClient is null)
        {
            return null;
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                "api/reports/send",
                payload,
                JsonOptions,
                token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<ReportSendResult>(JsonOptions, token)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Delete the server's copy. Called BEFORE the local copy is removed, so a failure here leaves
    /// the report listed as present rather than reported as gone (§12.6.5) — the
    /// <c>primitive_buckets</c> failure must not happen twice in one product.
    /// </summary>
    public async Task<bool> DeleteReportAsync(string reportId, string accountId, CancellationToken token)
    {
        if (_httpClient is null || string.IsNullOrWhiteSpace(reportId))
        {
            return false;
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.DeleteAsync(
                $"api/reports/{Uri.EscapeDataString(reportId)}?accountId={Uri.EscapeDataString(accountId)}",
                token).ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> PostMachineActionEventsAsync(
        IReadOnlyList<MachineActionEventPayload> events,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (_httpClient is null || events.Count == 0)
        {
            return false;
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                "api/agent/action-events",
                events,
                JsonOptions,
                token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<DeviceLinkResultPayload?> RegisterDeviceAsync(
        DeviceRegistrationRequestPayload request,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        DeviceLinkResultPayload? result = await PostForResultAsync<DeviceRegistrationRequestPayload, DeviceLinkResultPayload>(
            "api/device/register",
            request,
            token).ConfigureAwait(false);

        StoreBootstrappedCredential(result);
        return result;
    }

    /// <summary>
    /// Stores a credential the control plane bootstrapped during registration.
    ///
    /// This is the acquisition path for a machine nobody signs into interactively: the service
    /// registers as LocalSystem at boot and on every reporting cycle, so a linked device that
    /// somehow has no credential picks one up on its own, including after months dormant.
    ///
    /// The secret is returned exactly once. If the write fails it is gone, so the failure is
    /// logged loudly rather than swallowed -- but it self-heals: the control plane only issues
    /// when no credential exists, so the next registration issues a fresh one.
    /// </summary>
    private static void StoreBootstrappedCredential(DeviceLinkResultPayload? result)
    {
        if (result is null || string.IsNullOrWhiteSpace(result.DeviceSecret))
        {
            return;
        }

        string? deviceId = result.Device?.DeviceId;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        if (Secrets.TryWriteDeviceCredential(deviceId, result.DeviceSecret))
        {
            Secrets.InvalidateDeviceCredentialCache();
            Console.Error.WriteLine($"device_credential_bootstrapped device={deviceId}");
            return;
        }

        Console.Error.WriteLine(
            $"device_credential_bootstrap_store_failed device={deviceId} "
            + "detail=secret was issued but could not be stored; it will be re-issued on the next registration");
    }

    public async Task<DeviceLinkResultPayload?> LinkDeviceAccountAsync(
        DeviceAccountLinkRequestPayload request,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PostForResultAsync<DeviceAccountLinkRequestPayload, DeviceLinkResultPayload>(
            "api/device/link-account",
            request,
            token).ConfigureAwait(false);
    }

    public async Task<DeviceLinkResultPayload?> UnlinkDeviceAccountAsync(
        DeviceAccountUnlinkRequestPayload request,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await PostForResultAsync<DeviceAccountUnlinkRequestPayload, DeviceLinkResultPayload>(
            "api/device/unlink-account",
            request,
            token).ConfigureAwait(false);
    }

    public async Task<UserContextPayload?> GetUserContextAsync(string accountId, CancellationToken token)
    {
        if (_httpClient is null)
        {
            return null;
        }

        try
        {
            string url = "api/agent/user-context?accountId=" + Uri.EscapeDataString(accountId);
            using HttpResponseMessage response = await _httpClient.GetAsync(url, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<UserContextPayload>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<AppCategoryResolveResultPayload?> ResolveAppCategoriesAsync(
        string agentId,
        IReadOnlyList<string> tokens,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (string.IsNullOrWhiteSpace(agentId) || tokens.Count == 0)
        {
            return null;
        }

        return await PostForResultAsync<AppCategoryResolveRequestPayload, AppCategoryResolveResultPayload>(
            "api/agent/categories/resolve",
            new AppCategoryResolveRequestPayload(agentId, tokens),
            token).ConfigureAwait(false);
    }

    public async Task<WindowsReleaseManifestPayload?> GetLatestWindowsReleaseAsync(CancellationToken token)
    {
        if (_httpClient is null)
        {
            return null;
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync("api/client-release/windows/latest", token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<WindowsReleaseManifestPayload>(JsonOptions, token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    private async Task<TResponse?> PostForResultAsync<TRequest, TResponse>(
        string relativeUri,
        TRequest request,
        CancellationToken token)
        where TRequest : class
        where TResponse : class
    {
        if (_httpClient is null)
        {
            return null;
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                relativeUri,
                request,
                JsonOptions,
                token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Replace(":", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
    }
}
