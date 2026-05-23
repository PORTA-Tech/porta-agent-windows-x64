using System.Collections.Concurrent;

namespace Porta.Agent.Common.Analysis;

public enum AppCategory
{
    Unknown        = 0,
    Entertainment  = 1,
    Communication  = 2,
    SocialMedia    = 3,
    Gaming         = 4,   // active game running (steamapps path, known game exes)
    Development    = 5,
    Productivity   = 6,
    GamingLauncher = 7    // launcher/store front-end (Steam.exe, Epic launcher, etc.)
}

/// <summary>
/// Classifies exe paths into coarse app categories and maintains
/// an in-memory hash → category registry for the lifetime of the process.
/// Privacy-safe: only the enum value is stored, never the raw exe path.
/// </summary>
public static class AppCategoryClassifier
{
    private static readonly ConcurrentDictionary<string, AppCategory> HashRegistry =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Classifies a stable identifier (as produced by ResolveStableAppIdentifier)
    /// into an AppCategory. The identifier is either "exe:{path}", "process:{name}",
    /// or "pid:{id}".
    /// </summary>
    public static AppCategory ClassifyStableIdentifier(string stableIdentifier)
    {
        if (string.IsNullOrWhiteSpace(stableIdentifier))
            return AppCategory.Unknown;

        // ── Path-pattern check: actual game binaries running from known game folders ──
        // These are unambiguously active games, not launchers.
        if (stableIdentifier.Contains("\\steamapps\\", StringComparison.OrdinalIgnoreCase)
            || stableIdentifier.Contains("/steamapps/", StringComparison.OrdinalIgnoreCase)
            || stableIdentifier.Contains("\\epicgames\\games\\", StringComparison.OrdinalIgnoreCase)
            || stableIdentifier.Contains("\\gog games\\", StringComparison.OrdinalIgnoreCase)
            || stableIdentifier.Contains("\\roblox\\", StringComparison.OrdinalIgnoreCase))
        {
            return AppCategory.Gaming;
        }

        string name = ExtractClassifiableName(stableIdentifier);
        if (string.IsNullOrWhiteSpace(name))
            return AppCategory.Unknown;

        return name switch
        {
            // ── Entertainment ────────────────────────────────────────────────
            "spotify" or "deezer" or "tidal" or "soundcloud" or "aimp"
                or "foobar2000" or "winamp" or "itunes" or "music" or "applemusic"
                or "vlc" or "mpv" or "mpc-hc" or "mpc-hc64" or "mpc-be" or "mpc-be64"
                or "potplayer" or "potplayermini64" or "kmplayer" or "gomplayer"
                or "netflix" or "disneyplus" or "plex" or "plexmediaplayer"
                or "jellyfin" or "kodi" or "emby" or "infuse"
                or "twitch" or "hbomax" or "primevideoapp" or "peacock"
                or "youtube" or "youtubeapp"
                => AppCategory.Entertainment,

            // ── Communication ────────────────────────────────────────────────
            "slack" or "teams" or "msteams" or "ms-teams"
                or "zoom" or "zoomit" or "us.zoom.xos"
                or "webex" or "webexhost" or "ciscowebexstart"
                or "outlook" or "hxoutlook" or "thunderbird" or "mailspring" or "postbox"
                or "skype" or "skypefordesktop" or "lync"
                or "mattermost" or "rocketchat"
                or "telegram" or "kotatogram"
                or "signal" or "whatsapp" or "whatsappdesktop"
                or "discord"
                => AppCategory.Communication,

            // ── SocialMedia ──────────────────────────────────────────────────
            // LinkedIn is excluded — moved to Productivity (legitimate work use for
            // recruiters, founders, and researchers outweighs distraction risk).
            "twitter" or "twitterapp" or "twitterforwindows"
                or "instagram" or "instagramapp"
                or "facebook" or "facebookapp" or "messenger"
                or "tiktok" or "tiktokapp"
                or "pinterest" or "snapchat"
                or "reddit" or "redditmobile" or "redditapp"
                or "mastodon" or "ivory" or "mammoth"
                or "threads" or "threadsapp"
                or "bereal"
                => AppCategory.SocialMedia,

            // ── GamingLauncher ───────────────────────────────────────────────
            // Store/launcher front-ends only. No game content runs from these.
            "steam" or "steamwebhelper"
                or "epicgameslauncher"
                or "battle.net" or "battlenet" or "blizzardagent"
                or "goggalaxy" or "galaxyclient"
                or "origin" or "eadesktop" or "eaapp"
                or "xboxapp" or "xgpu" or "gamingservices"
                or "itchio"
                => AppCategory.GamingLauncher,

            // ── Gaming (active) ──────────────────────────────────────────────
            // Known game exes not caught by path detection above.
            "leagueclient" or "riotclientservices" or "valorant"
                or "fortnite" or "rocketleague" or "minecraft" or "minecraftlauncher"
                or "gamebar" or "gamebarinputdispatch"
                or "robloxplayerbeta" or "robloxplayerlauncher"
                or "retroarch"
                or "pcsx2" or "pcsx2-qt"
                or "dolphin" or "dolphin-emu"
                or "snes9x" or "snes9x-x64"
                or "unrealtournament"
                => AppCategory.Gaming,

            // ── Development ──────────────────────────────────────────────────
            "devenv"
                or "code" or "code - insiders"
                or "cursor"
                or "rider64" or "rider"
                or "idea64" or "idea"
                or "pycharm64" or "pycharm"
                or "webstorm64" or "webstorm"
                or "datagrip64" or "datagrip"
                or "clion64" or "clion"
                or "goland64" or "goland"
                or "phpstorm64" or "phpstorm"
                or "rubymine64" or "rubymine"
                or "fleet" or "fleet64"
                or "sublime_text" or "notepad++" or "atom" or "zed" or "helix"
                or "nvim" or "gvim" or "vim" or "emacs"
                or "windowsterminal" or "wt" or "hyper" or "alacritty" or "kitty" or "wezterm" or "warp"
                or "cmd" or "powershell" or "pwsh"
                or "dockerdesktop" or "rancherdesktop"
                or "postman" or "insomnia" or "bruno"
                or "tableplus" or "dbeaver" or "heidisql" or "dbeaverapplicationsmain"
                or "mongodbcompass" or "azuredatastudio"
                or "sourcetree" or "gitkraken" or "fork" or "githubdesktop"
                or "filezilla" or "cyberduck" or "winscp"
                or "virtualbox" or "vmware" or "vmwareworkstation"
                or "xcode" or "androidstudio" or "androidstudio64"
                or "proxyman" or "charlesproxy" or "charles"
                or "wireshark"
                or "burpsuite" or "burp suite community edition" or "burp suite professional"
                => AppCategory.Development,

            // ── Productivity ─────────────────────────────────────────────────
            "winword" or "excel" or "powerpnt" or "onenote" or "onenotem"
                or "publisher" or "access" or "msaccess" or "visio"
                or "soffice" or "scalc" or "swriter" or "simpress" or "sdraw"
                or "notion" or "obsidian" or "logseq" or "remnote" or "craft" or "upnote"
                or "evernote" or "bear" or "joplin" or "standardnotes"
                or "todoist" or "ticktick" or "things3" or "omnifocus"
                or "airtable" or "coda"
                or "linear" or "clickup"
                or "scrivener" or "scrivener3"
                or "canva" or "miro" or "lucidchart" or "whimsical" or "drawio"
                or "figma" or "sketch" or "zeplin" or "penpot"
                or "dropbox" or "onedrive" or "googledrive" or "backblaze"
                or "1password" or "lastpass" or "bitwarden" or "dashlane" or "keepass"
                or "loom" or "screenflow" or "camtasia" or "cleanshot"
                or "linkedin" or "linkedinapp"   // moved from SocialMedia — legitimate work use
                => AppCategory.Productivity,

            _ => AppCategory.Unknown
        };
    }

