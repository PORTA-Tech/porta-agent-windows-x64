using System.Reflection;
using System.Text.Json;

namespace Porta.Agent.Service.Tests;

/// <summary>
/// <b>The nightly digest and the narrative line stay project-free.</b> Narrower than the
/// <c>Step9aNothingLeavesTheMachineTests</c> it replaces (deleted 2026-08-13), and deliberately so.
///
/// That test held one decision open — whether project names leave the machine at all — and Yadin
/// made it on 2026-08-13: Ari emails a work report to a paying client, and the server holds it. So
/// the blanket rule is gone. What survives it is a smaller, still-true rule:
///
/// <list type="bullet">
///   <item>The <b>report</b> carries a project name. That is the feature.</item>
///   <item>The <b>digest</b> and the <b>narrative</b> do not. The digest is an email the user sends
///   themselves; a project name there buys nothing and was never asked for
///   (<c>Step10_Report_Spec.md</c> §12.5).</item>
/// </list>
///
/// The failure this exists to catch is drift, not a decision: a project field added to a shared
/// payload record for the report's sake, quietly riding along on tonight's email. Nothing about the
/// report payload is constrained here — it is a different root, and it must be free to carry
/// whatever §12 approves.
/// </summary>
public sealed class DigestStaysProjectFreeTests
{
    // The two outbound roots under guard. Anything reachable from these is on tonight's wire.
    private const string DigestRoot = "Porta.Agent.Service.Fleet.DailyDigestReportPayload";
    private const string NarrativeRoot = "Porta.Agent.Service.Fleet.NarrativeRequestPayload";

    // The task shape as of 1.2.149. Written out rather than derived, so a change has to be made
    // here too, by someone who has read the class comment above.
    private static readonly string[] DigestTaskFields =
    [
        "title", "plannedSeconds", "actualSeconds", "offtaskSeconds", "state", "deferredUntilDay"
    ];

    private static readonly string[] NarrativeTaskFields =
    [
        "title", "state", "plannedSeconds", "actualSeconds"
    ];

    [Fact]
    public void NothingReachableFromTheDigestOrTheNarrativeMentionsAProject()
    {
        IReadOnlyCollection<Type> reachable = ReachableFrom(DigestRoot, NarrativeRoot);

        List<string> offenders =
        [
            .. from type in reachable
               from property in type.GetProperties()
               where property.Name.Contains("Project", StringComparison.OrdinalIgnoreCase)
               select $"{type.Name}.{property.Name}"
        ];

        Assert.True(
            offenders.Count == 0,
            "A project reached an outbound digest/narrative payload: " + string.Join(", ", offenders) +
            ". The report carries project names; the nightly email does not (Step10_Report_Spec.md §12.5).");
    }

    /// <summary>
    /// The guard above is only worth anything if the walk actually descends into the nested
    /// payloads. A walk that silently reached nothing but its two roots would pass forever and
    /// assert nothing, which is the failure mode that makes a test worse than no test.
    /// </summary>
    [Fact]
    public void TheWalkReachesTheNestedPayloads_OrItIsAssertingNothing()
    {
        IReadOnlyCollection<string> reachable = ReachableFrom(DigestRoot, NarrativeRoot)
            .Select(t => t.Name)
            .ToList();

        foreach (string expected in new[]
                 {
                     "DigestTaskPayload",
                     "DigestTimelineHourPayload",
                     "FocusAppUsagePayload",
                     "NarrativeSituationPayload",
                     "NarrativeTaskPayload"
                 })
        {
            Assert.Contains(expected, reachable);
        }
    }

    [Fact]
    public void TheDigestTaskPayload_SerializesExactlyTheseFields()
    {
        IReadOnlyList<string> actual = SerializedFieldsOf(
            "Porta.Agent.Service.Fleet.DigestTaskPayload",
            ["Pricing page", 7200, 4020d, 480d, "done", ""]);

        Assert.Equal(DigestTaskFields, actual);
    }

    [Fact]
    public void TheNarrativeTaskPayload_SerializesExactlyTheseFields()
    {
        IReadOnlyList<string> actual = SerializedFieldsOf(
            "Porta.Agent.Service.Fleet.NarrativeTaskPayload",
            ["Pricing page", "done", 7200, 4020d]);

        Assert.Equal(NarrativeTaskFields, actual);
    }

    /// <summary>
    /// Every payload type reachable from the given roots, following properties and the element
    /// types of collections. Types from other assemblies (string, double, the framework) are not
    /// followed — only this assembly's own contracts can gain a project field.
    /// </summary>
    private static IReadOnlyCollection<Type> ReachableFrom(params string[] rootTypeNames)
    {
        Assembly assembly = ContractAssembly;
        HashSet<Type> seen = [];
        Queue<Type> pending = new(rootTypeNames.Select(ResolveType));

        while (pending.Count > 0)
        {
            Type type = pending.Dequeue();
            if (!seen.Add(type))
            {
                continue;
            }

            foreach (PropertyInfo property in type.GetProperties())
            {
                foreach (Type candidate in Unwrap(property.PropertyType))
                {
                    if (candidate.Assembly == assembly && !seen.Contains(candidate))
                    {
                        pending.Enqueue(candidate);
                    }
                }
            }
        }

        return seen;
    }

    // IReadOnlyList<DigestTaskPayload> has to yield DigestTaskPayload, or the whole walk stops at
    // the first collection and the guard above becomes decorative.
    private static IEnumerable<Type> Unwrap(Type type)
    {
        Type actual = Nullable.GetUnderlyingType(type) ?? type;
        yield return actual;

        if (!actual.IsGenericType)
        {
            yield break;
        }

        foreach (Type argument in actual.GetGenericArguments())
        {
            foreach (Type inner in Unwrap(argument))
            {
                yield return inner;
            }
        }
    }

    private static IReadOnlyList<string> SerializedFieldsOf(string typeName, object[] arguments)
    {
        Type type = ResolveType(typeName);
        object instance = Activator.CreateInstance(type, arguments)!;

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(instance, type));
        return document.RootElement.EnumerateObject().Select(p => p.Name).ToList();
    }

    private static Assembly ContractAssembly =>
        typeof(Porta.Agent.Service.Fleet.DigestTimelineHourPayload).Assembly;

    private static Type ResolveType(string typeName)
    {
        Type? type = ContractAssembly.GetType(typeName);
        Assert.NotNull(type);
        return type!;
    }
}
