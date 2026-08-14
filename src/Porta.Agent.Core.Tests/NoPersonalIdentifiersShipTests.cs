using System.Text.RegularExpressions;

namespace Porta.Agent.Core.Tests;

/// <summary>
/// Step 10 phase C/D (2026-08-14) — nothing about ONE user may be baked into the product.
///
/// The report emails as: From name = that user's own <c>displayName</c>, From address =
/// <c>reports@useporta.com</c>, Reply-to = that user's own <c>primaryEmail</c>. All three come from
/// the account profile at send time. The founder is the only real user today, which is exactly the
/// condition under which his name or address gets typed into a default "just to get it working" and
/// is then never removed — and the failure is silent, because on his machine it looks correct.
///
/// Yadin's instruction, 2026-08-14: fail if any real name, email or domain of his appears outside a
/// test fixture. This is that test, generalised — it forbids ANY user's address in shipped code, not
/// only his, because the next one will not be his.
///
/// <b>Names: comments exempt. Email addresses: comments included.</b> Two different rules, because
/// the two things carry different risk.
///
/// A NAME in a comment is history — "Yadin's decision, 2026-08-12" appears in 32 lines across 18
/// shipped files and has repeatedly been the thing that explained why a constant is what it is. It
/// cannot reach a screen, an email, or the wire.
///
/// An ADDRESS in a comment is different: <b>an address in a comment is an address someone pastes into
/// code later</b> (Yadin, 2026-08-14). Sample payloads and "e.g." lines are exactly where a real
/// address gets parked, and the next person copies it into a default. So the email scans read the
/// RAW file, comments and all.
/// </summary>
public sealed class NoPersonalIdentifiersShipTests
{
    /// <summary>
    /// The product's own service addresses. These MUST be hardcoded — `reports@useporta.com` is the
    /// From address for every user's report (spec §6) and `ari@useporta.com` sends the nightly
    /// digest. They belong to the product, not to a person.
    /// </summary>
    private static readonly string[] AllowedProductAddresses =
    [
        "reports@useporta.com",
        "ari@useporta.com",
        "support@useporta.com"
    ];

    /// <summary>
    /// The founder's real identifiers, named here so the guard is explicit. This file is the test
    /// fixture they are allowed to live in.
    /// </summary>
    private static readonly string[] PersonalIdentifiers =
    [
        "insideporta2",
        "insideporta2@gmail.com",
        "Yadin Sela"
    ];

    /// <summary>
    /// Consumer mail domains. A literal one of these in shipped code is a personal address that got
    /// left behind — there is no legitimate reason for the product to name one.
    /// </summary>
    private static readonly string[] ConsumerMailDomains =
    [
        "@gmail.com", "@googlemail.com", "@outlook.com", "@hotmail.com",
        "@yahoo.com", "@icloud.com", "@proton.me", "@protonmail.com"
    ];

