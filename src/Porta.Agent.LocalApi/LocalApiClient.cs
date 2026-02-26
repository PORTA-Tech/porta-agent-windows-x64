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
