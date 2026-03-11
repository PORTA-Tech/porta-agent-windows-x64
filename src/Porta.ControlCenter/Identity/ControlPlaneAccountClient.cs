using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Porta.ControlCenter.Identity;

internal sealed class ControlPlaneAccountClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(12) };

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public string? GetConfiguredBaseUrl()
    {
        return ReadConfiguredBackendUrl();
    }

    public async Task<ControlPlaneLoginResponse?> LoginAsync(string userId, string password, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        try
        {
            using HttpRequestMessage request = CreateJsonRequest(
                HttpMethod.Post,
                "auth/login",
                new ControlPlaneLoginRequest(userId.Trim(), password),
                accessToken: null);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ControlPlaneLoginResponse>(JsonOptions, token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<ControlPlaneMeProfileView?> GetMeAsync(string accessToken, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        try
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "v1/me", accessToken);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ControlPlaneMeProfileView>(JsonOptions, token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<ControlPlaneProfileUpdateResponse?> UpdateProfileAsync(string accessToken, string displayName, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        try
        {
            using HttpRequestMessage request = CreateJsonRequest(
                HttpMethod.Patch,
                "v1/me/profile",
                new ControlPlaneProfileUpdateRequest(displayName.Trim()),
                accessToken);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ControlPlaneProfileUpdateResponse>(JsonOptions, token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<ControlPlaneDeviceLinkResult?> RevokeMyDeviceAsync(string accessToken, string deviceId, CancellationToken token = default)
    {
        return await PostSessionDeviceActionAsync(accessToken, deviceId, "revoke", token).ConfigureAwait(false);
    }

    public async Task<ControlPlaneDeviceLinkResult?> UnlinkMyDeviceAsync(string accessToken, string deviceId, CancellationToken token = default)
    {
        return await PostSessionDeviceActionAsync(accessToken, deviceId, "unlink", token).ConfigureAwait(false);
    }

    public async Task<bool> LogoutAsync(string accessToken, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return false;
        }

        try
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Post, "auth/logout", accessToken);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ControlPlaneDeviceLinkResult?> RegisterDeviceAsync(
        ControlPlaneDeviceRegistrationRequest payload,
        string? accessToken,
        CancellationToken token = default)
    {
        return await PostDeviceIdentityAsync("api/device/register", payload, accessToken, token).ConfigureAwait(false);
    }

    public async Task<ControlPlaneDeviceLinkResult?> LinkDeviceAsync(
        ControlPlaneDeviceAccountLinkRequest payload,
        string? accessToken,
        CancellationToken token = default)
    {
        return await PostDeviceIdentityAsync("api/device/link-account", payload, accessToken, token).ConfigureAwait(false);
    }

    public async Task<ControlPlaneDeviceLinkResult?> UnlinkDeviceAsync(
        ControlPlaneDeviceAccountUnlinkRequest payload,
        string? accessToken,
        CancellationToken token = default)
    {
        return await PostDeviceIdentityAsync("api/device/unlink-account", payload, accessToken, token).ConfigureAwait(false);
    }

    private async Task<ControlPlaneDeviceLinkResult?> PostDeviceIdentityAsync<TPayload>(
        string path,
        TPayload payload,
        string? accessToken,
        CancellationToken token)
    {
        try
        {
            using HttpRequestMessage request = CreateJsonRequest(HttpMethod.Post, path, payload, accessToken);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ControlPlaneDeviceLinkResult>(JsonOptions, token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task<ControlPlaneDeviceLinkResult?> PostSessionDeviceActionAsync(
        string accessToken,
        string deviceId,
        string action,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        try
        {
            using HttpRequestMessage request = CreateRequest(
                HttpMethod.Post,
                $"v1/me/devices/{Uri.EscapeDataString(deviceId.Trim())}/{action}",
                accessToken);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ControlPlaneDeviceLinkResult>(JsonOptions, token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath, string? accessToken)
    {
        Uri? baseUri = ResolveBaseUri();
        if (baseUri == null)
        {
            throw new InvalidOperationException("Control plane backend URL is not configured.");
        }

        var request = new HttpRequestMessage(method, new Uri(baseUri, relativePath));
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return request;
    }

    private HttpRequestMessage CreateJsonRequest<TPayload>(HttpMethod method, string relativePath, TPayload payload, string? accessToken)
    {
        HttpRequestMessage request = CreateRequest(method, relativePath, accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        return request;
    }

    private static Uri? ResolveBaseUri()
    {
        string? configured = ReadConfiguredBackendUrl();
        if (string.IsNullOrWhiteSpace(configured) || !Uri.TryCreate(configured, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        if (!uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal))
        {
            uri = new Uri($"{uri.AbsoluteUri}/", UriKind.Absolute);
        }

        return uri;
    }

    private static string? ReadConfiguredBackendUrl()
    {
        return Environment.GetEnvironmentVariable("PORTA_BACKEND_URL", EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable("PORTA_BACKEND_URL", EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable("PORTA_BACKEND_URL", EnvironmentVariableTarget.Machine);
    }
}
