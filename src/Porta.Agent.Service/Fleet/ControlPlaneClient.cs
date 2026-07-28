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
    private static readonly TimeSpan UnauthorizedLogInterval = TimeSpan.FromMinutes(5);
    private readonly object _unauthorizedLogGate = new();
    private DateTimeOffset? _lastUnauthorizedLogUtc;

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

        _httpClient = new HttpClient(handler)
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
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (_httpClient is null)
        {
            return null;
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
                return null;
            }

            NarrativeResponsePayload? parsed = await response.Content
                .ReadFromJsonAsync<NarrativeResponsePayload>(cancellationToken: token)
                .ConfigureAwait(false);
            string? line = parsed?.Line?.Trim();
            return string.IsNullOrWhiteSpace(line) ? null : line;
        }
        catch
        {
            return null;
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
        return await PostForResultAsync<DeviceRegistrationRequestPayload, DeviceLinkResultPayload>(
            "api/device/register",
            request,
            token).ConfigureAwait(false);
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
            using var message = new HttpRequestMessage(HttpMethod.Post, relativeUri)
            {
                Content = JsonContent.Create(request, options: JsonOptions)
            };
            AttachDeviceCredential(message);

            using HttpResponseMessage response = await _httpClient.SendAsync(message, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogUnauthorizedThrottled(response, relativeUri);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Attaches the device credential when one exists. A device nobody has signed into has
    /// none and never will, so its absence is normal and the call proceeds without headers --
    /// the control plane accepts those as anonymous inventory.
    /// </summary>
    private static void AttachDeviceCredential(HttpRequestMessage message)
    {
        Secrets.DeviceCredentialReadResult credential = Secrets.ReadDeviceCredential();
        if (!credential.Success
            || string.IsNullOrWhiteSpace(credential.DeviceId)
            || string.IsNullOrWhiteSpace(credential.DeviceSecret))
        {
            return;
        }

        message.Headers.TryAddWithoutValidation("X-Porta-Device-Id", credential.DeviceId);
        message.Headers.TryAddWithoutValidation("X-Porta-Device-Secret", credential.DeviceSecret);
    }

    /// <summary>
    /// A 401 is the one failure worth its own line. Everything else here collapses to null and
    /// retries, so without this a credential-less or rotated-out agent would fail silently every
    /// ten seconds forever and look identical to the control plane being unreachable.
    /// </summary>
    private void LogUnauthorizedThrottled(HttpResponseMessage response, string relativeUri)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
        {
            return;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        lock (_unauthorizedLogGate)
        {
            if (_lastUnauthorizedLogUtc.HasValue && (nowUtc - _lastUnauthorizedLogUtc.Value) < UnauthorizedLogInterval)
            {
                return;
            }

            _lastUnauthorizedLogUtc = nowUtc;
        }

        bool hasCredential = Secrets.ReadDeviceCredential().Success;
        Console.Error.WriteLine(
            $"fleet_unauthorized route={relativeUri} device_credential={(hasCredential ? "present_but_rejected" : "missing")} "
            + "hint=sign in through Control Center to issue a device credential");
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
