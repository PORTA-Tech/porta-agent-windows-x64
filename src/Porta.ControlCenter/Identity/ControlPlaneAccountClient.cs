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

    public async Task<ControlPlaneAuthAccessHelpResponse?> GetAccessHelpAsync(CancellationToken token = default)
    {
        try
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "auth/access-help", accessToken: null);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ControlPlaneAuthAccessHelpResponse>(JsonOptions, token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<ControlPlaneLoginResult> LoginAsync(string userId, string password, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(password))
        {
            return new ControlPlaneLoginResult(false, null, "VALIDATION_ERROR", "Enter your email or account ID and password.");
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
                return await CreateLoginFailureAsync(response, token).ConfigureAwait(false);
            }

            ControlPlaneLoginResponse? payload = await response.Content.ReadFromJsonAsync<ControlPlaneLoginResponse>(JsonOptions, token).ConfigureAwait(false);
            return payload == null
                ? new ControlPlaneLoginResult(false, null, "INVALID_RESPONSE", "The sign-in response was incomplete.")
                : new ControlPlaneLoginResult(true, payload, null, null);
        }
        catch
        {
            return new ControlPlaneLoginResult(false, null, "NETWORK_ERROR", "Could not reach the sign-in server.");
        }
    }

    public async Task<ControlPlaneRegisterResult> RegisterAsync(
        string userId,
        string password,
        string displayName,
        string? signupCode,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(displayName))
        {
            return new ControlPlaneRegisterResult(false, null, "VALIDATION_ERROR", "Display name, email, and password are required.");
        }

        try
        {
            using HttpRequestMessage request = CreateJsonRequest(
                HttpMethod.Post,
                "auth/register",
                new ControlPlaneRegisterRequest(userId.Trim(), password, displayName.Trim(), string.IsNullOrWhiteSpace(signupCode) ? null : signupCode.Trim()),
                accessToken: null);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return await CreateRegisterFailureAsync(response, token).ConfigureAwait(false);
            }

            ControlPlaneRegisterResponse? payload = await response.Content.ReadFromJsonAsync<ControlPlaneRegisterResponse>(JsonOptions, token).ConfigureAwait(false);
            return payload == null
                ? new ControlPlaneRegisterResult(false, null, "INVALID_RESPONSE", "The account-creation response was incomplete.")
                : new ControlPlaneRegisterResult(true, payload, null, null);
        }
        catch
        {
            return new ControlPlaneRegisterResult(false, null, "NETWORK_ERROR", "Could not reach the sign-up server.");
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

    public async Task<ControlPlaneWindowsReleaseManifest?> GetLatestWindowsReleaseAsync(CancellationToken token = default)
    {
        try
        {
            using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "api/client-release/windows/latest", accessToken: null);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ControlPlaneWindowsReleaseManifest>(JsonOptions, token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<ControlPlaneProfileUpdateResponse?> UpdateProfileAsync(string accessToken, string displayName, string? primaryEmail = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        string? normalizedEmail = !string.IsNullOrWhiteSpace(primaryEmail) && primaryEmail.Contains('@')
            ? primaryEmail.Trim().ToLowerInvariant()
            : null;

        try
        {
            using HttpRequestMessage request = CreateJsonRequest(
                HttpMethod.Patch,
                "v1/me/profile",
                new ControlPlaneProfileUpdateRequest(displayName.Trim(), normalizedEmail),
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

    public async Task<ControlPlaneDeviceCallResult> RevokeMyDeviceAsync(string accessToken, string deviceId, CancellationToken token = default)
    {
        return await PostSessionDeviceActionAsync(accessToken, deviceId, "revoke", token).ConfigureAwait(false);
    }

    public async Task<ControlPlaneDeviceCallResult> UnlinkMyDeviceAsync(string accessToken, string deviceId, CancellationToken token = default)
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

    public async Task<ControlPlaneDeviceCallResult> RegisterDeviceAsync(
        ControlPlaneDeviceRegistrationRequest payload,
        string? accessToken,
        CancellationToken token = default)
    {
        return await PostDeviceIdentityAsync("api/device/register", payload, accessToken, token).ConfigureAwait(false);
    }

    public async Task<ControlPlaneDeviceCallResult> LinkDeviceAsync(
        ControlPlaneDeviceAccountLinkRequest payload,
        string? accessToken,
        CancellationToken token = default)
    {
        return await PostDeviceIdentityAsync("api/device/link-account", payload, accessToken, token).ConfigureAwait(false);
    }

    public async Task<ControlPlaneDeviceCallResult> UnlinkDeviceAsync(
        ControlPlaneDeviceAccountUnlinkRequest payload,
        string? accessToken,
        CancellationToken token = default)
    {
        return await PostDeviceIdentityAsync("api/device/unlink-account", payload, accessToken, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the control plane for this device's credential. Requires the account session --
    /// the control plane only issues one for a device already linked to that account, which is
    /// why the Control Center is the only process that can do this.
    /// </summary>
    public async Task<ControlPlaneDeviceCredentialResponse?> IssueDeviceCredentialAsync(
        string deviceId,
        string accessToken,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        try
        {
            using HttpRequestMessage request = CreateJsonRequest(
                HttpMethod.Post,
                "api/device/issue-credential",
                new { deviceId = deviceId.Trim() },
                accessToken);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content
                .ReadFromJsonAsync<ControlPlaneDeviceCredentialResponse>(JsonOptions, token)
                .ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task<ControlPlaneDeviceCallResult> PostDeviceIdentityAsync<TPayload>(
        string path,
        TPayload payload,
        string? accessToken,
        CancellationToken token)
    {
        try
        {
            using HttpRequestMessage request = CreateJsonRequest(HttpMethod.Post, path, payload, accessToken);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
            return await ReadDeviceCallResultAsync(response, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return ControlPlaneDeviceCallResult.Unreachable;
        }
    }

    private async Task<ControlPlaneDeviceCallResult> PostSessionDeviceActionAsync(
        string accessToken,
        string deviceId,
        string action,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(deviceId))
        {
            return new ControlPlaneDeviceCallResult(ControlPlaneCallOutcome.Rejected, null);
        }

        try
        {
            using HttpRequestMessage request = CreateRequest(
                HttpMethod.Post,
                $"v1/me/devices/{Uri.EscapeDataString(deviceId.Trim())}/{action}",
                accessToken);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
            return await ReadDeviceCallResultAsync(response, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return ControlPlaneDeviceCallResult.Unreachable;
        }
    }

    /// <summary>
    /// Classifies a device-call response. A 401 must stay distinguishable from every other
    /// failure: it is the only one the user can fix, by signing in again.
    /// </summary>
    internal static async Task<ControlPlaneDeviceCallResult> ReadDeviceCallResultAsync(
        HttpResponseMessage response,
        CancellationToken token)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return ControlPlaneDeviceCallResult.Unauthorized;
        }

        if (!response.IsSuccessStatusCode)
        {
            return new ControlPlaneDeviceCallResult(ControlPlaneCallOutcome.Rejected, null);
        }

        try
        {
            ControlPlaneDeviceLinkResult? payload = await response.Content
                .ReadFromJsonAsync<ControlPlaneDeviceLinkResult>(JsonOptions, token)
                .ConfigureAwait(false);
            return payload == null
                ? new ControlPlaneDeviceCallResult(ControlPlaneCallOutcome.Rejected, null)
                : new ControlPlaneDeviceCallResult(ControlPlaneCallOutcome.Success, payload);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return new ControlPlaneDeviceCallResult(ControlPlaneCallOutcome.Rejected, null);
        }
    }

    private static async Task<ControlPlaneLoginResult> CreateLoginFailureAsync(HttpResponseMessage response, CancellationToken token)
    {
        ControlPlaneApiErrorResponse? error = await TryReadErrorAsync(response, token).ConfigureAwait(false);
        string? message = error?.ErrorCode switch
        {
            "ACCOUNT_NOT_FOUND" => "Account not found. Create one below or ask your admin.",
            "INVALID_CREDENTIALS" => "Incorrect email or password. Check your details and try again.",
            _ => error?.Message
        };
        if (string.IsNullOrWhiteSpace(message))
        {
            int statusCode = (int)response.StatusCode;
            message = statusCode switch
            {
                401 => "Incorrect email or password. Check your details and try again.",
                404 => "Account not found. Create one below or ask your admin.",
                _ when statusCode >= 500 => "The sign-in server is not available right now. Please try again in a moment.",
                _ => "Sign-in failed. Please try again."
            };
        }

        return new ControlPlaneLoginResult(false, null, error?.ErrorCode, message);
    }

    private static async Task<ControlPlaneRegisterResult> CreateRegisterFailureAsync(HttpResponseMessage response, CancellationToken token)
    {
        ControlPlaneApiErrorResponse? error = await TryReadErrorAsync(response, token).ConfigureAwait(false);
        string? message = error?.ErrorCode switch
        {
            "USER_EXISTS" => "An account with that email already exists. Try signing in instead.",
            "SIGNUP_DISABLED" => "Self-service sign-up is not enabled on this server.",
            "INVALID_SIGNUP_CODE" => "That sign-up code was not accepted.",
            "INVALID_USER_ID" => "Use a valid email address as your account ID.",
            "WEAK_PASSWORD" => error?.Message,
            _ => error?.Message
        };
        if (string.IsNullOrWhiteSpace(message))
        {
            int statusCode = (int)response.StatusCode;
            message = statusCode switch
            {
                409 => "An account with that email already exists. Try signing in instead.",
                403 => "Self-service sign-up is not enabled on this server.",
                400 => "Check your display name, email, and password and try again.",
                _ when statusCode >= 500 => "The server could not create your account right now. Please try again in a moment.",
                _ => "Could not create the account. Please try again."
            };
        }

        return new ControlPlaneRegisterResult(false, null, error?.ErrorCode, message);
    }

    private static async Task<ControlPlaneApiErrorResponse?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ControlPlaneApiErrorResponse>(JsonOptions, token).ConfigureAwait(false);
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
