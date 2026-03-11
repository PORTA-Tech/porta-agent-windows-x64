using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

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

        _httpClient = new HttpClient(handler)
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(5)
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