    /// <summary>
    /// Registers a hash → category mapping. Called once per unique exe when the
    /// foreground app is first seen. Idempotent — same input always yields same output.
    /// </summary>
    public static void RegisterHashCategory(string hash, AppCategory category)
    {
        if (string.IsNullOrWhiteSpace(hash) || category == AppCategory.Unknown)
            return;
        HashRegistry[hash] = category;
    }

    /// <summary>
    /// Returns the category for a previously registered hash.
    /// Returns Unknown if the hash has never been registered (e.g. in tests
    /// where no real foreground events fire).
    /// </summary>
    public static AppCategory GetCategoryForHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return AppCategory.Unknown;
        return HashRegistry.TryGetValue(hash, out AppCategory cat) ? cat : AppCategory.Unknown;
    }

    /// <summary>
    /// Returns true if the category represents a focus-positive app (Development or Productivity).
    /// Used by distraction detection to reset focus-absence counters.
    /// </summary>
    public static bool IsFocusPositive(AppCategory category)
        => category == AppCategory.Development || category == AppCategory.Productivity;

    private static string ExtractClassifiableName(string stableIdentifier)
    {
        if (stableIdentifier.StartsWith("exe:", StringComparison.Ordinal))
        {
            string path = stableIdentifier["exe:".Length..];
            string fileName = Path.GetFileNameWithoutExtension(path);
            return fileName.Trim().ToLowerInvariant();
        }

        if (stableIdentifier.StartsWith("process:", StringComparison.Ordinal))
        {
            return stableIdentifier["process:".Length..].Trim().ToLowerInvariant();
        }

        return string.Empty;
    }
}
