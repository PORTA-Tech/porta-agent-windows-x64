using System.IO;

namespace Porta.ControlCenter.Tests;

public sealed class ControlCenterShellContentTests
{
    [Fact]
    public void MainWindowXaml_UsesRenamedTopLevelNavigation()
    {
        string xaml = LoadMainWindowXaml();

        Assert.Contains("x:Name=\"OverviewNavButton\" Content=\"Today\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TimelineNavButton\" Content=\"History\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"InterventionsNavButton\" Content=\"Actions\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"InputsNavButton\" Content=\"Tracking\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SettingsNavButton\" Content=\"Settings\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DiagnosticsNavButton\" Content=\"System\"", xaml, StringComparison.Ordinal);

        Assert.DoesNotContain("Content=\"What I Track\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Controls\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Health\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TodaySection_UsesUserFacingCardsAndKeepsDiagnosticsOut()
    {
        string xaml = LoadMainWindowXaml();
        string todaySection = SliceBetween(
            xaml,
            "<ScrollViewer x:Name=\"OverviewPanel\"",
            "<ScrollViewer x:Name=\"InputsPanel\"");

        Assert.Contains("Text=\"Focused today\"", todaySection, StringComparison.Ordinal);
        Assert.Contains("Text=\"Current focus session\"", todaySection, StringComparison.Ordinal);
        Assert.Contains("Text=\"Distraction time today\"", todaySection, StringComparison.Ordinal);
        Assert.Contains("Text=\"Current status\"", todaySection, StringComparison.Ordinal);

        Assert.DoesNotContain("Text=\"Engine health\"", todaySection, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Capture health\"", todaySection, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Keyboard signal\"", todaySection, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Mouse signal\"", todaySection, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Idle fragmentation\"", todaySection, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Fatigue\"", todaySection, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Stability\"", todaySection, StringComparison.Ordinal);
        Assert.DoesNotContain("processed history", todaySection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("live session activity", todaySection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw idle", todaySection, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SystemSection_HoldsDiagnosticsAndAdvancedReasoning()
    {
        string xaml = LoadMainWindowXaml();
        string systemSection = SliceBetween(
            xaml,
            "<ScrollViewer x:Name=\"DiagnosticsPanel\"",
            "<Grid x:Name=\"WhyDrawerBackdrop\"");

        Assert.Contains("Text=\"System\"", systemSection, StringComparison.Ordinal);
        Assert.Contains("Text=\"Engine health\"", systemSection, StringComparison.Ordinal);
        Assert.Contains("Text=\"Session truth\"", systemSection, StringComparison.Ordinal);
        Assert.Contains("Text=\"Input truth\"", systemSection, StringComparison.Ordinal);
        Assert.Contains("Text=\"Advanced\"", systemSection, StringComparison.Ordinal);
    }

    [Fact]
    public void HistorySection_UsesHumanReadableInsightLabels()
    {
        string xaml = LoadMainWindowXaml();
        string historySection = SliceBetween(
            xaml,
            "<ScrollViewer x:Name=\"TimelinePanel\"",
            "<ScrollViewer x:Name=\"AccountPanel\"");

        Assert.Contains("Text=\"Focused in this range\"", historySection, StringComparison.Ordinal);
        Assert.Contains("Text=\"Longest focus block\"", historySection, StringComparison.Ordinal);
        Assert.Contains("Text=\"Distraction in this range\"", historySection, StringComparison.Ordinal);
        Assert.Contains("Text=\"Pattern + actions\"", historySection, StringComparison.Ordinal);
        Assert.Contains("Text=\"Action markers in this range\"", historySection, StringComparison.Ordinal);
        Assert.Contains("Content=\"Advanced\"", historySection, StringComparison.Ordinal);

        Assert.DoesNotContain("Content=\"Technical view\"", historySection, StringComparison.Ordinal);
        Assert.DoesNotContain("No history chart data yet.", historySection, StringComparison.Ordinal);
    }

    private static string LoadMainWindowXaml()
    {
        string? current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            string candidate = Path.Combine(current, "src", "Porta.ControlCenter", "MainWindow.xaml");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException("Could not find src/Porta.ControlCenter/MainWindow.xaml from the test output directory.");
    }

    private static string SliceBetween(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find start marker '{startMarker}'.");

        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find end marker '{endMarker}' after '{startMarker}'.");

        return source[start..end];
    }
}
