using Porta.Agent.Common.Analysis;

namespace Porta.Agent.Core.Tests;

public sealed class BrowserDomainResolverTests
{
    [Theory]
    [InlineData("https://news.ycombinator.com/item?id=123", "ycombinator")]
    [InlineData("https://www.nytimes.com/2026/07/09/tech/story.html", "nytimes")]
    [InlineData("nytimes.com/foo/bar", "nytimes")]                 // scheme-less address-bar text
    [InlineData("https://www.bbc.co.uk/news", "bbc")]              // multi-label suffix co.uk
    [InlineData("https://sub.example.com.au/x", "example")]        // multi-label suffix com.au
    [InlineData("https://stackoverflow.com/questions/42", "stackoverflow")]
    [InlineData("https://github.com/owner/repo/pull/1", "github")]
    [InlineData("https://mail.google.com/mail/u/0/#inbox", "google")]
    [InlineData("HTTPS://WWW.Reddit.com/r/aww", "reddit")]         // case-insensitive
    public void ExtractDomainToken_ReturnsRegistrableLabel(string url, string expected)
    {
        Assert.Equal(expected, BrowserDomainResolver.ExtractDomainToken(url));
    }

    [Theory]
    [InlineData("about:blank")]
    [InlineData("chrome://newtab")]
    [InlineData("chrome://settings/privacy")]
    [InlineData("edge://settings")]
    [InlineData("file:///C:/Users/me/secret.pdf")]
    [InlineData("ftp://files.example.com/x")]
    [InlineData("http://localhost:3000/dashboard")]
    [InlineData("https://127.0.0.1:8080/")]
    [InlineData("http://192.168.1.10/admin")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ExtractDomainToken_NonWebOrNoHost_ReturnsNull(string? input)
    {
        Assert.Null(BrowserDomainResolver.ExtractDomainToken(input));
    }

    [Theory]
    [InlineData("https://news.ycombinator.com/item?id=123&utm=x#frag")]
    [InlineData("https://user:secret@nytimes.com/private/path?token=abcdef")]
    public void ExtractDomainToken_StripsEverythingButRegistrableLabel(string url)
    {
        string? token = BrowserDomainResolver.ExtractDomainToken(url);

        Assert.NotNull(token);
        // Hard privacy guarantee: the token is a bare label — nothing from the URL's scheme,
        // userinfo, sub-domains, path, query, or fragment can survive.
        Assert.DoesNotContain('/', token!);
        Assert.DoesNotContain('?', token!);
        Assert.DoesNotContain('#', token!);
        Assert.DoesNotContain(':', token!);
        Assert.DoesNotContain('@', token!);
        Assert.DoesNotContain('.', token!);
        Assert.DoesNotContain(' ', token!);
    }
}
