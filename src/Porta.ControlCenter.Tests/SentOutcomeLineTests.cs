using Porta.Agent.LocalApi;
using Porta.ControlCenter;

namespace Porta.ControlCenter.Tests;

// Chat 7 (2026-08-24) — the sent-reports list showed the period covered and the word "delivered",
// and never WHEN. sent_at_utc has been written, read back and put on the wire the whole time; the
// page simply never rendered it, so three sends of the same week were indistinguishable.
//
// The Reports history list has no preview-harness seam, so this is where it is checked.
public sealed class SentOutcomeLineTests
{
    private static LocalApiSentReport Report(string sentAtUtc, string sendState = "sent")
        => new(
            ReportId: "r1", ClientName: "Northbridge",
            RangeStartLocal: "2026-08-18", RangeEndLocal: "2026-08-24",
            RecipientName: "Sam", RecipientEmail: "sam@example.test",
            FromName: "Yadin", ReplyTo: "yadin@example.test",
            TotalHours: 6.5, RequestedAtUtc: sentAtUtc, SentAtUtc: sentAtUtc,
            SendState: sendState, SendError: "", ServerState: "ok",
            Lines: System.Array.Empty<LocalApiSentReportLine>());

    [Fact]
    public void ADeliveredReportSaysWhen()
    {
        string line = MainWindow.SentOutcomeLine(Report("2026-08-20T09:37:27.963Z"));

        Assert.StartsWith("delivered · ", line, StringComparison.Ordinal);
        Assert.Contains("20 Aug", line, StringComparison.Ordinal);
    }

    // The locked rule: an unknown outcome is never shown as a failure. It still is not — it just
    // says when it happened too.
    [Fact]
    public void AnUnknownOutcomeIsStillNotAFailure()
    {
        string line = MainWindow.SentOutcomeLine(Report("2026-08-20T09:37:27.963Z", sendState: "unknown"));

        Assert.StartsWith("outcome unknown", line, StringComparison.Ordinal);
        Assert.DoesNotContain("fail", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("error", line, StringComparison.OrdinalIgnoreCase);
    }

    // A timestamp that cannot be read must not make a delivered report look doubtful. It degrades
    // to the outcome alone rather than inventing a date or saying "unknown time".
    [Fact]
    public void AnUnreadableTimestampDegradesToTheOutcomeAlone()
    {
        Assert.Equal("delivered", MainWindow.SentOutcomeLine(Report("")));
        Assert.Equal("delivered", MainWindow.SentOutcomeLine(Report("not a date")));
    }

    // Rendered in the user's own local time, because the question it answers is "was that the one
    // I sent before lunch?".
    [Fact]
    public void TheTimeIsShownLocal_NotUtc()
    {
        var utc = new DateTimeOffset(2026, 08, 20, 23, 30, 0, TimeSpan.Zero);
        string line = MainWindow.SentOutcomeLine(Report(utc.ToString("O")));

        DateTimeOffset local = utc.ToLocalTime();
        Assert.Contains($"{local:d MMM}", line, StringComparison.Ordinal);
    }
}
