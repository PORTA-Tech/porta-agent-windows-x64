using Porta.Agent.Core.Outputs;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// Step 10 phase C/D — the envelope. Each of these is a way a report could leave in a state that
/// looks fine on screen and fails silently in a client's inbox.
/// </summary>
public sealed class ReportSendPreflightTests
{
    private const string GoodFrom = "A Real Person";
    private const string GoodReplyTo = "person@example.test";
    private const string GoodRecipient = "accounts@example-client.test";

    private static AgentReportDraft Draft(string clientName = "Nolan Studio", string label = "Chapter three")
        => new(
            ProjectId: 1,
            ProjectName: "nolan - chase invoice",
            ClientName: clientName,
            RangeStartLocal: "2026-08-12",
            RangeEndLocal: "2026-08-13",
            TotalHours: 6.2,
            DaysWorked: 1,
            LinesFinished: 1,
            Lines:
            [
                new(TaskId: 1, Title: "finish my writting part", ClientLabel: label,
                    Finished: true, Hours: 6.2, UnattributedHours: 0d, UnattributedMeasurable: true)
            ],
            Days: [new("2026-08-12", 6.2)]);

    private static ReportSendVerdict Check(
        string from = GoodFrom, string replyTo = GoodReplyTo, string recipient = GoodRecipient,
        AgentReportDraft? draft = null)
        => ReportSendPreflight.Evaluate(draft ?? Draft(), null, from, replyTo, recipient);

    [Fact]
    public void AFullyAddressedReadyReportCanSend()
    {
        ReportSendVerdict verdict = Check();

        Assert.True(verdict.CanSend);
        Assert.Empty(verdict.Refusals);
    }

    /// <summary>
    /// THE HARD BLOCK. `displaced-<accountId>@porta.internal` is assigned when two accounts collide
    /// on an email, so a profile can hold an address nobody reads. A client replying to it is a
    /// guaranteed silent failure — the reply vanishes and neither party learns that it did — and the
    /// entire point of this design is that hitting reply reaches the user. Refusal, never a warning.
    /// </summary>
    [Fact]
    public void ASyntheticReplyToIsRefused_NotWarned()
    {
        ReportSendVerdict verdict = Check(replyTo: "displaced-acct_9f21c8@porta.internal");

        Assert.False(verdict.CanSend);
        Assert.Contains(ReportSendRefusal.ReplyToIsSynthetic, verdict.Refusals);
    }

    [Fact]
    public void ASyntheticReplyToIsRefusedWhateverItsCase()
    {
        Assert.Contains(
            ReportSendRefusal.ReplyToIsSynthetic,
            Check(replyTo: "Displaced-Acct@PORTA.INTERNAL").Refusals);
    }

    /// <summary>The same dead end in the other direction.</summary>
    [Fact]
    public void ASyntheticRecipientIsRefused()
    {
        Assert.Contains(
            ReportSendRefusal.RecipientIsSynthetic,
            Check(recipient: "displaced-acct_1@porta.internal").Refusals);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    [InlineData("@example.test")]
    [InlineData("person@")]
    [InlineData("person@example")]
    [InlineData("two@at@example.test")]
    [InlineData("person@example.test.")]
    [InlineData("person name@example.test")]
    public void AReplyToThatCannotReceiveMailIsRefused(string replyTo)
    {
        ReportSendVerdict verdict = Check(replyTo: replyTo);

        Assert.False(verdict.CanSend);
        Assert.Contains(ReportSendRefusal.ReplyToInvalid, verdict.Refusals);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    public void ARecipientThatCannotReceiveMailIsRefused(string recipient)
        => Assert.Contains(ReportSendRefusal.RecipientInvalid, Check(recipient: recipient).Refusals);

    /// <summary>
    /// The From name is the first thing a client reads. An empty one sends a document that appears to
    /// come from nobody.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AMissingFromNameIsRefused(string from)
    {
        ReportSendVerdict verdict = Check(from: from);

        Assert.False(verdict.CanSend);
        Assert.Contains(ReportSendRefusal.FromNameMissing, verdict.Refusals);
    }

    /// <summary>
    /// The envelope being right does not make the document right. An unnamed project still stops the
    /// send, through the readiness rule rather than a second copy of it here.
    /// </summary>
    [Fact]
    public void AnUnreadyDocumentIsStillRefused_ThroughTheReadinessRule()
    {
        ReportSendVerdict verdict = Check(draft: Draft(clientName: string.Empty));

        Assert.False(verdict.CanSend);
        Assert.Contains(ReportSendRefusal.DocumentNotReady, verdict.Refusals);
        Assert.True(verdict.Readiness.ProjectClientNameMissing);
    }

    [Fact]
    public void AnUnlabelledLineIsStillRefused()
    {
        ReportSendVerdict verdict = Check(draft: Draft(label: string.Empty));

        Assert.False(verdict.CanSend);
        Assert.Contains(ReportSendRefusal.DocumentNotReady, verdict.Refusals);
        Assert.NotEmpty(verdict.Readiness.TaskIdsMissingLabel);
    }

    /// <summary>
    /// Every reason at once, so the confirm screen can list them all rather than making the user fix
    /// one, press send, and discover the next.
    /// </summary>
    [Fact]
    public void EveryReasonIsReported_NotJustTheFirst()
    {
        ReportSendVerdict verdict = ReportSendPreflight.Evaluate(
            Draft(clientName: string.Empty), null,
            fromName: "  ",
            replyTo: "displaced-x@porta.internal",
            recipientEmail: "nope");

        Assert.False(verdict.CanSend);
        Assert.Contains(ReportSendRefusal.DocumentNotReady, verdict.Refusals);
        Assert.Contains(ReportSendRefusal.FromNameMissing, verdict.Refusals);
        Assert.Contains(ReportSendRefusal.ReplyToIsSynthetic, verdict.Refusals);
        Assert.Contains(ReportSendRefusal.RecipientInvalid, verdict.Refusals);
    }

    /// <summary>
    /// §6 — `reports@`, not `ari@`, so a spam mark on the nightly digest cannot travel to client
    /// reports or the other way round. Asserted because it is a constant someone could "tidy".
    /// </summary>
    [Fact]
    public void TheFromAddressIsTheProductsReportsMailbox()
    {
        Assert.Equal("reports@useporta.com", ReportSendPreflight.FromAddress);
        Assert.DoesNotContain("ari@", ReportSendPreflight.FromAddress, StringComparison.Ordinal);
    }
}
