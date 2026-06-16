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
    // Curated, easy to extend. Tokens must be distinctive enough to avoid false matches
    // (no 2-3 letter or common-word tokens). First match wins, so the platform you are ON
    // takes priority over a topic in the title. Categories that drive distraction detection
    // (Entertainment / SocialMedia / Gaming) are intentionally thorough.
    private static readonly (string Keyword, string Token, AppCategory Category)[] SiteRules =
    {
        // ── Entertainment: video / streaming ────────────────────────────
        ("youtube",         "youtube",      AppCategory.Entertainment),
        ("netflix",         "netflix",      AppCategory.Entertainment),
        ("twitch",          "twitch",       AppCategory.Entertainment),
        ("disney+",         "disney",       AppCategory.Entertainment),
        ("hulu",            "hulu",         AppCategory.Entertainment),
        ("hbo max",         "hbomax",       AppCategory.Entertainment),
        ("prime video",     "primevideo",   AppCategory.Entertainment),
        ("paramount+",      "paramount",    AppCategory.Entertainment),
        ("peacock",         "peacock",      AppCategory.Entertainment),
        ("crunchyroll",     "crunchyroll",  AppCategory.Entertainment),
        ("funimation",      "funimation",   AppCategory.Entertainment),
        ("apple tv",        "appletv",      AppCategory.Entertainment),
        ("bilibili",        "bilibili",     AppCategory.Entertainment),
        ("vimeo",           "vimeo",        AppCategory.Entertainment),
        ("dailymotion",     "dailymotion",  AppCategory.Entertainment),
        ("rumble",          "rumble",       AppCategory.Entertainment),
        // ── Entertainment: music / audio ────────────────────────────────
        ("spotify",         "spotify",      AppCategory.Entertainment),
        ("soundcloud",      "soundcloud",   AppCategory.Entertainment),
        ("bandcamp",        "bandcamp",     AppCategory.Entertainment),
        ("mixcloud",        "mixcloud",     AppCategory.Entertainment),
        ("pandora",         "pandora",      AppCategory.Entertainment),
        ("deezer",          "deezer",       AppCategory.Entertainment),
        ("tidal",           "tidal",        AppCategory.Entertainment),
        // ── Entertainment: humor / images / media ───────────────────────
        ("9gag",            "9gag",         AppCategory.Entertainment),
        ("imgur",           "imgur",        AppCategory.Entertainment),
        ("giphy",           "giphy",        AppCategory.Entertainment),
        ("imdb",            "imdb",         AppCategory.Entertainment),
        ("letterboxd",      "letterboxd",   AppCategory.Entertainment),
        ("rotten tomatoes", "rottentomatoes", AppCategory.Entertainment),
        ("gamespot",        "gamespot",     AppCategory.Entertainment),
        ("kotaku",          "kotaku",       AppCategory.Entertainment),
        ("espn",            "espn",         AppCategory.Entertainment),
        ("bleacher report", "bleacherreport", AppCategory.Entertainment),

        // ── Gaming (browser games / launchers on the web) ───────────────
        ("chess.com",       "chess",        AppCategory.Gaming),
        ("lichess",         "lichess",      AppCategory.Gaming),
        ("roblox",          "roblox",       AppCategory.Gaming),
        ("miniclip",        "miniclip",     AppCategory.Gaming),
        ("crazygames",      "crazygames",   AppCategory.Gaming),
        ("kongregate",      "kongregate",   AppCategory.Gaming),
        ("newgrounds",      "newgrounds",   AppCategory.Gaming),
        ("coolmath",        "coolmath",     AppCategory.Gaming),
        ("poki",            "poki",         AppCategory.Gaming),
        ("steam community", "steam",        AppCategory.GamingLauncher),
        ("epic games",      "epicgames",    AppCategory.GamingLauncher),

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
        ("tumblr",          "tumblr",       AppCategory.SocialMedia),
        ("mastodon",        "mastodon",     AppCategory.SocialMedia),
        ("bluesky",         "bluesky",      AppCategory.SocialMedia),
        ("quora",           "quora",        AppCategory.SocialMedia),
        ("weibo",           "weibo",        AppCategory.SocialMedia),
        ("nextdoor",        "nextdoor",     AppCategory.SocialMedia),
        ("truth social",    "truthsocial",  AppCategory.SocialMedia),

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
        ("google meet",     "googlemeet",   AppCategory.Communication),
        ("zoom",            "zoom",         AppCategory.Communication),
        ("signal",          "signal",       AppCategory.Communication),
        ("skype",           "skype",        AppCategory.Communication),
        ("mattermost",      "mattermost",   AppCategory.Communication),
        ("zoho mail",       "zohomail",     AppCategory.Communication),
        ("fastmail",        "fastmail",     AppCategory.Communication),
        ("webex",           "webex",        AppCategory.Communication),
        ("tutanota",        "tutanota",     AppCategory.Communication),

        // ── Development ─────────────────────────────────────────────────
        ("github",          "github",       AppCategory.Development),
        ("gitlab",          "gitlab",       AppCategory.Development),
        ("bitbucket",       "bitbucket",    AppCategory.Development),
        ("stack overflow",  "stackoverflow",AppCategory.Development),
        ("stack exchange",  "stackexchange",AppCategory.Development),
        ("hacker news",     "hackernews",   AppCategory.Development),
        ("localhost",       "localhost",    AppCategory.Development),
        ("127.0.0.1",       "localhost",    AppCategory.Development),
        ("jira",            "jira",         AppCategory.Development),
        ("confluence",      "confluence",   AppCategory.Development),
        ("vercel",          "vercel",       AppCategory.Development),
        ("netlify",         "netlify",      AppCategory.Development),
        ("mdn web docs",    "mdn",          AppCategory.Development),
        ("developer.mozilla","mdn",         AppCategory.Development),
        ("codepen",         "codepen",      AppCategory.Development),
        ("codesandbox",     "codesandbox",  AppCategory.Development),
        ("stackblitz",      "stackblitz",   AppCategory.Development),
        ("replit",          "replit",       AppCategory.Development),
        ("glitch",          "glitch",       AppCategory.Development),
        ("gitpod",          "gitpod",       AppCategory.Development),
        ("hugging face",    "huggingface",  AppCategory.Development),
        ("npmjs",           "npm",          AppCategory.Development),
        ("pypi",            "pypi",         AppCategory.Development),
        ("docker hub",      "dockerhub",    AppCategory.Development),
        ("kubernetes",      "kubernetes",   AppCategory.Development),
        ("aws management console", "aws",   AppCategory.Development),
        ("amazon web services",    "aws",   AppCategory.Development),
        ("azure portal",    "azure",        AppCategory.Development),
        ("google cloud",    "gcp",          AppCategory.Development),
        ("cloudflare",      "cloudflare",   AppCategory.Development),
        ("digitalocean",    "digitalocean", AppCategory.Development),
        ("heroku",          "heroku",       AppCategory.Development),
        ("supabase",        "supabase",     AppCategory.Development),
        ("firebase",        "firebase",     AppCategory.Development),
        ("mongodb",         "mongodb",      AppCategory.Development),
        ("postman",         "postman",      AppCategory.Development),
        ("swagger",         "swagger",      AppCategory.Development),
        ("datadog",         "datadog",      AppCategory.Development),
        ("sentry",          "sentry",       AppCategory.Development),
        ("grafana",         "grafana",      AppCategory.Development),
        ("jenkins",         "jenkins",      AppCategory.Development),
        ("circleci",        "circleci",     AppCategory.Development),
        ("dev.to",          "devto",        AppCategory.Development),
        ("freecodecamp",    "freecodecamp", AppCategory.Development),
        ("leetcode",        "leetcode",     AppCategory.Development),
        ("hackerrank",      "hackerrank",   AppCategory.Development),
        ("codewars",        "codewars",     AppCategory.Development),
        ("kaggle",          "kaggle",       AppCategory.Development),
        ("geeksforgeeks",   "geeksforgeeks",AppCategory.Development),
        ("w3schools",       "w3schools",    AppCategory.Development),
        ("readthedocs",     "readthedocs",  AppCategory.Development),
        ("devdocs",         "devdocs",      AppCategory.Development),
        ("caniuse",         "caniuse",      AppCategory.Development),
        ("regex101",        "regex101",     AppCategory.Development),
        ("jsfiddle",        "jsfiddle",     AppCategory.Development),
        ("google colab",    "colab",        AppCategory.Development),
        ("jupyter",         "jupyter",      AppCategory.Development),

        // ── Productivity ────────────────────────────────────────────────
        ("google docs",     "googledocs",   AppCategory.Productivity),
        ("google sheets",   "googlesheets", AppCategory.Productivity),
        ("google slides",   "googleslides", AppCategory.Productivity),
        ("google drive",    "googledrive",  AppCategory.Productivity),
        ("google calendar", "googlecalendar", AppCategory.Productivity),
        ("google forms",    "googleforms",  AppCategory.Productivity),
        ("google keep",     "googlekeep",   AppCategory.Productivity),
        ("notion",          "notion",       AppCategory.Productivity),
        ("figma",           "figma",        AppCategory.Productivity),
        ("figjam",          "figjam",       AppCategory.Productivity),
        ("linear",          "linear",       AppCategory.Productivity),
        ("trello",          "trello",       AppCategory.Productivity),
        ("asana",           "asana",        AppCategory.Productivity),
        ("clickup",         "clickup",      AppCategory.Productivity),
        ("airtable",        "airtable",     AppCategory.Productivity),
        ("miro",            "miro",         AppCategory.Productivity),
        ("canva",           "canva",        AppCategory.Productivity),
        ("overleaf",        "overleaf",     AppCategory.Productivity),
        ("monday.com",      "monday",       AppCategory.Productivity),
        ("basecamp",        "basecamp",     AppCategory.Productivity),
        ("smartsheet",      "smartsheet",   AppCategory.Productivity),
        ("wrike",           "wrike",        AppCategory.Productivity),
        ("todoist",         "todoist",      AppCategory.Productivity),
        ("ticktick",        "ticktick",     AppCategory.Productivity),
        ("evernote",        "evernote",     AppCategory.Productivity),
        ("onenote",         "onenote",      AppCategory.Productivity),
        ("sharepoint",      "sharepoint",   AppCategory.Productivity),
        ("onedrive",        "onedrive",     AppCategory.Productivity),
        ("dropbox",         "dropbox",      AppCategory.Productivity),
        ("typeform",        "typeform",     AppCategory.Productivity),
        ("calendly",        "calendly",     AppCategory.Productivity),
        ("grammarly",       "grammarly",    AppCategory.Productivity),
        ("docusign",        "docusign",     AppCategory.Productivity),
        ("lucidchart",      "lucidchart",   AppCategory.Productivity),
        ("lucidspark",      "lucidspark",   AppCategory.Productivity),
        ("whimsical",       "whimsical",    AppCategory.Productivity),
        ("excalidraw",      "excalidraw",   AppCategory.Productivity),
        ("draw.io",         "drawio",       AppCategory.Productivity),
        ("diagrams.net",    "drawio",       AppCategory.Productivity),
        ("mural",           "mural",        AppCategory.Productivity),
        ("framer",          "framer",       AppCategory.Productivity),
        ("webflow",         "webflow",      AppCategory.Productivity),
        ("squarespace",     "squarespace",  AppCategory.Productivity),
        ("shopify",         "shopify",      AppCategory.Productivity),
        ("quickbooks",      "quickbooks",   AppCategory.Productivity),
        ("google translate","googletranslate", AppCategory.Productivity),
        ("deepl",           "deepl",        AppCategory.Productivity),
        ("linkedin",        "linkedin",     AppCategory.Productivity),  // matches exe classifier
        // ── Productivity: AI assistants (work either way; both Dev & Productivity are focus-positive)
        ("chatgpt",         "chatgpt",      AppCategory.Productivity),
        ("perplexity",      "perplexity",   AppCategory.Productivity),
        ("claude",          "claude",       AppCategory.Productivity),
        ("deepseek",        "deepseek",     AppCategory.Productivity),
        ("copilot",         "copilot",      AppCategory.Productivity),

        // ── Added from real usage — dev / founder / finance stack ───────
        ("cursor",          "cursor",       AppCategory.Development),    // cursor.com/docs (desktop app also classified by exe)
        ("npm |",           "npm",          AppCategory.Development),
        ("codex",           "codex",        AppCategory.Development),
        ("google for developers", "googledev", AppCategory.Development),
        ("wordpress",       "wordpress",    AppCategory.Productivity),
        ("wix.com",         "wix",          AppCategory.Productivity),
        ("hubspot",         "hubspot",      AppCategory.Productivity),
        ("paypal",          "paypal",       AppCategory.Productivity),
        ("y combinator",    "ycombinator",  AppCategory.Productivity),
        ("techstars",       "techstars",    AppCategory.Productivity),
        ("antler hub",      "antler",       AppCategory.Productivity),
        ("f6s",             "f6s",          AppCategory.Productivity),
        ("binance",         "binance",      AppCategory.Productivity),   // finance = work here; move to neutral/distraction if it's personal
        ("tradingview",     "tradingview",  AppCategory.Productivity),
        ("coinbase",        "coinbase",     AppCategory.Productivity),
        ("coinmarketcap",   "coinmarketcap",AppCategory.Productivity),

        // ── Reading / news: treated as WORK per product decision (leisure, not distraction)
        ("| medium",        "medium",       AppCategory.Productivity),
        ("substack",        "substack",     AppCategory.Productivity),
        ("techcrunch",      "techcrunch",   AppCategory.Productivity),
        ("- the verge",     "theverge",     AppCategory.Productivity),
        ("bloomberg",       "bloomberg",    AppCategory.Productivity),
        ("reuters",         "reuters",      AppCategory.Productivity),
        ("new york times",  "nytimes",      AppCategory.Productivity),
        ("ars technica",    "arstechnica",  AppCategory.Productivity),

        // ── Free-streaming / movie sites: distraction ───────────────────
        ("movie4k",         "movie4k",      AppCategory.Entertainment),
        ("123movies",       "123movies",    AppCategory.Entertainment),
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
