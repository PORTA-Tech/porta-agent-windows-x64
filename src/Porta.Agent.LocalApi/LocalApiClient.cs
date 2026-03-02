using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Porta.Agent.Core;

namespace Porta.Agent.LocalApi;

[SupportedOSPlatform("windows")]
public sealed class LocalApiClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private readonly HttpClient _httpClient;
    private readonly LocalApiRuntimeSettings? _settings;
    private string _host = "127.0.0.1";
    private int _port;

    public LocalApiClient()
    {
        _settings = LocalApiRuntimeSettingsStore.Load();
        if (_settings == null)
        {
            _httpClient = new HttpClient { Timeout = RequestTimeout };
            return;
        }

        LocalApiEndpointSettings? endpoint = LocalApiRuntimeSettingsStore.LoadEndpoint();
        _port = endpoint?.Port ?? _settings.Port;
        _host = endpoint?.Host ?? "127.0.0.1";
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"http://{_host}:{_port}"),
            Timeout = RequestTimeout
        };
        if (_settings.LocalApiAuthReady && !string.IsNullOrWhiteSpace(_settings.AuthToken))
        {
            _httpClient.DefaultRequestHeaders.Add("X-PORTA-TOKEN", _settings.AuthToken);
        }
    }

    public bool IsConfigured => _settings != null;

    public async Task<LocalApiStatusResponse?> GetStatusAsync(CancellationToken token = default)
    {
        if (_settings == null)
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            return await _httpClient.GetFromJsonAsync<LocalApiStatusResponse>("/v1/status", token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiCurrentSnapshotResponse?> GetCurrentSnapshotAsync(CancellationToken token = default)
    {
        if (_settings == null)
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            return await _httpClient.GetFromJsonAsync<LocalApiCurrentSnapshotResponse>("/v1/snapshot/current", token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiInputsSummaryResponse?> GetInputsSummaryAsync(int windowSeconds = 60, CancellationToken token = default)
    {
        if (_settings == null)
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            int window = Math.Clamp(windowSeconds, 5, 3600);
            return await _httpClient.GetFromJsonAsync<LocalApiInputsSummaryResponse>($"/v1/inputs/summary?window={window}", token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<LocalApiEventResponse>> GetRecentEventsAsync(int limit = 50, DateTimeOffset? sinceUtc = null, CancellationToken token = default)
    {
        if (_settings == null)
        {
            return [];
        }

        try
        {
            RefreshEndpointBinding();
            int boundedLimit = Math.Clamp(limit, 1, 500);
            string path = $"/v1/events/recent?limit={boundedLimit}";
            if (sinceUtc.HasValue)
            {
                string encoded = Uri.EscapeDataString(sinceUtc.Value.ToString("O"));
                path = $"{path}&since={encoded}";
            }

            IReadOnlyList<LocalApiEventResponse>? response = await _httpClient.GetFromJsonAsync<IReadOnlyList<LocalApiEventResponse>>(path, token).ConfigureAwait(false);
            return response ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<LocalApiTimelineResponse?> GetTimelineAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        int bucketSeconds = Time.bucketSeconds,
        CancellationToken token = default)
    {
        if (_settings == null)
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            _ = bucketSeconds;
            int bucket = Time.bucketSeconds;
            string from = Uri.EscapeDataString(fromUtc.ToString("O"));
            string to = Uri.EscapeDataString(toUtc.ToString("O"));
            string path = $"/v1/timeline?from={from}&to={to}&bucketSeconds={bucket}";
            return await _httpClient.GetFromJsonAsync<LocalApiTimelineResponse>(path, token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiSettingsResponse?> GetEffectiveSettingsAsync(CancellationToken token = default)
    {
        if (_settings == null)
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            return await _httpClient.GetFromJsonAsync<LocalApiSettingsResponse>("/v1/settings/effective", token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiRetentionResponse?> GetDataRetentionAsync(CancellationToken token = default)
    {
        if (_settings == null)
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            return await _httpClient.GetFromJsonAsync<LocalApiRetentionResponse>("/v1/data/retention", token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public Task<bool> PauseAsync(string? reason = null, DateTimeOffset? untilUtc = null, CancellationToken token = default)
    {
        return PostAsync("/v1/control/pause", new LocalApiPauseRequest(reason, untilUtc), token);
    }

    public Task<bool> ResumeAsync(CancellationToken token = default)
    {
        return PostAsync("/v1/control/resume", null, token);
    }

    public Task<bool> GrantConsentAsync(CancellationToken token = default)
    {
        return PostAsync("/v1/consent/grant", null, token);
    }

    public Task<bool> RevokeConsentAsync(CancellationToken token = default)
    {
        return PostAsync("/v1/consent/revoke", null, token);
    }

    public async Task<LocalApiSettingsResponse?> UpdateSettingsAsync(LocalApiUpdateSettingsRequest request, CancellationToken token = default)
    {
        if (_settings == null)
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/settings/update", content, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<LocalApiSettingsResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiRecalibrationResponse?> RecalibrateBaselineAsync(CancellationToken token = default)
    {
        if (_settings == null)
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/baseline/recalibrate", content, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<LocalApiRecalibrationResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiExportResponse?> ExportDataAsync(CancellationToken token = default)
    {
        if (_settings == null)
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/data/export", content, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<LocalApiExportResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public Task<bool> DeleteLocalDataAsync(CancellationToken token = default)
    {
        return PostAsync("/v1/data/delete_local", null, token);
    }

    public async Task<LocalApiAckResponse?> RestartServiceAsync(CancellationToken token = default)
    {
        if (_settings == null)
        {
            return null;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/service/restart", content, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<LocalApiAckResponse>(cancellationToken: token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<LocalApiAckResponse?> TriggerTestInterventionAsync(string action, CancellationToken token = default)
    {
        if (_settings == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(action))
        {
            return new LocalApiAckResponse(
                Success: false,
                Message: "missing_action",
                TimestampUtc: DateTimeOffset.UtcNow);
        }

        try
        {
            RefreshEndpointBinding();
            var request = new LocalApiTestInterventionRequest(action.Trim());
            using var content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync("/v1/testing/intervention", content, token).ConfigureAwait(false);
            LocalApiAckResponse? payload = await response.Content.ReadFromJsonAsync<LocalApiAckResponse>(cancellationToken: token).ConfigureAwait(false);
            return payload ?? new LocalApiAckResponse(
                Success: false,
                Message: response.IsSuccessStatusCode ? "empty_response" : $"http_{(int)response.StatusCode}",
                TimestampUtc: DateTimeOffset.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> PostAsync(string path, object? payload, CancellationToken token)
    {
        if (_settings == null)
        {
            return false;
        }

        try
        {
            RefreshEndpointBinding();
            using var content = payload == null
                ? new StringContent(string.Empty, Encoding.UTF8, "application/json")
                : new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.PostAsync(path, content, token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private void RefreshEndpointBinding()
    {
        if (_settings == null)
        {
            return;
        }

        LocalApiEndpointSettings? endpoint = LocalApiRuntimeSettingsStore.LoadEndpoint();
        int nextPort = endpoint?.Port ?? _settings.Port;
        string nextHost = endpoint?.Host ?? "127.0.0.1";

        if (nextPort == _port && string.Equals(nextHost, _host, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _port = nextPort;
        _host = nextHost;
        _httpClient.BaseAddress = new Uri($"http://{_host}:{_port}");
    }
}
