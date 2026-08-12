using System.Text.Json;

namespace Porta.Agent.Service.Tests;

/// <summary>
/// Step 9a (2026-08-12) — the promise that makes 9a shippable on its own: <b>no project name
/// leaves this machine.</b> Yadin's test for it is that he can tell 9a and 9b apart by looking at
/// the nightly email, so the payloads must be byte-for-byte what they were in 1.2.142.
///
/// These are deliberately written against the SERIALIZED shape rather than the record's property
/// list. Adding a project field for 9b will fail them, and that is the point: the field going out
/// is a live-data decision, and it must be made on purpose rather than arrive with a refactor.
/// </summary>
public sealed class Step9aNothingLeavesTheMachineTests
{
    // The whole task shape as of 1.2.142. Written out rather than derived, so a change has to be
    // made here too, by someone who has read this comment.
    private static readonly string[] DigestTaskFields =
    [
        "title", "plannedSeconds", "actualSeconds", "offtaskSeconds", "state", "deferredUntilDay"
    ];

    private static readonly string[] NarrativeTaskFields =
    [
        "title", "state", "plannedSeconds", "actualSeconds"
    ];

    [Fact]
    public void TheDigestTaskPayload_CarriesNoProjectField()
    {
        IReadOnlyList<string> actual = SerializedFieldsOf(
            "Porta.Agent.Service.Fleet.DigestTaskPayload",
            [("Pricing page", typeof(string)), (7200, typeof(int)), (4020d, typeof(double)),
             (480d, typeof(double)), ("done", typeof(string)), ("", typeof(string))]);

        Assert.Equal(DigestTaskFields, actual);
    }

    [Fact]
    public void TheNarrativeTaskPayload_CarriesNoProjectField()
    {
        IReadOnlyList<string> actual = SerializedFieldsOf(
            "Porta.Agent.Service.Fleet.NarrativeTaskPayload",
            [("Pricing page", typeof(string)), ("done", typeof(string)),
             (7200, typeof(int)), (4020d, typeof(double))]);

        Assert.Equal(NarrativeTaskFields, actual);
    }

    // Belt and braces: whatever the field list is, nothing in either payload may mention a project.
    [Theory]
    [InlineData("Porta.Agent.Service.Fleet.DigestTaskPayload")]
    [InlineData("Porta.Agent.Service.Fleet.NarrativeTaskPayload")]
    [InlineData("Porta.Agent.Service.Fleet.DailyDigestReportPayload")]
    [InlineData("Porta.Agent.Service.Fleet.NarrativeSituationPayload")]
    public void NoOutboundPayloadTypeMentionsAProject(string typeName)
    {
        Type type = ResolveType(typeName);

        IEnumerable<string> names = type
            .GetProperties()
            .Select(p => p.Name);

        Assert.DoesNotContain(names, n => n.Contains("Project", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> SerializedFieldsOf(
        string typeName,
        IReadOnlyList<(object Value, Type Type)> arguments)
    {
        Type type = ResolveType(typeName);
        object instance = Activator.CreateInstance(
            type,
            arguments.Select(a => a.Value).ToArray())!;

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(instance, type));
        return document.RootElement.EnumerateObject().Select(p => p.Name).ToList();
    }

    private static Type ResolveType(string typeName)
    {
        Type? type = typeof(Porta.Agent.Service.Fleet.DigestTimelineHourPayload).Assembly
            .GetType(typeName);
        Assert.NotNull(type);
        return type!;
    }
}
