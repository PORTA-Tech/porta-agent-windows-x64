using Porta.Agent.Common.Analysis;

namespace Porta.Agent.Core.Tests;

public sealed class BrowserTitleClassifierTests
{
    [Theory]
    [InlineData("Rick Astley - Never Gonna Give You Up - YouTube - Google Chrome", "youtube", AppCategory.Entertainment)]
    [InlineData("Inbox (5) - me@gmail.com - Gmail - Google Chrome", "gmail", AppCategory.Communication)]
    [InlineData("Fix bug · Pull Request #42 · owner/repo · GitHub - Google Chrome", "github", AppCategory.Development)]
    [InlineData("c# - How to parse JSON - Stack Overflow - Google Chrome", "stackoverflow", AppCategory.Development)]
    [InlineData("Cute cats : r/aww - Google Chrome", "reddit", AppCategory.SocialMedia)]
    [InlineData("photographer (@someone) • Instagram photos - Google Chrome", "instagram", AppCategory.SocialMedia)]
    [InlineData("Project Plan - Notion - Google Chrome", "notion", AppCategory.Productivity)]
    [InlineData("Untitled - Figma - Google Chrome", "figma", AppCategory.Productivity)]
    [InlineData("Slack | general | MyWorkspace", "slack", AppCategory.Communication)]
    [InlineData("(3) Feed | LinkedIn", "linkedin", AppCategory.Productivity)]
    [InlineData("localhost:3000/dashboard - Google Chrome", "localhost", AppCategory.Development)]
    [InlineData("Q3 Budget - Google Sheets - Google Chrome", "googlesheets", AppCategory.Productivity)]
    [InlineData("Discover Weekly - Spotify - Google Chrome", "spotify", AppCategory.Entertainment)]
    [InlineData("Play Chess Online - Chess.com", "chess", AppCategory.Gaming)]
    [InlineData("Zoom Meeting", "zoom", AppCategory.Communication)]
    [InlineData("Two Sum - LeetCode", "leetcode", AppCategory.Development)]
    [InlineData("Hacker News", "hackernews", AppCategory.Development)]
    [InlineData("Today - Todoist", "todoist", AppCategory.Productivity)]
    [InlineData("Claude", "claude", AppCategory.Productivity)]
    [InlineData("Dashboard - Binance", "binance", AppCategory.Productivity)]
    [InlineData("Why startups fail | by Author | Medium", "medium", AppCategory.Productivity)]
    [InlineData("Watch Movies & tv Shows Online free - 123Movies", "123movies", AppCategory.Entertainment)]
    [InlineData("npm | Home", "npm", AppCategory.Development)]
    [InlineData("Marketing Hub | HubSpot", "hubspot", AppCategory.Productivity)]
    public void ClassifyTitle_KnownSites_ReturnExpectedTokenAndCategory(
        string title, string expectedToken, AppCategory expectedCategory)
    {
        (string token, AppCategory category) = BrowserTitleClassifier.ClassifyTitle(title);
        Assert.Equal(expectedToken, token);
        Assert.Equal(expectedCategory, category);
    }