    private static readonly Regex EmailShaped = new(
        @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void NoPersonalIdentifierAppearsInShippedCode()
    {
        var offenders = new List<string>();

        // Comment-stripped: a name in an attribution comment is history, not shippable content.
        foreach ((string path, string _, string code) in ShippedSources())
        {
            foreach (string identifier in PersonalIdentifiers)
            {
                if (code.Contains(identifier, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{path}: contains \"{identifier}\"");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "One user's identity is baked into the product. From name and Reply-to must come from " +
            "the account profile at send time.\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void NoConsumerMailAddressAppearsInShippedCode()
    {
        var offenders = new List<string>();

        // RAW, comments included: an address in a comment is one someone pastes into code later.
        foreach ((string path, string raw, string _) in ShippedSources())
        {
            foreach (string domain in ConsumerMailDomains)
            {
                if (raw.Contains(domain, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{path}: contains \"{domain}\"");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A personal mail address is hardcoded. The product never names a user's provider.\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The general form, and the one that will catch the next person rather than only this one: no
    /// email-shaped literal at all except the product's own service addresses.
    /// </summary>
    [Fact]
    public void TheOnlyEmailAddressesInShippedCodeArePortaServiceAddresses()
    {
        var offenders = new List<string>();

        // RAW, comments included — same reason as above, and this is the scan that catches a sample
        // payload with a real address in it.
        foreach ((string path, string raw, string _) in ShippedSources())
        {
            foreach (Match match in EmailShaped.Matches(raw))
            {
                string found = match.Value;
                bool allowed = AllowedProductAddresses.Any(
                    a => string.Equals(a, found, StringComparison.OrdinalIgnoreCase));

                if (!allowed)
                {
                    offenders.Add($"{path}: \"{found}\"");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "An email address that is not a Porta service address is hardcoded. Recipient addresses " +
            "come from the address book; sender identity comes from the account profile.\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The guard is worth nothing if it is scanning an empty set — a renamed folder or a changed
    /// extension would leave it passing forever while checking nothing. Same reasoning as the
    /// digest walk asserting it actually reaches the nested payloads.
    /// </summary>
    [Fact]
    public void TheScanActuallyReachesTheProductSource()
    {
        List<(string Path, string Raw, string Code)> sources = ShippedSources().ToList();

        Assert.True(sources.Count > 200, $"only {sources.Count} shipped source files scanned");

        // Files that must be in scope, because they are where a sender default would be typed.
        Assert.Contains(sources, s => s.Path.EndsWith("MainWindow.Reports.cs", StringComparison.Ordinal));
        Assert.Contains(sources, s => s.Path.EndsWith("FleetContracts.cs", StringComparison.Ordinal));
        Assert.Contains(sources, s => s.Path.EndsWith("MainWindow.xaml", StringComparison.Ordinal));

        // And the comment stripper must not be eating everything.
        Assert.Contains(sources, s => s.Code.Contains("class ", StringComparison.Ordinal));

        // The name scan is comment-blind by design; prove it is actually reading a file that HAS an
        // attribution comment, so "no offenders" is not "nothing was looked at".
        Assert.Contains(sources, s => s.Raw.Contains("Yadin", StringComparison.Ordinal));
    }

    /// <summary>
    /// Proves the stripper is doing what the class comment claims: a name in a comment is invisible
    /// to the scan, the same name in a string literal is not. Without this, "the test passes" could
    /// mean "the stripper removed the whole file".
    /// </summary>
    [Fact]
    public void CommentsAreStripped_ButStringLiteralsAreNot()
    {
        const string sample = """
            // Yadin Sela decided this on 2026-08-14
            /* insideporta2@gmail.com in a block comment */
            /// <summary>Yadin Sela again</summary>
            var a = "kept@example.com";
            """;

        string stripped = StripComments(sample);

        Assert.DoesNotContain("Yadin Sela", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("insideporta2", stripped, StringComparison.Ordinal);
        Assert.Contains("kept@example.com", stripped, StringComparison.Ordinal);

        Assert.DoesNotContain("<!-- Yadin -->", StripComments("<!-- Yadin -->"), StringComparison.Ordinal);
    }

    // ── the scan ──

    /// <summary>
    /// Each shipped file twice: <c>Raw</c> as written, and <c>Code</c> with commentary removed. Names
    /// are checked against Code, addresses against Raw.
    /// </summary>
    private static IEnumerable<(string Path, string Raw, string Code)> ShippedSources()
    {
        string root = ResolveRepoRoot();
        string src = Path.Combine(root, "src");

        foreach (string file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories))
        {
            string extension = Path.GetExtension(file);
            if (extension is not (".cs" or ".xaml"))
            {
                continue;
            }

            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');

            // Build output, and the test projects themselves — a test fixture is allowed to name
            // real values, which is the exception Yadin granted.
            if (relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.Contains(".Tests/", StringComparison.Ordinal)
                || relative.Contains("/ValidationRunner/", StringComparison.Ordinal))
            {
                continue;
            }

            string raw = File.ReadAllText(file);
            yield return (relative, raw, StripComments(raw));
        }
    }

    /// <summary>
    /// Remove commentary so only shippable content is scanned: C# line and block comments (XML doc
    /// comments are line comments), and XAML comments. Crude by design — it does not understand
    /// strings containing "//" — and erring toward stripping only ever makes this test blinder in a
    /// way <see cref="CommentsAreStripped_ButStringLiteralsAreNot"/> and
    /// <see cref="TheScanActuallyReachesTheProductSource"/> both check for.
    /// </summary>
    private static string StripComments(string text)
    {
        text = Regex.Replace(text, @"<!--.*?-->", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"^[^\r\n""]*//[^\r\n]*", " ", RegexOptions.Multiline);
        return text;
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
