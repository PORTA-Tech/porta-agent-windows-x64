using System.Collections.Generic;
using Porta.Agent.Common.Analysis;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// 2d: the server category overlay fills only the gaps the local curated rules leave as Unknown,
/// and never overrides a curated result. These cover the overlay's public surface on
/// AppCategoryClassifier.
/// </summary>
public sealed class AppCategoryOverlayTests
{
    [Fact]
    public void OverlayCategory_EmptyOverlay_ReturnsUnknown()
    {
        AppCategoryClassifier.ApplyServerCategoryOverlay(new Dictionary<string, AppCategory>());

        Assert.Equal(AppCategory.Unknown, AppCategoryClassifier.OverlayCategoryForToken("obsidian"));
    }

    [Fact]
    public void OverlayCategory_KnownToken_ReturnsServerCategory()
    {
        AppCategoryClassifier.ApplyServerCategoryOverlay(new Dictionary<string, AppCategory>
        {
            ["obsidian"] = AppCategory.Productivity,
            ["linear"] = AppCategory.Development
        });

        Assert.Equal(AppCategory.Productivity, AppCategoryClassifier.OverlayCategoryForToken("obsidian"));
        Assert.Equal(AppCategory.Development, AppCategoryClassifier.OverlayCategoryForToken("linear"));
    }

    [Theory]
    [InlineData("OBSIDIAN")]
    [InlineData("  Obsidian  ")]
    public void OverlayCategory_LookupIsCaseInsensitiveAndTrimmed(string lookup)
    {
        AppCategoryClassifier.ApplyServerCategoryOverlay(new Dictionary<string, AppCategory>
        {
            ["obsidian"] = AppCategory.Productivity
        });

        Assert.Equal(AppCategory.Productivity, AppCategoryClassifier.OverlayCategoryForToken(lookup));
    }

    [Fact]
    public void ApplyOverlay_IgnoresBlankKeysAndUnknownValues()
    {
        AppCategoryClassifier.ApplyServerCategoryOverlay(new Dictionary<string, AppCategory>
        {
            ["figma"] = AppCategory.Productivity,
            ["   "] = AppCategory.Development,
            ["ghosttoken"] = AppCategory.Unknown
        });

        Assert.Equal(AppCategory.Productivity, AppCategoryClassifier.OverlayCategoryForToken("figma"));
        // An Unknown-valued entry is not stored (it would be indistinguishable from "no answer").
        Assert.Equal(AppCategory.Unknown, AppCategoryClassifier.OverlayCategoryForToken("ghosttoken"));
    }

    [Fact]
    public void ApplyOverlay_ReplacesPreviousContents()
    {
        AppCategoryClassifier.ApplyServerCategoryOverlay(new Dictionary<string, AppCategory>
        {
            ["notion"] = AppCategory.Productivity
        });
        AppCategoryClassifier.ApplyServerCategoryOverlay(new Dictionary<string, AppCategory>
        {
            ["slack"] = AppCategory.Communication
        });

        // The re-applied overlay fully replaces the old one — the earlier token is gone.
        Assert.Equal(AppCategory.Unknown, AppCategoryClassifier.OverlayCategoryForToken("notion"));
        Assert.Equal(AppCategory.Communication, AppCategoryClassifier.OverlayCategoryForToken("slack"));
    }

    [Fact]
    public void ApplyOverlay_Null_ClearsOverlay()
    {
        AppCategoryClassifier.ApplyServerCategoryOverlay(new Dictionary<string, AppCategory>
        {
            ["notion"] = AppCategory.Productivity
        });
        AppCategoryClassifier.ApplyServerCategoryOverlay(null);

        Assert.Equal(AppCategory.Unknown, AppCategoryClassifier.OverlayCategoryForToken("notion"));
    }

    [Fact]
    public void OverlayDoesNotAlterLocalCuratedClassification()
    {
        // Overlay claims a category for a token; the local curated classifier is independent and
        // must be unaffected — curated always wins because the call site only consults the overlay
        // when ClassifyStableIdentifier returns Unknown.
        AppCategoryClassifier.ApplyServerCategoryOverlay(new Dictionary<string, AppCategory>
        {
            ["code"] = AppCategory.Entertainment
        });

        Assert.Equal(
            AppCategory.Development,
            AppCategoryClassifier.ClassifyStableIdentifier("exe:C:\\Users\\me\\AppData\\Local\\Programs\\Microsoft VS Code\\Code.exe"));
    }
}
