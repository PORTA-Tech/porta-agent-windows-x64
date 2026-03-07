namespace Porta.UserAgent;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Porta.Agent.Common.UserAgent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

public sealed class UserAgentHost : BackgroundService
{
    private readonly ILogger<UserAgentHost> _logger;
    private readonly IUserAgentSessionContextStore _sessionContextStore;

    public UserAgentHost(
        ILogger<UserAgentHost> logger,
        IUserAgentSessionContextStore sessionContextStore)
    {
        _logger = logger;
        _sessionContextStore = sessionContextStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Process currentProcess = Process.GetCurrentProcess();
        int sessionId = currentProcess.SessionId;
        string captureContext = IsInteractiveUserSession(sessionId)
            ? UserAgentCaptureContexts.UserSession
            : UserAgentCaptureContexts.ServiceSession;

        _logger.LogInformation(
            "Porta.UserAgent running in process {ProcessId} with session {SessionId} and capture context {CaptureContext}.",
            Environment.ProcessId,
            sessionId,
            captureContext);

        if (captureContext == UserAgentCaptureContexts.ServiceSession)
        {
            _logger.LogWarning("Porta.UserAgent started outside an interactive desktop session; reporting ServiceSession.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            PublishSessionContext(captureContext, sessionId);
            _logger.LogDebug("Porta.UserAgent heartbeat.");
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Porta.UserAgent stopping.");
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void PublishSessionContext(string captureContext, int sessionId)
    {
        try
        {
            _sessionContextStore.WriteCurrent(new UserAgentSessionContextSnapshot(
                SchemaVersion: UserAgentSessionContextStore.CurrentSchemaVersion,
                CaptureContext: captureContext,
                ProcessId: Environment.ProcessId,
                SessionId: sessionId,
                UpdatedAtUtc: DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish user-session capture context heartbeat.");
        }
    }

    private static bool IsInteractiveUserSession(int sessionId)
    {
        return Environment.UserInteractive && sessionId > 0;
    }
}
