using Porta.UserAgent.Interventions;

namespace Porta.UserAgent.Tests;

public sealed class UserInterventionPreferenceStoreTests
{
    [Fact]
    public void SuppressTip_PersistsAcrossStoreInstances()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"porta-tip-prefs-{Guid.NewGuid():N}.json");

        try
        {
            var first = new UserInterventionPreferenceStore(filePath);
            Assert.False(first.IsTipSuppressed("break_suggestion"));

            Assert.True(first.TrySuppressTip("break_suggestion", DateTimeOffset.Parse("2026-03-29T10:00:00Z")));

            var second = new UserInterventionPreferenceStore(filePath);
            Assert.True(second.IsTipSuppressed("break_suggestion"));
            Assert.False(second.IsTipSuppressed("focus_lock_in_suggestion"));
        }
        finally
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void SnoozeTip_PersistsAcrossStoreInstances()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"porta-tip-snooze-{Guid.NewGuid():N}.json");
        DateTimeOffset nowUtc = DateTimeOffset.Parse("2026-03-29T10:00:00Z");
        DateTimeOffset snoozeUntilUtc = nowUtc.AddMinutes(30);

        try
        {
            var first = new UserInterventionPreferenceStore(filePath);
            Assert.False(first.IsTipSnoozed("break_suggestion", nowUtc));

            Assert.True(first.TrySnoozeTip("break_suggestion", snoozeUntilUtc, nowUtc));

            var second = new UserInterventionPreferenceStore(filePath);
            Assert.True(second.IsTipSnoozed("break_suggestion", nowUtc.AddMinutes(10)));
            Assert.False(second.IsTipSnoozed("break_suggestion", nowUtc.AddMinutes(31)));
        }
        finally
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
            }
        }
    }
}
