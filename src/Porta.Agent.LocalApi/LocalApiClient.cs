using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Porta.Agent.LocalApi;

public sealed class LocalApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly LocalApiRuntimeSettings? _settings;
    private readonly LocalApiEndpointSettings? _endpoint;

    public LocalApiClient()
    {
        _settings = LocalApiRuntimeSettingsStore.Load();
        _endpoint = LocalApiRuntimeSettingsStore.LoadEndpoint();
        if (_settings == null)
        {
            _httpClient = new HttpClient();
            return;
        }

        int port = _endpoint?.Port ?? _settings.Port;
        string host = _endpoint?.Host ?? "127.0.0.1";
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri($"http://{host}:{port}")
        };
        _httpClient.DefaultRequestHeaders.Add("X-PORTA-TOKEN", _settings.AuthToken);
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

    public async Task<LocalApiTimelineResponse?> GetTimelineAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, int bucketSeconds = 5, CancellationToken token = default)
    {
        if (_settings == null)
        {
            return null;
        }

        try
        {
            int bucket = Math.Clamp(bucketSeconds, 1, 60);
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

    private async Task<bool> PostAsync(string path, object? payload, CancellationToken token)
    {
        if (_settings == null)
        {
            return false;
        }

        try
        {
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
}
