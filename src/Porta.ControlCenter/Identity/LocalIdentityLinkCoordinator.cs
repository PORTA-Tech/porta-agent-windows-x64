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
}
