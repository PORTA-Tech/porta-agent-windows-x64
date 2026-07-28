using Porta.Agent.LocalApi;

namespace Porta.ControlCenter.Identity;

internal sealed class LocalIdentityLinkCoordinator
{
    private readonly LocalApiClient _apiClient;

    public LocalIdentityLinkCoordinator(LocalApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public Task<LocalApiIdentityLinkResponse?> GetCurrentAsync(CancellationToken token = default)
    {
        return _apiClient.GetIdentityLinkAsync(token);
    }

    public Task<LocalApiIdentityLinkResponse?> LinkSignedInAccountAsync(
        string accountId,
        string orgId,
        CancellationToken token = default)
    {
        return _apiClient.SetIdentityLinkAsync(accountId, orgId, token);
    }

    public Task<LocalApiIdentityLinkResponse?> ClearAsync(CancellationToken token = default)
    {
        return _apiClient.ClearIdentityLinkAsync(token);
    }

    /// <summary>
    /// Hands a freshly issued device credential to the service, which owns the secrets
    /// directory. The Control Center never persists it itself.
    /// </summary>
    public Task<bool> StoreDeviceCredentialAsync(
        string deviceId,
        string deviceSecret,
        CancellationToken token = default)
    {
        return _apiClient.SetDeviceCredentialAsync(deviceId, deviceSecret, token);
    }

    public async Task<bool> HasDeviceCredentialAsync(CancellationToken token = default)
    {
        LocalApiDeviceCredentialStatusResponse? status = await _apiClient
            .GetDeviceCredentialStatusAsync(token)
            .ConfigureAwait(false);

        return status?.Present == true;
    }
}
