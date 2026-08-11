using System.Text.Json;
using Porta.Agent.Common.Analysis;
using Porta.UserAgent.Input;

namespace Porta.UserAgent.Tests;

// Complaint 7 (2026-08-11) - the label map was being TRUNCATED on every user-agent restart.
//
// The AppCategoryClassifier registry is per-process and starts EMPTY. The user agent - the process
// that writes this file - never seeded itself from it (CategoryRegistrySeeder exists, but only the
// SERVICE calls it), and Persist() serialized that partial registry over the top of the file. Every
// restart, including every watchdog relaunch, destroyed every label learned before it.
//
// Measured on the founder's machine 2026-08-11: 5 labels for the whole machine, while 2026-08-10
// alone had 19 distinct foreground identities, 14 of them unnamed. The classifier was working - its
// output was being erased.
public sealed class FocusAppLabelStoreMergeTests : IDisposable
{
    private readonly string _dir;

    public FocusAppLabelStoreMergeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "porta-label-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        FocusAppLabelStore.DirectoryOverrideForTests = _dir;
    }

    public void Dispose()
    {
        FocusAppLabelStore.DirectoryOverrideForTests = null;
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private string LabelsPath => Path.Combine(_dir, FocusAppLabelStore.LabelsFileName);

    private Dictionary<string, string> ReadLabels()
        => File.Exists(LabelsPath)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LabelsPath)) ?? new()
            : new();

    [Fact]
    public void APersistDoesNotDeleteLabelsLearnedBeforeARestart()
    {
        // What an earlier session had already learned, before the process restarted.
        File.WriteAllText(LabelsPath, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["restart_survivor_a"] = "cursor",
            ["restart_survivor_b"] = "notion"
        }));

        // This session has only seen one thing so far - the state that used to truncate the file.
        string fresh = "merge_test_fresh_" + Guid.NewGuid().ToString("N");
        AppCategoryClassifier.RegisterHashLabel(fresh, "figma");
        FocusAppLabelStore.Persist();

        Dictionary<string, string> after = ReadLabels();
        Assert.Equal("cursor", after["restart_survivor_a"]);
        Assert.Equal("notion", after["restart_survivor_b"]);
        Assert.Equal("figma", after[fresh]);
    }

    [Fact]
    public void AReseenIdentityTakesTheFresherClassification()
    {
        string reclassified = "merge_test_reclassified_" + Guid.NewGuid().ToString("N");
        File.WriteAllText(LabelsPath, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [reclassified] = "web"
        }));

        AppCategoryClassifier.RegisterHashLabel(reclassified, "googledocs");
        FocusAppLabelStore.Persist();

        Assert.Equal("googledocs", ReadLabels()[reclassified]);
    }

    [Fact]
    public void ACorruptExistingFileDoesNotLoseThisSessionsLabels()
    {
        File.WriteAllText(LabelsPath, "{ this is not json");

        string fresh = "merge_test_corrupt_" + Guid.NewGuid().ToString("N");
        AppCategoryClassifier.RegisterHashLabel(fresh, "linear");
        FocusAppLabelStore.Persist();

        Assert.Equal("linear", ReadLabels()[fresh]);
    }
}
