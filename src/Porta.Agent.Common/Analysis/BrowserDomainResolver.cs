namespace Porta.Agent.Common.Analysis;

/// <summary>
/// 2e: turns a foreground browser URL (read from the address bar via UI Automation in the
/// user-session capture path) into a coarse, privacy-safe registrable-domain token —
/// e.g. "https://news.ycombinator.com/item?id=1" -> "ycombinator", "www.bbc.co.uk/news" -> "bbc".
///
/// PRIVACY CONTRACT: only the single registrable-domain label is ever produced. The scheme, any
/// host prefix (www / sub-domains), PATH, QUERY, FRAGMENT, port, and userinfo are all discarded and
/// can never be returned. The parsing goes through <see cref="Uri"/>, whose <c>Host</c> already
/// strips everything but the host; a final plain-label check guarantees no stray character survives.
///
/// This type is PURE and Windows-agnostic (no UIA here on purpose) so the extraction and the
/// privacy contract are fully unit-testable. The UIA address-bar read lives in
/// <c>UserSessionNativeSignals</c> and passes the raw URL string in here.
/// </summary>
public static class BrowserDomainResolver
{
    // Small embedded public-suffix list — only the common MULTI-label suffixes need listing.
    // Single-label TLDs (com, org, net, io, dev, ...) are handled implicitly: anything not matched
    // here is treated as a one-label suffix, so eTLD+1 = "<label>.<tld>" and the token is "<label>".
    // Deliberately compact; extend from real fleet data over time.
    private static readonly HashSet<string> MultiLabelSuffixes = new(StringComparer.Ordinal)
    {
        "co.uk", "org.uk", "gov.uk", "ac.uk", "me.uk",
        "com.au", "net.au", "org.au", "gov.au", "edu.au",
        "co.nz", "co.jp", "or.jp", "ne.jp", "co.in", "co.za",
        "com.br", "com.mx", "com.tr", "com.sg", "com.hk", "com.cn",
        "co.kr", "com.ar", "com.co", "co.il", "com.sa", "com.ua",
    };

    /// <summary>
    /// Extracts the coarse registrable-domain token from a raw browser URL (or bare host string).
    /// Returns <c>null</c> when there is no usable web host (about:/chrome:/file: pages, IP
    /// addresses, localhost, search text, or garbage). The token is a bare lowercase [a-z0-9-]
    /// label with the public suffix removed; nothing else survives.
    /// </summary>
    public static string? ExtractDomainToken(string? rawUrl)
    {
        string? host = ExtractHost(rawUrl);
        if (host is null)
        {
            return null;
        }

        // www is not a distinguishing label — www.nytimes.com and nytimes.com are the same site.
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        string[] labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2)
        {
            return null; // no dot -> not a registrable domain (single token / localhost / etc.)
        }

        // Public suffix is the last two labels if they're a known multi-label suffix, else the last.
        int suffixLabels = 1;
        if (labels.Length >= 3
            && MultiLabelSuffixes.Contains(labels[^2] + "." + labels[^1]))
        {
            suffixLabels = 2;
        }

        int registrableIndex = labels.Length - suffixLabels - 1;
        if (registrableIndex < 0)
        {
            return null;
        }

        string token = labels[registrableIndex];
        return IsPlainLabel(token) ? token : null;
    }

    private static string? ExtractHost(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        string value = rawUrl.Trim();

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            // A ':' with no "://" means a non-web scheme (about:blank, chrome:settings, file:,
            // javascript:, mailto:, or host:port) — never tokenize those.
            if (value.Contains(':'))
            {
                return null;
            }

            // Scheme-less address-bar text ("nytimes.com/foo"): give Uri a scheme so it can parse a
            // host. The scheme is discarded; only the host is used.
            value = "https://" + value.TrimStart('/');
        }
        else
        {
            string scheme = value[..value.IndexOf("://", StringComparison.Ordinal)].ToLowerInvariant();
            if (scheme is not ("http" or "https"))
            {
                return null; // ftp/file/chrome-extension/etc.
            }
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        // Uri.Host is the host only — no path, query, fragment, port, or userinfo.
        string host = uri.Host.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host)
            || uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            || string.Equals(host, "localhost", StringComparison.Ordinal))
        {
            return null;
        }

        return host;
    }

    private static bool IsPlainLabel(string label)
    {
        if (label.Length == 0)
        {
            return false;
        }

        foreach (char c in label)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
