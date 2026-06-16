using Porta.Agent.Common.Privacy;

namespace Porta.Agent.Core.Tests;

public sealed class PrivacySettingsStoreTests
{
    [Fact]
    public void Default_IsOptOut_TitleReadingOn()
    {
        Assert.True(PrivacySettings.Default.ReadBrowserTitles);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not valid json")]
    [InlineData("{}")]
    public void Parse_MissingOrInvalid_FallsBackToDefaultOn(string? json)
    {
        Assert.True(PrivacySettingsStore.Parse(json).ReadBrowserTitles);
    }

    [Fact]
    public void Parse_ExplicitlyDisabled_IsRespected()
    {
        Assert.False(PrivacySettingsStore.Parse("{\"ReadBrowserTitles\":false}").ReadBrowserTitles);
    }

    [Fact]
    public void Parse_ExplicitlyEnabled_IsRespected()
    {
        Assert.True(PrivacySettingsStore.Parse("{\"ReadBrowserTitles\":true}").ReadBrowserTitles);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SerializeThenParse_RoundTrips(bool enabled)
    {
        string json = PrivacySettingsStore.Serialize(new PrivacySettings(enabled));
        PrivacySettings parsed = PrivacySettingsStore.Parse(json);
        Assert.Equal(enabled, parsed.ReadBrowserTitles);
    }
}
