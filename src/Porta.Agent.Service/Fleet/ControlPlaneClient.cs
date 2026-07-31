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