    [Theory]
    [InlineData("Some personal blog about gardening - Google Chrome")]
    [InlineData("Online Banking - Account Summary")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ClassifyTitle_Unrecognized_IsNeutralUnknown(string? title)
    {
        (string token, AppCategory category) = BrowserTitleClassifier.ClassifyTitle(title);
        Assert.Equal(BrowserTitleClassifier.UnknownSiteToken, token);
        Assert.Equal(AppCategory.Unknown, category);
    }

    // A2 — x.com brands its titles only with a trailing " / X". Before this, all X time fell to
    // "web" → pooled into "Other browsing" and never counted as a distraction. It resolves to the
    // "twitter" token so x.com and twitter.com aggregate as the one platform they are.
    [Theory]
    [InlineData("Home / X")]
    [InlineData("(3) Home / X")]
    [InlineData("Elon Musk (@elonmusk) / X")]
    [InlineData("X")]
    public void ClassifyTitle_XDotCom_IsRecognizedAsSocialMedia(string title)
    {
        (string token, AppCategory category) = BrowserTitleClassifier.ClassifyTitle(title);
        Assert.Equal("twitter", token);
        Assert.Equal(AppCategory.SocialMedia, category);
    }

    // The exact false-match the end-anchor exists to prevent: a title merely ENDING in "/ xml"
    // (or containing a stray "x") must never be mistaken for X.
    [Theory]
    [InlineData("Parsing config / xml")]
    [InlineData("Excel tips / xlsx")]
    [InlineData("Some blog about x - Google Chrome")]
    public void ClassifyTitle_XLookalikes_StayNeutralUnknown(string title)
    {
        (string token, AppCategory category) = BrowserTitleClassifier.ClassifyTitle(title);
        Assert.Equal(BrowserTitleClassifier.UnknownSiteToken, token);
        Assert.Equal(AppCategory.Unknown, category);
    }

    [Fact]
    public void ClassifyTitle_PlatformBeatsTopicMentionedInTitle()
    {
        // Watching a video *about* GitHub is YouTube time, not Development.
        (string token, AppCategory category) =
            BrowserTitleClassifier.ClassifyTitle("How to use GitHub - YouTube - Google Chrome");
        Assert.Equal("youtube", token);
        Assert.Equal(AppCategory.Entertainment, category);
    }

    [Theory]
    [InlineData("chrome", true)]
    [InlineData("CHROME", true)]
    [InlineData("msedge", true)]
    [InlineData("firefox", true)]
    [InlineData("brave", true)]
    [InlineData("cursor", false)]
    [InlineData("code", false)]
    [InlineData("explorer", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsBrowserExe_RecognizesBrowsersOnly(string? exe, bool expected)
    {
        Assert.Equal(expected, BrowserTitleClassifier.IsBrowserExe(exe));
    }

    // ── 2e: title → domain → web precedence ────────────────────────────────

    [Fact]
    public void Classify_CuratedTitleRule_BeatsResolvedDomain()
    {
        // A branded title always wins over the address-bar domain and keeps its real category.
        (string token, AppCategory category) =
            BrowserTitleClassifier.Classify("Fix bug · owner/repo · GitHub - Google Chrome", domainToken: "ycombinator");
        Assert.Equal("github", token);
        Assert.Equal(AppCategory.Development, category);
    }

    [Theory]
    [InlineData(null, "ycombinator", "ycombinator")]
    [InlineData("", "nytimes", "nytimes")]
    [InlineData("Quarterly review draft", "stackexchange", "stackexchange")]
    [InlineData("Untitled page", "123movies", "123movies")]
    public void Classify_UnbrandedTitle_FallsBackToDomainToken_AsUnknown(
        string? title, string domainToken, string expectedToken)
    {
        // No curated title match: use the resolved domain, category Unknown until 2d fills it in.
        (string token, AppCategory category) = BrowserTitleClassifier.Classify(title, domainToken);
        Assert.Equal(expectedToken, token);
        Assert.Equal(AppCategory.Unknown, category);
    }

    [Fact]
    public void Classify_NoTitleAndNoDomain_IsWeb()
    {
        (string token, AppCategory category) = BrowserTitleClassifier.Classify(title: null, domainToken: null);
        Assert.Equal(BrowserTitleClassifier.UnknownSiteToken, token);
        Assert.Equal(AppCategory.Unknown, category);
    }

    [Theory]
    [InlineData("https://news.ycombinator.com/item?id=123")] // a full URL
    [InlineData("ycombinator.com")]                          // suffix/dot not stripped
    [InlineData("ycombinator/item")]                         // path fragment
    [InlineData("ycombinator?id=123")]                       // query
    [InlineData("ycombinator#section")]                      // fragment
    [InlineData("news ycombinator")]                         // whitespace
    [InlineData("user:pass@host")]                           // credentials / colon
    public void Classify_MalformedDomainToken_NeverLeaks_CollapsesToWeb(string dirtyDomain)
    {
        // Hard privacy guarantee: even if the resolver ever handed us something URL-shaped, no
        // '/', '?', '#', ':', '.', or whitespace may survive into the identity — it becomes "web".
        (string token, AppCategory category) =
            BrowserTitleClassifier.Classify(title: null, domainToken: dirtyDomain);
        Assert.Equal(BrowserTitleClassifier.UnknownSiteToken, token);
        Assert.Equal(AppCategory.Unknown, category);
    }
}
