namespace Porta.Agent.Service.Fleet;

using Porta.Agent.LocalApi;

/// <summary>
/// Thread-safe cache for user context fetched from the control plane.
/// Populated by UserContextRefresher; read by the local API's /v1/session/ari-context endpoint.
/// </summary>
internal sealed class AriContextCache : IAriContextProvider
{
    private static readonly LocalApiAriContextResponse Empty = new(null, null, null, null, null);
    private volatile LocalApiAriContextResponse _context = Empty;

    public LocalApiAriContextResponse GetContext() => _context;

    public void Update(LocalApiAriContextResponse context) => _context = context;
}
