namespace Porta.UserAgent.Tests;

public sealed class UserAgentStartupMetadataTests
{
    [Fact]
    public void Parse_DefaultsToUnknownWhenStartupSourceIsMissing()
    {
        UserAgentStartupMetadata metadata = UserAgentStartupMetadata.Parse(["--flag", "value"]);

        Assert.Equal("unknown", metadata.Source);
        Assert.Equal("--flag value", metadata.RawArgsText);
    }

    [Fact]
    public void Parse_UsesLastNonEmptyStartupSourceValue()
    {
        UserAgentStartupMetadata metadata = UserAgentStartupMetadata.Parse(
        [
            "--startup-source=run-key",
            "--startup-source=",
            "--startup-source=control-center-recovery"
        ]);

        Assert.Equal("control-center-recovery", metadata.Source);
        Assert.Equal(
            "--startup-source=run-key --startup-source= --startup-source=control-center-recovery",
            metadata.RawArgsText);
    }

    [Fact]
    public void Parse_ReportsNoneWhenArgumentListIsEmpty()
    {
        UserAgentStartupMetadata metadata = UserAgentStartupMetadata.Parse([]);

        Assert.Equal("unknown", metadata.Source);
        Assert.Equal("(none)", metadata.RawArgsText);
    }
}
