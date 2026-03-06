namespace Porta.Agent.Core.Tests;

public sealed class Step17VisualGuardrailsTests
{
    [Fact]
    public void GuardrailsDoc_ListsForbiddenOsSideEffects()
    {
        string repoRoot = ResolveRepoRoot();
        string docPath = Path.Combine(repoRoot, "docs", "STEP17_VISUAL_GUARDRAILS.md");
        Assert.True(File.Exists(docPath), "STEP17_VISUAL_GUARDRAILS.md must exist.");

        string doc = File.ReadAllText(docPath);
        Assert.Contains("Registry theme changes", doc, StringComparison.Ordinal);
        Assert.Contains("Windows theme switching", doc, StringComparison.Ordinal);
        Assert.Contains("Input blocking", doc, StringComparison.Ordinal);
        Assert.Contains("App blocking", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualModules_DoNotContainForbiddenSideEffectCalls()
    {
        string repoRoot = ResolveRepoRoot();
        string[] sources =
        [
            Path.Combine(repoRoot, "src", "Porta.Agent.Runtime", "Layer1", "Layer1TrayController.cs"),
            Path.Combine(repoRoot, "src", "Porta.ControlCenter", "VisualStateAdapter.cs")
        ];

        string[] forbiddenTokens =
        [
            "BlockInput(",
            "SendInput(",
            "SetWindowTheme(",
            "Registry.",
            "SetValue(",
            "FlashWindowEx(",
            "SetForegroundWindow("
        ];

        foreach (string sourcePath in sources)
        {
            Assert.True(File.Exists(sourcePath), $"Expected source file not found: {sourcePath}");
            string text = File.ReadAllText(sourcePath);

            foreach (string token in forbiddenTokens)
            {
                Assert.DoesNotContain(token, text, StringComparison.Ordinal);
            }
        }
    }

    private static string ResolveRepoRoot()
    {
        string? current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(Path.Combine(current, "src"))
                && Directory.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            current = parent?.FullName;
        }

        throw new InvalidOperationException("Unable to resolve repository root from test base directory.");
    }
}
