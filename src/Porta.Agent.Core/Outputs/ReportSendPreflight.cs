namespace Porta.Agent.Core.Outputs;

/// <summary>
/// Step 10 phase C/D (2026-08-14) — everything that must be true before a report may leave.
///
/// <see cref="ReportSendReadiness"/> answers "is the DOCUMENT fit to send" — is it named, does it
/// have lines. This answers "is the ENVELOPE fit to send" — the three addresses on the confirm
/// screen. Both must pass. They are separate because they fail for different reasons and the user
/// fixes them in different places.
///
/// <b>Nothing here belongs to any particular user.</b> The From name and Reply-to arrive as arguments,
/// resolved from that user's own account profile at send time (§6b); the From address is the product's
/// constant. `NoPersonalIdentifiersShipTests` fails the build if a name or address is ever typed in.
/// </summary>
public static class ReportSendPreflight
{
    /// <summary>
    /// The product's own From address, per §6. `reports@`, not `ari@`: mail providers score sending
    /// addresses separately, so if the nightly digest is ever marked as spam, client reports must not
    /// inherit that reputation — or the other way round.
    /// </summary>
    public const string FromAddress = "reports@useporta.com";

    /// <summary>
    /// The identity service assigns <c>displaced-&lt;accountId&gt;@porta.internal</c> when two
    /// accounts collide on an email, so a profile can legitimately hold an address nobody reads.
    /// </summary>
    private const string SyntheticDomain = "@porta.internal";

    public static ReportSendVerdict Evaluate(
        AgentReportDraft draft,
        IReadOnlyCollection<long>? excludedTaskIds,
        string fromName,
        string replyTo,
        string recipientEmail)
    {
        ArgumentNullException.ThrowIfNull(draft);

        AgentReportReadiness readiness = ReportSendReadiness.Evaluate(draft, excludedTaskIds);

        var refusals = new List<ReportSendRefusal>();

        if (!readiness.CanSend)
        {
            refusals.Add(ReportSendRefusal.DocumentNotReady);
        }

        if (string.IsNullOrWhiteSpace(fromName))
        {
            refusals.Add(ReportSendRefusal.FromNameMissing);
        }

        // THE HARD BLOCK, and it is not a warning the user may click past. A client replying to a
        // @porta.internal address is a GUARANTEED silent failure: the reply goes nowhere and neither
        // party ever learns that it did. The whole point of this design is that hitting reply reaches
        // the user, so an address that cannot receive one makes the report a lie about itself.
        // Yadin, 2026-08-14: refuse the send and say why.
        if (IsSynthetic(replyTo))
        {
            refusals.Add(ReportSendRefusal.ReplyToIsSynthetic);
        }
        else if (!LooksLikeAddress(replyTo))
        {
            refusals.Add(ReportSendRefusal.ReplyToInvalid);
        }

        // A synthetic recipient is not a real client either, and sending there is the same dead end
        // in the other direction.
        if (IsSynthetic(recipientEmail))
        {
            refusals.Add(ReportSendRefusal.RecipientIsSynthetic);
        }
        else if (!LooksLikeAddress(recipientEmail))
        {
            refusals.Add(ReportSendRefusal.RecipientInvalid);
        }

        return new ReportSendVerdict(
            CanSend: refusals.Count == 0,
            Readiness: readiness,
            Refusals: refusals);
    }

    private static bool IsSynthetic(string? address)
        => address is not null
           && address.Trim().EndsWith(SyntheticDomain, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deliberately shallow. Full RFC validation rejects addresses that genuinely deliver, and the
    /// real protection is §6b showing the address in full so the user sees it. This catches the empty
    /// and the obviously-not-an-address cases, nothing cleverer.
    /// </summary>
    private static bool LooksLikeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        string trimmed = address.Trim();
        int at = trimmed.IndexOf('@', StringComparison.Ordinal);

        return at > 0
               && at == trimmed.LastIndexOf('@')
               && at < trimmed.Length - 1
               && trimmed.IndexOf('.', at) > at + 1
               && !trimmed.EndsWith('.')
               && !trimmed.Contains(' ', StringComparison.Ordinal);
    }
}

/// <summary>Why a send was refused. Codes, not sentences — the wording belongs to the UI.</summary>
public enum ReportSendRefusal
{
    /// <summary>The project or a line still has no client-facing name, or there are no lines.</summary>
    DocumentNotReady,
    FromNameMissing,

    /// <summary>
    /// Reply-to is a <c>@porta.internal</c> address. A hard refusal, never a warning.
    /// </summary>
    ReplyToIsSynthetic,
    ReplyToInvalid,
    RecipientIsSynthetic,
    RecipientInvalid
}

public sealed record ReportSendVerdict(
    bool CanSend,
    AgentReportReadiness Readiness,
    IReadOnlyList<ReportSendRefusal> Refusals);
