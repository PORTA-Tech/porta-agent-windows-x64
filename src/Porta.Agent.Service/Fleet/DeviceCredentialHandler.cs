using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Porta.Agent.Core.Security;

namespace Porta.Agent.Service.Fleet;

/// <summary>
/// Attaches the device credential to every request leaving ControlPlaneClient, and logs 401s.
///
/// This lives in the pipeline rather than in each call site on purpose. The first version of
/// this attached headers inside PostForResultAsync only, which silently missed machine-summary,
/// daily-stats, narrative, daily-digest, action-events and user-context -- every one of them
/// builds its request inline instead of going through that helper. A handler cannot be
/// forgotten by the next method somebody adds.
/// </summary>
internal sealed class DeviceCredentialHandler : DelegatingHandler
{
    private static readonly TimeSpan UnauthorizedLogInterval = TimeSpan.FromMinutes(5);
    private readonly object _logGate = new();
    private DateTimeOffset? _lastUnauthorizedLogUtc;

    private readonly Func<Secrets.DeviceCredentialReadResult> _readCredential;

    public DeviceCredentialHandler(
        HttpMessageHandler innerHandler,
        Func<Secrets.DeviceCredentialReadResult>? readCredential = null)
        : base(innerHandler)
    {
        _readCredential = readCredential ?? (() => Secrets.ReadDeviceCredential());
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        AttachCredential(request, _readCredential);

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            LogUnauthorizedThrottled(request);
        }

        return response;
    }

    /// <summary>
    /// A device nobody has signed into has no credential and never will, so its absence is
    /// normal: the request goes out bare and the control plane takes it as anonymous inventory.
    /// </summary>
    private static void AttachCredential(
        HttpRequestMessage request,
        Func<Secrets.DeviceCredentialReadResult> readCredential)
    {
        Secrets.DeviceCredentialReadResult credential = readCredential();
        if (!credential.Success
            || string.IsNullOrWhiteSpace(credential.DeviceId)
            || string.IsNullOrWhiteSpace(credential.DeviceSecret))
        {
            return;
        }

        request.Headers.Remove("X-Porta-Device-Id");
        request.Headers.Remove("X-Porta-Device-Secret");
        request.Headers.TryAddWithoutValidation("X-Porta-Device-Id", credential.DeviceId);
        request.Headers.TryAddWithoutValidation("X-Porta-Device-Secret", credential.DeviceSecret);
    }

    /// <summary>
    /// Every other failure in this client collapses to null or false and retries, so without
    /// this a credential-less or rotated-out agent would fail silently every ten seconds and
    /// look identical to an unreachable control plane. Throttled because of that same cadence.
    /// </summary>
    private void LogUnauthorizedThrottled(HttpRequestMessage request)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        lock (_logGate)
        {
            if (_lastUnauthorizedLogUtc.HasValue && (nowUtc - _lastUnauthorizedLogUtc.Value) < UnauthorizedLogInterval)
            {
                return;
            }

            _lastUnauthorizedLogUtc = nowUtc;
        }

        bool hasCredential = _readCredential().Success;
        Console.Error.WriteLine(
            $"fleet_unauthorized route={request.RequestUri?.AbsolutePath} "
            + $"device_credential={(hasCredential ? "present_but_rejected" : "missing")} "
            + "hint=sign in through Control Center to issue a device credential");
    }
}
