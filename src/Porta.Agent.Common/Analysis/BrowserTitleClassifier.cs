namespace Porta.Agent.Common.Analysis;

/// <summary>
/// Classifies a browser window/tab TITLE into a coarse <see cref="AppCategory"/> plus a stable,
/// coarse "site token" (e.g. "youtube", "github"), so browser time can be split into work vs.
/// distraction instead of all collapsing to <see cref="AppCategory.Unknown"/>.
///
/// PRIVACY: the raw title is only ever passed in transiently and is never returned or stored.
/// The output is a coarse token + category enum. Callers hash the token (never store it raw),
/// so nothing identifying about the page survives. Titles are read in memory and discarded.
///
/// This is a deliberately CONSERVATIVE, title-keyword heuristic:
///  - Browser titles usually brand the platform (e.g. "… - YouTube", "… - Stack Overflow",
///    "… | LinkedIn", "… - Notion"); we match those high-confidence markers.
///  - Browser titles do NOT contain the URL/domain (Chrome/Edge/Firefox don't put it in the
///    title), so pages that don't brand their title are left as Unknown → treated as NEUTRAL
///    by the distraction logic. We never *guess* "distraction" from an unrecognized title.
/// The keyword table is expected to be tuned against real usage over time.
/// </summary>
public static class BrowserTitleClassifier
{
    /// <summary>Token used when a browser title can't be confidently classified.</summary>
    public const string UnknownSiteToken = "web";

    private static readonly HashSet<string> BrowserExeNames = new(StringComparer.Ordinal)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "operagx",
        "vivaldi", "arc", "chromium", "iexplore", "duckduckgo", "librewolf",
        "waterfox", "thorium", "torbrowser", "tor browser", "ungoogled-chromium",
    };

    /// <summary>True if the given exe/process base name (lowercased, no extension) is a browser.</summary>
    public static bool IsBrowserExe(string? exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName))
        {
            return false;
        }

        return BrowserExeNames.Contains(exeName.Trim().ToLowerInvariant());
    }

    // Ordered rules: (titleKeyword, siteToken, category). First substring match wins, so the
    // platform you are ON (e.g. YouTube) takes priority over a topic mentioned in the title.
    // Keep keywords distinctive enough to avoid false matches; unrecognized => Unknown (neutral).
    private static readonly (string Keyword, string Token, AppCategory Category)[] SiteRules =
    {
        // ── Entertainment ───────────────────────────────────────────────
        ("youtube",         "youtube",      AppCategory.Entertainment),
        ("netflix",         "netflix",      AppCategory.Entertainment),
        ("twitch",          "twitch",       AppCategory.Entertainment),
        ("disney+",         "disney",       AppCategory.Entertainment),
        ("hulu",            "hulu",         AppCategory.Entertainment),
        ("hbo max",         "hbomax",       AppCategory.Entertainment),
        ("prime video",     "primevideo",   AppCategory.Entertainment),
        ("soundcloud",      "soundcloud",   AppCategory.Entertainment),

        // ── Social media ────────────────────────────────────────────────
        // NB: x.com titles are often just "… / X" — too short/ambiguous to match safely by
        // substring (would false-match "… / xml" etc.), so X is left to fall back to neutral.
        ("twitter",         "twitter",      AppCategory.SocialMedia),
        ("instagram",       "instagram",    AppCategory.SocialMedia),
        ("facebook",        "facebook",     AppCategory.SocialMedia),
        ("tiktok",          "tiktok",       AppCategory.SocialMedia),
        ("reddit",          "reddit",       AppCategory.SocialMedia),
        (" r/",             "reddit",       AppCategory.SocialMedia),   // "… : r/subreddit"
        ("threads",         "threads",      AppCategory.SocialMedia),
        ("snapchat",        "snapchat",     AppCategory.SocialMedia),
        ("pinterest",       "pinterest",    AppCategory.SocialMedia),

        // ── Communication ───────────────────────────────────────────────
        ("gmail",           "gmail",        AppCategory.Communication),
        ("outlook",         "outlook",      AppCategory.Communication),
        ("proton mail",     "protonmail",   AppCategory.Communication),
        ("slack",           "slack",        AppCategory.Communication),
        ("discord",         "discord",      AppCategory.Communication),
        ("whatsapp",        "whatsapp",     AppCategory.Communication),
        ("messenger",       "messenger",    AppCategory.Communication),
        ("telegram",        "telegram",     AppCategory.Communication),
        ("microsoft teams", "teams",        AppCategory.Communication),
        ("google chat",     "googlechat",   AppCategory.Communication),

        // ── Development ─────────────────────────────────────────────────
        ("github",          "github",       AppCategory.Development),
        ("gitlab",          "gitlab",       AppCategory.Development),
        ("bitbucket",       "bitbucket",    AppCategory.Development),
        ("stack overflow",  "stackoverflow",AppCategory.Development),
        ("localhost",       "localhost",    AppCategory.Development),
        ("127.0.0.1",       "localhost",    AppCategory.Development),
        ("jira",            "jira",         AppCategory.Development),
        ("confluence",      "confluence",   AppCategory.Development),
        ("vercel",          "vercel",       AppCategory.Development),
        ("netlify",         "netlify",      AppCategory.Development),
        ("mdn web docs",    "mdn",          AppCategory.Development),
        ("codepen",         "codepen",      AppCategory.Development),
        ("codesandbox",     "codesandbox",  AppCategory.Development),
        ("replit",          "replit",       AppCategory.Development),
        ("hugging face",    "huggingface",  AppCategory.Development),

        // ── Productivity ────────────────────────────────────────────────
        ("google docs",     "googledocs",   AppCategory.Productivity),
        ("google sheets",   "googlesheets", AppCategory.Productivity),
        ("google slides",   "googleslides", AppCategory.Productivity),
        ("notion",          "notion",       AppCategory.Productivity),
        ("figma",           "figma",        AppCategory.Productivity),
        ("linear",          "linear",       AppCategory.Productivity),
        ("trello",          "trello",       AppCategory.Productivity),
        ("asana",           "asana",        AppCategory.Productivity),
        ("clickup",         "clickup",      AppCategory.Productivity),
        ("airtable",        "airtable",     AppCategory.Productivity),
        ("miro",            "miro",         AppCategory.Productivity),
        ("canva",           "canva",        AppCategory.Productivity),
        ("overleaf",        "overleaf",     AppCategory.Productivity),
        ("linkedin",        "linkedin",     AppCategory.Productivity),  // matches exe classifier
        ("chatgpt",         "chatgpt",      AppCategory.Productivity),
        ("perplexity",      "perplexity",   AppCategory.Productivity),
    };

    /// <summary>
    /// Classifies a raw browser title into a coarse (siteToken, category). Returns
    /// (<see cref="UnknownSiteToken"/>, <see cref="AppCategory.Unknown"/>) when nothing matches —
    /// the caller's distraction logic then treats Unknown as neutral, never as distraction.
    /// The raw title is not retained.
    /// </summary>
    public static (string SiteToken, AppCategory Category) ClassifyTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return (UnknownSiteToken, AppCategory.Unknown);
        }

        string lower = title.ToLowerInvariant();
        foreach ((string keyword, string token, AppCategory category) in SiteRules)
        {
            if (lower.Contains(keyword, StringComparison.Ordinal))
            {
                return (token, category);
            }
        }

        return (UnknownSiteToken, AppCategory.Unknown);
    }
}
