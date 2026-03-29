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
}
