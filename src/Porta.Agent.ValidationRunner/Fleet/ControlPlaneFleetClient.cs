using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Porta.Agent.ValidationRunner.Fleet;

internal sealed class ControlPlaneFleetClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly JsonSerializerOptions _jsonWriteOptions = new(JsonSerializerDefaults.Web);

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
        IReadOnlyList<MachineSummaryRow>? payload = await response.Content.ReadFromJsonAsync<IReadOnlyList<MachineSummaryRow>>(_jsonReadOptions, cancellationToken).ConfigureAwait(false);
        return payload ?? Array.Empty<MachineSummaryRow>();
    }

    public async Task<MachineSummaryRow?> GetMachineSummaryAsync(string machineId, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync($"v1/machines/{Uri.EscapeDataString(machineId)}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MachineSummaryRow>(_jsonReadOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MachineActionRow>> GetRecentActionsAsync(int limit, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync($"v1/actions/recent?limit={Math.Clamp(limit, 1, 500)}", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        IReadOnlyList<MachineActionRow>? payload = await response.Content.ReadFromJsonAsync<IReadOnlyList<MachineActionRow>>(_jsonReadOptions, cancellationToken).ConfigureAwait(false);
        return payload ?? Array.Empty<MachineActionRow>();
    }

    public async Task<FleetSystemHealthResponse?> GetSystemHealthAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync("v1/system/health", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FleetSystemHealthResponse>(_jsonReadOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControlPlaneLoginResponse?> LoginAsync(string userId, string password, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "auth/login")
        {
            Content = JsonContent.Create(
                new
                {
                    user_id = userId,
                    password
                },
                options: _jsonWriteOptions)
        };

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<ControlPlaneLoginResponse>(_jsonReadOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControlPlaneMeProfile?> GetMeAsync(string accessToken, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateSessionRequest(HttpMethod.Get, "v1/me", accessToken);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<ControlPlaneMeProfile>(_jsonReadOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ControlPlaneDeviceProfile>> GetSessionDevicesAsync(string accessToken, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateSessionRequest(HttpMethod.Get, "v1/me/devices", accessToken);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        IReadOnlyList<ControlPlaneDeviceProfile>? payload =
            await response.Content.ReadFromJsonAsync<IReadOnlyList<ControlPlaneDeviceProfile>>(_jsonReadOptions, cancellationToken).ConfigureAwait(false);
        return payload ?? Array.Empty<ControlPlaneDeviceProfile>();
    }

    public async Task<DeviceLinkResultRow?> RegisterDeviceAsync(DeviceRegistrationRequestRow payload, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.PostAsJsonAsync("api/device/register", payload, _jsonWriteOptions, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<DeviceLinkResultRow>(_jsonReadOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceLinkResultRow?> LinkDeviceAsync(
        DeviceAccountLinkRequestRow payload,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateSessionRequest(HttpMethod.Post, "api/device/link-account", accessToken);
        request.Content = JsonContent.Create(payload, options: _jsonWriteOptions);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<DeviceLinkResultRow>(_jsonReadOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceLinkResultRow?> RevokeSessionDeviceAsync(string accessToken, string deviceId, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateSessionRequest(
            HttpMethod.Post,
            $"v1/me/devices/{Uri.EscapeDataString(deviceId)}/revoke",
            accessToken);
        request.Content = JsonContent.Create(new { }, options: _jsonWriteOptions);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<DeviceLinkResultRow>(_jsonReadOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RolloutValidationReportPayload?> PublishRolloutValidationReportAsync(
        RolloutValidationReportPayload payload,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response =
            await _httpClient.PostAsJsonAsync("v1/rollout/validation-gates", payload, _jsonWriteOptions, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<RolloutValidationReportPayload>(_jsonReadOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RolloutValidationReportPayload?> GetRolloutValidationReportAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync("v1/rollout/validation-gates", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RolloutValidationReportPayload>(_jsonReadOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ControlPlaneRolloutEvaluationPayload?> GetRolloutEvaluationAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync("v1/rollout/evaluate", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<ControlPlaneRolloutEvaluationPayload>(_jsonReadOptions, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateSessionRequest(HttpMethod method, string relativePath, string accessToken)
    {
        var request = new HttpRequestMessage(method, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
