using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Porta.Agent.Common.Identity;
using Porta.Agent.LocalApi;

namespace Porta.Agent.Service.Fleet;

/// <summary>
/// Step 10 phase C/D (2026-08-14) — the bridge between the local API and the control plane for
/// client reports. The same shape as <see cref="NarrativeGateway"/>: these are the only local routes
/// that do not end at local SQLite.
///
/// The server composes and sends because the Resend key must never reach this machine (spec §6a).
/// That reversed a structural guarantee, and the bound this file is responsible for is the one the
/// server cannot enforce alone: <b>the send only ever happens because someone at this machine asked
/// for it.</b> Nothing here runs on a timer, and there is no retry loop.
///
/// Fail-soft everywhere, with one exception that matters more than the rest — see
/// <see cref="SendAsync"/> on why "unknown" must never be reported as "failed".
/// </summary>
internal sealed class ReportGateway : IReportGateway
{
    // Rendering an email server-side and handing it back is a bigger round trip than a status ping,
    // and it is user-initiated so a wait is expected and visible. Sending is bounded the same way:
    // Resend is quick, but a hung connection must not leave a button spinning forever.
    private static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ControlPlaneClient _client;
    private readonly ILocalIdentityLinkStore _identityLinkStore;

    public ReportGateway(ControlPlaneClient client, ILocalIdentityLinkStore identityLinkStore)
    {
        _client = client;
        _identityLinkStore = identityLinkStore;
    }

    public Task<ReportGatewayIdentity> ReadIdentityAsync(CancellationToken token)
    {
        LocalIdentityLinkSnapshot identity = _identityLinkStore.GetCurrent();

        bool linked =
            string.Equals(identity.LinkStatus, LocalIdentityLinkStatuses.Linked, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(identity.AccountId)
            && !string.IsNullOrWhiteSpace(identity.DeviceId);

        // The From name and Reply-to are the SERVER's to resolve, from the account profile it
        // already holds and already uses to address the digest. This machine reports what it knows
        // about itself and nothing about who the user is — that is what keeps the payload free of
        // sender fields and keeps one copy of the identity rather than two.
        return Task.FromResult(new ReportGatewayIdentity(
            AccountId: identity.AccountId ?? string.Empty,
            DeviceId: identity.DeviceId ?? string.Empty,
            MachineId: MachineFingerprint.Current,
            FromName: string.Empty,
            ReplyTo: string.Empty,
            Resolved: linked));
    }

    public async Task<ReportGatewayPreview> PreviewAsync(string reportJson, CancellationToken token)
    {
        try
        {
            if (!_client.IsEnabled)
            {
                return new ReportGatewayPreview(false, null, null, $"client_disabled:{_client.DisabledReason ?? "unknown"}");
            }

            ReportSendPayload? payload = Deserialize(reportJson);
            if (payload is null)
            {
                return new ReportGatewayPreview(false, null, null, "invalid_report");
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(CallBudget);

            ReportPreviewResult? result = await _client
                .PostReportPreviewAsync(payload, cts.Token)
                .ConfigureAwait(false);

            if (result is null)
            {
                return new ReportGatewayPreview(false, null, null, "unreachable");
            }

            return result.Success
                ? new ReportGatewayPreview(true, result.Html, result.Subject, null)
                : new ReportGatewayPreview(false, null, null, result.Reason ?? "refused");
        }
        catch
        {
            return new ReportGatewayPreview(false, null, null, "error");
        }
    }

    /// <summary>
    /// <b>A dropped connection is NOT a failure.</b> The request may have reached the server and the
    /// mail may already be in the client's inbox; the connection dying afterwards tells us nothing
    /// about that. Reporting "failed" would invite the user to press Send again, and the second
    /// press is the one that puts a second copy in front of a paying client — which cannot be
    /// recalled and is exactly the outcome this whole feature is built to avoid.
    ///
    /// So the outcome is <c>Unknown</c>, the local record stays pending, and the user is told the
    /// truth: we do not know yet. The server's idempotency on <c>reportId</c> is the backstop if
    /// they do try again; not lying to them is the first line.
    /// </summary>
    public async Task<ReportGatewayOutcome> SendAsync(string reportJson, CancellationToken token)
    {
        if (!_client.IsEnabled)
        {
            // Nothing left the machine, so this one IS a clean failure.
            return new ReportGatewayOutcome(false, false, null, $"client_disabled:{_client.DisabledReason ?? "unknown"}");
        }

        ReportSendPayload? payload = Deserialize(reportJson);
        if (payload is null)
        {
            return new ReportGatewayOutcome(false, false, null, "invalid_report");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(CallBudget);

            ReportSendResult? result = await _client
                .PostReportSendAsync(payload, cts.Token)
                .ConfigureAwait(false);

            if (result is null)
            {
                // The request left, and we never learned what became of it.
                return new ReportGatewayOutcome(false, true, null, "unknown");
            }

            if (result.Success)
            {
                return new ReportGatewayOutcome(true, false, result.SentAtUtc, null);
            }

            // The server answered and declined. "already_sent" is its idempotency guard speaking:
            // this report went out on an earlier attempt, so nothing was sent now and nothing is
            // wrong — the local record simply needs to catch up.
            return new ReportGatewayOutcome(false, false, null, result.Reason ?? "refused");
        }
        catch
        {
            // Same reasoning as a null result: we do not know, so we do not claim.
            return new ReportGatewayOutcome(false, true, null, "unknown");
        }
    }

    public async Task<bool> DeleteAsync(string reportId, CancellationToken token)
    {
        try
        {
            if (!_client.IsEnabled || string.IsNullOrWhiteSpace(reportId))
            {
                return false;
            }

            LocalIdentityLinkSnapshot identity = _identityLinkStore.GetCurrent();
            if (string.IsNullOrWhiteSpace(identity.AccountId))
            {
                return false;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(CallBudget);

            return await _client
                .DeleteReportAsync(reportId, identity.AccountId!, cts.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            // False means "still there", which is the honest answer when we cannot tell. The caller
            // leaves the report listed rather than reporting a delete that may not have happened.
            return false;
        }
    }

    /// <summary>
    /// THE ONE CHOKE POINT where a report payload becomes identified, so preview and send cannot
    /// disagree about who is asking.
    ///
    /// The local API assembles the document — it knows the hours, the labels and the recipient — but
    /// it does not know the identity, and it must not: the identity lives in the link store on this
    /// side. It therefore writes empty strings for the three id fields and this fills them in.
    /// Before this existed the empties travelled all the way to the server, which reads
    /// <c>accountId</c> from the body and refuses without it, so BOTH <c>/reports/preview</c> and
    /// <c>/reports/send</c> answered 400 and no report could ever leave.
    /// </summary>
    private ReportSendPayload StampIdentity(ReportSendPayload payload)
    {
        LocalIdentityLinkSnapshot identity = _identityLinkStore.GetCurrent();

        return payload with
        {
            AccountId = identity.AccountId ?? string.Empty,
            DeviceId = identity.DeviceId ?? string.Empty,
            // Never Environment.MachineName. See MachineFingerprint.
            MachineId = MachineFingerprint.Current
        };
    }

    private ReportSendPayload? Deserialize(string reportJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(reportJson))
            {
                return null;
            }

            ReportSendPayload? payload =
                JsonSerializer.Deserialize<ReportSendPayload>(reportJson, JsonOptions);

            return payload is null ? null : StampIdentity(payload);
        }
        catch
        {
            return null;
        }
    }
}
