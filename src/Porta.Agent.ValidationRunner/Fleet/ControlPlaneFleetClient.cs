using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Porta.Agent.ValidationRunner.Fleet;

internal sealed class ControlPlaneFleetClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ControlPlaneFleetClient(string baseUrl, string operatorId, string operatorRole)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("Control plane base URL is required.", nameof(baseUrl));
        }

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Add("x-operator-id", operatorId.Trim());
        _httpClient.DefaultRequestHeaders.Add("x-operator-role", operatorRole.Trim());
    }

    public async Task<IReadOnlyList<MachineSummaryRow>> GetMachineSummariesAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync("v1/machines/summary", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        IReadOnlyList<MachineSummaryRow>? payload = await response.Content.ReadFromJsonAsync<IReadOnlyList<MachineSummaryRow>>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        return payload ?? Array.Empty<MachineSummaryRow>();
    }

    public async Task<IReadOnlyList<MachineActionRow>> GetRecentActionsAsync(int limit, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync($"v1/actions/recent?limit={Math.Clamp(limit, 1, 500)}", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        IReadOnlyList<MachineActionRow>? payload = await response.Content.ReadFromJsonAsync<IReadOnlyList<MachineActionRow>>(_jsonOptions, cancellationToken).ConfigureAwait(false);
        return payload ?? Array.Empty<MachineActionRow>();
    }

    public async Task<FleetSystemHealthResponse?> GetSystemHealthAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync("v1/system/health", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FleetSystemHealthResponse>(_jsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
